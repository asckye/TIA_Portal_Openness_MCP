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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arguments = request.Params?.Arguments;
            string problem = "";
            if (arguments != null)
                foreach (var pair in arguments)
                    if (!names.Add(pair.Key))
                    { problem = "Duplicate argument names differing only by case are ambiguous: " + pair.Key + ". Nothing was executed."; break; }
            if (problem.Length == 0) problem = McpServer.VersionCallProblem(ProtocolTool.Name, key =>
            {
                var args = request.Params?.Arguments;
                if (args == null) return null;
                foreach (var pair in args)
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value.ToString();
                return null;
            });
            if (problem.Length != 0)
                return new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = problem } }
                });
            return inner.InvokeAsync(request, cancellationToken);
        }
    }
}
