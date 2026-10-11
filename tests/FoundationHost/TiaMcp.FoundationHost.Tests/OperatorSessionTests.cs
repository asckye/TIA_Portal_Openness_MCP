extern alias foundationhost;
extern alias enginehost;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;
using Xunit;
using Session = foundationhost::TiaMcp.FoundationHost.HostSession;
using Registration = foundationhost::TiaMcp.FoundationHost.HostRegistration;
using Connection = foundationhost::TiaMcp.FoundationHost.OperatorConnection;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;
using EngineHost = foundationhost::TiaMcp.FoundationHost.EngineReleaseHost;

public sealed class OperatorSessionTests
{
    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Operator_stream_shares_one_existing_worker_and_disconnect_state(string release)
    {
        using var fixture = new Fixture(release);
        using var a = await fixture.Create(); using var b = await fixture.Create();
        await using var ai = await Peer.Ai(a); await using var other = await Peer.Ai(b);
        await ai.Tool("ConnectPortal", new() { ["processId"] = 101 });
        await other.Tool("ConnectPortal", new() { ["processId"] = 102 });
        using var registration = fixture.Register(); using var membershipA = registration.Register(a); using var membershipB = registration.Register(b);
        await using var panel = await Peer.Operator(registration, a.SessionKey);
        Assert.Equal(a.SessionKey, panel.Handshake!.SessionKey);
        Assert.Equal(1, (int?)panel.Handshake.Sessions.Single()["participants"]?["workbench"]);
        Assert.Equal(1, (int?)a.Snapshot()["participants"]?["workbench"]);
        var catalogAi = await ai.Client.ListToolsAsync(); var catalogPanel = await panel.Client.ListToolsAsync();
        Assert.Equal(catalogAi.Select(t => t.Name), catalogPanel.Select(t => t.Name));
        Assert.Equal(a.Tools.Select(t => JsonSerializer.Serialize(t.ProtocolTool)), a.OperatorTools.Select(t => JsonSerializer.Serialize(t.ProtocolTool)));
        int? pidA = (int?)a.Snapshot()["worker"]?["pid"], pidB = (int?)b.Snapshot()["worker"]?["pid"];
        Assert.NotNull(pidA); Assert.NotEqual(pidA, pidB);
        await panel.Tool("GetSessionState");
        Assert.Equal(pidA, (int?)a.Snapshot()["worker"]?["pid"]);
        await panel.Tool("CallTool", new() { ["name"] = "DisconnectPortal", ["arguments"] = new Dictionary<string, object?>() });
        var refused = await ai.Tool("GetSessionState", ok: false);
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]?["code"]);
        Assert.Equal((string?)refused["error"]?["message"], (string?)(await panel.Tool("GetSessionState", ok: false))["error"]?["message"]);
        Assert.Equal("workbench", (string?)a.Snapshot()["endedBy"]); Assert.Equal("disconnect", (string?)a.Snapshot()["endReason"]);
        await panel.State(s => (string?)s["endedBy"] == "workbench"); await ai.State(s => (string?)s["endedBy"] == "workbench");
        await other.Tool("GetSessionState");
        await other.Tool("DisconnectPortal");
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Ai_disconnect_and_operator_detach_have_distinct_lifetimes(string release)
    {
        using var fixture = new Fixture(release); using var session = await fixture.Create();
        await using var ai = await Peer.Ai(session); await ai.Tool("ConnectPortal", new() { ["processId"] = 103 });
        using var registration = fixture.Register(); using var membership = registration.Register(session);
        var panel = await Peer.Operator(registration, session.SessionKey);
        await panel.DisposeAsync();
        Assert.Equal(0, (int?)session.Snapshot()["participants"]?["workbench"]);
        await ai.Tool("GetSessionState");
        await using var attached = await Peer.Operator(registration, session.SessionKey);
        await ai.Tool("DisconnectPortal");
        Assert.Equal("mcp", (string?)session.Snapshot()["endedBy"]);
        var refusal = await attached.Tool("GetSessionState", ok: false);
        Assert.Equal("PRECONDITION_FAILED", (string?)refusal["error"]?["code"]);
        await attached.State(s => (string?)s["endedBy"] == "mcp");
        if (fixture.Options.EngineWorkerExe == null)
        {
            foundationhost::TiaMcp.FoundationHost.WorkerShutdown.StopAll();
            session.Dispose();
            Assert.Equal("disconnected", (string?)session.Snapshot()["worker"]?["state"]);
        }
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Lock_and_restart_belong_to_the_attached_session(string release)
    {
        using var fixture = new Fixture(release); using var session = await fixture.Create(); using var other = await fixture.Create();
        using var approval = new DisabledApproval(fixture.Options.BundleRoot);
        await using var ai = await Peer.Ai(session); await using var separate = await Peer.Ai(other);
        using var registration = fixture.Register(); using var membership = registration.Register(session);
        await using var panel = await Peer.Operator(registration, session.SessionKey);
        await panel.Tool("GetPlcBlockFingerprints", new() { ["fault"] = true }, false);
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await ai.Tool("GetSessionState", ok: false))["error"]?["code"]);
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await panel.Tool("GetSessionState", ok: false))["error"]?["code"]);
        Assert.Equal("workbench", (string?)session.Snapshot()["endedBy"]); Assert.True((bool?)session.Snapshot()["sessionLocked"]);
        await separate.Tool("GetSessionState");
        long? before = (long?)session.Snapshot()["worker"]?["generation"];
        await ai.Tool("RestartOpennessWorker", new() { ["confirmRestart"] = true });
        Assert.Equal(before + 1, (long?)session.Snapshot()["worker"]?["generation"]);
        Assert.False((bool?)session.Snapshot()["sessionLocked"]); Assert.Equal("worker-restart", (string?)session.Snapshot()["endReason"]);
        Assert.Equal("mcp", (string?)session.Snapshot()["endedBy"]);
        await panel.Tool("GetSessionState");
    }

    [Fact]
    public async Task Actual_worker_lane_orders_operator_and_ai_and_cancels_only_queued_call()
    {
        using var fixture = new Fixture("21"); using var session = await fixture.Create();
        await using var ai = await Peer.Ai(session);
        using var registration = fixture.Register(); using var membership = registration.Register(session);
        await using var panel = await Peer.Operator(registration, session.SessionKey);
        await ai.Tool("GetSessionState");
        var running = ai.Tool("GetPlcBlockFingerprints", new() { ["hang"] = true });
        await WaitUntilBusy(fixture);
        await panel.State(s => (string?)s["lane"]?["actor"] == "mcp");
        string? requestId = (string?)session.Snapshot()["lane"]?["requestId"];
        using var cancellation = new CancellationTokenSource();
        var request = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new CallToolRequestParams { Name = "GetPlcBlockFingerprints", Arguments = new Dictionary<string, JsonElement>() } };
        var queued = session.OperatorTools.Single(t => t.ProtocolTool.Name == "GetPlcBlockFingerprints").InvokeAsync(request, cancellation.Token);
        cancellation.Cancel();
        var cancelled = await queued;
        Assert.Equal("CANCELLED", (string?)cancelled.StructuredContent?["error"]?["code"]);
        var ordered = panel.Tool("GetPlcBlockFingerprints");
        await Task.Delay(50); Assert.False(ordered.IsCompleted);
        var completed = await running; await ordered;
        Assert.Equal((string?)completed["meta"]?["requestId"], requestId);
        var calls = session.Snapshot()["recentCalls"]!.AsArray().Where(c => (string?)c?["outcome"] == "succeeded").ToArray();
        Assert.Equal("mcp", (string?)calls[^2]?["actor"]); Assert.Equal("workbench", (string?)calls[^1]?["actor"]);
        Assert.Null(session.Snapshot()["lane"]);
    }

    [Fact]
    public async Task Operator_write_uses_existing_approval_frame_and_cannot_spoof_actor()
    {
        var worker = new ApprovalWorker(); PendingApproval? pending = null;
        var inner = TiaMcp.FoundationHost.FoundationTools.Create(worker, "19").Single(t => t.ProtocolTool.Name == "SaveProject");
        var tool = new TiaMcp.FoundationHost.FoundationV4Tool(inner, "19", _ => TiaMcp.Logic.V4.BehaviorPolicy.SafeV4, () => new ApprovalSettings(true, 1), approvalWait: (request, _, _) => {
            pending = request; return Task.FromResult(new ApprovalOutcome(request, false, "denied"));
        });
        using var session = new Session("19", "fixture", false, [tool], [tool], () => new JsonObject(), worker.Dispose);
        await using var pair = await Peer.Ai(session);
        using var fixture = new Fixture("19"); using var registration = fixture.Register(); using var member = registration.Register(session);
        await using var panel = await Peer.Operator(registration, session.SessionKey);
        var arguments = new Dictionary<string, object?> { ["mode"] = "apply", ["confirm"] = true, ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = @"C:\fixture.ap19" };
        await panel.Tool("SaveProject", arguments, false);
        Assert.True(pending != null, session.Snapshot().ToJsonString()); Assert.Equal(2, pending!.Version); Assert.Equal("workbench", pending.Actor); Assert.NotNull(pending.OperatorCallId);
        await pair.Tool("SaveProject", arguments, false);
        Assert.Equal(1, pending.Version); Assert.Equal("mcp", pending.EffectiveActor); Assert.Null(pending.OperatorCallId);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Queued_ai_refusal_does_not_replace_the_operator_who_ended_the_session(string release)
    {
        using var fixture = new Fixture(release); using var session = await fixture.Create();
        await using var ai = await Peer.Ai(session);
        using var registration = fixture.Register(); using var membership = registration.Register(session);
        await using var panel = await Peer.Operator(registration, session.SessionKey);
        await ai.Tool("GetSessionState");
        var running = ai.Tool("GetPlcBlockFingerprints", new() { ["hang"] = true }, ok: false);
        await WaitUntilBusy(fixture);
        var refused = ai.Tool("GetPlcBlockFingerprints", ok: false);
        await Task.Delay(100); Assert.False(refused.IsCompleted);
        await panel.Tool("DisconnectPortal");
        await running; await refused;
        Assert.Equal("workbench", (string?)session.Snapshot()["endedBy"]);
        Assert.Equal("disconnect", (string?)session.Snapshot()["endReason"]);
    }

    [Fact]
    public async Task Engine_operator_view_does_not_forge_or_accept_another_ai_session_id()
    {
        using var fixture = new Fixture("21");
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        using var worker = new foundationhost::TiaMcp.FoundationHost.EngineWorkerClient(fixture.Options,
            foundationhost::TiaMcp.FoundationHost.EngineWorkerClient.Hash(fixture.Options.EngineWorkerExe!));
        var shared = foundationhost::TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, "21", true, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var staging = pipeline.RegisterHttpSession("real-ai-id");
        var request = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new CallToolRequestParams { Name = "GetSessionState", Arguments = new Dictionary<string, JsonElement>() } };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.Tools.Single(t => t.ProtocolTool.Name == "GetSessionState").InvokeAsync(request));
        using var actor = ActorScope.EnterWorkbench("real-ai-id");
        var result = await pipeline.OperatorTools.Single(t => t.ProtocolTool.Name == "GetSessionState").InvokeAsync(request);
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Null(request.Server.SessionId);
        Assert.Null(enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.OperatorSession.Value);
        using var approval = new DisabledApproval(fixture.Options.BundleRoot);
        request.Params = new CallToolRequestParams { Name = "StageImportFiles", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"files\":[{\"fileName\":\"operator.scl\",\"kind\":\"scl\",\"content\":\"FUNCTION Operator : Void\\nBEGIN\\nEND_FUNCTION\"}],\"dryRun\":false}") };
        var staged = await pipeline.OperatorTools.Single(t => t.ProtocolTool.Name == "StageImportFiles").InvokeAsync(request);
        Assert.True((bool?)staged.StructuredContent?["ok"], staged.StructuredContent?.ToJsonString());
        request.Params = new CallToolRequestParams { Name = "ListStagedImportFiles" };
        var listing = await pipeline.OperatorTools.Single(t => t.ProtocolTool.Name == "ListStagedImportFiles").InvokeAsync(request);
        Assert.Contains("real-ai-id", listing.StructuredContent!.ToJsonString());
        Assert.Contains("\"currentSession\":true", listing.StructuredContent!.ToJsonString());
    }

    private static async Task WaitUntilBusy(Fixture fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(fixture.DirectoryPath, "busy"))) await Task.Delay(10, timeout.Token);
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "operator-fixture-" + Guid.NewGuid().ToString("N"));
        internal readonly Options Options;
        private readonly string? priorLease = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT");
        internal Fixture(string release)
        {
            Directory.CreateDirectory(DirectoryPath); Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", DirectoryPath);
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            string catalog = Path.Combine(DirectoryPath, "catalog.json");
            var properties = new JsonObject { ["fault"] = new JsonObject { ["type"] = "boolean" }, ["hang"] = new JsonObject { ["type"] = "boolean" } };
            File.WriteAllText(catalog, new JsonObject { ["formatVersion"] = 1, ["release"] = release,
                ["workerSha256"] = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient.Hash(exe),
                ["behaviorCapabilities"] = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(TiaMcp.Logic.V4.BehaviorCapabilities).Assembly, release),
                ["tools"] = new JsonArray(new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["description"] = "[L0][Diagnostics][READ] Offline fixture.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false } }),
                ["descriptors"] = new JsonArray(new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["rawDescription"] = "", ["signature"] = "GetPlcBlockFingerprints()",
                    ["parameters"] = new JsonArray(properties.Select(p => (JsonNode)new JsonObject { ["name"] = p.Key, ["friendlyType"] = "boolean", ["clrType"] = "System.Boolean", ["required"] = false, ["description"] = "", ["synthesized"] = false, ["allowedValues"] = new JsonArray() }).ToArray()),
                    ["dryRun"] = new JsonObject { ["present"] = false, ["default"] = false }, ["execution"] = "worker" }),
                ["liteTools"] = new JsonArray(), ["serverInstructions"] = "Offline operator fixture." }.ToJsonString());
            Options = new Options { ReleaseKey = release, WorkerExe = exe, ApiDirectory = AppContext.BaseDirectory, BundledWorker = false,
                BundleRoot = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), NativeEnabled = true,
                EngineWorkerExe = release is "20" or "21" ? exe : null, EngineCatalog = catalog, Profile = "full" };
            BundleLayout.Initialize(AppContext.BaseDirectory, Options.BundleRoot);
        }
        internal async Task<Session> Create(bool owned = false) => Options.EngineWorkerExe == null
            ? Session.Legacy(Options, "fixture", owned) : await EngineHost.CreateHostSession(Options, owned);
        internal Registration Register() => new(Options, "S-1-5-21-1-2-3-1001", DirectoryPath, error => throw error);
        public void Dispose() { Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_LEASE_ROOT", priorLease); Directory.Delete(DirectoryPath, true); }
    }

    internal sealed class Peer : IAsyncDisposable
    {
        internal readonly IMcpClient Client;
        internal OperatorHandshakeResult? Handshake;
        private readonly CancellationTokenSource stopping;
        private readonly Task running;
        private readonly IMcpServer? server;
        private readonly System.Collections.Concurrent.ConcurrentQueue<JsonObject> states = new();
        private readonly IAsyncDisposable subscription;
        private Peer(IMcpClient client, Task running, CancellationTokenSource stopping, IMcpServer? server)
        {
            Client = client; this.running = running; this.stopping = stopping; this.server = server;
            subscription = client.RegisterNotificationHandler(OperatorProtocol.SessionStateNotification, (notification, _) => {
                states.Enqueue((JsonObject)notification.Params!.DeepClone()); return ValueTask.CompletedTask;
            });
        }
        internal static async Task<Peer> Ai(Session session)
        {
            var (host, client) = Streams(); var stopping = new CancellationTokenSource();
            var collection = new McpServerPrimitiveCollection<McpServerTool>(); foreach (var tool in session.Tools) collection.Add(tool);
            var server = McpServerFactory.Create(new StreamServerTransport(host, host), new McpServerOptions { ServerInfo = new Implementation { Name = "fixture", Version = "1" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ToolCollection = collection } } });
            var running = server.RunAsync(stopping.Token);
            var mcp = await McpClientFactory.CreateAsync(new StreamClientTransport(client, client));
            session.BindAi(server);
            return new Peer(mcp, running, stopping, server);
        }
        internal static async Task<Peer> Operator(Registration registration, string? key, string action = "attach", bool owner = false,
            Func<CancellationToken, Task<foundationhost::TiaMcp.FoundationHost.IHostSession>>? create = null)
        {
            var (host, client) = Streams(); var stopping = new CancellationTokenSource();
            var connection = new Connection(registration, create ?? (_ => throw new InvalidOperationException("Unexpected create.")), _ => owner);
            var running = connection.Serve(host, Environment.ProcessId, stopping.Token);
            await OperatorProtocol.Write(client, new OperatorHandshake { Action = action, SessionKey = key, Workbench = new OperatorWorkbench { Pid = Environment.ProcessId, Version = "fixture" } }, stopping.Token);
            var result = await OperatorProtocol.Read<OperatorHandshakeResult>(client, stopping.Token); Assert.True(result.Accepted, result.Error);
            var mcp = await McpClientFactory.CreateAsync(new StreamClientTransport(client, client));
            return new Peer(mcp, running, stopping, null) { Handshake = result };
        }
        internal async Task<JsonObject> Tool(string name, Dictionary<string, object?>? args = null, bool ok = true)
        {
            var result = await Client.CallToolAsync(name, args);
            var body = result.StructuredContent?.AsObject() ?? new JsonObject();
            Assert.True(ok == (bool?)body["ok"], JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions)); return body;
        }
        internal async Task State(Func<JsonObject, bool> predicate)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            { if (states.Any(predicate)) return; await Task.Delay(20); }
            Assert.Fail("Expected session notification: " + string.Join("\n", states.Select(s => s.ToJsonString())));
        }
        public async ValueTask DisposeAsync()
        {
            await subscription.DisposeAsync(); stopping.Cancel(); await Client.DisposeAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            if (server != null) await server.DisposeAsync(); stopping.Dispose();
        }
    }
    internal static (Stream Host, Stream Client) Streams()
    {
        var a = new Pipe(); var b = new Pipe();
        return (new Duplex(a.Reader.AsStream(), b.Writer.AsStream()), new Duplex(b.Reader.AsStream(), a.Writer.AsStream()));
    }
    private sealed class ApprovalWorker : TiaMcp.FoundationHost.IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => Task.FromResult(JsonSerializer.SerializeToNode(new TiaMcp.Adapters.Contracts.Candidates.SaveCloseReply {
                Observation = new TiaMcp.Adapters.Contracts.Candidates.SaveCloseObservation {
                    Binding = new TiaMcp.Adapters.Contracts.Candidates.SessionState { ProcessId = 1, ProcessStartUtc = DateTimeOffset.Parse("2026-10-05T00:00:00Z"),
                        ProjectFile = @"C:\fixture.ap19", Ownership = "owned", Epoch = 1, WorkerEpoch = 1 },
                    Dirty = false, ObjectValidity = "valid", DisconnectSupported = true, WorkerCleanup = "deferred-until-channel-close" } }));
        public void Dispose() { }
    }
    private sealed class DisabledApproval : IDisposable
    {
        private readonly string path = ApprovalSettings.SettingsPath;
        private readonly byte[]? prior;
        internal DisabledApproval(string root)
        {
            Assert.True(Path.GetFullPath(path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(path).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "Tests require an isolated TIA_MCP_DATA_DIRECTORY: " + path);
            prior = File.Exists(path) ? File.ReadAllBytes(path) : null;
            File.WriteAllText(path, "enabled=false\ntimeoutSeconds=1\n");
        }
        public void Dispose() { if (prior == null) File.Delete(path); else File.WriteAllBytes(path, prior); }
    }
    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Attached_panel_and_ai_share_generation_fence_before_approval(string release)
    {
        using var fixture = new Fixture(release); var worker = new GenerationFenceWorker(release); int approvals = 0;
        var tools = TiaMcp.FoundationHost.FoundationTools.Create(worker, release).Select(t => (McpServerTool)new TiaMcp.FoundationHost.FoundationV4Tool(t, release, null,
            () => new ApprovalSettings(false, 1), (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, true, null)); })).ToArray();
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, tools);
        using var session = new Session(release, "fence fixture", false, pipeline.Tools.ToArray(), pipeline.OperatorTools,
            () => new JsonObject(), pipeline.Dispose);
        await using var ai = await Peer.Ai(session);
        using var registration = fixture.Register(); using var member = registration.Register(session);
        await using var panel = await Peer.Operator(registration, session.SessionKey);
        var args = new Dictionary<string, object?> { ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/Fixture.ap" + release };
        using (GenerationWriteFence.Acquire(worker.SessionKey, "fixture-run"))
        {
            var direct = await panel.Tool("SaveProject", args, false);
            var bridged = await ai.Tool("CallTool", new() { ["name"] = "SaveProject", ["arguments"] = args }, false);
            foreach (var result in new[] { direct, bridged })
            {
                Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
                Assert.Equal("generation-run-active", (string?)result["error"]?["details"]?["condition"]);
                Assert.Equal("fixture-run", (string?)result["data"]?["runId"]);
            }
            await panel.Tool("GetSessionState"); await ai.Tool("GetSessionState");
            Assert.Equal(0, approvals); Assert.Equal(0, worker.Writes);
        }
        await panel.Tool("SaveProject", args); Assert.Equal(1, approvals); Assert.Equal(1, worker.Writes);
    }
    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public void Host_shutdown_releases_active_generation_fence(string release)
    {
        var worker = new GenerationFenceWorker(release);
        var tools = TiaMcp.FoundationHost.FoundationTools.Create(worker, release).Select(t => (McpServerTool)new TiaMcp.FoundationHost.FoundationV4Tool(t, release)).ToArray();
        var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, tools);
        using var fence = GenerationWriteFence.Acquire(worker.SessionKey, "shutdown-run");
        Assert.Equal("shutdown-run", GenerationWriteFence.RefusedRun(worker.SessionKey, "19", "SaveProject", "{}"));
        pipeline.Dispose();
        Assert.Null(GenerationWriteFence.RefusedRun(worker.SessionKey, "19", "SaveProject", "{}"));
        Assert.True(GenerationWriteFence.Ended(worker.SessionKey));
        Assert.Throws<InvalidOperationException>(() => GenerationWriteFence.Acquire(worker.SessionKey, "another-run"));
    }
    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task A_write_approved_before_fence_acquisition_is_rechecked_before_dispatch(string release)
    {
        var worker = new GenerationFenceWorker(release); IDisposable? fence = null; int approvals = 0;
        var inner = TiaMcp.FoundationHost.FoundationTools.Create(worker, release).Single(t => t.ProtocolTool.Name == "SaveProject");
        var tool = new TiaMcp.FoundationHost.FoundationV4Tool(inner, release, null, () => new ApprovalSettings(false, 1), (pending, _, _) => {
            approvals++; fence = GenerationWriteFence.Acquire(worker.SessionKey, "queued-run");
            return Task.FromResult(new ApprovalOutcome(pending, true, null));
        });
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, [tool]);
        using var session = pipeline.EnterSession();
        var request = new RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new CallToolRequestParams { Name = "SaveProject", Arguments = new Dictionary<string, JsonElement> {
                ["dryRun"] = JsonSerializer.SerializeToElement(false), ["confirm"] = JsonSerializer.SerializeToElement(true),
                ["expectedProjectFile"] = JsonSerializer.SerializeToElement("C:/Fixture.ap" + release) } } };
        try
        {
            var result = await tool.InvokeAsync(request);
            Assert.Equal("generation-run-active", (string?)result.StructuredContent?["error"]?["details"]?["condition"]);
            Assert.Equal("queued-run", (string?)result.StructuredContent?["data"]?["runId"]);
            Assert.Equal(1, approvals); Assert.Equal(0, worker.Writes);
        }
        finally { fence?.Dispose(); }
    }
    private sealed class GenerationFenceWorker(string release) : TiaMcp.FoundationHost.IFoundationSessionWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Writes;
        public object SessionKey { get; } = new();
        public bool Faulted => false;
        public bool Poisoned => false;
        public bool Bundled => false;
        public bool SharedSession => true;
        public string ApprovalIdentity => "{}";
        public JsonNode? Binding => new JsonObject();
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public void MarkUncertain() => throw new Exception("fixture must not become unknown");
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new EmptyLane());
        public JsonObject Snapshot() => new JsonObject { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token) => throw new NotSupportedException();
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ReleaseKey"] = release, ["IsAttached"] = true, ["ProcessId"] = 123, ["ProjectFile"] = "C:/Fixture.ap" + release, ["OwnsProject"] = false, ["IsLocalSession"] = false, ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" });
            if (operation == "SaveProject") { Writes++; return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = true, ["Operation"] = "SaveProject", ["NativeCallIssued"] = true, ["ProjectFile"] = "C:/Fixture.ap" + release }); }
            throw new NotSupportedException(operation);
        }
        public void Dispose() { }
        private sealed class EmptyLane : IDisposable { public void Dispose() { } }
    }
    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush(); public override Task FlushAsync(CancellationToken token) => write.FlushAsync(token);
        public override int Read(byte[] bytes, int offset, int count) => read.Read(bytes, offset, count);
        public override Task<int> ReadAsync(byte[] bytes, int offset, int count, CancellationToken token) => read.ReadAsync(bytes, offset, count, token);
        public override ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken token = default) => read.ReadAsync(bytes, token);
        public override void Write(byte[] bytes, int offset, int count) => write.Write(bytes, offset, count);
        public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token) => write.WriteAsync(bytes, offset, count, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => write.WriteAsync(bytes, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { read.Dispose(); write.Dispose(); } base.Dispose(disposing); }
    }
}
