extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Engine = toolpipeline::TiaMcp.FoundationHost;

public sealed class ApprovalStatusTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Fact]
    public void Only_status_tolerates_a_missing_live_host_session()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var context = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker) { Ended = true });
        Assert.Throws<InvalidOperationException>(() => Host.CurrentApprovalSession());
        Assert.Null(Host.CurrentApprovalStatusSession());
        Assert.Throws<InvalidOperationException>(() => Host.OnlinePlanRefusal("WritePlcWebVars",
            new JsonObject { ["dryRun"] = false, ["expectedPlanHash"] = new string('a', 64) }));
        var status = Host.GetApprovalStatus().StructuredContent!["data"]!;
        Assert.Empty(status["pending"]!.AsArray());
        Assert.Empty(status["recent"]!.AsArray());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Status_is_host_read_on_every_release_and_answers_while_approval_waits(string release)
    {
        using var targets = new OnlineTargetSettingsScope("target=192.0.2.0/24\n");
        using var fixture = new StatusFixture(release);
        using var session = fixture.EnterSession();
        var catalog = new Engine.FoundationCatalog(release, Array.Empty<McpServerTool>());
        var descriptor = catalog.All["GetApprovalStatus"];
        Assert.Equal("host", descriptor.Execution);
        Assert.NotNull(descriptor.Classification);
        Assert.Equal("READ", descriptor.Classification.Operation);
        Assert.Equal("L0", descriptor.Classification.Level);
        Assert.Contains(catalog.Lite, tool => tool.Name == "GetApprovalStatus");
        Assert.False(TiaMcpServer.ModelContextProtocol.ToolTaxonomy.UsesOpennessLane("GetApprovalStatus"));
        var usage = await fixture.Call("GetToolUsage", new { toolName = "GetApprovalStatus" });
        Assert.False(usage.IsError, usage.StructuredContent?.ToJsonString());
        Assert.Contains("GetApprovalStatus", usage.StructuredContent!.ToJsonString(), StringComparison.Ordinal);

        var decision = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<PendingApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Host.ApprovalWaitOverrideForTests;
        Host.ApprovalWaitOverrideForTests = (pending, _, _) => { entered.SetResult(pending); return decision.Task; };
        Task<ApprovalOutcome?>? waiting = null;
        PendingApproval? pending = null;
        try
        {
            waiting = Host.WaitForApproval("WritePlcSimAdvancedTags", "{\"dryRun\":false,\"password\":\"synthetic-private\"}", CancellationToken.None);
            pending = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await fixture.Call("GetApprovalStatus", new { }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
            var data = result.StructuredContent!["data"]!;
            var row = Assert.Single(data["pending"]!.AsArray());
            Assert.Equal(pending.RequestId, (string?)row!["requestId"]);
            Assert.Equal("WritePlcSimAdvancedTags", (string?)row["tool"]);
            Assert.Equal("pending", (string?)row["outcome"]);
            Assert.NotNull(row["startedUtc"]);
            Assert.NotNull(row["deadline"]);
            Assert.False(waiting.IsCompleted);
            Assert.Equal("192.0.2.0/24", (string?)Assert.Single(data["onlineTargets"]!["entries"]!.AsArray()));
            Assert.Null(data["vault"]);
            var alwaysConfirmed = data["alwaysConfirmed"]!["tools"]!.AsArray();
            Assert.Contains(alwaysConfirmed, name => (string?)name == "WritePlcWebVars");
            Assert.Equal(release != "14sp1", alwaysConfirmed.Any(name => (string?)name == "DownloadPlc"));
            Assert.DoesNotContain("synthetic-private", data.ToJsonString());
            Assert.Equal(0, fixture.Worker.Calls);

            var keyOverride = Host.ApprovalSessionKeyForTests;
            try
            {
                Host.ApprovalSessionKeyForTests = () => new object();
                var other = Host.GetApprovalStatus().StructuredContent!["data"]!;
                Assert.Empty(other["pending"]!.AsArray());
                Assert.Empty(other["recent"]!.AsArray());
            }
            finally { Host.ApprovalSessionKeyForTests = keyOverride; }

            decision.SetResult(new ApprovalOutcome(pending, false, "denied"));
            await waiting;
            var after = (await fixture.Call("GetApprovalStatus", new { })).StructuredContent!["data"]!;
            Assert.Empty(after["pending"]!.AsArray());
            Assert.Equal("denied", (string?)Assert.Single(after["recent"]!.AsArray())!["outcome"]);
        }
        finally
        {
            if (pending != null) decision.TrySetResult(new ApprovalOutcome(pending, false, "cancelled"));
            if (waiting != null) await waiting;
            Host.ApprovalWaitOverrideForTests = previous;
        }
    }

    [Fact]
    public async Task Foundation_direct_approval_uses_the_same_session_board()
    {
        using var targets = new OnlineTargetSettingsScope(null);
        using var worker = new OnlinePortAdmissionTests.VaultOnlineWorker();
        var entered = new TaskCompletionSource<PendingApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker,
            approvalWait: (card, _, _) => { entered.SetResult(card); return decision.Task; });
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["targetIpAddress"] = "192.0.2.10",
            ["pgPcInterface"] = "PLCSIM", ["confirmPhysicalTarget"] = true, ["dryRun"] = true };
        var preview = await fixture.Call("DownloadPlc", args);
        args["dryRun"] = false;
        args["expectedPlanHash"] = preview.StructuredContent!["data"]!["planHash"]!.DeepClone();
        args["expectedProjectFile"] = "C:/Fixture/P.ap21";
        var call = fixture.Call("DownloadPlc", args);
        var pending = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var status = (await fixture.Call("GetApprovalStatus", new { }).WaitAsync(TimeSpan.FromSeconds(10))).StructuredContent!["data"]!;
            Assert.Equal(pending.RequestId, (string?)Assert.Single(status["pending"]!.AsArray())!["requestId"]);
        }
        finally { decision.TrySetResult(new ApprovalOutcome(pending, false, "denied")); await call; }
        var recent = (await fixture.Call("GetApprovalStatus", new { })).StructuredContent!["data"]!["recent"]!.AsArray();
        Assert.Equal("denied", (string?)Assert.Single(recent)!["outcome"]);
    }

    [Fact]
    public async Task Board_retains_only_the_last_sixteen_outcomes_and_cleans_cancelled_waits()
    {
        var key = new object();
        var outcomes = new[] { "granted", "denied", "timeout", "workbench-unavailable", "cancelled", "disabled" };
        for (int index = 0; index < 22; index++)
        {
            string outcome = outcomes[index % outcomes.Length];
            var pending = PendingApproval.Create("test", "21", "DownloadPlc", "{}", "{}", 120, index.ToString());
            await ApprovalStatusBoard.Wait(key, pending, () => Task.FromResult(new ApprovalOutcome(pending, outcome == "disabled",
                outcome is "granted" or "disabled" ? null : outcome)));
        }
        var status = ApprovalStatusBoard.Snapshot(key);
        Assert.Empty(status["pending"]!.AsArray());
        Assert.Equal(16, status["recent"]!.AsArray().Count);
        Assert.Equal("21", (string?)status["recent"]![0]!["requestId"]);
        Assert.Equal("3", (string?)status["recent"]![15]!["requestId"]);
        Assert.Equal(outcomes.Where(value => value != "disabled").OrderBy(value => value),
            status["recent"]!.AsArray().Select(row => (string)row!["outcome"]!).Distinct().OrderBy(value => value));
        status["recent"]![0]!["tool"] = "modified-snapshot";
        Assert.Equal("DownloadPlc", (string?)ApprovalStatusBoard.Snapshot(key)["recent"]![0]!["tool"]);

        var cancelled = PendingApproval.Create("test", "21", "DownloadPlc", "{}", "{}", 120);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ApprovalStatusBoard.Wait(key, cancelled,
            () => throw new OperationCanceledException()));
        var after = ApprovalStatusBoard.Snapshot(key);
        Assert.Empty(after["pending"]!.AsArray());
        Assert.Equal("cancelled", (string?)after["recent"]![0]!["outcome"]);
    }

    [Fact]
    public async Task Unreadable_settings_report_invalid_without_echoing_contents()
    {
        using var targets = new OnlineTargetSettingsScope("not-a-list");
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        var result = await fixture.Call("GetApprovalStatus", new { });
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        var data = result.StructuredContent!["data"]!;
        Assert.True((bool?)data["onlineTargets"]!["configured"]);
        Assert.False((bool?)data["onlineTargets"]!["valid"]);
        Assert.DoesNotContain("not-a-list", data.ToJsonString());
    }

    [Fact]
    public async Task Disabled_writes_cannot_evict_human_approval_outcomes()
    {
        var key = new object();
        var human = PendingApproval.Create("test", "21", "DownloadPlc", "{}", "{}", 120, "human");
        await ApprovalStatusBoard.Wait(key, human, () => Task.FromResult(new ApprovalOutcome(human, false, null)));
        string before = ApprovalStatusBoard.Snapshot(key).ToJsonString();
        for (int index = 0; index < 100; index++)
        {
            var disabled = PendingApproval.Create("test", "21", "WritePlcSimAdvancedTags", "{}", "{}", 120);
            await ApprovalStatusBoard.Wait(key, disabled, () => Task.FromResult(new ApprovalOutcome(disabled, true, null)));
        }
        Assert.Equal(before, ApprovalStatusBoard.Snapshot(key).ToJsonString());
    }

    private sealed class StatusFixture : IDisposable
    {
        private readonly string previousRelease = Engine.EngineHostConfiguration.ReleaseKey;
        internal readonly HardwareAddressingAdmissionTests.UnboundWorker Worker = new();
        private readonly Engine.EngineHostPipeline pipeline;
        private readonly IReadOnlyList<McpServerTool> tools;
        internal StatusFixture(string release)
        {
            IReadOnlyList<McpServerTool>? published = null;
            var registry = TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(Worker, release, false,
                usageRoster: () => published!, hostPorts: false);
            pipeline = new Engine.EngineHostPipeline(release, Worker, registry);
            tools = published = pipeline.AllTools;
        }
        internal IDisposable EnterSession() => pipeline.EnterSession();
        internal Task<CallToolResult> Call(string name, object args) => tools.Single(tool => tool.ProtocolTool.Name == name).InvokeAsync(
            new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, VersionControlAdmissionTests.ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.SerializeToElement(args).Deserialize<Dictionary<string, JsonElement>>() }
            }).AsTask();
        public void Dispose()
        {
            pipeline.Dispose();
            Worker.Dispose();
            Engine.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
    }
}
