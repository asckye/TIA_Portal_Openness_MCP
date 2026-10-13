using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class GraphicSelectionService : UnifiedPortService
    {
        public GraphicSelectionService() { }
        internal GraphicSelectionService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadUnifiedGraphicSelection(string softwarePath, string expectedProject, string screenPath,
            string itemNamesJson, string cursor = "", int pageSize = 20, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedGraphicSelection", SoftwarePath = softwarePath, ExpectedProject = expectedProject, ScreenPath = screenPath, ItemNamesJson = itemNamesJson, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage CompareUnifiedGraphicSelections(string beforePagesJson, string afterPagesJson)
        {
            try { return new ResponseMessage { Message = "Offline comparison of raw geometry; no TIA call or coordinate write.", Meta = UnifiedGraphicComparison.Compare(beforePagesJson, afterPagesJson) }; }
            catch (Exception error)
            {
                var failure = new JsonObject { ["path"] = "comparison", ["kind"] = "gap", ["status"] = "failed", ["code"] = "InvalidSnapshot", ["reason"] = error.GetBaseException().GetType().Name + ": " + error.GetBaseException().Message };
                return new ResponseMessage { Message = "Comparison refused; supply complete compatible selection pages.", Meta = ResponseMeta.Unstamped(false, ("operationSuccess", false), ("apiCallSuccess", false), ("dataComplete", false), ("readOnly", true), ("offline", true), ("failureCount", 1), ("failures", new JsonArray(failure))) };
            }
        }
    }
}
