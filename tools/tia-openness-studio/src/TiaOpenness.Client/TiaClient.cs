using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;

namespace TiaOpenness.Client
{
    /// <summary>
    /// Typed wrapper over <see cref="BridgeClient"/>. Every front end (CLI, desktop UI,
    /// MCP server) goes through this, so a change to the wire protocol lands in one place.
    /// </summary>
    public sealed class TiaClient : IDisposable
    {
        private readonly BridgeClient _bridge;
        private readonly bool _ownsBridge;

        public TiaClient(BridgeClient bridge = null)
        {
            _bridge = bridge ?? new BridgeClient();
            _ownsBridge = bridge == null;
        }

        public BridgeClient Bridge { get { return _bridge; } }

        public void Start(string bridgeExePath = null, bool forceMock = false)
        {
            _bridge.Start(bridgeExePath, forceMock);
        }

        public Task<DoctorReport> DoctorAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<DoctorReport>(RpcMethods.DoctorRun, null, ct);
        }

        /// <summary>
        /// Builds the Openness adapter against the TIA Portal installed on this machine - the one
        /// step that cannot be done before shipping, because the Siemens assemblies it compiles
        /// against are not redistributable. The next <see cref="ConnectAsync"/> picks it up.
        /// </summary>
        public Task<AdapterBuildResult> BuildAdapterAsync(string version = null, CancellationToken ct = default)
        {
            return _bridge.CallAsync<AdapterBuildResult>(RpcMethods.OpennessBuild, new { version }, ct);
        }

        public Task<SessionState> ConnectAsync(bool withUserInterface = true, bool attachToRunning = true,
            string version = null, CancellationToken ct = default)
        {
            return _bridge.CallAsync<SessionState>(RpcMethods.SessionConnect,
                new { withUserInterface, attachToRunning, version }, ct);
        }

        public Task<SessionState> DisconnectAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<SessionState>(RpcMethods.SessionDisconnect, null, ct);
        }

        public Task<SessionState> StateAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<SessionState>(RpcMethods.SessionState, null, ct);
        }

        public Task<ProjectInfo> OpenProjectAsync(string path, CancellationToken ct = default)
        {
            return _bridge.CallAsync<ProjectInfo>(RpcMethods.ProjectOpen, new { path }, ct);
        }

        public Task<ProjectInfo> ProjectInfoAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<ProjectInfo>(RpcMethods.ProjectInfo, null, ct);
        }

        public Task SaveProjectAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<object>(RpcMethods.ProjectSave, null, ct);
        }

        public Task CloseProjectAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<object>(RpcMethods.ProjectClose, null, ct);
        }

        public Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<List<DeviceInfo>>(RpcMethods.DeviceList, null, ct);
        }

        public Task<List<BlockInfo>> ListBlocksAsync(string deviceId, bool includeSystemBlocks = false,
            CancellationToken ct = default)
        {
            return _bridge.CallAsync<List<BlockInfo>>(RpcMethods.BlockList,
                new { deviceId, includeSystemBlocks }, ct);
        }

        public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory,
            ExportFormat format = ExportFormat.SimaticMl, bool preserveFolders = true, CancellationToken ct = default)
        {
            return _bridge.CallAsync<ExportResult>(RpcMethods.BlockExport,
                new { deviceId, blocks, outputDirectory, format = format.ToString(), preserveFolders }, ct);
        }

        public Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite = false,
            CancellationToken ct = default)
        {
            return _bridge.CallAsync<ExportResult>(RpcMethods.BlockImport,
                new { deviceId, files, overwrite }, ct);
        }

        public Task<List<TagTableInfo>> ListTagTablesAsync(string deviceId, CancellationToken ct = default)
        {
            return _bridge.CallAsync<List<TagTableInfo>>(RpcMethods.TagTableList, new { deviceId }, ct);
        }

        public Task<List<TagInfo>> ListTagsAsync(string deviceId, string tableName = null, CancellationToken ct = default)
        {
            return _bridge.CallAsync<List<TagInfo>>(RpcMethods.TagList, new { deviceId, tableName }, ct);
        }

        public Task<CompileResult> CompileAsync(string deviceId, bool softwareOnly = true, CancellationToken ct = default)
        {
            return _bridge.CallAsync<CompileResult>(RpcMethods.CompileDevice, new { deviceId, softwareOnly }, ct);
        }

        public Task<InspectionReport> InspectAsync(string deviceId, string blockNamePattern = null,
            bool requireBlockComment = true, bool findUnusedBlocks = true, bool flagInconsistentBlocks = true,
            CancellationToken ct = default)
        {
            return _bridge.CallAsync<InspectionReport>(RpcMethods.InspectProject,
                new { deviceId, blockNamePattern, requireBlockComment, findUnusedBlocks, flagInconsistentBlocks }, ct);
        }

        // ---- Version Control Interface (TIA Portal V21+) -------------------

        /// <summary>True when the open project exposes a Version Control Interface.</summary>
        public async Task<bool> VcSupportedAsync(CancellationToken ct = default)
        {
            var result = await _bridge.CallAsync<VcSupportedResult>(RpcMethods.VcSupported, null, ct)
                .ConfigureAwait(false);
            return result != null && result.Supported;
        }

        public Task<List<WorkspaceInfo>> VcListWorkspacesAsync(CancellationToken ct = default)
        {
            return _bridge.CallAsync<List<WorkspaceInfo>>(RpcMethods.VcWorkspaceList, null, ct);
        }

        public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath, CancellationToken ct = default)
        {
            return _bridge.CallAsync<WorkspaceInfo>(RpcMethods.VcWorkspaceCreate, new { name, folderPath }, ct);
        }

        /// <summary>Maps project objects into a workspace. Defaults to a dry run.</summary>
        public Task<MappingResult> VcMapProjectAsync(string workspaceName = null, string deviceId = null,
            bool dryRun = true, CancellationToken ct = default)
        {
            return _bridge.CallAsync<MappingResult>(RpcMethods.VcMapProject,
                new { workspaceName, deviceId, dryRun }, ct);
        }

        public Task<WorkspaceStatusReport> VcStatusAsync(string workspaceName = null, bool changedOnly = true,
            CancellationToken ct = default)
        {
            return _bridge.CallAsync<WorkspaceStatusReport>(RpcMethods.VcStatus,
                new { workspaceName, changedOnly }, ct);
        }

        /// <summary>
        /// Synchronizes a workspace. Defaults to a dry run because
        /// <see cref="SyncDirection.WorkspaceToProject"/> overwrites blocks in the open project.
        /// </summary>
        /// <summary>
        /// The workspace's uncommitted changes as Git sees them. Meaningful after a push: until
        /// then the project has changed but the files have not.
        /// </summary>
        public Task<WorkspaceDiff> VcDiffAsync(string workspaceName = null, string file = null,
            CancellationToken ct = default)
        {
            return _bridge.CallAsync<WorkspaceDiff>(RpcMethods.VcDiff, new { workspaceName, file }, ct);
        }

        public Task<SyncResult> VcSyncAsync(string workspaceName = null,
            SyncDirection direction = SyncDirection.ProjectToWorkspace, bool dryRun = true,
            CancellationToken ct = default)
        {
            return _bridge.CallAsync<SyncResult>(RpcMethods.VcSync,
                new { workspaceName, direction = direction.ToString(), dryRun }, ct);
        }

        public void Dispose()
        {
            if (_ownsBridge) _bridge.Dispose();
        }

        /// <summary>Shape of the <c>vc.supported</c> reply.</summary>
        private sealed class VcSupportedResult
        {
            public bool Supported { get; set; }
        }
    }
}
