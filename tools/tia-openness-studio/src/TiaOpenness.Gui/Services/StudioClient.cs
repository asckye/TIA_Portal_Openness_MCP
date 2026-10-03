using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

/// <summary>Preserves the typed client's defaults and event delivery threads.</summary>
public sealed class StudioClient : IStudioClient
{
    private readonly TiaClient _client = new();

    public bool IsRunning => _client.Bridge.IsRunning;
    public bool IsMock => _client.Bridge.IsMock;
    public event EventHandler<BridgeLogEventArgs> Log
    {
        add => _client.Bridge.Log += value;
        remove => _client.Bridge.Log -= value;
    }
    public event EventHandler<ProgressEventArgs> Progress
    {
        add => _client.Bridge.Progress += value;
        remove => _client.Bridge.Progress -= value;
    }
    public event EventHandler Exited
    {
        add => _client.Bridge.Exited += value;
        remove => _client.Bridge.Exited -= value;
    }

    public void Start(bool forceMock, string opennessVersion) => _client.Start(forceMock: forceMock, opennessVersion: opennessVersion);
    public Task<SessionState> ConnectAsync(bool withUserInterface) => _client.ConnectAsync(withUserInterface);
    public Task<ProjectInfo> OpenProjectAsync(string path) => _client.OpenProjectAsync(path);
    public Task SaveProjectAsync() => _client.SaveProjectAsync();
    public Task<List<DeviceInfo>> ListDevicesAsync() => _client.ListDevicesAsync();
    public Task<List<BlockInfo>> ListBlocksAsync(string deviceId) => _client.ListBlocksAsync(deviceId);
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format)
        => _client.ExportBlocksAsync(deviceId, blocks, outputDirectory, format);
    public Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite)
        => _client.ImportBlocksAsync(deviceId, files, overwrite);
    public Task<CompileResult> CompileAsync(string deviceId) => _client.CompileAsync(deviceId);
    public Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern) => _client.InspectAsync(deviceId, blockNamePattern);
    public Task<bool> VcSupportedAsync() => _client.VcSupportedAsync();
    public Task<List<WorkspaceInfo>> VcListWorkspacesAsync() => _client.VcListWorkspacesAsync();
    public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath) => _client.VcCreateWorkspaceAsync(name, folderPath);
    public Task<MappingResult> VcMapProjectAsync(string? workspaceName, string? deviceId, bool dryRun)
        => _client.VcMapProjectAsync(workspaceName, deviceId, dryRun);
    public Task<WorkspaceStatusReport> VcStatusAsync(string? workspaceName, bool changedOnly) => _client.VcStatusAsync(workspaceName, changedOnly);
    public Task<WorkspaceDiff> VcDiffAsync(string? workspaceName, string? file) => _client.VcDiffAsync(workspaceName, file);
    public Task<SyncResult> VcSyncAsync(string? workspaceName, SyncDirection direction, bool dryRun)
        => _client.VcSyncAsync(workspaceName, direction, dryRun);

    public void Dispose() => _client.Dispose();
}
