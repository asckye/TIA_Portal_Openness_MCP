extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using View = enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView;

public sealed class SharedPipelineTests
{
    private sealed class Catalog : View
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; }
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable => All;
        public IReadOnlyList<Descriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Descriptor? Find(string name, bool includeUnavailable = false) => All.TryGetValue(name, out var tool) ? tool : null;
        internal Catalog()
        {
            var tool = new Tool { Name = "CompileDevice", Description = "[L1][Hardware][WRITE] Fixture engine-only operation.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }),
                OutputSchema = FoundationV4Result.Schema };
            All = new Dictionary<string, Descriptor> { [tool.Name] = new(tool.Name, tool.Description, tool,
                new("L1", "Hardware", "WRITE", false, true), "CompileDevice()",
                Array.Empty<enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor>(), new(false, false), null, "worker") };
        }
    }
    private sealed class Worker : IFoundationSessionWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal bool EngineUnknown, FoundationUnknown;
        internal int FoundationCalls, EngineCalls, FoundationPreviews, FoundationWrites;
        internal string? FoundationId, EngineId;
        public bool Faulted => false;
        public JsonNode? Binding => new JsonObject { ["projectPath"] = "C:/fixture.ap21" };
        public object SessionKey { get; } = new();
        public string ApprovalIdentity => Binding!.ToJsonString();
        public bool Poisoned => Host.SessionPrecheckRefusal("GetSessionState") != null;
        public bool Bundled => false;
        public bool SharedSession => true;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public void MarkUncertain() => Host.MarkSharedSessionUncertain();
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            FoundationCalls++; FoundationId = AuditInvocation.CurrentRequestId ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
            if ((bool?)arguments["dryRun"] == true) FoundationPreviews++;
            if ((bool?)arguments["dryRun"] == false) FoundationWrites++;
            if (FoundationUnknown && (bool?)arguments["dryRun"] == false) throw new WorkerOperationException("fixture interruption", -32603, "unknown");
            return Task.FromResult<JsonNode?>(operation == "ReadState" ? new JsonObject { ["ReleaseKey"] = "21", ["IsAttached"] = true }
                : new JsonObject { ["Executed"] = (bool?)arguments["dryRun"] != true, ["ProjectFile"] = "C:/fixture.ap21" });
        }
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            EngineCalls++; EngineId = id;
            var result = EngineUnknown ? Host.V4Result(name, new JsonObject { ["nativeOutcomeUnknown"] = true },
                new Error("fixture interruption", new OutcomeUnknownDetails("engine", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown)
                : Host.V4Result(name, Binding!.AsObject());
            return Task.FromResult(new enginehost::TiaMcp.FoundationHost.EngineReply(result, true, Binding, null, null));
        }
        public void Dispose() { }
    }
    private sealed class Scope : IDisposable
    {
        internal readonly Worker Worker = new();
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        internal Scope()
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Worker = Worker;
            var catalog = new Catalog();
            Host.ConfigureToolBridge(catalog, new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, Worker, lifetime), () => false, new HashSet<string>());
        }
        internal FoundationV4Tool Foundation(string source) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source || FoundationV4Tool.Name(d.Name) == source), Worker), "21", null, () => new(false, 1));
        internal void RegisterShared(FoundationV4Tool tool)
        {
            var catalog = new Catalog();
            ((Dictionary<string, Descriptor>)catalog.All)[tool.ProtocolTool.Name] = new(tool.ProtocolTool.Name,
                tool.ProtocolTool.Description!, tool.ProtocolTool, new("L1", "PLC", "WRITE", false, true), "shared fixture",
                Array.Empty<enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor>(), new(false, false), null, "foundation");
            Host.ConfigureToolBridge(catalog, new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, Worker, lifetime, new[] { tool }), () => false, new HashSet<string>());
        }
        internal static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>())
        { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
        public void Dispose() => lifetime.Dispose();
    }

    [Fact]
    public async Task Engine_unknown_is_the_shared_Foundation_lock()
    {
        using var scope = new Scope(); scope.Worker.EngineUnknown = true;
        Host.ToolInvoker.Invoke("CompileDevice", new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())), false);
        var result = await scope.Foundation("GetSessionState").InvokeAsync(Scope.Request("GetSessionState", new()));
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, scope.Worker.FoundationCalls); Assert.Equal(1, scope.Worker.EngineCalls);
    }

    [Fact]
    public async Task Foundation_unknown_is_the_engine_lock()
    {
        using var scope = new Scope(); scope.Worker.FoundationUnknown = true;
        var arguments = new JsonObject { ["plc"] = "PLC", ["table"] = "T", ["name"] = "Ready", ["dataType"] = "Bool", ["address"] = "%M0.0",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap21" };
        var shared = await scope.Foundation("CreatePlcTag").InvokeAsync(Scope.Request("CreatePlcTag", arguments));
        Assert.Equal("unknown", (string?)shared.StructuredContent?["meta"]?["outcome"]);
        var engine = Host.CallTool("CompileDevice", new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())));
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)engine.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, scope.Worker.EngineCalls);
    }

    [Fact]
    public async Task Both_pipelines_keep_one_invocation_correlation()
    {
        using var scope = new Scope(); const string id = "0123456789abcdef0123456789abcdef";
        using var root = AuditInvocation.Begin(true, "fixture", "21", "root", id);
        var shared = await scope.Foundation("GetSessionState").InvokeAsync(Scope.Request("GetSessionState", new()));
        Host.ToolInvoker.Invoke("CompileDevice", new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())), false);
        Assert.Equal(id, (string?)shared.StructuredContent?["meta"]?["requestId"]);
        Assert.Equal(id, scope.Worker.FoundationId); Assert.Equal(id, scope.Worker.EngineId);
    }

    [Fact]
    public async Task Shared_read_inherits_transport_journal_without_write_audit()
    {
        using var scope = new Scope(); const string id = "abcdef0123456789abcdef0123456789";
        using var foundationJournal = TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation(id);
        using var engineJournal = enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation(id);
        var shared = await scope.Foundation("GetSessionState").InvokeAsync(Scope.Request("GetSessionState", new()));
        Host.ToolInvoker.Invoke("CompileDevice", new ToolArguments(JsonSerializer.SerializeToElement(new JsonObject())), false);
        Assert.Equal(id, (string?)shared.StructuredContent?["meta"]?["requestId"]);
        Assert.Equal(id, scope.Worker.FoundationId); Assert.Equal(id, scope.Worker.EngineId);
    }

    [Fact]
    public async Task Shared_local_usage_inherits_transport_journal()
    {
        using var scope = new Scope(); const string id = "fedcba9876543210fedcba9876543210";
        using var journal = TiaMcpServer.ModelContextProtocol.InvocationJournal.UseCorrelation(id);
        var shared = scope.Foundation("GetSessionState");
        var usage = new FoundationV4Tool(new ToolUsageTool("21", () => new[] { shared }), "21", null, sharedSession: true);
        var result = await usage.InvokeAsync(Scope.Request("GetToolUsage", new() { ["toolName"] = "GetSessionState" }));
        Assert.Equal(id, (string?)result.StructuredContent?["meta"]?["requestId"]);
        Assert.Equal("GetSessionState", (string?)result.StructuredContent?["data"]?["toolName"]);
        Assert.Equal(0, scope.Worker.FoundationCalls); Assert.Equal(0, scope.Worker.EngineCalls);
    }

    [Fact]
    public void Shared_bridge_runs_one_precheck_and_one_approval()
    {
        using var scope = new Scope(); int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTag"), scope.Worker), "21", null,
            () => new(true, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
        scope.RegisterShared(tool);
        var args = new JsonObject { ["plc"] = "PLC", ["table"] = "T", ["name"] = "Ready", ["dataType"] = "Bool", ["address"] = "%M0.0",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap21" };
        using var request = Host.UseProgressRequest(Scope.Request("CallTool", new()), CancellationToken.None);
        var result = Host.CallTool("CreatePlcTag", new ToolArguments(JsonSerializer.SerializeToElement(args)));
        Assert.Equal(1, waits); Assert.Equal(2, scope.Worker.FoundationCalls); Assert.Equal(0, scope.Worker.EngineCalls);
        Assert.Equal(1, scope.Worker.FoundationPreviews); Assert.Equal(1, scope.Worker.FoundationWrites);
        Assert.Equal("succeeded", (string?)result.StructuredContent?["meta"]?["outcome"]);
    }
}
