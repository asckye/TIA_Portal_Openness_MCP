#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Studio;
using TiaMcp.Versioning;
using TiaMcpServer.Siemens;
using TiaOpenness.Openness;

namespace TiaMcp.Adapters
{
    // Opt-in Studio profile. Construction is managed only; Connect is the first operation
    // that can attach/start TIA. Foundation workers continue to use OpennessAdapter.
    public sealed class StudioAdapter : IOpennessAdapter, IStudioSession, IHardware, IHmiExport
    {
        private readonly StudioThreadGuard guard;
        private readonly OpennessSession session;

        public StudioAdapter()
        {
            guard = new StudioThreadGuard();
            ReleaseKey = CompiledReleaseKey();
            ApiIdentity = OpennessReleaseContract.For(ReleaseKey).CoreAssemblyIdentity.FullName;
            session = new OpennessSession(TiaVersionCatalog.Get(ReleaseKey).ApiVersion);
        }

        private static string CompiledReleaseKey()
        {
            foreach (AssemblyMetadataAttribute attribute in typeof(StudioAdapter).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                if (attribute.Key == "TiaReleaseKey") return attribute.Value;
            throw new InvalidOperationException("The adapter has no compiled release key.");
        }

        public string ReleaseKey { get; }
        public string ApiIdentity { get; }
        public AdapterCapabilities Capabilities => AdapterCapabilities.StudioSession | AdapterCapabilities.Hardware
            | AdapterCapabilities.HmiExport | AdapterCapabilities.GenerateSource
#if STUDIO_VCI
            | AdapterCapabilities.VersionControl
#endif
#if STUDIO_VCI_INITIAL
            | AdapterCapabilities.VersionControlInitial
#endif
#if STUDIO_VCI_MODERN
            | AdapterCapabilities.VersionControlModern
#endif
            ;
        public IPortalSession PortalSession => null;
        public IPlcProgram PlcProgram => null;
        public IPlcData PlcData => null;
        public IStudioSession StudioSession => this;
        public IHardware Hardware => this;
        public IHmiExport HmiExport => this;
        // The flag describes compiled support; the nullable facet describes the open project.
        public IVersionControl VersionControl => guard.Run(() =>
        {
            var versionControl = session.VersionControl;
            return versionControl == null ? null : new VersionControlFacet(guard, versionControl);
        });

        public SessionMode Mode => guard.Run(() => session.Mode);
        public bool IsConnected => guard.Run(() => session.IsConnected);
        public bool HasProject => guard.Run(() => session.HasProject);
        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version) =>
            guard.Run(() => session.Connect(withUserInterface, attachToRunning, version));
        public void Disconnect() => guard.Run(session.Disconnect);
        public SessionState GetState() => guard.Run(session.GetState);
        public ProjectInfo OpenProject(string path) => guard.Run(() => session.OpenProject(path));
        public ProjectInfo GetProjectInfo() => guard.Run(session.GetProjectInfo);
        public void SaveProject() => guard.Run(session.SaveProject);
        public void CloseProject() => guard.Run(session.CloseProject);
        public string FindPlcDeviceId(string deviceId) => guard.Run(() => session.FindPlcDeviceId(deviceId));
        public IReadOnlyList<DeviceInfo> ListDevices() => guard.Run(session.ListDevices);
        public IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks) =>
            guard.Run(() => session.ListBlocks(deviceId, includeSystemBlocks));
        public ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress) =>
            guard.Run(() => session.ExportBlocks(deviceId, blockPaths, outputDirectory, format, preserveFolders, progress));
        public ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite, ProgressCallback progress) =>
            guard.Run(() => session.ImportBlocks(deviceId, files, overwrite, progress));
        public IReadOnlyList<TagTableInfo> ListTagTables(string deviceId) => guard.Run(() => session.ListTagTables(deviceId));
        public IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName) => guard.Run(() => session.ListTags(deviceId, tableName));
        public CompileResult CompileDevice(string deviceId, bool softwareOnly) => guard.Run(() => session.CompileDevice(deviceId, softwareOnly));
        public IReadOnlyList<BlockInfo> ListItems(string deviceId) => guard.Run(() => session.ListBlocks(deviceId, false));
        public ExportResult ExportItems(string deviceId, IReadOnlyList<string> paths, string outputDirectory,
            bool preserveFolders, ProgressCallback progress) =>
            guard.Run(() => session.ExportBlocks(deviceId, paths, outputDirectory, ExportFormat.SimaticMl, preserveFolders, progress));
        public void Dispose() => guard.Run(session.Dispose);

        private sealed class VersionControlFacet : IVersionControl
        {
            private readonly StudioThreadGuard guard;
            private readonly IVersionControl versionControl;
            internal VersionControlFacet(StudioThreadGuard guard, IVersionControl versionControl)
            {
                this.guard = guard;
                this.versionControl = versionControl;
            }
            public IReadOnlyList<WorkspaceInfo> ListWorkspaces() => guard.Run(versionControl.ListWorkspaces);
            public WorkspaceInfo CreateWorkspace(string name, string folderPath) => guard.Run(() => versionControl.CreateWorkspace(name, folderPath));
            public MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress) =>
                guard.Run(() => versionControl.MapProject(workspaceName, deviceFilter, dryRun, progress));
            public WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly) => guard.Run(() => versionControl.GetStatus(workspaceName, changedOnly));
            public SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress) =>
                guard.Run(() => versionControl.Sync(workspaceName, direction, dryRun, progress));
        }
    }
}
