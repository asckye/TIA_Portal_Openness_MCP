using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Library.MasterCopies;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Engineering;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static readonly TiaMcp.Adapters.Reflection.ReflectionSession LibraryReflectionValues = new TiaMcp.Adapters.Reflection
            .ReflectionSession((_, __, ___, ____) => null, () => null);
        public Func<string, object>? ResolveEngineeringLibraryDevice { get; set; }
        private object ResolveLibraryDevice(string path, string[] names)
            => ResolveEngineeringLibraryDevice != null ? ResolveEngineeringLibraryDevice(path)
                : EngineeringPlcSession.ExactEngineeringHardware(names, Array.Empty<string>());
        public Func<string, string, object>? ResolveEngineeringHmiScreen { get; set; }
        public Func<object, string>? EngineeringLibraryOptionKind { get; set; }
        public Func<object, object?>? RenderEngineeringValue { get; set; }
        private object ResolveLibraryHmiScreen(string software, string path)
            => ResolveEngineeringHmiScreen != null ? ResolveEngineeringHmiScreen(software, path)
                : ResolveReflectionObject("hmiscreen", software + ":" + path, software, true)
                    ?? throw new PlcSoftwareException("NotFound", "HMI screen '" + path + "' not found.");
        private string LibraryOptionPackageKind(object type) => EngineeringLibraryOptionKind?.Invoke(type) ?? "other";
        private static object? LibraryTypedMember(object target, string member)
        {
#if LIBRARY_V20
            if (target is LibraryTypeVersion version)
            {
                if (member == "OriginalLibrary") return version.OriginalLibrary;
            }
            if (target is LibraryType type)
            {
                if (member == "Namespace") return type.Namespace;
                if (member == "DoNotUse") return type.DoNotUse;
                if (member == "MinimumTargetDeviceVersion") return type.MinimumTargetDeviceVersion;
                if (member == "SetForUpdate") return type.SetForUpdate;
                if (member == "Status") return type.Status;
            }
            if (target is LibraryTypeFolder folder && member == "Status") return folder.Status;
#endif
            return target.GetType().GetProperty(member)?.GetValue(target);
        }
        private static bool LibraryIsDefault(LibraryTypeVersion version)
        {
#if LIBRARY_V17
            return version.IsDefault;
#else
            return false;
#endif
        }
        private static bool LibraryWriteProtected(GlobalLibrary library)
        {
            if (library.IsReadOnly) return true;
#if LIBRARY_V20
            return library.IsWriteProtected;
#else
            return library.GetType().GetProperty("IsWriteProtected")?.GetValue(library) is bool protectedValue && protectedValue;
#endif
        }
        private object? LibraryValue(object? value) => value == null ? null : RenderEngineeringValue != null ? RenderEngineeringValue(value)
            : DefaultLibraryValue(value);
        private static object? DefaultLibraryValue(object value)
            => HardwareScalarEvidence.Scalar(value.GetType()) ? HardwareAddressingPolicy.ScalarValue(value) : HardwareOfficialService
                .Result(value)["nativeResult"];
        private static List<object?> LibraryList(IEnumerable<string> values) => values.Cast<object?>().ToList();
        private static object? FindExistingByName(object collection, string name)
        {
            try
            {
                if (collection is IEnumerable sequence)
                    foreach (var item in sequence)
                    {
                        var found = TryGetName(item);
                        if (!string.IsNullOrWhiteSpace(found) && string.Equals(found!.Trim(), name, StringComparison.OrdinalIgnoreCase)) return item;
                    }
            }
            catch /* swallow(enumerate-optional): unavailable collection enumeration preserves the missing-item fallback */ { }
            return null;
        }
        private static void LibraryPage(object[] rows, int offset, int limit, Dictionary<string, object?> meta)
            => meta["records"] = EngineeringEvidence.Page(rows.Cast<object?>().ToList(), offset, limit, meta);
        private static void LibraryRequireConfirmation(bool confirmed, string parameter, bool dryRun)
        {
            if (!dryRun && !confirmed) throw new ArgumentException(parameter + "=true required for execution.");
        }
        private List<(PropertyInfo Property, object? Value)> LibraryPrepare(Type type, Dictionary<string, HardwareScalar> changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            LibraryPolicy
                .GuardKnownTypeWrites(int
                    .Parse(ReleaseKey == "14sp1" ? "14" : ReleaseKey == "15.1" ? "15" : ReleaseKey, System.Globalization.CultureInfo.InvariantCulture), type,
                    changes.Keys);
            var result = new List<(PropertyInfo, object?)>();
            foreach (var change in changes)
            {
                var property = HardwareScalarEvidence.DistinctProperties(type).FirstOrDefault(p => p.Name == change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
                result.Add((property, HardwareAddressingPolicy.ConvertValue(change.Value, property.PropertyType)));
            }
            return result;
        }
        private void LibraryApply(object target, List<(PropertyInfo Property, object? Value)> changes, Dictionary<string, object?> meta)
        {
            LibraryPolicy
                .GuardKnownTypeWrites(int
                    .Parse(ReleaseKey == "14sp1" ? "14" : ReleaseKey == "15.1" ? "15" : ReleaseKey, System.Globalization.CultureInfo.InvariantCulture), target
                    .GetType(), changes.Select(p => p.Property.Name));
            HardwareScalarEvidence.Apply(target, changes, meta);
        }
        private static Dictionary<string, object?> LibraryObjectAddress(object target)
        {
            var result = HardwareScalarEvidence.Read(target);
            var properties = new List<object?>();
            foreach (var p in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0))
                properties.Add(new Dictionary<string, object?> { ["name"] = p.Name, ["type"] = p.PropertyType.FullName,
                    ["readable"] = p.GetMethod?.IsPublic == true, ["writable"] = p.SetMethod?.IsPublic == true,
                    ["backlink"] = new[] { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" }
                        .Contains(p.Name, StringComparer.OrdinalIgnoreCase) });
            result["propertySchema"] = properties;
            return result;
        }
        private static object? MultilingualJson(MultilingualText? text)
        {
            if (text == null) return null;
            var items = new Dictionary<string, object?>();
            foreach (var item in PlcGroupOperations.Items(text.Items).Cast<MultilingualTextItem>()) items[item.Language?.Culture?.Name ?? "?"] = item.Text;
            return items;
        }
        private static string LibraryPathOf(object node, string rootTypeName)
        {
            // Folder path of a type/master copy/folder relative to the library's system folder, by walking Parent names.
            var names = new List<string>(); object? current = (node as IEngineeringInstance)?.Parent;
            for (int hop = 0; hop < 32 && current != null && current.GetType().Name != rootTypeName && current is not ILibrary; hop++)
            {
                names.Insert(0, PlcGroupOperations.Get(current, "Name").ToString()!);
                current = (current as IEngineeringInstance)?.Parent;
            }
            return string.Join("/", names);
        }
        private static LibraryType ExactLibraryType(object library, string typePath)
        {
            var parts = PlcGroupOperations.Parts(typePath);
            var folder = EngineeringLibraryFolder(library, string.Join("/", parts.Take(parts.Length - 1)), "TypeFolder");
            return (LibraryType)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Types"), parts.Last())
                    ?? throw new PlcSoftwareException("NotFound", "Exact library type not found: " + typePath));
        }
        private static LibraryTypeVersion ExactTypeVersion(LibraryType type, string version)
        {
            var matches = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Where(v => v.VersionNumber?.ToString() == version).Take(2)
                .ToArray();
            if (matches.Length != 1)
                throw new PlcSoftwareException("NotFound", "Expected exactly one version '" + version + "' on type '" + type.Name + "', found " + matches
                    .Length + ".");
            return matches[0];
        }
        private MasterCopy ExactMasterCopy(string libraryName,string path) {
            var parts=PlcGroupOperations.Parts(path);
            var folder=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
            return (MasterCopy)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder,"MasterCopies"),parts.Last())
                    ?? throw new PlcSoftwareException("NotFound", "Exact master copy not found."));
        }
        private static object EngineeringLibraryFolder(object library, string folderPath, string rootProperty)
        {
            // SystemGlobalLibrary.TypeFolder is null (observed on TIA V21): say so instead of the generic "unavailable".
            var folder = library.GetType().GetProperty(rootProperty)?.GetValue(library)
                ?? throw new PlcSoftwareException("NotFound", LibraryPolicy
                        .NoTypeFolderMessage(library.GetType().Name, "resolving '" + folderPath + "' under " + rootProperty));
            foreach (var part in PlcGroupOperations.Parts(folderPath, true))
                folder = PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Folders"), part)
                    ?? throw new PlcSoftwareException("NotFound", "Library folder not found: " + part);
            return folder;
        }
        private object ExactMasterCopyPlcSource(string softwarePath, string sourcePath, bool block)
        {
            var plc = EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false);
            var parts = PlcGroupOperations.Parts(sourcePath);
            var root = block ? (object)plc.BlockGroup : plc.TypeGroup;
            var group = PlcGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            return PlcGroupOperations.Find(PlcGroupOperations.Get(group, block ? "Blocks" : "Types"), parts.Last())
                ?? throw new PlcSoftwareException("NotFound", "Exact PLC " + (block ? "block" : "type") + " not found: " + sourcePath + " (path under "
                        + (block ? "Program blocks" : "PLC data types") + ", groups separated by '/', see GetSoftwareTree).");
        }
        private object LibraryInstantiationCollection(string kind, string software, string path, bool writing)
        {
            string[] Parts(string value) => PlcObjectPath.Parts(value, true, "targetGroupPath");
            if (kind == "device")
            {
                var group = HardwareGroupOperations.Group(Project(), path, "DeviceGroups", Parts);
                return group is Project bound ? bound.Devices : ((DeviceUserGroup)group).Devices;
            }
            if (kind == "classicScreen" || kind == "classicTagTable")
            {
                var resolved = ResolveEngineeringHmiSoftware != null ? ResolveEngineeringHmiSoftware(software)
                    : DefaultHmiSoftware(EngineeringPlcSession, software);
                var hmi = resolved as HmiTarget
                    ?? throw new NotSupportedException("targetSoftwarePath must resolve to a Classic WinCC HmiTarget; Unified targets are unsupported.");
                var folders = EngineeringInputValidation.HmiFolderParts(path);
                return kind == "classicScreen" ? (object)ClassicHmiFoldersAdapter.ExactScreenFolder(hmi, folders).Screens
                    : ClassicHmiFoldersAdapter.ExactTagFolder(hmi, folders).TagTables;
            }
            var plc = EngineeringPlcSession.ExactPlcForEngineering(software, writing);
            if (kind == "block" || kind == "blockGroup")
            {
                var group = (PlcBlockGroup)PlcGroupOperations.Group(plc.BlockGroup, path, pathParts: Parts);
                return kind == "block" ? (object)group.Blocks : group.Groups;
            }
            if (kind == "tagTable")
                return ((PlcTagTableGroup)PlcGroupOperations.Group(plc.TagTableGroup, path, pathParts: Parts)).TagTables;
            var names = PlcObjectPath.Parts(path, parameter: "targetGroupPath");
            var parent = (PlcTagTableGroup)PlcGroupOperations.Group(plc.TagTableGroup, PlcObjectPath.Format(names.Take(names.Length - 1)), pathParts: Parts);
            return (parent.TagTables.Find(names.Last())
                ?? throw new PlcSoftwareException("NotFound", "Exact destination PLC tag table not found: " + path)).Tags;
        }

        private static Type LibraryInstantiationContentType(string targetKind) => targetKind switch {
            "block" => typeof(PlcBlock), "blockGroup" => typeof(PlcBlockUserGroup), "tagTable" => typeof(PlcTagTable), "tag" => typeof(PlcTag),
            "device" => typeof(Device), "classicScreen" => typeof(Screen), "classicTagTable" => typeof(TagTable),
            _ => throw new ArgumentException("Unsupported targetKind.", nameof(targetKind))
        };

        private static object LibraryCreateFromMasterCopy(object collection, MasterCopy source, string mode)
        {
#if LIBRARY_V20
            if (mode != "")
            {
                var copyMode = (MasterCopyMode)Enum.Parse(typeof(MasterCopyMode), mode);
                switch (collection)
                {
                    case PlcBlockComposition blocks: return blocks.CreateFrom(source, copyMode);
                    case PlcBlockUserGroupComposition groups: return groups.CreateFrom(source, copyMode);
                    case PlcTagTableComposition tables: return tables.CreateFrom(source, copyMode);
                    case PlcTagComposition tags: return tags.CreateFrom(source, copyMode);
                }
            }
#endif
            switch (collection)
            {
                case PlcBlockComposition blocks: return blocks.CreateFrom(source);
                case PlcBlockUserGroupComposition groups: return groups.CreateFrom(source);
                case PlcTagTableComposition tables: return tables.CreateFrom(source);
                case PlcTagComposition tags: return tags.CreateFrom(source);
                case DeviceComposition devices: return devices.CreateFrom(source);
                case ScreenComposition screens: return screens.CreateFrom(source);
                case TagTableComposition tables: return tables.CreateFrom(source);
                default: throw new NotSupportedException("Unsupported library instantiation composition.");
            }
        }

        private string InstantiateLibraryMasterCopy(MasterCopy source, string kind, string software, string groupPath, string copyMode, bool dryRun,
            Dictionary<string, object?> meta)
        {
            LibraryPolicy.ValidateInstantiationTarget(kind, software, groupPath, copyMode);
            var contents = source.ContentDescriptions.Select(content => (content.ContentName, content.ContentType)).ToArray();
            meta["contents"] = contents.Select(content => (object?)new Dictionary<string, object?> {
                ["name"] = content.ContentName, ["type"] = content.ContentType.FullName }).ToList();
            if (contents.Length == 0 || contents.Any(content => !LibraryInstantiationContentType(kind).IsAssignableFrom(content.ContentType)))
                throw new NotSupportedException("Master-copy content types do not match targetKind=" + kind + "; no CreateFrom was called.");
            return InstantiateLibraryContents(kind, software, groupPath, contents.Select(content => content.ContentName).ToArray(), copyMode, dryRun, meta,
                collection => LibraryCreateFromMasterCopy(collection, source, copyMode));
        }

        private string InstantiateLibraryVersion(LibraryType type, LibraryTypeVersion source, string software, string groupPath, string updatePathsMode,
            bool dryRun, Dictionary<string, object?> meta)
        {
            string kind = LibraryPolicy.InstantiationVersionKind(source.GetType().Name, updatePathsMode);
            LibraryPolicy.ValidateInstantiationTarget(kind, software, groupPath, "");
            meta["libraryTypeVersion"] = new Dictionary<string, object?> {
                ["type"] = type.Name, ["version"] = source.VersionNumber.ToString(), ["state"] = source.State.ToString(),
                ["versionClass"] = source.GetType().Name };
            meta["updatePathsMode"] = updatePathsMode;
            if (source.State.ToString() != "Released")
                throw new NotSupportedException("Instantiation requires a released library type version.");
            return InstantiateLibraryContents(kind, software, groupPath, new[] { type.Name }, "", dryRun, meta, collection => {
                if (source is CodeBlockLibraryTypeVersion code)
                {
                    var blocks = (PlcBlockComposition)collection;
#if LIBRARY_V20
                    if (updatePathsMode != "") return blocks.CreateFrom(code, (UpdatePathsMode)Enum.Parse(typeof(UpdatePathsMode), updatePathsMode));
#endif
                    return blocks.CreateFrom(code);
                }
                if (source is ScreenLibraryTypeVersion screen) return ((ScreenComposition)collection).CreateFrom(screen);
                throw new NotSupportedException("Unsupported library type version.");
            });
        }

        private string InstantiateLibraryContents(string kind, string software, string groupPath, string[] names, string copyMode, bool dryRun,
            Dictionary<string, object?> meta, Func<object, object> create)
        {
            object Collection(bool writing = false) => LibraryInstantiationCollection(kind, software, groupPath, writing);
            string Name(object item) => PlcGroupOperations.Get(item, "Name").ToString()!;
            string Apply(bool writing)
            {
                using var access = writing ? HardwareEditAccess() : null;
                var collection = Collection(writing);
                var before = new HashSet<string>(PlcGroupOperations.Items(collection).Select(Name), StringComparer.OrdinalIgnoreCase);
                meta["targetKind"] = kind; meta["targetSoftwarePath"] = software; meta["targetGroupPath"] = groupPath; meta["copyMode"] = copyMode;
                meta["targets"] = names.Select(name => (object?)new Dictionary<string, object?> {
                    ["name"] = name, ["path"] = groupPath + (groupPath == "" ? "" : "/") + ObjectPathEncoding.EncodeSegment(name),
                    ["exists"] = before.Contains(name) }).ToList();
                if ((copyMode == "" || copyMode == "ThrowIfExists") && names.Any(before.Contains))
                    throw new PlcSoftwareException("ALREADY_EXISTS", "A destination content name already exists; creation refused.");
                if (!writing) return "Library instantiation preview; source and destination resolved; no changes.";
                string[] createdNames = Array.Empty<string>();
                bool observed = false;
                for (int index = 0; index < names.Length; index++)
                {
                    string name = names[index];
                    int slot = index;
                    // Bind renamed/partial results while edit access is still held, including a throwing CreateFrom.
                    object? FindCreated()
                    {
                        if (!observed) throw new NotSupportedException("Created names could not be observed; inspect the target before cleanup.");
                        return slot >= createdNames.Length ? null : PlcGroupOperations.Find(Collection(), createdNames[slot]);
                    }
                    EngineeringWriteJournal.Creating(FindCreated, groupPath + "/" + ObjectPathEncoding.EncodeSegment(name));
                    if (copyMode == "Replace" && before.Contains(name))
                        EngineeringWriteJournal.Unrestored("Restore replaced library instance " + ObjectPathEncoding.EncodeSegment(name));
                }
                meta["mayHaveChanged"] = true;
                meta["instantiationAttempted"] = true;
                try
                {
                    var created = create(collection); meta["apiCallSuccess"] = true;
                    string createdName = Name(created);
                    if (copyMode == "Replace" && before.Contains(createdName))
                        EngineeringWriteJournal.Created(created, groupPath + "/" + ObjectPathEncoding.EncodeSegment(createdName),
                            () => PlcGroupOperations.Find(Collection(), createdName) == null);
                }
                finally
                {
                    createdNames = PlcGroupOperations.Items(Collection()).Select(Name).Where(name => !before.Contains(name)).ToArray();
                    observed = true;
                    meta["createdNames"] = createdNames.Cast<object?>().ToList();
                }
                var expected = copyMode == "Rename" ? createdNames : names;
                var readback = new List<object?>(); meta["createdObjects"] = readback;
                foreach (string name in expected)
                {
                    var found = PlcGroupOperations.Find(Collection(), name);
                    if (found != null) readback.Add(HardwareScalarEvidence.Read(found));
                }
                bool complete = expected.Length == names.Length && readback.Count == names.Length;
                meta["readbackComplete"] = complete; meta["operationSuccess"] = complete;
                if (!complete) meta["failed"] = Math.Max(1, names.Length - readback.Count);
                return complete ? "Library contents instantiated and read back; project not saved or compiled."
                    : "Library instantiation readback failed; rolling back created objects. Inspect createdNames and rollback evidence.";
            }
            return dryRun ? Apply(false) : EngineeringWriteJournal.Run(meta, () => Apply(true), () => EngineeringPlcSession.AcquireHmiEditAccess());
        }

        private static IEnumerable<string> LibraryInterestingAttributes(object target)
        {
            var result = new List<string>();
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods
                .FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (getInfos == null || getAttr == null)
                return result;

            var infos = getInfos.Invoke(target, Array.Empty<object>()) as IEnumerable;
            if (infos == null)
                return result;

            var interesting = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address", "partner", "node"
            };

            foreach (var info in infos)
            {
                var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var lower = name.ToLowerInvariant();
                if (!interesting.Any(k => lower.Contains(k)))
                    continue;

                try
                {
                    var value = getAttr.Invoke(target, new object[] { name });
                    result.Add($"{name}={value ?? ""}");
                }
                catch /* swallow(probe-optional): unreadable attributes are omitted from the diagnostic summary */ { }
            }

            return result;
        }



    }
}
