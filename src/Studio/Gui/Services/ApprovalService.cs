using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

public sealed class ApprovalService : ObservableObject, IApprovalService, IDisposable
{
    private sealed record Pending(PendingApproval Request, TaskCompletionSource<ApprovalDecision> Decision);
    private readonly object sync = new();
    private readonly object saveSync = new();
    private readonly Timer expiry;
    private int pendingCount;
    private ApprovalRequest[] published = [];
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly List<ApprovalRequest> requests = [];
    private readonly Dictionary<string, string> seen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private readonly string settingsPath, pipeName, sid;
    private readonly AuditLog? audit;
    private NamedPipeServerStream? listener;
    private ApprovalSettings settings;
    private bool disposed;

    public ApprovalService() : this(ApprovalSettings.SettingsPath, ApprovalPipe.CurrentName) { }
    internal ApprovalService(string settingsPath, string pipeName, AuditLog? audit = null)
    {
        this.settingsPath = settingsPath; this.pipeName = pipeName; this.audit = audit;
        sid = ApprovalPipe.CurrentSid; settings = ApprovalSettings.Load(settingsPath);
        expiry = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        try { listener = ApprovalPipe.CreateServer(pipeName, sid, true); _ = Listen(); }
        catch (Exception ex) { Trace.TraceWarning("Approval listener unavailable: " + ex.GetType().Name); }
    }
    public bool Enabled
    {
        get { lock (sync) return settings.Enabled; }
        set => Save(value, null);
    }
    public int TimeoutSeconds
    {
        get { lock (sync) return settings.TimeoutSeconds; }
        set => Save(null, value);
    }
    private void Save(bool? enabled, int? seconds)
    {
        lock (saveSync)
        {
            ApprovalSettings next;
            lock (sync)
            {
                next = new ApprovalSettings(enabled ?? settings.Enabled, seconds ?? settings.TimeoutSeconds);
                if (next.Enabled == settings.Enabled && next.TimeoutSeconds == settings.TimeoutSeconds) return;
            }
            next.Save(settingsPath, audit);
            lock (sync) settings = next;
        }
        Raise(enabled.HasValue ? nameof(Enabled) : nameof(TimeoutSeconds));
    }
    public int PendingCount { get { lock (sync) return pendingCount; } }
    public IReadOnlyList<ApprovalRequest> Requests { get { lock (sync) return published; } }
    public event EventHandler<ApprovalRequest>? NewRequest;
    public bool CanDecide(string id) { lock (sync) return pending.TryGetValue(id, out var item) && !item.Decision.Task.IsCompleted && item.Request.Deadline > DateTimeOffset.UtcNow; }
    public bool Approve(string id) => Decide(id, true);
    public bool Deny(string id) => Decide(id, false);
    private bool Decide(string id, bool approve)
    {
        lock (sync)
        {
            if (!CanDecide(id)) return false;
            var item = pending[id];
            if (!item.Decision.TrySetResult(new ApprovalDecision { RequestId = id, PlanHash = item.Request.PlanHash,
                ArgumentDigest = item.Request.ArgumentDigest, Decision = approve ? "granted" : "denied" })) return false;
            SetState(id, approve ? ApprovalState.Approved : ApprovalState.Rejected);
        }
        Changed(); return true;
    }
    private async Task Listen()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var accepted = listener!;
                await accepted.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                // Keep an owned instance alive before handing off this connection.
                lock (sync) { if (disposed) { accepted.Dispose(); return; } listener = ApprovalPipe.CreateServer(pipeName, sid, false); }
                _ = Handle(accepted);
            }
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Trace.TraceWarning("Approval listener stopped: " + ex.GetType().Name); }
    }
    private async Task Handle(NamedPipeServerStream pipe)
    {
        string? id = null;
        using (pipe)
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var item = await ApprovalFrames.Read<PendingApproval>(pipe, limit.Token).ConfigureAwait(false);
                if (!ApprovalPipe.PeerIsCurrentUser(pipe, sid)) throw new UnauthorizedAccessException();
                if (item.Kind == "result")
                {
                    lock (sync)
                    {
                        var original = requests.FirstOrDefault(r => r.Id == item.RequestId);
                        if (original == null || original.PlanHash != item.PlanHash || !seen.TryGetValue(item.RequestId, out var digest)
                            || digest != item.ArgumentDigest || original.State != ApprovalState.Approved) return;
                        SetState(original.Id, item.Outcome switch { "succeeded" => ApprovalState.Completed, "rejected-before-operation" => ApprovalState.Rejected,
                            "failed" or "read-failed" => ApprovalState.Failed, "partial" => ApprovalState.Partial, _ => ApprovalState.Unknown });
                    }
                    Changed(); return;
                }
                if (item.Kind != "request") throw new InvalidDataException("Only host requests are accepted.");
                item.Validate(); id = item.RequestId;
                var decision = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                ApprovalRequest row;
                lock (sync)
                {
                    if (disposed || seen.ContainsKey(id) || pending.Count >= 50) throw new InvalidDataException("Duplicate or excess approval request.");
                    seen.Add(id, item.ArgumentDigest);
                    row = new ApprovalRequest(id, "MCP", "MCP", item.Host, item.ReleaseKey, item.ProjectIdentity, "",
                        item.Operations.Select(op => new ApprovalOperation(LocalizedText.Literal(op.Action), op.Target, op.Tool)).ToArray(), [],
                        item.PlanHash, item.TimeoutSeconds, item.Deadline, ApprovalState.Pending, item.ParametersJson);
                    pending.Add(id, new Pending(item, decision)); requests.Add(row);
                    while (requests.Count > 1024 && requests[0].State != ApprovalState.Pending) requests.RemoveAt(0);
                }
                Changed(); NewRequest?.Invoke(this, row);
                var remaining = item.Deadline - DateTimeOffset.UtcNow;
                limit.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                // Monitor a host disappearing while waiting; no decision can revive it.
                var disconnected = pipe.ReadAsync(new byte[1], 0, 1, limit.Token);
                var done = await Task.WhenAny(decision.Task, disconnected).ConfigureAwait(false);
                if (done != decision.Task) { await disconnected.ConfigureAwait(false); SetStateSafe(id, ApprovalState.Disconnected); return; }
                await ApprovalFrames.Write(pipe, await decision.Task.ConfigureAwait(false), limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) /* swallow(privacy): expired or disconnected approval requests cannot execute */ { if (id != null) SetStateSafe(id, stop.IsCancellationRequested ? ApprovalState.Disconnected : ApprovalState.TimedOut); }
            catch (Exception ex) { if (id != null) SetStateSafe(id, ApprovalState.Disconnected); Trace.TraceInformation("Approval connection ended: " + ex.GetType().Name); }
            finally { if (id != null) { lock (sync) pending.Remove(id); Changed(); } }
        }
    }
    private void SetState(string id, ApprovalState state)
    { int index = requests.FindIndex(r => r.Id == id); if (index >= 0) requests[index] = requests[index] with { State = state }; }
    private void SetStateSafe(string id, ApprovalState state) { lock (sync) SetState(id, state); Changed(); }
    private void Changed()
    {
        bool countChanged, rowsChanged;
        lock (sync)
        {
            rowsChanged = !published.SequenceEqual(requests);
            if (rowsChanged) published = requests.ToArray();
            int count = requests.Count(r => r.State == ApprovalState.Pending);
            countChanged = pendingCount != count; pendingCount = count;
            var deadline = requests.Where(r => r.State == ApprovalState.Approved).Select(r => r.Deadline.AddMinutes(2)).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            if (!disposed) expiry.Change(deadline == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds)), Timeout.InfiniteTimeSpan);
        }
        if (rowsChanged) Raise(nameof(Requests)); if (countChanged) Raise(nameof(PendingCount));
    }
    public void Refresh()
    {
        bool changed = false;
        lock (sync)
        {
            foreach (var row in requests.Where(r => r.State == ApprovalState.Pending && r.Deadline <= DateTimeOffset.UtcNow).ToArray())
            { SetState(row.Id, ApprovalState.TimedOut); changed = true; }
            foreach (var row in requests.Where(r => r.State == ApprovalState.Approved && r.Deadline.AddMinutes(2) <= DateTimeOffset.UtcNow).ToArray())
            { SetState(row.Id, ApprovalState.Unknown); changed = true; }
        }
        if (changed) Changed();
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true; expiry.Dispose(); stop.Cancel(); listener?.Dispose();
            foreach (var row in requests.Where(r => r.State is ApprovalState.Pending or ApprovalState.Approved).ToArray())
                SetState(row.Id, row.State == ApprovalState.Pending ? ApprovalState.Disconnected : ApprovalState.Unknown);
        }
        Changed();
    }
}
