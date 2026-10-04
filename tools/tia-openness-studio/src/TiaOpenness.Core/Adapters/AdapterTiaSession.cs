using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;
using A = TiaMcp.Adapters.Contracts.Studio;
using ProgressCallback = TiaOpenness.Core.Abstractions.ProgressCallback;
using IVersionControl = TiaOpenness.Core.Abstractions.IVersionControl;

namespace TiaOpenness.Core.Adapters
{
    // Only managed DTO translation lives here. The adapter owns native policy,
    // synchronous callbacks and STA ownership; no native object crosses this boundary.
    internal sealed class AdapterTiaSession : ITiaSession
    {
        private readonly IOpennessAdapter adapter;
        private readonly IStudioSession session;
        private readonly IHardware hardware;

        internal AdapterTiaSession(IOpennessAdapter adapter)
        {
            this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            session = adapter.StudioSession ?? throw new InvalidOperationException("The adapter has no Studio session surface.");
            hardware = adapter.Hardware ?? throw new InvalidOperationException("The adapter has no Studio hardware surface.");
        }

        public SessionMode Mode => (SessionMode)session.Mode;
        public bool IsConnected => session.IsConnected;
        public bool HasProject => session.HasProject;
        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version) =>
            StudioDtoMap.Map(session.Connect(withUserInterface, attachToRunning, version));
        public void Disconnect() => session.Disconnect();
        public SessionState GetState() => StudioDtoMap.Map(session.GetState());
        public ProjectInfo OpenProject(string path) => StudioDtoMap.Map(session.OpenProject(path));
        public ProjectInfo GetProjectInfo() => StudioDtoMap.Map(session.GetProjectInfo());
        public void SaveProject() => session.SaveProject();
        public void CloseProject() => session.CloseProject();
        public IReadOnlyList<DeviceInfo> ListDevices() => StudioDtoMap.MapList(hardware.ListDevices(), StudioDtoMap.Map);
        public IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks) =>
            StudioDtoMap.MapList(session.ListBlocks(deviceId, includeSystemBlocks), StudioDtoMap.Map);
        public ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress) =>
            StudioDtoMap.Map(session.ExportBlocks(deviceId, blockPaths, outputDirectory, (A.ExportFormat)format, preserveFolders, Forward(progress)));
        public ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite, ProgressCallback progress) =>
            StudioDtoMap.Map(session.ImportBlocks(deviceId, files, overwrite, Forward(progress)));
        public IReadOnlyList<TagTableInfo> ListTagTables(string deviceId) =>
            StudioDtoMap.MapList(session.ListTagTables(deviceId), StudioDtoMap.Map);
        public IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName) =>
            StudioDtoMap.MapList(session.ListTags(deviceId, tableName), StudioDtoMap.Map);
        public CompileResult CompileDevice(string deviceId, bool softwareOnly) => StudioDtoMap.Map(session.CompileDevice(deviceId, softwareOnly));

        public InspectionReport Inspect(string deviceId, InspectionOptions options)
        {
            var plcDeviceId = session.FindPlcDeviceId(deviceId);
            if (plcDeviceId == null)
                return new InspectionReport { TimestampUtc = DateTimeOffset.UtcNow, DeviceId = deviceId };
            var blocks = ListBlocks(deviceId, includeSystemBlocks: false);
            return InspectionEngine.Run(plcDeviceId, blocks, options, referencedNames: null);
        }

        public IVersionControl VersionControl
        {
            get
            {
                var facet = adapter.VersionControl;
                return facet == null ? null : new AdapterVersionControl(facet);
            }
        }

        public void Dispose() => session.Dispose();

        private static A.ProgressCallback Forward(ProgressCallback progress) =>
            progress == null ? (A.ProgressCallback)null : (operation, current, total, message) => progress(operation, current, total, message);

        private sealed class AdapterVersionControl : IVersionControl
        {
            private readonly TiaMcp.Adapters.Contracts.IVersionControl facet;
            internal AdapterVersionControl(TiaMcp.Adapters.Contracts.IVersionControl facet) { this.facet = facet; }
            public IReadOnlyList<WorkspaceInfo> ListWorkspaces() => StudioDtoMap.MapList(facet.ListWorkspaces(), StudioDtoMap.Map);
            public WorkspaceInfo CreateWorkspace(string name, string folderPath) => StudioDtoMap.Map(facet.CreateWorkspace(name, folderPath));
            public MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress) =>
                StudioDtoMap.Map(facet.MapProject(workspaceName, deviceFilter, dryRun, Forward(progress)));
            public WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly) => StudioDtoMap.Map(facet.GetStatus(workspaceName, changedOnly));
            public SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress) =>
                StudioDtoMap.Map(facet.Sync(workspaceName, (A.SyncDirection)direction, dryRun, Forward(progress)));
        }
    }
}
