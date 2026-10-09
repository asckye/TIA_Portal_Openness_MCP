using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        private static readonly AsyncLocal<bool> McpApprovalContext = new AsyncLocal<bool>();
        private static readonly AsyncLocal<CancellationToken> McpDispatchCancellation = new AsyncLocal<CancellationToken>();
        private static readonly AsyncLocal<int> ApprovalPreviewDepth = new AsyncLocal<int>();
        private static readonly AsyncLocal<Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>?> ApprovalWaitOverride =
            new AsyncLocal<Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>?>();
        internal static Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? ApprovalWaitOverrideForTests
        { get => ApprovalWaitOverride.Value; set => ApprovalWaitOverride.Value = value; }
        private sealed class InternalPreviewScope : IDisposable
        {
            private readonly int previous = ApprovalPreviewDepth.Value;
            internal InternalPreviewScope() { ApprovalPreviewDepth.Value = previous + 1; }
            public void Dispose() => ApprovalPreviewDepth.Value = previous;
        }
        internal static IDisposable BeginReadOnlyApprovalPreview() => new InternalPreviewScope();
        private sealed class SessionFault { internal bool Unknown; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SessionFault> SessionFaults =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, SessionFault>();
        private static readonly AsyncLocal<Func<object?>?> ApprovalSessionOverride = new AsyncLocal<Func<object?>?>();
        internal static Func<object?>? ApprovalSessionKeyForTests { get => ApprovalSessionOverride.Value; set => ApprovalSessionOverride.Value = value; }
        static partial void ApprovalSessionKey(ref object? key);
        static partial void IsolationParent(ref bool parent);
        private static object? CurrentApprovalSession()
        {
            if (ApprovalSessionOverride.Value is { } test) return test();
            object? key = null; ApprovalSessionKey(ref key); return key;
        }
        internal static CallToolResult? SessionPrecheckRefusal(string name)
        {
            if (SessionBehavior.IsRecoveryTool(name) || ToolTaxonomy.DispatchesTargets(name)) return null;
            if (!ToolTaxonomy.UsesOpennessLane(name)) return null;
            var key = CurrentApprovalSession();
            return SessionBehavior.RequiresReset(key != null && SessionFaults.GetValue(key, _ => new SessionFault()).Unknown, ToolTaxonomy.UsesOpennessLane(name))
                ? V4Reject(name, HostBehavior.SessionReset()) : null;
        }
        // Only a call that reached Openness (a journaled native boundary in this call, or native-issued evidence from a
        // worker) can leave the session in an unknown state; an unknown refusal before the first native call cannot.
        internal static CallToolResult ObserveSessionOutcome(string name, CallToolResult result, bool write, bool nativeCallIssued)
        {
            var body = ResultBody(result);
            bool parent = false; IsolationParent(ref parent);
            if (parent || (string?)body?["meta"]?["outcome"] != "unknown" || !ToolTaxonomy.UsesOpennessLane(name)) return result;
            var data = body?["data"] as JsonObject;
            bool True(JsonNode? value) => value is JsonValue flag && flag.TryGetValue<bool>(out var truth) && truth;
            bool nativeIssued = nativeCallIssued || True((data?["evidence"] as JsonObject)?["nativeOutcomeUnknown"]) || True(data?["nativeIssued"]);
            bool mutation = write || ToolTaxonomy.OperationOf(name, null).Operation is "FILE" or "EXECUTE";
            var key = SessionBehavior.LocksSession(nativeIssued, (string?)body?["meta"]?["outcome"] == "unknown", mutation) ? CurrentApprovalSession() : null;
            if (key != null) SessionFaults.GetValue(key, _ => new SessionFault()).Unknown = true;
            return result;
        }
        internal static bool IsReadOnlyApprovalPreview => ApprovalPreviewDepth.Value > 0;
        internal static async Task<CallToolResult?> PrecheckBeforeApproval(string name, string arguments,
            Func<string, Task<CallToolResult>> previewCall, CancellationToken token)
        {
            if (ApprovalPreviewDepth.Value > 0) return null;
            var reset = SessionPrecheckRefusal(name);
            if (reset != null) return reset;
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            // A write that will wait for approval refuses a missing or unreadable caller file first, so no approval is
            // requested. Other calls validate inside their journaled lane (isolated host/child journals stay correlated).
            if (settings.Enabled && ApprovalWrite(name, arguments))
            {
                try { ValidateCallerInputFiles(name, arguments); }
                catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error) { return TargetFailure(name, error, false); }
            }
            var args = JsonNode.Parse(arguments)!.AsObject();
            if (HostBehavior.LegacyBatchImport(name, args)) return null;
            bool candidate = ToolBehaviorPolicy(name, BehaviorPolicy.Current) == BehaviorPolicy.SafeV4;
            if (!HostBehavior.NeedsPrecheck(ApprovalPreviewDepth.Value > 0, ApprovalWrite(name, arguments), settings.Enabled,
                TryDryRunDefault(name, out _) || candidate || HostBehavior.IsSingleXmlImport(name))) return null;
            string? identity = null; ApprovalBindingIdentity(ref identity);
            AuditInvocation.RecordCurrentRequest(PendingApproval.Create("engine", ReleaseKey, name, arguments, identity, settings.TimeoutSeconds, AuditInvocation.CurrentRequestId).PlanHash);
            CallToolResult? result = null;
            var batchRefusal = HostBehavior.BatchApplyRefusal(name, args);
            if (batchRefusal != null) result = V4Reject(name, batchRefusal);
            if (candidate)
            {
                string? missing = HostBehavior.MissingApplyArgument(args,
                    SessionCandidateContract.Entries.Contains(name, StringComparer.Ordinal) && SessionCandidateContract.Action(name) == "attach");
                if (missing != null) result = V4Reject(name, InvalidInput(missing));
            }
            if (result == null)
            {
                if (candidate || TryDryRunDefault(name, out _))
                    args[candidate ? "mode" : "dryRun"] = candidate ? JsonValue.Create("preview") : JsonValue.Create(true);
                using var preview = BeginReadOnlyApprovalPreview();
                using var audit = AuditInvocation.ReadOnlyPreview();
                try { token.ThrowIfCancellationRequested(); result = await previewCall(args.ToJsonString()).ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) /* swallow(privacy): cancellation in the read-only precheck has no issued write */
                { result = V4Reject(name, new Error("The approval precheck was cancelled.", new CancelledDetails("approval-precheck"))); }
                catch (Exception ex) /* swallow(privacy): preview failures are classified before a write is issued */
                { result = TargetFailure(name, ex, false); }
            }
            var blocked = HostBehavior.PreviewApplyRefusal(ResultBody(result));
            blocked ??= HostBehavior.BatchPreviewRefusal(name, JsonNode.Parse(arguments)!.AsObject(), ResultBody(result));
            if (blocked != null) result = V4Reject(name, blocked);
            if (ResultSucceeded(ResultBody(result)) == true) return PreviewHasNoEffect(ResultBody(result)) ? result : null;
            var body = ApprovalPrecheck.Mark(ResultBody(result)!, AuditInvocation.CurrentRequestId);
            return new CallToolResult { IsError = true, StructuredContent = body, Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
        internal static bool PreviewHasNoEffect(JsonNode? body)
            => HostBehavior.PreviewHasNoEffect(body);
        internal static bool ApprovalWrite(string tool, string arguments)
        {
            var args = JsonNode.Parse(arguments)!.AsObject();
            bool hasDryRun = TryDryRunDefault(tool, out var defaultPreview);
            return HostBehavior.ApprovalWrite(tool, args,
                ToolBehaviorPolicy(tool, BehaviorPolicy.Current) == BehaviorPolicy.SafeV4,
                IsWriteTool(tool), hasDryRun, defaultPreview);
        }
        private static bool TryDryRunDefault(string tool, out bool defaultPreview)
        {
            defaultPreview = true;
            if (!AllToolDescriptors(includeUnavailable: true).TryGetValue(tool, out var method)) return false;
            var parameter = method.Parameters.FirstOrDefault(p => p.Name == "dryRun" && p.ClrType == "System.Boolean");
            if (parameter == null) return false;
            defaultPreview = method.DryRun.Default;
            return method.DryRun.Present;
        }
        internal static bool ApprovalResultWrite(string tool, string arguments)
        {
            if (!string.Equals(tool, "CallTool", StringComparison.OrdinalIgnoreCase)) return ApprovalWrite(tool, arguments);
            var args = JsonNode.Parse(arguments)!.AsObject();
            return args["name"] is JsonValue name && name.TryGetValue<string>(out var target)
                && !string.Equals(target, "CallTool", StringComparison.OrdinalIgnoreCase)
                && AllToolDescriptors(includeUnavailable: true).ContainsKey(target)
                && ApprovalWrite(target, (args["arguments"] as JsonObject)?.ToJsonString() ?? "{}");
        }
        internal static async Task<ApprovalOutcome?> WaitForApproval(string tool, string arguments, CancellationToken token)
        {
            if (tool == "SaveExportContent" && JsonNode.Parse(arguments)?["outputPath"] is JsonValue output
                && output.TryGetValue<string>(out var path) && ApprovalSettings.IsAdministrativeTarget(path))
            {
                var rejected = PendingApproval.Create("engine", ReleaseKey, tool, arguments, null, 120, TiaOpenness.Shared.AuditInvocation.CurrentRequestId);
                TiaOpenness.Shared.AuditInvocation.RecordCurrentRequest(rejected.PlanHash);
                return ApprovalClient.RejectAdministrativeWrite(rejected);
            }
            if (!ApprovalWrite(tool, arguments) || tool == "ApplyToolBatch") return null;
            var args = JsonNode.Parse(arguments)!.AsObject();
            if (ToolBehaviorPolicy(tool, BehaviorPolicy.Current) == BehaviorPolicy.SafeV4
                && (string?)args["mode"] != "apply") return null;
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            string? identity = null;
            ApprovalBindingIdentity(ref identity);
            var pending = PendingApproval.Create("engine", ReleaseKey, tool, arguments, identity, settings.TimeoutSeconds, TiaOpenness.Shared.AuditInvocation.CurrentRequestId);
            TiaOpenness.Shared.AuditInvocation.RecordCurrentRequest(pending.PlanHash);
            if (ApprovalWaitOverride.Value is { } overrideWait) return await overrideWait(pending, settings, token).ConfigureAwait(false);
            if (settings.Enabled) ApprovalWaitSignal("begin", settings.TimeoutSeconds);
            try { return await ApprovalClient.Wait(pending, settings, token).ConfigureAwait(false); }
            finally { if (settings.Enabled) ApprovalWaitSignal("end", settings.TimeoutSeconds); }
        }
        static partial void ApprovalWaitSignal(string phase, int seconds);
        static partial void ApprovalBindingIdentity(ref string? identity);
        internal static CallToolResult ApprovalRefusal(ApprovalOutcome approval)
            => FinishApproval(V4Reject(approval.Request.Tool, new Error(approval.Request.Tool == "SaveExportContent"
                ? "MCP cannot write Workbench approval settings." : "Workbench confirmation is required before this write.",
                new ConfirmationRequiredDetails(approval.Reason!, approval.Request.PlanHash, approval.Request.RequestId))), approval);
        internal static CallToolResult ChangedApprovalRefusal(ApprovalOutcome approval)
        {
            ApprovalClient.Complete(approval, "rejected-before-operation").GetAwaiter().GetResult();
            return ApprovalRefusal(new ApprovalOutcome(approval.Request, false, "denied"));
        }
        internal static CallToolResult FinishApproval(CallToolResult result, ApprovalOutcome? approval, bool disabled = false, string? completion = null)
        {
            result = McpHints.WithRecovery(result);
            if (approval == null && !disabled) return result;
            var body = ResultBody(result);
            if (body == null)
            {
                if (approval != null) ApprovalClient.Complete(approval, completion ?? "unknown").GetAwaiter().GetResult();
                return result;
            }
            body = ApprovalResult.Decorate(body, disabled || approval?.Disabled == true, approval?.Request.RequestId);
            if (approval != null) ApprovalClient.Complete(approval, completion ?? (string?)body["meta"]?["outcome"] ?? "unknown").GetAwaiter().GetResult();
            return new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
        internal static CallToolResult ApprovedBridgeCall(string name, string arguments, Func<CallToolResult> invoke,
            Func<CallToolResult?>? beforeDispatch = null)
        {
            // Target approval must finish before acquiring its lane. The identity
            // check and audit start belong inside that lane, immediately before dispatch.
            var refusal = McpApprovalContext.Value ? PrecheckBeforeApproval(name, arguments, previewArgs => Task.FromResult(CallToolCore(name,
                new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.Deserialize<JsonElement>(previewArgs)))), McpDispatchCancellation.Value).GetAwaiter().GetResult() : SessionPrecheckRefusal(name);
            if (refusal != null) return refusal;
            var approval = McpApprovalContext.Value && ApprovalPreviewDepth.Value == 0
                ? WaitForApproval(name, arguments, McpDispatchCancellation.Value).GetAwaiter().GetResult() : null;
            if (approval?.Reason != null) return ApprovalRefusal(approval);
            IDisposable? lane = null;
            EnterTargetLane(beforeDispatch == null ? name : "GetSessionState", arguments, ref lane);
            using var dispatchLane = lane;
            IDisposable? targetLane = null;
            if (beforeDispatch != null) EnterTargetLane(name, arguments, ref targetLane);
            using var boundedTargetLane = targetLane;
            var reset = SessionPrecheckRefusal(name);
            if (reset != null) return FinishApproval(reset, approval);
            if (approval != null && !ApprovalStillMatches(approval, arguments)) return ChangedApprovalRefusal(approval);
            var rejection = beforeDispatch?.Invoke();
            if (rejection != null) return FinishApproval(rejection, approval);
            try { ValidateCallerInputFiles(name, arguments); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error) { return FinishApproval(TargetFailure(name, error, false), approval); }
            if (ApprovalPreviewDepth.Value > 0) return FinishApproval(invoke(), null,
                ApprovalWrite(name, arguments) && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled);
            TiaOpenness.Shared.AuditInvocation.StartCurrent();
            using var nativeCalls = InvocationJournal.BeginNativeCallScope();
            try
            {
                var invoked = invoke();
                return FinishApproval(ObserveSessionOutcome(name, invoked, ApprovalWrite(name, arguments), nativeCalls.NativeCallIssued), approval,
                    McpApprovalContext.Value && ApprovalWrite(name, arguments) && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled);
            }
            catch { if (approval != null) ApprovalClient.Complete(approval, "unknown").GetAwaiter().GetResult(); throw; }
        }
        static partial void EnterTargetLane(string name, string arguments, ref IDisposable? lane);
        internal static bool ApprovalStillMatches(ApprovalOutcome approval, string arguments)
        {
            if (approval.Disabled) return true;
            try
            {
                string? identity = null; ApprovalBindingIdentity(ref identity);
                return PendingApproval.Create("engine", ReleaseKey, approval.Request.Tool, arguments, identity,
                    approval.Request.TimeoutSeconds).ArgumentDigest == approval.Request.ArgumentDigest;
            }
            catch (Exception) /* swallow(fail-open-guard): an unreadable identity refuses the write before execution */ { return false; }
        }
        internal static bool EnterMcpApprovalContext() { bool previous = McpApprovalContext.Value; McpApprovalContext.Value = true; return previous; }
        internal static void LeaveMcpApprovalContext(bool previous) => McpApprovalContext.Value = previous;
    }
}
