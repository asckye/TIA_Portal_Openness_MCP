using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcp.Shared;

internal sealed class CfcPortService
{
    private readonly OptionalProductPort _port;
    public CfcPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal CfcPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalProductPort(call, identity);
    }
    public ResponseMessage ExchangeCfcCharts(string softwarePath, string action, string filePath, string modelVersion = "", long filter = 0, bool unattended = true, bool deleteAtTarget = false, bool dryRun = true, string chartNamesJson = "[]", bool skipChartPreflight = false)
    {
        var request = new OptionalProductRequest { Operation = "ExchangeCfcCharts", SoftwarePath = softwarePath, Action = action, FilePath = filePath, ModelVersion = modelVersion, Filter = filter, Unattended = unattended, DeleteAtTarget = deleteAtTarget, DryRun = dryRun, ChartNamesJson = chartNamesJson, SkipChartPreflight = skipChartPreflight };
        return _port.Invoke(request, () =>
        {
            var plan = CfcLogic.ValidateExchangeRequest(action, filePath, modelVersion, filter, chartNamesJson, deleteAtTarget, dryRun);
            request.Plan.Writes = plan.Writes;
            request.Plan.ChartNames = plan.ChartNames;
        });
    }
    public ResponseMessage ManageCfcChartProtection(string softwarePath, string chartName, string action = "read", string currentPassword = "", string newHashedPassword = "", bool dryRun = true, string modelVersion = "V2.0", bool skipChartPreflight = false)
    {
        var request = new OptionalProductRequest { Operation = "ManageCfcChartProtection", SoftwarePath = softwarePath, ChartName = chartName, Action = action, CurrentPassword = currentPassword, NewHashedPassword = newHashedPassword, DryRun = dryRun, ModelVersion = modelVersion, SkipChartPreflight = skipChartPreflight };
        return _port.Invoke(request, () =>
        {
            var plan = CfcLogic.ValidateProtectionRequest(action, chartName, currentPassword, newHashedPassword, dryRun);
            request.Plan.Writes = plan.Writes;
        });
    }
}
