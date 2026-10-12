using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

/// <summary>The local bridge operations used by the workbench, injectable for offline UI tests.</summary>
public interface IStudioClient : IDisposable
{
    public bool IsRunning { get; }
    public bool IsMock { get; }
    public event EventHandler<BridgeLogEventArgs> Log;
    public event EventHandler<ProgressEventArgs> Progress;
    public event EventHandler Exited;
    public void Start(bool forceMock, string opennessVersion);
    public Task<SessionState> ConnectAsync(bool withUserInterface);
    public Task<SessionState> DisconnectAsync();
    public Task<ProjectInfo> OpenProjectAsync(string path);
    public Task SaveProjectAsync();
    public Task<List<DeviceInfo>> ListDevicesAsync();
    public Task<List<BlockInfo>> ListBlocksAsync(string deviceId);
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format);
    public Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite);
    public Task<CompileResult> CompileAsync(string deviceId);
    public Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern);
    public Task<bool> VcSupportedAsync();
    public Task<List<WorkspaceInfo>> VcListWorkspacesAsync();
    public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath);
    public Task<MappingResult> VcMapProjectAsync(string? workspaceName, string? deviceId, bool dryRun);
    public Task<WorkspaceStatusReport> VcStatusAsync(string? workspaceName, bool changedOnly);
    public Task<WorkspaceDiff> VcDiffAsync(string? workspaceName, string? file);
    public Task<SyncResult> VcSyncAsync(string? workspaceName, SyncDirection direction, bool dryRun);
}
