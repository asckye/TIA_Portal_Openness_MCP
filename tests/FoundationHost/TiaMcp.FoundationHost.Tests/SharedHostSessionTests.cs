extern alias foundationhost;
extern alias enginehost;
using System.Text.Json.Nodes;
using Xunit;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;
using Host = foundationhost::TiaMcp.FoundationHost.SharedReleaseHost;
using Profile = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;

[Collection(BundleSelectionCollection.Name)]
public sealed class SharedHostSessionTests
{
    [Theory] [InlineData("20")] [InlineData("21")]
    public async Task Operator_cold_worker_fault_is_dispatched_and_locks_the_same_session(string release)
    {
        using var fixture = new OperatorSessionTests.Fixture(release);
        using var session = await fixture.Create();
        await using var ai = await OperatorSessionTests.Peer.Ai(session);
        using var registration = fixture.Register();
        using var membership = registration.Register(session);
        await using var panel = await OperatorSessionTests.Peer.Operator(registration, session.SessionKey);
        var fault = await panel.Tool(SessionFixtureTool.Name, new() { ["fault"] = true }, false);
        Assert.True((string?)fault["meta"]?["outcome"] == "unknown", fault.ToJsonString());
        Assert.True((bool?)session.Snapshot()["sessionLocked"], session.Snapshot().ToJsonString());
        var refused = await ai.Tool("GetSessionState", ok: false);
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)refused["error"]?["code"]);
    }

    [Theory]
    [InlineData("14sp1", false)] [InlineData("15.1", false)] [InlineData("16", false)] [InlineData("17", false)]
    [InlineData("18", false)] [InlineData("19", false)] [InlineData("20", false)] [InlineData("21", false)]
    [InlineData("20", true)] [InlineData("21", true)]
    public async Task Every_release_and_worker_choice_creates_the_same_lazy_session(string release, bool plc)
    {
        var root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var arguments = new List<string> { "--bundle-root", root, "--release-key", release, "--profile", "full" };
        if (plc) arguments.Add("--plc-worker");
        var options = Options.Parse(arguments.ToArray(), root);
        var data = Profile.Releases[release];
        Assert.Equal(plc || !data.SharedSession, options.EngineWorkerExe == null);
        Assert.Equal(plc, options.PlcWorker);
        using var session = await Host.CreateHostSession(options, false);
        Assert.Equal(data.Instructions, session.Instructions);
        Assert.NotEmpty(session.Tools);
        Assert.Equal(session.Tools.Select(t => t.ProtocolTool.Name), session.OperatorTools.Select(t => t.ProtocolTool.Name));
        Assert.Null(session.Snapshot()["worker"]?["pid"]);
        Assert.False((bool?)session.Snapshot()["sessionLocked"]);
        using var panel = session.AttachOperator();
        Assert.Equal(1, (int?)session.Snapshot()["participants"]?["workbench"]);
        Assert.Null(session.Snapshot()["worker"]?["pid"]);
    }

    [Theory] [InlineData("20")] [InlineData("21")]
    public void Plc_opt_in_refuses_an_engine_override(string release)
    {
        var root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--bundle-root", root, "--release-key", release,
            "--plc-worker", "--engine-worker", "unused", "--engine-catalog", "unused" }, root));
    }

    [Theory] [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task A_host_lock_refuses_adapter_work_before_worker_start(string release)
    {
        using var worker = new TiaMcp.FoundationHost.WorkerClient(release, "missing-worker", "missing-api", true)
            { SessionLocked = () => true, SharedSession = Profile.Releases[release].SharedSession };
        Assert.True(worker.Poisoned);
        Assert.False(worker.WorkerFaulted);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        Assert.Equal(TiaOpenness.Shared.SessionBehavior.Recovery, error.Message);
        Assert.Null(worker.OperatorSnapshot()["pid"]);
    }
    [Theory] [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void Disconnect_uses_one_exception_type_and_preserves_the_release_diagnostic(string release)
    {
        const string message = "Disconnect is ending this session; a new host session is required.";
        var error = TiaMcp.FoundationHost.WorkerClient.SessionEnded(release, message);
        Assert.IsType<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(error);
        var result = TiaMcp.FoundationHost.FoundationV4Result.Failure(release, "GetSessionState", "disconnect-test", false, false, null, error).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal(Profile.Releases[release].RevealSessionEndedDiagnostic ? message : "The request precondition failed before operation.", (string?)result["error"]?["message"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }

    [Theory] [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task Both_staging_facades_share_the_pipeline_owner_and_identical_listing(string release)
    {
        const string selection = "TiaOpenness.Shared.BundleLayout.v4";
        object? previous = AppDomain.CurrentDomain.GetData(selection);
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "h17-stg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.SetData(selection, root);
        try
        {
            using var worker = new TiaMcp.FoundationHost.WorkerClient(release, "missing-worker", "missing-api", true)
                { SharedSession = Profile.Releases[release].SharedSession };
            JsonObject Ready() => new() { ["ready"] = true };
            var services = TiaMcp.FoundationHost.FoundationHostPorts.CreateServices(worker, release, Ready, root);
            using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
                Array.Empty<ModelContextProtocol.Server.McpServerTool>());
            using var registration = pipeline.RegisterStagingSession("staging-" + release);
            using var context = pipeline.EnterSession();
            worker.StagingProvider = () => enginehost::TiaMcpServer.ModelContextProtocol.McpServer.SharedStagingOwner;
            var owner = worker.StagingProvider();
            TiaMcp.FoundationHost.FoundationTool Tool(string name) => new(
                TiaMcp.FoundationHost.FoundationTools.Definitions.Single(d => d.Name == name), worker);
            ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> Request(string name, JsonObject args) => new(
                System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                    Params = new() { Name = name, Arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(args.ToJsonString()) } };
            var stage = (await Tool("StageImportFiles").InvokeV4Async(Request("StageImportFiles", new JsonObject {
                ["dryRun"] = false, ["files"] = new JsonArray(new JsonObject { ["fileName"] = "Main.scl", ["kind"] = "scl", ["content"] = "FUNCTION Main : Void\nBEGIN\nEND_FUNCTION" })
            }), release, "stage", CancellationToken.None)).StructuredContent!;
            Assert.True((bool?)stage["ok"], stage.ToJsonString());
            Assert.Equal(owner.SessionId, (string?)stage["data"]?["sessionId"]);
            var foundation = (await Tool("ListStagedImportFiles").InvokeV4Async(Request("ListStagedImportFiles", new JsonObject()),
                release, "list", CancellationToken.None)).StructuredContent!;
            using var owned = enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools.UseOwnedSession(owner);
            var host = new enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools().ListStagedImportFiles().StructuredContent!;
            Assert.Equal(host["data"]!.ToJsonString(), foundation["data"]!.ToJsonString());
            Assert.Equal("current", (string?)foundation["data"]?["batches"]?[0]?["ownerState"]);
            Assert.Null(worker.OperatorSnapshot()["pid"]);
        }
        finally { AppDomain.CurrentDomain.SetData(selection, previous); Directory.Delete(root, true); }
    }

    [Theory] [InlineData("20")] [InlineData("21")]
    public async Task A_failed_full_supervisor_start_retains_its_fault_until_explicit_reset(string release)
    {
        using var worker = new TiaMcp.FoundationHost.WorkerClient(release, "missing-worker", AppContext.BaseDirectory, false)
            { Bundled = false, SharedSession = true };
        await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Observe("status", new JsonObject(), CancellationToken.None));
        var snapshot = worker.SupervisorSnapshot(new JsonObject { ["ready"] = false });
        Assert.Equal("Faulted", (string?)snapshot["state"]);
        Assert.Equal("FileNotFoundException", (string?)snapshot["fault"]?["cause"]);
        Assert.Equal("status", (string?)snapshot["fault"]?["operation"]);
        Assert.False((bool?)snapshot["fault"]?["dispatched"]);
        Assert.True(worker.WorkerFaulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Observe("status", new JsonObject(), CancellationToken.None));
        var detached = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
        Assert.False((bool?)detached?["RequiresTiaRestart"]);
        Assert.Equal(TiaOpenness.Shared.SessionBehavior.DetachedAfterUnknown, (string?)detached?["RecoveryMessage"]);
        Assert.Null(detached?["PriorUnknownRequestId"]);
    }

    [Theory] [InlineData("20")] [InlineData("21")]
    public void An_explicit_supervisor_read_starts_the_full_profile_lazily(string release)
    {
        using var fixture = new SessionWorkerFixture(release, false);
        var worker = fixture.Worker;
        JsonObject Ready() => new() { ["ready"] = true };
        var services = TiaMcp.FoundationHost.FoundationHostPorts.CreateServices(worker, release, Ready, AppContext.BaseDirectory);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        using var context = pipeline.EnterSession();
        Assert.Null(worker.OperatorSnapshot()["pid"]);
        var status = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ReadOpennessWorkerStatus();
        Assert.Equal("Ready", (string?)status.Meta?["worker"]?["state"]);
        Assert.NotNull(worker.OperatorSnapshot()["pid"]);
        Assert.False((bool?)status.Meta?["worker"]?["sessionLocked"]);
    }

    [Theory] [InlineData("20")] [InlineData("21")]
    public async Task Plc_shared_cached_status_and_restart_remain_available_offline(string release)
    {
        using var fixture = new SessionWorkerFixture(release, false);
        var worker = fixture.Worker;
        var status = await worker.Observe("status", new JsonObject(), CancellationToken.None);
        Assert.True((bool?)status?["readiness"]?["ready"]);
        Assert.False((bool?)status?["session"]?["isConnected"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        var restarted = await worker.RestartHostWorker(true, CancellationToken.None);
        Assert.True((bool?)restarted["success"]);
        Assert.True((bool?)restarted["requiresExplicitRebind"]);
        Assert.Equal("Ready", (string?)restarted["worker"]?["state"]);
    }

    [Theory] [InlineData("20")] [InlineData("21")]
    public async Task Plc_shared_unknown_locks_the_pipeline_until_an_explicit_restart(string release)
    {
        using var fixture = new SessionWorkerFixture(release, true);
        var worker = fixture.Worker;
        JsonObject Ready() => new() { ["ready"] = true };
        var shared = TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, release, true, readinessProvider: Ready, hostPorts: false);
        var services = TiaMcp.FoundationHost.FoundationHostPorts.CreateServices(worker, release, Ready, AppContext.BaseDirectory);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services, shared);
        services.ResetExports = pipeline.ResetExports;
        worker.SessionLocked = () => pipeline.SessionLocked;
        worker.StagingProvider = () => { using var context = pipeline.EnterSession(); return enginehost::TiaMcpServer.ModelContextProtocol.McpServer.SharedStagingOwner; };
        using var context = pipeline.EnterSession();
        var definition = TiaMcp.FoundationHost.FoundationTools.Definitions.Single(d => d.Name == "GetState");
        TiaMcp.FoundationHost.FoundationV4Tool Tool(string operation) => new(new TiaMcp.FoundationHost.FoundationTool(definition with { Operation = operation }, worker), release, null, sharedSession: true);
        ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> Request() => new(
            System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                Params = new() { Name = "GetSessionState", Arguments = new Dictionary<string, System.Text.Json.JsonElement>() } };
        try
        {
            var unknown = (await Tool("FixtureUnknown").InvokeAsync(Request())).StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)unknown["error"]?["code"]);
            Assert.True((bool?)unknown["meta"]?["requiresSessionReset"]);
            Assert.True(pipeline.SessionLocked);
            Assert.Equal("ChannelFailureException", (string?)services.Snapshot()["fault"]?["cause"]);
            Assert.Equal("FixtureUnknown", (string?)services.Snapshot()["fault"]?["operation"]);
            Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await Tool("ReadState").InvokeAsync(Request())).StructuredContent?["error"]?["code"]);
            var restarted = await services.Restart(true, CancellationToken.None);
            Assert.True((bool?)restarted["success"]);
            Assert.True((bool?)restarted["requiresExplicitRebind"]);
            Assert.False(pipeline.SessionLocked);
            var recovered = (await Tool("ReadState").InvokeAsync(Request())).StructuredContent!;
            Assert.True((bool?)recovered["ok"]);
            Assert.False((bool?)recovered["data"]?["isAttached"]);
        }
        finally { worker.SessionLocked = null; worker.StagingProvider = null; }
    }

    private sealed class SessionWorkerFixture : IDisposable
    {
        private readonly string adapter;
        private readonly byte[]? previous;
        internal TiaMcp.FoundationHost.WorkerClient Worker { get; }
        internal SessionWorkerFixture(string release, bool native)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            adapter = Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll");
            previous = File.Exists(adapter) ? File.ReadAllBytes(adapter) : null;
            File.WriteAllText(adapter, "offline session fixture");
            Worker = new(release, exe, AppContext.BaseDirectory, native) { Bundled = false, SharedSession = true };
        }
        public void Dispose()
        {
            Worker.Dispose();
            if (previous == null) File.Delete(adapter); else File.WriteAllBytes(adapter, previous);
        }
    }

}
