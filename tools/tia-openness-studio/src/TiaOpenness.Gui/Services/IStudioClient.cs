using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

/// <summary>The local bridge operations used by the workbench, injectable for offline UI tests.</summary>
public interface IStudioClient : IDisposable
{
    bool IsRunning { get; }
    bool IsMock { get; }
    event EventHandler<BridgeLogEventArgs> Log;
    event EventHandler<ProgressEventArgs> Progress;
    event EventHandler Exited;
    void Start(bool forceMock, string opennessVersion);
    Task<SessionState> ConnectAsync(bool withUserInterface);
    Task<ProjectInfo> OpenProjectAsync(string path);
    Task SaveProjectAsync();
    Task<List<DeviceInfo>> ListDevicesAsync();
    Task<List<BlockInfo>> ListBlocksAsync(string deviceId);
    Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format);
    Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite);
    Task<CompileResult> CompileAsync(string deviceId);
    Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern);
    Task<bool> VcSupportedAsync();
    Task<List<WorkspaceInfo>> VcListWorkspacesAsync();
    Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath);
    Task<MappingResult> VcMapProjectAsync(string? workspaceName, string? deviceId, bool dryRun);
    Task<WorkspaceStatusReport> VcStatusAsync(string? workspaceName, bool changedOnly);
    Task<WorkspaceDiff> VcDiffAsync(string? workspaceName, string? file);
    Task<SyncResult> VcSyncAsync(string? workspaceName, SyncDirection direction, bool dryRun);
}
