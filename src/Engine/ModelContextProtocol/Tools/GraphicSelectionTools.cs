using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Linq;
using System.Collections.Generic;
using System;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class GraphicSelectionTools
    {
        private readonly GraphicSelectionService _graphicSelection;

        public GraphicSelectionTools(GraphicSelectionService service) => _graphicSelection = service;

        [McpServerTool(Name = "GetUnifiedGraphicSelection"), Description("[L2][HMI-Unified][READ]Locate a caller-defined graphical selection by 1..128 exact case-sensitive object names, including unnamed editor groups via known members. itemNames is an array. Require expectedProject, explicit HMI softwarePath and absolute screenPath. Page raw Left/Top/Width/Height, one-hop engineering owner, advertised relationship metadata and a raw coordinate envelope. This does NOT prove native group membership or transformed group bounds; native-group coverage remains an explicit unsupported gap. No writes, exports, broad property recursion or auto-rebind. Keep scope unchanged with nextCursor; retain every data.page for before/after comparison. A page budget is checked between objects, not during synchronous Openness calls.")]
        public CallToolResult ReadUnifiedGraphicSelectionV4(
            string softwarePath,
            string expectedProject,
            string screenPath,
            [Description("itemNames: Array of screen item names.")] string[] itemNames,
            string cursor = "",
            int pageSize = 20,
            int budgetMs = 5000)
            => HmiInspectionContract.Run("GetUnifiedGraphicSelection", false, false, () => ReadUnifiedGraphicSelection(softwarePath, expectedProject, screenPath, V4Json.Serialize(itemNames), cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ReadUnifiedGraphicSelection(
            string softwarePath,
            string expectedProject,
            string screenPath,
            [Description("itemNamesJson: JSON array of screen item names.")] string itemNamesJson,
            string cursor = "",
            int pageSize = 20,
            int budgetMs = 5000)
        => _graphicSelection.ReadUnifiedGraphicSelection(softwarePath, expectedProject, screenPath, itemNamesJson, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "CompareUnifiedGraphicSelections"), Description("[L2][HMI-Unified][READ]Offline before/after raw coordinate comparison. beforePages and afterPages are JSON arrays of ALL saved GetUnifiedGraphicSelection data.page objects in pageIndex order. Requires completed traversal, identical project/HMI/screen/name scope and complete geometry. Reports each object's before/after Left/Top/Width/Height and deltas. No TIA connection required; never writes. Changes do not prove group membership, layout constraints or causality. Rejects missing pages/objects/coordinates instead of reporting false unchanged results.")]
        public CallToolResult CompareUnifiedGraphicSelectionsV4(
            [Description("beforePages: Array of the pages read before the change.")] GraphicSelectionPage[] beforePages,
            [Description("afterPages: Array of the pages read after the change.")] GraphicSelectionPage[] afterPages)
            => HmiInspectionContract.Run("CompareUnifiedGraphicSelections", false, false, () => CompareUnifiedGraphicSelections(V4Json.Serialize(beforePages), V4Json.Serialize(afterPages)));

        public ResponseMessage CompareUnifiedGraphicSelections(
            [Description("beforePagesJson: JSON array of the pages read before the change.")] string beforePagesJson,
            [Description("afterPagesJson: JSON array of the pages read after the change.")] string afterPagesJson)
        => _graphicSelection.CompareUnifiedGraphicSelections(beforePagesJson, afterPagesJson);
    }
}
