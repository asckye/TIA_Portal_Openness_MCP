extern alias toolpipeline;
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
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class NoLockRecoveryTests
{
    public static IEnumerable<object[]> Releases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(release => new object[] { release });

    public static IEnumerable<object[]> ReleasesAndFlags()
        // Explicit false rows exercise recovery without the shared status protocol, independently of the serving release policy.
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
        internal string ProjectFile { get; }
        internal string FactsPath => Path.Combine(root, "processes.json");
        internal string Gate => root;
        internal string TracePath => Path.Combine(root, "trace.jsonl");
        internal string SecondaryStatePath => Path.Combine(root, "secondary.json");
        internal Process Child => (Process)typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Worker)!;
        internal JsonObject[] Trace => File.ReadAllLines(TracePath).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        internal Scope(string mode = "crash", bool shared = true, string? timeoutConfig = null, string release = "19", string? projectFile = null)
        {
            this.release = release;
            ProjectFile = projectFile ?? "C:/fixture.ap" + release;
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
            Set("TIA_MCP_FIXTURE_SECONDARY_STATE", SecondaryStatePath);
        }
        private void Set(string name, string value)
        { environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        internal void Facts(string? project, int? processId = null)
        {
            using var process = Process.GetProcessById(processId ?? Environment.ProcessId);
            File.WriteAllText(FactsPath, project == null ? "[]" : new JsonArray(new JsonObject { ["ProcessId"] = process.Id,
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

    public static IEnumerable<object[]> SecondaryRecoveryCases()
        => ReleasesAndFlags().Where(row => (string)row[0] != "14sp1")
            .SelectMany(row => new[] { false, true }.Select(gone => new object[] { row[0], row[1], gone }));

    [Theory, MemberData(nameof(SecondaryRecoveryCases))]
    public async Task Secondary_record_survives_worker_loss_until_exact_path_close_or_confirmed_absence(string release, bool shared, bool gone)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        string file = @"C:\Reference\Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        await scope.Worker.Call("OpenProject", new() { ["path"] = file, ["openMode"] = "secondary", ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = file }, CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("Disconnect", new(), CancellationToken.None));
        Assert.Contains("CloseProject secondaryProjectFile", refusal.Message);
        Assert.Equal(file, (string?)SharedSessionState.For(scope.Worker).Snapshot(release)["secondaryProjectFile"]);
        scope.StopOwnedChild();
        Assert.Equal(file, (string?)SharedSessionState.For(scope.Worker).Snapshot(release)["secondaryProjectFile"]);
        if (gone) File.WriteAllText(scope.SecondaryStatePath, "{\"file\":null}");
        await scope.Worker.RecoverLostWorker(true, CancellationToken.None);
        Assert.Equal(file, (string?)SharedSessionState.For(scope.Worker).Snapshot(release)["secondaryProjectFile"]);
        var rebound = scope.Trace.Last(row => (string?)row["method"] == "adapter.BindProject");
        Assert.Equal(file, (string?)rebound["arguments"]?["recordedSecondaryProjectFile"]);
        Assert.Equal(Environment.ProcessId, (int?)rebound["arguments"]?["recordedSecondaryProcessId"]);
        using var holder = Process.GetCurrentProcess();
        Assert.Equal(holder.StartTime.ToUniversalTime().Ticks, (long?)rebound["arguments"]?["recordedSecondaryProcessStartTicks"]);
        Assert.Equal(scope.ProjectFile, (string?)SharedSessionState.For(scope.Worker).Identity?["projectPath"]);
        var preview = await scope.Worker.Call("CloseProject", new() { ["secondaryProjectFile"] = file, ["dryRun"] = true }, CancellationToken.None);
        Assert.Equal(gone, (bool?)preview?["SecondaryProjectGone"]);
        Assert.False((bool?)preview?["Executed"]);
        if (!gone)
        {
            Assert.Equal(file, (string?)SharedSessionState.For(scope.Worker).Snapshot(release)["secondaryProjectFile"]);
            await scope.Worker.Call("CloseProject", new() { ["secondaryProjectFile"] = file, ["dryRun"] = false,
                ["confirm"] = true, ["expectedProjectFile"] = file }, CancellationToken.None);
        }
        Assert.False(SharedSessionState.For(scope.Worker).Snapshot(release).ContainsKey("secondaryProjectFile"));
        Assert.Null(JsonNode.Parse(File.ReadAllText(scope.SecondaryStatePath))?["file"]);
        Assert.Equal(scope.ProjectFile, (string?)SharedSessionState.For(scope.Worker).Identity?["projectPath"]);
        Assert.False(scope.Worker.Faulted);
    }

    [Fact]
    public async Task Completed_secondary_close_releases_the_record_without_a_second_holder_query()
    {
        using var scope = new Scope(shared: true, release: "21");
        string file = await OpenReference(scope, "21");
        using var holder = Process.GetCurrentProcess();
        string started = holder.StartTime.ToUniversalTime().ToString("O");
        scope.Worker.SecondaryProcessObservation = _ => scope.Trace.Any(row => (string?)row["method"] == "adapter.CloseProject")
            ? (false, null) : (false, started);
        var result = await scope.Worker.Call("CloseProject", new() { ["secondaryProjectFile"] = file, ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = file }, CancellationToken.None);
        Assert.True((bool?)result?["Executed"]);
        Assert.Null(scope.Worker.OperatorSnapshot()["secondaryProjectFile"]);
    }

    public static IEnumerable<object[]> SecondaryHolderCases()
        => ReleasesAndFlags().Where(row => (string)row[0] != "14sp1");

    private static async Task<string> OpenReference(Scope scope, string release)
    {
        await scope.Bind();
        string file = @"C:\Reference\Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        await scope.Worker.Call("OpenProject", new() { ["path"] = file, ["openMode"] = "secondary", ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = file }, CancellationToken.None);
        return file;
    }

    [Theory, MemberData(nameof(SecondaryHolderCases))]
    public async Task Selecting_another_Tia_after_worker_loss_cannot_confirm_secondary_absence(string release, bool shared)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        string file = await OpenReference(scope, release);
        scope.StopOwnedChild();
        await scope.Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId + 100000 }, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("CloseProject",
            new() { ["secondaryProjectFile"] = file, ["dryRun"] = true }, CancellationToken.None));
        Assert.Contains("TIA PID " + Environment.ProcessId, refused.Message);
        using var holder = Process.GetCurrentProcess();
        Assert.Contains(holder.StartTime.ToUniversalTime().ToString("O"), refused.Message);
        Assert.Equal(file, (string?)SharedSessionState.For(scope.Worker).Snapshot(release)["secondaryProjectFile"]);
        Assert.Equal(file, (string?)JsonNode.Parse(File.ReadAllText(scope.SecondaryStatePath))?["file"]);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.CloseProject");
        var disconnect = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("Disconnect", new(), CancellationToken.None));
        Assert.Contains("CloseProject secondaryProjectFile", disconnect.Message);
    }

    [Theory, MemberData(nameof(SecondaryHolderCases))]
    public async Task Reconnecting_the_same_holder_without_binding_can_confirm_secondary_absence(string release, bool shared)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        string file = await OpenReference(scope, release);
        scope.StopOwnedChild();
        File.WriteAllText(scope.SecondaryStatePath, "{\"file\":null}");
        int binds = scope.Trace.Count(row => (string?)row["method"] == "adapter.BindProject");
        await scope.Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        var attached = scope.Trace.Last(row => (string?)row["method"] == "adapter.Attach");
        Assert.Equal(file, (string?)attached["arguments"]?["recordedSecondaryProjectFile"]);
        Assert.Equal(Environment.ProcessId, (int?)attached["arguments"]?["recordedSecondaryProcessId"]);
        using var holder = Process.GetCurrentProcess();
        Assert.Equal(holder.StartTime.ToUniversalTime().Ticks, (long?)attached["arguments"]?["recordedSecondaryProcessStartTicks"]);
        var gone = await scope.Worker.Call("CloseProject", new() { ["secondaryProjectFile"] = file, ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = file }, CancellationToken.None);
        Assert.True((bool?)gone?["SecondaryProjectGone"]);
        Assert.False((bool?)gone?["Executed"]);
        Assert.False(SharedSessionState.For(scope.Worker).Snapshot(release).ContainsKey("secondaryProjectFile"));
        Assert.Equal(binds, scope.Trace.Count(row => (string?)row["method"] == "adapter.BindProject"));
    }

    public static IEnumerable<object[]> SecondaryHolderGoneCases()
        => SecondaryHolderCases().SelectMany(row => new[] { false, true }.SelectMany(recovery => new[] { false, true }
            .Select(reused => new object[] { row[0], row[1], recovery, reused })));

    [Theory, MemberData(nameof(SecondaryHolderGoneCases))]
    public async Task Gone_holder_reconciles_during_recovery_or_disconnect_without_starting_Tia(string release, bool shared, bool recovery, bool reused)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await OpenReference(scope, release);
        scope.StopOwnedChild();
        int calls = scope.Trace.Length;
        scope.Worker.SecondaryProcessObservation = pid => {
            Assert.Equal(Environment.ProcessId, pid);
            return reused ? (false, DateTime.UnixEpoch.ToString("O")) : (true, null);
        };
        if (recovery)
        {
            await scope.Worker.RecoverLostWorker(false, CancellationToken.None);
            Assert.Null(typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Worker));
        }
        var result = await scope.Worker.Call("Disconnect", new(), CancellationToken.None);
        Assert.False((bool?)result?["ClosedProject"]);
        Assert.False(SharedSessionState.For(scope.Worker).Snapshot(release).ContainsKey("secondaryProjectFile"));
        Assert.Null(scope.Worker.OperatorSnapshot()["secondaryProjectFile"]);
        Assert.Equal(calls, scope.Trace.Length);
    }

    [Theory, MemberData(nameof(SecondaryRecoveryCases))]
    public async Task Live_or_unknown_holder_still_refuses_disconnect_without_restarting_a_worker(string release, bool shared, bool unknown)
    {
        using var scope = new Scope(shared: shared, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        string file = await OpenReference(scope, release);
        scope.StopOwnedChild();
        using var holder = Process.GetCurrentProcess();
        scope.Worker.SecondaryProcessObservation = _ => (false, unknown ? null : holder.StartTime.ToUniversalTime().ToString("O"));
        await scope.Worker.RecoverLostWorker(false, CancellationToken.None);
        int calls = scope.Trace.Length;
        var refusal = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("Disconnect", new(), CancellationToken.None));
        Assert.Contains("TIA PID " + Environment.ProcessId, refusal.Message);
        Assert.Contains("CloseProject secondaryProjectFile", refusal.Message);
        Assert.Equal(file, (string?)SharedSessionState.For(scope.Worker).Snapshot(release)["secondaryProjectFile"]);
        Assert.Null(typeof(WorkerClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope.Worker));
        Assert.Equal(calls, scope.Trace.Length);
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
        Assert.Equal(new[] { "adapter.HostObserve", "adapter.HostObserve", "adapter.Attach", "adapter.HostObserve",
            "adapter.BindProject", "adapter.HostObserve", "adapter.ReadBlocks", "adapter.HostObserve" },
            replay.Select(row => (string?)row["method"]));
        Assert.True(IsProcessRead(replay[0]));
        foreach (int index in new[] { 1, 3, 5, 7 })
        {
            Assert.Equal("status", (string?)replay[index]["arguments"]?["operation"]);
            Assert.Empty(replay[index]["arguments"]!["arguments"]!.AsObject());
        }
        Assert.Equal(Environment.ProcessId, (int?)replay[2]["arguments"]?["processId"]);
        Assert.Equal("fixture", (string?)replay[4]["arguments"]?["projectName"]);
        Assert.Equal(scope.ProjectFile, (string?)replay[4]["arguments"]?["expectedProjectFile"]);
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
        const string reason = "System.Runtime.InteropServices.COMException";
        using var scope = new Scope("portal-read-failed:" + reason, release: release);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        if (attached) await scope.Bind();
        await scope.Worker.Call("FixtureOwnsPortal", new(), CancellationToken.None);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        using var request = FoundationWorkerRequest.Use("lost", "ListPlcBlocks");
        await scope.Arm("ReadBlocks");
        var failed = await Assert.ThrowsAsync<WorkerOperationException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal(reason, (string?)JsonNode.Parse(failed.EvidenceJson!)?["portalLost"]);
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

    [Theory]
    [InlineData("FixtureNonRecoverableException")]
    [InlineData("System.ServiceModel.CommunicationObjectFaultedException")]
    [InlineData("tia-process-exited")]
    public async Task Confirmed_loss_of_an_owned_portal_allows_a_new_isolated_connection(string reason)
    {
        using var scope = new Scope("portal-read-failed:" + reason);
        using var production = new ProductionWorkerBudgets(scope.Worker);
        const string connect = "project-lifecycle.ConnectIsolatedPortal";
        await scope.Worker.Call(connect, new(), CancellationToken.None);
        int firstWorker = scope.Child.Id;
        using var request = FoundationWorkerRequest.Use("owned-portal-loss", "ListPlcBlocks");
        await scope.Arm("ReadBlocks");
        var failed = await Assert.ThrowsAsync<WorkerOperationException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal(reason, (string?)JsonNode.Parse(failed.EvidenceJson!)?["portalLost"]);
        Assert.True(scope.Worker.RecoveryNeeded);
        Assert.False(scope.Child.HasExited);
        using var selection = FoundationWorkerRequest.Use("new-isolated-portal", "ConnectIsolatedPortal");
        var result = await scope.Worker.Call(connect, new(), CancellationToken.None);
        Assert.True((bool?)result?["Meta"]?["operationSuccess"]);
        Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        Assert.False(scope.Worker.RecoveryNeeded);
        var connections = scope.Trace.Where(row => (string?)row["method"] == "adapter." + connect);
        Assert.Equal(new[] { firstWorker, scope.Child.Id }, connections.Select(row => (int)row["pid"]!));
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
            ? $"Could not rebind TIA PID {Environment.ProcessId}. The next binding call will try again. Project: {scope.ProjectFile}"
            : $"TIA PID {Environment.ProcessId} is gone. The next binding call will try again. Project: {scope.ProjectFile}", error.Message);
        Assert.False((bool?)error.Data["foundationRequestSent"]);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.ReadBlocks");
        var generation = (long?)scope.Worker.OperatorSnapshot()["generation"];
        scope.Facts(scope.ProjectFile);
        var blocks = await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.Equal("fixture", (string?)blocks?[0]?["Name"]);
        if (!missingProject) Assert.Equal(generation, (long?)scope.Worker.OperatorSnapshot()["generation"]);
    }

    [Theory]
    [InlineData("isolated")]
    [InlineData("connect")]
    [InlineData("createProject")]
    [InlineData("disconnect-attach")]
    public async Task A_missing_target_allows_process_listing_and_explicit_session_selection(string selection)
    {
        using var scope = new Scope(projectFile: @"C:\生产 工程\Fixture Project.ap19");
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        scope.Facts(null);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
            Assert.Equal("tia-process-gone", error.Condition);
            var body = FoundationV4Result.Failure("19", "ListPlcBlocks", "gone", false, false, null, error).StructuredContent!;
            Assert.Equal($"TIA PID {Environment.ProcessId} is gone. The next binding call will try again. Project: <path>",
                (string?)body["error"]?["message"]);
            Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        }
        int probes = scope.Trace.Count(IsProcessRead);
        using (var lane = await scope.Worker.AcquireLane(CancellationToken.None))
        {
            WorkerClient.ActivateLane(lane);
            var query = await scope.Worker.Call("ReadPortalProcessProjects", new(), CancellationToken.None);
            Assert.Empty(query!["Processes"]!.AsArray());
            Assert.True(scope.Worker.RecoveryNeeded);
            Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
            if (selection == "disconnect-attach")
            {
                await scope.Worker.Call("Disconnect", new(), CancellationToken.None);
                Assert.False(scope.Worker.RecoveryNeeded);
                await scope.Worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
            }
            else if (selection == "isolated")
                await scope.Worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
            else
                await scope.Worker.Call("project-lifecycle.ExecuteProjectScaffoldPrimitive",
                    new() { ["request"] = new JsonObject { ["Kind"] = selection,
                        ["Text"] = selection == "connect" ? new JsonArray() : new JsonArray("C:/fixture", "NewProject") } }, CancellationToken.None);
        }
        Assert.False(scope.Worker.RecoveryNeeded);
        Assert.Equal(probes, scope.Trace.Count(IsProcessRead));
    }

    [Theory]
    [InlineData("ReadBlocks", "ListPlcBlocks", false, "NATIVE_OPERATION_FAILED")]
    [InlineData("SaveProject", "SaveProject", true, "OUTCOME_UNKNOWN")]
    [InlineData("plc-alarms.ExecutePlcB5", "ImportPlcAlarmInstanceTexts", true, "OUTCOME_UNKNOWN")]
    public async Task Swallowed_loss_is_reported_by_the_host_on_the_originating_call(string operation, string tool, bool mutation, string code)
    {
        using var scope = new Scope("swallowed-portal-loss");
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        using var request = FoundationWorkerRequest.Use("loss", tool);
        await scope.Arm(operation);
        var args = operation == "plc-alarms.ExecutePlcB5"
            ? new JsonObject { ["request"] = new JsonObject { ["Operation"] = tool, ["DryRun"] = false }, ["dryRun"] = false }
            : new JsonObject();
        var failure = await Assert.ThrowsAsync<WorkerOperationException>(() => scope.Worker.Call(operation, args, CancellationToken.None));
        Assert.True(scope.Worker.RecoveryNeeded);
        var result = FoundationV4Result.Failure("19", tool, "loss", true, mutation, null, failure);
        var error = result.StructuredContent!["error"]!;
        Assert.Equal(code, (string?)error["code"]);
        Assert.True((bool?)result.StructuredContent["data"]?["evidence"]?["Ok"]);
        Assert.Contains("TIA process exited during this call. The session is lost.", (string?)error["message"]);
        Assert.Contains("ConnectIsolatedPortal", (string?)error["message"]);
        Assert.Contains("DisconnectPortal then ConnectPortal", (string?)error["message"]);
        Assert.Equal("FixtureNonRecoverableException", (string?)error["details"]?["evidence"]?["portalLost"]);
        if (code == "OUTCOME_UNKNOWN") Assert.Equal("tia-process-exited", (string?)error["details"]?["reason"]);
    }

    [Fact]
    public async Task Alarm_export_reports_the_lost_portal_through_the_registered_tool()
    {
        const string name = "ExportAlarmInstanceTexts";
        using var scope = new Scope("swallowed-portal-loss");
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        var services = FoundationHostPorts.CreateServices(scope.Worker, "19", () => new() { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("19", services, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        using var invocation = FoundationWorkerRequest.Use("export-loss", name);
        await scope.Arm("plc-alarms.ExecutePlcB5");
        var tool = PortedToolContract.Create(scope.Worker, "19").Single(candidate => candidate.ProtocolTool.Name == name);
        var arguments = new Dictionary<string, JsonElement> {
            ["softwarePath"] = JsonSerializer.SerializeToElement("PLC_1"),
            ["exportPath"] = JsonSerializer.SerializeToElement(Path.Combine(scope.Gate, "alarms.xlsx"))
        };
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = arguments }
        };
        var result = await tool.InvokeAsync(request);
        var error = result.StructuredContent!["error"]!;
        Assert.False((bool?)result.StructuredContent["ok"]);
        Assert.Equal("read-failed", (string?)result.StructuredContent["meta"]?["outcome"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)error["code"]);
        Assert.Equal("FixtureNonRecoverableException", (string?)error["details"]?["evidence"]?["portalLost"]);
        Assert.Contains("ConnectIsolatedPortal", (string?)error["message"]);
        Assert.Contains("DisconnectPortal then ConnectPortal", (string?)error["message"]);
        Assert.True(scope.Worker.RecoveryNeeded);
    }

    [Fact]
    public async Task File_only_block_inspection_keeps_the_missing_rebind_target()
    {
        using var scope = new Scope();
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        scope.Facts(null);
        const string name = "GetPlcBlockEditCapabilities";
        var tool = PortedToolContract.Create(scope.Worker, "19").Single(candidate => candidate.ProtocolTool.Name == name);
        var path = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "templates", "plc", "block-xml", "DB_HMI_Interface.xml");
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = new Dictionary<string, JsonElement> { ["filePath"] = JsonSerializer.SerializeToElement(path) } }
        };
        var services = FoundationHostPorts.CreateServices(scope.Worker, "19", () => new() { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("19", services, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var result = await tool.InvokeAsync(request);
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.True(scope.Worker.RecoveryNeeded);
        Assert.DoesNotContain(scope.Trace, IsProcessRead);
        Assert.DoesNotContain(scope.Trace, row => (string?)row["method"] == "adapter.plc-analysis.ExportBlockDocument");
    }

    [Fact]
    public async Task Reopened_project_in_a_new_process_rebinds_on_the_same_worker_generation()
    {
        using var scope = new Scope();
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Bind();
        scope.StopOwnedChild();
        scope.Facts(null);
        var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("ReadBlocks", new(), CancellationToken.None));
        Assert.Equal("tia-process-gone", refused.Condition);
        int newPid = scope.Child.Id;
        scope.Facts(scope.ProjectFile, newPid);
        var blocks = await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.Equal("fixture", (string?)blocks?[0]?["Name"]);
        var attach = scope.Trace.Last(row => (string?)row["method"] == "adapter.Attach");
        Assert.Equal(newPid, (int?)attach["arguments"]?["processId"]);
        Assert.Equal(newPid, scope.Child.Id);
        Assert.Equal(2, (long?)scope.Worker.OperatorSnapshot()["generation"]);
        Assert.False(scope.Worker.RecoveryNeeded);
    }

    [Fact]
    public async Task Disconnect_then_connect_replaces_an_exited_binding()
    {
        using var scope = new Scope();
        using var production = new ProductionWorkerBudgets(scope.Worker);
        await scope.Worker.Call("ReadPortalProcessProjects", new(), CancellationToken.None);
        int leasedPid = scope.Child.Id;
        await scope.Worker.Call("Attach", new() { ["processId"] = leasedPid }, CancellationToken.None);
        scope.StopOwnedChild();
        var selection = new JsonObject { ["processId"] = Environment.ProcessId };
        var refused = await Assert.ThrowsAsync<AdapterPreconditionException>(() => scope.Worker.Call("Attach", selection, CancellationToken.None));
        Assert.Equal("session-lost", refused.Condition);
        Assert.Contains("DisconnectPortal first", refused.Message);
        await scope.Worker.Call("Disconnect", new(), CancellationToken.None);
        await scope.Worker.Call("Attach", selection, CancellationToken.None);
        Assert.Equal(Environment.ProcessId, (int?)scope.Trace.Last(row => (string?)row["method"] == "adapter.Attach")["arguments"]?["processId"]);
        Assert.False(scope.Worker.RecoveryNeeded);
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
        var context = new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(services);
        using var session = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(context);
        string token = context.BatchPlans.Add(new() { State = "old", Project = scope.ProjectFile }, DateTime.UtcNow);
        string handle = toolpipeline::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "fixture", "retained", 4).id;
        services.ResetExports = () => {
            context.Exports.Dispose();
            context.Exports = new();
        };
        try
        {
            scope.StopOwnedChild();
            await scope.Worker.Call("ReadBlocks", new(), CancellationToken.None);
            Assert.Throws<ArgumentException>(() => context.BatchPlans.Take(token, DateTime.UtcNow));
            Assert.Equal("retained", toolpipeline::TiaMcp.FoundationHost.SessionExportStore.Get(handle)!.Content);
            var restarted = await services.Restart(true, CancellationToken.None);
            Assert.True((bool?)restarted["restarted"]);
            Assert.Null(toolpipeline::TiaMcp.FoundationHost.SessionExportStore.Get(handle));
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
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
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
        // Inject a native deadline, rather than a timeout of the shared status probe that just completed.
        typeof(WorkerClient).GetField("unansweredHostProbe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scope.Worker, false);
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
        Assert.Equal($"TIA PID {Environment.ProcessId} was started by the worker that exited. "
            + "Its lifetime is no longer owned by this session. Select a TIA instance deliberately with ConnectPortal or ConnectIsolatedPortal. "
            + $"Project: {scope.ProjectFile}", error.Message);
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
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
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
        var services = FoundationHostPorts.CreateServices(scope.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
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
