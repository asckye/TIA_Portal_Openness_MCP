extern alias foundationhost;
extern alias toolpipeline;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Session = foundationhost::TiaMcp.FoundationHost.HostSession;
using ResponseGuard = toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseGuardTool;

[Collection(BundleSelectionCollection.Name)]
public sealed class CallTimingTests
{
    [Fact]
    public async Task Host_preview_reports_elapsed_time_and_preserves_the_domain_plan_hash()
    {
        var clock = new FixtureSessionClock();
        var output = Path.Combine(AppContext.BaseDirectory, "MCP_B5_preview.xml");
        string PreviewHash() => TiaMcp.Adapters.PlcSpecialExportPolicy.Run("watch-table", "19", "C:/fixture.ap19", "devices/PLC_1/PLC_1",
            "G/WT", output, true, true, "", () => throw new InvalidOperationException(), _ => throw new InvalidOperationException()).PlanHash;
        var expected = PreviewHash();
        var result = await Invoke(clock, () => {
            clock.Advance(TimeSpan.FromMilliseconds(13));
            return Task.FromResult(FoundationV4Result.Composite("19", "ExportPlcWatchTable", "preview",
                new JsonObject { ["planHash"] = PreviewHash() }, Outcome.Succeeded, Execution.ReadOnly));
        });
        var body = result.StructuredContent!;
        Assert.Equal(expected, (string?)body["data"]?["planHash"]);
        Assert.Equal(13, (long?)body["meta"]?["elapsedMs"]);
        Assert.False(body["meta"]!.AsObject().ContainsKey("approvalWaitMs"));
        Assert.False(body["meta"]!.AsObject().ContainsKey("workerElapsedMs"));
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text)));
        Assert.Equal(13, V4Json.Deserialize<Envelope>(body.ToJsonString()).Meta.ElapsedMs);

        PendingApproval Card(string token) => PendingApproval.Create("foundation", "19", "ExportPlcWatchTable",
            new JsonObject { ["expectedPlanHash"] = token, ["exportPath"] = output, ["dryRun"] = false }.ToJsonString(),
            """{"projectPath":"C:/fixture.ap19"}""", 120, (string)body["meta"]!["requestId"]!);
        var originalCard = Card(expected);
        var timedCard = Card((string)body["data"]!["planHash"]!);
        timedCard.Validate();
        Assert.Equal(expected, timedCard.PlanHash);
        var grant = new ApprovalDecision { RequestId = originalCard.RequestId, PlanHash = originalCard.PlanHash,
            ArgumentDigest = originalCard.ArgumentDigest, Decision = "granted" };
        Assert.True(grant.Matches(timedCard));
        Assert.False(grant.Matches(Card(new string('0', 64))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCallResult_preserves_the_original_detached_calls_timing(bool lateFailure)
    {
        const string name = "SaveProject", id = "original-request";
        var clock = new FixtureSessionClock();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new SharedPipelineTests.Worker();
        var late = ((Engine.IEngineWorker)worker).LateResults;
        using var engine = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker));
        var original = new FixtureTool(async () => {
            clock.Advance(TimeSpan.FromMilliseconds(12));
            using (ToolCallTiming.BeginApprovalWait())
            {
                Engine.LateCalls.Detach(new JsonObject { ["requestId"] = id, ["tool"] = name });
                await resume.Task.WaitAsync(TestDeadlines.Hang);
            }
            clock.Advance(TimeSpan.FromMilliseconds(71));
            ToolCallTiming.RecordWorkerReply(49);
            if (lateFailure) throw new InvalidOperationException("Detached fixture failed");
            return FoundationV4Result.Composite("19", name, id, new JsonObject(), Outcome.Succeeded, Execution.Completed);
        }, name);
        var lookup = new FixtureTool(() => Task.FromResult(toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.GetCallResult(id)), "GetCallResult");
        using var session = new Session("19", "", true, [], [original, lookup], () => new JsonObject(), () => { },
            lateResults: late, unsettled: worker.Unsettled, clock: clock);
        var interim = await InvokeTool(session, name);
        Assert.Equal("in-progress", (string?)interim.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal(12, (long?)interim.StructuredContent?["meta"]?["elapsedMs"]);
        var polling = await InvokeTool(session, "GetCallResult");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += () => { if (late.Running == null) completed.TrySetResult(); };
        clock.Advance(TimeSpan.FromMilliseconds(61));
        resume.SetResult();
        await completed.Task.WaitAsync(TestDeadlines.Hang);
        clock.Advance(TimeSpan.FromMinutes(3));
        var read = await InvokeTool(session, "GetCallResult");
        var body = read.StructuredContent!;
        Assert.Equal(lateFailure, read.IsError);
        Assert.Equal(name, (string?)body["meta"]?["tool"]);
        Assert.Equal(id, (string?)body["meta"]?["requestId"]);
        Assert.Equal(83, (long?)body["meta"]?["elapsedMs"]);
        Assert.Equal(61, (long?)body["meta"]?["approvalWaitMs"]);
        Assert.Equal(49, (long?)body["meta"]?["workerElapsedMs"]);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(Assert.IsType<TextContentBlock>(Assert.Single(read.Content)).Text)));
        Assert.Equal("in-progress", (string?)polling.StructuredContent?["meta"]?["outcome"]);
        Assert.False(polling.StructuredContent!["meta"]!.AsObject().ContainsKey("elapsedMs"));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("granted")]
    public async Task Approval_excludes_only_the_wait_for_the_decision(string answer)
    {
        var clock = new FixtureSessionClock();
        var (host, workbench) = OperatorSessionTestsPlc.Streams();
        using var client = host;
        using var server = workbench;
        using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
        async Task Decide()
        {
            var received = await ApprovalFrames.Read<PendingApproval>(server, deadline.Token);
            clock.Advance(TimeSpan.FromMilliseconds(8000));
            await ApprovalFrames.Write(server, new ApprovalDecision { RequestId = received.RequestId, PlanHash = received.PlanHash,
                ArgumentDigest = received.ArgumentDigest, Decision = answer }, deadline.Token);
        }
        var decision = Decide();
        var result = await Invoke(clock, async () => {
            clock.Advance(TimeSpan.FromMilliseconds(11));
            var pending = PendingApproval.Create("foundation", "19", "SaveProject", "{}", null, 120);
            var reason = await ApprovalClient.RequestDecision(client, pending, deadline.Token);
            Assert.Equal(answer == "denied" ? "denied" : null, reason);
            clock.Advance(TimeSpan.FromMilliseconds(4));
            return reason == null
                ? FoundationV4Result.Composite("19", "SaveProject", pending.RequestId, new JsonObject(), Outcome.Succeeded, Execution.Completed)
                : FoundationV4Result.Reject("19", "SaveProject", pending.RequestId,
                    HostBehavior.Confirmation(reason, pending.PlanHash, pending.RequestId, false));
        });
        Assert.Equal(answer == "denied", result.IsError);
        Assert.True(result.StructuredContent != null, ((TextContentBlock)result.Content[0]).Text);
        await decision.WaitAsync(deadline.Token);
        Assert.Equal(8000, (long?)result.StructuredContent?["meta"]?["approvalWaitMs"]);
        Assert.Equal(15, (long?)result.StructuredContent?["meta"]?["elapsedMs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_lookup_uses_the_stored_identity_even_when_the_original_tool_has_the_same_name(bool bridge)
    {
        using var fixture = new OperatorSessionTestsPlc.Fixture("19");
        using var session = await fixture.Create();
        await using var peer = await OperatorSessionTestsPlc.Peer.Ai(session);
        const string id = "stored-lookup";
        Task<JsonObject> Read(string requestId, bool ok)
        {
            var args = new Dictionary<string, object?> { ["requestId"] = requestId };
            return bridge ? peer.Tool("CallTool", new() { ["name"] = "GetCallResult", ["arguments"] = args }, ok)
                : peer.Tool("GetCallResult", args, ok);
        }
        using var original = session.LateResults.CreateInvocation("GetCallResult", id, CancellationToken.None);
        using (original.Enter()) Engine.LateCalls.Detach(new JsonObject { ["tool"] = "GetCallResult", ["requestId"] = id });
        var running = await Read(id, false);
        Assert.Equal(id, (string?)running["meta"]?["requestId"]);
        Assert.False(running["meta"]!.AsObject().ContainsKey("elapsedMs"));
        var clock = new FixtureSessionClock();
        var result = await Invoke(clock, () => {
            clock.Advance(TimeSpan.FromMilliseconds(99));
            ToolCallTiming.RecordWorkerReply(72);
            return Task.FromResult(FoundationV4Result.Composite("19", "GetCallResult", id, new JsonObject(), Outcome.Succeeded, Execution.ReadOnly));
        });
        original.Complete(result);
        var retained = await Read(id, true);
        Assert.Equal(id, (string?)retained["meta"]?["requestId"]);
        Assert.Equal(99, (long?)retained["meta"]?["elapsedMs"]);
        Assert.Equal(72, (long?)retained["meta"]?["workerElapsedMs"]);
        var missing = await Read("missing-result", false);
        Assert.Equal("NOT_FOUND", (string?)missing["error"]?["code"]);
        Assert.True((long?)missing["meta"]?["elapsedMs"] >= 0);
    }

    [Theory]
    [InlineData(17, 54)]
    [InlineData(null, null)]
    public async Task Worker_time_requires_a_reported_duration_for_every_reply(int? second, int? total)
    {
        var result = await Invoke(new FixtureSessionClock(), () => {
            ToolCallTiming.RecordWorkerReply(37);
            ToolCallTiming.RecordWorkerReply(second);
            return Task.FromResult(FoundationV4Result.Composite("19", "GetSessionState", "timing",
                new JsonObject(), Outcome.Succeeded, Execution.ReadOnly));
        });
        var meta = result.StructuredContent!["meta"]!.AsObject();
        Assert.Equal(total.HasValue, meta.ContainsKey("workerElapsedMs"));
        Assert.Equal((long?)total, V4Json.Deserialize<Envelope>(result.StructuredContent.ToJsonString()).Meta.WorkerElapsedMs);
    }

    [Fact]
    public async Task Timed_export_page_stays_within_the_served_text_limit()
    {
        using var worker = new SharedPipelineTests.Worker();
        var context = new Engine.EngineSessionContext(worker);
        using var exports = context.Exports;
        using var session = Engine.EngineHostConfiguration.Enter(context);
        var result = await Invoke(new FixtureSessionClock(), () => {
            var large = FoundationV4Result.Composite("19", "GetSessionState", "page",
                new JsonObject { ["value"] = new string('a', 24000) }, Outcome.Succeeded, Execution.ReadOnly);
            return Task.FromResult(ResponseGuard.Shrink(large, "GetSessionState", "fixture"));
        });
        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var body = result.StructuredContent!;
        Assert.True(text.Length <= 20000);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(text)));
        Assert.Equal("GetExportContent", (string?)body["data"]?["continuation"]?["name"]);
        Assert.Equal(0, (long?)body["meta"]?["elapsedMs"]);
    }

    [Fact]
    public async Task Dispatched_worker_reply_contributes_time_to_the_served_call()
    {
        using var fixture = new OperatorSessionTestsPlc.Fixture("19");
        using var session = await fixture.Create();
        await using var peer = await OperatorSessionTestsPlc.Peer.Ai(session);
        var body = await peer.Tool("ConnectPortal", new() { ["processId"] = 101 });
        Assert.True((long?)body["meta"]?["workerElapsedMs"] >= 0);
        Assert.True((long?)body["meta"]?["elapsedMs"] >= 0);
    }

    [Fact]
    public async Task Disclosure_warnings_and_export_paging_preserve_an_existing_measurement()
    {
        using var worker = new SharedPipelineTests.Worker();
        var context = new Engine.EngineSessionContext(worker);
        using var exports = context.Exports;
        using var session = Engine.EngineHostConfiguration.Enter(context);
        var meta = new Meta(DateTimeOffset.UtcNow, "19", "GetSessionState", "prior", Outcome.Succeeded, Execution.ReadOnly,
            BehaviorPolicy.NotApplicable, Completeness.Complete, null, [], 91, 19, 87);
        var original = Envelope.Create(new { value = new string('a', 24000) }, null, meta);
        var disclosed = BehaviorCapabilities.Disclose(original, currentTarget: true);
        Assert.Equal(BehaviorPolicy.Current, disclosed.Meta.BehaviorPolicy);
        var body = EnvelopeMutation.Decorate(JsonNode.Parse(V4Json.Serialize(disclosed))!, true, null);
        var retained = new CallToolResult { StructuredContent = body, Content = [new TextContentBlock { Text = body.ToJsonString() }] };
        var page = await Invoke(new FixtureSessionClock(), () => Task.FromResult(ResponseGuard.Shrink(retained, "GetSessionState", "fixture")));
        var visible = V4Json.Deserialize<Envelope>(Assert.IsType<TextContentBlock>(Assert.Single(page.Content)).Text);
        Assert.NotNull(visible.Meta.Paging);
        Assert.Equal("prior", visible.Meta.RequestId);
        Assert.Equal(91, visible.Meta.ElapsedMs);
        Assert.Equal(19, visible.Meta.ApprovalWaitMs);
        Assert.Equal(87, visible.Meta.WorkerElapsedMs);
        Assert.True(JsonNode.DeepEquals(page.StructuredContent, JsonNode.Parse(V4Json.Serialize(visible))));
    }

    [Fact]
    public void Meta_schema_accepts_each_timing_as_an_optional_integer()
    {
        var meta = EnvelopeSchema.Value.GetProperty("properties").GetProperty("meta");
        foreach (var name in new[] { "elapsedMs", "approvalWaitMs", "workerElapsedMs" })
        {
            var field = meta.GetProperty("properties").GetProperty(name);
            Assert.Equal("integer", field.GetProperty("type").GetString());
            Assert.DoesNotContain(meta.GetProperty("required").EnumerateArray(), value => value.GetString() == name);
        }
    }

    private static async Task<CallToolResult> Invoke(FixtureSessionClock clock, Func<Task<CallToolResult>> invoke)
    {
        var tool = new FixtureTool(invoke);
        using var session = new Session("19", "", true, [], [tool], () => new JsonObject(), () => { }, clock: clock);
        return await InvokeTool(session, tool.ProtocolTool.Name);
    }

    private static async Task<CallToolResult> InvokeTool(Session session, string name)
    {
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new CallToolRequestParams { Name = name } };
        return await session.OperatorTools.Single(tool => tool.ProtocolTool.Name == name).InvokeAsync(request);
    }

    private sealed class FixtureTool(Func<Task<CallToolResult>> invoke, string name = "TimingFixture") : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new() { Name = name };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            => new(invoke());
    }
}
