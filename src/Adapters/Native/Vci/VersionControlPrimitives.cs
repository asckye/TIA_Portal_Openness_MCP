#nullable disable
#if STUDIO_VCI
using System.Collections.Generic;
using System;
using TiaMcp.Adapters.Native.Vci;
using TiaMcp.Adapters.Contracts;
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
#endif

namespace TiaOpenness.Openness
{
#if STUDIO_VCI
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

        public static void ExportObject(IEngineeringObject target, FileInfo file, ExportOptions options)
        {
            switch (target)
            {
                case PlcBlock block: block.Export(file, options); break;
                case PlcType type: type.Export(file, options); break;
                case PlcTagTable table: table.Export(file, options); break;
                default: throw new System.NotSupportedException("VCI XML export requires a PLC block, type or tag table.");
            }
        }

#if !STUDIO_VCI_MODERN
        internal static FileInfo MappingFile(string root, MappedObject mapped) =>
            TiaMcp.Adapters.Contracts.VciWorkspaceLayout.LegacyMappingFile(root, mapped.RelativeWorkspacePath);

        internal static FileInfo TryMappingFile(string root, MappedObject mapped)
        {
            try { return MappingFile(root, mapped); }
            catch (Exception error) when (Ordinary(error)) { return null; }
        }

        internal static bool Ordinary(Exception error) => NativeRefusalProbe.AllowsRefusalProof(error) && !DisposedStatus(error);
#else
        internal static bool Ordinary(Exception error) => NativeRefusalProbe.AllowsRefusalProof(error);
#endif

        internal static Action<FileInfo, bool> Exporter(IEngineeringObject target) =>
            (file, defaults) => ExportObject(target, file, defaults ? ExportOptions.WithDefaults : ExportOptions.None);

        internal static bool BlockIsInconsistent(IEngineeringObject target)
        {
            if (target is PlcBlock block)
            {
                try { return !block.IsConsistent; }
                catch (Exception error) when (Ordinary(error))
                { /* swallow(probe-optional): keep the comparison failure when consistency cannot be read. */ }
            }
            return false;
        }

        internal static string ComparisonDiagnostic(bool blockInconsistent, string message, bool exportSucceeded, out bool inconsistent)
        {
            inconsistent = blockInconsistent && !exportSucceeded;
            return inconsistent ? message + " Recompile the block, then retry the comparison export." : message;
        }

        internal static bool MatchesExisting(string root, FileInfo file, IEngineeringObject target, out string difference)
        {
            var proof = new VciContentProof(root, file);
            proof.Compare(() => Exporter(target), Ordinary);
            if (!proof.Matches && proof.ExportFailure != null)
                throw new IOException(VciExportPolicy.ExistingFile(file.Name) + " "
                    + ComparisonDiagnostic(BlockIsInconsistent(target), proof.ExportFailure, proof.ExportSucceeded, out _));
            difference = proof.Verification == "no-match" ? " The workspace XML content differs from the project export." : "";
            return proof.Matches && proof.Sha256 == proof.CurrentHash();
        }

#if !STUDIO_VCI_INITIAL
        // Keep a proven XML file read-locked through connection. Other formats can have companion files;
        // their existing contents cannot be established by the official SimaticML comparison.
        internal static FileStream OpenConnectTarget(string root, string stem, string format, IEngineeringObject target)
        {
            var xml = VciWorkspaceLayout.LegacyMappingFile(root, stem + ".xml");
            TiaMcp.Adapters.Contracts.Candidates.CandidateImportFiles.SafePath(xml.Directory);
            bool isXml = VciExportPolicy.IsXml(format);
            if (!isXml || target is not (PlcBlock or PlcType or PlcTagTable))
            {
                foreach (var entry in xml.Directory.EnumerateFileSystemInfos(stem + "*"))
                {
                    if (VciExportPolicy.MayBeExportFile(entry.Name, stem, format)) throw new IOException(VciExportPolicy.ExistingFile(entry.Name));
                }
                return null;
            }
            FileStream lease;
            try
            {
                if (!VciExportPolicy.TargetExists(xml)) return null;
                lease = xml.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Cannot verify workspace target " + xml.Name + ". " + VersionControlResult.Reconnect, error);
            }
            try
            {
                if (!MatchesExisting(root, xml, target, out string difference))
                    throw new IOException(VciExportPolicy.ExistingFile(xml.Name) + difference);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
#endif

#if STUDIO_VCI_INITIAL
        internal static bool RepairMissingFile(string root, MappedObject mapped, System.Action<FileInfo> written, Action issued = null)
        {
            var file = TryMappingFile(root, mapped);
            if (VciExportPolicy.FileExists(file) != false) return false;
            var target = mapped.LinkedProjectObject;
            issued?.Invoke();
            try { ExportObject(target, file, ExportOptions.None); }
            catch
            {
                if (File.Exists(file.FullName)) written(file);
                throw;
            }
            written(file);
            return NewMappingState(mapped) == CompareState.Equal;
        }

        private static CompareState NewMappingState(MappedObject mapping)
        {
            try { return State(ReadStatus(mapping)); }
            catch (Exception error) when (Ordinary(error)) { return CompareState.Unknown; }
        }

        internal static void SynchronizeNewMapping(MappedObject mapping)
        {
            var state = NewMappingState(mapping);
            if (state == CompareState.Equal) return;
            if (state != CompareState.Unknown && state != CompareState.Unequal)
                throw new System.InvalidOperationException("New mapping has comparison state " + state + ".");
            Synchronize(mapping, SynchronizationMode.ProjectToWorkspace);
        }
#endif

        public static IndividualObjectCompareResult ReadStatus(MappedObject mapped) => ReadStatus(mapped, out _);

        public static IndividualObjectCompareResult ReadStatus(MappedObject mapped, out string refreshMessage)
        {
            refreshMessage = null;
#if STUDIO_VCI_MODERN
            return mapped.GetStatus();
#else
            var service = mapped.GetService<IndividualObjectSynchronizationStatus>();
            try { service.UpdateStatus(); }
            catch (EngineeringException refreshError) when (TiaMcp.Adapters.Contracts.NativeRefusalProbe.AllowsRefusalProof(refreshError)
                && !DisposedStatus(refreshError))
            {
                // V17–V19 can refuse the refresh when the mapping already holds a known state.
                try
                {
                    var stored = service.GetStatus();
                    if (stored.CompareState != CompareState.Unknown)
                    {
                        refreshMessage = TiaMcp.Adapters.Contracts.NativeFailureText.BoundedDiagnostic(refreshError);
                        return stored;
                    }
                }
                catch (System.Exception readError) when (TiaMcp.Adapters.Contracts.NativeRefusalProbe.AllowsRefusalProof(readError)
                    && !DisposedStatus(readError))
                { /* swallow(probe-optional): An ordinary failed read retains the original refresh refusal. */ }
                throw;
            }
            return service.GetStatus();
#endif
        }

        internal const string DisconnectedMapping = "mapping no longer connected to an existing object";
#if !STUDIO_VCI_MODERN
        internal static bool DisposedStatus(System.Exception error)
        {
            if (error.InnerException != null && TiaMcp.Adapters.Contracts.NativeRefusalProbe.UncertainConnection(error.InnerException)) return false;
            const string disposed = "Access to a disposed object of type 'Siemens.Engineering.VersionControl.IndividualObjectSynchronizationStatus'";
            return error is EngineeringException && (error is EngineeringObjectDisposedException
                || TiaMcp.Adapters.Contracts.NativeRefusalProbe.AllowsRefusalProof(error))
                && error.Message.IndexOf(disposed, System.StringComparison.Ordinal) >= 0;
        }
#endif

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
        public static MappedObject Export(Workspace workspace, IEngineeringObject target, DirectoryInfo directory, string name, string format) => workspace
            .ExportObject(target, directory, name, format);
        internal static MappedObject ConnectExisting(Workspace workspace, IEngineeringObject target, DirectoryInfo directory, string name, string format)
            => workspace.ConnectObject(target, directory, name, format);
        public static object Attribute(IEngineeringObject target, string name) => target.GetAttribute(name);
        public static DeviceComposition Devices(ProjectBase project) => project.Devices;
        public static DeviceUserGroupComposition DeviceGroups(ProjectBase project) => project.DeviceGroups;
        public static DeviceSystemGroup UngroupedDevicesGroup(ProjectBase project) => project.UngroupedDevicesGroup;
        public static DeviceComposition Devices(DeviceGroup group) => group.Devices;
        public static DeviceUserGroupComposition DeviceGroups(DeviceUserGroup group) => group.Groups;
        public static DeviceItemComposition DeviceItems(HardwareObject hardware) => hardware.DeviceItems;
        public static global::Siemens.Engineering.HW.Features.SoftwareContainer SoftwareContainer(DeviceItem hardware) => hardware.GetService<global::Siemens
            .Engineering.HW.Features.SoftwareContainer>();
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
