using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
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

        [McpServerTool(Name="ExchangePlcSupervisions"), Description("[L2][PLC-Software][WRITE] ProDiag XLSX export/import/importSettings, explicit native options and diagnostic state. Import requires offline PLC; output is new and hashed. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangePlcSupervisions(
            string softwarePath,
            [Description("action: the operation to perform - export | import | importSettings.")] string action,
            [Description("filePath: absolute XLSX file for native ProDiag supervision exchange; new output or existing import file.")] string filePath,
            string importOptions="None",
            bool dryRun=true)
            => OptionalPackageContract.Run("ExchangePlcSupervisions", !dryRun, () => _specializedExchange.ExchangePlcSupervisions(softwarePath,action,filePath,importOptions,dryRun));
    }
}
