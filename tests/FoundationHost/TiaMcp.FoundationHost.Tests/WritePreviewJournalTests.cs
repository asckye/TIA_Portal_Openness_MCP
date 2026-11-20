extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class WritePreviewJournalTests
{
    [Fact]
    public void Refused_hooked_write_maps_to_failed_without_a_session_reset()
    {
        using var worker = new PlanWorker();
        using var session = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker));
        var result = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.Run("SetDeviceItemAttribute", () =>
            new toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseMessage { Message = "setter refused", Meta = new JsonObject {
                ["success"] = false, ["operationSuccess"] = false, ["rollbackCompleted"] = true,
                ["mayHaveChanged"] = false, ["cleanupRequired"] = new JsonArray() } }, true, false);
        Assert.Equal("failed", (string?)result.StructuredContent!["meta"]!["outcome"]);
        Assert.False((bool?)result.StructuredContent["data"]!["evidence"]!["mayHaveChanged"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compensation_results_distinguish_restored_failure_from_remaining_cleanup(bool restored)
    {
        var evidence = new JsonObject { ["rollbackCompleted"] = restored,
            ["cleanupRequired"] = restored ? new JsonArray() : new JsonArray(new JsonObject { ["target"] = "/HMI/Tags/Start" }) };
        Assert.True(TiaMcp.Logic.V4.NativeResultState.TryUnsuccessful(evidence, true, out var outcome, out var error));
        Assert.Equal(restored ? TiaMcp.Logic.V4.Outcome.Failed : TiaMcp.Logic.V4.Outcome.Unknown, outcome);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("EnsureUnifiedHmiButtonAction", "targetTag")]
    [InlineData("BindUnifiedHmiTagDynamization", "tagName")]
    public void Missing_unified_tag_reaches_the_client_with_its_name_and_zero_write_outcome(string tool, string parameter)
    {
        using var worker = new PlanWorker();
        using var scope = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker));
        var reply = new toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["status"] = "NotFound", ["parameter"] = parameter,
            ["target"] = "MissingTag", ["missingDeviceTarget"] = "MissingTag"
        } };
        var result = toolpipeline::TiaMcpServer.ModelContextProtocol.UnifiedHmiContract.Map(tool, reply, true, true).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)result["error"]?["code"]);
        Assert.Equal("MissingTag", (string?)result["error"]?["details"]?["target"]);
        Assert.Equal("rejected-before-operation", (string?)result["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }

    [Fact]
    public void Button_action_executes_in_one_worker_request_so_creation_and_script_share_rollback()
    {
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedHmiService((operation, arguments) => {
            calls++;
            Assert.Equal("hmi-unified.ExecuteUnifiedHmi", operation);
            Assert.Equal("EnsureUnifiedHmiButtonAction", (string?)arguments["request"]?["Operation"]);
            Assert.Equal("Start", (string?)arguments["request"]?["TagName"]);
            Assert.Equal("/Overview", (string?)arguments["request"]?["ScreenName"]);
            Assert.Equal("HMIRuntime.Tags(\"Start\").Write(true);", (string?)arguments["request"]?["ScriptCode"]);
            Assert.False((bool?)arguments["dryRun"]);
            return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "applied", ["Meta"] = new JsonObject { ["operationSuccess"] = true } } };
        }, () => "C:/Project.ap21");
        service.ApplyUnifiedHmiButtonAction("HMI", "/Overview", "Start", "Tapped", "Start", "HMIRuntime.Tags(\"Start\").Write(true);", false);
        Assert.Equal(1, calls);
    }

    private sealed class PlanWorker : toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal JsonObject Identity = new() { ["identity"] = new JsonObject { ["processId"] = 123, ["projectPath"] = "C:/Projects/original.ap21" } };
        public bool Faulted => false;
        public JsonNode? Binding => Identity;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<toolpipeline::TiaMcp.FoundationHost
            .EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }

}
