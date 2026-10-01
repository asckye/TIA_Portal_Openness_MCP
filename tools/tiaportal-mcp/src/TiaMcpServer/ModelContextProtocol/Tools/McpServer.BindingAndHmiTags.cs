using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "DeleteHmiTag"), Description("[L2][HMI][WRITE] Delete ONE exact ordinary Classic/Unified HMI tag in V20/V21. tagTablePath is /Folder/Table (no recursive name search); empty selects Unified device-root Tags only. Default dryRun=true previews existence. Actual deletion requires dryRun=false AND confirmDelete=true, acquires exclusive access, and verifies absence. No cross-reference call, no save/compile; screen/script/alarm/logging references are NOT checked and may break. No variable table, PLC tag, or system tag deletion.")]
        public static ResponseMessage DeleteHmiTag(
            [Description("softwarePath: exact HMI software path from the project tree.")] string softwarePath,
            [Description("tagTablePath: exact /Group/Sub/Table, or empty for Unified root Tags; Classic requires a table.")] string tagTablePath,
            [Description("tagName: one literal HMI variable name; no wildcard or regex search.")] string tagName,
            [Description("dryRun: true previews the exact target without deletion; false requires confirmDelete=true.")] bool dryRun = true,
            [Description("confirmDelete: must be true with dryRun=false; references remain unchecked and may break.")] bool confirmDelete = false)
            => Portal.DeleteHmiTag(softwarePath, tagTablePath, tagName, dryRun, confirmDelete);

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
