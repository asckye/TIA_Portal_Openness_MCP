using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ReadUnifiedGraphicSelection"), Description("[L2][HMI-Unified][READ]Locate a caller-defined graphical selection by 1..128 exact case-sensitive object names, including unnamed editor groups via known members. itemNamesJson is a JSON string array. Require expectedProject, explicit HMI softwarePath and absolute screenPath. Page raw Left/Top/Width/Height, one-hop engineering owner, advertised relationship metadata and a raw coordinate envelope. This does NOT prove native group membership or transformed group bounds; native-group coverage remains an explicit unsupported gap. No writes, exports, broad property recursion or auto-rebind. Keep scope unchanged with nextCursor; retain every Meta page for before/after comparison. A page budget is checked between objects, not during synchronous Openness calls.")]
        public static ResponseMessage ReadUnifiedGraphicSelection(
            string softwarePath,
            string expectedProject,
            string screenPath,
            [Description("itemNamesJson: JSON array of screen item names.")] string itemNamesJson,
            string cursor = "",
            int pageSize = 20,
            int budgetMs = 5000)
            => Portal.ReadUnifiedGraphicSelection(softwarePath, expectedProject, screenPath, itemNamesJson, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "CompareUnifiedGraphicSelections"), Description("[L2][HMI-Unified][READ]Offline before/after raw coordinate comparison. beforePagesJson and afterPagesJson are JSON arrays of ALL saved ReadUnifiedGraphicSelection Meta pages in pageIndex order. Requires completed traversal, identical project/HMI/screen/name scope and complete geometry. Reports each object's before/after Left/Top/Width/Height and deltas. No TIA connection required; never writes. Changes do not prove group membership, layout constraints or causality. Rejects missing pages/objects/coordinates instead of reporting false unchanged results.")]
        public static ResponseMessage CompareUnifiedGraphicSelections(
            [Description("beforePagesJson: JSON array of the pages read before the change.")] string beforePagesJson,
            [Description("afterPagesJson: JSON array of the pages read after the change.")] string afterPagesJson)
            => Portal.CompareUnifiedGraphicSelections(beforePagesJson, afterPagesJson);
    }
}
