using TiaMcp.Adapters.Native.Engineering;
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

internal sealed partial class LegacyOptionalAdapter
{
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
        var names = (OptionalInputs.Parse(raw) as HmiArray ?? throw new ArgumentException("Expected a JSON array of exact paths."))
            .Select(n => n!.OptionalValue<string>()).ToArray();
        if (names.Length < 1 || names.Length > 500 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names
                .Length)
            throw new ArgumentException("Provide 1..500 unique nonempty paths.");
        return names;
    }

    private object Folder(object root, string path)
    {
        foreach (var part in EngineeringInputValidation.HmiFolderParts(path)) root = Named(Get(root, "Folders"), part);
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
        OptionalObjectAddress.Parse(raw);
        var changes = _request.Operation == "ManageSivarcRule" ? Object(_request.PropertiesJson, "propertiesJson") : new HmiObject();
        if (changes.ContainsKey("Name") || _request.Action == "delete" && changes.Count != 0)
            throw new ArgumentException("No renaming or changes during delete.");
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
        if ((_request.Action == "create") == (targetObject != null))
            throw new InvalidOperationException(_request.Action == "create" ? "Object exists." : "Exact object not found.");
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
            if (children != null && Group.Items(children).Any())
                throw new InvalidOperationException("Nonempty rule container deletion refused: " + property.Name);
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
            _engine.ExactOpenEngineeringLibrary(_request.LibraryName), string.Join("/", EngineeringInputValidation.HmiFolderParts(_request.TypePath))),
            _request.TypeVersion);
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
            _request.PropertiesJson, _request.ReferencesJson, _request.DeviceSelectionJson, _request.MasterCopyPath, _request.CreateOption, _request
                .ConfirmDelete, _request.DryRun);
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
            if (Find(Get(fresh, _request.Kind == "group" ? "Groups" : "Rules"), parts.Last()) != null)
                throw new InvalidOperationException("SiVArc rule remains after Delete.");
            meta["verifiedAbsent"] = true;
            return "SiVArc rule deleted and verified on fresh navigation.";
        }
        if (_request.Action == "create") target = Call(collection, "Create", parts.Last());
        if (copy != null) target = Call(collection, "CreateFrom", copy, EnumValue("Siemens.Engineering.SiVArc.CreateOptions", _request.CreateOption));
        OptionalScalars.Apply(target!, OptionalScalars.Prepare(target!.GetType(), properties), meta);
        EngineeringWriteJournal.CaptureProperties(target!, resolved.Select(pair => target!.GetType().GetProperty(pair.Key)!));
        foreach (var pair in resolved) {
            var property = target!.GetType().GetProperty(pair.Key)!; EngineeringWriteJournal.Property(target, property); property.SetValue(target, pair.Value);
        }
        if (devices.Count != 0)
        {
            var changes = devices.Select(p => new KeyValuePair<string, object>(p.Key, p.Value!.OptionalValue<bool>())).ToList();
            var set = typeof(IEngineeringObject).GetMethod("SetAttributes", new[] { typeof(IEnumerable<KeyValuePair<string, object>>) })!;
            EngineeringWriteJournal.BeforeCall(target!, set, new object[] { changes });
            ((IEngineeringObject)target!).SetAttributes(changes);
        }
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
            if (Find(Get(Service(block!, "SiVArc.SivarcDataProvider"), collectionName), _request.Name) != null)
                throw new InvalidOperationException("Definition remains after Delete.");
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
                var item = Items(Get(text, "Items"))
                    .SingleOrDefault(v => Convert.ToString(Optional(Get(Get(v, "Language"), "Culture"), "Name"), CultureInfo.InvariantCulture) == pair.Key)
                    ?? throw new ArgumentException("Project language unavailable: " + pair.Key);
                EngineeringWriteJournal.Property(item, item.GetType().GetProperty("Text")!);
                item.GetType().GetProperty("Text")!.SetValue(item, pair.Value!.OptionalValue<string>());
            }
        }
        meta["after"] = Row(target);
        return "SiVArc definition updated; no generation or save.";
    }

}
