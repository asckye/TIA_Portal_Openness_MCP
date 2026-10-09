using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Shared
{
    internal static class ApprovalPrecheck
    {
        internal const string Recovery = SessionBehavior.Recovery;
        internal static System.Text.Json.Nodes.JsonNode Mark(System.Text.Json.Nodes.JsonNode body, string? requestId)
        {
            body = body.DeepClone();
            var meta = body["meta"]!;
            if (requestId != null) meta["requestId"] = requestId;
            meta["outcome"] = "rejected-before-operation";
            meta["execution"] = "not-started";
            meta["completeness"] = "none";
            meta["warnings"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject {
                ["code"] = "APPROVAL_PRECHECK_REFUSED", ["message"] = "The write was refused by the approval precheck; no approval was requested.",
                ["details"] = new System.Text.Json.Nodes.JsonObject { ["stage"] = "approval-precheck" } });
            return body;
        }
    }
    internal static class ApprovalPipe
    {
        internal static string CurrentSid => LocalPipeSecurity.CurrentSid;
        internal static string Name(string scope, string sid) => "TiaMcp.Approval.v1." + PendingApproval.Hash(sid + "\n" + Path.GetFullPath(scope).ToUpperInvariant());
        internal static string CurrentName => Name(ApprovalSettings.SettingsPath, CurrentSid);
        internal static string SecurityDescriptor(string sid) => LocalPipeSecurity.SecurityDescriptor(sid);
        internal static NamedPipeServerStream CreateServer(string name, string sid, bool first)
            => LocalPipeSecurity.CreateServer(name, sid, first, 255);
        internal static bool PeerIsCurrentUser(NamedPipeServerStream pipe, string sid)
            => LocalPipeSecurity.PeerIsCurrentUser(pipe, sid);
        internal static bool ServerIsCurrentUser(NamedPipeClientStream pipe, string sid)
            => LocalPipeSecurity.ServerIsCurrentUser(pipe, sid);
    }

    internal sealed class ApprovalOutcome
    {
        internal PendingApproval Request { get; }
        internal bool Disabled { get; }
        internal string? Reason { get; }
        internal ApprovalOutcome(PendingApproval request, bool disabled, string? reason)
        { Request = request; Disabled = disabled; Reason = reason; }
    }

    internal static class ApprovalClient
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Used = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        internal static ApprovalOutcome RejectAdministrativeWrite(PendingApproval request)
        {
            var log = AuditLog.Current;
            try { log.Approval(request.RequestId, request.Host, request.ReleaseKey, request.Tool, "denied", request.PlanHash, request.EffectiveActor); }
            catch (Exception ex) { Trace.TraceWarning("Approval decision audit unavailable: " + ex.GetType().Name); }
            return new ApprovalOutcome(request, false, "denied");
        }
        internal static async Task Complete(ApprovalOutcome approval, string outcome, string? pipeName = null)
        {
            if (approval.Disabled || approval.Reason != null) return;
            try
            {
                using (var limit = new CancellationTokenSource(1000))
                using (var pipe = new NamedPipeClientStream(".", pipeName ?? ApprovalPipe.CurrentName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    await pipe.ConnectAsync(250, limit.Token).ConfigureAwait(false);
                    if (!ApprovalPipe.ServerIsCurrentUser(pipe, ApprovalPipe.CurrentSid)) return;
                    approval.Request.Kind = "result"; approval.Request.Outcome = outcome;
                    await ApprovalFrames.Write(pipe, approval.Request, limit.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Trace.TraceInformation("Approval completion unavailable: " + ex.GetType().Name); }
        }
        // Host creates the nonce. No MCP argument/metadata is an approval token.
        internal static async Task<ApprovalOutcome> Wait(PendingApproval request, ApprovalSettings settings,
            CancellationToken cancellation, string? pipeName = null, AuditLog? audit = null)
        {
            if (!settings.Enabled) return new ApprovalOutcome(request, true, null);
            string? reason = "workbench-unavailable";
            var log = audit ?? AuditInvocation.CurrentLog ?? AuditLog.Current;
            bool connected = false;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var remaining = request.Deadline - DateTimeOffset.UtcNow;
                limit.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                try
                {
                    if (!Used.TryAdd(request.RequestId, request.PlanHash + request.ArgumentDigest))
                    {
                        try { log.Approval(request.RequestId, request.Host, request.ReleaseKey, request.Tool, "denied", request.PlanHash, request.EffectiveActor); }
                        catch (Exception ex) { Trace.TraceWarning("Replay refusal audit unavailable: " + ex.GetType().Name); }
                        return new ApprovalOutcome(request, false, "denied");
                    }
                    using (var pipe = new NamedPipeClientStream(".", pipeName ?? ApprovalPipe.CurrentName, PipeDirection.InOut,
                        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                    {
                        await pipe.ConnectAsync(250, limit.Token).ConfigureAwait(false);
                        connected = true;
                        if (!ApprovalPipe.ServerIsCurrentUser(pipe, ApprovalPipe.CurrentSid)) throw new IOException("Unexpected pipe owner.");
                        request.Validate();
                        await ApprovalFrames.Write(pipe, request, limit.Token).ConfigureAwait(false);
                        var decision = await ApprovalFrames.Read<ApprovalDecision>(pipe, limit.Token).ConfigureAwait(false);
                        // A mismatched, late, duplicate or malformed decision cannot release this call.
                        reason = request.Deadline <= DateTimeOffset.UtcNow ? "timeout"
                            : !limit.IsCancellationRequested && decision.Matches(request) ? decision.Decision == "granted" ? null : "denied" : "denied";
                    }
                }
                catch (OperationCanceledException) /* swallow(privacy): cancellation before execution becomes a bounded approval refusal */ { reason = cancellation.IsCancellationRequested ? "denied" : connected ? "timeout" : "workbench-unavailable"; }
                catch (Exception ex)
                { Trace.TraceInformation("Approval channel unavailable: " + ex.GetType().Name + ": " + ex.Message); reason = DateTimeOffset.UtcNow >= request.Deadline ? "timeout" : "workbench-unavailable"; }
            }
            try { log.Approval(request.RequestId, request.Host, request.ReleaseKey, request.Tool,
                reason == null ? "granted" : reason == "timeout" ? "timeout" : "denied", request.PlanHash, request.EffectiveActor); }
            catch (Exception ex) { Trace.TraceWarning("Approval decision audit unavailable: " + ex.GetType().Name); }
            return new ApprovalOutcome(request, false, reason);
        }
    }
}
