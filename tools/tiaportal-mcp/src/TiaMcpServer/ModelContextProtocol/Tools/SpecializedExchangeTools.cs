using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SpecializedExchangeTools
    {
        private readonly SpecializedExchangeService _specializedExchange;

        public SpecializedExchangeTools(SpecializedExchangeService specializedExchange) => _specializedExchange = specializedExchange;

        [McpServerTool(Name="ExchangePlcSupervisions"), Description("[L2][PLC-Software][WRITE] ProDiag XLSX export/import/importSettings, explicit native options and diagnostic state. Import requires offline PLC; output is new and hashed. Default preview; no save/compile/download.")]
        public ResponseMessage ExchangePlcSupervisions(
            string softwarePath,
            [Description("action: the operation to perform - export | import | importSettings.")] string action,
            string filePath,
            string importOptions="None",
            bool dryRun=true)
            => _specializedExchange.ExchangePlcSupervisions(softwarePath,action,filePath,importOptions,dryRun);
    }
}
