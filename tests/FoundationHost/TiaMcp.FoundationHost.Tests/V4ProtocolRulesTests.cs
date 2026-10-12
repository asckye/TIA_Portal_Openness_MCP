using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;
using Argument = TiaMcp.FoundationHost.Argument;

public sealed class V4ProtocolRulesTests
{
    [Theory]
    [InlineData("ImportDeviceAml", "{\"dryRun\":false}", true)]
    [InlineData("ImportDeviceAml", "{}", false)]
    [InlineData("ImportDeviceAml", "{\"dryRun\":true}", false)]
    [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\",\"dryRun\":false}", true)]
    [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"export\",\"dryRun\":false}", false)]
    [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\"}", false)]
    [InlineData("ExchangeSystemDiagnosticsSettings", "{\"action\":\"import\",\"dryRun\":true}", false)]
    [InlineData("ExchangeUnifiedTags", "{\"action\":\"import\",\"dryRun\":false}", true)]
    [InlineData("ExchangeUnifiedTags", "{\"action\":\"export\",\"dryRun\":false}", false)]
    [InlineData("ExchangeUnifiedTags", "{\"action\":\"import\"}", false)]
    [InlineData("ExchangeUnifiedTags", "{\"action\":\"import\",\"dryRun\":true}", false)]
    [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"import\",\"dryRun\":false}", true)]
    [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"export\",\"dryRun\":false}", false)]
    [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"import\"}", false)]
    [InlineData("ExchangeUnifiedScriptModules", "{\"action\":\"import\",\"dryRun\":true}", false)]
    [InlineData("RunPlcSimAdvancedTestScenario", "{\"dryRun\":false}", true)]
    [InlineData("RunPlcSimAdvancedTestScenario", "{\"dryRun\":true}", false)]
    [InlineData("RunPlcSimAdvancedTestScenario", "{}", false)]
    [InlineData("RunTestSuiteCase", "{\"dryRun\":false}", true)]
    [InlineData("RunTestSuiteCase", "{\"dryRun\":true}", false)]
    [InlineData("RunTestSuiteCase", "{}", false)]
    [InlineData("EnsureOpennessUserGroup", "{}", true)]
    [InlineData("GetEnvironmentDiagnostics", "{\"fix\":true}", true)]
    [InlineData("GetEnvironmentDiagnostics", "{\"fix\":false}", false)]
    [InlineData("GetEnvironmentDiagnostics", "{}", false)]
    public async Task Foundation_mutations_wait_for_approval_and_read_actions_do_not(string name, string json, bool gated)
    {
        bool preview = name is "ImportDeviceAml" or "ExchangeSystemDiagnosticsSettings" or "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "RunPlcSimAdvancedTestScenario" or "RunTestSuiteCase";
        var arguments = new List<Argument>();
        if (preview) arguments.Add(new Argument("dryRun", "boolean", false, true));
        if (name.StartsWith("Exchange", StringComparison.Ordinal)) arguments.Add(new Argument("action"));
        if (name == "GetEnvironmentDiagnostics") arguments.Add(new Argument("fix", "boolean", false, false));
        var worker = new ApprovalWorker(); int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(new Definition(name, "ApprovalFixture", "Offline approval fixture.", arguments.ToArray()), worker), "19", null,
            () => new ApprovalSettings(true, 1), (pending, _, _) => {
                Assert.Equal(name, pending.Tool); waits++;
                return Task.FromResult(new ApprovalOutcome(pending, false, "denied")); });
        var args = JsonNode.Parse(json)!.AsObject();
        if (preview && (bool?)args["dryRun"] == false) { args["confirm"] = true; args["expectedProjectFile"] = "fixture.ap19"; }
        var request = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal(gated ? "CONFIRMATION_REQUIRED" : null, (string?)result.StructuredContent!["error"]?["code"]);
        Assert.Equal(gated ? 1 : 0, waits);
        Assert.Equal(gated && !preview ? 0 : 1, worker.Calls);
        if (preview) Assert.Equal(new[] { gated || (bool?)args["dryRun"] != false }, worker.PreviewCalls);
    }
    private sealed class ApprovalWorker : IFoundationWorker
    {
        internal int Calls;
        internal readonly List<bool> PreviewCalls = new();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        { Calls++; if (arguments["dryRun"] is JsonValue preview) PreviewCalls.Add(preview.GetValue<bool>()); return Task.FromResult<JsonNode?>(new JsonObject { ["success"] = true, ["Executed"] = arguments["dryRun"]?.GetValue<bool>() != true }); }
        public void Dispose() { }
    }
    [Theory]
    [InlineData(false, "rejected-before-operation", "not-started", "tool-queue")]
    [InlineData(true, "read-failed", "read-only", "tool-call")]
    public void Read_cancellation_reports_the_actual_dispatch_stage(bool dispatched, string outcome, string execution, string stage)
    {
        var result = FoundationV4Result.Failure("19", "GetProjectInfo", "id", dispatched, false, null, new OperationCanceledException());
        var body = result.StructuredContent!;
        Assert.Equal("CANCELLED", (string?)body["error"]!["code"]);
        Assert.Equal(stage, (string?)body["error"]!["details"]!["stage"]);
        Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
        Assert.Equal(execution, (string?)body["meta"]!["execution"]);
        V4Json.Deserialize<Envelope>(body.ToJsonString());
    }

    [Fact]
    public void Invalid_update_repository_is_an_argument_refusal_with_stable_message()
    {
        var raw = new JsonObject { ["success"] = false, ["summary"] = "repository must be 'owner/name'." };
        var args = new Dictionary<string, JsonElement> { ["repository"] = JsonSerializer.SerializeToElement("x") };
        var result = FoundationV4Result.Host("19", "CheckProductUpdate", "id", raw, false, args: args);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal("repository", (string?)result.StructuredContent!["error"]!["details"]!["parameter"]);
        Assert.Equal("The operation did not establish success; inspect the returned result.", (string?)result.StructuredContent!["error"]!["message"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent!["meta"]!["outcome"]);
    }

    [Fact]
    public void Native_capability_limit_is_used_and_output_schema_is_shared()
    {
        var result = FoundationV4Result.Reject("19", "CompileDevice", "id",
            new Error("Unavailable.", new UnsupportedCapabilityDetails("19", "compile", "read")));
        Assert.Contains(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "NATIVE_CAPABILITY_LIMIT");
        Assert.Equal(EnvelopeSchema.Value.GetRawText(), FoundationV4Result.Schema.GetRawText());
        var meta = FoundationV4Result.Schema.GetProperty("properties").GetProperty("meta");
        Assert.False(meta.TryGetProperty("additionalProperties", out _));
    }
}
