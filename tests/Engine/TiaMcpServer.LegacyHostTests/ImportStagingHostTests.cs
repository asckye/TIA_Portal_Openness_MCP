using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.PlcFoundation;
using TiaOpenness.Shared;
using Xunit;

public sealed class ImportStagingHostTests : IDisposable
{
    private readonly string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    private sealed class Worker : IFoundationWorker
    {
        internal int Imports, Calls;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Calls++;
            var request = new PlcBatchImportRequest { Release = "17", Project = "C:/P.ap17", ExpectedProject = (string?)args["expectedProjectFile"] ?? "", Software = (string)args["softwarePath"]!, Directory = (string)args["dir"]!, DryRun = (bool)args["dryRun"]!, Confirm = (bool)args["confirm"]!, ExpectedHash = (string)args["expectedPlanHash"]!, Order = args["importOrder"]!.AsArray().Select(x => (string)x!).ToArray() };
            var result = PlcBatchImportPolicy.Run(request, Array.Empty<PlcBatchImportObject>(), () => { }, (file, item) => { Imports++; Assert.Contains("<Name>Main</Name>", File.ReadAllText(file.FullName)); item.Number = 1; return new[] { item }; });
            return Task.FromResult(JsonSerializer.SerializeToNode(result));
        }
        public void Dispose() { }
    }
    public ImportStagingHostTests() { Directory.CreateDirectory(bundle); }
    [Theory]
    [InlineData("stdio-A")][InlineData("http-A")]
    public async Task Production_worker_session_lifetime_releases_staging_after_close(string mcpSessionId)
    {
        using var worker = new Worker();
        using var owner = FoundationTool.RegisterStagingSession(worker, mcpSessionId);
        var store = new ImportStagingStore(bundle, "18", owner);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "StageImportFiles"), worker, store), "18",
            null, () => new(false, 1));
        var stage = (await tool.InvokeAsync(Request("StageImportFiles", Args()))).StructuredContent!;
        Assert.True((bool?)stage["ok"]); Assert.Equal(mcpSessionId, (string?)stage["data"]?["mcpSessionId"]);
        using var connected = new ImportStagingStore(bundle, "18", Guid.NewGuid().ToString("N"));
        string id = (string)stage["data"]!["batchId"]!;
        Assert.Throws<ArgumentException>(() => connected.Cleanup(id, false));
        owner.Dispose();
        Assert.Equal("ended", (string?)connected.List()["batches"]![0]!["ownerState"]);
        connected.Cleanup(id, false); Assert.Empty(connected.List()["batches"]!.AsArray()); Assert.Equal(0, worker.Calls);
    }
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public void Unknown_single_import_retains_unavailable_backup_warning(string release)
    {
        var failure = new WorkerOperationException("fixture", -32603, "unknown", JsonSerializer.Serialize(new { exceptionType = "IOException", recoveryStatus = "backup-skipped",
            recoveryDirectory = "C:/bundle/data/recovery", recoveryWarning = "Backup skipped for Group/Uncompiled: inconsistent.",
            recoverySkipped = new[] { new { @object = "Group/Uncompiled", reason = "inconsistent" } }, attemptedPath = "C:/bundle/data/recovery" }));
        var result = FoundationV4Result.Failure(release, "ImportPlcBlock", "fixture", true, true, null, failure).StructuredContent!;
        Assert.Equal("backup-skipped", (string?)result["data"]!["evidence"]!["recoveryStatus"]);
        var warning = Assert.Single(result["meta"]!["warnings"]!.AsArray(), x => (string?)x?["code"] == "BACKUP_SKIPPED")!;
        Assert.Equal("Backup skipped for Group/Uncompiled: inconsistent.", (string?)warning["message"]);
        Assert.Equal("Group/Uncompiled", (string?)warning["details"]?["objects"]?[0]?["object"]);
        Assert.Equal("inconsistent", (string?)warning["details"]?["objects"]?[0]?["reason"]);
        Assert.Equal("C:/bundle/data/recovery", (string?)warning["details"]?["recoveryDirectory"]);
        Assert.DoesNotContain(result["meta"]!["warnings"]!.AsArray(), x => (string?)x?["code"] == "NATIVE_WARNING");
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        Assert.True((bool)result["meta"]!["requiresSessionReset"]!);
    }
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args) => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>())
    { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
    private static JsonObject Args(string name = "Main.xml") => new() { ["dryRun"] = false, ["files"] = new JsonArray(new JsonObject { ["fileName"] = name, ["kind"] = "simaticml", ["content"] = BatchImportTests.Xml("Main") }) };
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public async Task File_write_prechecks_before_approval_and_audits_without_worker(string release)
    {
        var worker = new Worker(); var store = new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N")); int approvals = 0;
        var audit = new AuditLog(Path.Combine(bundle, "audit")); using var scope = AuditInvocation.UseLog(audit);
        var inner = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "StageImportFiles"), worker, store);
        Assert.False(inner.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("expectedProjectFile", out _));
        var tool = new FoundationV4Tool(inner, release, null, () => new(true, 1), async (pending, _, _) =>
        { approvals++; await TiaMcpServer.Tests.ImportStagingTests.VerifyApproval(pending); audit.Approval(pending.RequestId, pending.Host, pending.ReleaseKey, pending.Tool, "granted", pending.PlanHash); Assert.Empty(store.List()["batches"]!.AsArray()); Assert.False(Directory.Exists(Path.Combine(bundle, "staging"))); return new ApprovalOutcome(pending, false, null); });
        var refusal = await tool.InvokeAsync(Request("StageImportFiles", Args("../escape.xml")));
        Assert.False((bool?)refusal.StructuredContent?["ok"]); Assert.Equal(0, approvals); Assert.Equal(0, worker.Calls);
        Assert.Contains(refusal.StructuredContent!["meta"]!["warnings"]!.AsArray(), x => (string?)x?["code"] == "RECOVERY_GUIDANCE");
        Assert.Equal(new[] { "request", "end" }, audit.Read().Select(x => x.Event));
        var large = Args("Main.scl"); large["files"]![0]!["kind"] = "scl"; large["files"]![0]!["content"] = new string('x', 4194304);
        var result = await tool.InvokeAsync(Request("StageImportFiles", large));
        Assert.True((bool?)result.StructuredContent?["ok"]); Assert.Equal(1, approvals); Assert.Equal(0, worker.Calls);
        Assert.True(audit.Verify().Passed); Assert.Contains(audit.Read(), x => x.Event == "approval-granted");
        Assert.Single(store.List()["batches"]!.AsArray());
    }
    [Fact]
    public async Task Staged_directory_round_trips_through_actual_facade_and_fake_import_worker()
    {
        var worker = new Worker(); var store = new ImportStagingStore(bundle, "17", Guid.NewGuid().ToString("N"));
        FoundationV4Tool Tool(string name) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == name), worker, store), "17", null, () => new(false, 1), (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, true, null)));
        var stage = await Tool("StageImportFiles").InvokeAsync(Request("StageImportFiles", Args()));
        Assert.True((bool?)stage.StructuredContent?["ok"]);
        var args = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["dir"] = stage.StructuredContent!["data"]!["directory"]!.DeepClone() };
        var import = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ImportBlocksFromDirectory"), worker);
        var preview = await import.InvokeAsync(Request("ImportBlocksFromDirectory", args)); Assert.NotEqual(true, preview.IsError); Assert.Equal(0, worker.Imports);
        var body = JsonNode.Parse(((TextContentBlock)preview.Content.Single()).Text)!;
        args["dryRun"] = false; args["confirm"] = true; args["expectedProjectFile"] = "C:/P.ap17";
        args["importOrder"] = new JsonArray("Main.xml"); args["expectedPlanHash"] = body["PlanHash"]!.DeepClone();
        var applied = await import.InvokeAsync(Request("ImportBlocksFromDirectory", args)); Assert.NotEqual(true, applied.IsError); Assert.Equal(1, worker.Imports);
        var cleanupArgs = new JsonObject { ["batchId"] = stage.StructuredContent["data"]!["batchId"]!.DeepClone(), ["dryRun"] = false };
        Assert.True((bool?)(await Tool("CleanupStagedImportFiles").InvokeAsync(Request("CleanupStagedImportFiles", cleanupArgs))).StructuredContent?["ok"]);
        Assert.Empty(store.List()["batches"]!.AsArray());
    }
    public void Dispose() { if (Directory.Exists(bundle)) Directory.Delete(bundle, true); }
}
