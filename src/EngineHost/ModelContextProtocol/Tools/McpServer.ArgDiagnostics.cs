using System.Collections.Generic;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Both transports assert the generated contract before wrapping dispatch.
        public static IList<McpServerTool> WrapTools(IList<McpServerTool> tools)
        {
            foreach (var tool in tools) AssertV4Tool(tool.ProtocolTool.Name);
            var guarded = WrapWithSerializedCalls(WrapWithResponseGuard(tools));
            return WrapWithAuditCalls(WrapWithVersionPolicy(Dispatch.OpennessReadinessGuard.Wrap(guarded)));
        }

        internal static IList<McpServerTool> WrapToolsForRelease(IList<McpServerTool> tools, string release)
        {
            foreach (var tool in tools) AssertV4Tool(tool.ProtocolTool.Name);
            var guarded = WrapWithSerializedCalls(WrapWithResponseGuard(tools));
            return WrapWithAuditCalls(WrapWithVersionPolicy(Dispatch.OpennessReadinessGuard.Wrap(guarded),
                name => Siemens.ToolVersionPolicy.ToolProblem(release, name), release));
        }
    }
}
