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
        private static readonly AsyncLocal<Func<object?>?> ApprovalSessionOverride = new AsyncLocal<Func<object?>?>();
        internal static Func<object?>? ApprovalSessionKeyForTests { get => ApprovalSessionOverride.Value; set => ApprovalSessionOverride.Value = value; }
        static partial void ApprovalSessionKey(ref object? key);
        private static object? CurrentApprovalSession()
        {
            if (ApprovalSessionOverride.Value is { } test) return test();
            object? key = null; ApprovalSessionKey(ref key); return key;
        }
        internal static CallToolResult ObserveSessionOutcome(string name, CallToolResult result, bool write, string arguments, bool preview = false, JsonArray
                ? unsettledBeforeSave = null)
        {
#if TIA_ENGINE_HOST
            var body = ResultBody(result);
            if (body == null) return result;
            bool unknown = TiaMcp.FoundationHost.UnsettledCalls.HasUnknownOutcome(body);
            if (!unknown && name is not ("SaveProject" or "SaveProjectCopy" or "CallTool")) return result;
            if ((string?)body["meta"]?["outcome"] == "unknown" && body["data"] is JsonObject data) data.Remove("recovery");
            var args = JsonNode.Parse(arguments)!.AsObject();
            if (name == "CallTool" && args["arguments"] is JsonObject target) args = target;
            if (name == "CallTool" && (string?)body["meta"]?["tool"] is string targetName) name = targetName;
            bool mutation = write || ToolTaxonomy.OperationOf(name, null).Operation is "FILE" or "EXECUTE";
            bool isPreview = preview || HostBehavior.IsPreview(args, TryDryRunDefault(name, out bool defaultPreview) && defaultPreview);
            if (unknown && mutation && !isPreview && !IsReadOnlyApprovalPreview)
            {
                string? project = (string?)Worker.Binding?["identity"]?["projectPath"] ?? (string?)Worker.Binding?["projectPath"];
                string? digest = TiaMcp.FoundationHost.WriteDigest.ForOutcome(name, args, project);
                RecordUnsettled((string?)body["meta"]?["requestId"] ?? InvocationJournal.CorrelationId, name, digest);
                Worker.LateResults.RecordUnknown((string?)body["meta"]?["requestId"] ?? InvocationJournal.CorrelationId,
                    name, digest, result);
            }
            if (name is "SaveProject" or "SaveProjectCopy")
                body = Worker.Unsettled.WarnOnSave(name, body, unsettledBeforeSave);
            return new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
#else
            return result;
#endif
        }
        internal static CallToolResult? GenerationFenceRefusal(string name, string arguments, bool dispatched = false, bool? write = null)
        {
#if TIA_ENGINE_HOST
            var key = TiaMcp.FoundationHost.EngineHostConfiguration.CurrentOrNull?.Worker.SessionKey;
            if (key != null && (write ?? ApprovalWrite(name, arguments))
                && GenerationWriteFence.RefusedRun(key, ReleaseKey, name, arguments, dispatched) is { } runId)
                return V4Reject(name, new Error("A generation run is active in this session.", new PreconditionFailedDetails("generation-run-active", runId)),
                    new JsonObject { ["runId"] = runId });
#endif
            return null;
        }
        internal static CallToolResult? ReviewedApplyRefusal(string name, string arguments)
        {
#if TIA_ENGINE_HOST
            var args = JsonNode.Parse(arguments)!.AsObject();
            bool hasPreview = TryDryRunDefault(name, out bool defaultPreview);
            if (HostBehavior.IsPreview(args, hasPreview && defaultPreview)
                || !(ApprovalWrite(name, arguments) || ToolTaxonomy.OperationOf(name, null).Operation is "FILE" or "EXECUTE")) return null;
            string? project = (string?)Worker.Binding?["identity"]?["projectPath"] ?? (string?)Worker.Binding?["projectPath"];
            Worker.Unsettled.ProjectChanged(project);
            string digest = TiaMcp.FoundationHost.WriteDigest.Capture(name, args, project);
            string? priorId = Worker.LateResults.FindUnread(digest) ?? Worker.Unsettled.FindWrite(digest)?.RequestId;
            if (priorId != null)
                return V4Reject(name, new Error("The same write is running or has an unread result. Do not repeat this call. "
                    + "Read GetCallResult(requestId) for its result or outcome summary, then inspect its target before submitting another write.",
                    new PreconditionFailedDetails("same-call-unsettled", priorId)));
            TiaMcp.FoundationHost.LateCalls.IdentifyWrite(digest);
#endif
            return null;
        }
        internal static IDisposable DeferGenerationCancellation()
        {
            var previous = McpDispatchCancellation.Value;
            McpDispatchCancellation.Value = CancellationToken.None;
            return new CancellationScope(previous);
        }
        private sealed class CancellationScope(CancellationToken previous) : IDisposable
        { public void Dispose() => McpDispatchCancellation.Value = previous; }
        internal static bool IsReadOnlyApprovalPreview => ApprovalPreviewDepth.Value > 0;
        internal static async Task<CallToolResult?> PrecheckBeforeApproval(string name, string arguments,
            Func<string, Task<CallToolResult>> previewCall, CancellationToken token)
        {
            if (ApprovalPreviewDepth.Value > 0) return null;
#if TIA_ENGINE_HOST
            if (Worker.Faulted && name is not ("GetSessionState" or "GetOpennessWorkerStatus" or "RestartOpennessWorker" or "DisconnectPortal")
                && ToolTaxonomy.UsesOpennessLane(name) && !ToolTaxonomy.DispatchesTargets(name)
                && !(TiaMcp.Adapters.Contracts.PortedFamilies.Contains(name)
                    && TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(name).Name == "F04"))
                return V4Reject(name, new Error("The worker is faulted. Call RestartOpennessWorker before another native call.",
                    new PreconditionFailedDetails("worker-faulted", null)));
#endif
            var args = JsonNode.Parse(arguments)!.AsObject();
            try
            {
                try { ApprovalOutputTargets.ValidateWhitespace(name, args); }
                catch (ArgumentException)
                {
                    if (ReviewedApplyRefusal(name, arguments) is { } whitespaceReplay) return whitespaceReplay;
                    throw;
                }
                if (ApprovalWrite(name, arguments)) ApprovalOutputTargets.Normalize(name, args);
                if (ReviewedApplyRefusal(name, arguments) is { } replayRefusal) return replayRefusal;
#if TIA_ENGINE_HOST
                if (Worker.LateResults.Running is { } running && !Worker.LateResults.ContinuesCurrentCall
                    && ToolTaxonomy.UsesOpennessLane(name)
                    && name is not ("GetSessionState" or "GetOpennessWorkerStatus") && !ToolTaxonomy.DispatchesTargets(name))
                    return V4Reject(name, TiaMcp.FoundationHost.LateCalls.BusyError(running));
#endif
            }
            catch (ArgumentException error)
            {
                return V4Reject(name, new Error("Filesystem target is invalid or too long to display.",
                    new InvalidArgumentDetails(error.ParamName ?? "", Array.Empty<string>())));
            }
            var fenced = GenerationFenceRefusal(name, arguments);
            if (fenced != null) return fenced;
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            // A write that will wait for approval refuses a missing or unreadable caller file first, so no approval is
            // requested. Other calls validate inside their journaled lane (isolated host/child journals stay correlated).
            if (settings.Enabled && ApprovalWrite(name, arguments))
            {
                try { ValidateCallerInputFiles(name, arguments); }
                catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error) { return TargetFailure(name, error, false); }
            }
            if (HostBehavior.LegacyBatchImport(name, args)) return null;
            if (!HostBehavior.NeedsPrecheck(ApprovalPreviewDepth.Value > 0, ApprovalWrite(name, arguments), settings.Enabled,
                TryDryRunDefault(name, out _) || HostBehavior.IsSingleXmlImport(name))) return null;
            string? identity = await BridgeWait.On(BridgeApprovalIdentityAsync()).ConfigureAwait(false);
            AuditInvocation
                .RecordCurrentRequest(PendingApproval
                    .Create(McpServer.InvocationHostTag, ReleaseKey, name, arguments, identity, settings.TimeoutSeconds, AuditInvocation.CurrentRequestId)
                    .PlanHash);
            CallToolResult? result = null;
            var batchRefusal = HostBehavior.BatchApplyRefusal(name, args);
            if (batchRefusal != null) result = V4Reject(name, batchRefusal);
            if (result == null)
            {
                if (TryDryRunDefault(name, out _)) args["dryRun"] = JsonValue.Create(true);
                using var preview = BeginReadOnlyApprovalPreview();
                using var audit = AuditInvocation.ReadOnlyPreview();
                try {
                    token.ThrowIfCancellationRequested(); result = await BridgeWait.On(previewCall(args.ToJsonString())).ConfigureAwait(false); token
                        .ThrowIfCancellationRequested();
                }
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
                var rejected = PendingApproval
                    .Create(McpServer.InvocationHostTag, ReleaseKey, tool, arguments, null, 120, TiaOpenness.Shared.AuditInvocation.CurrentRequestId);
                TiaOpenness.Shared.AuditInvocation.RecordCurrentRequest(rejected.PlanHash);
                return ApprovalClient.RejectAdministrativeWrite(rejected);
            }
            if (!ApprovalWrite(tool, arguments) || tool == "ApplyToolBatch") return null;
            var args = JsonNode.Parse(arguments)!.AsObject();
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            string? identity = await BridgeWait.On(BridgeApprovalIdentityAsync()).ConfigureAwait(false);
            var pending = PendingApproval
                .Create(McpServer.InvocationHostTag, ReleaseKey, tool, arguments, identity, settings.TimeoutSeconds, TiaOpenness.Shared.AuditInvocation
                    .CurrentRequestId);
            TiaOpenness.Shared.AuditInvocation.RecordCurrentRequest(pending.PlanHash);
            if (ApprovalWaitOverride.Value is { } overrideWait) return await BridgeWait.On(overrideWait(pending, settings, token)).ConfigureAwait(false);
            if (settings.Enabled) ApprovalWaitSignal("begin", settings.TimeoutSeconds);
            try { return await BridgeWait.On(ApprovalClient.Wait(pending, settings, token)).ConfigureAwait(false); }
            finally { if (settings.Enabled) ApprovalWaitSignal("end", settings.TimeoutSeconds); }
        }
        static partial void ApprovalWaitSignal(string phase, int seconds);
        static partial void ApprovalBindingIdentity(ref string? identity);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0040",
            Justification = "Optional partial hooks require implicit accessibility to remain erasable.")]
        static partial void ApprovalBindingIdentityAsync(ref Task<string?>? pending);
        private static Task<string?> BridgeApprovalIdentityAsync()
        {
            Task<string?>? pending = null;
            ApprovalBindingIdentityAsync(ref pending);
            if (pending != null) return pending;
            string? identity = null; ApprovalBindingIdentity(ref identity);
            return Task.FromResult(identity);
        }
        internal static CallToolResult ApprovalRefusal(ApprovalOutcome approval)
            => ApprovalRefusalAsync(approval).GetAwaiter().GetResult();
        internal static Task<CallToolResult> ApprovalRefusalAsync(ApprovalOutcome approval)
            => FinishApprovalAsync(V4Reject(approval.Request.Tool, approval.Administrative
                ? new Error("MCP cannot write Workbench approval settings.",
                    new ConfirmationRequiredDetails(approval.Reason!, approval.Request.PlanHash, approval.Request.RequestId))
                : approval.Reason == "cancelled"
                    ? new Error("Workbench confirmation is required before this write.", new CancelledDetails("approval-wait"))
                : HostBehavior.Confirmation(approval.Reason!, approval.Request.PlanHash, approval.Request.RequestId,
                    !IsWriteTool(approval.Request.Tool) && approval.Request.Tool != "ManagePlcGitRepository"
                        && TiaMcp.Logic.V4.CallerInputFiles.WritesOutsideOutputRoot(approval.Request.Tool,
                        JsonNode.Parse(approval.Request.ParametersJson)!.AsObject()))), approval);
        internal static CallToolResult ChangedApprovalRefusal(ApprovalOutcome approval)
            => ChangedApprovalRefusalAsync(approval).GetAwaiter().GetResult();
        internal static async Task<CallToolResult> ChangedApprovalRefusalAsync(ApprovalOutcome approval)
        {
            await BridgeWait.On(ApprovalClient.Complete(approval, "rejected-before-operation")).ConfigureAwait(false);
            return await BridgeWait.On(ApprovalRefusalAsync(new ApprovalOutcome(approval.Request, false, "denied"))).ConfigureAwait(false);
        }
        internal static CallToolResult FinishApproval(CallToolResult result, ApprovalOutcome? approval, bool disabled = false, string? completion = null)
            => FinishApprovalAsync(result, approval, disabled, completion).GetAwaiter().GetResult();
        internal static async Task<CallToolResult> FinishApprovalAsync(CallToolResult result, ApprovalOutcome? approval, bool disabled = false, string
                ? completion = null)
        {
            result = McpHints.WithRecovery(result);
            if (approval == null && !disabled) return result;
            var body = ResultBody(result);
            if (body == null)
            {
                if (approval != null) await BridgeWait.On(ApprovalClient.Complete(approval, completion ?? "unknown")).ConfigureAwait(false);
                return result;
            }
            body = ApprovalResult.Decorate(body, disabled || approval?.Disabled == true, approval?.Request.RequestId);
            if (approval != null) await BridgeWait.On(ApprovalClient.Complete(approval, completion ?? (string?)body["meta"]?["outcome"] ?? "unknown"))
                .ConfigureAwait(false);
            return new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
        internal static async Task<CallToolResult> ApprovedBridgeCallAsync(string name, string arguments, Func<Task<CallToolResult>> invoke,
            Func<Task<CallToolResult?>>? beforeDispatch = null)
        {
#if TIA_ENGINE_HOST
            using var digestScope = TiaMcp.FoundationHost.WriteDigest.Enter();
#endif
            // Target approval must finish before acquiring its lane. The identity
            // check and audit start belong inside that lane, immediately before dispatch.
            var refusal = McpApprovalContext.Value ? await BridgeWait.On(PrecheckBeforeApproval(name, arguments, previewArgs => CallToolCoreAsync(name,
                new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.Deserialize<JsonElement>(previewArgs))), McpDispatchCancellation.Value))
                    .ConfigureAwait(false) : null;
            if (refusal != null) return refusal;
            var approval = McpApprovalContext.Value && ApprovalPreviewDepth.Value == 0
                ? await BridgeWait.On(WaitForApproval(name, arguments, McpDispatchCancellation.Value)).ConfigureAwait(false) : null;
            if (approval?.Reason != null) return await BridgeWait.On(ApprovalRefusalAsync(approval)).ConfigureAwait(false);
            using var dispatchLane = await BridgeWait.On(BridgeTargetLaneAsync(beforeDispatch == null ? name : "GetSessionState", arguments))
                .ConfigureAwait(false);
            ActivateTargetLane(dispatchLane);
            using var boundedTargetLane = beforeDispatch == null ? null
                : await BridgeWait.On(BridgeTargetLaneAsync(name, arguments)).ConfigureAwait(false);
            ActivateTargetLane(boundedTargetLane);
            var fenced = GenerationFenceRefusal(name, arguments, dispatched: true);
            if (fenced != null) return await BridgeWait.On(FinishApprovalAsync(fenced, approval)).ConfigureAwait(false);
            if (ReviewedApplyRefusal(name, arguments) is { } queuedReplay)
                return await BridgeWait.On(FinishApprovalAsync(queuedReplay, approval)).ConfigureAwait(false);
            if (approval != null && !await BridgeWait.On(ApprovalStillMatchesAsync(approval, arguments)).ConfigureAwait(false))
                return await BridgeWait.On(ChangedApprovalRefusalAsync(approval)).ConfigureAwait(false);
            var rejection = beforeDispatch == null ? null : await BridgeWait.On(beforeDispatch()).ConfigureAwait(false);
            if (rejection != null) return await BridgeWait.On(FinishApprovalAsync(rejection, approval)).ConfigureAwait(false);
            try { ValidateCallerInputFiles(name, arguments); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            { return await BridgeWait.On(FinishApprovalAsync(TargetFailure(name, error, false), approval)).ConfigureAwait(false); }
            if (ApprovalPreviewDepth.Value > 0) return await BridgeWait.On(FinishApprovalAsync(await BridgeWait.On(invoke()).ConfigureAwait(false), null,
                ApprovalWrite(name, arguments) && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled)).ConfigureAwait(false);
            TiaOpenness.Shared.AuditInvocation.StartCurrent();
            using var nativeCalls = InvocationJournal.BeginNativeCallScope();
            try
            {
                var invoked = await BridgeWait.On(invoke()).ConfigureAwait(false);
                return await BridgeWait
                    .On(FinishApprovalAsync(ObserveSessionOutcome(name, invoked, ApprovalWrite(name, arguments), arguments), approval,
                    McpApprovalContext.Value && ApprovalWrite(name, arguments) && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled))
                        .ConfigureAwait(false);
            }
            catch { if (approval != null) await BridgeWait.On(ApprovalClient.Complete(approval, "unknown")).ConfigureAwait(false); throw; }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0040",
            Justification = "Optional partial hooks require implicit accessibility to remain erasable.")]
        static partial void AcquireTargetLaneAsync(string name, string arguments, ref Task<IDisposable?>? pending);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0040",
            Justification = "Optional partial hooks require implicit accessibility to remain erasable.")]
        static partial void ActivateTargetLane(IDisposable? lane);
        private static Task<IDisposable?> BridgeTargetLaneAsync(string name, string arguments)
        {
            Task<IDisposable?>? pending = null;
            AcquireTargetLaneAsync(name, arguments, ref pending);
            return pending ?? Task.FromResult<IDisposable?>(null);
        }
        internal static bool ApprovalStillMatches(ApprovalOutcome approval, string arguments)
            => ApprovalStillMatchesAsync(approval, arguments).GetAwaiter().GetResult();
        internal static async Task<bool> ApprovalStillMatchesAsync(ApprovalOutcome approval, string arguments)
        {
            if (approval.Disabled) return true;
            try
            {
                string? identity = await BridgeWait.On(BridgeApprovalIdentityAsync()).ConfigureAwait(false);
                return PendingApproval.Create(McpServer.InvocationHostTag, ReleaseKey, approval.Request.Tool, arguments, identity,
                    approval.Request.TimeoutSeconds).ArgumentDigest == approval.Request.ArgumentDigest;
            }
            catch (Exception) /* swallow(fail-open-guard): an unreadable identity refuses the write before execution */ { return false; }
        }
        internal static bool EnterMcpApprovalContext() { bool previous = McpApprovalContext.Value; McpApprovalContext.Value = true; return previous; }
        internal static void LeaveMcpApprovalContext(bool previous) => McpApprovalContext.Value = previous;
    }
}
