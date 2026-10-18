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
using EngineClient = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient;
using EngineOptions = foundationhost::TiaMcp.FoundationHost.HostOptions;
using Pipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using Server = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(BundleSelectionCollection.Name)]
public sealed class SharedWorkerRegressionTests
{
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
        Assert.True((bool?)ack?["RequiresTiaRestart"]);
        Assert.False((bool?)ack?["WorkerAcknowledged"]);
        if (pending != null) await Assert.ThrowsAnyAsync<Exception>(() => pending);
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new Pipeline(release, services, Array.Empty<McpServerTool>());
        using var context = pipeline.EnterSession();
        var next = await new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetState"), worker), release)
            .InvokeAsync(Request("GetSessionState"));
        Assert.Equal("PRECONDITION_FAILED", (string?)next.StructuredContent?["error"]?["code"]);
        Assert.False((bool?)worker.OperatorSnapshot()["sessionLocked"]);
        Assert.Null(Server.SessionPrecheckRefusal("CompileDevice"));
        if (release is "20" or "21")
        {
            using var engine = fixture.Engine();
            await engine.Status(CancellationToken.None);
            var engineChild = Field<Process>(engine, "process");
            var engineChannel = Field<ChannelClient>(engine, "channel");
            Task<string>? enginePending = null;
            if (busy)
            {
                fixture.ClearBusy();
                enginePending = engineChannel.CallAsync("adapter.FixtureHang", "{}", BindingChange.None, true, TimeSpan.FromSeconds(20), CancellationToken.None);
                await fixture.WaitBusy();
            }
            else { WorkerShutdown.KillOwned(engineChild); Assert.True(engineChild.WaitForExit(5000)); }
            await engine.Call("Disconnect", new(), CancellationToken.None);
            if (enginePending != null) await Assert.ThrowsAnyAsync<Exception>(() => enginePending);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => engine.Call("ReadState", new(), CancellationToken.None));
            var expected = FoundationV4Result.Failure(release, "GetSessionState", "compare", false, false, null, error).StructuredContent!;
            Assert.True(JsonNode.DeepEquals(expected["error"], next.StructuredContent?["error"]));
            Assert.Equal((string?)engine.Snapshot()["state"], (string?)services.Snapshot()["state"]);
            Assert.Equal(engine.OperatorSnapshot().ToJsonString(), worker.OperatorSnapshot().ToJsonString());
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
    public async Task Engine_readiness_probe_uses_the_owned_candidate_lane(string release)
    {
        using var fixture = new Fixture(release);
        using var engine = fixture.Engine();
        using var lane = await engine.AcquireLane(CancellationToken.None);
        engine.ActivateLane(lane);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await engine.PrepareReadiness(deadline.Token);
        Assert.True((bool?)engine.Snapshot()["readiness"]?["ready"]);
        Assert.Null(await engine.AcquireLane(deadline.Token));
        lane?.Dispose();
        Assert.Equal("Ready", (string?)engine.Snapshot()["state"]);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task CallTool_compile_wrapper_observes_cold_readiness_inside_the_target_lane(string release)
    {
        using var fixture = new Fixture(release);
        using var engine = fixture.Engine();
        var inner = foundationhost::TiaMcp.FoundationHost.PortedToolContract.Create(engine, release)
            .OfType<foundationhost::TiaMcp.FoundationHost.FoundationTool>().Single(t => t.ProtocolTool.Name == "CompileDevice");
        var target = new foundationhost::TiaMcp.FoundationHost.FoundationV4Tool(inner, release, null,
            readinessForTest: () => new JsonObject { ["ready"] = true });
        var services = foundationhost::TiaMcp.FoundationHost.FoundationHostPorts.CreateServices(engine, release,
            () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new Pipeline(release, services, new McpServerTool[] { target });
        Assert.Null(engine.OperatorSnapshot()["pid"]);
        var arguments = new JsonObject { ["devicePath"] = new JsonArray("PLC1") };
        var properties = target.ProtocolTool.InputSchema.GetProperty("properties");
        if (properties.TryGetProperty("mode", out _)) arguments["mode"] = "preview";
        if (properties.TryGetProperty("dryRun", out _)) arguments["dryRun"] = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await Task.Run(() => {
            using var context = pipeline.EnterSession();
            using var request = Server.UseProgressRequest(Request("CallTool"), deadline.Token);
            return Server.CallTool("CompileDevice", new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(arguments)));
        });
        Assert.False(deadline.IsCancellationRequested);
        Assert.NotEqual("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True((bool?)engine.Snapshot()["readiness"]?["ready"], result.StructuredContent?.ToJsonString());
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

    [Fact]
    public void Candidate_mappers_keep_the_original_top_level_exception_boundary()
    {
        var authored = new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Authored inner diagnostic.", isArgument: false);
        foreach (bool io in new[] { false, true })
        {
            Exception original = io ? new IOException("Outer fixture failure.") : new InvalidOperationException("Outer fixture failure.");
            Exception nested = io ? new IOException(original.Message, authored) : new InvalidOperationException(original.Message, authored);
            Assert.Equal(TiaMcp.Logic.V4.V4Json.Serialize(TiaMcp.Logic.V4.SourceSession.Map(original)),
                TiaMcp.Logic.V4.V4Json.Serialize(TiaMcp.Logic.V4.SourceSession.Map(nested)));
        }
    }

    [Theory, InlineData("14sp1", false), InlineData("15.1", false), InlineData("16", false), InlineData("17", false),
        InlineData("18", false), InlineData("19", false), InlineData("20", false), InlineData("21", false),
        InlineData("20", true), InlineData("21", true)]
    public async Task Restart_and_dispose_do_not_replace_or_lose_a_worker_after_identity_refusal(string release, bool engineBackend)
    {
        using var fixture = new Fixture(release);
        using var engine = engineBackend ? fixture.Engine() : null;
        object worker = (object?)engine ?? fixture.Worker;
        if (engine != null) await engine.Status(CancellationToken.None);
        else await fixture.Worker.Call("ReadState", new(), CancellationToken.None);
        var child = Field<Process>(worker, "process");
        var channel = Field<ChannelClient>(worker, "channel");
        var pending = channel.CallAsync("adapter.FixtureHang", "{}", BindingChange.None, true, TimeSpan.FromSeconds(20), CancellationToken.None);
        await fixture.WaitBusy();
        string expected = child.StartInfo.FileName;
        child.StartInfo.FileName = Path.Combine(AppContext.BaseDirectory, "other-worker.exe");
        try
        {
            var error = await Record.ExceptionAsync(async () => {
                if (engine != null) await engine.Restart(true, CancellationToken.None);
                else await fixture.Worker.RestartHostWorker(true, CancellationToken.None);
            });
            Assert.NotNull(error);
            Assert.Equal("WorkerTerminationException", error.GetType().Name);
            Assert.Same(child, Field<Process>(worker, "process"));
            Assert.False(child.HasExited);
            var snapshot = engine?.Snapshot() ?? fixture.Worker.SupervisorSnapshot(new JsonObject { ["ready"] = true });
            Assert.True((bool?)snapshot["sessionLocked"]);
            Assert.Equal(1, (long?)(engine?.OperatorSnapshot() ?? fixture.Worker.OperatorSnapshot())["generation"]);
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
        private readonly string adapter;
        private readonly byte[]? previous;
        private readonly string? previousLease = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        private readonly string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "r2-worker-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly WorkerClient Worker;
        internal Fixture(string release)
        {
            this.release = release;
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", root);
            adapter = Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll");
            previous = File.Exists(adapter) ? File.ReadAllBytes(adapter) : null;
            File.WriteAllText(adapter, "offline session fixture");
            Worker = new(release, Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe"), AppContext.BaseDirectory, true)
                { Bundled = false, SharedSession = release is "20" or "21" };
        }
        internal EngineClient Engine()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            return new(new EngineOptions { ReleaseKey = release, WorkerExe = exe, EngineWorkerExe = exe,
                ApiDirectory = AppContext.BaseDirectory, BundleRoot = AppContext.BaseDirectory, NativeEnabled = true, BundledWorker = false }, EngineClient.Hash(exe));
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
            if (previous == null) File.Delete(adapter); else File.WriteAllBytes(adapter, previous);
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", previousLease);
            Directory.Delete(root, true);
        }
    }
}
