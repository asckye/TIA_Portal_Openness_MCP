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
        using (var scope = new Scope("")) first = scope.SpillsDirectory;
        using (var scope = new Scope("")) Assert.NotEqual(first, scope.SpillsDirectory);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Worker_inherits_host_diagnostics_directory(string release)
    {
        using var scope = new Scope("", release);
        JsonNode? status = await scope.Worker.Observe("status", new JsonObject(), CancellationToken.None);
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

    private sealed class Scope : IDisposable
    {
        internal readonly Client Worker;
        internal readonly List<JsonObject> Rows = new();
        private readonly string faultVariable;
        private readonly string? previousFault;
        private readonly string? previousData = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        private readonly string dataDirectory;
        private readonly string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        private readonly string gate;
        private readonly Foundation.HostSession session;
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly McpServerTool engine;
        private readonly McpServerTool shared;
        internal readonly string SpillsDirectory = Path.Combine(AppContext.BaseDirectory, "worker-fixture-spills", Guid.NewGuid().ToString("N"));
        internal Scope(string mode, string release = "21")
        {
            faultVariable = "TIA_MCP_PLC_WORKER_FAULT";
            previousFault = Environment.GetEnvironmentVariable(faultVariable);
            Environment.SetEnvironmentVariable(faultVariable, mode);
            // Parallel shard processes share this output directory; each scope owns its data location.
            dataDirectory = Path.Combine(AppContext.BaseDirectory, "worker-fixture-data", "plc-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", dataDirectory);
            gate = Path.Combine(dataDirectory, "gate");
            if (mode == "hang")
            {
                Directory.CreateDirectory(gate);
                Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
            }
            string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
            Assert.True(File.Exists(exe), exe);
            TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            // The process data location is cached at its first use; the explicit spills directory isolates each scope.
            // A successful 16/32 MiB spill includes serialization and a durable disk flush.
            // Keep the short deadline for injected faults, not as a performance gate for CI disks.
            string budget = mode is "oversized" or "oversized-delayed" ? "30" : "0.8";
            Worker =
                new Client(new Options {
                    ReleaseKey = release, WorkerExe = exe, ApiDirectory = Path.GetDirectoryName(exe)!, BundleRoot = AppContext.BaseDirectory,
                NativeEnabled = true, BundledWorker = false, WorkerTimeoutConfig = "{\"default\":" + budget + ",\"compile\":0.8,\"firstAttach\":1.2}" },
                    SpillsDirectory);
            Engine.EngineHostConfiguration.ReleaseKey = release; Engine.EngineHostConfiguration.Worker = Worker;
            var ports = Foundation.LegacyHostToolRegistry.Create(Worker, release, true, hostPorts: false);
            var catalog = new Engine.FoundationCatalog(release, ports);
            Host.ConfigureToolBridge(catalog, new Engine.WorkerToolInvoker(catalog, Worker, lifetime, ports), () => false, new HashSet<string>());
            Journal.ConfigureOutput(row => Rows.Add(JsonNode.Parse(row)!.AsObject()));
            // Spill checks exercise the original unpaged read. BlockPage deliberately
            // preserves its cursor and is covered by Round2BlockPageTests instead.
            var spillRead = Foundation.FoundationTools.Definitions.Single(d => d.Name == "ListPlcBlocks")
                with { Name = "GetPlcBlockHierarchy", Operation = "ReadBlocks", ResponseMember = "Items" };
            shared = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(spillRead, Worker),
                release, () => new(false, 1), readinessForTest: () => new JsonObject { ["ready"] = true });
            // V14-V16 use the existing unpaged block-read fixture for the export response.
            engine = ports.SingleOrDefault(tool => tool.ProtocolTool.Name == SessionFixtureTool.Name)
                ?? shared;

            // The first generation starts and reports its status under the production budgets; the scaled budgets above apply
            // from the first operation a test measures (P8-stab-1).
            using (Worker.ProductionBudgets()) Worker.Status(CancellationToken.None).GetAwaiter().GetResult();
            var tools = new[] { engine, shared }.Distinct().ToArray();
            session = new Foundation.HostSession(release, "fixture", true, tools, tools,
                ((Foundation.WorkerClient)Worker.Client).OperatorSnapshot, () => { }, nativeConsentDirectory: dataDirectory,
                lateResults: Worker.LateResults, unsettled: Worker.Unsettled);
        }
        internal Task<CallToolResult> Call(bool foundation, string? payload = null, CancellationToken token = default)
        {
            var tool = foundation ? shared : engine;
            var args = tool.ProtocolTool.Name == SessionFixtureTool.Name ? new JsonObject { ["category"] = "application", ["name"] = payload ?? "" }
                : new JsonObject { ["softwarePath"] = "PLC" };
            if (ReferenceEquals(tool, shared)) args["regexName"] = payload ?? "";
            var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
            var request = new RequestContext<CallToolRequestParams>(server) { Params = JsonSerializer.Deserialize<CallToolRequestParams>(new JsonObject {
                ["name"] = tool.ProtocolTool.Name, ["arguments"] = args }.ToJsonString()) };
            return session.OperatorTools.Single(entry => entry.ProtocolTool.Name == tool.ProtocolTool.Name).InvokeAsync(request, token).AsTask();
        }
        internal void ClearFault() => Environment.SetEnvironmentVariable(faultVariable, null);
        internal void Release() { if (Directory.Exists(gate)) File.WriteAllText(Path.Combine(gate, "release"), ""); }
        internal async Task WaitCompleted()
        {
            using var deadline = new CancellationTokenSource(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
            while ((bool?)session.Snapshot()["callInProgress"] == true) await Task.Delay(5, deadline.Token);
        }
        internal void EndFaultedFixture()
        {
            var child = (System.Diagnostics.Process?)Worker.Client.GetType().GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Worker.Client);
            if (child is { HasExited: false })
            {
                string expected = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture",
                    "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
                Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(child.MainModule!.FileName), ignoreCase: true);
                Foundation.WorkerShutdown.KillOwned(child);
            }
        }
        internal string FailureDetails(CallToolResult? result = null)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var process = (System.Diagnostics.Process?)Worker.Client.GetType().GetField("process", flags)!.GetValue(Worker.Client);
            var channel = (ChannelClient?)Worker.Client.GetType().GetField("channel", flags)!.GetValue(Worker.Client);
            var stderr = (Queue<string>)Worker.Client.GetType().GetField("diagnostics", flags)!.GetValue(Worker.Client)!;
            var details = new JsonObject { ["result"] = result?.StructuredContent?.DeepClone(), ["worker"] = Worker.Snapshot(),
                ["faultReason"] = channel?.LastFault?.ToString(), ["workerExited"] = process?.HasExited,
                ["workerExitCode"] = process?.HasExited == true ? process.ExitCode : null,
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
            Release();
            Assert.True(SpinWait.SpinUntil(() => (bool?)session.Snapshot()["callInProgress"] != true,
                TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
            session.Dispose();
            Worker.Dispose();
            EndFaultedFixture();
            Worker.Dispose(); lifetime.Dispose(); Journal.ConfigureOutput(null);
            if (Directory.Exists(SpillsDirectory)) Directory.Delete(SpillsDirectory, true);
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
            Environment.SetEnvironmentVariable(faultVariable, previousFault);
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previousData);
            Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", previousGate);
        }
    }
    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "SendMessageAsync") return Task.CompletedTask;
            if (method.ReturnType == typeof(IServiceProvider)) return new EmptyServices();
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        }
        private sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
    }
    public static IEnumerable<object[]> Faults()
    { foreach (string release in new[] { "20", "21" }) foreach (bool shared in new[] { false, true }) foreach (string mode in new[] { "hang", "crash", "broken-pipe", "malformed", "truncated", "epoch", "tia-lost" }) yield return new object[] { release, shared, mode }; }
    [Fact]
    public async Task Fault_seen_while_the_call_unwinds_names_the_running_operation()
    {
        using var scope = new Scope("crash");
        var worker = (Foundation.WorkerClient)scope.Worker.Client;
        var previous = Journal.BindingSnapshot;
        try
        {
            Journal.BindingSnapshot = () => worker.SupervisorSnapshot(new JsonObject());
            using var request = Foundation.FoundationWorkerRequest.Use("fixture-id", "FixtureTool");
            await worker.Call("FaultFixtureArm", new() { ["method"] = "adapter.SaveProject" }, CancellationToken.None);
            await Assert.ThrowsAnyAsync<Exception>(() => worker.Call("SaveProject", new(), CancellationToken.None));
            Assert.Contains(scope.Rows, row => (string?)row["phase"] == "THREW");
            var fault = worker.SupervisorSnapshot(new JsonObject())["fault"]!;
            Assert.Equal("SaveProject", (string?)fault["operation"]);
            Assert.True((bool?)fault["dispatched"]);
        }
        finally { Journal.BindingSnapshot = previous; }
    }

    [Theory, MemberData(nameof(Faults))]
    public async Task Read_failures_keep_their_outcome_and_real_transport_faults_require_explicit_restart(string release, bool shared, string mode)
    {
        using var scope = new Scope(mode, release);
        var result = await scope.Call(shared);
        bool draining = mode == "hang";
        bool faulted = !draining && mode != "tia-lost";
        Assert.Equal(draining ? "in-progress" : "read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        if (draining) Assert.Equal("OPERATION_IN_PROGRESS", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.NotEmpty(result.StructuredContent!["meta"]!["warnings"]!.AsArray());
        var refused = await scope.Call(!shared);
        Assert.Equal(mode == "tia-lost" ? "NATIVE_OPERATION_FAILED" : "PRECONDITION_FAILED", (string?)refused.StructuredContent?["error"]?["code"]);
        if (mode == "tia-lost") Assert.Equal("read-failed", (string?)refused.StructuredContent?["meta"]?["outcome"]);
        if (faulted) Assert.Equal("worker-faulted", (string?)refused.StructuredContent?["error"]?["details"]?["condition"]);
        if (draining) Assert.Equal("worker-call-running", (string?)refused.StructuredContent?["error"]?["details"]?["condition"]);
        var previous = scope.Worker.Snapshot();
        Assert.Equal(faulted, (string?)previous["state"] == "Faulted");
        Assert.Null(previous["sessionLocked"]);
        Assert.Equal(faulted, previous["fault"] != null);
        object key = scope.Worker.SessionKey;
        scope.ClearFault();
        if (draining)
        {
            scope.Release();
            await scope.WaitCompleted();
        }
        Assert.Contains(scope.Rows, row => (string?)row["phase"] == "RETURNED");
        // The faulting call above ran on the scaled budgets. The explicit restart starts the next generation, which is then
        // rebound and used, under the production budgets (P8-stab-1).
        using var production = scope.Worker.ProductionBudgets();
        var restart = await scope.Worker.Restart(true, CancellationToken.None);
        Assert.True((bool?)restart["success"]); Assert.Null(restart["requiresExplicitRebind"]);
        Assert.NotSame(key, scope.Worker.SessionKey); Assert.Null(scope.Worker.Binding);
        var ended = restart["worker"]!["previousGenerations"]![0]!.AsObject();
        Assert.Equal(new[] { "generation", "endedUtc", "reason" }, ended.Select(pair => pair.Key));
        Assert.Equal(1, (long?)ended["generation"]);
        Assert.Equal("restart", (string?)ended["reason"]);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse((string)ended["endedUtc"]!, System.Globalization.CultureInfo.InvariantCulture).Offset);
        Assert.Null(restart["requiresTiaRestart"]);
        Assert.False(scope.Worker.Faulted);
        await scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        Assert.NotNull(scope.Worker.Binding);
        Assert.True((bool?)(await scope.Call(shared)).StructuredContent?["ok"]);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Cancellation_before_dispatch_is_a_queue_refusal(bool shared)
    {
        using var scope = new Scope("slow"); using var held = await scope.Worker.Acquire(CancellationToken.None);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await scope.Call(shared, token: cancel.Token);
        Assert.Equal("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal("tool-queue", (string?)result.StructuredContent?["error"]?["details"]?["stage"]);
        Assert.False(scope.Worker.Faulted);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Cancellation_after_dispatch_keeps_the_late_result_and_journal(bool shared)
    {
        // Barrier instead of a 70-ms sleep (P8-stab-1): the slow request marks its dispatch on the worker and waits there until
        // this test has cancelled. Under load the sleep cancelled before dispatch or after the reply.
        string gate = Path.Combine(AppContext.BaseDirectory, "worker-fixture-gates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gate);
        string? previousGate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
        Environment.SetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE", gate);
        try
        {
            using var scope = new Scope("slow"); using var cancel = new CancellationTokenSource();
            var call = Task.Run(() => scope.Call(shared, token: cancel.Token));
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
    [Theory, InlineData(false), InlineData(true)]
    public async Task Oversized_request_never_faults_or_dispatches(bool shared)
    {
        using var scope = new Scope("crash");
        var result = await scope.Call(shared, payload: new string('x', ChannelLimits.RequestBytes));
        Assert.Equal("LIMIT_EXCEEDED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.False(scope.Worker.Faulted);
    }
    public static IEnumerable<object[]> ExportReleases()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (bool shared in new[] { false, true }) yield return new object[] { release, shared };
    }
    [Theory, MemberData(nameof(ExportReleases))]
    public async Task Oversized_reply_is_a_readable_export_handle(string release, bool shared)
    {
        using var scope = new Scope("oversized", release); var result = await scope.Call(shared);
        AssertReadableExport(scope, result);
    }
    [Theory, MemberData(nameof(ExportReleases))]
    public async Task Oversized_reply_survives_a_spill_slower_than_the_fault_injection_deadline(string release, bool shared)
    {
        using var scope = new Scope("oversized-delayed", release);
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
        string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
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
            if (!process.HasExited)
            {
                Assert.Equal(Path.GetFullPath(exe), Path.GetFullPath(process.MainModule!.FileName), ignoreCase: true);
                process.Kill();
                process.WaitForExit();
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Assert.True(failure == null, failure + Environment.NewLine + "worker stderr:" + Environment.NewLine + await stderr);
    }
    private static void AssertReadableExport(Scope scope, CallToolResult result)
    {
        Assert.True((bool?)result.StructuredContent?["ok"], scope.FailureDetails(result));
        Assert.False(scope.Worker.Faulted, scope.FailureDetails(result));
        string id = (string)result.StructuredContent!["data"]!["export"]!["id"]!;
        var entry = Engine.SessionExportStore.Get(id);
        Assert.NotNull(entry); Assert.Contains(new string('x', 1000), entry.Content);
        Assert.True(entry.Content.Length > ChannelLimits.ResponseBytes);
        Assert.Equal(20000, Engine.SessionExportStore.Slice(id, 0, 20000).Returned);
        Assert.Empty(Directory.GetFiles(scope.SpillsDirectory));
    }
    [Fact]
    public async Task First_attach_has_a_longer_deadline_and_the_access_confirmation_hint()
    {
        using var scope = new Scope("hang"); var clock = System.Diagnostics.Stopwatch.StartNew();
        using var invocation = scope.Worker.LateResults.CreateInvocation("ConnectPortal", "first-attach", CancellationToken.None);
        using var flow = invocation.Enter();
        var call = scope.Worker.Call("Attach", new() { ["processId"] = 123 }, CancellationToken.None);
        var running = await invocation.Detachment.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
        Assert.True(clock.Elapsed.TotalSeconds >= 1);
        Assert.Contains("Openness access confirmation", (string?)running["message"]);
        Assert.False(scope.Worker.Faulted);
        scope.Release();
        await call.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang);
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
        // The confirmed restart starts the next generation; it does so under the production budgets (P8-stab-1).
        using var production = scope.Worker.ProductionBudgets();
        var restarted = await scope.Worker.Restart(true, CancellationToken.None);
        Assert.True((bool?)restarted["success"]); Assert.Null(scope.Worker.Binding); Assert.NotSame(key, scope.Worker.SessionKey);
        var ended = restarted["worker"]!["previousGenerations"]![0]!.AsObject();
        Assert.Equal(new[] { "generation", "endedUtc", "reason" }, ended.Select(pair => pair.Key));
        Assert.Equal(1, (long?)ended["generation"]);
        Assert.Equal("restart", (string?)ended["reason"]);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse((string)ended["endedUtc"]!, System.Globalization.CultureInfo.InvariantCulture).Offset);
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
