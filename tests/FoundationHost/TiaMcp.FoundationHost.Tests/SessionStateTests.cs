extern alias enginehost;
extern alias foundationhost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Generation.Planning.Tests;
using TiaMcp.Logic.Generation;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using GenerationSession = enginehost::TiaMcpServer.ModelContextProtocol.GenerationExecutionSession;
using Session = foundationhost::TiaMcp.FoundationHost.HostSession;

[Collection(BundleSelectionCollection.Name)]
public sealed class SessionStateTests
{
    [Fact]
    public async Task Host_tool_call_publishes_registration_once_and_ends_idle()
    {
        using var fixture = new OperatorSessionTests.Fixture("21"); using var registration = fixture.Register();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool block = false;
        var tool = new HostOnlyTool(async token => {
            if (block) { entered.TrySetResult(true); await release.Task.WaitAsync(token); }
            return new CallToolResult { Content = [], StructuredContent = new JsonObject { ["schemaVersion"] = 4,
                ["ok"] = true, ["data"] = new JsonObject(), ["error"] = null, ["meta"] = new JsonObject { ["outcome"] = "succeeded" } } };
        });
        using var session = new Session("21", "host-only fixture", false, [tool], [], () => new JsonObject(), () => { }, fixture.DirectoryPath);
        using var member = registration.Register(session);
        await using var ai = await OperatorSessionTests.Peer.Ai(session);
        int changes = 0;
        var published = new List<JsonObject>();
        void Capture()
        {
            changes++;
            published.Add(JsonNode.Parse(File.ReadAllText(registration.FilePath))!["sessions"]!.AsArray().Single()!.AsObject());
        }
        session.StateChanged += Capture;
        try
        {
            const int count = 4;
            for (int call = 1; call <= count; call++)
            {
                await ai.Tool(tool.ProtocolTool.Name);
                Assert.Equal(call, changes); Assert.Equal(call, published.Count);
                Assert.False((bool?)published[^1]["callInProgress"]);
                Assert.Equal("idle", (string?)published[^1]["worker"]?["state"]);
                Assert.Equal(call, published[^1]["recentCalls"]!.AsArray().Count);
            }
            block = true;
            var pending = ai.Tool(tool.ProtocolTool.Name);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(pending.IsCompleted);
                Assert.False(session.TryBeginHostShutdown()); Assert.False(registration.TryBeginShutdown());
                Assert.True((bool?)session.Snapshot()["callInProgress"]);
                Assert.Equal("busy", (string?)session.Snapshot()["worker"]?["state"]);
                Assert.Equal(count, changes); Assert.Equal(count, published.Count);
            }
            finally { release.TrySetResult(true); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.Equal(count + 1, changes); Assert.Equal(count + 1, published.Count);
            Assert.All(published, state => {
                Assert.False((bool?)state["callInProgress"]);
                Assert.Equal("idle", (string?)state["worker"]?["state"]);
            });
            Assert.Equal(count + 1, published[^1]["recentCalls"]!.AsArray().Count);
            Assert.True(registration.TryBeginShutdown()); registration.CancelShutdown();
        }
        finally { session.StateChanged -= Capture; release.TrySetResult(true); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Host_tool_call_failure_publishes_once_and_releases_shutdown_admission(bool setupFailure)
    {
        using var fixture = new OperatorSessionTests.Fixture("21"); using var registration = fixture.Register();
        bool invoked = false;
        var tool = new HostOnlyTool(_ => { invoked = true; throw new InvalidOperationException("Fixture host-only failure."); });
        using var session = new Session("21", "host-only fixture", setupFailure, [tool], [tool], () => new JsonObject(), () => { }, fixture.DirectoryPath);
        using var member = registration.Register(session);
        int changes = 0;
        void Count() => changes++;
        session.StateChanged += Count;
        try
        {
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                Params = new() { Name = tool.ProtocolTool.Name, Arguments = new Dictionary<string, JsonElement>() } };
            var observed = setupFailure ? session.Tools.Single() : session.OperatorTools.Single();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => observed.InvokeAsync(request).AsTask());
            Assert.Equal(setupFailure ? "AI clients cannot join an operator-owned session." : "Fixture host-only failure.", error.Message);
            Assert.Equal(!setupFailure, invoked); Assert.Equal(1, changes);
            var state = JsonNode.Parse(File.ReadAllText(registration.FilePath))!["sessions"]!.AsArray().Single()!;
            Assert.False((bool?)state["callInProgress"]); Assert.Equal("idle", (string?)state["worker"]?["state"]);
            Assert.Equal(setupFailure ? 0 : 1, state["recentCalls"]!.AsArray().Count);
            Assert.True(session.TryBeginHostShutdown()); session.CancelHostShutdown();
        }
        finally { session.StateChanged -= Count; }
    }

    private sealed class HostOnlyTool(Func<CancellationToken, ValueTask<CallToolResult>> invoke) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new() { Name = "FixtureHostOnly",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken token = default)
            => invoke(token);
    }

    [Theory] [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")]
    [InlineData("17")] [InlineData("18")] [InlineData("19")]
    public void Legacy_projections_preserve_cached_unbinding_instead_of_the_previous_approval_target(string release)
    {
        using var worker = new WorkerClient(release, "unused-worker", "unused-api", false);
        void Cached(string field, JsonObject value) => typeof(WorkerClient).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, value);
        Cached("approvalIdentity", new JsonObject { ["ProcessId"] = Environment.ProcessId, ["ProjectFile"] = "C:/previous.ap19", ["BindingEpoch"] = 4 });
        Cached("hostState", new JsonObject { ["ReleaseKey"] = release, ["IsAttached"] = true, ["ProcessId"] = Environment.ProcessId,
            ["ProjectFile"] = null, ["OwnsProject"] = false, ["IsLocalSession"] = false,
            ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" });
        var legacy = worker.HostSnapshot();
        var state = SharedSessionState.For(worker).Snapshot(release);
        Assert.True(JsonNode.DeepEquals(legacy["binding"], state["binding"]));
        Assert.Null(state["binding"]?["identity"]?["projectPath"]);
        Assert.Null(WorkbenchControlSession.For(worker).Project);
    }

    [Theory] [InlineData("20")] [InlineData("21")]
    public async Task Real_worker_status_shape_drives_host_planning_and_operator_binding(string release)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        using var worker = new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false, SharedSession = true };
        await worker.Call("Attach", new JsonObject { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, AppContext.BaseDirectory);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, services, Array.Empty<McpServerTool>());
        using var scope = pipeline.EnterSession();
        var raw = worker.SharedStatus!;
        Assert.NotNull(raw["binding"]?["processId"]);
        Assert.Null(raw["binding"]?["identity"]); // WorkerObservations.CachedStatus wire format.
        var status = await services.Status(CancellationToken.None);
        var canonical = status["binding"]!["identity"]!;
        Assert.True(JsonNode.DeepEquals(raw["binding"], canonical));
        var panel = services.OperatorSnapshot();
        Assert.Equal((string?)canonical["projectPath"], (string?)panel["binding"]?["projectFile"]);
        Assert.Equal((int?)canonical["processId"], (int?)panel["binding"]?["processId"]);
        Assert.True(JsonNode.DeepEquals(status["sessionState"], panel["sessionState"]));

        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "des1-planning-" + Guid.NewGuid().ToString("N"));
        var planner = new PlanningFixture(release);
        planner.Machine.Target.Project.ProjectIdentity = (string)canonical["projectPath"]!;
        var session = new GenerationSession(() => root, (name, _, _) => Host.V4Result(name, name switch {
            "GetSessionState" => new JsonObject(),
            "GetOpennessWorkerStatus" => new JsonObject { ["worker"] = services.Snapshot() },
            "GetProjectTree" => new JsonObject { ["devices"] = new JsonArray() },
            "GetSoftwareTree" => new JsonObject { ["paths"] = new JsonArray() },
            _ => new JsonObject { ["items"] = new JsonArray() } }), descriptor: _ => null);
        try
        {
            var plan = session.Plan(planner.Package(), GenerationDocuments.Canonical(planner.Machine));
            Assert.True((bool?)plan.StructuredContent?["ok"], plan.StructuredContent?.ToJsonString());
            Assert.Equal((string?)canonical["projectPath"], (string?)plan.StructuredContent?["data"]?["expectedProject"]);
        }
        finally
        {
            Assert.StartsWith(Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build") + Path.DirectorySeparatorChar,
                Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        worker.SessionLocked = () => pipeline.SessionLocked;
        try
        {
            Host.MarkSharedSessionUncertain();
            Assert.True((bool?)services.Snapshot()["sessionState"]?["requiresSessionReset"]);
            Assert.True((bool?)services.OperatorSnapshot()["sessionLocked"]);
            Assert.NotNull(Host.SessionPrecheckRefusal("PlanProjectGeneration"));
        }
        finally { worker.SessionLocked = null; }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Runtime_query_and_workbench_origin_share_the_same_session_switches(bool enabled)
    {
        using var worker = new CachedWorker();
        var state = SharedSessionState.For(worker);
        state.HumanCrossReferences = () => enabled;
        var query = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetSessionState"), worker);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = "GetSessionState", Arguments = new Dictionary<string, JsonElement>() } };
        var result = await query.InvokeV4Async(request, "21", "state", CancellationToken.None);
        var body = result.StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.True(JsonNode.DeepEquals(body["data"]?["sessionState"], WorkbenchControlSession.For(worker).State("21")));
        Assert.Equal(enabled, (bool?)body["data"]?["sessionState"]?["switches"]?["nativeCrossReferences"]?["humanSessionAllowed"]);
        Assert.True((bool?)body["data"]?["sessionState"]?["switches"]?["approval"]?["readOnly"]);
        Assert.Equal(body.ToJsonString(), ((TextContentBlock)result.Content[0]).Text);
    }
    private sealed class CachedWorker : IFoundationSessionWorker
    {
        public string ApprovalIdentity => "{\"processId\":123,\"projectPath\":\"C:/fixture.ap21\",\"generation\":3}";
        public bool Poisoned => false;
        public bool Bundled => false;
        public bool SharedSession => true;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public void MarkUncertain() => throw new InvalidOperationException();
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token) => Task.FromResult<JsonNode?>(new JsonObject {
            ["ReleaseKey"] = "21", ["IsAttached"] = true, ["ProcessId"] = 123, ["ProjectFile"] = "C:/fixture.ap21",
            ["OwnsProject"] = false, ["IsLocalSession"] = false, ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" });
        public void Dispose() { }
    }
}
