using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        static partial void RecordBridgeEvent(string id, string name, string phase) => InvocationJournal.Write(id, name, phase);
        static partial void StartCallProjection(string id, System.Reflection.MethodInfo method, object?[] arguments, ref System.IDisposable? observation)
        {
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
                var methods = AllToolMethods();
                bool known = name != null && methods.ContainsKey(name);
                using var journal = InvocationJournal.Observe(System.Guid.NewGuid().ToString("N"), known ? name! : "CallTool", "engine", ReleaseKey,
                    known && IsWriteTool(methods[name!]), () => arguments.Json.GetRawText());
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            }
            catch (System.Exception) /* swallow(logging-failure): journal failures cannot replace an existing rejection response */ { }
        }
        static partial void ValidateRuntimeBinding(System.Reflection.MethodInfo method)
        {
            // The bridge has already selected an attributed overload. Looking it up
            // again by name is ambiguous for compatibility overloads (InvokeObject).
            ValidateRuntimeTool(method.Name, null, ToolMetadata.RuntimeOperation(method.Name));
        }
        internal static void ValidateRuntimeTool(string name, string? description, string? operation = null)
        {
            // Never create a Portal for an offline request; inspection stays available after a fault.
            var portal = EngineServices.GetIfInitialized(typeof(Siemens.Portal)) as Siemens.Portal;
            if (portal != null && PreflightLogic.NeedsProject(operation ?? ToolTaxonomy.OperationOf(name, null).Operation, name))
                portal.VerifyBinding(name);
        }
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
            using var audit = TiaOpenness.Shared.AuditInvocation.Begin(ToolCatalog.IsWrite(ProtocolTool.Name),
                "engine", McpServer.ReleaseKey, ProtocolTool.Name);
            var result = await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            if (audit != null) audit.Complete(result.StructuredContent?.ToJsonString() ??
                (result.Content.Count == 1 && result.Content[0] is TextContentBlock text ? text.Text : null));
            return result;
        }
    }
    internal sealed class SerializedCallTool : McpServerTool
    {
        // All transports share this gate; session leases coordinate separate MCP processes.
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private readonly McpServerTool _inner;
        public SerializedCallTool(McpServerTool inner) { _inner = inner; }
        public override Tool ProtocolTool => _inner.ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            try { await Gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (System.OperationCanceledException) /* swallow(privacy): report typed cancellation before dispatch without exposing exception text */
            { return McpServer.V4TargetReject(ProtocolTool.Name, new TiaMcp.Logic.V4.Error("The request was cancelled before dispatch.", new TiaMcp.Logic.V4.CancelledDetails("tool-queue")),
                McpServer.CurrentBehaviorTargets(ProtocolTool.Name, System.Text.Json.JsonSerializer.SerializeToElement(request?.Params?.Arguments ?? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()))); }
            string? correlation = null;
            if (Isolation.IsolatedWorkerHost.IsChild)
            {
                try
                {
                    var meta = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request.Params?.Meta, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                    correlation = meta?["tiaMcpWorkerCorrelation"]?.GetValue<string>();
                }
                catch /* swallow(parse-fallback): malformed optional correlation metadata uses a new journal id without leaking the call gate */ { /* Malformed optional metadata must not leak the serialization gate. */ }
            }
            string id = InvocationJournal.Begin(ProtocolTool.Name, correlation);
            using var journal = InvocationJournal.Observe(id, ProtocolTool.Name, "engine", McpServer.ReleaseKey,
                McpServer.IsWriteTool(ProtocolTool.Name),
                () => System.Text.Json.JsonSerializer.Serialize(request.Params?.Arguments, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            bool issued = false;
            try
            {
                McpServer.ValidateRuntimeTool(ProtocolTool.Name, ProtocolTool.Description);
                issued = true;
                var result = McpServer.DiscloseTargets(McpServer.ToolResult(await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false)), ProtocolTool.Name,
                    System.Text.Json.JsonSerializer.SerializeToElement(request?.Params?.Arguments ?? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()));
                ExitFaultedWorker(id);
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                InvocationJournal.Write(id, ProtocolTool.Name, "RETURNED");
                return result;
            }
            catch (System.Exception ex)
            {
                _ = PortalFailureClassifier.IsPortalProcessLost(ex);
                InvocationJournal.Write(id, ProtocolTool.Name, "THREW");
                ExitFaultedWorker(id);
                var result = McpServer.DiscloseTargets(McpServer.TargetFailure(ProtocolTool.Name, ex, issued), ProtocolTool.Name,
                    System.Text.Json.JsonSerializer.SerializeToElement(request?.Params?.Arguments ?? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()));
                journal.Complete(() => System.Text.Json.JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                return result;
            }
            finally { Gate.Release(); }
        }
        private void ExitFaultedWorker(string id)
        {
            if (!Isolation.IsolatedWorkerHost.IsChild || Isolation.IsolatedWorkerHost.NativeFault == null) return;
            InvocationJournal.Write(id, ProtocolTool.Name, "NATIVE_CHANNEL_FAULT");
            System.Environment.Exit(75); // Client process only; no save/retry/TIA process termination.
        }
    }
}
