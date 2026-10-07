using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaOpenness.Shared;
using Xunit;

public sealed class ApprovalPrecheckTests
{
    private sealed class Worker : IFoundationWorker
    {
        internal int Previews, Writes, Reads;
        internal string? Refusal;
        internal bool Unknown, Cancel;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            bool preview = (bool?)arguments["dryRun"] == true;
            if (preview) Previews++; else if (arguments.ContainsKey("dryRun")) Writes++; else Reads++;
            if (Cancel) throw new OperationCanceledException(token);
            if (Refusal != null) throw new WorkerOperationException("Fixture precondition: " + Refusal, -32602, "rejected-before-operation", JsonSerializer.Serialize(new { parameter = Refusal }));
            if (!preview && Unknown) throw new WorkerOperationException("Fixture native write interrupted.", -32603, "unknown");
            return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = !preview, ["ProjectFile"] = "C:/Test.ap19" });
        }
        public void Dispose() { }
    }
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args) => new(DispatchProxy.Create<IMcpServer, ServerProxy>())
    { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
    private static JsonObject Arguments(string source) => source switch
    {
        "CreatePlcTag" => JsonNode.Parse("{\"plc\":\"PLC_1\",\"table\":\"T\",\"name\":\"Ready\",\"dataType\":\"Bool\",\"address\":\"%M0.0\"}")!.AsObject(),
        "ImportPlcTagTable" => JsonNode.Parse("{\"softwarePath\":\"PLC_1\",\"folderPath\":\"\",\"importPath\":\"C:/T.xml\",\"overwrite\":false}")!.AsObject(),
        "ExportBlock" => JsonNode.Parse("{\"softwarePath\":\"PLC_1\",\"blockPath\":\"B\",\"exportPath\":\"C:/Output\"}")!.AsObject(),
        _ => new()
    };
    [Theory]
    [InlineData("CreatePlcTag", "plc")][InlineData("CreatePlcTag", "table")][InlineData("CreatePlcTag", "name")]
    [InlineData("ImportPlcTagTable", "overwrite")][InlineData("ImportPlcTagTable", "importPath")]
    [InlineData("CloseProject", "ownership")][InlineData("ExportBlock", "exportPath")]
    [InlineData("CreatePlcTag", "expectedProjectFile")]
    public async Task Invalid_write_has_only_request_and_end_without_approval(string source, string condition)
    {
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
        {
            var worker = new Worker { Refusal = condition == "expectedProjectFile" ? null : condition };
            var audit = new AuditLog(Path.GetFullPath(Path.Combine("bin-build/P6-60/audit", Guid.NewGuid().ToString("N"))));
            using var scope = AuditInvocation.UseLog(audit);
            int waits = 0;
            var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source), worker), release, source == "ExportBlock" ? _ => TiaMcp.Logic.V4.BehaviorPolicy.SafeV4 : null,
                () => new(true, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
            var args = Arguments(source); args["dryRun"] = false; args["confirm"] = true;
            if (condition != "expectedProjectFile") args["expectedProjectFile"] = "C:/Test.ap19";
            if (source == "ExportBlock") { args.Remove("dryRun"); args["mode"] = "apply"; args["expectedPlanHash"] = new string('a', 64); }
            var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
            var body = result.StructuredContent!;
            _ = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Envelope>(body.ToJsonString());
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Single(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["stage"] == "approval-precheck");
            Assert.Equal(0, waits); Assert.Equal(0, worker.Writes);
            Assert.Equal(new[] { "request", "end" }, audit.Read().Select(r => r.Event));
            Assert.All(audit.Read(), r => Assert.Equal((string?)body["meta"]?["requestId"], r.RequestId));
            Assert.True(audit.Verify().Passed);
        }
    }
    [Theory]
    [InlineData(true, false)][InlineData(true, true)][InlineData(false, false)]
    public async Task Preview_then_approval_then_real_precondition_with_disabled_bypass(bool enabled, bool changed)
    {
        var worker = new Worker(); int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTag"), worker), "19", null,
            () => new(enabled, 1), (pending, _, _) => { waits++; if (changed) worker.Refusal = "name"; return Task.FromResult(new ApprovalOutcome(pending, !enabled, null)); });
        var args = Arguments("CreatePlcTag"); args["dryRun"] = false; args["confirm"] = true; args["expectedProjectFile"] = "C:/Test.ap19";
        var watch = Stopwatch.StartNew();
        var result = await tool.InvokeAsync(Request("CreatePlcTag", args)); watch.Stop();
        Assert.Equal(enabled ? 1 : 0, worker.Previews); Assert.Equal(1, worker.Writes); Assert.Equal(1, waits);
        Assert.Equal(changed ? "rejected-before-operation" : "succeeded", (string?)result.StructuredContent?["meta"]?["outcome"]);
        if (!enabled) Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "APPROVAL_DISABLED");
        Directory.CreateDirectory("bin-build/P6-60");
        File.AppendAllText("bin-build/P6-60/latency.txt", $"foundation enabled={enabled} changed={changed}: {watch.Elapsed.TotalMilliseconds:F3} ms; previews={worker.Previews}\n");
    }
    [Fact]
    public async Task Warm_fixture_reports_added_preview_latency()
    {
        bool enabled = true;
        var worker = new Worker();
        var audit = new AuditLog(Path.GetFullPath(Path.Combine("bin-build/P6-60/timing-audit", Guid.NewGuid().ToString("N"))));
        using var scope = AuditInvocation.UseLog(audit);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTag"), worker), "19", null,
            () => new(enabled, 1), (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, true, null)));
        var args = Arguments("CreatePlcTag"); args["dryRun"] = false; args["confirm"] = true; args["expectedProjectFile"] = "C:/Test.ap19";
        // The fixture decision bypasses pipe completion to isolate precheck cost.
        var samples = new Dictionary<bool, List<double>> { [true] = new(), [false] = new() };
        for (int i = 0; i < 35; i++) foreach (bool setting in new[] { false, true })
        {
            enabled = setting; var watch = Stopwatch.StartNew();
            var result = await tool.InvokeAsync(Request("CreatePlcTag", args)); watch.Stop();
            Assert.True((bool?)result.StructuredContent?["ok"]);
            if (i >= 5) samples[setting].Add(watch.Elapsed.TotalMilliseconds);
        }
        File.WriteAllText("bin-build/P6-60/foundation-latency.json", JsonSerializer.Serialize(samples.ToDictionary(p => p.Key ? "enabled" : "disabled", p => p.Value)));
    }
    [Fact]
    public async Task Cancellation_in_precheck_is_not_started_and_never_approved()
    {
        var worker = new Worker { Cancel = true }; int waits = 0;
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTag"), worker), "19", null,
            () => new(true, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
        var args = Arguments("CreatePlcTag"); args["dryRun"] = false; args["confirm"] = true; args["expectedProjectFile"] = "C:/Test.ap19";
        var result = await tool.InvokeAsync(Request("CreatePlcTag", args));
        Assert.Equal("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]); Assert.Equal(0, waits); Assert.Equal(0, worker.Writes);
    }
    [Fact]
    public async Task Only_the_issued_unknown_write_is_unknown_and_later_reads_and_writes_refuse_without_dispatch()
    {
        var worker = new Worker { Unknown = true }; int waits = 0;
        FoundationV4Tool Tool(string source) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source), worker), "19", null,
            () => new(true, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
        var args = Arguments("CreatePlcTag"); args["dryRun"] = false; args["confirm"] = true; args["expectedProjectFile"] = "C:/Test.ap19";
        var first = await Tool("CreatePlcTag").InvokeAsync(Request("CreatePlcTag", args)); Assert.Equal("OUTCOME_UNKNOWN", (string?)first.StructuredContent?["error"]?["code"]);
        foreach (var source in new[] { "CreatePlcTag", "GetProject", "Connect" })
        {
            var tool = Tool(source); var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, source == "CreatePlcTag" ? args : source == "Connect" ? new JsonObject { ["processId"] = 1 } : new()));
            Assert.Equal("SESSION_RESET_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("previous-outcome-unknown", (string?)result.StructuredContent?["error"]?["details"]?["reason"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
        Assert.Equal(1, waits); Assert.Equal(1, worker.Previews); Assert.Equal(1, worker.Writes); Assert.Equal(0, worker.Reads);
    }
    public class ServerProxy : DispatchProxy
    { protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null; }
}
