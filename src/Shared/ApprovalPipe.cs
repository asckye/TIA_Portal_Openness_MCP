using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TiaOpenness.Shared
{
    internal static class ApprovalPrecheck
    {
        internal const string Recovery = "A previous native write has an unknown outcome. Inspect TIA, then DisconnectPortal and establish a new explicit ConnectPortal/AttachOpenProject session. Never replay the failed request.";
        internal static System.Text.Json.Nodes.JsonNode Mark(System.Text.Json.Nodes.JsonNode body, string? requestId)
        {
            body = body.DeepClone();
            var meta = body["meta"]!;
            if (requestId != null) meta["requestId"] = requestId;
            meta["outcome"] = "rejected-before-operation";
            meta["execution"] = "not-started";
            meta["completeness"] = "none";
            meta["warnings"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject {
                ["code"] = "NATIVE_WARNING", ["message"] = "The write was refused by the approval precheck; no approval was requested.",
                ["details"] = new System.Text.Json.Nodes.JsonObject { ["stage"] = "approval-precheck" } });
            return body;
        }
    }
    internal static class ApprovalPipe
    {
        internal static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("No current user SID.");
        internal static string Name(string scope, string sid) => "TiaMcp.Approval.v1." + PendingApproval.Hash(sid + "\n" + Path.GetFullPath(scope).ToUpperInvariant());
        internal static string CurrentName => Name(ApprovalSettings.SettingsPath, CurrentSid);
        internal static string SecurityDescriptor(string sid) => "O:" + new SecurityIdentifier(sid).Value + "D:P(A;;GA;;;" + sid + ")";

        internal static NamedPipeServerStream CreateServer(string name, string sid, bool first)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(SecurityDescriptor(sid), 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
                // Duplex + overlapped, byte mode, reject remote clients. Protected DACL grants only this SID.
                var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3u | 0x40000000u | (first ? 0x00080000u : 0u),
                    8u, 255, 65536, 65536, 0, ref attributes);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            finally { LocalFree(descriptor); }
        }
        internal static bool PeerIsCurrentUser(NamedPipeServerStream pipe, string sid)
        {
            string? peer = null;
            pipe.RunAsClient(() => peer = WindowsIdentity.GetCurrent().User?.Value);
            return peer == sid;
        }
        internal static bool ServerIsCurrentUser(NamedPipeClientStream pipe, string sid)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return false;
            using (var process = OpenProcess(0x1000, false, pid))
            {
                if (process.IsInvalid || !OpenProcessToken(process.DangerousGetHandle(), 8, out var token)) return false;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value == sid;
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
        { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
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
            try { log.Approval(request.RequestId, request.Host, request.ReleaseKey, request.Tool, "denied", request.PlanHash); }
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
                        try { log.Approval(request.RequestId, request.Host, request.ReleaseKey, request.Tool, "denied", request.PlanHash); }
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
                reason == null ? "granted" : reason == "timeout" ? "timeout" : "denied", request.PlanHash); }
            catch (Exception ex) { Trace.TraceWarning("Approval decision audit unavailable: " + ex.GetType().Name); }
            return new ApprovalOutcome(request, false, reason);
        }
    }
}
