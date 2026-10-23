using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;
using Xunit.Abstractions;

public sealed partial class PipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Engine_host_carries_transport_actor_through_approval_journal_and_audit(bool human)
    {
        using var fixture = new Fixture();
        string root = Path.Combine(AppContext.BaseDirectory, "actor-audit", Guid.NewGuid().ToString("N"));
        var log = new AuditLog(root); using var audit = AuditInvocation.UseLog(log);
        using var actor = human ? ActorScope.EnterWorkbench("attached-session", new string('c', 32)) : ActorScope.EnterCall("attached-session");
        var rows = new List<string>(); InvocationJournal.ConfigureOutput(rows.Add);
        PendingApproval? approval = null;
        fixture.Wait = (pending, _, _) => { approval = pending; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        try
        {
            var result = await fixture.Call("SaveProject", "{}");
            Assert.NotNull(approval); Assert.Equal(human ? 2 : 1, approval.Version);
            Assert.Equal(human ? "workbench" : "mcp", approval.EffectiveActor);
            Assert.NotEmpty(fixture.Worker.Calls); Assert.True(log.Verify().Passed);
            Assert.NotEmpty(log.Read()); Assert.All(log.Read(), row => Assert.Equal(human ? "workbench" : "mcp", row.Actor));
            var projected = rows.Select(row => JsonNode.Parse(row)!).Where(row => (int?)row["callProjection"] == 1).ToArray();
            Assert.Equal(2, projected.Length);
            Assert.All(projected, row => { Assert.Equal(human ? "workbench" : "mcp", (string?)row["actor"]); Assert.Equal(ActorScope.McpSession, (string?)row["mcpSession"]); });
            Assert.Null(result.StructuredContent?["meta"]?["actor"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Session_context_flows_across_await_and_nested_calls_restore_the_caller()
    {
        var a = new EngineSessionContext(new Worker()); var b = new EngineSessionContext(new Worker());
        using var first = EngineHostConfiguration.Enter(a);
        await Task.Run(async () => {
            Assert.Same(a.Worker, McpServer.Worker);
            using (EngineHostConfiguration.Enter(b)) { await Task.Yield(); Assert.Same(b.Worker, McpServer.Worker); }
            Assert.Same(a.Worker, McpServer.Worker);
        });
        Assert.Same(a.Worker, McpServer.Worker);
        a.Ended = true;
        Assert.Throws<InvalidOperationException>(() => McpServer.Worker);
    }

    [Fact]
    public void Export_access_and_cleanup_are_owned_by_the_current_MCP_session()
    {
        var a = new EngineSessionContext(new Worker()); var b = new EngineSessionContext(new Worker());
        string id;
        using (EngineHostConfiguration.Enter(a)) id = SessionExportStore.PutAndSlice("fixture", "A", "retained", 4).id;
        using (EngineHostConfiguration.Enter(b))
        {
            Assert.Null(SessionExportStore.Get(id)); Assert.Empty(SessionExportStore.List(null, 10));
            Assert.False(SessionExportStore.Delete(id)); Assert.Equal(0, SessionExportStore.Clear(0));
        }
        using (EngineHostConfiguration.Enter(a))
        {
            Assert.Equal("retained", SessionExportStore.Get(id)!.Content);
            Assert.Equal(1, SessionExportStore.Stats().count);
        }
        a.Exports.Dispose(); Assert.Null(ExportStore.Get(id)); b.Exports.Dispose();
    }

    [Fact]
    public void Preview_plan_store_belongs_to_the_worker_generation()
    {
        var worker = new Worker(); var session = new EngineSessionContext(worker);
        var previous = session.BatchPlans; worker.SessionKey = new object();
        Assert.NotSame(previous, session.BatchPlans);
    }

    [Theory]
    [InlineData("20")] [InlineData("21")]
    public void Shared_catalog_restores_lifecycle_descriptors_without_adding_them_to_lite(string release)
    {
        using var fixture = new Fixture(release, all: true);
        var shared = new SharedToolCatalog(fixture.Catalog, Array.Empty<McpServerTool>(), new HashSet<string>());
        Assert.Null(shared.Find("ConnectProject", includeUnavailable: true));
        foreach (string name in new[] { "RetrieveProjectArchive", "SaveProjectCopy", "ManageMultiuserSession", "BuildProjectScaffold", "ConnectIsolatedPortal" })
        {
            Assert.Same(fixture.Catalog.Find(name), shared.Find(name));
            Assert.Same(shared.Find(name), shared.Find(name, includeUnavailable: true));
            Assert.DoesNotContain(shared.Lite, tool => tool.Name == name);
        }
    }

    [Theory]
    [InlineData("20", 480)]
    [InlineData("21", 491)]
    public void EveryCatalogEntryHasReviewedOwnershipAndALocalImplementation(string release, int count)
    {
        using var fixture = new Fixture(release, all: true);
        Assert.Equal(count, fixture.Catalog.All.Count);
        Assert.Equal(95, fixture.Catalog.All.Values.Count(t => t.Execution == "host"));
        Assert.Equal("host", fixture.Catalog.All["ManagePlcGitRepository"].Execution);
        Assert.All(fixture.Catalog.All.Values, tool => Assert.Equal(ToolExecution.Table[tool.Name], tool.Execution));
        Assert.Equal(count, McpServer.GetAllTools().Count);
    }

    [Theory]
    [InlineData("SaveProject", "{\"unexpected\":true}")]
    [InlineData("ListExportHandles", "{\"limit\":2147483648}")]
    [InlineData("ListExportHandles", "{\"limit\":\"invalid\"}")]
    public async Task AdmissionStopsBeforeWorkerOrApproval(string name, string arguments)
    {
        using var fixture = new Fixture();
        fixture.Wait = (_, _, _) => throw new Exception("Invalid arguments cannot request approval.");
        var result = await fixture.Call(name, arguments);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NativeEvidenceControlsUnknownSessionLock(bool native, bool locked)
    {
        using var fixture = new Fixture();
        fixture.Worker.Unknown = true; fixture.Worker.Native = native;
        await fixture.Call("SaveProject", "{}");
        int calls = fixture.Worker.Calls.Count;
        var result = await fixture.Call("GetSessionState", "{}");
        Assert.Equal(locked ? "SESSION_RESET_REQUIRED" : "OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(locked ? calls : calls + 1, fixture.Worker.Calls.Count);
        Assert.True((bool?)(await fixture.Call("ListExportHandles", "{}")).StructuredContent?["ok"]);
    }

    [Fact]
    public async Task ProcessLossRefusesBeforeApproval()
    {
        using var fixture = new Fixture(); fixture.Worker.Faulted = true;
        fixture.Wait = (_, _, _) => throw new Exception("A faulted generation cannot request approval.");
        var result = await fixture.Call("SaveProject", "{}");
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Calls);
    }

    [Fact]
    public async Task ResponseLimitRetainsNativeUnknownEvidence()
    {
        using var fixture = new Fixture(); fixture.Worker.Limited = true; fixture.Worker.Native = true;
        Assert.Equal("LIMIT_EXCEEDED", (string?)(await fixture.Call("SaveProject", "{}")).StructuredContent?["error"]?["code"]);
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await fixture.Call("GetSessionState", "{}")).StructuredContent?["error"]?["code"]);
    }

    [Fact]
    public async Task BindingChangesInvalidateApproval()
    {
        using var fixture = new Fixture();
        fixture.Wait = (pending, _, _) => { fixture.Worker.Binding = new JsonObject { ["project"] = "other" }; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        var result = await fixture.Call("SaveProject", "{}");
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Calls);
    }

    [Fact]
    public async Task WorkerReadinessUsesItsOwnEnvironmentEvidence()
    {
        using var fixture = new Fixture(); fixture.Worker.Ready = false;
        var result = await fixture.Call("GetSessionState", "{}");
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("fixture-worker-cause", (string?)result.StructuredContent?["data"]?["environment"]?["cause"]);
        Assert.Empty(fixture.Worker.Calls);
    }

    [Fact]
    public async Task LiteBridgeKeepsAdmissionAndNativeEvidence()
    {
        using var fixture = new Fixture();
        var rejected = await fixture.Call("CallTool", "{\"name\":\"SaveProject\",\"arguments\":{\"unexpected\":true}}");
        Assert.Equal("INVALID_ARGUMENT", (string?)rejected.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Calls);
        fixture.Worker.Unknown = true; fixture.Worker.Native = true;
        await fixture.Call("CallTool", "{\"name\":\"SaveProject\",\"arguments\":{}}");
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await fixture.Call("GetSessionState", "{}")).StructuredContent?["error"]?["code"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SupervisorRestartDoesNotAcquireItsOwnWorkerLane(bool confirmed, bool faulted)
    {
        using var fixture = new Fixture(); fixture.Worker.Faulted = faulted;
        var result = await fixture.Call("RestartOpennessWorker", confirmed ? "{\"confirmRestart\":true}" : "{\"confirmRestart\":false}");
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Equal(0, fixture.Worker.Acquired);
        Assert.Equal(confirmed, fixture.Worker.RestartConfirmed);
    }

    [Fact]
    public async Task Async_bridge_keeps_approval_lane_journal_and_audit_order_across_yields()
    {
        using var fixture = new Fixture();
        string root = Path.Combine(AppContext.BaseDirectory, "bridge-audit", Guid.NewGuid().ToString("N"));
        var log = new AuditLog(root); using var audit = AuditInvocation.UseLog(log);
        var events = new List<string>(); var journal = new List<JsonNode>();
        fixture.Worker.Yield = true; fixture.Worker.Event = events.Add;
        InvocationJournal.ConfigureOutput(row => journal.Add(JsonNode.Parse(row)!));
        fixture.Wait = async (pending, _, _) => {
            Assert.Equal(0, fixture.Worker.Acquired);
            events.Add("approval"); await Task.Yield(); return new ApprovalOutcome(pending, false, null, delegated: true);
        };
        bool context = McpServer.EnterMcpApprovalContext();
        try
        {
            var result = await McpServer.CallToolAsync("SaveProject", McpServer.EmptyArguments());
            Assert.True((bool?)result.StructuredContent?["ok"]);
            Assert.Equal(new[] { "status", "approval", "lane", "status", "invoke" }, events);
            Assert.Equal(1, fixture.Worker.Acquired);
            Assert.Equal(new[] { "worker:SaveProject:BEFORE", "worker:SaveProject:RETURNED" },
                journal.Select(row => (string?)row["tool"] + ":" + (string?)row["phase"]));
            Assert.Equal(new[] { "request", "start", "end" }, log.Read().Select(row => row.Event));
            Assert.All(log.Read(), row => Assert.Equal((string?)result.StructuredContent?["meta"]?["requestId"], row.RequestId));
            Assert.True(log.Verify().Passed);
            using var released = await fixture.Worker.Acquire(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            McpServer.LeaveMcpApprovalContext(context);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Async_bridge_cancellation_while_waiting_for_the_target_lane_stays_tool_queue()
    {
        using var fixture = new Fixture();
        using var held = await fixture.Worker.Acquire(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        using var request = McpServer.UseProgressRequest(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = "CallTool" }
        }, cancellation.Token);
        var pending = McpServer.CallToolAsync("GetSessionState", McpServer.EmptyArguments());
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("tool-queue", (string?)result.StructuredContent?["error"]?["details"]?["stage"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Empty(fixture.Worker.Calls);
    }

    [Fact]
    public async Task CatalogTool_returns_an_incomplete_task_and_waits_for_its_foundation_target()
    {
        using var fixture = new Fixture();
        var target = new PendingFoundationTool();
        var catalog = new SharedToolCatalog(fixture.Catalog, new[] { target }, new HashSet<string>());
        using var lifetime = new ImportStagingHostLifetime();
        var invoker = new WorkerToolInvoker(catalog, fixture.Worker, lifetime, new[] { target });
        McpServer.ConfigureToolBridge(catalog, invoker, () => false, new HashSet<string>());
        var tool = invoker.CreateTool(catalog.Find("CallTool")!);
        var returned = new TaskCompletionSource<Task<CallToolResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                    Params = new() { Name = "CallTool", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"name\":\"GetSessionState\",\"arguments\":{}}") }
                };
                // SerializedCallTool supplies this context on the production path.
                using var progressRequest = McpServer.UseProgressRequest(request, CancellationToken.None);
                returned.SetResult(tool.InvokeAsync(request).AsTask());
            }
            catch (Exception error) { returned.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            var expected = McpServer.V4Result("GetSessionState", new JsonObject { ["fixture"] = true });
            target.Completion.SetResult(expected);
            Assert.Same(expected, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            target.Completion.TrySetResult(McpServer.V4Result("GetSessionState", new JsonObject()));
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class PendingFoundationTool : McpServerTool
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<CallToolResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Tool ProtocolTool { get; } = new() { Name = "GetSessionState",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }) };
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            return await Completion.Task;
        }
    }

    [Fact]
    public async Task LocalToolsStayBelow100MsP95DuringA60SecondWorkerCall()
    {
        using var fixture = new Fixture();
        fixture.Worker.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(() => fixture.Call("GetSessionState", "{}"));
        await fixture.Worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var samples = new List<double>();
        var duration = Stopwatch.StartNew();
        try
        {
            while (duration.Elapsed < TimeSpan.FromSeconds(60))
            {
                var clock = Stopwatch.StartNew();
                foreach (string name in new[] { "ListExportHandles", "GetOpennessWorkerStatus", "GetNativeInvocationLog" })
                {
                    clock.Restart();
                    var result = await fixture.Call(name, "{}");
                    samples.Add(clock.Elapsed.TotalMilliseconds);
                    Assert.True((bool?)result.StructuredContent?["ok"]);
                }
                await Task.Delay(100);
            }
        }
        finally { fixture.Worker.Hold.SetResult(); await pending; }
        samples.Sort();
        output.WriteLine("Local calls during 60s worker wait: samples=" + samples.Count + ", p95(ms)=" + samples[(int)(samples.Count * .95)]);
        Assert.True(samples[(int)(samples.Count * .95)] < 100, "Local p95=" + samples[(int)(samples.Count * .95)]);
    }

    private sealed class Catalog : IToolCatalogView
    {
        public IReadOnlyDictionary<string, ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities { get; }
        public ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.TryGetValue(name, out var value) ? value : null;
        internal Catalog(string release, bool all)
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalog", release + ".json")))!;
            var names = new[] { "SaveProject", "GetSessionState", "ListExportHandles", "CallTool", "GetOpennessWorkerStatus", "GetNativeInvocationLog", "RestartOpennessWorker" };
            All = root["tools"]!.AsArray().Where(t => all || names.Contains((string)t!["name"]!)).Select(t => {
                string name = (string)t!["name"]!;
                var schema = JsonSerializer.SerializeToElement(t["inputSchema"]);
                var parameters = schema.GetProperty("properties").EnumerateObject().Select(p => new ToolParameterDescriptor(p.Name, "integer",
                    p.Name is "limit" or "take" ? "System.Int32" : p.Name == "confirmRestart" ? "System.Boolean" : p.Name == "arguments" ? typeof(ToolArguments).FullName! : "System.String",
                    false, null, null, "", false, Array.Empty<string>())).ToArray();
                var classification = ToolMetadata.Find(name)!;
                return new ToolDescriptor(name, "", new Tool { Name = name, InputSchema = schema },
                    new ToolDescriptorClassification(classification.Layer, classification.Domain, classification.Operation, classification.BatchRead, classification.BatchWrite),
                    name + "()", parameters, new ToolDryRunDescriptor(false, true), null, ToolExecution.Table[name]);
            }).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            BehaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(Catalog).Assembly, release);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ApprovalSettings previous;
        private readonly ImportStagingHostLifetime lifetime = new();
        private readonly Dictionary<string, McpServerTool> tools;
        internal Catalog Catalog { get; }
        internal Worker Worker { get; } = new();
        internal Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>> Wait =
            (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, false, null));
        internal Fixture(string release = "21", bool all = false)
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.Combine(AppContext.BaseDirectory, "fixture-data"));
            previous = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            EngineHostConfiguration.ReleaseKey = release; EngineHostConfiguration.Worker = Worker;
            Catalog = new Catalog(release, all);
            McpServer.ConfigureToolBridge(Catalog, new WorkerToolInvoker(Catalog, Worker, lifetime), () => true, new HashSet<string>(Catalog.All.Keys));
            InvocationJournal.BindingSnapshot = () => Worker.Binding as JsonObject;
            InvocationJournal.ConfigureOutput(_ => { });
            new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
            McpServer.ApprovalWaitOverrideForTests = (pending, settings, token) => Wait(pending, settings, token);
            tools = McpServer.WrapTools(McpServer.GetAllTools()).ToDictionary(t => t.ProtocolTool.Name);
        }
        internal Task<CallToolResult> Call(string name, string json) => tools[name].InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } }).AsTask();
        public void Dispose()
        {
            McpServer.ApprovalWaitOverrideForTests = null; InvocationJournal.ConfigureOutput(null);
            previous.Save(ApprovalSettings.SettingsPath); lifetime.Dispose();
        }
    }
    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType == typeof(string) ? null
            : method.ReturnType == typeof(IServiceProvider) ? new EmptyServices() : null;
        private sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
    }
    private sealed class Worker : IEngineWorker
    {
        internal bool Unknown, Native, Limited;
        internal bool Yield;
        internal Action<string>? Event;
        internal bool Ready = true;
        internal int Acquired;
        internal bool? RestartConfirmed;
        internal readonly List<string> Calls = new();
        internal TaskCompletionSource? Hold;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Faulted { get; set; }
        public JsonNode? Binding { get; set; }
        public object SessionKey { get; set; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = Ready, ["cause"] = "fixture-worker-cause",
            ["recommendedFix"] = "fixture-worker-fix", ["recommendedFixZh"] = "fixture-worker-fix-zh" } };
        public async Task<JsonObject> Status(CancellationToken token)
        {
            Event?.Invoke("status");
            if (Yield) await Task.Yield();
            return Snapshot();
        }
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) { RestartConfirmed = confirmed; return Task.FromResult(new JsonObject { ["success"] = true }); }
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IDisposable> Acquire(CancellationToken token)
        {
            if (Yield) await Task.Yield();
            await gate.WaitAsync(token); Acquired++; Event?.Invoke("lane"); return new Lane(gate);
        }
        public async Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            Event?.Invoke("invoke");
            if (Yield) await Task.Yield();
            Calls.Add(name); Started.TrySetResult();
            if (Hold != null) await Hold.Task;
            var error = Limited ? new Error("limit", new LimitExceededDetails("response", 16 * 1024 * 1024, null))
                : Unknown ? new Error("unknown", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>())) : null;
            var result = McpServer.V4Result(name, Limited ? new JsonObject { ["nativeOutcomeUnknown"] = true } : new JsonObject(), error,
                Unknown ? Outcome.Unknown : Limited ? Outcome.Failed : Outcome.Succeeded, Unknown ? Execution.Unknown : Limited ? Execution.Completed : Execution.ReadOnly,
                Unknown ? Completeness.Unknown : Limited ? Completeness.None : Completeness.Complete);
            return new EngineReply(result, Native, Binding, null, null);
        }
        public void Dispose() { }
        private sealed class Lane(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
    }
}
