using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Safety;
using Group = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;

namespace TiaMcp.Adapters.Native.Optional;

internal sealed class LegacyOptionalAdapter
{
    private readonly PlcFoundationEngine _engine;
    private readonly OptionalEngineeringRequest _request;
    private readonly string _release;

    internal LegacyOptionalAdapter(PlcFoundationEngine engine, OptionalEngineeringRequest request)
    {
        _engine = engine;
        _request = request;
        _release = engine.ReleaseKey;
    }

    private static object Get(object target, string name) => Group.Get(target, name);
    private static object? Optional(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target);
    private static object[] Items(object target) => Group.Items(target).ToArray();
    private static object? Find(object target, string name) => Group.Find(target, name);
    private static object Named(object target, string name) => Find(target, name) ?? throw new PlcSoftwareException("NotFound", "Exact object not found: " + name);
    private static string Name(object target) => Convert.ToString(Get(target, "Name"), CultureInfo.InvariantCulture)!;
    private static HmiObject Object(string raw, string parameter) => OptionalRules.ParseObject(raw, parameter);
    private static HmiObject Row(object value) => HmiScalar.Read(value);

    private static object? Call(object target, string method, params object?[] arguments)
        => OptionalNativeOperations.Call(target, method, arguments);

    private static Type ApiType(string name) => typeof(IEngineeringObject).Assembly.GetType(name)
        ?? throw new NotSupportedException("Official API type unavailable on this release: " + name);
    private static object EnumValue(string name, string value) => Enum.Parse(ApiType(name), value, false);
    private static object Service(object owner, string name) => HardwareOfficialService.Require(owner, "Siemens.Engineering." + name, "Siemens.Engineering");
    private IDisposable? Edit(bool writing) => writing ? _engine.PlcB5Session.AcquireHmiEditAccess() : null;
    private DeviceItem DriveItem() => _engine.PlcB5Session.ExactEngineeringHardware(
        SivarcPolicy.ParseNames(_request.DevicePathJson, "devicePathJson"), SivarcPolicy.ParseNames(_request.ItemPathJson, "itemPathJson")) as DeviceItem
        ?? throw new ArgumentException("itemPathJson must select a drive device item.");
    private object Drive()
    {
        var selector = StartdrivePolicy.ParseDriveSelector(_request.DriveObjectNumber, _request.DriveObjectIndex);
        var objects = Drives(DriveItem());
        if (selector.Index >= objects.Length) throw new PlcSoftwareException("NotFound", "driveObjectIndex is out of range.");
        return objects[selector.Index];
    }
    private object[] Drives(DeviceItem item) => _release == "14sp1"
        ? new[] { Service(item, "Startdrive.DriveObject") }
        : Items(Get(Service(item, "MC.Drives.DriveObjectContainer"), "DriveObjects"));
    private object Dcc() => Service(Drive(), "MC.Drives.Dcc.DriveControlChartContainer");
    private object Sivarc() => Service(_engine.PlcB5Session.CurrentProject!, "SiVArc.Sivarc");
    private object Rules() => Get(Sivarc(), SivarcPolicy.AnchorProperty(_request.Category));

    internal HardwareAddressingReply Execute()
    {
        if (OptionalEngineeringCapabilities.Unsupported(_release, _request) is string unavailable)
            throw new NotSupportedException(unavailable);
        var previous = OptionalInputs.Current;
        OptionalInputs.Current = _request;
        try
        {
            return _engine.RunHmiStep(_request.Operation, values => {
                var meta = new HmiObject(values);
                meta["mayHaveChanged"] = false;
                meta["mayHaveWrittenFiles"] = false;
                try
                {
                    return Dispatch(meta);
                }
                catch (Exception error)
                {
                    OptionalEvidence.RecordFailure(meta, error);
                    throw;
                }
                finally
                {
                    foreach (var pair in meta) values[pair.Key] = pair.Value;
                }
            }, requiresProject: !(_request.Operation == "ManageTechnologyExtensions" && StartdrivePolicy.IsPortalScopedExtensionAction(_request.Action)));
        }
        finally { OptionalInputs.Current = previous; }
    }

    private void Page(object[] rows, HmiObject meta, bool schema = false)
    {
        int limit = _request.Limit == 0 ? 500 : _request.Limit;
        OptionalRules.ValidatePagination(_request.Offset, limit);
        var records = rows.Skip(_request.Offset).Take(limit).Select(value => (object?)(schema ? OptionalObjectAddress.Read(value) : Row(value))).ToArray();
        meta["records"] = new HmiArray(records);
        foreach (var pair in OptionalRules.PageMeta(rows.Length, _request.Offset, limit, records.Length)) meta[pair.Key] = pair.Value;
    }

    private string Dispatch(HmiObject meta) => _request.Operation switch {
        "GenerateSivarc" => Generate(meta),
        "GetSivarcRuleTree" => SivarcTree(meta),
        "ListSivarcRules" or "ManageSivarcRule" => GenericSivarc(meta),
        "ManageSivarcRuleContainer" => SivarcContainer(meta),
        "ManageSivarcTableRule" => SivarcRule(meta),
        "ListSivarcBlockDefinitions" or "ManageSivarcBlockDefinition" => Definitions(meta),
        "ListDriveObjects" => DriveObjects(meta),
        "GetDriveParameters" or "GetOnlineDriveParameters" or "ManageStartdriveParameter" => Parameters(meta),
        "ManageDriveTelegrams" => Telegrams(meta),
        "ManageDriveFunctions" or "ManageOnlineDriveFunctions" => Functions(meta),
        "ManageDriveSecurity" => Security(meta),
        "ManageDriveHardwareModule" => HardwareModule(meta),
        "ManageTechnologyExtensions" => Extensions(meta),
        "GetDccObject" => DccObject(meta),
        "ListDccCharts" => DccCharts(meta),
        "ManageDcbLibraries" => DcbLibraries(meta),
        "ManageDccChart" or "ManageDccBlock" or "ManageDccPin" or "ManageDccChartInterface" or "ManageDccChartPartition" => DccEdit(meta),
        _ => throw new NotSupportedException("The optional-product operation is unavailable on this release.")
    };

    private string Generate(HmiObject meta)
    {
        var flags = SivarcPolicy.ParseGenerationOptions(_request.GenerationOptions);
        var type = ApiType("Siemens.Engineering.SiVArc.GenerationOptions");
        var options = Enum.ToObject(type, flags.Aggregate(0, (value, name) => value | Convert.ToInt32(Enum.Parse(type, name), CultureInfo.InvariantCulture)));
        using var access = Edit(!_request.DryRun);
        Device Device(string name) => _engine.PlcB5Session.ExactEngineeringHardware(new[] { name }, Array.Empty<string>()) as Device
            ?? throw new ArgumentException("Exact HMI device required.");
        var device = Device(_request.HmiDeviceName);
        var extras = SivarcPolicy.ParseNames(_request.AdditionalHmiDeviceNamesJson, "additionalHmiDeviceNamesJson").Select(n => Device(n).Name).ToArray();
        var devices = new[] { device.Name }.Concat(extras).ToArray();
        if (devices.Distinct(StringComparer.Ordinal).Count() != devices.Length) throw new ArgumentException("HMI device names repeat.");
        var paths = ExactPaths(_request.PlcSoftwarePathsJson);
        var plcs = paths.Select(p => _engine.PlcB5Session.ExactPlcForEngineering(p, !_request.DryRun).Name).ToArray();
        var plcDevices = paths.Select(path => OptionalNativeOperations.OwningDeviceName(
            _engine.PlcB5Session.ResolveSoftwareContainerUncached(path)?.Parent,
            value => (value as IEngineeringObject)?.Parent,
            value => value is Device owner ? owner.Name : null)).ToArray();
        if (plcDevices.Distinct(StringComparer.Ordinal).Count() != plcDevices.Length)
            throw new InvalidOperationException("Native PLC device names repeat (two PLC paths inside the same device).");
        var service = Sivarc();

        meta["plcDeviceNames"] = new HmiArray(plcDevices);
        meta["hmiDeviceNames"] = new HmiArray(devices);
        meta["plcSoftwareNames"] = new HmiArray(plcs);
        meta["generationOptions"] = options.ToString();
        meta["dryRun"] = _request.DryRun;
        if (_request.DryRun) return "SiVArc native generation preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        var result = Call(service, "Generate", devices.Length == 1 ? (object)device.Name : devices, plcDevices, options)!;
        meta["result"] = Row(result);
        meta["generationPassed"] = Optional(result, "IsGenerationSuccessful");
        if (meta["generationPassed"] is false) meta["operationSuccess"] = false;
        return "SiVArc generation returned; no automatic save, compile or download.";
    }

    private static string[] ExactPaths(string raw)
    {
        if (raw.Length > 65536) throw new ArgumentException("Name list too large.");
        var names = (OptionalInputs.Parse(raw) as HmiArray ?? throw new ArgumentException("Expected a JSON array of exact paths.")).Select(n => n!.OptionalValue<string>()).ToArray();
        if (names.Length < 1 || names.Length > 500 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Provide 1..500 unique nonempty paths.");
        return names;
    }

    private object Folder(object root, string path)
    {
        foreach (var part in Group.Parts(path, true)) root = Named(Get(root, "Folders"), part);
        return root;
    }

    private object Table(object root, string path)
    {
        var parts = Group.Parts(path);
        return Named(Get(Folder(root, string.Join("/", parts.Take(parts.Length - 1))), "Tables"), parts.Last());
    }

    private static object RuleGroup(object root, string path)
    {
        foreach (var part in Group.Parts(path, true)) root = Named(Get(root, "Groups"), part);
        return root;
    }

    private string SivarcTree(HmiObject meta)
    {
        SivarcPolicy.ValidateTreeRequest(_request.Category, _request.FolderPath, _request.TablePath, _request.MaxDepth, _request.Offset, _request.Limit);
        var root = Rules();
        if (_release is "15.1" or "16" or "17")
        {
            Page(Items(Get(root, "Rules")), meta);
            meta["scope"] = "Root rules; rule tables and folders are unavailable before V18.";
        }
        else
        {
            var folder = Folder(root, _request.FolderPath);
            meta["folder"] = Row(folder);
            meta["folders"] = new HmiArray(Items(Get(folder, "Folders")).Select(v => (object?)Row(v)).ToArray());
            var table = string.IsNullOrEmpty(_request.TablePath) ? null : Table(root, _request.TablePath);
            Page(Items(Get(table ?? folder, table == null ? "Tables" : "Rules")), meta);
        }
        return "SiVArc rule scope read; no generation.";
    }

    private string GenericSivarc(HmiObject meta)
    {
        var raw = _request.Operation == "ListSivarcRules" ? _request.ObjectPathJson : _request.CollectionPathJson;
        if (_request.Operation == "ListSivarcRules")
        {
            var target = OptionalObjectAddress.Resolve(Rules(), raw);
            Page(target is IEnumerable && target is not string ? Items(target) : new[] { target }, meta, true);
            meta["dataComplete"] = false;
            return "SiVArc scalar properties and schema read; no generation.";
        }
        if (!new[] { "create", "update", "delete" }.Contains(_request.Action) || string.IsNullOrWhiteSpace(_request.Name))
            throw new ArgumentException("Exact name and create/update/delete required.");
        using var access = Edit(!_request.DryRun);
        var collection = OptionalObjectAddress.Resolve(Rules(), raw);
        if (collection.GetType().Namespace != "Siemens.Engineering.SiVArc" || !collection.GetType().Name.EndsWith("Composition", StringComparison.Ordinal))
            throw new ArgumentException("Select a native SiVArc rule/folder/table collection.");
        var targetObject = Find(collection, _request.Name);
        if ((_request.Action == "create") == (targetObject != null)) throw new InvalidOperationException(_request.Action == "create" ? "Object exists." : "Exact object not found.");
        var changes = Object(_request.PropertiesJson, "propertiesJson");
        if (changes.ContainsKey("Name") || _request.Action == "delete" && changes.Count != 0) throw new ArgumentException("No renaming or changes during delete.");
        if (_request.Action == "delete") RequireEmpty(targetObject!);
        var type = targetObject?.GetType() ?? collection.GetType().GetMethod("Create", new[] { typeof(string) })?.ReturnType
            ?? throw new NotSupportedException("Native Create(string) unavailable.");
        var prepared = OptionalScalars.Prepare(type, changes);
        if (targetObject != null) meta["before"] = OptionalObjectAddress.Read(targetObject);
        if (_request.DryRun) return "SiVArc rule edit preview; no generation performed.";
        meta["mayHaveChanged"] = true;
        if (_request.Action == "create") targetObject = Call(collection, "Create", _request.Name)!;
        if (_request.Action == "delete")
        {
            Call(targetObject!, "Delete");
            if (Find(OptionalObjectAddress.Resolve(Rules(), raw), _request.Name) != null) throw new InvalidOperationException("Rule remains after deletion.");
            meta["verifiedAbsent"] = true;
            meta["verifiedOnFreshNavigation"] = true;
        }
        else
        {
            OptionalScalars.Apply(targetObject!, prepared, meta);
            meta["after"] = OptionalObjectAddress.Read(targetObject!);
        }
        return "SiVArc native rule operation completed; no generation, save, compile or download.";
    }

    private static void RequireEmpty(object target)
    {
        foreach (var property in target.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true
            && p.PropertyType.Name.EndsWith("Composition", StringComparison.Ordinal)))
        {
            var children = property.GetValue(target);
            if (children != null && Group.Items(children).Any()) throw new InvalidOperationException("Nonempty rule container deletion refused: " + property.Name);
        }
    }

    private string SivarcContainer(HmiObject meta)
    {
        bool writing = SivarcPolicy.ValidateContainerRequest(_request.Category, _request.Kind, _request.Path, _request.Action, _request.TypePath,
            _request.TypeVersion, _request.ConfirmDelete, _request.DryRun);
        using var access = Edit(writing);
        var parts = Group.Parts(_request.Path);
        var root = Rules();
        var parentPath = string.Join("/", parts.Take(parts.Length - 1));
        var collection = Get(Folder(root, parentPath), _request.Kind == "folder" ? "Folders" : "Tables");
        var target = Find(collection, parts.Last());
        if (target != null) meta["before"] = Row(target);
        if (_request.Action == "read")
        {
            if (target == null) throw new PlcSoftwareException("NotFound", "SiVArc container not found: " + _request.Path);
            return "SiVArc container read; no generation.";
        }
        object? version = null;
        if (_request.Action == "createFromType") version = _engine.OptionalTypeVersion(_engine.OptionalLibraryType(
            _engine.ExactOpenEngineeringLibrary(_request.LibraryName), _request.TypePath), _request.TypeVersion);
        if (_request.Action == "delete")
        {
            if (target == null) throw new PlcSoftwareException("NotFound", "SiVArc container not found: " + _request.Path);
            if (Optional(target, "IsDefault") is true) throw new InvalidOperationException("The default rule table cannot be deleted.");
            if (_request.Kind == "folder") RequireEmpty(target);
        }
        else if (target != null) throw new InvalidOperationException("SiVArc container already exists.");
        if (!writing) return "SiVArc container preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        if (_request.Action == "delete")
        {
            Call(target!, "Delete");
            var fresh = Get(Folder(Rules(), parentPath), _request.Kind == "folder" ? "Folders" : "Tables");
            if (Find(fresh, parts.Last()) != null) throw new InvalidOperationException("SiVArc container remains after Delete.");
            meta["verifiedAbsent"] = true;
        }
        else meta["created"] = Row(version == null ? Call(collection, "Create", parts.Last())! : Call(collection, "CreateFrom", version)!);
        return "SiVArc container operation completed; no generation or save.";
    }

    private string SivarcRule(HmiObject meta)
    {
        bool writing = SivarcPolicy.ValidateRuleRequest(_request.Category, _request.TablePath, _request.RulePath, _request.Kind, _request.Action,
            _request.PropertiesJson, _request.ReferencesJson, _request.DeviceSelectionJson, _request.MasterCopyPath, _request.CreateOption, _request.ConfirmDelete, _request.DryRun);
        using var access = Edit(writing);
        var parts = Group.Parts(_request.RulePath, _request.Action == "createFromMasterCopy");
        var root = Rules();
        var table = _release is "15.1" or "16" or "17" ? root : Table(root, _request.TablePath);
        var ownerPath = _request.Action == "createFromMasterCopy" ? _request.RulePath : string.Join("/", parts.Take(parts.Length - 1));
        var owner = RuleGroup(table, ownerPath);
        var collection = Get(owner, _request.Kind == "group" ? "Groups" : "Rules");
        var target = _request.Action == "createFromMasterCopy" ? null : Find(collection, parts.Last());
        if (_request.Action == "read")
        {
            if (target == null) throw new PlcSoftwareException("NotFound", "SiVArc rule not found: " + _request.RulePath);
            meta["before"] = Row(target);
            return "SiVArc rule read; no generation.";
        }
        var properties = Object(_request.PropertiesJson, "propertiesJson");
        var references = Object(_request.ReferencesJson, "referencesJson");
        var devices = Object(_request.DeviceSelectionJson, "deviceSelectionJson");
        var resolved = new Dictionary<string, object?>();
        foreach (var pair in references) resolved[pair.Key] = pair.Value == null ? null : Reference(SivarcPolicy.ParseReference(pair.Value, pair.Key));
        if (_request.Action != "create" && _request.Action != "createFromMasterCopy" && target == null)
            throw new PlcSoftwareException("NotFound", "SiVArc rule not found: " + _request.RulePath);
        if (_request.Action == "delete" && _request.Kind == "group") RequireEmpty(target!);
        if (_request.Action == "create" && target != null) throw new InvalidOperationException("SiVArc rule already exists.");
        var copy = _request.Action == "createFromMasterCopy" ? _engine.PlcB5Session.ExactMasterCopy(_request.LibraryName, _request.MasterCopyPath) : null;
        if (!writing) return "SiVArc rule preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        if (_request.Action == "delete")
        {
            Call(target!, "Delete");
            var fresh = RuleGroup(_release is "15.1" or "16" or "17" ? Rules() : Table(Rules(), _request.TablePath), ownerPath);
            if (Find(Get(fresh, _request.Kind == "group" ? "Groups" : "Rules"), parts.Last()) != null) throw new InvalidOperationException("SiVArc rule remains after Delete.");
            meta["verifiedAbsent"] = true;
            return "SiVArc rule deleted and verified on fresh navigation.";
        }
        if (_request.Action == "create") target = Call(collection, "Create", parts.Last());
        if (copy != null) target = Call(collection, "CreateFrom", copy, EnumValue("Siemens.Engineering.SiVArc.CreateOptions", _request.CreateOption));
        OptionalScalars.Apply(target!, OptionalScalars.Prepare(target!.GetType(), properties), meta);
        foreach (var pair in resolved) target.GetType().GetProperty(pair.Key)!.SetValue(target, pair.Value);
        if (devices.Count != 0) ((IEngineeringObject)target).SetAttributes(devices.Select(p => new KeyValuePair<string, object>(p.Key, p.Value!.OptionalValue<bool>())).ToList());
        meta["after"] = Row(target);
        return "SiVArc rule operation completed; no generation or save.";
    }

    private object Reference(SivarcPolicy.Reference reference)
    {
        if (reference.Kind == "plcBlock") return _engine.PlcB5Session.ExactMasterCopyPlcSource(reference.SoftwarePath, reference.Path, true) as CodeBlock
            ?? throw new ArgumentException("Referenced block is not a CodeBlock: " + reference.Path);
        if (reference.Kind == "masterCopy") return _engine.PlcB5Session.ExactMasterCopy(reference.LibraryName, reference.Path);
        var library = _engine.ExactOpenEngineeringLibrary(reference.LibraryName);
        if (reference.Kind == "libraryType") return _engine.OptionalLibraryType(library, reference.Path);
        return _engine.HmiLibraryFolder(library, reference.Path, reference.Kind == "masterCopyFolder" ? "MasterCopySystemFolder" : "LibraryTypeSystemFolder");
    }

    private string Definitions(HmiObject meta)
    {
        bool writing = _request.Operation == "ManageSivarcBlockDefinition" && SivarcPolicy.ValidateDefinitionRequest(_request.BlockPath,
            _request.Kind, _request.Name, _request.Action, _request.PropertiesJson, _request.TextsJson, _request.ConfirmDelete, _request.DryRun);
        using var access = Edit(writing);
        _engine.PlcB5Session.ExactPlcForEngineering(_request.SoftwarePath, writing);
        var block = _engine.PlcB5Session.ExactMasterCopyPlcSource(_request.SoftwarePath, _request.BlockPath, true) as PlcBlock;
        SivarcPolicy.RequireSupportedBlockClass(block?.GetType().Name ?? "non-code block");
        var provider = Service(block!, "SiVArc.SivarcDataProvider");
        if (_request.Operation == "ListSivarcBlockDefinitions")
        {
            meta["tagDefinitions"] = new HmiArray(Items(Get(provider, "TagDefinitions")).Select(v => (object?)Row(v)).ToArray());
            meta["textDefinitions"] = new HmiArray(Items(Get(provider, "TextDefinitions")).Select(v => (object?)Row(v)).ToArray());
            return "SiVArc FB/FC definitions read; OB access was refused before service lookup.";
        }
        string collectionName = _request.Kind == "textDefinition" ? "TextDefinitions" : "TagDefinitions";
        var collection = Get(provider, collectionName);
        var target = Find(collection, _request.Name);
        if (_request.Action == "create" && target != null) throw new InvalidOperationException("SiVArc definition already exists.");
        if (_request.Action != "create" && target == null) throw new PlcSoftwareException("NotFound", "SiVArc definition not found: " + _request.Name);
        if (target != null) meta["before"] = Row(target);
        if (_request.Action == "read") return "SiVArc definition read; no change.";
        var properties = Object(_request.PropertiesJson, "propertiesJson");
        var texts = Object(_request.TextsJson, "textsJson");
        if (!writing) return "SiVArc definition preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        if (_request.Action == "delete")
        {
            Call(target!, "Delete");
            if (Find(Get(Service(block!, "SiVArc.SivarcDataProvider"), collectionName), _request.Name) != null) throw new InvalidOperationException("Definition remains after Delete.");
            meta["verifiedAbsent"] = true;
            return "SiVArc definition deleted and verified on fresh navigation.";
        }
        if (_request.Action == "create") target = Call(collection, "Create", _request.Name);
        OptionalScalars.Apply(target!, OptionalScalars.Prepare(target!.GetType(), properties), meta);
        if (texts.Count != 0)
        {
            var text = Get(target, "Text");
            foreach (var pair in texts)
            {
                var item = Items(Get(text, "Items")).SingleOrDefault(v => Convert.ToString(Optional(Get(Get(v, "Language"), "Culture"), "Name"), CultureInfo.InvariantCulture) == pair.Key)
                    ?? throw new ArgumentException("Project language unavailable: " + pair.Key);
                item.GetType().GetProperty("Text")!.SetValue(item, pair.Value!.OptionalValue<string>());
            }
        }
        meta["after"] = Row(target);
        return "SiVArc definition updated; no generation or save.";
    }

    private string DriveObjects(HmiObject meta)
    {
        var item = DriveItem();
        var objects = Drives(item);
        meta["deviceItem"] = item.Name;
        meta["records"] = new HmiArray(objects.Select((drive, index) => {
            var row = new HmiObject { ["index"] = index, ["driveObjectNumber"] = null };
            OptionalEvidence.Safe(row, "telegramCount", () => Items(Get(drive, "Telegrams")).Length);
            OptionalEvidence.Safe(row, "securityAvailable", () => Optional(drive, "Security") != null);
            if (_request.IncludeTelegrams) OptionalEvidence.Safe(row, "telegrams", () => new HmiArray(Items(Get(drive, "Telegrams")).Select(v => (object?)Row(v)).ToArray()));
            if (_request.IncludeFunctions && _release != "14sp1") OptionalEvidence.Safe(row, "functionInterface", () => FunctionRow(Service(drive, "MC.Drives.DFI.DriveFunctionInterface")));
            if (_request.IncludeTechnologyExtensions && _release is "18" or "19") OptionalEvidence.Safe(row, "technologyExtensions", () => new HmiArray(Items(Get(Service(drive, "MC.Drives.TechnologyExtensionContainer"), "TechnologyExtensions")).Select(v => (object?)Row(v)).ToArray()));
            if (_request.IncludeDcc && _release is not ("14sp1" or "15.1")) OptionalEvidence.Safe(row, "dcc", () => DccSummary(Service(drive, "MC.Drives.Dcc.DriveControlChartContainer")));
            return (object?)row;
        }).ToArray());
        meta["expectedCount"] = objects.Length;
        meta["actualCount"] = objects.Length;
        meta["scope"] = "Available offline drive fields; select objects by composition index on this release.";
        meta["unavailableIncludes"] = new HmiArray(new[] {
            _request.IncludeFunctions && _release == "14sp1" ? "functionInterface requires V15.1" : null,
            _request.IncludeTechnologyExtensions && _release is not ("18" or "19") ? "technologyExtensions requires V18" : null,
            _request.IncludeDcc && _release is "14sp1" or "15.1" ? "dcc requires V16" : null
        }.Where(note => note != null).Cast<object>().ToArray());
        meta["dataComplete"] = false;
        return "Startdrive objects read; no online drive access.";
    }

    private static object? Parameter(object composition, string name)
    {
        var direct = Call(composition, "Find", name);
        if (direct != null) return direct;
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        var parent = Call(composition, "Find", name.Substring(0, dot));
        return parent == null ? null : Items(Get(parent, "Bits")).FirstOrDefault(v => string.Equals(Name(v), name, StringComparison.OrdinalIgnoreCase));
    }

    private string Parameters(HmiObject meta)
    {
        if (_request.Operation is "GetDriveParameters" or "GetOnlineDriveParameters")
        {
            bool online = _request.Operation == "GetOnlineDriveParameters";
            var selection = StartdrivePolicy.ValidateParametersRequest(online ? "write" : _request.Source, _request.NamesJson, _request.NumbersJson, _request.Offset, _request.Limit);
            foreach (var number in selection.Numbers) OptionalNativeOperations.ParameterNumber(_release, number.Number);
            object parameterOwner;
            if (online)
            {
                var objects = Items(Get(Service(DriveItem(), "MC.Drives.OnlineDriveObjectContainer"), "OnlineDriveObjects"));
                var selector = StartdrivePolicy.ParseDriveSelector(_request.DriveObjectNumber, _request.DriveObjectIndex);
                if (selector.Index >= objects.Length) throw new PlcSoftwareException("NotFound", "online driveObjectIndex is out of range.");
                parameterOwner = objects[selector.Index];
                meta["online"] = true;
                meta["driveObject"] = selector.Label;
            }
            else parameterOwner = Drive();
            var composition = Get(parameterOwner, "Parameters");
            var missing = new HmiArray();
            object[] parameters;
            if (selection.Enumerate)
            {
                var page = Items(composition).Skip(_request.Offset).Take(_request.Limit + 1).ToArray();
                parameters = page.Take(_request.Limit).ToArray();
                meta["truncated"] = page.Length > _request.Limit;
                meta["nextOffset"] = page.Length > _request.Limit ? _request.Offset + _request.Limit : (int?)null;
            }
            else parameters = selection.Names.Select(n => (Name: n, Parameter: Parameter(composition, n)))
                .Concat(selection.Numbers.Select(n => (Name: n.Number + (n.ArrayIndex >= 0 ? "[" + n.ArrayIndex + "]" : ""), Parameter: Call(composition, "Find", OptionalNativeOperations.ParameterNumber(_release, n.Number), n.ArrayIndex))))
                .Where(p => { if (p.Parameter == null) missing.Add(p.Name); return p.Parameter != null; }).Select(p => p.Parameter!).ToArray();
            meta["records"] = new HmiArray(parameters.Select(p => (object?)ParameterRow(p, _request.IncludeValue, _request.IncludeBits, _request.IncludeEnumValues)).ToArray());
            meta["actualCount"] = parameters.Length;
            meta["source"] = "Parameters";
            meta["offset"] = _request.Offset;
            meta["limit"] = _request.Limit;
            meta["notFound"] = missing;
            meta["includeValue"] = _request.IncludeValue;
            return online ? "Online drive parameters read through OnlineDriveObject.Parameters; nothing written."
                : "Offline writable-view drive parameters read; no online device access.";
        }
        StartdrivePolicy.ValidateParameterWriteRequest(_request.Parameter, _request.Action);
        bool writing = _request.Action == "write" && !_request.DryRun;
        using var access = Edit(writing);
        var drive = Drive();
        var parametersView = Get(drive, "Parameters");
        if (_request.Action == "read")
        {
            meta["before"] = StartdrivePolicy.ReadExactParameter<object, object>(_request.Parameter, n => Parameter(parametersView, n), n => null,
                p => BicoOrScalar(Get(p, "Value")), p => null)
                ?? throw new PlcSoftwareException("NotFound", "Exact drive parameter not found: " + _request.Parameter);
            return "Exact offline parameter value read; no bit expansion or online device access.";
        }
        var target = Parameter(parametersView, _request.Parameter) ?? throw new PlcSoftwareException("NotFound", "Exact writable drive parameter not found: " + _request.Parameter);
        meta["before"] = ParameterRow(target, true);
        var requested = StartdrivePolicy.ParseParameterValue(_request.ValueJson);
        var current = Get(target, "Value");
        object? value = requested.IsBico ? Parameter(parametersView, requested.BicoSource) ?? throw new PlcSoftwareException("NotFound", "BICO source not found.")
            : HmiScalar.ConvertValue(requested.Scalar, current?.GetType() ?? typeof(object));
        if (!writing) return "Offline parameter write preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        target.GetType().GetProperty("Value")!.SetValue(target, value);
        meta["after"] = ParameterRow(Parameter(parametersView, _request.Parameter) ?? target, true);
        return "Offline parameter changed and read back; no download or online device command.";
    }

    private static HmiObject BicoOrScalar(object? value)
    {
        if (value != null && value.GetType().FullName is "Siemens.Engineering.MC.Drives.DriveParameter" or "Siemens.Engineering.Startdrive.DriveParameter")
            return new HmiObject { ["bicoSource"] = Name(value), ["parameterText"] = Get(value, "ParameterText") };
        if (value != null && !HmiScalar.Scalar(value.GetType())) throw new NotSupportedException("Complex drive parameter values cannot leave the worker lane.");
        return new HmiObject { ["value"] = HmiScalar.Json(value) };
    }

    private static HmiObject ParameterRow(object parameter, bool value, bool bits = false, bool enums = false)
    {
        var row = new HmiObject { ["name"] = Name(parameter), ["parameterClass"] = parameter.GetType().Name };
        OptionalEvidence.Safe(row, "number", () => HmiScalar.Json(Optional(parameter, "Number") ?? Get(parameter, "ParamNo")));
        foreach (var field in new[] { "ArrayIndex", "ArrayLength", "MinValue", "MaxValue", "Unit", "ParameterText", "TypeName" })
            OptionalEvidence.Safe(row, char.ToLowerInvariant(field[0]) + field.Substring(1), () => HmiScalar.Json(Get(parameter, field)));
        if (enums) OptionalEvidence.Safe(row, "enumValueList", () => {
            var values = new HmiObject();
            if (Get(parameter, "EnumValueList") is IDictionary entries)
                foreach (DictionaryEntry pair in entries)
                {
                    if (values.Count == 500) break;
                    values[Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!] = pair.Value;
                }
            return values;
        });
        if (bits) OptionalEvidence.Safe(row, "bits", () => new HmiArray(Items(Get(parameter, "Bits")).Take(64).Select(p => (object?)ParameterRow(p, value, false, enums)).ToArray()));
        if (value) OptionalEvidence.Safe(row, "value", () => BicoOrScalar(Get(parameter, "Value")));
        else row["value"] = null;
        return row;
    }

    private string HardwareModule(HmiObject meta)
    {
        StartdrivePolicy.ValidateHardwareModuleRequest(_request.Action, _request.TypeIdentifier, _request.PositionNumber, _request.DryRun);
        var item = DriveItem();
        var point = Service(item, "MC.Drives.ModuleAccessPoint");
        meta["deviceItem"] = item.Name;
        meta["moduleAccessPointHwIdentifiers"] = new HmiArray(Items(Get(point, "HwIdentifiers")).Select(v => Optional(v, "Identifier")).ToArray());
        meta["hardwareModule"] = null;
        meta["hardwareModuleNote"] = "DriveItemHardwareModule requires V21.";
        return "Module access point read; no drive component mutation.";
    }

    private string Security(HmiObject meta)
    {
        bool writing = StartdrivePolicy.ValidateSecurityRequest(_request.Action, _request.Password, _request.DryRun);
        using var access = Edit(writing);
        var security = Get(Drive(), "Security");
        meta["before"] = Row(security);
        if (_request.Action == "read") return "Drive security read; no change.";
        if (!writing) return "Drive security preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        object? result;
        if (_request.Action is "activateUmac" or "deactivateUmac") result = Call(Get(security, "UmacConfiguration"), _request.Action == "activateUmac" ? "Activate" : "Deactivate");
        else
        {
            using var password = EngineeringCredentialRules.ToSecureString(_request.Password);
            result = Call(Get(security, "DriveDataEncryption"), _request.Action == "activateEncryption" ? "Activate" : "Deactivate", password);
        }
        meta["result"] = result;
        if (result is false) meta["operationSuccess"] = false;
        return "Drive security action returned; no download.";
    }

    private string Extensions(HmiObject meta)
    {
        bool writing = StartdrivePolicy.ValidateTechnologyExtensionRequest(_request.Action, _request.Identifier, _request.FilePath, _request.ConfirmUninstallInUse, _request.DryRun);
        if (StartdrivePolicy.IsPortalScopedExtensionAction(_request.Action))
        {
            var provider = Service(_engine.PlcB5Session.CurrentPortal!, "MC.Drives.TechnologyExtensionInstallationProvider");
            var packages = Get(provider, "TechnologyExtensionPackages");
            meta["before"] = new HmiArray(Items(packages).Select(v => (object?)Row(v)).ToArray());
            if (_request.Action == "readPackages") return "Installed technology extension packages read.";
            var package = _request.Action == "uninstall" ? Items(packages).SingleOrDefault(v => Convert.ToString(Get(v, "Identifier"), CultureInfo.InvariantCulture) == _request.Identifier)
                ?? throw new PlcSoftwareException("NotFound", "Exact technology extension package not found.") : null;
            var file = package == null ? OptionalRules.RequireExistingInputFile(_request.FilePath, "filePath") : null;
            if (!writing) return "Technology extension installation preview; nothing changed.";
            meta["mayHaveChanged"] = true;
            meta["result"] = package == null ? Call(provider, "Install", file!) : Call(provider, "Uninstall", _request.Identifier, _request.ConfirmUninstallInUse);
            return "Technology extension installation action returned.";
        }
        using var access = Edit(writing);
        var container = Service(Drive(), "MC.Drives.TechnologyExtensionContainer");
        var extensions = Get(container, "TechnologyExtensions");
        meta["before"] = new HmiArray(Items(extensions).Select(v => (object?)Row(v)).ToArray());
        if (_request.Action == "read") return "Drive technology extensions read.";
        var target = Items(extensions).SingleOrDefault(v => Convert.ToString(Get(v, "Identifier"), CultureInfo.InvariantCulture) == _request.Identifier)
            ?? throw new PlcSoftwareException("NotFound", "Exact drive technology extension not found.");
        if (!writing) return "Drive technology extension preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        meta["result"] = Call(target, _request.Action == "activate" ? "Activate" : "Deactivate");
        return "Drive technology extension action returned; no download.";
    }

    private string Telegrams(HmiObject meta)
    {
        var plan = StartdrivePolicy.ValidateTelegramRequest(_request.Action, _request.TelegramType, _request.TelegramNumber, _request.InputSize,
            _request.OutputSize, _request.Direction, _request.Size, _request.KeepOriginalAddress, _request.SoftwarePath, _request.ObjectPath,
            _request.InterfaceKind, _request.SensorIndex, _request.ConnectOption, _request.DryRun);
        using var access = Edit(plan.Writes);
        var composition = Get(Drive(), "Telegrams");
        var all = Items(composition);
        meta["before"] = new HmiArray(all.Select(v => (object?)Row(v)).ToArray());
        if (_request.Action == "read") return "Drive telegrams read; no change.";
        if (all.Length == 0) throw new PlcSoftwareException("InvalidState", "This drive object has no telegram yet; Can*/Insert*/Erase calls are refused to avoid a known TIA crash.");
        var type = EnumValue(_release == "14sp1" ? "Siemens.Engineering.Startdrive.Enums.TelegramType" : "Siemens.Engineering.MC.Drives.Enums.TelegramType", plan.TelegramType);
        var target = Call(composition, "Find", type);
        if (_request.TelegramType == "MainTelegram" && target != null && _request.Action is "check" or "insert")
        {
            if (_request.Action == "insert") throw new PlcSoftwareException("InvalidState", "MainTelegram already exists; use changeNumber or changeSize.");
            meta["checks"] = new HmiObject { ["mainTelegramPresent"] = true, ["canInsertMainTelegram"] = null };
            return "A main telegram already exists; no CanInsertMainTelegram or InsertMainTelegram call was issued.";
        }
        string suffix = plan.TelegramType == "AdditionalTelegram" ? "AdditionalTelegram" : plan.TelegramType == "SupplementaryTelegram" ? "SupplementaryTelegram" : plan.TelegramType == "MainTelegram" ? "MainTelegram" : plan.TelegramType == "TorqueTelegram" ? "TorqueTelegram" : "SafetyTelegram";
        if (_request.Action is "check" or "insert")
        {
            var args = plan.TelegramType == "AdditionalTelegram" ? new object?[] { plan.InputSize, plan.OutputSize } : new object?[] { plan.TelegramNumber };
            var allowed = Call(composition, "CanInsert" + suffix, args);
            meta["canInsert"] = allowed;
            if (_request.Action == "check" || !plan.Writes) return "Drive telegram insertion checked; nothing changed.";
            OptionalNativeOperations.RequireCanChange(allowed);
            meta["mayHaveChanged"] = true;
            var inserted = Call(composition, "Insert" + suffix, args);
            meta["result"] = inserted == null || inserted is bool ? inserted : Row(inserted);
        }
        else
        {
            if (target == null) throw new PlcSoftwareException("NotFound", "Exact telegram not found.");
            if (_request.Action == "changeNumber")
            {
                OptionalNativeOperations.ChangeTelegramNumber(target, plan.TelegramNumber, plan.Writes, () => meta["mayHaveChanged"] = true);
                if (!plan.Writes) return "Drive telegram edit preview; nothing changed.";
            }
            else if (_request.Action == "changeSize")
            {
                var direction = EnumValue("Siemens.Engineering.HW.AddressIoType", plan.Direction);
                OptionalNativeOperations.RequireCanChange(Call(target, "CanChangeSize", direction, plan.Size, plan.KeepOriginalAddress));
                if (!plan.Writes) return "Drive telegram edit preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                Call(target, "ChangeSize", direction, plan.Size, plan.KeepOriginalAddress);
            }
            else
            {
                if (!plan.Writes) return "Drive telegram edit preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (_request.Action == "erase") Call(composition, "EraseTelegram", type);
                else
                {
                    var technology = _engine.OptionalTechnology(plan.SoftwarePath, plan.ObjectPath, true);
                    var provider = Service(technology, plan.InterfaceKind == "encoder" ? "SW.TechnologicalObjects.Motion.EncoderHardwareConnectionProvider" : "SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider");
                    var connection = Get(provider, plan.InterfaceKind == "actor" ? "ActorInterface" : "SensorInterface");
                    if (plan.InterfaceKind == "sensor")
                    {
                        var sensors = Items(connection);
                        if (plan.SensorIndex >= sensors.Length) throw new ArgumentException("sensorIndex out of range: " + sensors.Length + " sensor interface(s).");
                        connection = sensors[plan.SensorIndex];
                    }
                    if (plan.HasConnectOption) Call(connection, "Connect", target, EnumValue("Siemens.Engineering.MC.Drives.Enums.ConnectOption", plan.ConnectOption));
                    else Call(connection, "Connect", target);
                    meta["after"] = OptionalObjectAddress.Read(connection);
                }
            }
        }
        meta["after"] = new HmiArray(Items(Get(Drive(), "Telegrams")).Select(v => (object?)Row(v)).ToArray());
        return "Drive telegram action returned and read back; no download.";
    }

    private string Functions(HmiObject meta)
    {
        if (_request.Operation == "ManageOnlineDriveFunctions")
        {
            StartdrivePolicy.ValidateOnlineFunctionRequest(_request.Action, _request.ResetMode, _request.ActivationState, _request.ConfirmOnline);
            var container = Service(DriveItem(), "MC.Drives.OnlineDriveObjectContainer");
            var drives = Items(Get(container, "OnlineDriveObjects"));
            var selector = StartdrivePolicy.ParseDriveSelector(_request.DriveObjectNumber, _request.DriveObjectIndex);
            if (selector.Index >= drives.Length) throw new PlcSoftwareException("NotFound", "Online driveObjectIndex is out of range.");
            var online = Service(drives[selector.Index], "MC.Drives.DFI.OnlineDriveFunctionInterface");
            meta["before"] = Row(online);
            if (_request.Action == "read") return "Online drive function interface read; nothing written.";
            meta["mayHaveChanged"] = true;
            meta["result"] = _request.Action == "performFactoryReset" ? Call(Get(online, "DriveDomainFunctions"), "PerformFactoryReset", EnumValue("Siemens.Engineering.MC.Drives.Enums.ResetMode", _request.ResetMode))
                : _request.Action == "performRamToRomCopy" ? Call(Get(online, "DriveDomainFunctions"), "PerformRAMtoROMCopyAllDriveObject")
                : Call(Get(Get(online, "DriveObjectFunctions"), "DriveObjectActivation"), "ChangeActivationState", EnumValue("Siemens.Engineering.MC.Drives.Enums.DriveObjectActivationState", _request.ActivationState));
            if (meta["result"] is false) meta["operationSuccess"] = false;
            return "Online drive function returned; inspect the device state before any retry.";
        }
        var plan = StartdrivePolicy.ValidateFunctionRequest(_request.Action, _request.ValueJson, _request.DryRun);
        using var access = Edit(plan.Writes);
        var functions = Service(Drive(), "MC.Drives.DFI.DriveFunctionInterface");
        meta["before"] = FunctionRow(functions);
        if (_request.Action == "read") return "Offline drive function interface read.";
        if (!plan.Writes && _request.Action is not "readMotorConfiguration" and not "readEncoderConfiguration") return "Offline drive function preview; nothing changed.";
        object? result;
        if (_request.Action == "changeDriveObjectType")
        {
            var handler = Get(Get(functions, "DriveObjectFunctions"), "DriveObjectTypeHandler");
            var type = Call(Get(handler, "PossibleDriveObjectTypes"), "Find", plan.DriveObjectType)
                ?? throw new PlcSoftwareException("NotFound", "Requested drive object type is unavailable.");
            meta["mayHaveChanged"] = true;
            result = Call(handler, "ChangeDriveObjectType", type);
        }
        else if (_request.Action == "changeActivationState")
        {
            meta["mayHaveChanged"] = true;
            result = Call(Get(Get(functions, "DriveObjectFunctions"), "DriveObjectActivation"), "ChangeActivationState",
                EnumValue("Siemens.Engineering.MC.Drives.Enums.DriveObjectActivationState", plan.ActivationState));
        }
        else if (_request.Action == "setSimoGearMlfb")
        {
            meta["mayHaveChanged"] = true;
            result = Call(Get(functions, "Commissioning"), "SetSimoGearMlfb", plan.Mlfb);
        }
        else if (_request.Action == "updateCheckSums")
        {
            meta["mayHaveChanged"] = true;
            result = Call(Get(functions, "SafetyCommissioning"), "UpdateCheckSums");
        }
        else
        {
            var projection = Get(functions, "HardwareProjection");
            string method = _request.Action switch {
                "setMotorType" => "SetMotorType", "readMotorConfiguration" or "projectMotorConfiguration" or "setEquivalentCircuitDiagramData" => "GetCurrentMotorConfiguration",
                "setEncoder" => "SetEncoder", _ => "GetCurrentEncoderConfiguration" };
            object?[] args = _request.Action == "setMotorType" ? new object?[] { EnumValue("Siemens.Engineering.MC.Drives.Enums.MotorType", plan.MotorType), (ushort)plan.DataSet }
                : _request.Action == "setEncoder" ? new object?[] { EnumValue("Siemens.Engineering.MC.Drives.Enums.EncoderInterface", plan.EncoderInterface),
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.EncoderType", plan.EncoderType), EnumValue("Siemens.Engineering.MC.Drives.Enums.AbsoluteIncrementalFlag", plan.AbsoluteIncremental),
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.RotaryLinearFlag", plan.RotaryLinear), (ushort)plan.EncoderNumber }
                : _request.Action.Contains("Motor") || _request.Action == "setEquivalentCircuitDiagramData" ? new object?[] { (ushort)plan.DataSet }
                : new object?[] { (ushort)plan.EncoderNumber };
            if (plan.Writes && _request.Action is "setMotorType" or "setEncoder") meta["mayHaveChanged"] = true;
            result = Call(projection, method, args);
            if (_request.Action == "setEncoderType")
            {
                meta["mayHaveChanged"] = true;
                var rotary = EnumValue("Siemens.Engineering.MC.Drives.Enums.RotaryLinearFlag", plan.RotaryLinear);
                result = plan.HasAbsoluteIncremental ? Call(result!, "SetEncoderType", rotary,
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.AbsoluteIncrementalFlag", plan.AbsoluteIncremental)) : Call(result!, "SetEncoderType", rotary);
            }
            if (_request.Action is "projectMotorConfiguration" or "projectEncoderConfiguration" or "setEquivalentCircuitDiagramData")
            {
                var configuration = result!;
                var entries = Items(Get(configuration, "RequiredConfigurationEntries")).Concat(Optional(configuration, "OptionalConfigurationEntries") is object optional ? Items(optional) : Array.Empty<object>()).ToArray();
                OptionalNativeOperations.ApplyConfigurationEntries(entries, plan.Entries, HmiScalar.ConvertValue,
                    () => meta["mayHaveChanged"] = true);
                result = _request.Action == "setEquivalentCircuitDiagramData" ? Call(configuration, "SetEquivalentCircuitDiagramData", plan.EquivalentCircuitDiagram)
                    : Call(projection, _request.Action == "projectEncoderConfiguration" ? "ProjectEncoderConfiguration" : "ProjectMotorConfiguration", configuration,
                        (ushort)(_request.Action == "projectEncoderConfiguration" ? plan.EncoderNumber : plan.DataSet));
            }
        }
        meta["result"] = result == null || result is bool ? result : Row(result);
        if (result is false) meta["operationSuccess"] = false;
        meta["after"] = FunctionRow(functions);
        return "Offline drive function returned; no download or online drive command.";
    }

    private string DccObject(HmiObject meta)
    {
        OptionalRules.ValidatePagination(_request.Offset, _request.Limit);
        var target = OptionalObjectAddress.Resolve(Dcc(), _request.ObjectPathJson);
        Page(target is IEnumerable && target is not string ? Items(target) : new[] { target }, meta, true);
        meta["dataComplete"] = false;
        return "DCC scalar properties and schema read from an exact property path.";
    }

    private object Chart(object container, string path)
    {
        var parts = DccPolicy.ChartParts(path);
        object current = Named(Get(container, "Charts"), parts[0]);
        foreach (var part in parts.Skip(1)) current = Named(Get(current, "Subcharts"), part);
        return current;
    }

    private string DccCharts(HmiObject meta)
    {
        if (_request.MaxDepth < 1 || _request.MaxDepth > 8) throw new ArgumentException("maxDepth 1..8 required.");
        var container = Dcc();
        var charts = string.IsNullOrEmpty(_request.ChartPath) ? Items(Get(container, "Charts")) : new[] { Chart(container, _request.ChartPath) };
        if (_request.IncludeLibraries) meta["dcbLibraries"] = new HmiArray(Items(Get(container, "DcbLibraries")).Select(v => (object?)Row(v)).ToArray());
        meta["records"] = new HmiArray(charts.Select(v => (object?)ChartRow(v, 1)).ToArray());
        meta["expectedCount"] = charts.Length;
        meta["actualCount"] = charts.Length;
        meta["dataComplete"] = false;
        return "Available DCC chart properties read; no modification.";
    }

    private static HmiObject DccSummary(object container) => new HmiObject { ["chartCount"] = Items(Get(container, "Charts")).Length };

    private static HmiObject FunctionRow(object functions)
    {
        var row = new HmiObject();
        OptionalEvidence.Safe(row, "driveObjectType", () => {
            var handler = Optional(Get(functions, "DriveObjectFunctions"), "DriveObjectTypeHandler");
            return handler == null ? null : new HmiObject { ["current"] = Optional(handler, "CurrentDriveObjectType") is object current ? Row(current) : null,
                ["possible"] = new HmiArray(Items(Get(handler, "PossibleDriveObjectTypes")).Select(v => (object?)Row(v)).ToArray()) };
        });
        OptionalEvidence.Safe(row, "activation", () => {
            var activation = Optional(Get(functions, "DriveObjectFunctions"), "DriveObjectActivation");
            return activation == null ? null : new HmiObject { ["activationState"] = Get(activation, "ActivationState").ToString(), ["isActive"] = Get(activation, "IsActive") };
        });
        foreach (var field in new[] { "FunctionInUse", "Commissioning", "SafetyCommissioning", "HardwareProjection" })
            OptionalEvidence.Safe(row, char.ToLowerInvariant(field[0])+field.Substring(1)+"Available", () => Optional(functions, field) != null);
        return row;
    }

    private HmiObject ChartRow(object chart, int depth)
    {
        var row = Row(chart);
        if (_request.IncludeBlocks) OptionalEvidence.Safe(row, "blocks", () => new HmiArray(Items(Get(chart, "Blocks")).Take(500).Select(block => {
            var value = Row(block);
            if (_request.IncludePins) value["pins"] = new HmiArray(Items(Get(block, "Pins")).Take(500).Select(pin => (object?)Row(pin)).ToArray());
            return (object?)value;
        }).ToArray()));
        OptionalEvidence.Safe(row, "chartInterfaces", () => new HmiArray(Items(Get(chart, "ChartInterfaces")).Take(200).Select(v => (object?)Row(v)).ToArray()));
        OptionalEvidence.Safe(row, "partitions", () => new HmiArray(Items(Get(chart, "Partitions")).Select(v => (object?)Row(v)).ToArray()));
        if (depth < _request.MaxDepth) OptionalEvidence.Safe(row, "subcharts", () => new HmiArray(Items(Get(chart, "Subcharts")).Take(200).Select(v => (object?)ChartRow(v, depth+1)).ToArray()));
        return row;
    }

    private string DcbLibraries(HmiObject meta)
    {
        bool writing = DccPolicy.ValidateLibraryRequest(_request.Action, _request.FilePath, _request.DryRun);
        if (_request.Action == "import")
        {
            var file = OptionalRules.RequireExistingInputFile(_request.FilePath, "filePath");
            meta["file"] = file.FullName; meta["action"] = _request.Action; meta["dryRun"] = _request.DryRun;
#if LIBRARY_DCB_IMPORT
            if (!writing) return "DCB extension library import preview; nothing imported into the project library.";
            using var access = Edit(true);
            meta["mayHaveChanged"] = true; meta["nativeSignature"] = "ProjectLibrary.ImportDcbLibrary(string)";
            _engine.PlcB5Session.CurrentProject!.ProjectLibrary.ImportDcbLibrary(file.FullName);
            return "DCB extension library imported into the project library; no save.";
#else
            throw new NotSupportedException("ProjectLibrary.ImportDcbLibrary requires V16 or later; V21 uses DcbLibraryImporter.");
#endif
        }
        Page(Items(Get(Dcc(), "DcbLibraries")), meta);
        return "DCB libraries of the selected drive object read; no import.";
    }

    private string DccEdit(HmiObject meta)
    {
        bool writing = _request.IsWrite;
        object plan;
        switch (_request.Operation)
        {
            case "ManageDccChart": plan = DccPolicy.ValidateChartRequest(_request.ChartName, _request.Action, _request.FilePath, _request.ImportOptions,
                _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun, _request.SequenceIndex); break;
            case "ManageDccBlock": plan = DccPolicy.ValidateBlockRequest(_request.ChartPath, _request.BlockName, _request.Action, _request.BlockType,
                _request.LibraryName, _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun, _request.SequenceIndex); break;
            case "ManageDccPin": plan = DccPolicy.ValidatePinRequest(_request.ChartPath, _request.BlockName, _request.PinName, _request.Action,
                _request.PropertiesJson, _request.PartnerJson, _request.SetAsSignal, _request.ParameterNumber, _request.ArrayIndex, _request.DryRun); break;
            case "ManageDccChartInterface": plan = DccPolicy.ValidateInterfaceRequest(_request.ChartPath, _request.InterfaceName, _request.Action,
                _request.SourceBlock, _request.SourcePin, _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun); break;
            default: plan = DccPolicy.ValidatePartitionRequest(_request.ChartPath, _request.PartitionName, _request.Action,
                _request.PropertiesJson, _request.ConfirmDelete, _request.DryRun); break;
        }
        using var access = Edit(writing);
        var container = Dcc();
        if (_request.Operation == "ManageDccChart" && _request.Action == "readSequence")
        {
            var sequence = string.IsNullOrEmpty(_request.ChartName) ? Call(Get(container, "Charts"), "GetChartSequence") : Call(Chart(container, _request.ChartName), "GetRunSequence");
            meta["sequence"] = new HmiArray(Items(sequence!).Take(1000).Select(v => (object?)Row(v)).ToArray());
            return "DCC execution sequence read; nothing changed.";
        }
        if (_request.Operation == "ManageDccChart" && _request.Action is "optimizeSequence" or "showEditor")
        {
            var selected = Chart(container, _request.ChartName);
            if (_request.DryRun) return "DCC sequence/editor preview; nothing changed.";
            meta["mayHaveChanged"] = _request.Action == "optimizeSequence";
            Call(selected, _request.Action == "showEditor" ? "ShowDccEditor" : "OptimizeRunSequence");
            return "DCC sequence/editor action returned; no save.";
        }
        if (_request.Operation == "ManageDccChart" && _request.Action is "exportAll" or "import")
        {
            var file = _request.Action == "import" ? OptionalRules.RequireExistingInputFile(_request.FilePath, "filePath") : SafetyFileOutput.Plan(_request.FilePath);
            if (_request.DryRun) return "DCC chart exchange preview; nothing changed or written.";
            if (_request.Action == "import")
            {
                meta["mayHaveChanged"] = true;
                meta["result"] = Row(Call(Get(container, "Charts"), "Import", file.FullName,
                    EnumValue("Siemens.Engineering.MC.Drives.Dcc.DccImportOptions", ((DccPolicy.ChartRequest)plan).ImportOption))!);
            }
            else
            {
                meta["mayHaveWrittenFiles"] = true;
                Call(Get(container, "Charts"), "Export", file.FullName);
                meta["output"] = SafetyFileOutput.Verify(file);
            }
            return "DCC chart exchange returned; no save.";
        }
        string path = _request.Operation == "ManageDccChart" ? _request.ChartName : _request.ChartPath;
        object? chart = null;
        object collection;
        string name;
        if (_request.Operation == "ManageDccChart")
        {
            collection = Get(container, "Charts");
            name = path;
        }
        else
        {
            chart = Chart(container, path);
            collection = Get(chart, _request.Operation == "ManageDccBlock" ? "Blocks" : _request.Operation == "ManageDccChartInterface" ? "ChartInterfaces" : _request.Operation == "ManageDccPin" ? "Blocks" : "Partitions");
            name = _request.Operation == "ManageDccBlock" ? _request.BlockName : _request.Operation == "ManageDccChartInterface" ? _request.InterfaceName : _request.PartitionName;
        }
        object? target;
        if (_request.Operation == "ManageDccPin")
        {
            var block = Named(Get(chart!, "Blocks"), _request.BlockName);
            target = Named(Get(block, "Pins"), _request.PinName);
        }
        else target = string.IsNullOrEmpty(name) ? null : Find(collection, name);
        if (_request.Action == "read")
        {
            if (target == null) Page(Items(collection), meta); else meta["before"] = Row(target);
            return "DCC object read; nothing changed.";
        }
        if (target != null) meta["before"] = Row(target);
        var properties = Object(_request.PropertiesJson, "propertiesJson");
        var scalarProperties = new HmiObject();
        foreach (var pair in properties) if (pair.Key != "Partition") scalarProperties[pair.Key] = pair.Value;
        var partition = properties.ContainsKey("Partition") ? Named(Get(chart ?? Chart(container, string.Join("/", DccPolicy.ChartParts(path).Take(DccPolicy.ChartParts(path).Length - 1))), "Partitions"), properties["Partition"]!.OptionalValue<string>()) : null;
        var changes = target == null ? null : OptionalScalars.Prepare((_request.Action == "updateParameter" ? Get(target, "Parameter") : target).GetType(), scalarProperties);
        if (_request.Action == "export")
        {
            var file = SafetyFileOutput.Plan(_request.FilePath);
            if (_request.DryRun) return "DCC export preview; nothing written.";
            meta["mayHaveWrittenFiles"] = true;
            Call(target!, "Export", file.FullName);
            meta["output"] = SafetyFileOutput.Verify(file);
            return "DCC object exported and hashed; content semantics not verified.";
        }
        if (_request.DryRun) return "DCC object edit preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        if (_request.Action == "delete")
        {
            Call(target!, "Delete");
            meta["verifiedAbsent"] = Find(collection, name) == null;
            return "DCC object deletion returned; no save.";
        }
        if (_request.Action == "create")
        {
            if (_request.Operation == "ManageDccBlock")
            {
                target = string.IsNullOrEmpty(name) ? Call(collection, "Create", _request.BlockType)
                    : string.IsNullOrEmpty(_request.LibraryName) ? Call(collection, "Create", name, _request.BlockType)
                    : Call(collection, "Create", name, _request.BlockType, _request.LibraryName);
            }
            else if (_request.Operation == "ManageDccChartInterface")
            {
                var pin = Named(Get(Named(Get(chart!, "Blocks"), _request.SourceBlock), "Pins"), _request.SourcePin);
                target = Call(collection, "Create", pin);
            }
            else target = _request.Operation == "ManageDccChart" && ((DccPolicy.ChartRequest)plan).AutoName ? Call(collection, "Create") : Call(collection, "Create", name);
        }
        else if (_request.Action == "setAsPredecessor") Call(target!, "SetAsPredecessor");
        else if (_request.Operation == "ManageDccPin" && _request.Action is "publish" or "unpublish")
        {
            var published = _request.Action == "unpublish" ? Call(target!, "Unpublish")
                : _request.ParameterNumber >= 0 ? Call(target!, "Publish", _request.SetAsSignal, (uint)_request.ParameterNumber)
                : Call(target!, "Publish", _request.SetAsSignal);
            meta["result"] = published == null ? null : Row(published);
        }
        else if (_request.Operation == "ManageDccPin" && _request.Action is "connect" or "disconnect")
        {
            var pinPlan = (DccPolicy.PinRequest)plan;
            var partnerChart = chart!;
            var partner = string.IsNullOrEmpty(pinPlan.PartnerInterface) ? Named(Get(Named(Get(partnerChart, "Blocks"), pinPlan.PartnerBlock), "Pins"), pinPlan.PartnerPin)
                : Named(Get(partnerChart, "ChartInterfaces"), pinPlan.PartnerInterface);
            if (_request.Action == "connect") meta["connection"] = Row(Call(target!, "Connect", partner)!);
            else
            {
                var connection = Items(Get(target!, "Connections")).FirstOrDefault(v => MatchesEndpoint(Get(v, "Sink"), pinPlan) || MatchesEndpoint(Get(v, "Source"), pinPlan))
                    ?? throw new PlcSoftwareException("NotFound", "No connection of pin " + _request.PinName + " to the given partner.");
                Call(connection, "Delete");
            }
        }
        else
        {
            if (_request.Action == "updateParameter") target = Get(target!, "Parameter");
            OptionalScalars.Apply(target!, changes ?? OptionalScalars.Prepare(target!.GetType(), scalarProperties), meta);
        }
        if (_request.Action == "create" && scalarProperties.Count != 0) OptionalScalars.Apply(target!, OptionalScalars.Prepare(target!.GetType(), scalarProperties), meta);
        if (partition != null) target!.GetType().GetProperty("Partition")!.SetValue(target, partition);
        if (_request.SequenceIndex >= 0)
        {
            Call(target!, "MoveInRuntimeSequence", (uint)_request.SequenceIndex);
        }
        meta["after"] = Row(target!);
        return "DCC native object operation returned; no save or download.";
    }

    private static bool MatchesEndpoint(object endpoint, DccPolicy.PinRequest request)
    {
        if (!string.IsNullOrEmpty(request.PartnerInterface)) return endpoint.GetType().Name == "DccChartInterface" && Name(endpoint) == request.PartnerInterface;
        return endpoint.GetType().Name == "DccPin" && Name(endpoint) == request.PartnerPin && Name(Get(endpoint, "Parent")) == request.PartnerBlock;
    }
}
