#nullable disable
#if TIA_ENGINE_LOCAL_PRIMITIVES
#define STUDIO_VCI
#define STUDIO_VCI_MODERN
namespace TiaMcpServer.Siemens.LocalVci
#else
namespace TiaOpenness.Openness
#endif
{
#if STUDIO_VCI
    using System.Collections.Generic;
    using System.IO;
    using global::Siemens.Engineering;
    using global::Siemens.Engineering.HW;
    using global::Siemens.Engineering.SW;
    using global::Siemens.Engineering.SW.Blocks;
    using global::Siemens.Engineering.SW.Tags;
    using global::Siemens.Engineering.SW.Types;
    using global::Siemens.Engineering.VersionControl;
#if !STUDIO_VCI_MODERN
    using MappedObject = global::Siemens.Engineering.VersionControl.WorkspaceMapping;
#endif

    // Raw VCI operations shared by the engine and Studio. This layer has no session,
    // response envelope, retry, exception translation, filtering or thread dispatch.
    public static class VersionControlPrimitives
    {
        public static VersionControlInterface Service(IEngineeringServiceProvider project) => project?.GetService<VersionControlInterface>();
        public static WorkspaceSystemGroup Group(VersionControlInterface service) => service.WorkspaceGroup;
        public static WorkspaceComposition Workspaces(WorkspaceGroup group) => group.Workspaces;
        public static WorkspaceUserGroupComposition Groups(WorkspaceGroup group) => group.Groups;
        public static string Name(Workspace workspace) => workspace.Name;
        public static DirectoryInfo Root(Workspace workspace) => workspace.RootPath;
        public static CompareState State(IndividualObjectCompareResult result) => result.CompareState;
        public static PlcBlockSystemGroup BlockGroup(PlcSoftware software) => software.BlockGroup;
        public static PlcTagTableSystemGroup TagTableGroup(PlcSoftware software) => software.TagTableGroup;
        public static PlcTypeSystemGroup TypeGroup(PlcSoftware software) => software.TypeGroup;
        public static PlcBlockComposition Blocks(PlcBlockGroup group) => group.Blocks;
        public static PlcBlockUserGroupComposition BlockGroups(PlcBlockGroup group) => group.Groups;
        public static PlcTagTableComposition TagTables(PlcTagTableGroup group) => group.TagTables;
        public static PlcTagTableUserGroupComposition TagTableGroups(PlcTagTableGroup group) => group.Groups;
        public static PlcTypeComposition Types(PlcTypeGroup group) => group.Types;
        public static PlcTypeUserGroupComposition TypeGroups(PlcTypeGroup group) => group.Groups;
#if STUDIO_VCI_INITIAL
        public static Workspace Create(WorkspaceComposition workspaces, string name) => workspaces.Create(name);
        public static void SetRoot(Workspace workspace, DirectoryInfo directory) => workspace.RootPath = directory;
#else
        public static Workspace Create(WorkspaceComposition workspaces, string name, DirectoryInfo directory) => workspaces.Create(name, directory);
#endif

        public static IndividualObjectCompareResult ReadStatus(MappedObject mapped)
        {
#if STUDIO_VCI_MODERN
            return mapped.GetStatus();
#else
            var service = mapped.GetService<IndividualObjectSynchronizationStatus>();
            service.UpdateStatus();
            return service.GetStatus();
#endif
        }

        public static void Synchronize(MappedObject mapped, SynchronizationMode mode)
        {
#if STUDIO_VCI_MODERN
            mapped.Synchronize(mode);
#else
            mapped.GetService<IndividualObjectSynchronizationStatus>().Synchronize(mode);
#endif
        }

#if STUDIO_VCI_MODERN
        public static WorkspaceSequence Enumerate(WorkspaceComposition source) => new WorkspaceSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct WorkspaceSequence
        {
            private readonly WorkspaceComposition source;
            internal WorkspaceSequence(WorkspaceComposition source) { this.source = source; }
            public IEnumerator<Workspace> GetEnumerator() => source.GetEnumerator();
        }

        public static WorkspaceUserGroupSequence Enumerate(WorkspaceUserGroupComposition source) => new WorkspaceUserGroupSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct WorkspaceUserGroupSequence
        {
            private readonly WorkspaceUserGroupComposition source;
            internal WorkspaceUserGroupSequence(WorkspaceUserGroupComposition source) { this.source = source; }
            public IEnumerator<WorkspaceUserGroup> GetEnumerator() => source.GetEnumerator();
        }
        public static System.Globalization.CultureInfo Language(Workspace workspace) => workspace.WorkspaceLanguage;
        public static MappedObjectComposition MappedObjects(Workspace workspace) => workspace.MappedObjects;
        public static int Count(MappedObjectComposition mappings) => mappings.Count;
        public static string FileName(MappedObject mapped) => mapped.FileNameWithoutExtension;
        public static DirectoryInfo Directory(MappedObject mapped) => mapped.DirectoryPath;
        public static string Format(MappedObject mapped) => mapped.FileFormat;
        public static IEnumerable<string> SupportedFormats(Workspace workspace, IEngineeringObject target) => workspace.GetSupportedFileFormats(target);
        public static MappedObject Find(MappedObjectComposition mappings, IEngineeringObject target) => mappings.Find(target);
        public static MappedObject Export(Workspace workspace, IEngineeringObject target, DirectoryInfo directory, string name, string format) => workspace.ExportObject(target, directory, name, format);
        public static object Attribute(IEngineeringObject target, string name) => target.GetAttribute(name);
        public static DeviceComposition Devices(ProjectBase project) => project.Devices;
        public static DeviceUserGroupComposition DeviceGroups(ProjectBase project) => project.DeviceGroups;
        public static DeviceComposition Devices(DeviceGroup group) => group.Devices;
        public static DeviceUserGroupComposition DeviceGroups(DeviceUserGroup group) => group.Groups;
        public static DeviceItemComposition DeviceItems(HardwareObject hardware) => hardware.DeviceItems;
        public static global::Siemens.Engineering.HW.Features.SoftwareContainer SoftwareContainer(DeviceItem hardware) => hardware.GetService<global::Siemens.Engineering.HW.Features.SoftwareContainer>();
        public static Software Software(global::Siemens.Engineering.HW.Features.SoftwareContainer container) => container?.Software;


        public static MappedObjectSequence Enumerate(MappedObjectComposition source) => new MappedObjectSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct MappedObjectSequence
        {
            private readonly MappedObjectComposition source;
            internal MappedObjectSequence(MappedObjectComposition source) { this.source = source; }
            public IEnumerator<MappedObject> GetEnumerator() => source.GetEnumerator();
        }

        public static DeviceSequence Enumerate(DeviceComposition source) => new DeviceSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct DeviceSequence
        {
            private readonly DeviceComposition source;
            internal DeviceSequence(DeviceComposition source) { this.source = source; }
            public IEnumerator<Device> GetEnumerator() => source.GetEnumerator();
        }

        public static DeviceUserGroupSequence Enumerate(DeviceUserGroupComposition source) => new DeviceUserGroupSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct DeviceUserGroupSequence
        {
            private readonly DeviceUserGroupComposition source;
            internal DeviceUserGroupSequence(DeviceUserGroupComposition source) { this.source = source; }
            public IEnumerator<DeviceUserGroup> GetEnumerator() => source.GetEnumerator();
        }

        public static DeviceItemSequence Enumerate(DeviceItemComposition source) => new DeviceItemSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct DeviceItemSequence
        {
            private readonly DeviceItemComposition source;
            internal DeviceItemSequence(DeviceItemComposition source) { this.source = source; }
            public IEnumerator<DeviceItem> GetEnumerator() => source.GetEnumerator();
        }

        public static PlcBlockSequence Enumerate(PlcBlockComposition source) => new PlcBlockSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct PlcBlockSequence
        {
            private readonly PlcBlockComposition source;
            internal PlcBlockSequence(PlcBlockComposition source) { this.source = source; }
            public IEnumerator<PlcBlock> GetEnumerator() => source.GetEnumerator();
        }

        public static PlcBlockUserGroupSequence Enumerate(PlcBlockUserGroupComposition source) => new PlcBlockUserGroupSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct PlcBlockUserGroupSequence
        {
            private readonly PlcBlockUserGroupComposition source;
            internal PlcBlockUserGroupSequence(PlcBlockUserGroupComposition source) { this.source = source; }
            public IEnumerator<PlcBlockUserGroup> GetEnumerator() => source.GetEnumerator();
        }

        public static PlcTagTableSequence Enumerate(PlcTagTableComposition source) => new PlcTagTableSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct PlcTagTableSequence
        {
            private readonly PlcTagTableComposition source;
            internal PlcTagTableSequence(PlcTagTableComposition source) { this.source = source; }
            public IEnumerator<PlcTagTable> GetEnumerator() => source.GetEnumerator();
        }

        public static PlcTagTableUserGroupSequence Enumerate(PlcTagTableUserGroupComposition source) => new PlcTagTableUserGroupSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct PlcTagTableUserGroupSequence
        {
            private readonly PlcTagTableUserGroupComposition source;
            internal PlcTagTableUserGroupSequence(PlcTagTableUserGroupComposition source) { this.source = source; }
            public IEnumerator<PlcTagTableUserGroup> GetEnumerator() => source.GetEnumerator();
        }

        public static PlcTypeSequence Enumerate(PlcTypeComposition source) => new PlcTypeSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct PlcTypeSequence
        {
            private readonly PlcTypeComposition source;
            internal PlcTypeSequence(PlcTypeComposition source) { this.source = source; }
            public IEnumerator<PlcType> GetEnumerator() => source.GetEnumerator();
        }

        public static PlcTypeUserGroupSequence Enumerate(PlcTypeUserGroupComposition source) => new PlcTypeUserGroupSequence(source);

        // Keep the composition's typed GetEnumerator dispatch; no eager read or buffering.
        public readonly struct PlcTypeUserGroupSequence
        {
            private readonly PlcTypeUserGroupComposition source;
            internal PlcTypeUserGroupSequence(PlcTypeUserGroupComposition source) { this.source = source; }
            public IEnumerator<PlcTypeUserGroup> GetEnumerator() => source.GetEnumerator();
        }
#endif
    }

#endif
}
