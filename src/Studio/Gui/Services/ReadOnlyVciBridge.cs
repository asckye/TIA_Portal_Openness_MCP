using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Rpc;

namespace TiaOpenness.Gui.Services;

internal sealed class ReadOnlyVciBridge(IMcpSessionClient session) : IVersionControlClient
{
    private TiaClient? client;
    private string? attached;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private CancellationTokenSource? active;
    private bool disposed;
    internal bool CancelPending()
    {
        lock (sync)
        {
            if (active == null) return false;
            active.Cancel();
            return true;
        }
    }
    public bool CanRead => session.SessionKey != null && (string?)session.State?["releaseKey"] is "16" or "17" or "18" or "19";
    public bool CanWrite => false;
    private string Identity => session.SessionKey + ":" + session.State?["worker"]?["generation"] + ":" + session.State?["binding"];
    private async Task<T> Read<T>(Func<TiaClient, CancellationToken, Task<T>> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        using var read = new CancellationTokenSource();
        try
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ReadOnlyVciBridge));
                active = read;
            }
            try { return await ReadBound(operation, read.Token).ConfigureAwait(false); }
            catch (Exception) when (read.IsCancellationRequested)
            {
                Reset();
                throw new OperationCanceledException(read.Token);
            }
        }
        finally
        {
            lock (sync) { active = null; if (disposed) Reset(); }
            gate.Release();
        }
    }
    private async Task<T> ReadBound<T>(Func<TiaClient, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var state = session.State;
        if (!CanRead || state?["lane"] != null)
            throw new InvalidOperationException("The shared session must be attached and idle for VCI reads.");
        int pid = (int?)state?["binding"]?["processId"] ?? 0;
        string file = (string?)state?["binding"]?["projectFile"] ?? "";
        if (pid <= 0 || file.Length == 0) throw new InvalidOperationException("No shared project/process lease binding.");
        string identity = Identity;
        long ticks;
        using (var process = Process.GetProcessById(pid)) ticks = process.StartTime.ToUniversalTime().Ticks;
        VciReadOnlyBinding.Require(pid, ticks);
        if (attached != identity)
        {
            Reset();
            client = new TiaClient();
            try
            {
                await client.Bridge.StartAsync(opennessVersion: (string)state!["releaseKey"]!, cancellation: token).ConfigureAwait(false);
                if (Identity != identity) throw new InvalidOperationException("The shared session binding changed during the bridge handshake.");
                await client.AttachVciReadOnlyAsync(pid, ticks, file, token).ConfigureAwait(false);
                attached = identity;
            }
            catch { Reset(); throw; }
        }
        var result = await operation(client!, token).ConfigureAwait(false);
        if (Identity != identity) { Reset(); throw new InvalidOperationException("The shared session binding changed during the VCI read."); }
        VciReadOnlyBinding.Require(pid, ticks);
        return result;
    }
    public Task<bool> VcSupportedAsync() => Read((c, token) => c.VcSupportedAsync(token));
    public Task<List<WorkspaceInfo>> VcListWorkspacesAsync() => Read((c, token) => c.VcListWorkspacesAsync(token));
    public Task<WorkspaceStatusReport> VcStatusAsync(string? name, bool changedOnly) => Read((c, token) => c.VcStatusAsync(name, changedOnly, token));
    public Task<WorkspaceDiff> VcDiffAsync(string? name, string? file) => Read((c, token) => c.VcDiffAsync(name, file, token));
    private static InvalidOperationException WritesRefused() => new("The VCI bridge permits reads only. F33 MCP tools are required for writes.");
    public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath) => throw WritesRefused();
    public Task<MappingResult> VcMapProjectAsync(string? name, string? deviceId, bool dryRun) => throw WritesRefused();
    public Task<SyncResult> VcSyncAsync(string? name, SyncDirection direction, bool dryRun) => throw WritesRefused();
    private void Reset() { var previous = client; client = null; attached = null; previous?.Dispose(); }
    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            if (active != null) active.Cancel();
            else Reset();
        }
    }
}
