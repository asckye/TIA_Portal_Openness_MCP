using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class ConcurrencyPerformanceChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class RequestServer : RealProxy
    {
        internal RequestServer() : base(typeof(IMcpServer)) { }
        public override IMessage Invoke(IMessage message) => new ReturnMessage(null, null, 0, null, (IMethodCallMessage)message);
    }
    private sealed class FakeTool : McpServerTool
    {
        private readonly Func<CancellationToken, Task> action;
        private readonly Tool tool;
        internal FakeTool(string name, Func<CancellationToken, Task> action)
        { tool = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }; this.action = action; }
        public override Tool ProtocolTool => tool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            return new CallToolResult { Content = new[] { new TextContentBlock { Text = "{}" } } };
        }
    }
    private static RequestContext<CallToolRequestParams> Request(string name) => new RequestContext<CallToolRequestParams>(
        (IMcpServer)new RequestServer().GetTransparentProxy()) { Params = new CallToolRequestParams {
            Name = name, Arguments = name == "GetToolUsage" ? new Dictionary<string, JsonElement> {
                ["toolName"] = JsonSerializer.SerializeToElement("GetSessionState") } : new Dictionary<string, JsonElement>() } };
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Bound(Task task, int ms = 5000)
    { if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new Exception("Concurrency check timed out."); await task; }

    private static TaskCompletionSource<bool> approvalEntered = Signal(), approvalGranted = Signal();
    private static async Task<T> ApprovalWait<T>(object pending, object settings, CancellationToken token)
    {
        approvalEntered.TrySetResult(true);
        await approvalGranted.Task;
        token.ThrowIfCancellationRequested();
        return (T)Activator.CreateInstance(typeof(T), All, null, new object?[] { pending, false, null }, null)!;
    }

    private sealed class PrecheckTool : McpServerTool
    {
        private readonly Assembly engine;
        internal int Previews, Writes;
        internal bool Refuse;
        internal PrecheckTool(Assembly engine) { this.engine = engine; }
        public override Tool ProtocolTool => new Tool { Name = "CreatePlcTypeGroup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken token = default)
        {
            bool preview = request.Params.Arguments!["dryRun"].GetBoolean();
            if (preview) Previews++; else Writes++;
            var facade = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
            object? result;
            if (Refuse)
            {
                var details = Activator.CreateInstance(Program.FindServerType(engine, "TiaMcp.Logic.V4.InvalidArgumentDetails"), new object[] { "name", Array.Empty<string>() });
                var error = Activator.CreateInstance(Program.FindServerType(engine, "TiaMcp.Logic.V4.Error"), new object[] { "Existing fixture symbol.", details! });
                var method = facade.GetMethod("V4Reject", All)!;
                var values = method.GetParameters().Select(p => p.DefaultValue).ToArray(); values[0] = "CreatePlcTypeGroup"; values[1] = error;
                result = method.Invoke(null, values);
            }
            else
            {
                var method = facade.GetMethods(All).Single(m => m.Name == "V4Result" && m.GetParameters().Length == 5);
                result = method.Invoke(null, new object?[] { "CreatePlcTypeGroup", new JsonObject(), null, false, !preview });
            }
            return new ValueTask<CallToolResult>((CallToolResult)result!);
        }
    }
    private static async Task ApprovalPrecheckChecks(Assembly engine, Func<McpServerTool, McpServerTool> wrap, Delegate callback, PropertyInfo wait)
    {
        RequestContext<CallToolRequestParams> Apply() { var request = Request("CreatePlcTypeGroup"); request.Params = new CallToolRequestParams { Name = "CreatePlcTypeGroup", Arguments = new Dictionary<string, JsonElement> { ["dryRun"] = JsonSerializer.SerializeToElement(false) } }; return request; }
        var settingsType = Program.FindServerType(engine, "TiaOpenness.Shared.ApprovalSettings");
        var settingsPath = settingsType.GetProperty("SettingsPath", All)!.GetValue(null);
        var prior = settingsType.GetMethod("Load", All)!.Invoke(null, new[] { settingsPath });
        var save = settingsType.GetMethod("Save", All)!;
        void Save(object settings) { var args = save.GetParameters().Select(p => p.DefaultValue).ToArray(); args[0] = settingsPath; save.Invoke(settings, args); }
        Save(Activator.CreateInstance(settingsType, All, null, new object[] { true, 1 }, null)!);
        approvalEntered = Signal(); approvalGranted = Signal(); wait.SetValue(null, callback);
        try
        {
            var refused = new PrecheckTool(engine) { Refuse = true };
            var rejection = await wrap(refused).InvokeAsync(Apply());
            if (approvalEntered.Task.IsCompleted || refused.Previews != 1 || refused.Writes != 0
                || rejection.StructuredContent?["meta"]?["warnings"]?.AsArray().Any(w => (string?)w?["details"]?["stage"] == "approval-precheck") != true)
                throw new Exception("Refused precheck requested approval or dispatched a write.");
            var valid = new PrecheckTool(engine);
            var writing = wrap(valid).InvokeAsync(Apply()).AsTask(); await Bound(approvalEntered.Task);
            if (valid.Previews != 1 || valid.Writes != 0) throw new Exception("Approval preceded the own preview.");
            await Bound(wrap(new FakeTool("GetSessionState", _ => Task.CompletedTask)).InvokeAsync(Request("GetSessionState")).AsTask());
            approvalGranted.SetResult(true); await Bound(writing);
            if (valid.Previews != 1 || valid.Writes != 1 || writing.Result.IsError == true) throw new Exception("Valid prechecked write did not dispatch once.");
            Console.WriteLine("PASS serialized approval precheck refusal, own preview and lane release during approval");
        }
        finally { wait.SetValue(null, null); Save(prior!); }
    }

    internal static async Task RegressionChecks(Assembly engine)
    {
        engine.GetType("TiaMcpServer.Runtime.OpennessReadiness", true)!.GetMethod("MarkUnavailable", All)!
            .Invoke(null, new object?[] { "Offline concurrency fixture", "No native dispatch", "No native dispatch", null });
        var facade = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        McpServerTool Wrap(McpServerTool tool) => ((IList<McpServerTool>)facade.GetMethod("WrapWithSerializedCalls", All)!
            .Invoke(null, new object[] { new List<McpServerTool> { tool } })!)[0];
        var taxonomy = Program.FindServerType(engine, "TiaMcpServer.ModelContextProtocol.ToolTaxonomy");
        bool Native(string name) => (bool)taxonomy.GetMethod("UsesOpennessLane")!.Invoke(null, new object[] { name })!;
        if (!Native("UnknownFutureTool") || !Native("GetEnvironmentDiagnostics") || !Native("PreviewToolCall")
            || Native("GetToolUsage") || Native("GetExportContent")) throw new Exception("Reviewed dispatch classification changed.");
        var catalog = engine.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        Type[] loaded;
        try { loaded = engine.GetTypes(); } catch (ReflectionTypeLoadException ex) { loaded = ex.Types.OfType<Type>().ToArray(); }
        foreach (var pair in new[] { ("ToolTypeNames", "TiaMcpServer.ModelContextProtocol."), ("ServiceTypeNames", "TiaMcpServer.Siemens.Services.") })
        {
            var registered = ((string[])catalog.GetField(pair.Item1, All)!.GetValue(null)!).OrderBy(x => x).ToArray();
            var discovered = loaded.Where(t => pair.Item1 == "ToolTypeNames"
                ? t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "McpServerToolTypeAttribute")
                : t.IsClass && !t.IsAbstract && t.Namespace == "TiaMcpServer.Siemens.Services" && t.Name.EndsWith("Service"))
                .Select(t => t.Name).OrderBy(x => x).ToArray();
            if (!registered.SequenceEqual(discovered)) throw new Exception("Registration metadata differs: " + pair.Item1
                + " missing=" + string.Join(",", discovered.Except(registered)) + " extra=" + string.Join(",", registered.Except(discovered)));
        }
        var tools = (IList<McpServerTool>)facade.GetMethod("GetAllTools", All)!.Invoke(null, null)!;
        foreach (var tool in tools)
        {
            bool safe = (bool)taxonomy.GetMethod("IsSafeWithoutTia")!.Invoke(null, new object[] { tool.ProtocolTool.Name })!;
            if (!Native(tool.ProtocolTool.Name) && !safe) throw new Exception("Unreviewed local tool: " + tool.ProtocolTool.Name);
        }
        var occupied = Signal(); var releaseSession = Signal();
        var occupying = Wrap(new FakeTool("GetSessionState", async token => { occupied.SetResult(true); await releaseSession.Task; }));
        var sessionCall = occupying.InvokeAsync(Request("GetSessionState")).AsTask();
        await Bound(occupied.Task);
        var batchRequest = Request("RunReadOnlyToolBatch");
        batchRequest.Params = new CallToolRequestParams { Name = "RunReadOnlyToolBatch", Arguments = new Dictionary<string, JsonElement> {
            ["operations"] = JsonSerializer.SerializeToElement(new[] { new { name = "GetToolUsage", arguments = new { toolName = "GetSessionState" } } }) } };
        var batch = Wrap(tools.Single(tool => tool.ProtocolTool.Name == "RunReadOnlyToolBatch"));
        var batchCall = batch.InvokeAsync(batchRequest).AsTask();
        try { await Bound(batchCall, 1000); if (batchCall.Result.IsError == true) throw new Exception("Local batch failed while the native lane was occupied."); }
        finally { releaseSession.SetResult(true); await Bound(sessionCall); }
        var lanes = engine.GetType("TiaMcpServer.Isolation.ToolDispatchLanes", true)!;
        Task<IDisposable?> Acquire(object session, CancellationToken token = default) => (Task<IDisposable?>)lanes.GetMethod("AcquireForSession", All)!
            .Invoke(null, new object[] { "GetSessionState", session, token })!;
        var firstSession = new object();
        using (await Acquire(firstSession))
        {
            using (await Acquire(new object())) { }
            using var cancel = new CancellationTokenSource();
            var queued = Acquire(firstSession, cancel.Token); cancel.Cancel();
            try { await queued; throw new Exception("Queued cancellation acquired a session lane."); }
            catch (OperationCanceledException) { }
        }
        using (await Acquire(firstSession)) { }
        using (var sessionLane = await Acquire(firstSession))
        {
            lanes.GetMethod("Activate", All)!.Invoke(null, new object?[] { sessionLane });
            using (var localLane = await (Task<IDisposable?>)lanes.GetMethod("AcquireForSession", All)!
                .Invoke(null, new object[] { "GetToolUsage", firstSession, CancellationToken.None })!)
            {
                lanes.GetMethod("Activate", All)!.Invoke(null, new object?[] { localLane });
                if (await Acquire(firstSession) != null) throw new Exception("Batch identity callback reacquired its held native lane.");
            }
        }

        int localActive = 0, localMax = 0;
        var eight = Signal(); var releaseLocal = Signal();
        var local = Wrap(new FakeTool("BuildStructuredText", async token => {
            int current = Interlocked.Increment(ref localActive); localMax = Math.Max(localMax, current);
            if (current == 8) eight.TrySetResult(true);
            await releaseLocal.Task; Interlocked.Decrement(ref localActive);
        }));
        var locals = Enumerable.Range(0, 12).Select(_ => local.InvokeAsync(Request("BuildStructuredText")).AsTask()).ToArray();
        await Bound(eight.Task); if (localActive != 8) throw new Exception("Local lane is not bounded at eight.");
        releaseLocal.SetResult(true); await Bound(Task.WhenAll(locals));
        if (localMax != 8) throw new Exception("Local parallelism limit exceeded.");

        var wait = facade.GetProperty("ApprovalWaitOverrideForTests", All)!;
        var parameters = wait.PropertyType.GetGenericArguments().Take(3).Select((type, i) => Expression.Parameter(type, "p" + i)).ToArray();
        var outcome = wait.PropertyType.GetGenericArguments()[3].GetGenericArguments()[0];
        var helper = typeof(ConcurrencyPerformanceChecks).GetMethod("ApprovalWait", All)!.MakeGenericMethod(outcome);
        var callback = Expression.Lambda(wait.PropertyType, Expression.Call(helper, Expression.Convert(parameters[0], typeof(object)),
            Expression.Convert(parameters[1], typeof(object)), parameters[2]), parameters).Compile();
        approvalEntered = Signal(); approvalGranted = Signal();
        wait.SetValue(null, callback);
        try
        {
            var dispatched = Signal();
            var write = Wrap(new FakeTool("SaveProject", token => { dispatched.TrySetResult(true); return Task.CompletedTask; }));
            var writing = write.InvokeAsync(Request("SaveProject")).AsTask();
            await Bound(approvalEntered.Task);
            var competingEntered = Signal(); var competingRelease = Signal();
            var competing = Wrap(new FakeTool("GetSessionState", async token => { competingEntered.SetResult(true); await competingRelease.Task; }));
            var reading = competing.InvokeAsync(Request("GetSessionState")).AsTask();
            await Bound(competingEntered.Task); // Approval wait cannot hold this lane.
            approvalGranted.SetResult(true);
            await Task.Delay(50);
            if (dispatched.Task.IsCompleted) throw new Exception("Approved write bypassed the held session lane.");
            competingRelease.SetResult(true); await Bound(Task.WhenAll(reading, writing));
            if (!dispatched.Task.IsCompleted) throw new Exception("Approved write did not dispatch.");
        }
        finally { wait.SetValue(null, null); }
        approvalEntered = Signal(); approvalGranted = Signal(); wait.SetValue(null, callback);
        try
        {
            bool bridgeIssued = false;
            var bridge = Wrap(new FakeTool("CallTool", token => Task.Run(() =>
                facade.GetMethod("ApprovedBridgeCall", All)!.Invoke(null, new object?[] { "SaveProject", "{}",
                    (Func<CallToolResult>)(() => { bridgeIssued = true; return new CallToolResult { Content = Array.Empty<ContentBlock>() }; }), null }))));
            var writing = bridge.InvokeAsync(Request("CallTool")).AsTask();
            await Bound(approvalEntered.Task);
            await Bound(Wrap(new FakeTool("GetSessionState", _ => Task.CompletedTask)).InvokeAsync(Request("GetSessionState")).AsTask());
            approvalGranted.SetResult(true); await Bound(writing);
            if (!bridgeIssued) throw new Exception("Bridge target was not dispatched after approval.");
        }
        finally { wait.SetValue(null, null); }
        var binding = engine.GetType("TiaMcpServer.ModelContextProtocol.InvocationJournal", true)!.GetField("BindingSnapshot", All)!;
        var previousBinding = binding.GetValue(null);
        int epoch = 1;
        binding.SetValue(null, (Func<JsonObject?>)(() => new JsonObject { ["epoch"] = epoch }));
        approvalEntered = Signal(); approvalGranted = Signal(); wait.SetValue(null, callback);
        try
        {
            bool issued = false;
            var write = Wrap(new FakeTool("SaveProject", token => { issued = true; return Task.CompletedTask; }));
            var writing = write.InvokeAsync(Request("SaveProject")).AsTask();
            await Bound(approvalEntered.Task);
            epoch = 2; approvalGranted.SetResult(true);
            await Bound(writing);
            if (issued || writing.Result.StructuredContent?["error"]?["code"]?.GetValue<string>() != "CONFIRMATION_REQUIRED")
                throw new Exception("Changed approved binding dispatched a write.");
        }
        finally { wait.SetValue(null, null); binding.SetValue(null, previousBinding); }
        await ApprovalPrecheckChecks(engine, Wrap, callback, wait);
        await AuditChecks(engine);
        Console.WriteLine("PASS classification of " + tools.Count + " tools, registration metadata, independent sessions, queued cancellation, local bound and approval-before-lane");
    }

    private static async Task AuditChecks(Assembly engine)
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY") ?? Path.GetTempPath(), "audit-concurrency-" + Guid.NewGuid().ToString("N"));
        var type = Program.FindServerType(engine, "TiaOpenness.Shared.AuditLog");
        object New(long limit = 1048576) => Activator.CreateInstance(type, All, null, new object[] { root, limit }, null)!;
        void Append(object log) => type.GetMethod("Append", All)!.Invoke(log, new object?[] { "start", "fixture", "engine", "21", "SaveProject", null, null, null });
        var logs = new[] { New(1024), New(1024) };
        await Bound(Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => { for (int j = 0; j < 20; j++) Append(logs[i % 2]); }))));
        var verify = type.GetMethod("Verify", All)!.Invoke(logs[0], null)!;
        if (!(bool)verify.GetType().GetProperty("Passed")!.GetValue(verify)! || (int)verify.GetType().GetProperty("Count")!.GetValue(verify)! != 80)
            throw new Exception("Concurrent rotated audit chain failed.");
        string tail = Directory.GetFiles(root, "audit-*.jsonl").OrderBy(x => x).Last();
        byte[] bytes = File.ReadAllBytes(tail); bytes[bytes.Length - 1] = (byte)' ';
        File.WriteAllBytes(tail, bytes);
        try { Append(logs[0]); throw new Exception("Interrupted audit tail was accepted from cache."); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
        bytes[bytes.Length - 1] = (byte)'\n'; File.WriteAllBytes(tail, bytes);
        Append(logs[1]);
        verify = type.GetMethod("Verify", All)!.Invoke(logs[0], null)!;
        if (!(bool)verify.GetType().GetProperty("Passed")!.GetValue(verify)!) throw new Exception("Audit repair did not invalidate the cache.");
        var fileLock = Program.FindServerType(engine, "TiaOpenness.Shared.JournalFileLock");
        FileStream Lock(bool read) => (FileStream)fileLock.GetMethod("Acquire", All)!.Invoke(null, new object[] { Path.Combine(root, "test.lock"), read })!;
        var held = Lock(true);
        await Bound(Task.Run(() => { using var shared = Lock(true); }));
        var writer = Task.Run(() => { using var exclusive = Lock(false); });
        await Task.Delay(50); if (writer.IsCompleted) throw new Exception("Audit writer bypassed the shared reader lock.");
        held.Dispose(); await Bound(writer);
        Console.WriteLine("PASS concurrent audit append, rotation, interrupted-tail/cache recovery and shared/exclusive native file locks");
    }

    internal static async Task Host(Assembly engine, string transport, int port)
    {
        engine.GetType("TiaMcpServer.Runtime.OpennessReadiness", true)!.GetMethod("MarkUnavailable", All)!
            .Invoke(null, new object?[] { "Offline concurrency fixture", "No native dispatch", "No native dispatch", null });
        var facade = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var roster = (IList<McpServerTool>)facade.GetMethod("GetAllTools", All)!.Invoke(null, null)!;
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        var selected = new List<McpServerTool> { roster.Single(t => t.ProtocolTool.Name == "GetToolUsage"),
            new FakeTool("GetSessionState", token => Task.Delay(60000, token)) };
        foreach (var tool in (IList<McpServerTool>)facade.GetMethod("WrapWithSerializedCalls", All)!.Invoke(null, new object[] { selected })!) collection.Add(tool);
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "P6-58 offline fixture", Version = "1" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ToolCollection = collection } } };
        if (transport == "stdio")
        {
            await using var server = McpServerFactory.Create(new StdioServerTransport("P6-58 offline fixture"), options);
            await server.RunAsync();
        }
        else
        {
            var streamType = engine.GetType("TiaMcpServer.McpBlockingStream", true)!;
            using var input = (Stream)Activator.CreateInstance(streamType, true)!;
            using var output = (Stream)Activator.CreateInstance(streamType, true)!;
            await using var server = McpServerFactory.Create(new StreamServerTransport(input, output, "P6-58 offline fixture"), options);
            var cli = Program.FindServerType(engine, "TiaMcpServer.CliOptions");
            var settings = Activator.CreateInstance(cli)!;
            cli.GetProperty("HttpPrefix")!.SetValue(settings, "http://127.0.0.1:" + port + "/");
            cli.GetProperty("HttpApiKey")!.SetValue(settings, "p6-58-test-key");
            var http = (Task)engine.GetType("TiaMcpServer.HttpMcpServer", true)!.GetMethod("Run", All)!
                .Invoke(null, new object?[] { settings, input, output, (Action<string>)(_ => { }), CancellationToken.None })!;
            await Task.WhenAll(server.RunAsync(), http);
        }
    }

    internal static async Task HttpPipeline(Assembly engine, string outputPath, bool baseline)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "http-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", root);
        engine.GetType("TiaMcpServer.Runtime.OpennessReadiness", true)!.GetMethod("MarkUnavailable", All)!
            .Invoke(null, new object?[] { "Offline HTTP concurrency fixture", "No native dispatch", "No native dispatch", null });
        var streamType = engine.GetType("TiaMcpServer.McpBlockingStream", true)!;
        using var input = (Stream)Activator.CreateInstance(streamType, true)!;
        using var output = (Stream)Activator.CreateInstance(streamType, true)!;
        var clock = Stopwatch.StartNew();
        var host = engine.GetType("TiaMcpServer.Program", true)!.GetMethod("BuildHttpMcpHost", All)!
            .Invoke(null, new object?[] { null, input, output })!;
        using var hostLifetime = (IDisposable)host;
        var services = (IServiceProvider)host.GetType().GetProperty("Services")!.GetValue(host)!;
        var optionsType = Assembly.Load("Microsoft.Extensions.Options").GetType("Microsoft.Extensions.Options.IOptions`1", true)!
            .MakeGenericType(typeof(McpServerOptions));
        var configured = services.GetService(optionsType)!;
        var options = (McpServerOptions)optionsType.GetProperty("Value")!.GetValue(configured)!;
        var tools = options.Capabilities!.Tools!.ToolCollection!;
        var facade = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        McpServerTool Wrap(McpServerTool tool) => ((IList<McpServerTool>)facade.GetMethod("WrapWithSerializedCalls", All)!
            .Invoke(null, new object[] { new List<McpServerTool> { tool } })!)[0];
        int active = 0, maximum = 0;
        bool measuring = true;
        var entered = Signal();
        var native = Wrap(new FakeTool("GetSessionState", async token => {
            int current = Interlocked.Increment(ref active); maximum = Math.Max(maximum, current);
            try { entered.TrySetResult(true); await Task.Delay(measuring ? 60000 : 30, token); }
            finally { Interlocked.Decrement(ref active); }
        }));
        tools.Remove(tools.Single(t => t.ProtocolTool.Name == "GetSessionState")); tools.Add(native);
        var routerType = engine.GetType("TiaMcpServer.McpHttpResponseRouter", true)!;
        var router = baseline
            ? Activator.CreateInstance(routerType, All, null, new object[] { input, output, true }, null)!
            : engine.GetType("TiaMcpServer.HttpMcpServer", true)!.GetMethod("CreateRouter", All)!
                .Invoke(null, new object[] { input, output })!;
        using var routerLifetime = (IDisposable)router;
        var send = routerType.GetMethod("SendAsync", All)!;
        async Task<JsonObject?> Send(string method, object? parameters, int? id = 1)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (id.HasValue) body["id"] = id.Value;
            if (parameters != null) body["params"] = JsonSerializer.SerializeToNode(parameters);
            var reply = await (Task<string?>)send.Invoke(router, new object[] { body.ToJsonString(), TimeSpan.FromSeconds(90) })!;
            if (!id.HasValue) { if (reply != null) throw new Exception("HTTP notification returned a reply."); return null; }
            var result = JsonNode.Parse(reply!)!.AsObject();
            if ((int?)result["id"] != id || result["error"] != null) throw new Exception("HTTP response ID or envelope mismatch: " + result);
            return result;
        }
        var start = host.GetType().GetMethod("StartAsync")!;
        var stop = host.GetType().GetMethod("StopAsync")!;
        try
        {
            await (Task)start.Invoke(host, new object[] { CancellationToken.None })!;
            // The listener's four session headers share this real SDK host. Reused
            // client IDs must remain separate after HTTP wire-ID correlation.
            for (int session = 0; session < 4; session++)
            {
                await Send("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "P6-58 HTTP pipeline", version = "2" } });
                await Send("notifications/initialized", null, null);
            }
            double startupMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); await Send("tools/list", new { }); double toolsListMs = clock.Elapsed.TotalMilliseconds;
            var local = new { name = "GetToolUsage", arguments = new { toolName = "GetSessionState" } };
            await Send("tools/call", local);
            var held = Send("tools/call", new { name = "GetSessionState", arguments = new { } });
            await Bound(entered.Task);
            clock.Restart();
            var clients = Enumerable.Range(0, 8).Select(async client => {
                var values = new List<double>();
                for (int request = 0; request < 5; request++)
                {
                    var elapsed = Stopwatch.StartNew();
                    var reply = await Send("tools/call", local, client / 4 * 100 + request + 1);
                    if ((bool?)reply!["result"]?["isError"] == true) throw new Exception("HTTP local tool failed.");
                    values.Add(elapsed.Elapsed.TotalMilliseconds);
                }
                return values;
            }).ToArray();
            var latencies = (await Task.WhenAll(clients)).SelectMany(values => values).OrderBy(value => value).ToArray();
            double callsPerSecond = latencies.Length / clock.Elapsed.TotalSeconds;
            await held;
            measuring = false;
            await Task.WhenAll(Enumerable.Range(0, 12).Select(id => Send("tools/call", new { name = "GetSessionState", arguments = new { } }, id)));
            if (maximum != 1) throw new Exception("HTTP native lane overlapped.");
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { baseline, startupMs, toolsListMs,
                local = new { p50Ms = latencies[19], p95Ms = latencies[37], maxMs = latencies[39], callsPerSecond },
                sessions = 4, concurrentClients = 8, fakeSeconds = 60, listener = false,
                assertions = new { exclusive = maximum == 1, nativeCalls = 13, responseIds = true } }, new JsonSerializerOptions { WriteIndented = true }));
            if (!baseline && latencies[37] >= 100) throw new Exception("HTTP local p95 exceeded 100 ms: " + latencies[37]);
            Console.WriteLine("PASS real HTTP router, host/SDK dispatch, response IDs and 60-second lane: " + outputPath);
        }
        finally
        {
            routerLifetime.Dispose();
            await (Task)stop.Invoke(host, new object[] { CancellationToken.None })!;
            await (Task)routerType.GetProperty("Completion", All)!.GetValue(router)!;
        }
    }

    internal static async Task Run(Assembly engine, string output, bool baseline)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", root);
        engine.GetType("TiaMcpServer.Runtime.OpennessReadiness", true)!.GetMethod("MarkUnavailable", All)!
            .Invoke(null, new object?[] { "Offline concurrency fixture", "No native dispatch", "No native dispatch", null });
        var facade = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        McpServerTool Wrap(McpServerTool tool) => ((IList<McpServerTool>)facade.GetMethod("WrapWithSerializedCalls", All)!
            .Invoke(null, new object[] { new List<McpServerTool> { tool } })!)[0];
        var clock = Stopwatch.StartNew();
        var tools = (IList<McpServerTool>)facade.GetMethod("GetAllTools", All)!.Invoke(null, null)!;
        double catalogMs = clock.Elapsed.TotalMilliseconds;
        var local = Wrap(tools.Single(t => t.ProtocolTool.Name == "GetToolUsage"));
        await local.InvokeAsync(Request("GetToolUsage"));
        var entered = Signal();
        var longCall = Wrap(new FakeTool("GetSessionState", async token => { entered.SetResult(true); await Task.Delay(60000, token); }));
        var held = longCall.InvokeAsync(Request("GetSessionState")).AsTask();
        await Bound(entered.Task);
        clock.Restart();
        var latencies = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ => {
            var elapsed = Stopwatch.StartNew();
            var result = await local.InvokeAsync(Request("GetToolUsage"));
            if (result.IsError == true) throw new Exception("Local usage failed.");
            return elapsed.Elapsed.TotalMilliseconds;
        }));
        double throughput = 40 / clock.Elapsed.TotalSeconds;
        await held;
        Array.Sort(latencies);
        if (!baseline && latencies[37] >= 100) throw new Exception("Local p95 exceeded 100 ms: " + latencies[37]);

        int active = 0, maximum = 0;
        var native = Wrap(new FakeTool("GetSessionState", async token => {
            int current = Interlocked.Increment(ref active); maximum = Math.Max(maximum, current);
            try { await Task.Delay(30, token); } finally { Interlocked.Decrement(ref active); }
        }));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => native.InvokeAsync(Request("GetSessionState")).AsTask()));
        if (maximum != 1) throw new Exception("Openness calls overlapped.");
        var cancellationEntered = Signal();
        var cancelled = Wrap(new FakeTool("GetSessionState", async token => { cancellationEntered.SetResult(true); await Task.Delay(60000, token); }));
        using (var token = new CancellationTokenSource())
        {
            var call = cancelled.InvokeAsync(Request("GetSessionState"), token.Token).AsTask();
            await Bound(cancellationEntered.Task); token.Cancel(); await Bound(call);
            await Bound(native.InvokeAsync(Request("GetSessionState")).AsTask());
        }

        var auditType = Program.FindServerType(engine, "TiaOpenness.Shared.AuditLog");
        object audit = Activator.CreateInstance(auditType, All, null, new object[] { Path.Combine(root, "audit-benchmark"), 10L * 1024 * 1024 }, null)!;
        var append = auditType.GetMethod("Append", All)!;
        clock.Restart();
        for (int i = 0; i < 200; i++) append.Invoke(audit, new object?[] { "start", "benchmark", "engine", "21", "test", null, null, null });
        double auditMs = clock.Elapsed.TotalMilliseconds / 200;
        var verification = auditType.GetMethod("Verify", All)!.Invoke(audit, null)!;
        if (!(bool)verification.GetType().GetProperty("Passed")!.GetValue(verification)!) throw new Exception("Audit chain failed.");
        var journal = engine.GetType("TiaMcpServer.ModelContextProtocol.InvocationJournal", true)!;
        var write = journal.GetMethod("Write", All)!;
        clock.Restart();
        for (int i = 0; i < 200; i++) write.Invoke(null, new object?[] { "benchmark", "native:test", "BEFORE", null, null, null });
        double journalMs = clock.Elapsed.TotalMilliseconds / 200;
        File.WriteAllText(output, JsonSerializer.Serialize(new { baseline, catalogMs, local = new {
            p50Ms = latencies[19], p95Ms = latencies[37], maxMs = latencies[39], callsPerSecond = throughput }, auditMs, journalMs,
            assertions = new { exclusive = maximum == 1, cancellation = true, auditChain = true } }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS dispatch benchmark, exclusivity, cancellation and audit chain: " + output);
    }
}
