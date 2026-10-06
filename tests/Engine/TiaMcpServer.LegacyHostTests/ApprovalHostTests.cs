using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaOpenness.Shared;
using Xunit;

public sealed class ApprovalHostTests
{
    [Theory]
    [InlineData("SaveProject", "{}")]
    [InlineData("CloseProject", "{}")]
    [InlineData("CompileSoftware", "{\"softwarePath\":\"PLC\"}")]
    [InlineData("ImportPlcExternalSource", "{\"sourceName\":\"fixture.scl\",\"filePath\":\"C:/fixture.scl\"}")]
    [InlineData("AttachToOpenProject", "{\"processId\":1,\"processStartUtc\":\"2026-10-05T00:00:00Z\",\"projectPath\":\"C:/fixture.ap19\"}")]
    public async Task Candidate_apply_waits_before_worker_dispatch_in_every_merged_family(string source, string json)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", _ => TiaMcp.Logic.V4.BehaviorPolicy.SafeV4, () => new ApprovalSettings(true, 1));
        var args = JsonNode.Parse(json)!.AsObject(); args["mode"] = "apply"; args["confirm"] = true;
        args["expectedPlanHash"] = new string('a', 64); args["expectedProjectFile"] = "C:/fixture.ap19";
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args.ToJsonString()));
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("workbench-unavailable", (string?)result.StructuredContent?["error"]?["details"]?["reason"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("SaveProject")]
    [InlineData("CloseProject")]
    public async Task Current_session_save_and_close_are_approved_without_changing_catalog_policy(string source)
    {
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        try
        {
            var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
            var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
            var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"dryRun\":false}"));
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Equal(0, worker.Calls);

            tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(false, 1));
            result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"dryRun\":false}"));
            Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), row => (string?)row?["code"] == "APPROVAL_DISABLED");
        }
        finally { before.Save(ApprovalSettings.SettingsPath); }
    }

    [Theory]
    [InlineData("Connect", "{\"processId\":123}")]
    [InlineData("Disconnect", "{}")]
    public async Task Foundation_connect_and_disconnect_remain_ungated(string source, string arguments)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, arguments));
        Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True(worker.Calls > 0);
    }

    [Theory]
    [InlineData("SaveProject")]
    [InlineData("CloseProject")]
    public async Task Approved_foundation_session_candidate_passes_the_fake_workbench_gate(string source)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == source);
        bool approved = false;
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", _ => BehaviorPolicy.SafeV4,
            () => new ApprovalSettings(true, 2), (pending, _, _) =>
            {
                Assert.Equal(source, pending.Tool);
                approved = true;
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            });
        var args = new JsonObject { ["mode"] = "apply", ["confirm"] = true,
            ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = @"C:\fixture.ap19" };
        var invocation = await tool.InvokeAsync(Request(source, args.ToJsonString()));
        Assert.True(approved);
        Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)invocation.StructuredContent?["error"]?["code"]);
        Assert.True(worker.Calls > 0);
    }
    private sealed class Worker : IFoundationWorker
    {
        public int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        { Calls++; return Task.FromResult<JsonNode?>(new JsonObject()); }
        public void Dispose() { }
    }
    private static RequestContext<CallToolRequestParams> Request(string tool, string json)
        => new(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>())
        { Params = new CallToolRequestParams { Name = tool, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_is_refused_without_workbench_before_any_worker_call(bool dryRun)
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportBlock");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request("ImportPlcBlock", "{\"softwarePath\":\"PLC\",\"groupPath\":\"\",\"importPath\":\"C:\\\\fixture.xml\",\"dryRun\":" + dryRun.ToString().ToLowerInvariant() + "}"));
        var body = result.StructuredContent!;
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
        Assert.Equal("workbench-unavailable", (string?)body["error"]?["details"]?["reason"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal((string?)body["meta"]?["requestId"], (string?)body["error"]?["details"]?["requestId"]);
        Assert.Equal(0, worker.Calls);
    }
    [Fact]
    public async Task Invalid_input_retains_its_admission_error_before_approval()
    {
        var worker = new Worker(); var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportBlock");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request("ImportPlcBlock", "{\"unexpected\":true}"));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, worker.Calls);
    }
}
