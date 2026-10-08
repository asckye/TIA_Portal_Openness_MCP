using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;
using Xunit.Abstractions;

public sealed class PipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("20", 480)]
    [InlineData("21", 491)]
    public void EveryCatalogEntryHasReviewedOwnershipAndALocalImplementation(string release, int count)
    {
        using var fixture = new Fixture(release, all: true);
        Assert.Equal(count, fixture.Catalog.All.Count);
        Assert.Equal(45, fixture.Catalog.All.Values.Count(t => t.Execution == "host"));
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
        internal bool Ready = true;
        internal int Acquired;
        internal bool? RestartConfirmed;
        internal readonly List<string> Calls = new();
        internal TaskCompletionSource? Hold;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Faulted { get; set; }
        public JsonNode? Binding { get; set; }
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = Ready, ["cause"] = "fixture-worker-cause",
            ["recommendedFix"] = "fixture-worker-fix", ["recommendedFixZh"] = "fixture-worker-fix-zh" } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) { RestartConfirmed = confirmed; return Task.FromResult(new JsonObject { ["success"] = true }); }
        public Task<IDisposable> Acquire(CancellationToken token) { Acquired++; return Task.FromResult<IDisposable>(new Lane()); }
        public async Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
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
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
