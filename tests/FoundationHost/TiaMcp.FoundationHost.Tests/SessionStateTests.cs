extern alias enginehost;
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

[Collection(BundleSelectionCollection.Name)]
public sealed class SessionStateTests
{
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
        var query = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "GetState"), worker);
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
