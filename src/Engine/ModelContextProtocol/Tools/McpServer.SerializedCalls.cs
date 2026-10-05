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
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            try
            {
                McpServer.ValidateRuntimeTool(ProtocolTool.Name, ProtocolTool.Description);
                var result = await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                ExitFaultedWorker(id);
                InvocationJournal.Write(id, ProtocolTool.Name, "RETURNED");
                return result;
            }
            catch (System.Exception ex)
            {
                _ = PortalFailureClassifier.IsPortalProcessLost(ex);
                InvocationJournal.Write(id, ProtocolTool.Name, "THREW");
                ExitFaultedWorker(id);
                throw;
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
