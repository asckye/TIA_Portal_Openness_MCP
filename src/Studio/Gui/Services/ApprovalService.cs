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
        try { listener = ApprovalPipe.CreateServer(pipeName, sid, true); _ = Listen(); }
        catch (Exception ex) { Trace.TraceWarning("Approval listener unavailable: " + ex.GetType().Name); }
    }
    public bool Enabled
    {
        get { lock (sync) return settings.Enabled; }
        set { lock (sync) { if (settings.Enabled == value) return; var next = new ApprovalSettings(value, settings.TimeoutSeconds); next.Save(settingsPath, audit); settings = next; } Raise(nameof(Enabled)); }
    }
    public int TimeoutSeconds
    {
        get { lock (sync) return settings.TimeoutSeconds; }
        set { lock (sync) { if (settings.TimeoutSeconds == value) return; var next = new ApprovalSettings(settings.Enabled, value); next.Save(settingsPath, audit); settings = next; } Raise(nameof(TimeoutSeconds)); }
    }
    public int PendingCount { get { lock (sync) return requests.Count(r => r.State == ApprovalState.Pending); } }
    public IReadOnlyList<ApprovalRequest> Requests { get { lock (sync) return requests.ToArray(); } }
    public event EventHandler<ApprovalRequest>? NewRequest;
    public bool CanDecide(string id) { lock (sync) return pending.ContainsKey(id) && requests.Any(r => r.Id == id && r.State == ApprovalState.Pending && r.Deadline > DateTimeOffset.UtcNow); }
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
    private void Changed() { Raise(nameof(Requests)); Raise(nameof(PendingCount)); }
    public void Refresh()
    {
        lock (sync)
        {
            foreach (var row in requests.Where(r => r.State == ApprovalState.Pending && r.Deadline <= DateTimeOffset.UtcNow).ToArray()) SetState(row.Id, ApprovalState.TimedOut);
            foreach (var row in requests.Where(r => r.State == ApprovalState.Approved && r.Deadline.AddMinutes(2) <= DateTimeOffset.UtcNow).ToArray()) SetState(row.Id, ApprovalState.Unknown);
        }
        Changed();
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true; stop.Cancel(); listener?.Dispose();
            foreach (var row in requests.Where(r => r.State is ApprovalState.Pending or ApprovalState.Approved).ToArray())
                SetState(row.Id, row.State == ApprovalState.Pending ? ApprovalState.Disconnected : ApprovalState.Unknown);
        }
        Changed();
    }
}
