#if STUDIO_VCI && !STUDIO_VCI_MODERN
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using NativeVci = TiaOpenness.Openness.VersionControlPrimitives;

namespace TiaMcp.Adapters.Native.Vci;

internal static class LegacyVciPortPrimitives
{
    internal static VersionControlInterface? Service(IEngineeringServiceProvider project) => NativeVci.Service(project);
    internal static WorkspaceSystemGroup Group(VersionControlInterface service) => NativeVci.Group(service);
    internal static WorkspaceComposition Workspaces(WorkspaceGroup group) => NativeVci.Workspaces(group);
    internal static WorkspaceUserGroupComposition Groups(WorkspaceGroup group) => NativeVci.Groups(group);
    internal static string Name(Workspace workspace) => NativeVci.Name(workspace);
    internal static DirectoryInfo Root(Workspace workspace) => NativeVci.Root(workspace);
    internal static CompareState State(IndividualObjectCompareResult result) => NativeVci.State(result);
    internal static IndividualObjectCompareResult ReadStatus(WorkspaceMapping mapped) => NativeVci.ReadStatus(mapped);
    internal static void Synchronize(WorkspaceMapping mapped, SynchronizationMode mode) => NativeVci.Synchronize(mapped, mode);
    internal static IEnumerable<T> Enumerate<T>(IEnumerable<T> values) => values;
    internal static WorkspaceMappingComposition MappedObjects(Workspace workspace) => workspace.Mappings;
    internal static int Count(WorkspaceMappingComposition mappings) => mappings.Count;
    internal static string FileName(WorkspaceMapping mapped) => Path.GetFileNameWithoutExtension(mapped.RelativeWorkspacePath);
    internal static DirectoryInfo Directory(WorkspaceMapping mapped)
        => new DirectoryInfo(Path.Combine(((Workspace)mapped.Parent).RootPath.FullName, Path.GetDirectoryName(mapped.RelativeWorkspacePath) ?? ""));
    internal static string Format(WorkspaceMapping mapped) => "xml";
    internal static CultureInfo? Language(Workspace workspace)
    {
#if STUDIO_VCI_INITIAL
        return null;
#else
        return workspace.WorkspaceLanguage;
#endif
    }

    internal static Workspace Create(WorkspaceComposition workspaces, string name, DirectoryInfo directory)
    {
#if STUDIO_VCI_INITIAL
        var workspace = NativeVci.Create(workspaces, name);
        NativeVci.SetRoot(workspace, directory);
        return workspace;
#else
        return NativeVci.Create(workspaces, name, directory);
#endif
    }

    internal static IEnumerable<string> SupportedFormats(Workspace workspace, IEngineeringObject target)
        => target is PlcBlock || target is PlcType || target is PlcTagTable ? new[] { "xml" } : System.Array.Empty<string>();

    internal static WorkspaceMapping? Find(WorkspaceMappingComposition mappings, IEngineeringObject target) => mappings.Find(target);

    internal static WorkspaceMapping Export(Workspace workspace, IEngineeringObject target, DirectoryInfo directory, string name, string format)
    {
        var mapped = workspace.Mappings.Create(name + ".xml", target);
        NativeVci.Synchronize(mapped, SynchronizationMode.ProjectToWorkspace);
        return mapped;
    }

    internal static object Attribute(IEngineeringObject target, string name) => target.GetAttribute(name);
    internal static DeviceComposition Devices(Project project) => project.Devices;
    internal static DeviceUserGroupComposition DeviceGroups(Project project) => project.DeviceGroups;
    internal static DeviceComposition Devices(DeviceGroup group) => group.Devices;
    internal static DeviceUserGroupComposition DeviceGroups(DeviceUserGroup group) => group.Groups;
    internal static DeviceItemComposition DeviceItems(HardwareObject hardware) => hardware.DeviceItems;
    internal static SoftwareContainer SoftwareContainer(DeviceItem hardware) => hardware.GetService<SoftwareContainer>();
    internal static Software? Software(SoftwareContainer? container) => container?.Software;
    internal static PlcBlockSystemGroup BlockGroup(PlcSoftware software) => NativeVci.BlockGroup(software);
    internal static PlcTagTableSystemGroup TagTableGroup(PlcSoftware software) => NativeVci.TagTableGroup(software);
    internal static PlcTypeSystemGroup TypeGroup(PlcSoftware software) => NativeVci.TypeGroup(software);
    internal static PlcBlockComposition Blocks(PlcBlockGroup group) => NativeVci.Blocks(group);
    internal static PlcBlockUserGroupComposition BlockGroups(PlcBlockGroup group) => NativeVci.BlockGroups(group);
    internal static PlcTagTableComposition TagTables(PlcTagTableGroup group) => NativeVci.TagTables(group);
    internal static PlcTagTableUserGroupComposition TagTableGroups(PlcTagTableGroup group) => NativeVci.TagTableGroups(group);
    internal static PlcTypeComposition Types(PlcTypeGroup group) => NativeVci.Types(group);
    internal static PlcTypeUserGroupComposition TypeGroups(PlcTypeGroup group) => NativeVci.TypeGroups(group);
}
#endif
