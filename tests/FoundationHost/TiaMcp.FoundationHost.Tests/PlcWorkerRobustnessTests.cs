extern alias foundationhost;
using HostSession = foundationhost::TiaMcp.FoundationHost.HostSession;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using Xunit;

public sealed class PlcFixtureWorkerRobustnessTests
{
    private static string Fixture => TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
    private sealed class Scope : IDisposable
    {
        private readonly string release;
        private readonly string? previous = Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT");
        private readonly string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        private readonly string gate = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "robustness-gate-" + Guid.NewGuid().ToString("N"));
        private HostSession? session;
        internal WorkerClient Worker { get; }
        internal Scope(string release, string mode, string timeoutConfig = "{\"default\":2}")
        {
            this.release = release;
            Assert.True(File.Exists(Fixture), Fixture);
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(Fixture)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", mode);
            if (mode == "hang")
            {
                Directory.CreateDirectory(gate);
                Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
            }
            Worker = new WorkerClient(release, Fixture, Path.GetDirectoryName(Fixture)!, true,
                timeoutConfig: timeoutConfig) {
                    Bundled = false, WithUi = true, TiaPortalLocation = "C:/fixture Portal V" + release };
            // Start the worker under the production budgets; the scaled 2-s default applies from the first operation a test
            // measures (P8-stab-1). The fixture faults only the armed request, so this status read never consumes a fault.
            using (new ProductionWorkerBudgets(Worker)) Worker.Observe("status", new(), CancellationToken.None).GetAwaiter().GetResult();
        }
        internal Process? Process => (Process?)typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Worker);
        internal async Task Arm(string operation)
        {
            var armed = await Worker.Call("FaultFixtureArm", new() { ["method"] = "adapter." + operation }, CancellationToken.None);
            Assert.True((bool?)armed?["armed"]);
        }
        internal async Task Prechecks(string release)
        {
            string correlation = FoundationWorkerRequest.CorrelationId;
            var state = await Worker.Call("ReadState", new(), CancellationToken.None);
            Assert.Equal(release, (string?)state?["ReleaseKey"]);
            var readiness = await Worker.Observe("status", new(), CancellationToken.None);
            Assert.True((bool?)readiness?["readiness"]?["ready"]);
            using (FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "ListPlcBlocks"))
            {
                var blocks = await Worker.Call("ReadBlocks", new(), CancellationToken.None);
                Assert.Equal("fixture", (string?)blocks?[0]?["Name"]);
            }
            using (FoundationWorkerRequest.Use(correlation, "GetPlcBlockInfo"))
            {
                var blocks = await Worker.Call("ReadBlocks", new(), CancellationToken.None);
                Assert.Equal("fixture", (string?)blocks?[0]?["Name"]);
            }
            Assert.False(Worker.Faulted);
        }
        internal void VerifyChild()
        {
            if (Process is { HasExited: false } child)
                Assert.Equal(Path.GetFullPath(Fixture), Path.GetFullPath(child.MainModule!.FileName), ignoreCase: true);
        }
        internal Task<CallToolResult> Call(McpServerTool tool, RequestContext<CallToolRequestParams> request)
        {
            session ??= new HostSession(release, "fixture", true, new[] { tool }, new[] { tool }, Worker.OperatorSnapshot,
                () => { }, nativeConsentDirectory: gate, lateResults: Worker.LateResults, unsettled: Worker.Unsettled);
            return session.OperatorTools.Single().InvokeAsync(request).AsTask();
        }
        internal void Release() { if (Directory.Exists(gate)) File.WriteAllText(Path.Combine(gate, "release"), ""); }
        internal void EndFaultedFixture()
        {
            VerifyChild();
            if (Process is { HasExited: false } child)
            { child.Kill(); Assert.True(child.WaitForExit(TiaMcp.Engine.Tests.Shared.TestDeadlines.HangMilliseconds)); }
        }
        public void Dispose()
        {
            Release();
            if (session != null)
            {
                Assert.True(SpinWait.SpinUntil(() => (bool?)session.Snapshot()["callInProgress"] != true,
                    TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
                session.Dispose();
            }
            VerifyChild();
            Worker.Dispose();
            EndFaultedFixture();
            Worker.Dispose();
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", previous);
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", previousGate);
        }
    }

    public static IEnumerable<object[]> Faults()
    {
        foreach (string release in new[] { "19", "20", "21" })
        foreach (string mode in new[] { "hang", "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost" })
            yield return new object[] { release, mode };
    }
    [Theory, MemberData(nameof(Faults))]
    public async Task Plc_faults_lock_the_host_and_require_explicit_restart(string release, string mode)
    {
        using var scope = new Scope(release, mode);
        using var request = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState");
        await scope.Arm("ReadState");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => scope.Worker.Call("ReadState", new(), CancellationToken.None));
        Assert.Equal(mode != "tia-lost", scope.Worker.Faulted);
        Assert.True((bool?)error.Data["foundationRequestSent"]);
        Assert.Null(error.Data["foundationSessionPoisoned"]);
        if (mode == "hang")
        {
            Assert.IsType<ChannelFaultException>(error);
            var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
                scope.Worker.Call("ReadState", new(), CancellationToken.None));
            Assert.Equal("worker-faulted", refused.Condition);
        }
        else
        {
            if (mode == "tia-lost") Assert.Equal("unknown", Assert.IsType<WorkerOperationException>(error).Outcome);
            else Assert.IsType<ChannelFaultException>(error);
            if (mode == "tia-lost")
            {
                using var independent = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState");
                Assert.Equal(release, (string?)(await scope.Worker.Call("ReadState", new(), CancellationToken.None))?["ReleaseKey"]);
            }
            else
            {
                var refusal = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(
                    () => scope.Worker.Call("ReadState", new(), CancellationToken.None));
                Assert.Equal("worker-faulted", refusal.Condition);
            }
        }
        Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", null);
        scope.VerifyChild();
        // The explicit restart and the next generation's first read run under the production budgets (P8-stab-1).
        using var production = new ProductionWorkerBudgets(scope.Worker);
        var restarted = await scope.Worker.RestartHostWorker(true, CancellationToken.None);
        Assert.True((bool?)restarted["restarted"]); Assert.Null(restarted["requiresExplicitRebind"]);
        Assert.Null(restarted["requiresTiaRestart"]);
        Assert.False(scope.Worker.Faulted);
        var state = await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        Assert.Equal(release, (string?)state?["ReleaseKey"]);
        Assert.False((bool?)state?["IsAttached"]);
    }

    [Theory, MemberData(nameof(Faults))]
    public async Task Plc_faults_keep_the_V4_unknown_outcome_and_session_reset(string release, string mode)
    {
        using var scope = new Scope(release, mode);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(definition => definition.Name == "ListPlcBlocks"), scope.Worker),
            release, () => new(false, 1), readinessForTest: () => {
                scope.Arm("plc-paging.ReadBlockPage").GetAwaiter().GetResult();
                return new JsonObject { ["ready"] = true };
            });
        var server = DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>();
        var request = new RequestContext<CallToolRequestParams>(server) { Params = JsonSerializer.Deserialize<CallToolRequestParams>(
            new JsonObject { ["name"] = tool.ProtocolTool.Name, ["arguments"] = new JsonObject { ["softwarePath"] = "PLC", ["regexName"] = "" } }.ToJsonString()) };
        var result = await scope.Call(tool, request);
        Assert.Equal(mode == "hang" ? "in-progress" : "read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        if (mode == "hang") Assert.Equal("OPERATION_IN_PROGRESS", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.NotEmpty(result.StructuredContent!["meta"]!["warnings"]!.AsArray());
        Assert.Equal(mode is not ("hang" or "tia-lost"), scope.Worker.Faulted);
        if (mode == "tia-lost")
        {
            using var independent = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState");
            Assert.Equal(release, (string?)(await scope.Worker.Call("ReadState", new(), CancellationToken.None))?["ReleaseKey"]);
        }
        else
        {
            var refused = await scope.Call(tool, request);
            Assert.Equal("PRECONDITION_FAILED", (string?)refused.StructuredContent?["error"]?["code"]);
            Assert.Equal(mode == "hang" ? "worker-call-running" : "worker-faulted", (string?)refused.StructuredContent?["error"]?["details"]?["condition"]);
        }
    }

    [Theory, MemberData(nameof(Faults))]
    public async Task Prechecks_and_other_invocations_do_not_consume_the_V4_fault(string release, string mode)
    {
        using var scope = new Scope(release, mode);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(definition => definition.Name == "ListPlcBlocks"), scope.Worker),
            release, () => new(false, 1), readinessForTest: () => {
                scope.Arm("plc-paging.ReadBlockPage").GetAwaiter().GetResult();
                scope.Prechecks(release).GetAwaiter().GetResult();
                return new JsonObject { ["ready"] = true };
            });
        var server = DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>();
        var request = new RequestContext<CallToolRequestParams>(server) { Params = JsonSerializer.Deserialize<CallToolRequestParams>(
            new JsonObject { ["name"] = tool.ProtocolTool.Name, ["arguments"] = new JsonObject { ["softwarePath"] = "PLC", ["regexName"] = "" } }.ToJsonString()) };
        var result = await scope.Call(tool, request);
        Assert.Equal(mode == "hang" ? "in-progress" : "read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        if (mode == "hang") Assert.Equal("OPERATION_IN_PROGRESS", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.NotEmpty(result.StructuredContent!["meta"]!["warnings"]!.AsArray());
        Assert.Equal(mode is not ("hang" or "tia-lost"), scope.Worker.Faulted);
        if (mode == "tia-lost")
        {
            using var independent = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState");
            Assert.Equal(release, (string?)(await scope.Worker.Call("ReadState", new(), CancellationToken.None))?["ReleaseKey"]);
        }
        else
        {
            var refused = await scope.Call(tool, request);
            Assert.Equal("PRECONDITION_FAILED", (string?)refused.StructuredContent?["error"]?["code"]);
            Assert.Equal(mode == "hang" ? "worker-call-running" : "worker-faulted", (string?)refused.StructuredContent?["error"]?["details"]?["condition"]);
        }
    }

    [Theory, InlineData("15.1"), InlineData("19"), InlineData("21")]
    public async Task Timed_out_write_keeps_the_unknown_outcome_guard(string release)
    {
        using var scope = new Scope(release, "hang", "{\"default\":2,\"archive\":0.2}");
        using var request = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "SaveProjectCopy");
        await scope.Arm("project-lifecycle.ArchiveSaveProjectCopy");
        using var invocation = scope.Worker.LateResults.CreateInvocation("SaveProjectCopy", FoundationWorkerRequest.CorrelationId,
            CancellationToken.None);
        using var flow = invocation.Enter();
        var call = scope.Worker.Call("project-lifecycle.ArchiveSaveProjectCopy",
            new() { ["path"] = "fixture", ["dryRun"] = false }, CancellationToken.None);
        await invocation.Detachment.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        Assert.False(scope.Worker.Faulted);
        var refusal = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(
            () => scope.Worker.Call("ReadState", new(), CancellationToken.None));
        Assert.Equal("worker-call-running", refusal.Condition);
        scope.Release();
        Assert.True((bool?)await call.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
    }

    public static IEnumerable<object[]> Keys() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key => new object[] { key });
    [Theory, MemberData(nameof(Keys))]
    public async Task Background_exception_after_TIA_exit_survives_and_reports_unknown(string release)
    {
        using var scope = new Scope(release, "background-tia-exit");
        using var request = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState");
        await scope.Arm("ReadState");
        using (FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState"))
        {
            var precheck = await scope.Worker.Call("ReadState", new(), CancellationToken.None);
            Assert.Equal(release, (string?)precheck?["ReleaseKey"]);
            Assert.False(scope.Worker.Faulted);
        }
        var error = await Assert.ThrowsAsync<WorkerOperationException>(() => scope.Worker.Call("ReadState", new(), CancellationToken.None));
        Assert.Equal("unknown", error.Outcome); Assert.False(scope.Worker.Faulted);
        Assert.False(scope.Process!.HasExited);
        var stderr = (Queue<string>)typeof(WorkerClient).GetField("diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Worker)!;
        lock (stderr) Assert.Contains(stderr, line => line.Contains("terminating=False", StringComparison.Ordinal));
        using var independent = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "GetSessionState");
        Assert.Equal(release, (string?)(await scope.Worker.Call("ReadState", new(), CancellationToken.None))?["ReleaseKey"]);
    }

    [Theory, MemberData(nameof(Keys))]
    public async Task Host_launch_options_reach_the_worker_on_every_release(string release)
    {
        using var scope = new Scope(release, "");
        var state = await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        Assert.True((bool?)state?["WithUi"]);
        Assert.Equal("C:/fixture Portal V" + release, (string?)state?["TiaPortalLocation"]);
        var parsed = WorkerLaunchOptions.Parse(new WorkerLaunchOptions(release, "C:/API with spaces/中文", "nonce").Arguments().ToArray());
        Assert.False(parsed.WithUi); Assert.Null(parsed.TiaPortalLocation);
        Assert.Equal(release, parsed.ReleaseKey);
    }

    [Fact]
    public void Sdk_ready_requires_the_switch_and_matching_worker_metadata()
    {
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_SDK_READY");
        string? previousFault = Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_SDK_READY", null);
            Assert.False(WorkerSdkFixture.Ready(Fixture, "19"));
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_SDK_READY", "1");
            Assert.True(WorkerSdkFixture.Ready(Fixture, "19"));
            Assert.False(WorkerSdkFixture.Ready(Fixture, "20"));
            // Real production host/channel assemblies have no fixture marker.
            Assert.False(WorkerSdkFixture.Ready(typeof(WorkerClient).Assembly.Location, "19"));
            Assert.False(WorkerFaultInjection.PlcSdkReady(_ => null));
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", "crash");
            Assert.Null(WorkerFaultInjection.PlcMode(typeof(ChannelClient).Assembly));
            Assert.True((bool?)WorkerSdkFixture.Readiness(Fixture, "19", "C:/SDK", () => new() { ["ready"] = false })["ready"]);
            Assert.False((bool?)WorkerSdkFixture.Readiness(typeof(WorkerClient).Assembly.Location, "19", "C:/SDK", () => new() { ["ready"] = false })["ready"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_SDK_READY", previous);
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", previousFault);
        }
    }
}
