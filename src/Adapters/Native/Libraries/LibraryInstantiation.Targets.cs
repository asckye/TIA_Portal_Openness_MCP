using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#endif

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private object LibraryInstantiationCollection(string kind, string software, string path, bool writing)
        {
            string[] Parts(string value) => PlcObjectPath.Parts(value, true, "targetGroupPath");
            if (kind == "device")
            {
                var project = Project();
                var group = HardwareGroupOperations.Group(project, path, "DeviceGroups", Parts);
                return ReferenceEquals(group, project) ? project.Devices : ((DeviceUserGroup)group).Devices;
            }
            if (kind == "classicScreen" || kind == "classicTagTable")
            {
                var resolved = ResolveEngineeringHmiSoftware != null ? ResolveEngineeringHmiSoftware(software)
                    : DefaultHmiSoftware(EngineeringPlcSession, software);
                var hmi = resolved as HmiTarget
                    ?? throw new NotSupportedException("targetSoftwarePath must resolve to a Classic WinCC HmiTarget; Unified targets are unsupported.");
                var folders = LibraryTargetFolderParts(path);
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

        internal string InstantiateLibraryVersion(LibraryType type, LibraryTypeVersion source, string software, string groupPath, string updatePathsMode,
            bool dryRun, Dictionary<string, object?> meta)
        {
            string kind = LibraryPolicy.InstantiationVersionKind(source.GetType().Name, updatePathsMode);
            LibraryPolicy.ValidateInstantiationTarget(kind, software, groupPath, "");
            meta["libraryTypeVersion"] = new Dictionary<string, object?> {
                ["type"] = type.Name, ["version"] = source.VersionNumber.ToString(), ["state"] = source.State.ToString(),
                ["versionClass"] = source.GetType().Name };
            meta["updatePathsMode"] = updatePathsMode;
            RequireCommittedLibraryVersion(source.State);
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
            => InstantiateLibraryContentsCore(kind, software, groupPath, names, copyMode, dryRun, meta, create,
                writing => LibraryInstantiationCollection(kind, software, groupPath, writing),
                () => LibraryInstantiationScope(kind, software, groupPath), HardwareEditAccess, () => EngineeringPlcSession.AcquireHmiEditAccess());

        internal IEnumerable<(object Item, string Name, string Path)> LibraryInstantiationScope(string kind, string software, string groupPath)
        {
            if (kind == "blockGroup")
                return PlcGroupOperations.Items(LibraryInstantiationCollection(kind, software, groupPath, false))
                    .Select(item => {
                        string name = PlcGroupOperations.Get(item, "Name").ToString()!;
                        return (item, name, PlcObjectPath.Format(LibraryTargetFolderParts(groupPath)) + (groupPath == "" ? "" : "/")
                            + ObjectPathEncoding.EncodeSegment(name));
                    });
            if (kind == "device")
            {
                var project = Project();
                return TiaMcp.Adapters.Native.Hardware.ProjectDeviceWalk.Walk(project.Devices, project.DeviceGroups, project.UngroupedDevicesGroup)
                    .Select(row => {
                        string name = row.Device.Name;
                        return ((object)row.Device, name, PlcObjectPath.Format(row.GroupNames.Concat(new[] { name })));
                    });
            }
            if (kind == "classicScreen" || kind == "classicTagTable")
            {
                var hmi = (ResolveEngineeringHmiSoftware != null ? ResolveEngineeringHmiSoftware(software)
                    : DefaultHmiSoftware(EngineeringPlcSession, software)) as HmiTarget
                    ?? throw new NotSupportedException("targetSoftwarePath must resolve to a Classic WinCC HmiTarget.");
                return kind == "classicScreen" ? LibraryScopeObjects(hmi.ScreenFolder, "Screens", "Folders")
                    : LibraryScopeObjects(hmi.TagFolder, "TagTables", "Folders");
            }
            var plc = EngineeringPlcSession.ExactPlcForEngineering(software, false);
            if (kind != "block") return LibraryScopeObjects(plc.TagTableGroup, "TagTables", "Groups", tags: kind == "tag");
            IEnumerable<(object Item, string Name, string Path)> SharedBlocks()
            {
                foreach (var row in LibraryScopeWithSystemObjects(plc.BlockGroup, "Blocks", "SystemBlockGroups", "")) yield return row;
                foreach (var row in LibraryScopeWithSystemObjects(plc.TypeGroup, "Types", "SystemTypeGroups", "$types/")) yield return row;
#if PLC_UNITS
                IEnumerable<(object Unit, string Prefix)> Units()
                {
                    foreach (var unit in PlcUnitScope.Units(plc)) yield return (unit.Value, "$units/" + ObjectPathEncoding.EncodeSegment(unit.Key) + "/");
#if PLC_SAFETY_UNITS
                    var provider = plc.GetService<PlcUnitProvider>();
                    if (provider != null) foreach (var safety in provider.UnitGroup.SafetyUnits)
                    {
                        string name = safety.Name;
                        var unit = PlcUnitScope.FindSafety(provider.UnitGroup, name);
                        if (unit != null) yield return (unit, "$safetyUnits/" + ObjectPathEncoding.EncodeSegment(name) + "/");
                    }
#endif
                }
                foreach (var unit in Units())
                {
#if PLC_SAFETY_UNITS
                    if (!string.IsNullOrEmpty(((PlcUnitBase)unit.Unit).NamespacePreset)) continue;
#endif
                    foreach (var row in LibraryScopeWithSystemObjects(PlcUnitScope.Blocks(plc, unit.Unit), "Blocks", "SystemBlockGroups", unit.Prefix))
                        yield return row;
                    foreach (var row in LibraryScopeWithSystemObjects(PlcUnitScope.Types(plc, unit.Unit), "Types", "SystemTypeGroups",
                        unit.Prefix + "$types/")) yield return row;
                }
#endif
            }
            return SharedBlocks();
        }

    }
}
