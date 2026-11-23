using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Rpc;

namespace TiaOpenness.Gui.Services;

internal sealed class ReadOnlyVciBridge(IMcpSessionClient session) : IVersionControlClient
{
    private TiaClient? client;
    private Binding? attached;
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
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    internal sealed record Binding(string? Key, string? Release, string Generation, string Lease, bool Busy, int Pid, string Project)
    {
        internal bool Readable => Key != null && Release is "16" or "17" or "18" or "19";
    }

    private Binding Capture()
    {
        dispatcher.VerifyAccess();
        var state = session.State;
        var lease = state?["binding"];
        return new(session.SessionKey, (string?)state?["releaseKey"], state?["worker"]?["generation"]?.ToString() ?? "",
            lease?.ToJsonString() ?? "", state?["lane"] != null, (int?)lease?["processId"] ?? 0, (string?)lease?["projectFile"] ?? "");
    }

    internal Task<Binding> Snapshot(CancellationToken token) =>
        dispatcher.InvokeAsync(Capture, DispatcherPriority.Normal, token).Task;
    public bool CanRead => dispatcher.Invoke(Capture).Readable;
    public bool CanWrite => false;
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
        var identity = await Snapshot(token).ConfigureAwait(false);
        if (!identity.Readable || identity.Busy)
            throw new InvalidOperationException("The shared session must be attached and idle for VCI reads.");
        int pid = identity.Pid;
        string file = identity.Project;
        if (pid <= 0 || file.Length == 0) throw new InvalidOperationException("No shared project/process lease binding.");
        long ticks;
        using (var process = Process.GetProcessById(pid)) ticks = process.StartTime.ToUniversalTime().Ticks;
        VciReadOnlyBinding.Require(pid, ticks);
        if (attached != identity)
        {
            Reset();
            client = new TiaClient();
            try
            {
                await client.Bridge.StartAsync(opennessVersion: identity.Release!, cancellation: token).ConfigureAwait(false);
                if (await Snapshot(token).ConfigureAwait(false) != identity)
                    throw new InvalidOperationException("The shared session binding changed during the bridge handshake.");
                await client.AttachVciReadOnlyAsync(pid, ticks, file, token).ConfigureAwait(false);
                attached = identity;
            }
            catch { Reset(); throw; }
        }
        var result = await operation(client!, token).ConfigureAwait(false);
        if (await Snapshot(token).ConfigureAwait(false) != identity)
        {
            Reset();
            throw new InvalidOperationException("The shared session binding changed during the VCI read.");
        }
        VciReadOnlyBinding.Require(pid, ticks);
        return result;
    }
    public Task<bool> VcSupportedAsync() => Read((c, token) => c.VcSupportedAsync(token));
    public Task<List<WorkspaceInfo>> VcListWorkspacesAsync() => Read((c, token) => c.VcListWorkspacesAsync(token));
    public Task<WorkspaceStatusReport> VcStatusAsync(string? name, bool changedOnly) => Read((c, token) => c.VcStatusAsync(name, changedOnly, token));
    public Task<WorkspaceDiff> VcDiffAsync(string? name, string? file) => Read((c, token) => c.VcDiffAsync(name, file, token));
    private static InvalidOperationException WritesRefused() => new(Localization.Loc.Current["Vc.ReadOnlyBridge"]);
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
