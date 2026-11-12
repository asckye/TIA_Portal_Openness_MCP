extern alias foundationhost;
extern alias enginehost;
using System.Text.Json.Nodes;
using Xunit;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;
using Host = foundationhost::TiaMcp.FoundationHost.SharedReleaseHost;
using Profile = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
using SessionTests = OperatorSessionTestsPlc;

[Collection(BundleSelectionCollection.Name)]
public sealed class SharedHostSessionTests
{
    [Theory] [InlineData("20")] [InlineData("21")]
    public async Task Operator_cold_worker_unknown_is_recorded_and_the_same_session_stays_usable(string release)
    {
        using var fixture = new SessionTests.Fixture(release);
        using var session = await fixture.Create();
        await using var ai = await SessionTests.Peer.Ai(session);
        using var registration = fixture.Register();
        using var membership = registration.Register(session);
        await using var panel = await SessionTests.Peer.Operator(registration, session.SessionKey);
        var fault = await panel.Tool(SessionFixtureTool.Name, new() { ["fault"] = true }, false);
        Assert.True((string?)fault["meta"]?["outcome"] == "unknown", fault.ToJsonString());
        Assert.Single(session.Snapshot()["sessionState"]!["unsettled"]!.AsArray());
        var state = await ai.Tool("GetSessionState");
        Assert.Single(state["data"]!["sessionState"]!["unsettled"]!.AsArray());
        Assert.True((bool?)(await ai.Tool("GetProjectInfo"))["ok"]);
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
        using var session = await Host.CreateHostSession(options, false);
        Assert.Equal(data.Instructions, session.Instructions);
        Assert.NotEmpty(session.Tools);
        Assert.Equal(session.Tools.Select(t => t.ProtocolTool.Name), session.OperatorTools.Select(t => t.ProtocolTool.Name));
        Assert.Null(session.Snapshot()["worker"]?["pid"]);
        Assert.Empty(session.Snapshot()["sessionState"]!["unsettled"]!.AsArray());
        using var panel = session.AttachOperator();
        Assert.Equal(1, (int?)session.Snapshot()["participants"]?["workbench"]);
        Assert.Null(session.Snapshot()["worker"]?["pid"]);
    }

    // Another shard's host or worker hashes the shared adapter identity while this fixture starts and ends: ArgumentRules.HashFile
    // and the worker open it for reading with FileShare.Read. Barrier: hold that read handle across the whole fixture lifetime.
    // The fixture must neither rewrite nor remove the identity under it (P8-stab-1).
    [Fact]
    public void Session_fixture_leaves_the_adapter_identity_that_another_shard_is_hashing()
    {
        string adapter = Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.20.dll");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(adapter, "offline session fixture");
        using (new FileStream(adapter, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new SessionWorkerFixture("20", false)) { }
        Assert.Equal("offline session fixture", File.ReadAllText(adapter));
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
    public async Task Unsettled_call_does_not_refuse_worker_start(string release)
    {
        using var worker = new TiaMcp.FoundationHost.WorkerClient(release, "missing-worker", "missing-api", true)
            { SharedSession = Profile.Releases[release].SharedSession };
        worker.Unsettled.Record("unknown", "SaveProject", "digest", DateTimeOffset.UtcNow);
        Assert.False(worker.Faulted);
        Assert.False(worker.WorkerFaulted);
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        Assert.Contains("Select the compiled worker", error.Message);
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
    public async Task A_failed_cold_supervisor_start_keeps_the_failure_retryable(string release)
    {
        using var worker = new TiaMcp.FoundationHost.WorkerClient(release, "missing-worker", AppContext.BaseDirectory, false)
            { Bundled = false, SharedSession = true };
        await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Observe("status", new JsonObject(), CancellationToken.None));
        var snapshot = worker.SupervisorSnapshot(new JsonObject { ["ready"] = false });
        Assert.Equal("NotStarted", (string?)snapshot["state"]);
        Assert.Null(snapshot["fault"]);
        Assert.Null(worker.OperatorSnapshot()["pid"]);
        Assert.False(worker.WorkerFaulted);
        await Assert.ThrowsAsync<FileNotFoundException>(() => worker.Observe("status", new JsonObject(), CancellationToken.None));
        var detached = await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
        Assert.Null(detached?["RequiresTiaRestart"]);
        Assert.Null(detached?["RecoveryMessage"]);
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
        Assert.Null(status.Meta?["worker"]?["sessionLocked"]);
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

        worker.StagingProvider = () => { using var context = pipeline.EnterSession(); return enginehost::TiaMcpServer.ModelContextProtocol.McpServer.SharedStagingOwner; };
        using var context = pipeline.EnterSession();
        var definition = TiaMcp.FoundationHost.FoundationTools.Definitions.Single(d => d.Name == "GetSessionState");
        TiaMcp.FoundationHost.FoundationV4Tool Tool(string operation) => new(new TiaMcp.FoundationHost.FoundationTool(definition with { Operation = operation }, worker), release, null, sharedSession: true);
        ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> Request() => new(
            System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                Params = new() { Name = "GetSessionState", Arguments = new Dictionary<string, System.Text.Json.JsonElement>() } };
        var writeDefinition = TiaMcp.FoundationHost.FoundationTools.Definitions.Single(d => d.Name == "SaveProject")
            with { Operation = "FixtureUnknown" };
        var writeTool = new TiaMcp.FoundationHost.FoundationV4Tool(new TiaMcp.FoundationHost.FoundationTool(writeDefinition, worker),
            release, () => new(false, 1), sharedSession: true);
        var writeRequest = Request();
        writeRequest.Params = new() { Name = "SaveProject", Arguments = new Dictionary<string, System.Text.Json.JsonElement> {
            ["dryRun"] = System.Text.Json.JsonSerializer.SerializeToElement(false),
            ["confirm"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            ["expectedProjectFile"] = System.Text.Json.JsonSerializer.SerializeToElement("C:/fixture.ap" + release) } };
        try
        {
            var unknown = (await writeTool.InvokeAsync(writeRequest)).StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)unknown["error"]?["code"]);
            Assert.True((bool?)unknown["meta"]?["requiresSessionReset"]);
            Assert.Single(worker.Unsettled.Snapshot());
            Assert.Null(services.Snapshot()["fault"]);
            var state = (await Tool("ReadState").InvokeAsync(Request())).StructuredContent!;
            Assert.True((bool?)state["ok"]);
            Assert.Single(state["data"]!["sessionState"]!["unsettled"]!.AsArray());
            Assert.Single(enginehost::TiaMcpServer.ModelContextProtocol.McpServer.Worker.Unsettled.Snapshot());
            var restarted = await services.Restart(true, CancellationToken.None);
            Assert.True((bool?)restarted["success"]);
            Assert.True((bool?)restarted["requiresExplicitRebind"]);
            Assert.Single(worker.Unsettled.Snapshot());
            var recovered = (await Tool("ReadState").InvokeAsync(Request())).StructuredContent!;
            Assert.True((bool?)recovered["ok"]);
            Assert.False((bool?)recovered["data"]?["sessionState"]?["connected"]);
        }
        finally { worker.StagingProvider = null; }
    }

    private sealed class SessionWorkerFixture : IDisposable
    {
        internal TiaMcp.FoundationHost.WorkerClient Worker { get; }
        internal SessionWorkerFixture(string release, bool native)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            // Parallel shards share this output directory and hash the identity while their workers start: install it atomically and
            // leave it in place. Truncating, restoring or deleting it here refused or killed another shard's worker (P8-stab-1).
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            Worker = new(release, exe, AppContext.BaseDirectory, native) { Bundled = false, SharedSession = true };
        }
        public void Dispose() => Worker.Dispose();
    }

}
