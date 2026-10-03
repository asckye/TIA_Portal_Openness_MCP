using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ConnectToProject"), Description("[L0][Portal][SESSION] Attach only to one running TIA process using processId, processStartUtc and full projectPath from ListPortalProcessProjects. Rejects stale identity, a competing MCP lease, and an unclean prior owner. Captures exact project identity; never starts TIA, saves, closes another project, or automatically retries. MCP-owned open projects must be explicitly closed/disconnected first.")]
        public static ResponseMessage ConnectToProject(
            [Description("processId: exact running TIA PID from ListPortalProcessProjects.")] int processId,
            [Description("processStartUtc: exact ISO UTC start timestamp from the same process listing; prevents PID reuse.")] string processStartUtc,
            [Description("projectPath: absolute project file path from that process listing; another path is refused.")] string projectPath)
        {
            Portal.ConnectToProject(processId, processStartUtc, projectPath);
            return new ResponseMessage { Message = "Exact TIA project binding established.", Meta = new JsonObject { ["success"] = true, ["binding"] = Portal.GetBindingIdentity() } };
        }
    }
}
