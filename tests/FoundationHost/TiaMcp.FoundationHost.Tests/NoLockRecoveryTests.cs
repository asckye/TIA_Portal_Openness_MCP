extern alias enginehost;
extern alias foundationhost;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;
using TiaMcp.Engine.Tests.Shared;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class NoLockRecoveryTests
{
    public static IEnumerable<object[]> Releases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(release => new object[] { release });

    public static IEnumerable<object[]> ReleasesAndFlags()
        => Releases().SelectMany(row => new[] { new object[] { row[0], false }, new object[] { row[0], true } });

    public static IEnumerable<object[]> Faults()
        => Releases().SelectMany(row => new[] { "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost" }
            .Select(mode => new object[] { row[0], mode }));

    private static bool IsProcessRead(JsonObject row)
        => (string?)row["method"] == "adapter.HostObserve" && (string?)row["arguments"]?["operation"] == "recovery-processes";
    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<string, string?> environment = new();
        private readonly string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "recovery-" + Guid.NewGuid().ToString("N"));
        internal WorkerClient Worker { get; }
        private readonly string release;
        internal string ProjectFile => "C:/fixture.ap" + release;
        internal string FactsPath => Path.Combine(root, "processes.json");
        internal string Gate => root;
        internal string TracePath => Path.Combine(root, "trace.jsonl");
        internal Process Child => (Process)typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Worker)!;
        internal JsonObject[] Trace => File.ReadAllLines(TracePath).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        internal Scope(string mode = "crash", bool shared = false, string? timeoutConfig = null, string release = "19")
        {
            this.release = release;
            Directory.CreateDirectory(root);
            Facts(ProjectFile);
            string fixture = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
            TiaMcp.Engine.Tests.Shared.TestPaths
                .WriteFixtureFile(Path.Combine(Path.GetDirectoryName(fixture)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            Worker = new(release, fixture, Path.GetDirectoryName(fixture)!, true, timeoutConfig: timeoutConfig) { Bundled = false, SharedSession = shared };
            Set("TIA_MCP_PLC_WORKER_FAULT", mode);
            Set("TIA_MCP_FIXTURE_RECOVERY_PROCESSES", FactsPath);
            Set("TIA_MCP_FIXTURE_RECOVERY_TRACE", TracePath);
            Set("TIA_MCP_FIXTURE_SLOW_GATE", root);
        }
        private void Set(string name, string value)
        { environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        internal void Facts(string? project)
        {
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(FactsPath, project == null ? "[]" : new JsonArray(new JsonObject { ["ProcessId"] = Environment.ProcessId,
                ["ProcessStartUtc"] = process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["ProjectPath"] = project, ["AttachedSessions"] = 0 }).ToJsonString());
        }
        internal async Task Bind()
        {
            await Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
            await Worker.Call("BindProject", new() { ["projectName"] = "fixture", ["expectedProjectFile"] = ProjectFile }, CancellationToken.None);
        }
        internal async Task Arm(string operation)
            => await Worker.Call("FaultFixtureArm", new() { ["method"] = "adapter." + operation }, CancellationToken.None);
        internal void ClearFault() => Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", null);
        internal void DelayRecovery() => Set("TIA_MCP_FIXTURE_SLOW_RECOVERY", "attach");
        internal void StopOwnedChild()
        {
            Assert.StartsWith(Path.GetFullPath(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar,
                Path.GetFullPath(Child.MainModule!.FileName), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(Child.StartInfo.FileName), Path.GetFullPath(Child.MainModule!.FileName), ignoreCase: true);
            WorkerShutdown.KillOwned(Child);
        }
        public void Dispose()
        {
            File.WriteAllText(Path.Combine(root, "release"), "");
            Worker.Dispose();
            foreach (var variable in environment) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }
    }

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task Worker_exit_between_calls_is_invisible(string release, bool shared)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        string before = scope.Worker.ApprovalIdentity;
        object sessionKey = scope.Worker.HostSessionKey;
        scope.Worker.Unsettled.Record("prior-unknown", "SaveProject", "digest", DateTimeOffset.UtcNow);
        scope.Worker.LateResults
            .RecordUnknown("prior-unknown", "SaveProject", "digest", FoundationV4Result.LateFailure(release, "SaveProject", "prior-unknown"));
        scope.StopOwnedChild();
        Assert.True(scope.Worker.Faulted);
        using var request = FoundationWorkerRequest.Use("recovered-read", "ListPlcBlocks");
        var blocks = await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.Equal("fixture", (string?)blocks?[0]?["Name"]);
        Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        Assert.NotSame(sessionKey, scope.Worker.HostSessionKey);
        Assert.NotEqual(before, scope.Worker.ApprovalIdentity);
        Assert.Equal("prior-unknown", Assert.Single(scope.Worker.Unsettled.Snapshot()).RequestId);
        Assert.NotNull(scope.Worker.LateResults.Read(release, "prior-unknown", scope.Worker.Unsettled));
        Assert.Equal(scope.ProjectFile, (string?)SharedSessionState.For(scope.Worker).Identity?["projectPath"]);
        var stateRead = FoundationV4Result.HostSessionState(release, "host-state", new());
        Assert.Single(scope.Worker.ParkResult(stateRead, "GetSessionState").StructuredContent!["meta"]!["warnings"]!.AsArray(),
            warning => (string?)warning?["code"] == "WORKER_RESTARTED");
        var result = FoundationV4Result.HostSessionState(release, "recovered-read", new());
        Assert.DoesNotContain(scope.Worker.ParkResult(result, "ListPlcBlocks").StructuredContent!["meta"]!["warnings"]!.AsArray(),
            warning => (string?)warning?["code"] == "WORKER_RESTARTED");
        Assert.DoesNotContain(scope.Worker.ParkResult(result, "ListPlcBlocks").StructuredContent!["meta"]!["warnings"]!.AsArray(),
            warning => (string?)warning?["code"] == "WORKER_RESTARTED");
        Assert.True((bool?)SharedSessionState.For(scope.Worker).Snapshot(release)["workerRecovery"]?["rebound"]);
    }

    [Theory, MemberData(nameof(Faults))]
    public async Task Worker_fault_during_a_write_is_unknown_and_the_next_call_rebinds(string release, string mode)
    {
        using var scope = new Scope(mode, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        using var request = FoundationWorkerRequest.Use("failed-write", "SaveProject");
        await scope.Arm("SaveProject");
        var args = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = scope.ProjectFile };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => scope.Worker.Call("SaveProject", args, CancellationToken.None));
        var result = FoundationV4Result.Failure(release, "SaveProject", "failed-write", true, true, null, error).StructuredContent!;
        Assert.Equal("unknown", (string?)result["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result["error"]?["code"]);
        scope.ClearFault();
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        var trace = scope.Trace;
        Assert.Single(trace, row => (string?)row["method"] == "adapter.SaveProject");
        int restartedPid = scope.Child.Id;
        var replay = trace.Where(row => (int?)row["pid"] == restartedPid).ToArray();
        Assert.Equal(new[] { "adapter.HostObserve", "adapter.Attach", "adapter.BindProject", "adapter.ReadBlocks" },
            replay.Select(row => (string?)row["method"]));
        Assert.True(IsProcessRead(replay[0]));
        Assert.Equal(Environment.ProcessId, (int?)replay[1]["arguments"]?["processId"]);
        Assert.Equal("fixture", (string?)replay[2]["arguments"]?["projectName"]);
        Assert.Equal(scope.ProjectFile, (string?)replay[2]["arguments"]?["expectedProjectFile"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Worker_fault_during_a_read_is_read_failed(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        using var request = FoundationWorkerRequest.Use("failed-read", "ListPlcBlocks");
        await scope.Arm("ReadBlocks");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        var result = FoundationV4Result.Failure(release, "ListPlcBlocks", "failed-read", true, false, null, error).StructuredContent!;
        Assert.Equal("read-failed", (string?)result["meta"]?["outcome"]);
        Assert.Equal("read-only", (string?)result["meta"]?["execution"]);
    }

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task Automatic_recovery_never_disposes_a_worker_started_portal(string release, bool attached)
    {
        using var scope = new Scope("tia-lost", release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        if (attached) await scope.Bind();
        await scope.Worker.Call("FixtureOwnsPortal", new(), CancellationToken.None);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        using var request = FoundationWorkerRequest.Use("lost", "ListPlcBlocks");
        await scope.Arm("ReadBlocks");
        await Assert.ThrowsAsync<WorkerOperationException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        int pid = scope.Child.Id;
        var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("worker-owns-portal", refusal.Condition);
        Assert.Equal("This worker started the TIA instance it uses. Stopping it would close TIA. "
            + "Save in TIA, then call RestartOpennessWorker.", refusal.Message);
        Assert.False((bool?)refusal.Data["foundationRequestSent"]);
        Assert.Equal(pid, scope.Child.Id);
        Assert.False(scope.Child.HasExited);
        Assert.DoesNotContain(scope.Trace, IsProcessRead);
    }

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task Rebind_refusal_names_the_target_and_retries_on_the_next_call(string release, bool missingProject)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        scope.Facts(missingProject ? "C:/Other.ap19" : null);
        var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal(missingProject ? "project-not-open" : "tia-process-gone", error.Condition);
        Assert.Equal(missingProject
            ? $"Could not rebind TIA PID {Environment.ProcessId}, project {scope.ProjectFile}. The next call will try again."
            : $"TIA PID {Environment.ProcessId}, project {scope.ProjectFile}, is gone. The next call will try again.", error.Message);
        Assert.False((bool?)error.Data["foundationRequestSent"]);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.ReadBlocks");
        scope.Facts(scope.ProjectFile);
        var blocks = await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.Equal("fixture", (string?)blocks?[0]?["Name"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Host_state_reads_do_not_start_recovery(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        await scope.Worker.RefreshHostState(true, CancellationToken.None);
        Assert.Equal(1, (long?)SharedSessionState.For(scope.Worker).Snapshot(release)["workerGeneration"]);
        Assert.DoesNotContain(scope.Trace, IsProcessRead);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Automatic_recovery_never_stops_a_worker_with_an_unanswered_request(string release)
    {
        using var scope = new Scope("hang", timeoutConfig: "{\"default\":0.02}", release: release);
        using var request = FoundationWorkerRequest.Use("running", "ListPlcBlocks");
        using (new ProductionWorkerBudgets(scope.Worker))
        { await scope.Bind(); await scope.Arm("ReadBlocks"); }
        using var invocation = scope.Worker.LateResults.CreateInvocation("ListPlcBlocks", "running", CancellationToken.None);
        using var flow = invocation.Enter();
        var call = scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        await invocation.Detachment.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        var fault = typeof(WorkerClient).GetField("faultEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fault.SetValue(scope.Worker, new JsonObject { ["reason"] = "synthetic" });
        try
        {
            int pid = scope.Child.Id;
            var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.RecoverLostWorker(true, CancellationToken.None));
            Assert.Equal("worker-call-running", error.Condition);
            Assert.False(scope.Child.HasExited);
            Assert.Equal(pid, scope.Child.Id);
            Assert.DoesNotContain(scope.Trace, IsProcessRead);
        }
        finally
        {
            fault.SetValue(scope.Worker, null);
            File.WriteAllText(Path.Combine(scope.Gate, "release"), "");
            await call.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Automatic_restart_preserves_exports_and_invalidates_plans_and_explicit_restart_clears_exports(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        var services = FoundationHostPorts.CreateServices(scope.Worker, release, () => new() { ["ready"] = true }, AppContext.BaseDirectory);
        var context = new enginehost::TiaMcp.FoundationHost.EngineSessionContext(services);
        using var session = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(context);
        string token = context.BatchPlans.Add(new() { State = "old", Project = scope.ProjectFile }, DateTime.UtcNow);
        string handle = enginehost::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "fixture", "retained", 4).id;
        services.ResetExports = () => {
            context.Exports.Dispose();
            context.Exports = new();
        };
        try
        {
            scope.StopOwnedChild();
            await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
            Assert.Throws<ArgumentException>(() => context.BatchPlans.Take(token, DateTime.UtcNow));
            Assert.Equal("retained", enginehost::TiaMcp.FoundationHost.SessionExportStore.Get(handle)!.Content);
            var restarted = await services.Restart(true, CancellationToken.None);
            Assert.True((bool?)restarted["restarted"]);
            Assert.Null(enginehost::TiaMcp.FoundationHost.SessionExportStore.Get(handle));
        }
        finally { context.Exports.Dispose(); }
    }

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task A_plan_from_before_automatic_recovery_is_stale(string release, bool shared)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        var services = FoundationHostPorts.CreateServices(scope.Worker, release, () => new() { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        using var session = pipeline.EnterSession();
        int writes = 0;
        CallToolResult Execute() { writes++; return Host.V4Result("CreateDevice", new JsonObject(), completed: true); }
        var arguments = new JsonObject { ["name"] = "Device" };
        var preview = Host.RunWritePreview("CreateDevice", true, arguments, Execute);
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        scope.StopOwnedChild();
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        var stale = Host.RunWritePreview("CreateDevice", false, arguments, Execute, hash, scope.ProjectFile).StructuredContent!;
        Assert.Equal("PLAN_STALE", (string?)stale["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)stale["meta"]?["outcome"]);
        Assert.Equal(0, writes);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Explicit_connection_can_replace_a_missing_recovery_target(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        scope.Facts(null);
        await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        await scope.Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        Assert.False(scope.Worker.RecoveryNeeded);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Automatic_restart_is_not_a_session_end_in_the_workbench(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        using var session = new foundationhost::TiaMcp.FoundationHost.HostSession(release, "fixture", false,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>(), Array.Empty<ModelContextProtocol.Server.McpServerTool>(),
            () => SharedSessionState.For(scope.Worker).OperatorSnapshot(release), () => { });
        scope.StopOwnedChild();
        session.LaneChanged(true, "ListPlcBlocks", "mcp", "recovery");
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        session.LaneChanged(false, "ListPlcBlocks", "mcp", "recovery");
        var snapshot = session.Snapshot();
        Assert.Null(snapshot["endedBy"]);
        Assert.Null(snapshot["endReason"]);
        Assert.Equal(2, (long?)snapshot["worker"]?["generation"]);
        Assert.True((bool?)snapshot["sessionState"]?["workerRecovery"]?["rebound"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_rebind_over_budget_rejects_the_operation_and_retains_the_unanswered_worker(string release)
    {
        using var scope = new Scope(timeoutConfig: "{\"firstAttach\":0.02}", release: release);
        using (new ProductionWorkerBudgets(scope.Worker)) { await scope.Bind(); }
        scope.DelayRecovery();
        scope.StopOwnedChild();
        using var invocation = scope.Worker.LateResults.CreateInvocation("ListPlcBlocks", "recovery-budget", CancellationToken.None);
        using var flow = invocation.Enter();
        try
        {
            var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
            Assert.Equal("tia-not-responding", error.Condition);
            Assert.Equal($"Recovery before adapter.Attach is still waiting for TIA PID {Environment.ProcessId}, project {scope.ProjectFile}. "
                + "The requested operation has not started. The next native call can retry after recovery finishes.", error.Message);
            Assert.False((bool?)error.Data["foundationRequestSent"]);
            Assert.False(invocation.Detached);
            Assert.False(scope.Child.HasExited);
            Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.ReadBlocks");
            var busy = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
            Assert.Equal("worker-call-running", busy.Condition);
            Assert.NotNull(scope.Worker.Running);
        }
        finally
        {
            File.WriteAllText(Path.Combine(scope.Gate, "release"), "");
            await scope.Worker.RecoveryDrain.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        }
        using var production = new ProductionWorkerBudgets(scope.Worker);
        Assert.Equal("fixture", (string?)(await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None))?[0]?["Name"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_refused_disconnect_keeps_the_recorded_recovery_target(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        await scope.Worker.Call("FixtureOwnsPortal", new(), CancellationToken.None);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        string binding = scope.Worker.ApprovalIdentity;
        var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("Disconnect", new(), CancellationToken.None));
        Assert.Equal("session", refused.ParamName);
        Assert.False(refused.IsArgument);
        Assert.Equal(binding, scope.Worker.ApprovalIdentity);
        Assert.False(scope.Child.HasExited);
        scope.StopOwnedChild();
        var gone = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("tia-process-gone", gone.Condition);
        Assert.False((bool?)gone.Data["foundationRequestSent"]);
        Assert.Equal(binding, scope.Worker.ApprovalIdentity);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_native_deadline_fault_keeps_the_worker_running(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        var channel = (ChannelClient)typeof(WorkerClient).GetField("channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Worker)!;
        channel.Invalidate(new TimeoutException("Synthetic native deadline fault."));
        typeof(ChannelClient).GetField("unknown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(channel, true);
        try
        {
            int pid = scope.Child.Id;
            var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.RecoverLostWorker(true, CancellationToken.None));
            Assert.Equal("worker-call-running", error.Condition);
            Assert.False(scope.Child.HasExited);
            Assert.Equal(pid, scope.Child.Id);
            Assert.DoesNotContain(scope.Trace, IsProcessRead);
        }
        finally { scope.StopOwnedChild(); }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Refused_isolated_connection_keeps_the_borrowed_worker_recoverable(string release)
    {
        using var scope = new Scope("tia-lost", release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        var refused = await Assert.ThrowsAsync<WorkerOperationException>(() =>
            scope.Worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None));
        Assert.Equal("rejected-before-operation", refused.Outcome);
        using var request = FoundationWorkerRequest.Use("lost-after-refusal", "ListPlcBlocks");
        await scope.Arm("ReadBlocks");
        await Assert.ThrowsAsync<WorkerOperationException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        scope.ClearFault();
        Assert.Equal("fixture", (string?)(await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None))?[0]?["Name"]);
        Assert.True((bool?)SharedSessionState.For(scope.Worker).Snapshot(release)["workerRecovery"]?["rebound"]);
    }

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task Operator_snapshots_during_recovery_never_tear(string release, bool shared)
    {
        using var scope = new Scope(release: release, shared: shared);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.DelayRecovery();
        scope.StopOwnedChild();
        using var watcher = new FileSystemWatcher(scope.Gate, "dispatched");
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => dispatched.TrySetResult();
        watcher.EnableRaisingEvents = true;
        var call = scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task? reader = null;
        int snapshots = 0;
        try
        {
            await dispatched.Task.WaitAsync(TestDeadlines.Hang);
            Assert.False((bool?)scope.Worker.OperatorSnapshot()["workerRecovery"]?["rebound"]);
            var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader = Task.Run(() => {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var snapshot = scope.Worker.OperatorSnapshot();
                        var recovery = Assert.IsType<JsonObject>(snapshot["workerRecovery"]);
                        Assert.Equal(new[] { "generation", "lost", "previousGeneration", "reason", "rebound" }, recovery.Select(row => row.Key).Order());
                        Assert.Equal(1, (long?)recovery["previousGeneration"]);
                        Assert.Equal(2, (long?)recovery["generation"]);
                        Assert.NotEmpty((string)recovery["reason"]!);
                        Assert.Equal(10, recovery["lost"]!.AsArray().Count);
                        Assert.True(recovery["rebound"]!.AsValue().TryGetValue<bool>(out _));
                        Assert.NotNull(JsonNode.Parse(snapshot.ToJsonString()));
                        var status = scope.Worker.SharedStatus;
                        if (shared && (bool?)snapshot["connected"] == true) Assert.NotNull(snapshot["binding"]?["projectFile"]);
                        if (shared && (bool?)status?["session"]?["isConnected"] == true)
                        {
                            Assert.Equal(scope.ProjectFile, (string?)(status["binding"]?["identity"]?["projectPath"] ?? status["binding"]?["projectPath"]));
                            Assert.NotNull(status["binding"]?["identity"]?["generation"] ?? status["binding"]?["generation"]);
                        }
                        if (++snapshots == 100) sampled.TrySetResult();
                    }
                }
                catch (Exception error) { sampled.TrySetException(error); throw; }
            });
            await sampled.Task.WaitAsync(TestDeadlines.Hang);
            File.WriteAllText(Path.Combine(scope.Gate, "release"), "");
            await call.WaitAsync(TestDeadlines.Hang);
            Assert.True((bool?)scope.Worker.OperatorSnapshot()["workerRecovery"]?["rebound"]);
            if (shared)
            {
                await scope.Worker.RefreshHostState(true, CancellationToken.None);
                Assert.NotNull(scope.Worker.OperatorSnapshot()["binding"]?["projectFile"]);
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(scope.Gate, "release"), "");
            stop.Cancel();
            if (reader != null) await reader.WaitAsync(TestDeadlines.Hang);
            await call.WaitAsync(TestDeadlines.Hang);
        }
        Assert.True(snapshots >= 100);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_connect_that_fails_after_starting_tia_keeps_the_ownership_guard(string release)
    {
        using var scope = new Scope("connect-read-failed", release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        // ReadFailed is legal only on a read-only frame; the fixture simulates portal creation before that failure.
        var failed = await Assert.ThrowsAsync<WorkerOperationException>(() =>
            scope.Worker.Call("project-lifecycle.ConnectIsolatedPortal", new() { ["dryRun"] = true }, CancellationToken.None));
        Assert.Equal("read-failed", failed.Outcome);
        Assert.True(scope.Worker.RecoveryNeeded);
        int pid = scope.Child.Id;
        var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("worker-owns-portal", refusal.Condition);
        Assert.Equal("This worker started the TIA instance it uses. Stopping it would close TIA. "
            + "Save in TIA, then call RestartOpennessWorker.", refusal.Message);
        Assert.False((bool?)refusal.Data["foundationRequestSent"]);
        Assert.False(scope.Child.HasExited);
        Assert.Equal(pid, scope.Child.Id);
        Assert.DoesNotContain(scope.Trace, IsProcessRead);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.ReadBlocks");
    }

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task The_reported_binding_epoch_never_repeats_across_a_recovery(string release, bool shared)
    {
        using var scope = new Scope(release: release, shared: shared);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        long before = (long)scope.Worker.OperatorSnapshot()["binding"]!["bindingEpoch"]!;
        scope.StopOwnedChild();
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        long after = (long)scope.Worker.OperatorSnapshot()["binding"]!["bindingEpoch"]!;
        Assert.True(after > before);
        Assert.Equal(after, (long?)SharedSessionState.For(scope.Worker).OperatorSnapshot(release)["binding"]?["bindingEpoch"]);
        Assert.Equal(after, (long?)SharedSessionState.For(scope.Worker).Snapshot(release)["binding"]?["identity"]?["generation"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_dead_worker_that_started_tia_is_not_rebound(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        await scope.Worker.Call("FixtureOwnsPortal", new(), CancellationToken.None);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        int oldWorker = scope.Child.Id;
        scope.StopOwnedChild();
        var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("tia-process-gone", error.Condition);
        Assert.Equal($"TIA PID {Environment.ProcessId}, project {scope.ProjectFile}, was started by the worker that exited. "
            + "Its lifetime is no longer owned by this session. Select a TIA instance deliberately with ConnectPortal or ConnectIsolatedPortal.", error
                .Message);
        Assert.False((bool?)error.Data["foundationRequestSent"]);
        Assert.DoesNotContain(scope.Trace, row => (int?)row["pid"] != oldWorker && (string?)row["method"] == "adapter.Attach");
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        Assert.DoesNotContain(scope.Trace, row => (int?)row["pid"] == scope.Child.Id && (string?)row["method"] == "adapter.Attach");
        Assert.DoesNotContain(scope.Trace, IsProcessRead);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task An_independent_runtime_call_on_a_dead_worker_has_a_clean_precondition(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        const string operation = "runtime-channels.ExecutePlcSimAdvanced";
        var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call(operation, new(), CancellationToken.None));
        Assert.Equal("worker-faulted", error.Condition);
        Assert.Equal("The worker stopped. The next native call will attempt recovery.", error.Message);
        Assert.False((bool?)error.Data["foundationRequestSent"]);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter." + operation);
        Assert.DoesNotContain(scope.Trace, IsProcessRead);
        Assert.Equal(1, (long?)scope.Worker.OperatorSnapshot()["generation"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task The_restart_warning_reaches_the_first_result_that_can_carry_it(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        using var request = FoundationWorkerRequest.Use("without-warnings", "ListPlcBlocks");
        await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        var noWarnings = new CallToolResult { StructuredContent = new JsonObject { ["meta"] = new JsonObject { ["requestId"] = "without-warnings" } } };
        Assert.Same(noWarnings, scope.Worker.ParkResult(noWarnings, "ListPlcBlocks"));
        var next = FoundationV4Result.HostSessionState(release, "next-request", new());
        Assert.Single(scope.Worker.ParkResult(next, "GetSessionState").StructuredContent!["meta"]!["warnings"]!.AsArray(),
            warning => (string?)warning?["code"] == "WORKER_RESTARTED");
        Assert.DoesNotContain(scope.Worker.ParkResult(next, "GetSessionState").StructuredContent!["meta"]!["warnings"]!.AsArray(),
            warning => (string?)warning?["code"] == "WORKER_RESTARTED");
        Assert.True((bool?)SharedSessionState.For(scope.Worker).Snapshot(release)["workerRecovery"]?["rebound"]);
    }

    private static RequestContext<CallToolRequestParams> WriteRequest(JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = "SaveProject", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) }
        };

    [Theory, MemberData(nameof(ReleasesAndFlags))]
    public async Task An_approved_write_after_recovery_records_the_generation_it_ran_on(string release, bool shared)
    {
        using var scope = new Scope(release: release, shared: shared);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        var services = FoundationHostPorts.CreateServices(scope.Worker, release, () => new() { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        using var session = pipeline.EnterSession();
        await scope.Bind();
        string before = scope.Worker.ApprovalIdentity;
        scope.StopOwnedChild();
        int approvals = 0;
        PendingApproval? recorded = null;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), scope.Worker), release,
            () => new(true, 1), (pending, _, _) => {
                approvals++; recorded = pending;
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            }, readinessForTest: () => new() { ["ready"] = true });
        var arguments = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = scope.ProjectFile };
        var result = await tool.InvokeAsync(WriteRequest(arguments));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Equal(1, approvals);
        Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        Assert.NotEqual(before, scope.Worker.ApprovalIdentity);
        var identity = JsonNode.Parse(scope.Worker.ApprovalIdentity)!;
        var approved = JsonNode.Parse(Assert.IsType<PendingApproval>(recorded).ProjectIdentity)!;
        string epoch = shared ? "generation" : "BindingEpoch";
        Assert.NotNull(identity[epoch]);
        Assert.True(JsonNode.DeepEquals(identity[epoch], approved[epoch]));
        Assert.Single(scope.Trace, row => (string?)row["method"] == "adapter.SaveProject" && (bool?)row["arguments"]?["dryRun"] == false);
        Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "WORKER_RESTARTED");
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task An_approval_is_refused_if_the_binding_changes_while_confirmation_is_open(string release)
    {
        using var scope = new Scope(release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        int approvals = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "SaveProject"), scope.Worker), release,
            () => new(true, 1), (pending, _, _) => {
                approvals++; scope.StopOwnedChild();
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            }, readinessForTest: () => new() { ["ready"] = true });
        var arguments = new JsonObject { ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = scope.ProjectFile };
        var result = await tool.InvokeAsync(WriteRequest(arguments));
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("Target changed while awaiting confirmation.", (string?)result.StructuredContent?["error"]?["message"]);
        Assert.Equal(1, approvals);
        Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.SaveProject" && (bool?)row["arguments"]?["dryRun"] == false);
    }
}
