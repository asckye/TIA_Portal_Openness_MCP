using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.DiagnosticClients;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ScriptClientTests
{
    private sealed class FixtureRpc(Func<string, JsonNode?, JsonNode?> respond) : IMcpClient
    {
        public List<(string Method, JsonNode? Params)> Requests { get; } = [];
        public JsonNode? Rpc(string method, JsonNode? parameters = null, bool notify = false, int timeoutSeconds = 0)
        { Requests.Add((method, parameters?.DeepClone())); return respond(method, parameters); }
    }
    private static JsonObject Value(bool ok = true, JsonObject? data = null, string? outcome = null) => new()
    {
        ["schemaVersion"] = 4, ["ok"] = ok, ["data"] = data ?? new JsonObject(),
        ["error"] = ok ? null : new JsonObject { ["code"] = "OUTCOME_UNKNOWN", ["message"] = "Inspect evidence" },
        ["meta"] = new JsonObject { ["outcome"] = outcome ?? (ok ? "succeeded" : "unknown"), ["requiresSessionReset"] = !ok }
    };
    private static JsonObject Reply(JsonObject value) => new() { ["result"] = new JsonObject { ["isError"] = !(bool)value["ok"]!, ["structuredContent"] = value.DeepClone() } };
    [Fact]
    public void ConstructingCampaignIsPassive()
    {
        var calls = 0; var campaign = new Campaign(() => { calls++; throw new InvalidOperationException(); });
        Assert.False(campaign.Connected); Assert.Equal(0, calls);
    }
    [Fact]
    public void DirectAndBridgeArgumentsRemainTyped()
    {
        var client = new FixtureRpc((_, _) => Reply(Value(data: new JsonObject { ["items"] = new JsonArray() })));
        var campaign = new Campaign(client, ["GetSessionState"]);
        Assert.True((bool)campaign.CallRaw("GetSessionState", new JsonObject())["ok"]!);
        Assert.Equal("GetSessionState", (string?)client.Requests[^1].Params?["name"]);
        campaign.CallRaw("RunReadOnlyToolBatch", new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["name"] = "GetSessionState", ["arguments"] = new JsonObject() }) });
        var request = client.Requests[^1].Params!;
        Assert.Equal("CallTool", (string?)request["name"]); Assert.IsType<JsonArray>(request["arguments"]!["arguments"]!["operations"]);
        Assert.False(request["arguments"]!.AsObject().ContainsKey("argumentsJson"));
    }
    [Theory]
    [InlineData("ok")]
    [InlineData("error")]
    [InlineData("any")]
    public void UnknownOutcomeNeverPasses(string expectation)
    {
        Assert.False(Campaign.Classify(Value(false)).Ok);
        Assert.Equal("UNCONFIRMED", Campaign.Verdict(Value(false), expectation));
        var known = Value(false, outcome: "failed"); known["meta"]!["requiresSessionReset"] = false; known["error"]!["code"] = "PROJECT_NOT_BOUND";
        Assert.Equal(expectation == "ok" ? "FAIL" : "PASS", Campaign.Verdict(known, expectation));
        Assert.Throws<ArgumentException>(() => Campaign.Classify(new JsonObject { ["message"] = "success", ["meta"] = new JsonObject { ["success"] = true } }));
    }
    [Fact]
    public void SweepAndWatcherDecodeTheSameV4Fixtures()
    {
        var response = Reply(Value()); var client = new FixtureRpc((_, _) => response);
        Assert.True((bool)McpSession.Call("ListDevices", client, new JsonObject())["ok"]!);
        Assert.True((bool)McpSession.Call("SynchronizeVersionControlWorkspace", client, new JsonObject { ["dryRun"] = false })["ok"]!);
        Assert.False((bool)client.Requests[^1].Params!["arguments"]!["dryRun"]!);
        response = Reply(Value(false)); Assert.False((bool)McpSession.Call("CompilePlcSoftware", client, new JsonObject { ["softwarePath"] = "PLC" })["ok"]!);
    }
    [Fact]
    public void ExportIdentityAndNestedFailuresArePreserved()
    {
        var value = Value(data: new JsonObject { ["items"] = new JsonArray(new JsonObject { ["result"] = Value(false) }), ["export"] = new JsonObject { ["id"] = "local" } });
        var fields = Campaign.ResultFields(value); Assert.Equal("local", (string?)fields["exportId"]);
        Assert.False((bool)fields["items"]![0]!["result"]!["ok"]!);
    }
    [Fact]
    public void ExportAssemblesPagesAndStopsOnFailureOrRepeatedOffset()
    {
        JsonObject Page(string text, int? next) { var value = Value(data: new JsonObject { ["text"] = text }); value["meta"]!["paging"] = new JsonObject { ["nextOffset"] = next }; return value; }
        var pages = new Queue<JsonObject>([Page("abc", 3), Page("def", null)]); var offsets = new List<int>();
        Assert.Equal("abcdef", Campaign.Assemble("fixture", (_, args) => { offsets.Add((int)args["offset"]!); return pages.Dequeue(); }));
        Assert.Equal([0, 3], offsets);
        pages = new Queue<JsonObject>([Page("abc", 3), Value(false)]); Assert.Throws<ArgumentException>(() => Campaign.Assemble("fixture", (_, _) => pages.Dequeue()));
        Assert.Throws<ArgumentException>(() => Campaign.Assemble("fixture", (_, _) => Page("abc", 0)));
    }
    [Theory]
    [InlineData("PID=1 project=Fixture", true, true)]
    [InlineData("PID=1 projects=<empty>", true, false)]
    [InlineData("PID=1 project=Fixture", false, false)]
    public void WatcherProjectProbeFailsClosed(string item, bool ok, bool expected)
    {
        var client = new FixtureRpc((_, _) => Reply(Value(ok, new JsonObject { ["items"] = new JsonArray(item) })));
        Assert.Equal(expected, WatchOperations.HasOpenProject(client));
        Assert.False(WatchOperations.HasOpenProject(new FixtureRpc((_, _) => throw new ArgumentException("Invalid V4 envelope"))));
    }
    [Fact]
    public void SseUsesLastResponseAndCombinesDataLines()
    {
        var raw = "data: {\"result\":1}\r\n\r\ndata: {\"method\":\"progress\"}\n\ndata: {\"result\":\ndata: {\"value\":2}}\n\ndata: ignored\n\n";
        Assert.Equal(2, (int)HttpProbe.Decode(raw, "text/event-stream")!["result"]!["value"]!);
        Assert.Null(HttpProbe.Decode("", "application/json"));
    }
    private sealed class HttpFixture(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class WaitingHttpFixture : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
    [Fact]
    public async Task ConfiguredTimeoutAppliesThroughTheSharedClientInterface()
    {
        using var probe = new HttpProbe(new Connection("http://fixture.invalid/mcp", "Bearer fixture"), timeoutSeconds: 1, handler: new WaitingHttpFixture());
        var request = Task.Run(() => McpSession.Tools(probe));
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public void CredentialsNeverReachOutputErrorOrLedger()
    {
        const string token = "fixture-secret-never-log-4829";
        var connection = new Connection("http://fixture.invalid/mcp", "Bearer " + token);
        var handler = new HttpFixture(request =>
        {
            Assert.Equal("Bearer " + token, request.Headers.GetValues("Authorization").Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply(Value(data: new JsonObject { ["summary"] = token })).ToJsonString()) };
            response.Headers.Add("Mcp-Session-Id", "session"); return response;
        });
        using var probe = new HttpProbe(connection, handler: handler);
        var reply = probe.Rpc("tools/call", new JsonObject());
        Assert.DoesNotContain(token, reply!.ToJsonString()); Assert.Equal("session", probe.Session);
        var errorHandler = new HttpFixture(_ => throw new InvalidOperationException("echoed Bearer " + token));
        using var failing = new HttpProbe(connection, handler: errorHandler);
        Assert.DoesNotContain(token, Assert.Throws<InvalidOperationException>(() => failing.Rpc("tools/list")).ToString());
        var client = new FixtureRpc((_, _) => Reply(Value(data: new JsonObject { ["summary"] = token })));
        var campaign = new Campaign(client, ["GetSessionState"]); var ledger = new StringWriter(); var output = new StringWriter();
        campaign.Run(new JsonArray(new JsonObject { ["tool"] = "GetSessionState", ["args"] = new JsonObject { ["fixture"] = token } }), 0, "C:/bundle/exports/test", ledger, output, connection.Redact);
        Assert.DoesNotContain(token, ledger.ToString() + output + connection);
    }
    [Fact]
    public void EnvironmentCredentialsOverrideNestedClaudeEntry()
    {
        var folder = Path.Combine(Repository.FindRoot(Environment.CurrentDirectory), "bin-build", "client-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, ".claude.json"); File.WriteAllText(file, "{\"projects\":[{\"mcpServers\":{\"tia-portal-vm\":{\"url\":\"http://config.invalid/mcp\",\"headers\":{\"Authorization\":\"Bearer config-secret\"}}}}]}");
            var configured = Connection.Load(claudeFile: file, environment: _ => null); Assert.Equal("http://config.invalid/mcp", configured.Url); Assert.Equal("[REDACTED]", configured.Redact("config-secret"));
            var env = Connection.Load(claudeFile: file, environment: key => key switch { "TIA_MCP_URL" => "http://env.invalid/mcp", "TIA_MCP_TOKEN" => "env-secret", _ => null });
            Assert.Equal("http://env.invalid/mcp", env.Url); Assert.Equal("[REDACTED]", env.Redact("env-secret"));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void UnknownCampaignOutcomeStopsWithoutAnotherStep()
    {
        var client = new FixtureRpc((_, p) => (string?)p?["name"] == "GetSessionState" ? Reply(Value()) : Reply(Value(false)));
        var campaign = new Campaign(client, ["First", "Second", "GetSessionState"]);
        var plan = new JsonArray(new JsonObject { ["tool"] = "First", ["args"] = new JsonObject() }, new JsonObject { ["tool"] = "Second", ["args"] = new JsonObject() });
        var ledger = new StringWriter(); var output = new StringWriter(); campaign.Run(plan, 0, "C:/bundle/exports/run", ledger, output);
        Assert.Equal(2, client.Requests.Count); Assert.Contains("UNCONFIRMED", ledger.ToString()); Assert.Contains("stop without retry or cleanup", output.ToString());
    }
    [Fact]
    public void PlanPathsExpandRecursivelyWithoutMutatingFrozenData()
    {
        var args = new JsonObject { ["path"] = "${EXPORT_ROOT}\\x.xml", ["nested"] = new JsonArray(new JsonObject { ["id"] = "LAST" }), ["json"] = "{\"path\":\"${EXPORT_ROOT}\\\\x.xml\"}" };
        var root = Campaign.ExportRoot(new JsonObject { ["bundleRoot"] = "C:/bundle" }, "run");
        var expanded = Campaign.Expand(args, root, "export-id")!;
        Assert.Equal("C:/bundle/exports/run\\x.xml", (string?)expanded["path"]); Assert.Equal("export-id", (string?)expanded["nested"]![0]!["id"]);
        Assert.Equal("C:/bundle/exports/run\\x.xml", (string?)JsonNode.Parse((string)expanded["json"]!)!["path"]);
        Assert.Contains("${EXPORT_ROOT}", args.ToJsonString());
        Assert.Throws<ArgumentException>(() => Campaign.ExportRoot(new JsonObject { ["bundleRoot"] = "C:/bundle", ["exportRoot"] = "C:/Users/SIEMENS/Desktop" }, "run"));
        Assert.Throws<ArgumentException>(() => Campaign.ExportRoot(new JsonObject { ["bundleRoot"] = "C:/bundle" }, "../run"));
    }
    [Fact]
    public void BothClientsSelectThePackagedFoundationHost()
    {
        var root = Path.Combine(Repository.FindRoot(Environment.CurrentDirectory), "bin-build", "bundle-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "manifest")); Directory.CreateDirectory(Path.Combine(root, "runtime/v21"));
        try
        {
            File.WriteAllText(Path.Combine(root, "manifest/package-manifest.json"), "{}"); File.WriteAllText(Path.Combine(root, "runtime/v21/TiaMcp.FoundationHost.exe"), "fixture"); File.WriteAllText(Path.Combine(root, "runtime/v21/release-key.txt"), "21");
            var launch = FoundationLaunch.Resolve(root, "21");
            Assert.Equal(["--bundle-root", root, "--release-key", "21", "--transport", "stdio", "--profile", "full", "--logging", "0"], launch.Arguments);
            Assert.EndsWith("TiaMcp.FoundationHost.exe", launch.Executable);
            Assert.True(WindowsProcesses.OwnedHost(launch.Executable + " " + string.Join(' ', launch.Arguments), launch));
            Assert.False(WindowsProcesses.OwnedHost("TiaMcp.Engine.V21.exe --profile full", launch));
            File.WriteAllText(Path.Combine(root, "runtime/v21/release-key.txt"), "20"); Assert.Throws<ArgumentException>(() => FoundationLaunch.Resolve(root, "21"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ProcessFiltersNeverTreatHeadlessOrUnknownCommandsAsUserSessions()
    {
        Assert.True(WindowsProcesses.UserSession(new WindowsProcess(1, 0, "Siemens.Automation.Portal.exe", "Portal.exe project.ap21")));
        Assert.False(WindowsProcesses.UserSession(new WindowsProcess(1, 0, "Siemens.Automation.Portal.exe", null)));
        Assert.False(WindowsProcesses.UserSession(new WindowsProcess(1, 0, "Siemens.Automation.Portal.exe", "Portal.exe -bootstrapper=BackgroundProcess")));
        Assert.True(WindowsProcesses.HeadlessPortal("Siemens.Automation.Portal.exe -bootstrapper=Openness.Loader.BootStrapper"));
        Assert.False(WindowsProcesses.HeadlessPortal("Siemens.Automation.Portal.exe project.ap21"));
    }
    [Fact]
    public void ScheduledTaskUsesPublishedExeWithInteractiveLeastPrivilegeAndTimeout()
    {
        var doc = XDocument.Parse(WatchOperations.TaskXml("C:/work/watch.exe", "C:/work/config.json", "DOMAIN\\Engineer", 30, DateTimeOffset.Parse("2026-10-10T12:00:00-07:00")));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Text(string name) => doc.Descendants(ns + name).Single().Value;
        Assert.Equal("C:/work/watch.exe", Text("Command")); Assert.Contains("--config", Text("Arguments")); Assert.Equal("PT30M", Text("Interval"));
        Assert.Equal("InteractiveToken", Text("LogonType")); Assert.Equal("LeastPrivilege", Text("RunLevel")); Assert.Equal("IgnoreNew", Text("MultipleInstancesPolicy")); Assert.Equal("PT15M", Text("ExecutionTimeLimit"));
        Assert.Throws<ArgumentException>(() => WatchOperations.TaskXml("watch.exe", "config.json", "user", 0, DateTimeOffset.Now));
    }
    [Fact]
    public void SweepRequiresSuccessfulSentinelAndStopsBeforeCleanupOnUnknownOutcome()
    {
        var tools = new JsonArray(new JsonObject { ["name"] = "GetPlcBlockInfo", ["inputSchema"] = new JsonObject { ["required"] = new JsonArray("softwarePath", "blockPath"), ["properties"] = new JsonObject() } });
        var client = new FixtureRpc((method, p) => method == "tools/list" ? new JsonObject { ["result"] = new JsonObject { ["tools"] = tools.DeepClone() } } : Reply((string?)p?["name"] == "GetPlcBlockInfo" ? Value(false) : Value()));
        var output = new StringWriter(); Assert.Equal(1, WrongPathSweep.Run(client, "fixture.ap21", output)); Assert.DoesNotContain(client.Requests, r => (string?)r.Params?["name"] == "CloseProject"); Assert.Contains("suspects 1", output.ToString());
        var broken = new FixtureRpc((method, p) => method == "tools/list" ? new JsonObject { ["result"] = new JsonObject { ["tools"] = tools.DeepClone() } } : Reply(Value((string?)p?["name"] != "ListDevices")));
        Assert.Throws<ArgumentException>(() => WrongPathSweep.Run(broken, "fixture.ap21", new StringWriter()));
        Assert.DoesNotContain(broken.Requests, r => (string?)r.Params?["name"] == "GetPlcBlockInfo");
    }
    [Fact]
    public void FrozenPlansHaveOnlyConfiguredExportRoots()
    {
        var root = Repository.FindRoot(Environment.CurrentDirectory);
        var files = Directory.GetFiles(Path.Combine(root, "scripts/diagnostics/campaign/plans"), "plan_*.json");
        Assert.Equal(21, files.Length); var count = 0;
        var exportRoot = Campaign.ExportRoot(new JsonObject { ["bundleRoot"] = "C:\\bundle" }, "fixture");
        Assert.Equal("C:/bundle/exports/fixture", exportRoot);
        foreach (var file in files)
        {
            var raw = File.ReadAllBytes(file); Assert.DoesNotContain((byte)13, raw); Assert.False(raw.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
            var plan = JsonNode.Parse(raw)!.AsArray(); count += plan.Count;
            var expanded = Campaign.Expand(plan, exportRoot, "fixture-export")!;
            Assert.DoesNotContain("Desktop", expanded.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("${EXPORT_ROOT}", expanded.ToJsonString());
        }
        Assert.Equal(918, count);
    }
    [Fact]
    public void HistoricalLedgerHandlesNullSuccessAndChecksExactBytes()
    {
        var root = Path.Combine(Repository.FindRoot(Environment.CurrentDirectory), "bin-build", "ledger-fixture-" + Guid.NewGuid().ToString("N"));
        var campaign = Path.Combine(root, "scripts/diagnostics/campaign"); Directory.CreateDirectory(Path.Combine(campaign, "ledger")); Directory.CreateDirectory(Path.Combine(root, "manifest"));
        try
        {
            File.WriteAllText(Path.Combine(root, "manifest/tools-list.json"), "{\"tools\":[{\"name\":\"ReadFixture\",\"domain\":\"Fixture\"}]}");
            File.WriteAllText(Path.Combine(campaign, "historical_tool_names.json"), "{}"); File.WriteAllText(Path.Combine(campaign, "vm_ledger.json"), "{\"invoked\":{}}");
            File.WriteAllText(Path.Combine(campaign, "ledger-history.json"), "{\"overrides\":{},\"intro\":[\"# Fixture\"],\"meanings\":[[\"⚠ 参数/前置条件\",\"refused\"]],\"crashes\":\"crash evidence\"}");
            File.WriteAllText(Path.Combine(campaign, "ledger/run.jsonl"), "{\"tool\":\"ReadFixture\",\"ok\":null,\"text\":\"old|evidence\"}\n");
            var expected = "# Fixture\n|---|---|---:|\n| ⚠ 参数/前置条件 | refused | 1 |\n\ncrash evidence\n\n## Fixture（1）\n\n| 工具 | 状态 | 说明 |\n|---|---|---|\n| `ReadFixture` | ⚠ 参数/前置条件 | V3 historical evidence; V4 native acceptance NOT RUN. old/evidence |\n";
            Assert.Equal(expected, Ledger.Render(root));
            var file = Path.Combine(root, "historical.md"); Ledger.Write(root, file, false); Ledger.Write(root, file, true);
            File.WriteAllText(file, expected.Replace("\n", "\r\n")); Assert.Throws<ArgumentException>(() => Ledger.Write(root, file, true));
            File.WriteAllBytes(file, new byte[] { 0xef, 0xbb, 0xbf }.Concat(System.Text.Encoding.UTF8.GetBytes(expected)).ToArray()); Assert.Throws<ArgumentException>(() => Ledger.Write(root, file, true));
        }
        finally { Directory.Delete(root, true); }
    }
}
