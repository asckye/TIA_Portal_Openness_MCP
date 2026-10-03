using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        public static ResponseMessage ConnectToProject(
            int processId,
            string processStartUtc,
            string projectPath)
            => ((SessionTools)EngineServices.Get(typeof(SessionTools))).ConnectToProject(processId, processStartUtc, projectPath);
    }
}
