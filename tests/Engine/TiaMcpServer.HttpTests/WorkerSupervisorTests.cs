using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static string WorkerQuote(string value)
    {
        var result=new System.Text.StringBuilder("\""); int slashes=0;
        foreach(char ch in value) { if(ch=='\\'){slashes++;continue;} result.Append('\\',ch=='"'?slashes*2+1:slashes).Append(ch); slashes=0; }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    private static ProcessStartInfo TestWorkerStart(string exe, string api, int major, object options)
    {
        string? fault=Environment.GetEnvironmentVariable("TIA_MCP_TEST_WORKER_FAULT");
        if(!string.IsNullOrEmpty(fault)) {
            var mcp=EngineSurface.For(Server);
            bool lite=(bool)mcp.Invoke(mcp.ToolMethod("IsLiteProfile",All),null)!;
            var roster=(IEnumerable)mcp.Invoke(mcp.ToolMethod(lite?"GetLiteTools":"GetAllTools",All),null)!;
            var names=roster.Cast<object>().Select(t=>{var p=t.GetType().GetProperty("ProtocolTool")!.GetValue(t)!;return (string)p.GetType().GetProperty("Name")!.GetValue(p)!;}).ToArray();
            var isolated=Server.GetType("TiaMcpServer.Isolation.IsolatedWorkerHost",true)!;
            var hash=(string)isolated.GetMethod("Hash",All)!.Invoke(null,new object[]{exe})!;
            return new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,string.Join(" ",new[]{"worker-fixture",fault,
                Path.Combine(Path.GetTempPath(),"tia-worker-fault-"+Guid.NewGuid().ToString("N")+".log"),major.ToString(),hash,
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Json.Serialize(names)))}.Select(WorkerQuote)));
        }
        using var parent = Process.GetCurrentProcess();
        var values = new[] { exe, "isolated-worker-host", api, "--tia-major-version", major.ToString(), "--transport", "stdio", "--logging", "0",
            "--worker-parent-pid", parent.Id.ToString(), "--worker-parent-start", parent.StartTime.ToUniversalTime().Ticks.ToString(),
            "--profile", (string?)options.GetType().GetProperty("Profile")!.GetValue(options) ?? Environment.GetEnvironmentVariable("TIA_MCP_PROFILE") ?? "lite" };
        return new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, string.Join(" ", values.Select(WorkerQuote)));
    }

    private static int RunWorkerFixture(string[] args)
    {
        Console.InputEncoding = Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string mode = args[1];
        int major=args.Length>3?int.Parse(args[3]):21;
        string hash=args.Length>4?args[4]:"test-hash";
        var names=args.Length>5?Json.Deserialize<string[]>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[5]))):new[]{"GetSessionState"};
        if (mode == "hello-hang") { Thread.Sleep(30000); return 0; }
        if (mode == "hello-crash") return 17;
        Console.WriteLine(Json.Serialize(new { kind = "tia-openness-worker", protocol = mode == "bad-protocol" ? 2 : 1,
            engineMajor = mode == "wrong-major" ? 20 : major, engineSha256 = mode == "wrong-hash" ? "bad" : hash, pid = Process.GetCurrentProcess().Id }));
        string? line;
        int calls = 0;
        var inputFrames = new List<string>();
        using var rawInput = mode == "stdin-utf8" ? Console.OpenStandardInput() : null;
        while ((line = rawInput == null ? Console.ReadLine() : ReadWorkerInput(rawInput, inputFrames)) != null)
        {
            var request = Parse(line);
            string method = (string)request["method"];
            if (!request.TryGetValue("id", out var id)) continue;
            if (method == "initialize")
            {
                Console.WriteLine(Reply(id, new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } } }));
                continue;
            }
            if (method == "tools/list")
            {
                Console.WriteLine(Reply(id, new { tools = (mode=="wrong-tools"?new[]{"Wrong"}:names).Select(n=>new {name=n}).ToArray() }));
                continue;
            }
            calls++;
            if (args.Length > 2) File.AppendAllText(args[2], calls + "\n");
            if (mode == "hang") { Thread.Sleep(30000); return 0; }
            if (mode == "crash") Environment.Exit(23);
            if (mode == "fault-after-call") { File.AppendAllText(args[2] + ".completed", "simulated operation completed\n"); return 23; }
            if (mode == "partial") { Console.Write("{\"jsonrpc\":"); Console.Out.Flush(); return 0; }
            if (mode == "malformed") { Console.WriteLine("garbage"); continue; }
            if (mode == "missing-content") { Console.WriteLine(Reply(id, new { isError = false })); continue; }
            if (mode == "invalid-content") { Console.WriteLine(Reply(id, new { content = new[] { new { type = "text", text = 13 } } })); continue; }
            if (mode == "oversize") { Console.WriteLine(new string('x', 16 * 1024 * 1024 + 1)); continue; }
            if (mode == "slow") Thread.Sleep(350);
            if (mode == "logical-error") { Console.WriteLine(Json.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Invalid test argument" } })); continue; }
            if (mode == "internal-error") { Console.WriteLine(Json.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32603, message = "Unconfirmed test call" } })); continue; }
            if (mode == "progress") Console.WriteLine(Json.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = "test", progress = 1 } }));
            if (mode == "old-envelope") { Console.WriteLine(Reply(id, new { content = new[] { new { type = "text", text = "{\"message\":\"old\",\"meta\":{\"success\":true}}" } }, isError = false })); continue; }
            var parameters = (Dictionary<string, object>)request["params"];
            string tool = (string)parameters["name"];
            if (tool == "CallTool") tool = (string)((Dictionary<string, object>)parameters["arguments"])["name"];
            bool ok = mode != "binding-fails";
            var payload = new {
                schemaVersion = 4, ok,
                data = mode == "stdin-utf8" ? (object)new { frames = inputFrames } : new { value = "中文🟦", call = calls },
                error = ok ? null : new { code = "PROJECT_NOT_BOUND", message = "Fixture is not bound.", details = new { } },
                meta = new { timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), releaseKey = major.ToString(), tool,
                    requestId = "fixture-request", outcome = ok ? "succeeded" : "rejected-before-operation", execution = ok ? "read-only" : "not-started",
                    requiresSessionReset = false, behaviorPolicy = "not-applicable", completeness = ok ? "complete" : "none", paging = (object?)null, warnings = new object[0] }
            };
            Console.WriteLine(Reply(mode == "wrong-id" ? "wrong" : id, new {
                content = new[] { new { type = "text", text = Json.Serialize(payload) } },
                structuredContent = mode == "mismatched-envelope" ? (object)new { schemaVersion = 4 } : payload,
                isError = mode == "wrong-is-error" ? ok : !ok }));
            if (mode == "duplicate") Console.WriteLine(Reply(id, new { }));
        }
        return 0;
    }

    private static string? ReadWorkerInput(Stream input, List<string> frames)
    {
        using var bytes = new MemoryStream();
        int value;
        while ((value = input.ReadByte()) >= 0) {
            bytes.WriteByte((byte)value);
            if (value == '\n') break;
        }
        if (bytes.Length == 0) return null;
        byte[] frame = bytes.ToArray();
        frames.Add(BitConverter.ToString(frame));
        return new System.Text.UTF8Encoding(false, true).GetString(frame);
    }

    private static async Task ChildStdinTests()
    {
        int before = Passed, failures = 0;
        var previous = Console.InputEncoding;
        async Task Run(string label, Func<Task> test)
        {
            try { await Test(label, async () => {
                var encoding = Console.InputEncoding;
                await test();
                Check(Console.InputEncoding.CodePage == encoding.CodePage &&
                    Console.InputEncoding.GetPreamble().SequenceEqual(encoding.GetPreamble()), "Console input encoding was not restored");
                if (encoding.GetPreamble().Length == 0)
                    Check(ReferenceEquals(Console.InputEncoding, encoding), "BOM-free console encoding was unnecessarily replaced");
            }); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + label + ": " + ex); }
        }
        try {
            // Deliberately undo the host workaround: both producers must own their encoding.
            foreach (var encoding in new[] { System.Text.Encoding.UTF8, System.Text.Encoding.GetEncoding(936), new System.Text.UTF8Encoding(false) }) {
                Console.InputEncoding = encoding;
                await Run("worker stdin is BOM-free UTF-8 under CP" + encoding.CodePage, async () => {
                    using var f = new WorkerFixture("stdin-utf8");
                    for (int call = 0; call < 2; call++) {
                        var response = await f.Call(arguments: "{\"value\":\"中文🟦\"}");
                        var result = (Dictionary<string, object>)response["result"];
                        string text = (string)((Dictionary<string, object>)((IList)result["content"])[0]!)["text"];
                        var frames = ((IList)((Dictionary<string, object>)Parse(text)["data"])["frames"]).Cast<string>().ToArray();
                        Check(frames.Length == 4 + call, "Worker framing/order changed");
                        var methods = new[] { "initialize", "notifications/initialized", "tools/list", "tools/call", "tools/call" };
                        for (int i = 0; i < frames.Length; i++) {
                            Check(frames[i].StartsWith("7B-") && !frames[i].Contains("EF-BB-BF"), "Worker stdin contains a BOM");
                            Check(frames[i].EndsWith("-0A") && !frames[i].EndsWith("-0D-0A"), "Worker frame is not LF terminated");
                            byte[] bytes = frames[i].Split('-').Select(b => Convert.ToByte(b, 16)).ToArray();
                            var frame = Parse(new System.Text.UTF8Encoding(false, true).GetString(bytes));
                            Check((string)frame["method"] == methods[i], "Worker message order changed");
                            if (i >= 3) {
                                var parameters = (Dictionary<string, object>)frame["params"];
                                Check((string)((Dictionary<string, object>)parameters["arguments"])["value"] == "中文🟦", "Worker input Unicode corrupted");
                            }
                        }
                    }
                });
                foreach (string? input in new[] { "{\"value\":\"中文🟦\"}\nsecond\n", "", null }) {
                    await Run("LocalProcess stdin/EOF under CP" + encoding.CodePage + " (" + (input == null ? "null" : input.Length.ToString()) + ")", async () => {
                        string hex = await LocalProcessInput(input);
                        Check(hex == BitConverter.ToString(new System.Text.UTF8Encoding(false).GetBytes(input ?? "")), "Child bytes differ: " + hex);
                    });
                }
                await Run("failed child start restores CP" + encoding.CodePage + " and preserves the start error", async () => {
                    string missing = Path.Combine(Environment.CurrentDirectory, "missing-child-" + Guid.NewGuid().ToString("N") + ".exe");
                    await Fault(LocalProcessInput("", missing), typeof(System.ComponentModel.Win32Exception));
                });
                await Run("concurrent worker and LocalProcess starts preserve CP" + encoding.CodePage, async () => {
                    await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () => {
                        if (i % 2 == 0) {
                            using var f = new WorkerFixture("stdin-utf8", 5);
                            Check((await f.Call()).ContainsKey("result"), "Concurrent worker handshake failed");
                        } else {
                            string hex = await LocalProcessInput("中文🟦\n");
                            Check(hex == "E4-B8-AD-E6-96-87-F0-9F-9F-A6-0A", "Concurrent child bytes differ: " + hex);
                        }
                    })));
                });
            }
        }
        finally { Console.InputEncoding = previous; }
        Console.WriteLine("COMPLETE: " + (Passed - before) + " child stdin checks passed; " + failures + " failed");
        Check(failures == 0, "Child stdin regression failed");
    }

    private static async Task<string> LocalProcessInput(string? input, string? executable = null)
    {
        var type = FindServerType(Server, "TiaOpenness.Shared.LocalProcess");
        var task = (Task)type.GetMethod("Run")!.Invoke(null, new object?[] {
            executable ?? Assembly.GetExecutingAssembly().Location, new[] { "stdin-hex-fixture" },
            Environment.CurrentDirectory, input, 5, 1024 * 1024, null })!;
        await Bounded(AsResult(task), 10000);
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Check((bool)result.GetType().GetProperty("Success")!.GetValue(result)!, "Hex child failed or did not receive EOF");
        return ((string)result.GetType().GetProperty("Stdout")!.GetValue(result)!).Trim();
    }

    private sealed class WorkerFixture : IDisposable
    {
        internal readonly object Supervisor;
        private readonly Type type;
        private readonly Type jsonNode;
        internal readonly string Log = Path.Combine(Path.GetTempPath(), "tia-worker-dispatch-" + Guid.NewGuid().ToString("N") + ".log");
        internal int Starts;
        internal WorkerFixture(string mode = "ok", double timeout = 3)
        {
            type = Server.GetType("TiaMcpServer.Isolation.OpennessWorkerSupervisor", true)!;
            jsonNode = Assembly.Load("System.Text.Json").GetType("System.Text.Json.Nodes.JsonNode", true)!;
            Func<ProcessStartInfo> start = () => { Starts++; return new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                string.Join(" ", new[] { "worker-fixture", mode, Log }.Select(WorkerQuote))); };
            Supervisor = Activator.CreateInstance(type, All, null, new object[] { start, 21, "test-hash", new[] { "GetSessionState" }, TimeSpan.FromSeconds(timeout) }, null)!;
        }
        internal Dictionary<string, object> State => Parse(type.GetMethod("Snapshot", All)!.Invoke(Supervisor, null)!.ToString()!);
        internal Dictionary<string, object> Restart(bool confirm) => Parse(type.GetMethod("Restart", All)!.Invoke(Supervisor, new object[] { confirm })!.ToString()!);
        internal async Task<Dictionary<string, object>> Call(string name = "GetSessionState", string arguments = "{}", CancellationToken cancellation = default)
        {
            var parse = jsonNode.GetMethods().Single(m => m.Name == "Parse" && m.GetParameters()[0].ParameterType == typeof(string));
            object node = parse.Invoke(null, new object?[] { "{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}", null, null })!;
            var task = (Task)type.GetMethod("CallAsync", All)!.Invoke(Supervisor, new object?[] { node, null, cancellation })!;
            await Bounded(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), 12000);
            return Parse(task.GetType().GetProperty("Result")!.GetValue(task)!.ToString()!);
        }
        public void Dispose() { ((IDisposable)Supervisor).Dispose(); }
        internal int Dispatches => File.Exists(Log) ? File.ReadAllLines(Log).Length : 0;
    }

    private static async Task WorkerFailure(Task task, bool unknown, WorkerFixture? fixture = null, string? code = null)
    {
        try { await task; }
        catch (Exception ex)
        {
            Check(ex.GetType().Name == "WorkerCallException", "Wrong failure type: " + ex.GetType().Name);
            Check((bool)ex.GetType().GetProperty("OutcomeUnknown", All)!.GetValue(ex)! == unknown, "Wrong native outcome certainty");
            var host = Server.GetType("TiaMcpServer.Isolation.IsolatedWorkerHost", true)!;
            object response = host.GetMethod("Error", All)!.Invoke(null, new object?[] { "GetSessionState", ex, fixture?.Supervisor })!;
            var envelope = CheckWorkerEnvelope(response, unknown);
            if (code != null) Check((string)((Dictionary<string, object>)envelope["error"])["code"] == code, "Wrong section-3 guard code");
            if (fixture != null) {
                var evidence = (Dictionary<string, object>)((Dictionary<string, object>)envelope["data"])["evidence"];
                Check((string)((Dictionary<string, object>)evidence["worker"])["state"] == (string)fixture.State["state"], "Guard lost worker state evidence");
            }
            return;
        }
        throw new Exception("Expected worker rejection");
    }

    private static Dictionary<string, object> CheckWorkerEnvelope(object response, bool unknown)
    {
        var blocks = ((IEnumerable)response.GetType().GetProperty("Content")!.GetValue(response)!).Cast<object>().ToArray();
        Check(blocks.Length == 1, "Guard returned more than one content block");
        string text = (string)blocks[0].GetType().GetProperty("Text")!.GetValue(blocks[0])!;
        var value = Parse(text);
        string structured = response.GetType().GetProperty("StructuredContent")!.GetValue(response)!.ToString()!;
        Check(Json.Serialize(Parse(structured)) == Json.Serialize(value), "Guard structuredContent/text mismatch");
        Check((int)value["schemaVersion"] == 4 && !(bool)value["ok"] && (bool)response.GetType().GetProperty("IsError")!.GetValue(response)!, "Guard status is not V4");
        var meta = (Dictionary<string, object>)value["meta"];
        var error = (Dictionary<string, object>)value["error"];
        Check(!value.ContainsKey("message") && !meta.ContainsKey("success"), "Guard retained a 3.x verdict");
        Check((string)meta["outcome"] == (unknown ? "unknown" : "rejected-before-operation")
            && (string)meta["execution"] == (unknown ? "unknown" : "not-started"), "Guard lost its dispatch boundary");
        Check((bool)meta["requiresSessionReset"] == (unknown || (string)error["code"] == "SESSION_RESET_REQUIRED"), "Guard lost reset requirement");
        Check(error.ContainsKey("details") && (!unknown || (string)error["code"] == "OUTCOME_UNKNOWN"), "Guard lost code-specific details");
        var data = (Dictionary<string, object>)value["data"];
        Check(!((bool)((Dictionary<string, object>)data["evidence"])["automaticReplay"]), "Guard enabled replay");
        return value;
    }

    private sealed class SdkGuardFixture : ModelContextProtocol.Server.McpServerTool
    {
        private readonly bool write;
        private readonly bool legacy;
        internal int Calls;
        internal SdkGuardFixture(bool write, bool legacy = false) { this.write = write; this.legacy = legacy; }
        public override ModelContextProtocol.Protocol.Tool ProtocolTool => new ModelContextProtocol.Protocol.Tool {
            Name = write ? "RestartOpennessWorker" : "GetSessionState" };
        public override ValueTask<ModelContextProtocol.Protocol.CallToolResult> InvokeAsync(
            ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (legacy) return new ValueTask<ModelContextProtocol.Protocol.CallToolResult>(new ModelContextProtocol.Protocol.CallToolResult {
                Content = new[] { new ModelContextProtocol.Protocol.TextContentBlock { Text = "{\"message\":\"old\",\"meta\":{\"success\":true}}" } } });
            throw new ModelContextProtocol.McpException("SECRET-fixture-argument", ModelContextProtocol.McpErrorCode.InternalError);
        }
    }

    private static async Task SerializedCallGuardTests()
    {
        int before = Passed;
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        string data = Path.Combine(Path.GetTempPath(), "tia-sdk-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "config"));
        File.WriteAllText(Path.Combine(data, "config", "approval.settings"), "enabled=false\ntimeoutSeconds=120\n");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", data);
            foreach (var scenario in new[] { "read-exception", "write-exception", "legacy-result", "cancel-before-call" })
                await Test("direct SDK dispatch guard returns V4: " + scenario, async () => {
                    var fixture = new SdkGuardFixture(scenario == "write-exception", scenario == "legacy-result");
                    var wrapper = Server.GetType("TiaMcpServer.ModelContextProtocol.SerializedCallTool", true)!;
                    var tool = (ModelContextProtocol.Server.McpServerTool)Activator.CreateInstance(wrapper, All, null, new object[] { fixture }, null)!;
                    using var cancellation = new CancellationTokenSource();
                    if (scenario == "cancel-before-call") cancellation.Cancel();
                    var result = await tool.InvokeAsync(null!, cancellation.Token);
                    var text = result.Content.Cast<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
                    var body = Parse(text);
                    Check(Json.Serialize(Parse(result.StructuredContent!.ToString())) == Json.Serialize(body)
                        && (int)body["schemaVersion"] == 4 && !(bool)body["ok"] && result.IsError == true, "SDK guard is not a matching V4 error");
                    var error = (Dictionary<string, object>)body["error"];
                    var meta = (Dictionary<string, object>)body["meta"];
                    bool unknown = scenario == "write-exception", cancelled = scenario == "cancel-before-call";
                    Check((string)error["code"] == (unknown ? "OUTCOME_UNKNOWN" : cancelled ? "CANCELLED" : "INTERNAL_ERROR")
                        && (string)meta["outcome"] == (unknown ? "unknown" : cancelled ? "rejected-before-operation" : "read-failed")
                        && (string)meta["execution"] == (unknown ? "unknown" : cancelled ? "not-started" : "read-only")
                        && (bool)meta["requiresSessionReset"] == unknown, "SDK guard lost typed outcome details: " + scenario + " " + text);
                    Check(fixture.Calls == (cancelled ? 0 : 1) && !text.Contains("SECRET") && !body.ContainsKey("message"), "SDK guard leaked arguments or dispatched cancellation");
                    if (unknown)
                    {
                        var warnings = (IList)meta["warnings"];
                        Check(warnings.Cast<Dictionary<string, object>>().Count(w => (string)w["code"] == "APPROVAL_DISABLED") == 1,
                            "SDK guard omitted or duplicated the disabled approval warning");
                    }
                });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previous);
            try { Directory.Delete(data, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine("COMPLETE: " + (Passed-before) + " direct SDK guard checks passed; no native call executed");
    }

    private sealed class GuardRequestServer : System.Runtime.Remoting.Proxies.RealProxy
    {
        internal GuardRequestServer() : base(typeof(ModelContextProtocol.Server.IMcpServer)) { }
        public override System.Runtime.Remoting.Messaging.IMessage Invoke(System.Runtime.Remoting.Messaging.IMessage message)
            => new System.Runtime.Remoting.Messaging.ReturnMessage(null, null, 0, null,
                (System.Runtime.Remoting.Messaging.IMethodCallMessage)message);
    }
    private sealed class ParentBridgeGuardFixture : ModelContextProtocol.Server.McpServerTool
    {
        private readonly WorkerFixture worker;
        internal ParentBridgeGuardFixture(WorkerFixture worker) { this.worker = worker; }
        public override ModelContextProtocol.Protocol.Tool ProtocolTool => new() { Name = "CallTool" };
        public override async ValueTask<ModelContextProtocol.Protocol.CallToolResult> InvokeAsync(
            ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            try { await worker.Call("CallTool", "{\"name\":\"CreatePlcBlockGroup\",\"arguments\":{}}", cancellationToken); }
            catch (Exception ex)
            {
                var host = Server.GetType("TiaMcpServer.Isolation.IsolatedWorkerHost", true)!;
                return (ModelContextProtocol.Protocol.CallToolResult)host.GetMethod("Error", All)!.Invoke(null,
                    new object[] { "CallTool", ex, worker.Supervisor })!;
            }
            throw new Exception("Expected isolated worker guard");
        }
    }

    private static async Task WorkerSupervisorTests()
    {
        int before = Passed;
        foreach (var mode in new[] { "hello-crash", "crash" })
            await Test("parent bridge write guard retains disabled approval warning: " + mode, async () => {
                string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
                string data = Path.Combine(Path.GetTempPath(), "tia-parent-approval-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(data, "config"));
                File.WriteAllText(Path.Combine(data, "config", "approval.settings"), "enabled=false\ntimeoutSeconds=120\n");
                try
                {
                    Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", data);
                    using var fixture = new WorkerFixture(mode);
                    var wrapper = Server.GetType("TiaMcpServer.ModelContextProtocol.AuditCallTool", true)!;
                    var tool = (ModelContextProtocol.Server.McpServerTool)Activator.CreateInstance(wrapper, All, null,
                        new object[] { new ParentBridgeGuardFixture(fixture) }, null)!;
                    var request = new ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams>(
                        (ModelContextProtocol.Server.IMcpServer)new GuardRequestServer().GetTransparentProxy()) {
                        Params = new ModelContextProtocol.Protocol.CallToolRequestParams { Name = "CallTool",
                            Arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                                "{\"name\":\"CreatePlcBlockGroup\",\"arguments\":{}}") }
                    };
                    var result = await tool.InvokeAsync(request);
                    var body = CheckWorkerEnvelope(result, mode == "crash");
                    var warnings = (IList)((Dictionary<string, object>)body["meta"])["warnings"];
                    Check(warnings.Cast<Dictionary<string, object>>().Count(w => (string)w["code"] == "APPROVAL_DISABLED") == 1,
                        "Parent CallTool guard omitted or duplicated the disabled warning");
                    Check(fixture.Dispatches == (mode == "crash" ? 1 : 0), "Guard dispatched or replayed unexpectedly");
                }
                finally { Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previous); }
            });
        await Test("production worker start preserves relative path context and omits HTTP credentials", () => {
            var optionsType = FindServerType(Server, "TiaMcpServer.CliOptions");
            var options = Activator.CreateInstance(optionsType)!;
            optionsType.GetProperty("TiaPortalLocation")!.SetValue(options, @"relative API\V21\");
            optionsType.GetProperty("HttpApiKey")!.SetValue(options, "private-host-key-marker");
            var host = Server.GetType("TiaMcpServer.Isolation.IsolatedWorkerHost", true)!;
            var start = (ProcessStartInfo)host.GetMethod("StartInfo", All)!.Invoke(null, new object[] { Server.Location, options })!;
            Check(start.WorkingDirectory == Environment.CurrentDirectory, "Worker changed relative path semantics");
            Check(start.Arguments.Contains(WorkerQuote(@"relative API\V21\")), "Relative API path was not quoted intact");
            Check(!start.Arguments.Contains("private-host-key-marker"), "HTTP secret copied into child arguments");
            return Task.CompletedTask;
        });
        await Test("worker handshake, Unicode results and one persistent child", async () => {
            using var f = new WorkerFixture();
            for (int i = 0; i < 20; i++) {
                var response=await f.Call(); Check(response.ContainsKey("result"), "Missing result");
                var result=(Dictionary<string,object>)response["result"];
                var text=(string)((Dictionary<string,object>)((IList)result["content"])[0]!)["text"];
                Check((string)((Dictionary<string, object>)Parse(text)["data"])["value"]=="中文🟦","Unicode response corrupted");
            }
            Check(f.Starts == 1 && f.Dispatches == 20 && (string)f.State["state"] == "Ready", "Worker state drift");
        });
        await Test("disconnected worker rejects before the operation with a V4 envelope", async () => {
            using var f = new WorkerFixture(); f.Dispose();
            await WorkerFailure(f.Call(), false, f, "RESOURCE_UNAVAILABLE");
            Check(f.Starts == 0 && f.Dispatches == 0, "Disconnected worker dispatched an operation");
        });
        foreach (var mode in new[] { "hello-hang", "hello-crash", "bad-protocol", "wrong-major", "wrong-hash", "wrong-tools" })
            await Test("reject startup " + mode + " without dispatch", async () => {
                using var f = new WorkerFixture(mode, .7);
                await WorkerFailure(f.Call(), false, f, mode == "hello-hang" ? "TIMEOUT" : "RESOURCE_UNAVAILABLE");
                Check((string)f.State["state"] == "Faulted" && f.Dispatches == 0, "Startup fault dispatched tool");
                await WorkerFailure(f.Call(), false, f, "SESSION_RESET_REQUIRED");
                Check(f.Starts == 1, "Fault silently restarted worker");
            });
        foreach (var mode in new[] { "hang", "crash", "fault-after-call", "partial", "malformed", "missing-content", "invalid-content", "old-envelope", "mismatched-envelope", "wrong-is-error", "internal-error", "wrong-id", "oversize" })
            await Test("fault " + mode + " invalidates channel with no replay", async () => {
                using var f = new WorkerFixture(mode, mode=="oversize"?10:1.5);
                await WorkerFailure(f.Call(), true, f, "OUTCOME_UNKNOWN");
                Check((string)f.State["state"] == "Faulted" && f.Dispatches == 1, "Fault was not latched");
                if (mode == "fault-after-call") Check(File.Exists(f.Log + ".completed"), "After-call fixture did not reach simulated completion");
                await WorkerFailure(f.Call(), false, f, "SESSION_RESET_REQUIRED");
                Check(f.Starts == 1 && f.Dispatches == 1, "Operation replayed");
            });
        await Test("duplicate response invalidates worker", async () => {
            using var f = new WorkerFixture("duplicate");
            try { await f.Call(); } catch { }
            for (int i=0;i<100 && (string)f.State["state"]!="Faulted";i++) await Task.Delay(10);
            Check((string)f.State["state"]=="Faulted", "Duplicate response was ignored");
            await WorkerFailure(f.Call(),false);
        });
        await Test("JSON-RPC logical failure keeps worker usable", async () => {
            using var f = new WorkerFixture("logical-error");
            Check((await f.Call()).ContainsKey("error"), "Logical failure hidden");
            Check((await f.Call()).ContainsKey("error") && f.Starts == 1 && (string)f.State["state"] == "Ready", "Logical error poisoned worker");
        });
        await Test("explicit reset clears memory and requires successful named binding", async () => {
            using var f = new WorkerFixture(); await f.Call();
            Check((bool)f.Restart(false)["dryRun"] && f.Starts == 1, "Preview reset mutated state");
            f.Restart(true);
            await WorkerFailure(f.Call("SaveProject"), false, f, "PROJECT_NOT_BOUND");
            await WorkerFailure(f.Call("ConnectPortal"), false);
            Check(f.Starts==1, "Blocked operation launched worker");
            await f.Call("ConnectPortal", "{\"projectName\":\"scratch\"}");
            Check(!(bool)f.State["explicitBindingRequired"], "Successful binding not recognized");
            await f.Call("SaveProject"); Check(f.Starts==2, "Fresh generation not created");
        });
        await Test("failed binding does not unlock recovered worker", async () => {
            using var f = new WorkerFixture("binding-fails"); f.Restart(true);
            await f.Call("ConnectPortal", "{\"projectName\":\"scratch\"}");
            await WorkerFailure(f.Call("SaveProject"), false);
        });
        foreach (string target in new[] { "ConnectProject", "ConnectPortal" })
            await Test("typed bridge recognizes recovered binding: " + target, async () => {
                using var f = new WorkerFixture(); f.Restart(true);
                await f.Call("CallTool", "{\"name\":\"" + target + "\",\"arguments\":{\"projectName\":\"scratch\"}}");
                Check(!(bool)f.State["explicitBindingRequired"], "Bridge target result did not establish binding");
                await f.Call("SaveProject");
                Check(f.Dispatches == 2, "Bound bridge did not admit the subsequent call");
            });
        await Test("failed typed bridge binding keeps recovery locked", async () => {
            using var f = new WorkerFixture("binding-fails"); f.Restart(true);
            await f.Call("CallTool", "{\"name\":\"ConnectPortal\",\"arguments\":{\"projectName\":\"scratch\"}}");
            await WorkerFailure(f.Call("SaveProject"), false);
            Check(f.Dispatches == 1, "Failed bridge unlocked recovery");
        });
        await Test("bridge strings and unnamed Connect do not provide binding evidence", async () => {
            using var f = new WorkerFixture(); f.Restart(true);
            foreach (string arguments in new[] { "{}", "{\"arguments\":{}}", "{\"arguments\":\"{\\\"projectName\\\":\\\"scratch\\\"}\"}",
                "{\"argumentsJson\":{\"projectName\":\"scratch\"}}" }) {
                var values = Parse(arguments); values["name"] = "ConnectPortal";
                await WorkerFailure(f.Call("CallTool", Json.Serialize(values)), false);
            }
            Check(f.Starts == 0 && f.Dispatches == 0, "Invalid bridge arguments launched a worker");
        });
        foreach (string diagnostic in new[] { "FindTools", "ListToolCategories", "GetToolUsage", "PreviewToolCall", "GetToolSchema" })
            await Test("current discovery remains diagnostic during recovery: " + diagnostic, async () => {
                using var f = new WorkerFixture(); f.Restart(true);
                await f.Call(diagnostic);
                await f.Call("CallTool", "{\"name\":\"" + diagnostic + "\",\"arguments\":{}}");
                Check((bool)f.State["explicitBindingRequired"] && f.Dispatches == 2, "Diagnostic call changed recovery binding");
                await WorkerFailure(f.Call("SaveProject"), false);
            });
        await Test("active worker refuses reset and reports state immediately", async () => {
            using var f = new WorkerFixture("slow"); var call=f.Call();
            await Task.Delay(60);
            var clock=Stopwatch.StartNew(); var state=f.State;
            Check(clock.ElapsedMilliseconds<200 && (int)state["admittedCalls"]==1,"Status blocked behind worker");
            Check(!(bool)f.Restart(true)["success"],"Active worker reset"); await call;
        });
        await Test("bounded queue serializes real child dispatch", async () => {
            using var f = new WorkerFixture("slow",8);
            var calls=Enumerable.Range(0,16).Select(_=>f.Call()).ToArray();
            await WorkerFailure(f.Call(),false);
            await Task.WhenAll(calls);
            Check(f.Dispatches==16 && (int)f.State["admittedCalls"]==0,"Queue lost or duplicated requests");
        });
        await Test("cancel queued call never reaches child", async () => {
            using var f=new WorkerFixture("slow"); var active=f.Call(); using var cancel=new CancellationTokenSource();
            var queued=f.Call(cancellation:cancel.Token); cancel.Cancel();
            await WorkerFailure(queued,false); await active; Check(f.Dispatches==1,"Queued cancellation dispatched");
        });
        await Test("cancel in flight faults child and does not claim rollback", async () => {
            using var f=new WorkerFixture("hang"); using var cancel=new CancellationTokenSource();
            var call=f.Call(cancellation:cancel.Token);
            for(int i=0;i<100 && f.Dispatches==0;i++) await Task.Delay(10);
            cancel.Cancel(); await WorkerFailure(call,true); Check((string)f.State["state"]=="Faulted","Cancellation did not invalidate child");
        });
        await Test("faulted queued callers are not dispatched into a new generation", async () => {
            using var f=new WorkerFixture("hang",.6); var first=f.Call(); await Task.Delay(80); var queued=f.Call();
            await WorkerFailure(first,true); await WorkerFailure(queued,false); Check(f.Dispatches==1,"Queued operation replayed");
        });
        Console.WriteLine("COMPLETE: " + (Passed-before) + " worker supervisor checks passed; no TIA connection attempted");
    }
}
