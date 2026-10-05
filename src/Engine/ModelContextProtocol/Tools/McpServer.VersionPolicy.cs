using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        internal static CallToolResult? V4Admission(RequestContext<CallToolRequestParams> request)
        {
            CallToolResult? result = null;
            ValidateV4Admission(request, ref result);
            return result;
        }
        static partial void ValidateV4Admission(RequestContext<CallToolRequestParams> request, ref CallToolResult? result);

        private static IList<McpServerTool> WrapWithVersionPolicy(IList<McpServerTool> tools)
            => tools.Where(t => VersionToolProblem(t.ProtocolTool.Name).Length == 0)
                .Select(t => (McpServerTool)new VersionPolicyTool(t)).ToList();
    }

    // Outermost on both transports, including isolated worker forwarding. Child
    // registration uses the same wrapper; nested bridge dispatch rechecks as well.
    internal sealed class VersionPolicyTool : McpServerTool
    {
        private readonly McpServerTool inner;
        internal VersionPolicyTool(McpServerTool tool) { inner = tool; }
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var refusal = McpServer.V4Admission(request);
            if (refusal != null) return new ValueTask<CallToolResult>(refusal);
            return inner.InvokeAsync(request, cancellationToken);
        }
    }
}
