using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class OnlinePlanTagRulesTests
{
    [Theory]
    [InlineData("14sp1"), InlineData("21")]
    public async Task Symbolic_paths_and_provider_options_have_separate_rules(string release)
    {
        using var worker = new WorkerClient(release, "missing", "missing", false) { Bundled = false };
        async Task<JsonNode> Plan(string name, JsonObject args)
        {
            var inner = PortedToolContract.Create(worker, release).OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == name);
            return await ToolHintOnceTests.Invoke(new FoundationV4Tool(inner, release), args);
        }
        foreach (var tag in new[] { "DB_HMI.Start", "\"DB_HMI\".Start", "\"DB_HMI\".\"StartDelay\"", "DB_HMI.UpdateRate" })
        {
            var result = await Plan("PlanOnlineReadOnlyMonitoring", new() { ["softwarePath"] = "PLC_1", ["tagPaths"] = new JsonArray(tag) });
            Assert.True((bool?)result["ok"], result.ToJsonString());
        }
        foreach (var tag in new[] { "%M0.0", "MotorRun", "" })
        {
            var result = await Plan("PlanOnlineReadOnlyMonitoring", new() { ["softwarePath"] = "PLC_1", ["tagPaths"] = new JsonArray(tag) });
            Refused(result, "tagPaths");
        }
        Refused(await Plan("PlanOnlineReadOnlyMonitoring", new() {
            ["softwarePath"] = "PLC_1", ["tagPaths"] = new JsonArray("DB_HMI.Start"), ["mode"] = "x" }), "mode");
        var provider = new JsonObject { ["provider"] = "opcua", ["endpoint"] = "opc.tcp://fixture:4840",
            ["tagPaths"] = new JsonArray("DB_HMI.Start"), ["options"] = new JsonObject { ["source"] = "force-table" } };
        Refused(await Plan("PlanOnlineReadOnlyDataProvider", provider), "options");
        provider["options"] = new JsonObject { ["pollMs"] = 1000, ["source"] = "watch-table-export" };
        var accepted = await Plan("PlanOnlineReadOnlyDataProvider", provider);
        Assert.True((bool?)accepted["ok"], accepted.ToJsonString());
        provider["provider"] = "x";
        Refused(await Plan("PlanOnlineReadOnlyDataProvider", provider), "provider");
    }

    private static void Refused(JsonNode result, string parameter)
    {
        Assert.False((bool?)result["ok"], result.ToJsonString());
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Equal(parameter, (string?)result["error"]?["details"]?["parameter"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }
}
