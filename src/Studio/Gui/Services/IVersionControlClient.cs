using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

public interface IVersionControlClient : IDisposable
{
    bool CanRead { get; }
    bool CanWrite { get; }
    Task<bool> VcSupportedAsync();
    Task<List<WorkspaceInfo>> VcListWorkspacesAsync();
    Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath);
    Task<MappingResult> VcMapProjectAsync(string? workspaceName, string? deviceId, bool dryRun);
    Task<WorkspaceStatusReport> VcStatusAsync(string? workspaceName, bool changedOnly);
    Task<WorkspaceDiff> VcDiffAsync(string? workspaceName, string? file);
    Task<SyncResult> VcSyncAsync(string? workspaceName, SyncDirection direction, bool dryRun);
}

internal sealed class LegacyVersionControlClient(IStudioClient client, Action ensureBridge, bool enabled) : IVersionControlClient
{
    public bool CanRead => enabled;
    public bool CanWrite => enabled;
    public Task<bool> VcSupportedAsync() { ensureBridge(); return client.VcSupportedAsync(); }
    public Task<List<WorkspaceInfo>> VcListWorkspacesAsync() => client.VcListWorkspacesAsync();
    public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath) => client.VcCreateWorkspaceAsync(name, folderPath);
    public Task<MappingResult> VcMapProjectAsync(string? name, string? deviceId, bool dryRun) => client.VcMapProjectAsync(name, deviceId, dryRun);
    public Task<WorkspaceStatusReport> VcStatusAsync(string? name, bool changedOnly) => client.VcStatusAsync(name, changedOnly);
    public Task<WorkspaceDiff> VcDiffAsync(string? name, string? file) => client.VcDiffAsync(name, file);
    public Task<SyncResult> VcSyncAsync(string? name, SyncDirection direction, bool dryRun) => client.VcSyncAsync(name, direction, dryRun);
    public void Dispose() { }
}
