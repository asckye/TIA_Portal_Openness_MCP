extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class ShowObjectInEditorTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Served_usage_keeps_ui_defaults_and_written_object_sequences(string release)
    {
        var call = ToolUsageCatalog.ServedCall("ShowObjectInEditor", release)!;
        Assert.False((bool)call["arguments"]!["dryRun"]!);
        foreach (bool engineSource in new[] { false, true })
        {
            var sequences = ToolUsageCatalog.Sequences(engineSource);
            var pattern = sequences.Single(s => (string?)s!["id"] == "sequence/show-written-object")!;
            Assert.Contains(release, pattern["releaseKeys"]!.AsArray().Select(k => (string)k!));
            Assert.Equal("ShowObjectInEditor", (string?)pattern["steps"]!.AsArray()[^1]!["tool"]);
            foreach (var maintained in ToolUsageCatalog.Sequences().Where(s => s!["steps"]!.AsArray()
                .Any(step => (string?)step!["tool"] == "ShowObjectInEditor")))
                Assert.True(JsonNode.DeepEquals(maintained, sequences.Single(s => (string?)s!["id"] == (string?)maintained!["id"])));
        }
        using var fixture = new DiscoveryFixture(release);
        var usage = await fixture.Call("GetToolUsage", new { toolName = "ShowObjectInEditor" });
        Assert.True((bool?)usage["ok"], usage.ToJsonString());
        var examples = usage["data"]!["examples"]!.AsArray();
        Assert.Contains(examples, e => (string?)e!["id"] == "sequence/watch-table");
        Assert.Contains(examples, e => (string?)e!["id"] == "sequence/hardware-network");
        var watchResult = await fixture.Call("GetToolUsage", new { exampleId = "sequence/watch-table" });
        Assert.True((bool?)watchResult["ok"], watchResult.ToJsonString());
        var watch = Assert.Single(watchResult["data"]!["examples"]!.AsArray())!;
        Assert.Equal(new[] { "20", "21" }, watch["releaseKeys"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(release is "20" or "21", (bool?)watch["available"]);
        var watchShow = watch["steps"]!.AsArray()[^1]!;
        Assert.Equal("ShowObjectInEditor", (string?)watchShow["tool"]);
        Assert.Equal("plcWatchTable", (string?)watchShow["arguments"]!["kind"]);
        Assert.False((bool)watchShow["arguments"]!["dryRun"]!);
        var result = await fixture.Call("GetToolUsage", new { exampleId = "sequence/show-written-object", exampleKind = "sequence" });
        Assert.True((bool?)result["ok"], result.ToJsonString());
        var served = Assert.Single(result["data"]!["examples"]!.AsArray())!;
        Assert.True((bool?)served["available"], served.ToJsonString());
        Assert.Equal("ShowObjectInEditor", (string?)served["steps"]!.AsArray()[^1]!["tool"]);
        var networkResult = await fixture.Call("GetToolUsage", new { exampleId = "sequence/hardware-network", exampleKind = "sequence" });
        Assert.True((bool?)networkResult["ok"], networkResult.ToJsonString());
        var network = Assert.Single(networkResult["data"]!["examples"]!.AsArray())!;
        Assert.True((bool?)network["available"], network.ToJsonString());
        Assert.Equal("network", (string?)network["steps"]!.AsArray()[^1]!["arguments"]!["view"]);
    }

    public static IEnumerable<object[]> Calls() =>
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from preview in new bool?[] { null, false, true }
        select new object[] { release, preview! };

    [Theory, MemberData(nameof(Calls))]
    public async Task Ui_calls_open_once_by_default_and_never_request_approval(string release, bool? preview)
    {
        var classification = ToolMetadata.Find("ShowObjectInEditor")!;
        Assert.Equal("UI", classification.Operation);
        Assert.Equal("L2", classification.Layer);
        Assert.Equal("Project", classification.Domain);
        Assert.False(classification.BatchWrite);
        Assert.Equal("UI", ToolMetadata.RuntimeOperation("ShowObjectInEditorV4"));
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(contextWorker, release);
        using var worker = new EditorWorker();
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        int approvals = 0;
        Host.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
        {
            approvals++;
            return Task.FromResult(new ApprovalOutcome(pending, false, "denied"));
        };
        try
        {
            var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "ShowObjectInEditor");
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("dryRun").GetProperty("default").GetBoolean());
            var usage = ToolUsageCatalog.Describe("ShowObjectInEditor", release, "plc-foundation", tool.ProtocolTool.Description!,
                JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject());
            Assert.False((bool)usage["example"]!["request"]!["params"]!["arguments"]!["dryRun"]!);
            var args = new JsonObject { ["kind"] = "plcBlock", ["softwarePath"] = "PLC_1", ["objectPath"] = "Group/FC_Add" };
            if (preview.HasValue) args["dryRun"] = preview.Value;
            var body = await ToolHintOnceTests.Invoke(tool, args);
            Assert.True((bool?)body["ok"], body.ToJsonString());
            Assert.Equal(0, approvals);
            Assert.Equal(1, worker.Calls);
            Assert.Equal(preview == true ? 0 : 1, worker.ShowInEditorCalls);
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    private sealed class EditorWorker : IFoundationWorker
    {
        internal int Calls, ShowInEditorCalls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            Assert.Equal("engineering-services.ExecuteEngineeringServices", operation);
            Assert.Equal("ShowObjectInEditor", (string?)arguments["request"]?["Operation"]);
            Assert.Equal("Group/FC_Add", (string?)arguments["request"]?["ObjectPath"]);
            bool preview = (bool)arguments["request"]!["DryRun"]!;
            Assert.Equal(preview, (bool)arguments["dryRun"]!);
            Calls++;
            if (!preview) ShowInEditorCalls++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "Editor stand-in", ["Meta"] = new JsonObject {
                ["operationSuccess"] = true, ["mayHaveChanged"] = false, ["dryRun"] = preview } });
        }
        public void Dispose() { }
    }
}
