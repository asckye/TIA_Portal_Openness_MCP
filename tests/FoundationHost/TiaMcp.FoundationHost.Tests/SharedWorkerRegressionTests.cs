extern alias foundationhost;
extern alias enginehost;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Xunit;
using Pipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using Server = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(BundleSelectionCollection.Name)]
public sealed class SharedWorkerRegressionTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void Online_monitoring_safety_self_test_runs_the_reflection_guard(string release)
    {
        using var fixture = new Fixture(release);
        var services = FoundationHostPorts.CreateServices(fixture.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        var response = new enginehost::TiaMcpServer.ModelContextProtocol.HostMetaTools().RunOnlineMonitoringSafetySelfTest();
        Assert.Equal("pass", Assert.Single(response.Items!, item => item.Id == "safety.reflection-hard-deny-semantics").Status);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Acceptance_report_writes_both_files(string release)
    {
        using var fixture = new Fixture(release);
        var services = FoundationHostPorts.CreateServices(fixture.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        string directory = Path.Combine(fixture.Root, "acceptance");
        var response = await new enginehost::TiaMcpServer.ModelContextProtocol.HostMetaTools().GenerateAcceptanceReport(directory);
        Assert.True(File.Exists(response.MarkdownPath));
        Assert.True(File.Exists(response.JsonPath));
        Assert.NotEmpty(File.ReadAllText(response.MarkdownPath));
        Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(response.JsonPath)));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Compatibility_reads_host_metadata_while_the_worker_is_faulted(string release)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        await worker.Observe("status", new(), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => worker.Call("FixtureUnknown", new(), CancellationToken.None));
        Assert.True(worker.WorkerFaulted);
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, fixture.Root);
        string assembly = typeof(TiaMcp.Adapters.Contracts.NativeFailureText).Assembly.Location;
        string copied = Path.Combine(fixture.Root, "Siemens.Engineering.HostMetadata.dll");
        File.Copy(assembly, copied);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        var observed = await services.Observe("assemblies", new(), CancellationToken.None);
        Assert.Equal(copied, (string?)Assert.Single(observed!.AsArray())!["location"]);
        var response = new enginehost::TiaMcpServer.ModelContextProtocol.EngineeringDiagnosticsTools().GetOpennessCompatibilityV4();
        Assert.False(response.IsError == true, response.StructuredContent?.ToJsonString());
    }

    public static IEnumerable<object[]> DisconnectCases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .SelectMany(release => new[] { new object[] { release, false }, new object[] { release, true } });

    [Theory, MemberData(nameof(DisconnectCases))]
    public async Task Forced_disconnect_retains_the_profile_reply_snapshot_and_approval_state(string release, bool busy)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        await worker.Call("ReadState", new(), CancellationToken.None);
        var child = Field<Process>(worker, "process");
        var channel = Field<ChannelClient>(worker, "channel");
        Task<string>? pending = null;
        if (busy)
        {
            // A real in-flight request, without manufacturing an unknown native outcome in the host.
            pending = channel.CallAsync("adapter.FixtureHang", "{}", BindingChange.None, true, TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.True(channel.InFlight);
            await fixture.WaitBusy();
        }
        else { WorkerShutdown.KillOwned(child); Assert.True(child.WaitForExit(5000)); }
        var ack = await worker.Call("Disconnect", new(), CancellationToken.None);
        Assert.Equal(busy, (bool?)ack?["RequiresTiaRestart"]);
        Assert.False((bool?)ack?["WorkerAcknowledged"]);
        if (pending != null) await Assert.ThrowsAnyAsync<Exception>(() => pending);
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        var next = await new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetSessionState"), worker), release)
            .InvokeAsync(Request("GetSessionState"));
        Assert.Equal("PRECONDITION_FAILED", (string?)next.StructuredContent?["error"]?["code"]);
        Assert.False((bool?)worker.OperatorSnapshot()["sessionLocked"]);
        Assert.Null(Server.SessionPrecheckRefusal("CompileDevice"));
        if (release is "20" or "21")
        {
            using var production = fixture.ProductionWorker();
            await production.Status(CancellationToken.None);
            var productionChild = Field<Process>(production.Client, "process");
            var productionChannel = Field<ChannelClient>(production.Client, "channel");
            Task<string>? productionPending = null;
            if (busy)
            {
                fixture.ClearBusy();
                productionPending = productionChannel.CallAsync("adapter.FixtureHang", "{}", BindingChange.None, true, TimeSpan.FromSeconds(20), CancellationToken.None);
                await fixture.WaitBusy();
            }
            else { WorkerShutdown.KillOwned(productionChild); Assert.True(productionChild.WaitForExit(5000)); }
            await production.Call("Disconnect", new(), CancellationToken.None);
            if (productionPending != null) await Assert.ThrowsAnyAsync<Exception>(() => productionPending);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => production.Call("ReadState", new(), CancellationToken.None));
            var expected = FoundationV4Result.Failure(release, "GetSessionState", "compare", false, false, null, error).StructuredContent!;
            Assert.True(JsonNode.DeepEquals(expected["error"], next.StructuredContent?["error"]));
            Assert.Equal((string?)production.Snapshot()["state"], (string?)services.Snapshot()["state"]);
            Assert.Equal(((foundationhost::TiaMcp.FoundationHost.IFoundationWorkerSupervisor)production.Client).OperatorSnapshot().ToJsonString(), worker.OperatorSnapshot().ToJsonString());
            Assert.Null(services.Snapshot()["fault"]);
        }
        else
        {
            // Frozen master semantics: its terminal Process remains, without becoming a session fault.
            Assert.Equal("Running", (string?)worker.HostSnapshot()["state"]);
            Assert.Equal("The request precondition failed before operation.", (string?)next.StructuredContent?["error"]?["message"]);
            Assert.False(services.Faulted);
        }
    }

    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public async Task Shared_status_reads_are_cached_when_cold_or_the_lane_is_held(string release, bool warm)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        if (warm) await worker.Observe("status", new(), CancellationToken.None);
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        var gate = Field<SemaphoreSlim>(worker, "serial");
        await gate.WaitAsync();
        Task<JsonObject>? read = null;
        try
        {
            read = services.Status(CancellationToken.None);
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(500)));
            Assert.False(services.Faulted);
        }
        finally { gate.Release(); if (read != null) await read; }
        var pid = worker.OperatorSnapshot()["pid"];
        Assert.Equal(warm, pid != null);
        await services.Status(CancellationToken.None);
        Assert.Equal(warm, worker.OperatorSnapshot()["pid"] != null);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Non_shared_readiness_does_not_add_a_managed_worker_operation(string release)
    {
        using var worker = new WorkerClient(release, "missing-worker", "missing-api", true) { Bundled = false };
        await worker.PrepareReadiness(CancellationToken.None);
        Assert.Null(worker.OperatorSnapshot()["pid"]);
        Assert.False(worker.WorkerFaulted);
    }

    [Theory, InlineData("20", "doctor"), InlineData("21", "doctor"),
        InlineData("20", "self-test"), InlineData("21", "self-test"),
        InlineData("20", "compatibility"), InlineData("21", "compatibility")]
    public async Task No_tia_passive_diagnostics_do_not_require_a_startable_worker(string release, string diagnostic)
    {
        using var worker = new WorkerClient(release, "missing-worker", "missing-api", true) { SharedSession = true, Bundled = false };
        JsonObject Ready() => FoundationPassiveDiagnostics.Readiness(release, _ => null, () => false);
        var services = FoundationHostPorts.CreateServices(worker, release, Ready, null);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        Assert.False((bool?)services.Snapshot()["readiness"]?["ready"]);
        Assert.False(string.IsNullOrEmpty((string?)services.Snapshot()["readiness"]?["recommendedFix"]));
        if (diagnostic == "doctor")
        {
            var result = await Server.GetEnvironmentDiagnosticsV4(fix: false);
            Assert.False(result.IsError == true, result.StructuredContent?.ToJsonString());
            var data = result.StructuredContent!["data"]!;
            Assert.False((bool?)data["ready"]);
            Assert.Contains(data["checks"]!.AsArray(), check => !string.IsNullOrEmpty((string?)check?["fix"]));
            Assert.False(string.IsNullOrEmpty((string?)data["recommendedFixZh"]));
        }
        else if (diagnostic == "self-test")
        {
            var result = await new enginehost::TiaMcpServer.ModelContextProtocol.HostMetaTools().RunCapabilitySelfTest();
            var group = Assert.Single(result.Items!, item => item.Id == "openness.user-group");
            Assert.Equal("fail", group.Status);
            Assert.Equal("Current user is not in the Openness group, or membership could not be confirmed.", group.Detail);
        }
        else
        {
            var result = new enginehost::TiaMcpServer.ModelContextProtocol.EngineeringDiagnosticsTools().GetOpennessCompatibilityV4();
            Assert.False(result.IsError == true, result.StructuredContent?.ToJsonString());
        }
        Assert.Null(worker.OperatorSnapshot()["pid"]);
        Assert.False(services.Faulted);
    }

    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public async Task Available_worker_passive_observations_keep_the_worker_reply(string release, bool ready)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = ready,
            ["environment"] = new JsonObject { ["tiaInstallPath"] = AppContext.BaseDirectory } }, AppContext.BaseDirectory);
        foreach (var operation in new[] { "environment", "group", "assemblies" })
        {
            var expected = await worker.Observe(operation, new(), CancellationToken.None);
            var actual = await services.Observe(operation, new(), CancellationToken.None);
            Assert.Equal(expected?.ToJsonString(), actual?.ToJsonString());
        }
    }

    [Theory, InlineData("20", false), InlineData("21", false), InlineData("20", true), InlineData("21", true)]
    public async Task Explicit_supervisor_reads_start_only_when_the_cold_lane_is_free(string release, bool restartPreview)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        JsonObject Read() => (restartPreview ? Server.RestartOpennessWorker(false) : Server.ReadOpennessWorkerStatus()).Meta!["worker"]!.AsObject();
        var gate = Field<SemaphoreSlim>(worker, "serial");
        await gate.WaitAsync();
        Task<JsonObject>? read = null;
        try
        {
            read = Task.Run(Read);
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(500)));
            Assert.Equal("NotStarted", (string?)(await read)["state"]);
            Assert.Null(worker.OperatorSnapshot()["pid"]);
            Assert.False(services.Faulted);
        }
        finally { gate.Release(); if (read != null) await read; }
        Assert.Equal("Ready", (string?)Read()["state"]);
        Assert.NotNull(worker.OperatorSnapshot()["pid"]);
        Assert.False(services.Faulted);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Worker_readiness_probe_uses_the_owned_candidate_lane(string release)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        using var lane = await worker.AcquireLane(CancellationToken.None);
        WorkerClient.ActivateLane(lane);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.PrepareReadiness(deadline.Token);
        Assert.True((bool?)worker.SharedStatus?["readiness"]?["ready"]);
        Assert.Null(await worker.AcquireLane(deadline.Token));
        lane?.Dispose();
        Assert.Equal("Ready", (string?)worker.SupervisorSnapshot(new JsonObject { ["ready"] = false })["state"]);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task CallTool_compile_wrapper_observes_cold_readiness_inside_the_target_lane(string release)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        var inner = PortedToolContract.Create(worker, release)
            .OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == "CompileDevice");
        var target = new FoundationV4Tool(inner, release,
            readinessForTest: () => new JsonObject { ["ready"] = true });
        var services = FoundationHostPorts.CreateServices(worker, release,
            () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new Pipeline(release, services, new McpServerTool[] { target });
        Assert.Null(worker.OperatorSnapshot()["pid"]);
        var arguments = new JsonObject { ["devicePath"] = new JsonArray("PLC1") };
        var properties = target.ProtocolTool.InputSchema.GetProperty("properties");
        if (properties.TryGetProperty("mode", out _)) arguments["mode"] = "preview";
        if (properties.TryGetProperty("dryRun", out _)) arguments["dryRun"] = true;
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string? deadlinePool = null;
        using var deadlineRegistration = deadline.Token.Register(() => Volatile.Write(ref deadlinePool, PoolState()));
        var result = await Task.Run(async () => {
            using var context = pipeline.EnterSession();
            using var request = Server.UseProgressRequest(Request("CallTool"), deadline.Token);
            return await Server.CallToolAsync("CompileDevice", new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(arguments)));
        });
        Assert.False(deadline.IsCancellationRequested, "deadlinePool=" + Volatile.Read(ref deadlinePool) + "; returnedPool=" + PoolState()
            + "; elapsedMs=" + elapsed.Elapsed.TotalMilliseconds + "; worker=" + worker.SupervisorSnapshot(new JsonObject { ["ready"] = false }).ToJsonString());
        Assert.NotEqual("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True((bool?)worker.SharedStatus?["readiness"]?["ready"], result.StructuredContent?.ToJsonString());
    }

    private static string PoolState()
    {
        ThreadPool.GetAvailableThreads(out int available, out _);
        ThreadPool.GetMaxThreads(out int maximum, out _);
        return "threads=" + ThreadPool.ThreadCount + ", pending=" + ThreadPool.PendingWorkItemCount + ", busy=" + (maximum - available);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Cold_bridge_call_completes_on_a_pool_capped_at_four_threads(string release)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--bridge-pool-probe"); start.ArgumentList.Add(release);
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        start.Environment["DOTNET_ThreadPool_ForceMinWorkerThreads"] = "4";
        start.Environment["DOTNET_ThreadPool_ForceMaxWorkerThreads"] = "4";
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally
        {
            if (!child.HasExited)
            {
                string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory) + Path.DirectorySeparatorChar;
                Assert.True(Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(Path.GetFullPath(executable), child.MainModule!.FileName, ignoreCase: true);
                WorkerShutdown.KillOwned(child);
                Assert.True(child.WaitForExit(5000));
            }
        }
        string result = await stdout; string errors = await stderr;
        Assert.True(child.ExitCode == 0, result + errors);
        var probe = JsonNode.Parse(result)!;
        Assert.Equal(4, (int?)probe["maximumWorkers"]);
        Assert.True((bool?)probe["ready"], result);
        Assert.NotEqual("CANCELLED", (string?)probe["result"]?["error"]?["code"]);
        // This fixture has no bound project; completion and readiness are independent of the target's precondition result.
        Assert.IsType<JsonObject>(probe["result"]);
    }

    internal static async Task<int> RunBridgePoolProbeAsync(string release)
    {
        ThreadPool.GetMaxThreads(out int maximum, out _);
        Assert.Equal(4, maximum);
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        var inner = PortedToolContract.Create(worker, release)
            .OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == "CompileDevice");
        var target = new FoundationV4Tool(inner, release,
            readinessForTest: () => new JsonObject { ["ready"] = true });
        var services = FoundationHostPorts.CreateServices(worker, release,
            () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new Pipeline(release, services, new McpServerTool[] { target });
        Assert.Null(worker.OperatorSnapshot()["pid"]);
        var arguments = new JsonObject { ["devicePath"] = new JsonArray("PLC1") };
        var properties = target.ProtocolTool.InputSchema.GetProperty("properties");
        if (properties.TryGetProperty("mode", out _)) arguments["mode"] = "preview";
        if (properties.TryGetProperty("dryRun", out _)) arguments["dryRun"] = true;
        var result = await Task.Run(async () => {
            var request = Request("CallTool");
            request.Params = new() { Name = "CallTool", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                new JsonObject { ["name"] = "CompileDevice", ["arguments"] = arguments }.ToJsonString()) };
            return await pipeline.AllTools.Single(t => t.ProtocolTool.Name == "CallTool").InvokeAsync(request);
        });
        Console.WriteLine(new JsonObject { ["maximumWorkers"] = maximum, ["ready"] = worker.SharedStatus?["readiness"]?["ready"]?.DeepClone(),
            ["result"] = result.StructuredContent?.DeepClone() }.ToJsonString());
        return 0;
    }

    [Fact]
    public async Task Shutdown_identity_refusal_is_not_the_already_exited_exception()
    {
        using var fixture = new Fixture("20");
        await fixture.Worker.Observe("status", new(), CancellationToken.None);
        var child = Field<Process>(fixture.Worker, "process");
        var error = Record.Exception(() => WorkerShutdown.KillOwned(child, _ => Path.Combine(AppContext.BaseDirectory, "other-worker.exe")));
        Assert.NotNull(error);
        Assert.IsNotType<InvalidOperationException>(error);
        Assert.Equal("WorkerTerminationException", error.GetType().Name);
        Assert.False(child.HasExited);
    }

    // TerminateForDisconnect closes the channel before it stops the worker, and an idle worker then leaves on its own, possibly
    // while its image is probed. Barrier: the probe closes the input and waits for that exit; it then fails or finds no module,
    // as reading the modules of a process that has left does. The owned child is stopped, so neither outcome is a refusal
    // (FoundationRecoveryTests under load, P8-stab-1).
    [Theory, InlineData(true), InlineData(false)]
    public async Task Shutdown_of_a_worker_that_exits_during_the_image_probe_is_complete(bool probeThrows)
    {
        using var fixture = new Fixture("20");
        await fixture.Worker.Observe("status", new(), CancellationToken.None);
        var child = Field<Process>(fixture.Worker, "process");
        var channel = Field<ChannelClient>(fixture.Worker, "channel");
        var error = Record.Exception(() => WorkerShutdown.KillOwned(child, process =>
        {
            channel.Dispose();
            Assert.True(process.WaitForExit(30000), "The idle worker did not leave after its input closed.");
            return probeThrows ? throw new Win32Exception(299) : null;
        }));
        Assert.Null(error);
        Assert.True(child.HasExited);
    }

    // Another shard's host or worker hashes the shared adapter identity while this fixture starts and ends: ArgumentRules.HashFile
    // and the worker open it for reading with FileShare.Read. Barrier: hold that read handle across the whole fixture lifetime.
    // The fixture must neither rewrite nor remove the identity under it (P8-stab-1).
    [Fact]
    public void Worker_fixture_leaves_the_adapter_identity_that_another_shard_is_hashing()
    {
        string adapter = Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.20.dll");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(adapter, "offline session fixture");
        using (new FileStream(adapter, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new Fixture("20")) { }
        Assert.Equal("offline session fixture", File.ReadAllText(adapter));
    }

    [Fact]
    public async Task Shutdown_image_probe_failure_is_a_retained_refusal()
    {
        using var fixture = new Fixture("20");
        await fixture.Worker.Observe("status", new(), CancellationToken.None);
        var child = Field<Process>(fixture.Worker, "process");
        var error = Record.Exception(() => WorkerShutdown.KillOwned(child, _ => throw new Win32Exception(299)));
        Assert.NotNull(error);
        Assert.Equal("WorkerTerminationException", error.GetType().Name);
        Assert.IsType<Win32Exception>(error.InnerException);
        Assert.False(child.HasExited);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Shutdown_stop_retains_a_live_process_and_evidence_after_refusal(bool probeFails)
    {
        using var fixture = new Fixture("20");
        await fixture.Worker.Observe("status", new(), CancellationToken.None);
        var child = Field<Process>(fixture.Worker, "process");
        var channel = Field<ChannelClient>(fixture.Worker, "channel");
        var pending = channel.CallAsync("adapter.FixtureHang", "{}", BindingChange.None, true, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.True(channel.InFlight);
        await fixture.WaitBusy();
        var refusal = WorkerShutdown.Stop(child, channel, _ => probeFails ? throw new Win32Exception(299)
            : Path.Combine(AppContext.BaseDirectory, "other-worker.exe"));
        Assert.NotNull(refusal);
        Assert.False(child.HasExited);
        Assert.Same(child, Field<Process>(fixture.Worker, "process"));
        Assert.Equal(probeFails, refusal.InnerException is Win32Exception);
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public void Suppressed_session_ended_errors_use_the_untyped_mapper_policy(string release)
    {
        var typed = WorkerClient.SessionEnded(release, "Disconnect ended this session.");
        var original = new InvalidOperationException(typed.Message);
        var policy = typeof(TiaMcp.Logic.V4.HostBehavior).Assembly.GetType("TiaOpenness.Shared.HostFailurePolicy")!;
        var precondition = policy.GetMethod("Precondition", BindingFlags.NonPublic | BindingFlags.Static)!;
        var diagnostic = typeof(TiaMcp.Logic.V4.HostBehavior).GetMethod("AdmissionDiagnostic", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(precondition.Invoke(null, new object[] { typed }));
        Assert.Null(diagnostic.Invoke(null, new object?[] { typed, null, null, null }));
        Assert.Null(diagnostic.Invoke(null, new object?[] { typed, -32603, "rejected-before-operation", typed.GetType().Name }));
        var classify = policy.GetMethod("Classify", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (bool dispatched in new[] { false, true }) foreach (bool readOnly in new[] { false, true })
            Assert.Equal(classify.Invoke(null, new object?[] { original, dispatched, readOnly, null, null, null, true }),
                classify.Invoke(null, new object?[] { typed, dispatched, readOnly, null, null, null, true }));
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"),
        InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Restart_and_dispose_do_not_replace_or_lose_a_worker_after_identity_refusal(string release)
    {
        using var fixture = new Fixture(release);
        var worker = fixture.Worker;
        await worker.Call("ReadState", new(), CancellationToken.None);
        var child = Field<Process>(worker, "process");
        var channel = Field<ChannelClient>(worker, "channel");
        var pending = channel.CallAsync("adapter.FixtureHang", "{}", BindingChange.None, true, TimeSpan.FromSeconds(20), CancellationToken.None);
        await fixture.WaitBusy();
        string expected = child.StartInfo.FileName;
        child.StartInfo.FileName = Path.Combine(AppContext.BaseDirectory, "other-worker.exe");
        try
        {
            var error = await Record.ExceptionAsync(async () => {
                await worker.RestartHostWorker(true, CancellationToken.None);
            });
            Assert.NotNull(error);
            Assert.Equal("WorkerTerminationException", error.GetType().Name);
            Assert.Same(child, Field<Process>(worker, "process"));
            Assert.False(child.HasExited);
            var snapshot = worker.SupervisorSnapshot(new JsonObject { ["ready"] = true });
            Assert.True((bool?)snapshot["sessionLocked"]);
            Assert.Equal(1, (long?)worker.OperatorSnapshot()["generation"]);
            Assert.Null(Field<object?>(worker, "disconnectAcknowledgement"));
            ((IDisposable)worker).Dispose();
            Assert.Same(child, Field<Process>(worker, "process"));
            Assert.False(child.HasExited);
        }
        finally { child.StartInfo.FileName = expected; }
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static RequestContext<CallToolRequestParams> Request(string name) => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
        Params = new() { Name = name, Arguments = new Dictionary<string, JsonElement>() } };
    private sealed class Fixture : IDisposable
    {
        private readonly string release;
        private readonly string? previousLease = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        private readonly string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "r2-worker-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly WorkerClient Worker;
        internal string Root => root;
        internal Fixture(string release)
        {
            this.release = release;
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
            // Parallel shards share this output directory and hash the identity while their workers start: install it atomically and
            // leave it in place. Truncating, restoring or deleting it here refused or killed another shard's worker (P8-stab-1).
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            Worker = new(release, Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe"), AppContext.BaseDirectory, true)
                { Bundled = false, SharedSession = release is "20" or "21" };
        }
        internal WorkerBackendFixture ProductionWorker()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            return new(new foundationhost::TiaMcp.FoundationHost.HostOptions { ReleaseKey = release, WorkerExe = exe,
                ApiDirectory = AppContext.BaseDirectory, BundleRoot = AppContext.BaseDirectory, NativeEnabled = true, BundledWorker = false });
        }
        internal void ClearBusy() => File.Delete(Path.Combine(root, "busy"));
        internal async Task WaitBusy()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(Path.Combine(root, "busy"))) await Task.Delay(10, deadline.Token);
        }
        public void Dispose()
        {
            Worker.Dispose();
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previousLease);
            Directory.Delete(root, true);
        }
    }
}
