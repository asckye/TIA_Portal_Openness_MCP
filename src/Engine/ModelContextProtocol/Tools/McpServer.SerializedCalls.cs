using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class InvocationJournal
    {
        // Engine dispatch policy stays out of the JSON adapter linked by Studio tests.
        static partial void RequireDiskFlush(string row, ref bool flush)
        {
            using var document = System.Text.Json.JsonDocument.Parse(row);
            string tool = document.RootElement.GetProperty("tool").GetString() ?? "";
            if (tool.StartsWith("native:", StringComparison.Ordinal) || document.RootElement.TryGetProperty("nativeCallId", out _)) { flush = true; return; }
            // Local READ rows have no native crash evidence. Native boundaries,
            // writes and session calls retain synchronous hardware flushes.
            if (!ToolTaxonomy.UsesOpennessLane(tool) && ToolTaxonomy.OperationOf(tool, null).Operation == "READ") flush = false;
        }
    }
    public static partial class McpServer
    {
        private static readonly AsyncLocal<RequestContext<CallToolRequestParams>?> ProgressRequest = new AsyncLocal<RequestContext<CallToolRequestParams>?>();
        internal static IDisposable UseProgressRequest(RequestContext<CallToolRequestParams> request, CancellationToken token)
        {
            var previous = ProgressRequest.Value;
            var previousToken = McpDispatchCancellation.Value;
            ProgressRequest.Value = request; McpDispatchCancellation.Value = token;
            return new ProgressScope(previous, previousToken);
        }
        private sealed class ProgressScope : IDisposable
        {
            private readonly RequestContext<CallToolRequestParams>? previous;
            private readonly CancellationToken previousToken;
            internal ProgressScope(RequestContext<CallToolRequestParams>? previous, CancellationToken token) { this.previous = previous; previousToken = token; }
            public void Dispose() { ProgressRequest.Value = previous; McpDispatchCancellation.Value = previousToken; }
        }
        static partial void EnterTargetLane(string name, string arguments, ref IDisposable? lane)
            => lane = Dispatch.ToolDispatchLanes.Enter(name, McpDispatchCancellation.Value);
#if !TIA_ENGINE_HOST
        static partial void ApprovalSessionKey(ref object? key)
        {
            if (TiaMcpServer.Runtime.OpennessReadiness.Ready) key = ReadyApprovalSessionKey();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object? ReadyApprovalSessionKey() => EngineServices.GetIfInitialized(typeof(Siemens.Portal));
#endif
#if !TIA_ENGINE_HOST
        static partial void ApprovalBindingIdentity(ref string? identity) => identity = InvocationJournal.BindingSnapshot?.Invoke()?.ToJsonString();
#endif
        static partial void RecordBridgeEvent(string id, string name, string phase)
        { if (!TiaOpenness.Shared.AuditInvocation.IsReadOnlyPreview) InvocationJournal.Write(id, name, phase); }
#if !TIA_ENGINE_HOST
        static partial void StartCallProjection(string id, System.Reflection.MethodInfo method, object?[] arguments, ref System.IDisposable? observation)
        {
            if (TiaOpenness.Shared.AuditInvocation.IsReadOnlyPreview) return;
            try
            {
                var parameters = method.GetParameters();
                string tool = ((McpServerToolAttribute?)System.Attribute.GetCustomAttribute(method, typeof(McpServerToolAttribute)))?.Name ?? method.Name;
                observation = InvocationJournal.Observe(id, tool, "engine", ReleaseKey, IsWriteTool(method), () =>
                {
                    var values = new Dictionary<string, object?>();
                    for (int i = 0; i < parameters.Length; i++)
                        if (!IsInfrastructureParameter(parameters[i].ParameterType)) values[parameters[i].Name!] = arguments[i];
                    return System.Text.Json.JsonSerializer.Serialize(values, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
                });
            }
            catch (System.Exception) /* swallow(logging-failure): diagnostic reflection must not change the existing dispatch boundary */ { }
        }
        static partial void EndCallProjection(System.IDisposable? observation, object? result)
        {
            if (observation is InvocationJournal.CallSpan span)
                span.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
        }
#endif
        static partial void RecordAdmissionRejection(RequestContext<CallToolRequestParams> request, CallToolResult result)
        {
            try
            {
                RecordCallRejection(request.Params?.Name ?? "", new TiaMcp.Logic.V4.Inputs.ToolArguments(
                    System.Text.Json.JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>())), result);
            }
            catch (System.Exception) /* swallow(logging-failure): journal adaptation cannot replace an existing admission response */ { }
        }
        static partial void RecordCallRejection(string name, TiaMcp.Logic.V4.Inputs.ToolArguments arguments, CallToolResult result)
        {
            try
            {
                var methods = AllToolDescriptors();
                bool known = name != null && methods.ContainsKey(name);
                using var journal = InvocationJournal.Observe(System.Guid.NewGuid().ToString("N"), known ? name! : "CallTool", "engine", ReleaseKey,
                    known && IsWrite(ClassificationOf(methods[name!])), () => arguments.Json.GetRawText());
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            }
            catch (System.Exception) /* swallow(logging-failure): journal failures cannot replace an existing rejection response */ { }
        }
#if !TIA_ENGINE_HOST
        static partial void ValidateRuntimeBinding(System.Reflection.MethodInfo method)
        {
            // The bridge has already selected an attributed overload. Looking it up
            // again by name is ambiguous for compatibility overloads (InvokeObject).
            ValidateRuntimeTool(method.Name, null, ToolMetadata.RuntimeOperation(method.Name));
        }
        internal static void ValidateRuntimeTool(string name, string? description, string? operation = null)
        {
            if (!TiaMcpServer.Runtime.OpennessReadiness.Ready) return;
            ValidateReadyRuntimeTool(name, operation);
        }

        // Keep Siemens-backed Portal type references out of the no-TIA invocation path.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ValidateReadyRuntimeTool(string name, string? operation)
        {
            var portal = EngineServices.GetIfInitialized(typeof(Siemens.Portal)) as Siemens.Portal;
            if (portal != null && PreflightLogic.NeedsProject(operation ?? ToolTaxonomy.OperationOf(name, null).Operation, name))
                portal.VerifyBinding(name);
        }
#endif
        internal static IList<McpServerTool> WrapWithSerializedCalls(IList<McpServerTool> tools)
        {
            var result = new List<McpServerTool>();
            foreach (var tool in tools) result.Add(new SerializedCallTool(tool));
            return result;
        }
        internal static IList<McpServerTool> WrapWithAuditCalls(IList<McpServerTool> tools)
        {
            var result = new List<McpServerTool>();
            foreach (var tool in tools) result.Add(new AuditCallTool(tool));
            return result;
        }
    }
    // Outside admission and worker forwarding: invalid write requests are audited too.
    internal sealed class AuditCallTool : McpServerTool
    {
        private readonly McpServerTool inner;
        internal AuditCallTool(McpServerTool inner) { this.inner = inner; }
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            using var actor = TiaOpenness.Shared.ActorScope.EnterCall(request?.Server?.SessionId, request?.Server);
            using var stagingSession = ImportStagingTools.UseSession(request);
            if (string.Equals(ProtocolTool.Name, "CallTool", StringComparison.OrdinalIgnoreCase))
            {
                bool callToolDisabled = McpServer.ApprovalResultWrite(ProtocolTool.Name,
                    System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>()))
                    && !TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath).Enabled;
                string? targetName = request?.Params?.Arguments != null && request.Params.Arguments.TryGetValue("name", out var target)
                    && target.ValueKind == System.Text.Json.JsonValueKind.String ? target.GetString() : null;
                var bridgeReset = targetName == null ? null : McpServer.SessionPrecheckRefusal(targetName);
                if (bridgeReset != null)
                {
                    bool bridgeWrite = McpServer.ApprovalResultWrite(ProtocolTool.Name,
                        System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>()));
                    using var rejectedAudit = TiaOpenness.Shared.AuditInvocation.Begin(bridgeWrite, "engine", McpServer.ReleaseKey, targetName!);
                    bridgeReset = McpServer.SessionPrecheckRefusal(targetName!)!;
                    rejectedAudit?.Complete(bridgeReset.StructuredContent?.ToJsonString());
                    return McpHints.WithRecovery(bridgeReset);
                }
                using var bridgeNativeCalls = InvocationJournal.BeginNativeCallScope();
                var bridgeResult = await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                return McpServer.FinishApproval(McpServer.ObserveSessionOutcome(targetName ?? ProtocolTool.Name, bridgeResult, McpServer.ApprovalResultWrite(ProtocolTool.Name, System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>())), bridgeNativeCalls.NativeCallIssued), null, callToolDisabled);
            }
            bool write = McpServer.ApprovalResultWrite(ProtocolTool.Name,
                System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>()));
            bool disabled = write && !TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath).Enabled;
            string? correlation = null;
            using var audit = TiaOpenness.Shared.AuditInvocation.Begin(write,
                "engine", McpServer.ReleaseKey, ProtocolTool.Name, correlation);
            var reset = McpServer.SessionPrecheckRefusal(ProtocolTool.Name);
            using var nativeCalls = InvocationJournal.BeginNativeCallScope();
            var result = reset ?? await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            result = McpServer.FinishApproval(McpServer.ObserveSessionOutcome(ProtocolTool.Name, result, write, nativeCalls.NativeCallIssued), null, disabled);
            if (audit != null) audit.Complete(result.StructuredContent?.ToJsonString() ??
                (result.Content.Count == 1 && result.Content[0] is TextContentBlock text ? text.Text : null));
            return result;
        }
    }
    internal sealed class SerializedCallTool : McpServerTool
    {
        private readonly McpServerTool _inner;
        public SerializedCallTool(McpServerTool inner) { _inner = inner; }
        public override Tool ProtocolTool => _inner.ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            using var actor = TiaOpenness.Shared.ActorScope.EnterCall(request?.Server?.SessionId, request?.Server);
            using var progressRequest = McpServer.UseProgressRequest(request, cancellationToken);
            string? correlation = null;
            // The call projection starts at the transport boundary. The tool's BEFORE row is written inside its lane,
            // after approval, so journal BEFORE/terminal pairs never overlap on the Openness lane.
            string id = InvocationJournal.NewId(correlation ?? TiaOpenness.Shared.AuditInvocation.CurrentRequestId);
            using var journal = InvocationJournal.Observe(id, ProtocolTool.Name, "engine", McpServer.ReleaseKey,
                McpServer.IsWriteTool(ProtocolTool.Name),
                () => System.Text.Json.JsonSerializer.Serialize(request.Params?.Arguments, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            CallToolResult Recorded(CallToolResult result)
            {
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                return result;
            }
            string arguments = System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>());
            var precheck = await McpServer.PrecheckBeforeApproval(ProtocolTool.Name, arguments, async previewArguments =>
            {
                var previewLane = await Dispatch.ToolDispatchLanes.Acquire(ProtocolTool.Name, cancellationToken).ConfigureAwait(false);
                using var heldPreviewLane = previewLane;
                Dispatch.ToolDispatchLanes.Activate(previewLane);
                // The precheck preview is journaled as its own pair inside the lane; its native reads correlate with this id.
                InvocationJournal.Begin(ProtocolTool.Name, id);
                var original = request!.Params;
                request.Params = new CallToolRequestParams { Name = ProtocolTool.Name,
                    Arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(previewArguments) };
                try
                {
                    McpServer.ValidateCallerInputFiles(ProtocolTool.Name, previewArguments);
                    var preview = McpServer.ToolResult(await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
                    InvocationJournal.Write(id, ProtocolTool.Name, "RETURNED");
                    return preview;
                }
                catch { InvocationJournal.Write(id, ProtocolTool.Name, "THREW"); throw; }
                finally { request.Params = original; }
            }, cancellationToken).ConfigureAwait(false);
            if (precheck != null)
            {
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(precheck, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                return precheck;
            }
            var approval = await McpServer.WaitForApproval(ProtocolTool.Name,
                System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>()), cancellationToken).ConfigureAwait(false);
            if (approval?.Reason != null)
            { var rejection = McpServer.ApprovalRefusal(approval); journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(rejection, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)); return rejection; }
            IDisposable? lane;
            try { lane = await Dispatch.ToolDispatchLanes.Acquire(ProtocolTool.Name, cancellationToken).ConfigureAwait(false); }
            catch (System.OperationCanceledException) /* swallow(privacy): report typed cancellation before dispatch without exposing exception text */
            { return McpServer.FinishApproval(McpServer.V4TargetReject(ProtocolTool.Name, new TiaMcp.Logic.V4.Error("The request was cancelled before dispatch.", new TiaMcp.Logic.V4.CancelledDetails("tool-queue")),
                McpServer.CurrentBehaviorTargets(ProtocolTool.Name, System.Text.Json.JsonSerializer.SerializeToElement(request?.Params?.Arguments ?? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()))), approval, completion: "unknown"); }
            using var dispatchLane = lane;
            Dispatch.ToolDispatchLanes.Activate(lane);
            InvocationJournal.Begin(ProtocolTool.Name, id);
            bool previousContext = McpServer.EnterMcpApprovalContext();
            bool issued = false;
            using var nativeCalls = InvocationJournal.BeginNativeCallScope();
            try
            {
                var reset = McpServer.SessionPrecheckRefusal(ProtocolTool.Name);
                if (reset != null) return Recorded(McpServer.FinishApproval(reset, approval));
                if (approval != null && !McpServer.ApprovalStillMatches(approval,
                    System.Text.Json.JsonSerializer.Serialize(request?.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>())))
                    return Recorded(McpServer.ChangedApprovalRefusal(approval));
                if (!ToolTaxonomy.DispatchesTargets(ProtocolTool.Name))
                {
                    McpServer.ValidateRuntimeTool(ProtocolTool.Name, ProtocolTool.Description);
                    TiaOpenness.Shared.AuditInvocation.StartCurrent();
                }
                else
                {
                    // A batch's read-only session prelude ends before target approval.
                    // Pure local batches do not need a project/session validation.
                    bool sessionPrelude = RequiresSessionPrelude(request);
                    using var auditLane = Dispatch.ToolDispatchLanes.Enter(sessionPrelude ? "GetSessionState" : "GetToolUsage", cancellationToken);
                    if (sessionPrelude) McpServer.ValidateRuntimeTool(ProtocolTool.Name, ProtocolTool.Description);
                    TiaOpenness.Shared.AuditInvocation.StartCurrent();
                }
                issued = true;
                McpServer.ValidateCallerInputFiles(ProtocolTool.Name, arguments);
                var result = McpServer.DiscloseTargets(McpServer.ToolResult(await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false)), ProtocolTool.Name,
                    System.Text.Json.JsonSerializer.SerializeToElement(request?.Params?.Arguments ?? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()));
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                InvocationJournal.Write(id, ProtocolTool.Name, "RETURNED");
                return McpServer.FinishApproval(McpServer.ObserveSessionOutcome(ProtocolTool.Name, result, McpServer.ApprovalWrite(ProtocolTool.Name, arguments), nativeCalls.NativeCallIssued), approval);
            }
            catch (System.Exception ex)
            {
                _ = PortalFailureClassifier.IsPortalProcessLost(ex);
                InvocationJournal.Write(id, ProtocolTool.Name, "THREW");
                var result = McpServer.DiscloseTargets(McpServer.TargetFailure(ProtocolTool.Name, ex, issued), ProtocolTool.Name,
                    System.Text.Json.JsonSerializer.SerializeToElement(request?.Params?.Arguments ?? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()));
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                return McpServer.FinishApproval(McpServer.ObserveSessionOutcome(ProtocolTool.Name, result, McpServer.ApprovalWrite(ProtocolTool.Name, arguments), nativeCalls.NativeCallIssued), approval, completion: "unknown");
            }
            finally { McpServer.LeaveMcpApprovalContext(previousContext); }
        }
        private bool RequiresSessionPrelude(RequestContext<CallToolRequestParams> request)
        {
            if (ProtocolTool.Name == "PreviewToolBatch" || ProtocolTool.Name == "ApplyToolBatch") return true;
            if (ProtocolTool.Name != "RunReadOnlyToolBatch" || request.Params?.Arguments == null) return false;
            var arguments = request.Params.Arguments;
            if (arguments.TryGetValue("expectedProject", out var project) && project.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrEmpty(project.GetString())) return true;
            if (arguments.TryGetValue("operations", out var calls) && calls.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var call in calls.EnumerateArray())
                    if (call.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String
                        && ToolTaxonomy.UsesOpennessLane(name.GetString() ?? "")) return true;
            return false;
        }
    }
}
