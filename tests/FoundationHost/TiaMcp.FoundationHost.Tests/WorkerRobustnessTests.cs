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
using Client = WorkerBackendFixture;
using Options = foundationhost::TiaMcp.FoundationHost.HostOptions;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Journal = enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Engine = enginehost::TiaMcp.FoundationHost;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using Parameter = enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor;

// Scope sets the process-wide data directory and fault variables. Classes running beside it read
// DataLocations.Current (approval settings, audit directory) and observed that directory mid-test.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerDataDirectoryCollection
{
    public const string Name = "Process-wide worker data directory";
}

[Collection(WorkerDataDirectoryCollection.Name)]
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

    // Two scopes in one process never share a spills directory, so disposing one cannot remove another's spill (P8-stab-1).
    // Before the fix both PlcWorker scopes used the process-cached data location.
    [Fact]
    public void Plc_scopes_spill_into_their_own_directories()
    {
        string first;
        using (var scope = new Scope("", plcWorker: true)) first = scope.SpillsDirectory;
        using (var scope = new Scope("", plcWorker: true)) Assert.NotEqual(first, scope.SpillsDirectory);
    }

    [Theory]
    [InlineData("20", false)]
    [InlineData("21", false)]
    [InlineData("20", true)]
    [InlineData("21", true)]
    public async Task Worker_inherits_host_diagnostics_directory(string release, bool plcWorker)
    {
        using var scope = new Scope("", release, plcWorker);
        // The engine rows keep their original EngineWorkerClient.Status subject; the PlcWorker
        // reports the inherited directory in its HostObserve status reply.
        JsonNode? status = plcWorker ? await scope.Worker.Observe("status", new JsonObject(), CancellationToken.None)
            : await ((Foundation.EngineWorkerClient)scope.Worker.Client).Status(CancellationToken.None);
        Assert.Equal(HostDiagnosticsDirectory(), (string?)status!["diagnosticsDirectory"]);
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
            string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.WorkerChannel.TransportFixture", "TiaMcp.WorkerChannel.TransportFixture.exe");
            Assert.True(File.Exists(exe), exe);
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter.19.dll"), "offline fixture identity");
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
            var tool = new Tool { Name = SessionFixtureTool.Name, Description = "[L1][Hardware][READ] Fixture engine-only operation.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                    ["payload"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false }) };
            All = new Dictionary<string, Descriptor> { [tool.Name] = new(tool.Name, tool.Description, tool, new("L1", "Hardware", "READ", true, false),
                SessionFixtureTool.Name + "(payload?: string)", new[] { new Parameter("payload", "string", "System.String", false, null, null, "", false, null) }, new(false, true), null, "worker") };
        }
    }
    private sealed class Scope : IDisposable
    {
        internal readonly Client Worker;
        internal readonly List<JsonObject> Rows = new();
        private readonly string faultVariable;
        private readonly string? previousFault;
        private readonly bool plcWorker;
        private readonly string? previousData = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        private readonly string? dataDirectory;
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly McpServerTool engine;
        private readonly McpServerTool shared;
        internal readonly string SpillsDirectory = Path.Combine(AppContext.BaseDirectory, "worker-fixture-spills", Guid.NewGuid().ToString("N"));
        internal Scope(string mode, string release = "21", bool plcWorker = false)
        {
            this.plcWorker = plcWorker;
            faultVariable = plcWorker ? "TIA_MCP_PLC_WORKER_FAULT" : "TIA_MCP_ENGINE_WORKER_FAULT";
            previousFault = Environment.GetEnvironmentVariable(faultVariable);
            Environment.SetEnvironmentVariable(faultVariable, mode);
            // PlcWorker rows spill into the data location, and parallel shard processes share this
            // output directory: give each such scope its own location, removed on disposal.
            dataDirectory = plcWorker ? Path.Combine(AppContext.BaseDirectory, "worker-fixture-data", "plc-" + Guid.NewGuid().ToString("N")) : null;
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "worker-fixture-data"));
            string exe = plcWorker ? Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/bin/Release/net48/TiaMcp.PlcWorker.FaultFixture.exe")
                : TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.EngineWorker.FaultFixture", "TiaMcp.EngineWorker.FaultFixture.exe");
            Assert.True(File.Exists(exe), exe);
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            // Both backends spill into this scope's own directory (P8-stab-1). The PlcWorker rows used the process data location,
            // which is cached at its first use and can be the worker-fixture-data directory every test process shares; deleting it
            // at dispose broke another process's spill (DirectoryNotFoundException in AssertReadableExport).
            // A successful 16/32 MiB spill includes serialization and a durable disk flush.
            // Keep the short deadline for injected faults, not as a performance gate for CI disks.
            string budget = mode is "oversized" or "oversized-delayed" ? "30" : "0.8";
            Worker = new Client(plcWorker, new Options { ReleaseKey = release, EngineWorkerExe = exe, WorkerExe = exe, ApiDirectory = Path.GetDirectoryName(exe)!, BundleRoot = AppContext.BaseDirectory,
                NativeEnabled = true, BundledWorker = false, WorkerTimeoutConfig = "{\"default\":" + budget + ",\"compile\":0.8,\"firstAttach\":1.2}" }, Client.Hash(exe), SpillsDirectory, TimeSpan.FromSeconds(30));
            Engine.EngineHostConfiguration.ReleaseKey = release; Engine.EngineHostConfiguration.Worker = Worker;
            var ports = plcWorker ? Foundation.LegacyHostToolRegistry.Create(Worker, release, true, hostPorts: false) : null;
            enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView catalog = plcWorker
                ? new Engine.FoundationCatalog(release, ports!) : new Catalog();
            Host.ConfigureToolBridge(catalog, new Engine.WorkerToolInvoker(catalog, Worker, lifetime, ports), () => false, new HashSet<string>());
            Journal.ConfigureOutput(row => Rows.Add(JsonNode.Parse(row)!.AsObject()));
            engine = plcWorker ? ports!.Single(tool => tool.ProtocolTool.Name == SessionFixtureTool.Name)
                : Host.WrapTools(new[] { Host.ToolInvoker.CreateTool(catalog.All[SessionFixtureTool.Name]) }).Single();
            shared = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(Foundation.FoundationTools.Definitions.Single(d => d.Name == "GetBlocks"), Worker),
                release, null, () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true });
            Worker.SessionLocked = () => Host.SessionPrecheckRefusal("GetSessionState") != null;
            // The first generation starts and reports its status under the production budgets; the scaled budgets above apply
            // from the first operation a test measures (P8-stab-1).
            using (Worker.ProductionBudgets()) Worker.Status(CancellationToken.None).GetAwaiter().GetResult();
        }
        internal Task<CallToolResult> Call(bool foundation, CancellationToken token = default, string? payload = null, bool progress = false)
        {
            var tool = foundation ? shared : engine;
            var args = plcWorker && !foundation ? new JsonObject { ["category"] = "application", ["name"] = payload ?? "" }
                : foundation ? new JsonObject { ["softwarePath"] = "PLC", ["regexName"] = payload ?? "" }
                : payload == null ? new JsonObject() : new JsonObject { ["payload"] = payload };
            var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
            ((ServerProxy)server).Progress = p => { if (FailProgress) throw new IOException("Fixture MCP client disconnected."); Progress.Add(p); };
            var request = new RequestContext<CallToolRequestParams>(server) { Params = JsonSerializer.Deserialize<CallToolRequestParams>(new JsonObject {
                ["name"] = tool.ProtocolTool.Name, ["arguments"] = args, ["_meta"] = progress ? new JsonObject { ["progressToken"] = 42 } : null }.ToJsonString()) };
            return tool.InvokeAsync(request, token).AsTask();
        }
        internal readonly List<JsonObject> Progress = new();
        internal bool FailProgress;
        internal void ClearFault() => Environment.SetEnvironmentVariable(faultVariable, null);
        internal string FailureDetails(CallToolResult? result = null)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var process = (System.Diagnostics.Process?)Worker.Client.GetType().GetField("process", flags)!.GetValue(Worker.Client);
            var channel = (ChannelClient?)Worker.Client.GetType().GetField("channel", flags)!.GetValue(Worker.Client);
            var stderr = (Queue<string>)Worker.Client.GetType().GetField("diagnostics", flags)!.GetValue(Worker.Client)!;
            var details = new JsonObject { ["result"] = result?.StructuredContent?.DeepClone(), ["worker"] = Worker.Snapshot(),
                ["faultReason"] = channel?.LastFault?.ToString(), ["workerExited"] = process?.HasExited,
                ["workerExitCode"] = process?.HasExited == true ? process.ExitCode : null,
                ["statusTimeoutSeconds"] = Worker.StatusTimeoutForTest?.TotalSeconds,
                ["spillDirectory"] = SpillsDirectory, ["spillDirectoryExists"] = Directory.Exists(SpillsDirectory) };
            lock (stderr) details["stderrTail"] = new JsonArray(stderr.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
            try
            {
                details["spillFiles"] = new JsonArray((Directory.Exists(SpillsDirectory) ? Directory.GetFiles(SpillsDirectory) : Array.Empty<string>())
                    .Select(path => (JsonNode)new JsonObject { ["path"] = path, ["byteLength"] = new FileInfo(path).Length,
                        ["attributes"] = File.GetAttributes(path).ToString() }).ToArray());
            }
            catch (IOException error) { details["spillStateError"] = error.ToString(); }
            return details.ToJsonString();
        }
        public void Dispose()
        {
            Worker.Dispose(); lifetime.Dispose(); Journal.ConfigureOutput(null);
            if (Directory.Exists(SpillsDirectory)) Directory.Delete(SpillsDirectory, true);
            if (dataDirectory != null && Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
            Environment.SetEnvironmentVariable(faultVariable, previousFault);
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
    { foreach (string release in new[] { "20", "21" }) foreach (bool plcWorker in new[] { false, true }) foreach (bool shared in new[] { false, true }) foreach (string mode in new[] { "hang", "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost" }) yield return new object[] { release, shared, mode, plcWorker }; }
    [Theory, MemberData(nameof(Faults))]
    public async Task Faults_lock_both_pipelines_and_require_explicit_restart(string release, bool shared, string mode, bool plcWorker)
    {
        using var scope = new Scope(mode, release, plcWorker);
        var result = await scope.Call(shared);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.NotEmpty(result.StructuredContent!["meta"]!["warnings"]!.AsArray());
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await scope.Call(!shared)).StructuredContent?["error"]?["code"]);
        var previous = scope.Worker.Snapshot();
        Assert.Equal("Faulted", (string?)previous["state"]); Assert.True((bool?)previous["sessionLocked"]); Assert.NotNull(previous["fault"]);
        Assert.Contains(scope.Rows, row => (string?)row["phase"] == "RETURNED");
        object key = scope.Worker.SessionKey;
        scope.ClearFault();
        // The faulting call above ran on the scaled budgets. The explicit restart starts the next generation, which is then
        // rebound and used, under the production budgets (P8-stab-1).
        using var production = scope.Worker.ProductionBudgets();
        var restart = await scope.Worker.Restart(true, CancellationToken.None);
        Assert.True((bool?)restart["success"]); Assert.True((bool?)restart["requiresExplicitRebind"]);
        Assert.NotSame(key, scope.Worker.SessionKey); Assert.Null(scope.Worker.Binding);
        Assert.NotNull(restart["worker"]?["previousGenerations"]?[0]?["fault"]);
        Assert.False(scope.Worker.Poisoned);
        await scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        Assert.NotNull(scope.Worker.Binding);
        Assert.True((bool?)(await scope.Call(shared)).StructuredContent?["ok"]);
    }
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task Cancellation_before_dispatch_is_a_queue_refusal(bool shared, bool plcWorker)
    {
        using var scope = new Scope("slow", plcWorker: plcWorker); using var held = await scope.Worker.Acquire(CancellationToken.None);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await scope.Call(shared, cancel.Token);
        Assert.Equal("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal("tool-queue", (string?)result.StructuredContent?["error"]?["details"]?["stage"]);
        Assert.False(scope.Worker.Faulted);
    }
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task Cancellation_after_dispatch_keeps_the_late_result_and_journal(bool shared, bool plcWorker)
    {
        // Barrier instead of a 70-ms sleep (P8-stab-1): the slow request marks its dispatch on the worker and waits there until
        // this test has cancelled. Under load the sleep cancelled before dispatch or after the reply.
        string gate = Path.Combine(AppContext.BaseDirectory, "worker-fixture-gates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gate);
        string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
        try
        {
            using var scope = new Scope("slow", plcWorker: plcWorker); using var cancel = new CancellationTokenSource();
            var call = Task.Run(() => scope.Call(shared, cancel.Token));
            using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!File.Exists(Path.Combine(gate, "dispatched")))
                {
                    Assert.False(call.IsCompleted, "The call completed before its request reached the worker.");
                    await Task.Delay(5, bound.Token);
                }
            cancel.Cancel();
            File.WriteAllText(Path.Combine(gate, "release"), "");
            var result = await call;
            Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString()); Assert.False(scope.Worker.Faulted);
            Assert.Contains(scope.Rows, row => (string?)row["phase"] == "RETURNED_AFTER_CANCELLATION");
            Assert.True((bool?)(await scope.Call(shared)).StructuredContent?["ok"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", previousGate);
            Directory.Delete(gate, true);
        }
    }
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task Oversized_request_never_faults_or_dispatches(bool shared, bool plcWorker)
    {
        using var scope = new Scope("crash", plcWorker: plcWorker);
        var result = await scope.Call(shared, payload: new string('x', ChannelLimits.RequestBytes));
        Assert.Equal("LIMIT_EXCEEDED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.False(scope.Worker.Faulted);
    }
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task Oversized_reply_is_a_readable_export_handle(bool shared, bool plcWorker)
    {
        using var scope = new Scope("oversized", plcWorker: plcWorker); var result = await scope.Call(shared);
        AssertReadableExport(scope, result);
    }
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task Oversized_reply_survives_a_spill_slower_than_the_fault_injection_deadline(bool shared, bool plcWorker)
    {
        using var scope = new Scope("oversized-delayed", plcWorker: plcWorker);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await scope.Call(shared);
        AssertReadableExport(scope, result);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1200), scope.FailureDetails(result));
    }
    // The net48 PlcWorker writes the spill below the host's data location. On a CI checkout that directory was
    // 235 characters, .NET Framework refused the 281-character spill file and the worker exited mid-reply.
    // 240 keeps the directory itself creatable, as on CI; 300 puts the directory beyond MAX_PATH as well.
    [Theory, InlineData(240), InlineData(300)]
    public async Task Net48_worker_spills_an_oversized_reply_beyond_MAX_PATH(int directoryLength)
    {
        string exe = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/bin/Release/net48/TiaMcp.PlcWorker.FaultFixture.exe");
        Assert.True(File.Exists(exe), exe);
        string adapter = Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter.21.dll");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(adapter, "offline fixture identity");
        string root = Path.Combine(AppContext.BaseDirectory, "worker-fixture-spills", Guid.NewGuid().ToString("N")), directory = root;
        while (directory.Length < directoryLength)
            directory = Path.Combine(directory, new string('d', Math.Clamp(directoryLength - directory.Length - 1, 1, 40)));
        Assert.True(directory.Length + "\\".Length + 32 + ".json.pending".Length >= 260, directory);
        string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        // The channel uses the raw streams, and UTF-8 without a preamble keeps a BOM out of stdin, as in WorkerClient.
        // Stderr keeps the console code page that the net48 fixture writes in.
        var utf8 = new System.Text.UTF8Encoding(false);
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = utf8, StandardOutputEncoding = utf8 };
        foreach (string argument in new[] { "--native-session", "21", Path.GetDirectoryName(exe)!, nonce }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_PLC_WORKER_FAULT"] = "oversized";
        start.Environment["TIA_MCP_WORKER_SPILLS_DIRECTORY"] = directory;
        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        string? failure = null;
        try
        {
            using (var channel = new ChannelClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                new ChannelIdentity("21", Client.Hash(exe), Client.Hash(adapter), process.Id, nonce)))
            {
                var budget = TimeSpan.FromSeconds(30);
                await channel.ConnectAsync(budget);
                string correlation = Guid.NewGuid().ToString("N");
                await channel.CallToolAsync("adapter.FaultFixtureArm", "{\"method\":\"adapter.ReadBlocks\"}", BindingChange.None, true, budget, false, correlation, "ListPlcBlocks");
                string wire = await channel.CallToolAsync("adapter.ReadBlocks", "{}", BindingChange.None, true, budget, false, correlation, "ListPlcBlocks");
                string full = WorkerReplySpill.Read(directory, wire, out bool spilled);
                Assert.True(spilled, wire);
                Assert.True(full.Length > ChannelLimits.ResponseBytes);
                Assert.Contains(new string('x', 1000), full);
                Assert.Empty(Directory.GetFiles(directory));
                Assert.False(channel.Poisoned);
            }
            // Disposing the channel closes the fixture's input; a worker that did not fault then returns 0.
            Assert.True(process.WaitForExit(30000), "The worker fixture did not exit after its input closed.");
            Assert.Equal(0, process.ExitCode);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            // Only this synthetic child is terminated; production clients never kill a worker.
            if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Assert.True(failure == null, failure + Environment.NewLine + "worker stderr:" + Environment.NewLine + await stderr);
    }
    private static void AssertReadableExport(Scope scope, CallToolResult result)
    {
        Assert.True((bool?)result.StructuredContent?["ok"], scope.FailureDetails(result));
        Assert.False(scope.Worker.Faulted, scope.FailureDetails(result));
        string id = (string)result.StructuredContent!["data"]!["export"]!["id"]!;
        var entry = TiaMcpServer.ModelContextProtocol.ExportStore.Get(id);
        Assert.NotNull(entry); Assert.Contains(new string('x', 1000), entry.Content);
        Assert.True(entry.Content.Length > ChannelLimits.ResponseBytes);
        Assert.Equal(20000, TiaMcpServer.ModelContextProtocol.ExportStore.Slice(id, 0, 20000).Returned);
        Assert.Empty(Directory.GetFiles(scope.SpillsDirectory));
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
    [Theory, InlineData(false), InlineData(true)]
    public async Task First_attach_has_a_longer_deadline_and_the_access_confirmation_hint(bool plcWorker)
    {
        using var scope = new Scope("hang", plcWorker: plcWorker); var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ChannelFaultException>(() => scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None));
        Assert.True(clock.Elapsed.TotalSeconds >= 1);
        Assert.Contains("Openness access confirmation", error.Message);
        Assert.True(scope.Worker.Faulted);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Restart_clears_an_adopted_binding_and_refuses_a_busy_lane(bool plcWorker)
    {
        using var scope = new Scope("", plcWorker: plcWorker);
        await scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        Assert.NotNull(scope.Worker.Binding);
        object key = scope.Worker.SessionKey;
        Assert.True((bool?)(await scope.Worker.Restart(false, CancellationToken.None))["dryRun"]);
        Assert.Same(key, scope.Worker.SessionKey); Assert.NotNull(scope.Worker.Binding);
        using (await scope.Worker.Acquire(CancellationToken.None))
            Assert.False((bool?)(await scope.Worker.Restart(true, CancellationToken.None))["success"]);
        // The confirmed restart starts the next generation; it does so under the production budgets (P8-stab-1).
        using var production = scope.Worker.ProductionBudgets();
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
