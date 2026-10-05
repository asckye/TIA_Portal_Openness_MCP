using System.Collections.Generic;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Both transports assert the generated contract before wrapping local or isolated dispatch.
        public static IList<McpServerTool> WrapTools(IList<McpServerTool> tools)
        {
            foreach (var tool in tools) AssertV4Tool(tool.ProtocolTool.Name);
            return WrapWithVersionPolicy(Isolation.IsolatedWorkerHost.Current != null ? Isolation.IsolatedWorkerHost.Wrap(tools) :
                WrapWithSerializedCalls(WrapWithResponseGuard(tools)));
        }
    }
}
