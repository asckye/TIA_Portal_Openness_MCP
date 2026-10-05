using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HmiTagDeletionTools
    {
        private readonly HmiTagDeletionService _service;

        public HmiTagDeletionTools(HmiTagDeletionService service) => _service = service;

        [McpServerTool(Name = "DeleteHmiTag"), Description("[L2][HMI][WRITE] Delete ONE exact ordinary Classic/Unified HMI tag in V20/V21. tagTablePath is /Folder/Table (no recursive name search); empty selects Unified device-root Tags only. Default dryRun=true previews existence. Actual deletion requires dryRun=false AND confirmDelete=true, acquires exclusive access, and verifies absence. No cross-reference call, no save/compile; screen/script/alarm/logging references are NOT checked and may break. No variable table, PLC tag, or system tag deletion.")]
        public ResponseMessage DeleteHmiTag(
            [Description("softwarePath: exact HMI software path from the project tree.")] string softwarePath,
            [Description("tagTablePath: exact /Group/Sub/Table, or empty for Unified root Tags; Classic requires a table.")] string tagTablePath,
            [Description("tagName: one literal HMI variable name; no wildcard or regex search.")] string tagName,
            [Description("dryRun: true previews the exact target without deletion; false requires confirmDelete=true.")] bool dryRun = true,
            [Description("confirmDelete: must be true with dryRun=false; references remain unchecked and may break.")] bool confirmDelete = false)
            => _service.DeleteHmiTag(softwarePath, tagTablePath, tagName, dryRun, confirmDelete);

    }
}
