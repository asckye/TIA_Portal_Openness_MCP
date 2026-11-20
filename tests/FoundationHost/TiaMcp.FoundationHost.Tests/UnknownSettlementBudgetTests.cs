extern alias foundationhost;
extern alias toolpipeline;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using EngineHost = toolpipeline::TiaMcp.FoundationHost;

namespace TiaMcp.FoundationHost.Tests;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class UnknownSettlementBudgetTests
{
    public static IEnumerable<object[]> Releases() => NoLockTests.Releases;
    public static IEnumerable<object[]> Dialogs() => Releases().SelectMany(row => new[] { false, true }
        .Select(dialog => new object[] { row[0], dialog }));

    [Theory, MemberData(nameof(Releases))]
    public async Task A_reported_portal_loss_defers_the_read_until_exact_rebind(string release)
    {
        using var scope = new Scope(release, false, "tia-lost");
        var definition = Foundation.FoundationTools.Definitions.Single(tool => tool.Name == "CreatePlcTagTable");
        var tool = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(definition, scope.Worker), release, () => new(false, 1),
            readinessForTest: () => {
                scope.Worker.Call("FaultFixtureArm", new() { ["method"] = "adapter.CreateTagTable" }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                return new JsonObject { ["ready"] = true };
            });
        var services = Foundation.FoundationHostPorts.CreateServices(scope.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new EngineHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        var result = await tool.InvokeAsync(Request("CreatePlcTagTable", new() { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "SettledTable",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = scope.Project }));
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True(scope.Worker.RecoveryNeeded);
        Assert.Null(result.StructuredContent?["data"]?["settlement"]);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.DoesNotContain(File.ReadAllLines(scope.Trace), line => (string?)JsonNode.Parse(line)?["method"] == "adapter.ReadTagTableNames");
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_dialog_read_that_finishes_keeps_the_unknown_envelope_and_its_settlement(string release)
    {
        using var scope = new Scope(release, true);
        var definition = Foundation.FoundationTools.Definitions.Single(tool => tool.Name == "CreatePlcTagTable")
            with { Operation = "FixtureAnsweredUnknown" };
        var tool = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(definition, scope.Worker), release, () => new(false, 1));
        var services = Foundation.FoundationHostPorts.CreateServices(scope.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new EngineHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        using var session = new Foundation.HostSession(release, "fixture", true, new[] { tool }, new[] { tool }, services.OperatorSnapshot,
            () => { }, lateResults: scope.Worker.LateResults, unsettled: scope.Worker.Unsettled);
        var pending = session.OperatorTools.Single().InvokeAsync(Request("CreatePlcTagTable", new() {
            ["plc"] = "PLC_1", ["group"] = "", ["name"] = "SettledTable", ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = scope.Project })).AsTask();
        await scope.WaitDispatched();
        await scope.WaitDialog();
        Assert.Null(scope.Worker.Running);
        Assert.Null(typeof(Foundation.WorkerClient).GetField("runningCall", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Worker));
        Assert.Null(scope.Worker.LateResults.Running);
        scope.Release();
        var result = await pending.WaitAsync(TestDeadlines.Hang);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.NotEqual("in-progress", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("applied", (string?)result.StructuredContent?["data"]?["settlement"]?["outcome"]);
        Assert.Empty(scope.Worker.Unsettled.Snapshot());
        Assert.Null(session.Snapshot()["running"]);
        Assert.False((bool?)session.Snapshot()["callInProgress"]);
    }

    [Theory, MemberData(nameof(Dialogs))]
    public async Task Optional_read_cannot_detach_the_ended_write_or_publish_a_running_call(string release, bool dialog)
    {
        using var scope = new Scope(release, dialog);
        var definition = Foundation.FoundationTools.Definitions.Single(tool => tool.Name == "CreatePlcTagTable")
            with { Operation = "FixtureAnsweredUnknown" };
        var tool = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(definition, scope.Worker), release, () => new(false, 1));
        var services = Foundation.FoundationHostPorts.CreateServices(scope.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new EngineHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        using var session = new Foundation.HostSession(release, "fixture", true, new[] { tool }, new[] { tool }, services.OperatorSnapshot,
            () => { }, lateResults: scope.Worker.LateResults, unsettled: scope.Worker.Unsettled);
        var request = Request("CreatePlcTagTable", new() { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "SettledTable",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = scope.Project });
        var pending = session.OperatorTools.Single().InvokeAsync(request).AsTask();
        await scope.WaitDispatched();
        var result = await pending.WaitAsync(TestDeadlines.Hang);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.NotEqual("in-progress", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Null(result.StructuredContent?["data"]?["settlement"]);
        Assert.Null(scope.Worker.Running);
        Assert.Null(typeof(Foundation.WorkerClient).GetField("runningCall", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Worker));
        Assert.Null(scope.Worker.LateResults.Running);
        Assert.Null(session.Snapshot()["running"]);
        Assert.False((bool?)session.Snapshot()["callInProgress"]);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Empty(scope.Worker.Unsettled.SettledJson());
        Assert.True(scope.Worker.CallRunning);
        Assert.False(File.Exists(Path.Combine(scope.Root, "release")));
        var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("worker-call-running", refused.Condition);
        Assert.Contains("settlement read", refused.Message);
        Assert.DoesNotContain("GetCallResult", refused.Message);
        var handover = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            scope.Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId, ["takeOver"] = true }, CancellationToken.None));
        Assert.Equal("worker-call-running", handover.Condition);
        Assert.Contains("settlement read", handover.Message);
        Assert.False((bool)typeof(Foundation.WorkerClient).GetField("handoverPending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scope.Worker)!);
        Assert.Equal(1, File.ReadAllLines(scope.Trace).Count(line => (string?)JsonNode.Parse(line)?["method"] == "adapter.FixtureAnsweredUnknown"));
        scope.Release();
        await scope.Worker.RecoveryDrain.WaitAsync(TestDeadlines.Hang);
        Assert.Null(scope.Worker.Running);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Empty(scope.Worker.Unsettled.SettledJson());
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
    }

    [Fact]
    public async Task One_rebind_attempts_one_entry_and_later_rebinds_reach_the_remaining_entries()
    {
        var calls = new EngineHost.UnsettledCalls();
        var results = new EngineHost.LateCalls();
        var args = new JsonObject { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "SettledTable" };
        calls.Record("no-row", "CompilePlcSoftware", "compile", DateTimeOffset.UtcNow, args);
        calls.Record("first", "CreatePlcTagTable", "first", DateTimeOffset.UtcNow, args);
        calls.Record("second", "CreatePlcTagTable", "second", DateTimeOffset.UtcNow, args);
        int reads = 0;
        await EngineHost.UnknownSettlement.RunPending(calls, results, (_, _) => {
            reads++;
            throw new IOException("Synthetic unavailable evidence.");
        });
        Assert.Equal(1, reads);
        Assert.Equal(3, calls.Snapshot().Count);
        await EngineHost.UnknownSettlement.RunPending(calls, results, (_, _) => {
            reads++;
            return Task.FromResult<JsonNode?>(new JsonArray("SettledTable"));
        });
        Assert.Equal(2, reads);
        Assert.Equal("second", (string?)Assert.Single(calls.SettledJson())!["requestId"]);
        Assert.Equal(new[] { "no-row", "first" }, calls.Snapshot().Select(call => call.RequestId));
        Assert.Equal(TimeSpan.FromSeconds(5), EngineHost.UnknownSettlement.ReadBudget);
        Assert.Equal(1, EngineHost.UnknownSettlement.RecoveryLimit);
    }

    [Fact]
    public async Task Missing_requested_source_basename_leaves_both_identical_write_guards_in_place()
    {
        var calls = new EngineHost.UnsettledCalls();
        var results = new EngineHost.LateCalls();
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["groupPath"] = "", ["filePath"] = "C:/fixture/Source.scl" };
        calls.Record("import", "ImportPlcExternalSource", "digest", DateTimeOffset.UtcNow, args);
        results.RecordUnknown("import", "ImportPlcExternalSource", "digest",
            Foundation.FoundationV4Result.LateFailure("21", "ImportPlcExternalSource", "import"));
        var settlement = await EngineHost.UnknownSettlement.Run(calls, results, "import", (_, _) =>
            Task.FromResult<JsonNode?>(new JsonArray("NormalisedSource")));
        Assert.Null(settlement);
        Assert.Single(calls.Snapshot());
        Assert.Empty(calls.SettledJson());
        Assert.Equal("import", calls.FindWrite("digest")?.RequestId);
        Assert.Equal("import", results.FindUnread("digest"));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Recovery_settlement_honours_cancellation_before_the_requested_operation(string release)
    {
        using var scope = new Scope(release, false, bindProject: false);
        using var request = Foundation.FoundationWorkerRequest.Use("lost-create", "CreatePlcTagTable");
        await scope.Worker.Call("FaultFixtureArm", new() { ["method"] = "adapter.CreateTagTable" }, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => scope.Worker.Call("CreateTagTable", new() { ["dryRun"] = false }, CancellationToken.None));
        Assert.True(scope.Worker.Faulted);
        scope.Worker.Unsettled.Record("lost-create", "CreatePlcTagTable", "digest", DateTimeOffset.UtcNow,
            new() { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "SettledTable" });
        scope.ClearCrash();
        using var cancellation = new CancellationTokenSource(TestDeadlines.Hang);
        var next = scope.Worker.Call("ReadBlocks", new(), cancellation.Token);
        await scope.WaitDispatched();
        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TestDeadlines.Hang));
        Assert.False((bool?)canceled.Data["foundationRequestSent"]);
        Assert.True((bool?)canceled.Data["workerRecoveryRejected"]);
        Assert.Single(scope.Worker.Unsettled.Snapshot());
        Assert.Empty(scope.Worker.Unsettled.SettledJson());
        Assert.Null(scope.Worker.Running);
        Assert.DoesNotContain(File.ReadAllLines(scope.Trace), line => (string?)JsonNode.Parse(line)?["method"] == "adapter.ReadBlocks");
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) => new(
        DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };

    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();
        private readonly string image;
        internal readonly string Root = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build",
            "settlement-budget-" + Guid.NewGuid().ToString("N"));
        internal string Trace => Path.Combine(Root, "trace.jsonl");
        internal string Project { get; }
        internal Foundation.WorkerClient Worker { get; }
        internal Scope(string release, bool dialog, string faultMode = "crash", bool bindProject = true)
        {
            Directory.CreateDirectory(Root);
            Project = "C:/fixture.ap" + release;
            Set("TIA_MCP_FIXTURE_SETTLEMENT_FAULT", "gated");
            Set("TIA_MCP_FIXTURE_SLOW_GATE", Root);
            Set("TIA_MCP_FIXTURE_RECOVERY_TRACE", Trace);
            Set("TIA_MCP_PLC_WORKER_FAULT", faultMode);
            using var current = Process.GetCurrentProcess();
            string facts = Path.Combine(Root, "facts.json");
            File.WriteAllText(facts, new JsonArray(new JsonObject { ["ProcessId"] = Environment.ProcessId,
                ["ProcessStartUtc"] = current.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["ProjectPath"] = Project, ["AttachedSessions"] = 0 }).ToJsonString());
            Set("TIA_MCP_FIXTURE_RECOVERY_PROCESSES", facts);
            if (dialog) File.WriteAllText(Path.Combine(Root, "dialog"), "TIA_MCP_DIALOG " + new JsonObject {
                ["event"] = "Confirmation", ["answer"] = "unanswered", ["caption"] = "Synthetic settlement dialog",
                ["choices"] = new JsonArray("Yes", "No") }.ToJsonString());
            image = TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
            TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(image)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            Worker = new Foundation.WorkerClient(release, image, Path.GetDirectoryName(image)!, true) { Bundled = false,
                SharedSession = EngineHost.ReleaseCatalogData.Releases[release].SharedSession };
            using var budgets = new ProductionWorkerBudgets(Worker);
            Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None).GetAwaiter().GetResult();
            if (bindProject)
                Worker.Call("BindProject", new() { ["projectName"] = "fixture", ["expectedProjectFile"] = Project }, CancellationToken.None)
                    .GetAwaiter().GetResult();
        }
        private void Set(string key, string value)
        {
            previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        internal void ClearCrash() => Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", null);
        internal void Release() => File.WriteAllText(Path.Combine(Root, "release"), "");
        internal async Task WaitDispatched()
        {
            using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
            while (!File.Exists(Path.Combine(Root, "dispatched"))) await Task.Delay(5, deadline.Token);
        }
        internal async Task WaitDialog()
        {
            var diagnostics = (Queue<string>)typeof(Foundation.WorkerClient)
                .GetField("diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Worker)!;
            bool Reported()
            {
                lock (diagnostics) return diagnostics.Any(line => line.Contains("Synthetic settlement dialog", StringComparison.Ordinal));
            }
            using var deadline = new CancellationTokenSource(TestDeadlines.Hang);
            while (!Reported()) await Task.Delay(5, deadline.Token);
        }
        public void Dispose()
        {
            Release();
            if (Worker.OperatorSnapshot()["pid"] is JsonValue pid)
            {
                using var child = Process.GetProcessById(pid.GetValue<int>());
                if (!child.HasExited) Assert.Equal(Path.GetFullPath(image), Path.GetFullPath(child.MainModule!.FileName), ignoreCase: true);
            }
            Worker.Dispose();
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
            string parent = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build") + Path.DirectorySeparatorChar;
            Assert.StartsWith(Path.GetFullPath(parent), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(Root, true);
        }
    }
}
