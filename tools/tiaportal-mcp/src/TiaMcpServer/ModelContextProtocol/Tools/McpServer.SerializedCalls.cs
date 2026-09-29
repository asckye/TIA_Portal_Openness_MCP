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
        private static IList<McpServerTool> WrapWithSerializedCalls(IList<McpServerTool> tools)
        {
            var result = new List<McpServerTool>();
            foreach (var tool in tools) result.Add(new SerializedCallTool(tool));
            return result;
        }
    }
    internal sealed class SerializedCallTool : McpServerTool
    {
        // All transports in this server process share the gate. Separate servers must bind separate TIA processes.
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private readonly McpServerTool _inner;
        public SerializedCallTool(McpServerTool inner) { _inner = inner; }
        public override Tool ProtocolTool => _inner.ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string id = InvocationJournal.Begin(ProtocolTool.Name);
            try
            {
                var result = await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                InvocationJournal.Write(id, ProtocolTool.Name, "RETURNED");
                return result;
            }
            catch { InvocationJournal.Write(id, ProtocolTool.Name, "THREW"); throw; }
            finally { Gate.Release(); }
        }
    }
}
