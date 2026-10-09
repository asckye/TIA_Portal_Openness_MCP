extern alias foundationhost;
extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.WorkerChannel;
using Xunit;
using Client = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Journal = enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Engine = enginehost::TiaMcp.FoundationHost;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using Parameter = enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor;

public sealed class WorkerRobustnessTests
{
    private static string HostDiagnosticsDirectory()
    {
        var type = typeof(Foundation.WorkerClient).Assembly.GetType("TiaOpenness.Shared.DataLocations")
            ?? typeof(Meta).Assembly.GetType("TiaOpenness.Shared.DataLocations", true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var current = type.GetProperty("Current", flags)!.GetValue(null)!;
        return (string)type.GetProperty("DiagnosticsDirectory", flags)!.GetValue(current)!;
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Worker_inherits_host_diagnostics_directory(string release)
    {
        using var scope = new Scope("", release);
        var status = await scope.Worker.Status(CancellationToken.None);
        Assert.Equal(HostDiagnosticsDirectory(), (string?)status["diagnosticsDirectory"]);
    }

    [Fact]
    public async Task Plc_worker_inherits_host_diagnostics_directory()
    {
        string? previousLog = Environment.GetEnvironmentVariable("TIA_FIXTURE_LOG");
        string? previousFault = Environment.GetEnvironmentVariable("TIA_FIXTURE_FAULT");
        string log = Path.Combine(Path.GetTempPath(), "p707b-worker-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Environment.SetEnvironmentVariable("TIA_FIXTURE_LOG", log);
            Environment.SetEnvironmentVariable("TIA_FIXTURE_FAULT", "");
            string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe"));
            Assert.True(File.Exists(exe), exe);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter.19.dll"), "offline fixture identity");
            using var worker = new Foundation.WorkerClient("19", exe, Path.GetDirectoryName(exe)!, true) { Bundled = false };
            var state = await worker.Call("ReadState", new JsonObject(), CancellationToken.None);
            Assert.Equal(HostDiagnosticsDirectory(), (string?)state!["diagnosticsDirectory"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_FIXTURE_LOG", previousLog);
            Environment.SetEnvironmentVariable("TIA_FIXTURE_FAULT", previousFault);
            if (File.Exists(log)) File.Delete(log);
        }
    }

    private sealed class Catalog : enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; }
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable => All;
        public IReadOnlyList<Descriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Descriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal Catalog()
        {
            var tool = new Tool { Name = "CompileDevice", Description = "[L1][Hardware][READ] Fixture engine-only operation.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                    ["payload"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false }) };
            All = new Dictionary<string, Descriptor> { [tool.Name] = new(tool.Name, tool.Description, tool, new("L1", "Hardware", "READ", true, false),
                "CompileDevice(payload?: string)", new[] { new Parameter("payload", "string", "System.String", false, null, null, "", false, null) }, new(false, true), null, "worker") };
        }
    }
    private sealed class Scope : IDisposable
    {
        internal readonly Client Worker;
        internal readonly List<JsonObject> Rows = new();
        private readonly string? previousFault = Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_WORKER_FAULT");
        private readonly string? previousData = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly McpServerTool engine;
        private readonly McpServerTool shared;
        internal Scope(string mode, string release = "21")
        {
            Environment.SetEnvironmentVariable("TIA_MCP_ENGINE_WORKER_FAULT", mode);
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.Combine(AppContext.BaseDirectory, "worker-fixture-data"));
            string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../WorkerChannel/TiaMcp.EngineWorker.FaultFixture/bin/Release/net10.0/TiaMcp.EngineWorker.FaultFixture.exe"));
            Assert.True(File.Exists(exe), exe);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            Worker = new Client(new Options { ReleaseKey = release, EngineWorkerExe = exe, WorkerExe = exe, ApiDirectory = "", BundleRoot = AppContext.BaseDirectory,
                NativeEnabled = true, WorkerTimeoutConfig = "{\"default\":0.8,\"compile\":0.8,\"firstAttach\":1.2}" }, Client.Hash(exe));
            Engine.EngineHostConfiguration.ReleaseKey = release; Engine.EngineHostConfiguration.Worker = Worker;
            var catalog = new Catalog();
            Host.ConfigureToolBridge(catalog, new Engine.WorkerToolInvoker(catalog, Worker, lifetime), () => false, new HashSet<string>());
            Journal.ConfigureOutput(row => Rows.Add(JsonNode.Parse(row)!.AsObject()));
            engine = Host.WrapTools(new[] { Host.ToolInvoker.CreateTool(catalog.All["CompileDevice"]) }).Single();
            shared = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(Foundation.FoundationTools.Definitions.Single(d => d.Name == "GetBlocks"), Worker),
                release, null, () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true });
            Worker.SessionLocked = () => Host.SessionPrecheckRefusal("GetSessionState") != null;
            Worker.Status(CancellationToken.None).GetAwaiter().GetResult();
        }
        internal Task<CallToolResult> Call(bool foundation, CancellationToken token = default, string? payload = null, bool progress = false)
        {
            var tool = foundation ? shared : engine;
            var args = foundation ? new JsonObject { ["softwarePath"] = "PLC", ["regexName"] = payload ?? "" }
                : payload == null ? new JsonObject() : new JsonObject { ["payload"] = payload };
            var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
            ((ServerProxy)server).Progress = p => { if (FailProgress) throw new IOException("Fixture MCP client disconnected."); Progress.Add(p); };
            var request = new RequestContext<CallToolRequestParams>(server) { Params = JsonSerializer.Deserialize<CallToolRequestParams>(new JsonObject {
                ["name"] = tool.ProtocolTool.Name, ["arguments"] = args, ["_meta"] = progress ? new JsonObject { ["progressToken"] = 42 } : null }.ToJsonString()) };
            return tool.InvokeAsync(request, token).AsTask();
        }
        internal readonly List<JsonObject> Progress = new();
        internal bool FailProgress;
        public void Dispose()
        {
            Worker.Dispose(); lifetime.Dispose(); Journal.ConfigureOutput(null);
            Environment.SetEnvironmentVariable("TIA_MCP_ENGINE_WORKER_FAULT", previousFault);
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previousData);
        }
    }
    public class ServerProxy : DispatchProxy
    {
        internal Action<JsonObject>? Progress;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "SendMessageAsync") { if (args![0] is JsonRpcNotification p) Progress?.Invoke(JsonSerializer.SerializeToNode(p.Params)!.AsObject()); return Task.CompletedTask; }
            if (method.ReturnType == typeof(IServiceProvider)) return new EmptyServices();
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        }
        private sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
    }
    public static IEnumerable<object[]> Faults()
    { foreach (string release in new[] { "20", "21" }) foreach (bool shared in new[] { false, true }) foreach (string mode in new[] { "hang", "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost" }) yield return new object[] { release, shared, mode }; }
    [Theory, MemberData(nameof(Faults))]
    public async Task Faults_lock_both_pipelines_and_require_explicit_restart(string release, bool shared, string mode)
    {
        using var scope = new Scope(mode, release);
        var result = await scope.Call(shared);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.NotEmpty(result.StructuredContent!["meta"]!["warnings"]!.AsArray());
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await scope.Call(!shared)).StructuredContent?["error"]?["code"]);
        var previous = scope.Worker.Snapshot();
        Assert.Equal("Faulted", (string?)previous["state"]); Assert.True((bool?)previous["sessionLocked"]); Assert.NotNull(previous["fault"]);
        Assert.Contains(scope.Rows, row => (string?)row["phase"] == "RETURNED");
        object key = scope.Worker.SessionKey;
        Environment.SetEnvironmentVariable("TIA_MCP_ENGINE_WORKER_FAULT", null);
        var restart = await scope.Worker.Restart(true, CancellationToken.None);
        Assert.True((bool?)restart["success"]); Assert.True((bool?)restart["requiresExplicitRebind"]);
        Assert.NotSame(key, scope.Worker.SessionKey); Assert.Null(scope.Worker.Binding);
        Assert.NotNull(restart["worker"]?["previousGenerations"]?[0]?["fault"]);
        Assert.False(scope.Worker.Poisoned);
        await scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        Assert.NotNull(scope.Worker.Binding);
        Assert.True((bool?)(await scope.Call(shared)).StructuredContent?["ok"]);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Cancellation_before_dispatch_is_a_queue_refusal(bool shared)
    {
        using var scope = new Scope("slow"); using var held = await scope.Worker.Acquire(CancellationToken.None);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await scope.Call(shared, cancel.Token);
        Assert.Equal("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal("tool-queue", (string?)result.StructuredContent?["error"]?["details"]?["stage"]);
        Assert.False(scope.Worker.Faulted);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Cancellation_after_dispatch_keeps_the_late_result_and_journal(bool shared)
    {
        using var scope = new Scope("slow"); using var cancel = new CancellationTokenSource();
        var call = Task.Run(() => scope.Call(shared, cancel.Token));
        await Task.Delay(70); cancel.Cancel();
        var result = await call;
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString()); Assert.False(scope.Worker.Faulted);
        Assert.Contains(scope.Rows, row => (string?)row["phase"] == "RETURNED_AFTER_CANCELLATION");
        Assert.True((bool?)(await scope.Call(shared)).StructuredContent?["ok"]);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Oversized_request_never_faults_or_dispatches(bool shared)
    {
        using var scope = new Scope("crash");
        var result = await scope.Call(shared, payload: new string('x', ChannelLimits.RequestBytes));
        Assert.Equal("LIMIT_EXCEEDED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.False(scope.Worker.Faulted);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Oversized_reply_is_a_readable_export_handle(bool shared)
    {
        using var scope = new Scope("oversized"); var result = await scope.Call(shared);
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString()); Assert.False(scope.Worker.Faulted);
        string id = (string)result.StructuredContent!["data"]!["export"]!["id"]!;
        var entry = TiaMcpServer.ModelContextProtocol.ExportStore.Get(id);
        Assert.NotNull(entry); Assert.Contains(new string('x', 1000), entry.Content);
        Assert.True(entry.Content.Length > ChannelLimits.ResponseBytes);
        Assert.Equal(20000, TiaMcpServer.ModelContextProtocol.ExportStore.Slice(id, 0, 20000).Returned);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Progress_requires_a_token_and_preserves_it(bool enabled)
    {
        using var scope = new Scope("progress"); var result = await scope.Call(false, progress: enabled);
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Equal(enabled ? 3 : 0, scope.Progress.Count);
        Assert.All(scope.Progress, row => Assert.Equal(42, (int?)row["progressToken"]));
    }
    [Fact]
    public async Task Progress_delivery_failure_does_not_poison_the_native_channel()
    {
        using var scope = new Scope("progress"); scope.FailProgress = true;
        Assert.True((bool?)(await scope.Call(false, progress: true)).StructuredContent?["ok"]);
        Assert.False(scope.Worker.Faulted);
        Assert.Contains(scope.Rows, row => (string?)row["phase"] == "PROGRESS_DELIVERY_FAILED");
    }
    [Fact]
    public async Task First_attach_has_a_longer_deadline_and_the_access_confirmation_hint()
    {
        using var scope = new Scope("hang"); var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ChannelFault>(() => scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None));
        Assert.True(clock.Elapsed.TotalSeconds >= 1);
        Assert.Contains("Openness access confirmation", error.Message);
        Assert.True(scope.Worker.Faulted);
    }
    [Fact]
    public async Task Restart_clears_an_adopted_binding_and_refuses_a_busy_lane()
    {
        using var scope = new Scope("");
        await scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        Assert.NotNull(scope.Worker.Binding);
        object key = scope.Worker.SessionKey;
        Assert.True((bool?)(await scope.Worker.Restart(false, CancellationToken.None))["dryRun"]);
        Assert.Same(key, scope.Worker.SessionKey); Assert.NotNull(scope.Worker.Binding);
        using (await scope.Worker.Acquire(CancellationToken.None))
            Assert.False((bool?)(await scope.Worker.Restart(true, CancellationToken.None))["success"]);
        var restarted = await scope.Worker.Restart(true, CancellationToken.None);
        Assert.True((bool?)restarted["success"]); Assert.Null(scope.Worker.Binding); Assert.NotSame(key, scope.Worker.SessionKey);
        Assert.NotNull(restarted["worker"]?["previousGenerations"]?[0]?["binding"]);
    }
    [Fact]
    public void Timeout_data_and_override_precedence_cover_long_native_families()
    {
        var policy = new WorkerTimeoutPolicy(120, "{\"compile\":900}", "{\"compile\":600,\"firstAttach\":360}");
        Assert.Equal(TimeSpan.FromSeconds(120), policy.For("ReadState"));
        Assert.Equal(TimeSpan.FromSeconds(600), policy.For("CompileHardware"));
        Assert.Equal(TimeSpan.FromSeconds(360), policy.For("Attach", true));
        foreach (string operation in new[] { "DownloadPlc", "UploadPlc", "ImportProgram", "ImportDirectory", "ExportBlocks", "ArchiveProject", "RetrieveProject", "UpdateLibrary" })
            Assert.Equal(TimeSpan.FromMinutes(30), policy.For(operation));
        Assert.Throws<ArgumentException>(() => new WorkerTimeoutPolicy(environmentJson: "{\"compile\":0}"));
    }
}
