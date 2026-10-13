using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class PlcSimAdvancedPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    public PlcSimAdvancedPortService() : this(HardwareAddressWorkerBridge.Call) { }
    internal PlcSimAdvancedPortService(Func<string, JsonObject, JsonNode?> call) => this.call = call;
    private ResponseJsonReport Invoke(string operation, object arguments)
        => call("runtime-channels.ExecutePlcSimAdvanced", new JsonObject {
            ["request"] = new JsonObject { ["operation"] = operation, ["arguments"] = JsonSerializer.SerializeToNode(arguments) }
        })?.Deserialize<ResponseJsonReport>() ?? throw new InvalidOperationException("Missing PLCSIM Advanced worker reply.");
    public ResponseJsonReport ReadPlcSimAdvancedInstances(bool includeState = true, string apiPath = "", string memberFilter = "")
        => Invoke("ReadPlcSimAdvancedInstances", new { includeState, apiPath, memberFilter });
    public ResponseJsonReport ManagePlcSimAdvancedInstance(string instanceName, string action, string cpuType = "", int timeoutMs = 60000, bool confirmInstanceChange = false, bool dryRun = true, string apiPath = "", string communicationInterface = "")
        => Invoke("ManagePlcSimAdvancedInstance", new { instanceName, action, cpuType, timeoutMs, confirmInstanceChange, dryRun, apiPath, communicationInterface });
    public ResponseJsonReport ReadPlcSimAdvancedTags(string instanceName, string namesJson = "", string areaFilter = "", string nameContains = "", int offset = 0, int limit = 200, string apiPath = "")
        => Invoke("ReadPlcSimAdvancedTags", new { instanceName, namesJson, areaFilter, nameContains, offset, limit, apiPath });
    public ResponseJsonReport WritePlcSimAdvancedTags(string instanceName, string valuesJson, bool confirmWrite = false, bool dryRun = true, string apiPath = "")
        => Invoke("WritePlcSimAdvancedTags", new { instanceName, valuesJson, confirmWrite, dryRun, apiPath });
    public ResponseJsonReport RunPlcSimAdvancedTestScenario(string scenarioJson, bool confirmRun = false, bool dryRun = true, string apiPath = "")
        => Invoke("RunPlcSimAdvancedTestScenario", new { scenarioJson, confirmRun, dryRun, apiPath });
}
