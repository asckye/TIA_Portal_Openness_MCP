extern alias foundationhost;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class EngineHttpSessionTests
{
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Unknown_outcome_can_disconnect_and_a_new_session_can_acquire_the_same_lease(string release)
    {
        using var host = await Fixture.Start(release);
        using var a = new Session(host.Url); await a.Initialize();
        await a.Tool("ConnectPortal", new() { ["processId"] = 321 });
        var unknown = await a.Tool("CompileDevice", new() { ["fault"] = true }, ok: false);
        Assert.Equal("Fixture native outcome unknown.", (string?)unknown["error"]?["message"]);
        Assert.NotNull(unknown["data"]?["fixturePid"]);
        string prior = (string)unknown["meta"]!["requestId"]!;
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await a.Tool("GetSessionState", ok: false))["error"]?["code"]);
        var detached = await a.Tool("CallTool", new() { ["name"] = "DisconnectPortal", ["arguments"] = new JsonObject() });
        Assert.True((bool?)detached["data"]?["workerAcknowledged"]);
        Assert.False((bool?)detached["data"]?["requiresTiaRestart"]);
        Assert.Equal(prior, (string?)detached["data"]?["priorUnknownRequestId"]);
        Assert.False((bool?)detached["data"]?["savedProject"]); Assert.False((bool?)detached["data"]?["closedProject"]);
        Assert.Equal("RELEASED\n", File.ReadAllText(Path.Combine(host.DataDirectory, "leases", "321-1.lease")));
        using var b = new Session(host.Url); await b.Initialize(); await b.Tool("ConnectPortal", new() { ["processId"] = 321 });
        Assert.Contains(Directory.EnumerateFiles(host.DataDirectory, "*.jsonl", SearchOption.AllDirectories), path => File.ReadAllText(path).Contains("RECOVERY_DISCONNECT") && File.ReadAllText(path).Contains(prior));
        await b.Tool("DisconnectPortal"); await a.End(); await b.End();
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Idle_attached_restart_detaches_before_starting_the_next_generation(string release)
    {
        using var host = await Fixture.Start(release); using var a = new Session(host.Url); await a.Initialize();
        await a.Tool("ConnectPortal", new() { ["processId"] = 322 });
        var restarted = await a.Tool("RestartOpennessWorker", new() { ["confirmRestart"] = true });
        Assert.Contains("\"requiresTiaRestart\":false", restarted["data"]!.ToJsonString());
        Assert.Equal("RELEASED\n", File.ReadAllText(Path.Combine(host.DataDirectory, "leases", "322-1.lease")));
        await a.Tool("ConnectPortal", new() { ["processId"] = 322 });
        await a.Tool("DisconnectPortal"); await a.End();
    }

    [Theory, InlineData("20", true), InlineData("21", true), InlineData("20", false), InlineData("21", false)]
    public async Task Busy_or_timed_out_disconnect_terminates_the_worker_and_keeps_the_lease_active(string release, bool waitForTimeout)
    {
        using var host = await Fixture.Start(release, shortTimeout: waitForTimeout); using var a = new Session(host.Url); await a.Initialize();
        await a.Tool("ConnectPortal", new() { ["processId"] = 323 });
        int pid = (int)(await a.Tool("CompileDevice"))["data"]!["fixturePid"]!;
        using var worker = Process.GetProcessById(pid);
        var running = a.Tool("CompileDevice", new() { ["hang"] = true }, ok: false);
        if (waitForTimeout) await running;
        else
        {
            for (int i = 0; i < 100; i++)
            {
                var snapshot = await a.Tool("GetOpennessWorkerStatus");
                if (snapshot["data"]!.ToJsonString().Contains("\"active\":1")) break;
                await Task.Delay(20);
            }
        }
        var detached = await a.Tool("DisconnectPortal");
        Assert.True((bool?)detached["data"]?["requiresTiaRestart"]);
        Assert.False((bool?)detached["data"]?["workerAcknowledged"]);
        Assert.Contains("restart that TIA instance", (string?)detached["data"]?["recoveryMessage"]);
        await running;
        Assert.True(worker.WaitForExit(5000));
        Assert.Equal("ACTIVE\n", File.ReadAllText(Path.Combine(host.DataDirectory, "leases", "323-1.lease")));
        await a.End();
    }
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Restart_after_a_timeout_reports_a_dirty_lease(string release)
    {
        using var host = await Fixture.Start(release, shortTimeout: true); using var a = new Session(host.Url); await a.Initialize();
        await a.Tool("ConnectPortal", new() { ["processId"] = 324 });
        await a.Tool("CompileDevice", new() { ["hang"] = true }, ok: false);
        var restarted = await a.Tool("RestartOpennessWorker", new() { ["confirmRestart"] = true });
        Assert.Contains("\"requiresTiaRestart\":true", restarted["data"]!.ToJsonString());
        Assert.Contains("restart that TIA instance", restarted["data"]!.ToJsonString());
        Assert.Equal("ACTIVE\n", File.ReadAllText(Path.Combine(host.DataDirectory, "leases", "324-1.lease")));
        await a.End();
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Http_sessions_own_workers_disconnect_restart_and_teardown(string release)
    {
        using var host = await Fixture.Start(release);
        using var a = new Session(host.Url);
        using var b = new Session(host.Url);
        await a.Initialize(); await b.Initialize();
        Assert.NotEqual(a.Id, b.Id);
        int pidA = (int)(await a.Tool("CompileDevice"))["data"]!["fixturePid"]!;
        int pidB = (int)(await b.Tool("CallTool", new() { ["name"] = "CompileDevice", ["arguments"] = new JsonObject() }))["data"]!["fixturePid"]!;
        Assert.NotEqual(pidA, pidB);
        using var processA = Process.GetProcessById(pidA);
        using var processB = Process.GetProcessById(pidB);
        await a.Tool("ConnectPortal", new() { ["processId"] = 123 });
        await b.Tool("ConnectPortal", new() { ["processId"] = 124 });
        long generationA = (long)(await a.Tool("GetOpennessWorkerStatus"))["data"]!["evidence"]!["worker"]!["generation"]!;
        await b.Tool("RestartOpennessWorker", new() { ["confirmRestart"] = true });
        Assert.Equal(generationA, (long?)(await a.Tool("GetOpennessWorkerStatus"))["data"]!["evidence"]!["worker"]!["generation"]);
        Assert.False(processA.HasExited);
        Assert.True(processB.WaitForExit(5000));
        await a.Tool("DisconnectPortal");
        var refused = await a.Tool("GetSessionState", ok: false);
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]?["code"]);
        await b.Tool("GetSessionState"); await b.Tool("ListPortalProcessProjects");
        int currentB = (int)(await b.Tool("CompileDevice"))["data"]!["fixturePid"]!;
        using var workerB = Process.GetProcessById(currentB);
        await a.End();
        Assert.True(processA.WaitForExit(5000));
        Assert.False(workerB.HasExited);
        await b.Tool("GetSessionState");
        await b.End();
        Assert.True(workerB.WaitForExit(5000));
        using var c = new Session(host.Url); await c.Initialize(); await c.Tool("GetSessionState"); await c.End();
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Session_fault_and_bridge_staging_owner_do_not_escape_the_session(string release)
    {
        using var host = await Fixture.Start(release);
        using var a = new Session(host.Url); using var b = new Session(host.Url);
        await a.Initialize(); await b.Initialize();
        await a.Tool("CompileDevice", new() { ["fault"] = true }, ok: false);
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)(await a.Tool("GetSessionState", ok: false))["error"]?["code"]);
        await b.Tool("GetSessionState"); await b.Tool("CompileDevice");
        var stageArgs = new JsonObject { ["files"] = new JsonArray(new JsonObject { ["fileName"] = "fixture.scl", ["kind"] = "scl", ["content"] = "FUNCTION Fixture : Void\nBEGIN\nEND_FUNCTION" }), ["dryRun"] = false };
        await b.Tool("StageImportFiles", stageArgs);
        var batches = (await a.Tool("ListStagedImportFiles"))["data"]!.ToJsonString();
        Assert.Contains(b.Id!, batches);
        Assert.DoesNotContain("\"currentSession\":true", batches);
        await a.End(); await b.End();
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Authored_lease_refusal_is_sanitised_and_internal_exception_text_is_hidden(string release)
    {
        using var host = await Fixture.Start(release); using var session = new Session(host.Url); await session.Initialize();
        var authored = await session.Tool("ConnectPortal", new() { ["processId"] = 901 }, ok: false);
        Assert.Equal("PRECONDITION_FAILED", (string?)authored["error"]?["code"]);
        Assert.Contains(EngineSessionFixture.LeaseMessage, (string?)authored["error"]?["message"]);
        Assert.DoesNotContain("private", authored.ToJsonString());
        var sanitised = await session.Tool("ConnectPortal", new() { ["processId"] = 903 }, ok: false);
        Assert.Contains("Authored refusal", (string?)sanitised["error"]?["message"]);
        Assert.DoesNotContain("private", sanitised.ToJsonString());
        var internalError = await session.Tool("ConnectPortal", new() { ["processId"] = 902 }, ok: false);
        Assert.DoesNotContain("Internal secret", internalError.ToJsonString());
        await session.Tool("GetSessionState"); await session.End();
    }

    [Fact]
    public async Task Health_has_file_version_and_readiness_requires_authentication()
    {
        using var host = await Fixture.Start("21"); using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(host.Url) };
        var health = await client.GetFromJsonAsync<JsonObject>("/mcp/health");
        Assert.Equal("full-engine", (string?)health!["profile"]); Assert.False(string.IsNullOrWhiteSpace((string?)health["fileVersion"]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/mcp/ready")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-key");
        var ready = await client.GetFromJsonAsync<JsonObject>("/mcp/ready");
        Assert.True((bool?)ready!["mcpHostReady"]); Assert.Equal("21", (string?)ready["releaseKey"]); Assert.Equal("NOT RUN", (string?)ready["nativeAcceptance"]);
    }

    private sealed class Session(string url) : IDisposable
    {
        private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(20) };
        private int sequence;
        internal string? Id;
        internal async Task<JsonNode> Call(string method, JsonObject input)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-key");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await client.PostAsJsonAsync("/mcp", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = method, ["params"] = input });
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) { Id = values.Single(); client.DefaultRequestHeaders.Remove("Mcp-Session-Id"); client.DefaultRequestHeaders.Add("Mcp-Session-Id", Id); }
            string text = await response.Content.ReadAsStringAsync();
            var reply = JsonNode.Parse(text.StartsWith("event:") || text.StartsWith("data:") ? text.Split('\n').Single(l => l.StartsWith("data:")).Substring(5) : text)!;
            Assert.Null(reply["error"]); return reply["result"]!;
        }
        internal Task Initialize() => Call("initialize", new() { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "engine-session-fixture", ["version"] = "1" } });
        internal async Task<JsonNode> Tool(string name, JsonObject? arguments = null, bool ok = true)
        {
            var result = await Call("tools/call", new() { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() });
            var body = result["structuredContent"]!;
            Assert.True((bool?)body["ok"] == ok, body.ToJsonString()); Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse((string)result["content"]![0]!["text"]!)));
            return body;
        }
        internal async Task End() { using var response = await client.DeleteAsync("/mcp"); response.EnsureSuccessStatusCode(); }
        public void Dispose() => client.Dispose();
    }

    private sealed class Fixture(Process process, string url, string directory) : IDisposable
    {
        internal string Url => url;
        internal string DataDirectory => directory;
        internal static async Task<Fixture> Start(string release, bool shortTimeout = false)
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
            string directory = Path.Combine(root, "bin-build", "P7-07a-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
            var tools = new JsonArray(); var descriptors = new JsonArray();
            foreach (string name in new[] { "CompileDevice", "CallTool", "GetOpennessWorkerStatus", "RestartOpennessWorker" })
            {
                var properties = name == "CompileDevice" ? new JsonObject { ["fault"] = new JsonObject { ["type"] = "boolean" }, ["hang"] = new JsonObject { ["type"] = "boolean" } }
                    : name == "RestartOpennessWorker" ? new JsonObject { ["confirmRestart"] = new JsonObject { ["type"] = "boolean" } }
                    : name == "CallTool" ? new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["arguments"] = new JsonObject { ["type"] = "object" } } : new JsonObject();
                tools.Add(new JsonObject { ["name"] = name, ["description"] = "[L0][Diagnostics][READ] Offline session fixture.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false } });
                var parameters = new JsonArray();
                foreach (var p in properties) parameters.Add(new JsonObject { ["name"] = p.Key, ["friendlyType"] = (string)p.Value!["type"]!,
                    ["clrType"] = p.Key == "arguments" ? typeof(TiaMcp.Logic.V4.Inputs.ToolArguments).FullName : p.Key == "name" ? "System.String" : "System.Boolean",
                    ["required"] = false, ["description"] = "", ["synthesized"] = false, ["allowedValues"] = new JsonArray() });
                descriptors.Add(new JsonObject { ["name"] = name, ["rawDescription"] = "", ["signature"] = name + "()", ["parameters"] = parameters,
                    ["dryRun"] = new JsonObject { ["present"] = false, ["default"] = false }, ["execution"] = name == "CompileDevice" ? "worker" : "host" });
            }
            string catalog = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalog, new JsonObject { ["formatVersion"] = 1, ["release"] = release,
                ["workerSha256"] = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient.Hash(exe), ["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(BehaviorCapabilities).Assembly, release),
                ["tools"] = tools, ["descriptors"] = descriptors, ["liteTools"] = new JsonArray(), ["serverInstructions"] = "Offline session fixture." }.ToJsonString());
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string url = "http://127.0.0.1:" + port;
            var start = new ProcessStartInfo(Path.ChangeExtension(typeof(foundationhost::TiaMcp.FoundationHost.HostOptions).Assembly.Location, ".exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--bundle-root", root, "--release-key", release, "--engine-worker", exe, "--engine-catalog", catalog, "--transport", "http", "--http-prefix", url + "/", "--http-api-key", "fixture-key", "--profile", "full" }) start.ArgumentList.Add(arg);
            if (shortTimeout)
            {
                string timeouts = Path.Combine(directory, "timeouts.json"); File.WriteAllText(timeouts, "{\"default\":10,\"compile\":0.5}");
                start.ArgumentList.Add("--worker-timeout-config"); start.ArgumentList.Add(timeouts);
            }
            start.Environment["TIA_MCP_DATA_DIRECTORY"] = directory; start.Environment["TiaPortalLocation"] = "";
            // Release checks set their own diagnostics root; the journal assertions read this host's data directory.
            start.Environment["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(directory, "diagnostics");
            string config = Path.Combine(directory, "config"); Directory.CreateDirectory(config);
            new ApprovalSettings(false, 1).Save(Path.Combine(config, "approval.settings"));
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(Path.Combine(directory, "host.log"), e.Data + "\n"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(Path.Combine(directory, "host-error.log"), e.Data + "\n"); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var fixture = new Fixture(process, url, directory);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    Assert.False(process.HasExited, "Host exited: " + (File.Exists(Path.Combine(directory, "host-error.log")) ? File.ReadAllText(Path.Combine(directory, "host-error.log")) : ""));
                    try { if ((await client.GetAsync(url + "/mcp/health")).IsSuccessStatusCode) return fixture; }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
                    await Task.Delay(50);
                }
                throw new TimeoutException("Fixture host did not listen.");
            }
            catch { fixture.Dispose(); throw; }
        }
        public void Dispose() { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } process.Dispose(); }
    }
}
