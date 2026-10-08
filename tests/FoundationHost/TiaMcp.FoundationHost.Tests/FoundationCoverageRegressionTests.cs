using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Adapters;
using TiaOpenness.Shared;
using Xunit;

public sealed class FoundationCoverageRegressionTests
{
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public async Task Refused_foundation_call_carries_the_same_release_example_as_GetToolUsage(string release)
    {
        var worker = new DeleteWorker(false, release);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "DeletePlcExternalSource"), worker), release);
        RequestContext<CallToolRequestParams> Request(string name, object args) => new(DispatchProxy.Create<IMcpServer, ServerProxy>())
        { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) } };
        var refused = await tool.InvokeAsync(Request("DeletePlcExternalSource", new { softwarePath = "caller-secret", externalSourceName = 123 }));
        var hint = refused.StructuredContent!["meta"]!["warnings"]!.AsArray().Single(w => (string?)w?["code"] == "RECOVERY_GUIDANCE")!;
        var usageTool = new ToolUsageTool(release, () => new[] { tool });
        var usageResult = await usageTool.InvokeAsync(Request("GetToolUsage", new { toolName = "DeletePlcExternalSource" }));
        var usage = JsonNode.Parse(((TextContentBlock)usageResult.Content.Single()).Text)!["usage"]!;
        Assert.True(JsonNode.DeepEquals(usage["example"]!["request"]!["params"]!["arguments"], hint["details"]!["exampleArguments"]));
        Assert.Equal(release, (string?)hint["details"]?["releaseKey"]);
        Assert.Equal("DeletePlcExternalSource", (string?)hint["details"]?["getToolUsage"]?["toolName"]);
        Assert.DoesNotContain("caller-secret", hint.ToJsonString());
        V4Json.Deserialize<Envelope>(refused.StructuredContent.ToJsonString());
        var success = await tool.InvokeAsync(Request("DeletePlcExternalSource", new { softwarePath = "PLC_1", groupPath = "", externalSourceName = "Pump.scl", dryRun = true, confirm = false }));
        Assert.True((bool?)success.StructuredContent?["ok"], success.StructuredContent?.ToJsonString());
        Assert.DoesNotContain(success.StructuredContent!["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "RECOVERY_GUIDANCE");
    }

    [Fact]
    public void Foundation_usage_examples_keep_source_extensions_ascii_and_separate_reviewed_operations()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "reference/tool-examples/calls.json"))) root = root.Parent;
        Assert.NotNull(root);
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName, "reference/tool-examples/calls.json")))!["profiles"]!["plc-foundation"]!;
        foreach (var name in new[] { "PlanPlcExternalSourceImport", "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource" })
        {
            var row = calls[name]!;
            Assert.Equal("PLC_1", (string?)row["arguments"]?["softwarePath"]);
            Assert.Contains("without a BOM", (string?)row["note"]);
            Assert.Contains("planning only", (string?)row["note"]);
            if (name == "PlanPlcExternalSourceImport") Assert.False((bool?)row["execution"]?["available"]);
            else Assert.NotNull(row["execution"]?["arguments"]?["expectedPlanHash"]);
            if (name is "GenerateBlocksFromExternalSource" or "DeletePlcExternalSource") Assert.Equal("FC_Add.scl", (string?)row["arguments"]?["externalSourceName"]);
        }
        var sequence = JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "reference/tool-examples/sequences.json")))!.AsArray()
            .Single(s => (string?)s?["id"] == "sequence/plc-scl-block-foundation")!;
        foreach (var operation in new[] { "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource" })
        {
            var steps = sequence["steps"]!.AsArray().Where(s => (string?)s?["tool"] == operation).ToArray();
            Assert.Equal(2, steps.Length); Assert.True((bool?)steps[0]?["arguments"]?["dryRun"]);
            Assert.False((bool?)steps[1]?["arguments"]?["dryRun"]); Assert.NotNull(steps[1]?["arguments"]?["expectedPlanHash"]);
        }
    }
    [Theory]
    [InlineData("softwarePath", "Exact softwarePath required; aliases refused.")]
    [InlineData("softwarePath", "Use an exact softwarePath from GetProjectTree.")]
    [InlineData("filePath", "Use a canonical bounded Windows drive file path with backslashes.")]
    [InlineData("filePath", "External-source generation requires ASCII text; save this source as ASCII without a BOM.")]
    [InlineData("externalSourceName", "External source not found: Missing.scl")]
    public void Known_read_argument_failure_uses_worker_code_and_parameter_without_message_matching(string parameter, string message)
    {
        foreach (var tool in new[] { "PlanPlcExternalSourceImport", "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "ListPlcTags" })
        {
            var result = FoundationV4Result.Failure("14sp1", tool, "fixture", true, false, null,
                new WorkerOperationException(message, -32602, "read-failed", JsonSerializer.Serialize(new { parameter, exceptionType = nameof(AdapterPreconditionException) })));
            var body = result.StructuredContent!;
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal(parameter, (string?)body["error"]?["details"]?["parameter"]);
            Assert.Equal(message, (string?)body["error"]?["message"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
        }
    }

    [Fact]
    public void Native_read_failure_and_untyped_issued_write_are_not_reclassified_by_message()
    {
        const string message = "Use an exact softwarePath from GetProjectTree.";
        var read = FoundationV4Result.Failure("14sp1", "ListPlcTags", "fixture", true, false, null,
            new WorkerOperationException(message, -32603, "read-failed"));
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)read.StructuredContent?["error"]?["code"]);
        var write = FoundationV4Result.Failure("14sp1", "ImportPlcExternalSource", "fixture", true, true, null,
            new WorkerOperationException(message, -32602, "unknown"));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)write.StructuredContent?["error"]?["code"]);
        Assert.True((bool?)write.StructuredContent?["meta"]?["requiresSessionReset"]);
        var state = FoundationV4Result.Failure("14sp1", "PlanPlcExternalSourceImport", "fixture", true, false, null,
            new WorkerOperationException("PLC/root identity changed.", -32603, "rejected-before-operation"));
        Assert.Equal("PRECONDITION_FAILED", (string?)state.StructuredContent?["error"]?["code"]);
        Assert.Equal("The request precondition failed before operation.", (string?)state.StructuredContent?["error"]?["message"]);
        Assert.DoesNotContain("PLC/root identity changed.", state.StructuredContent!.ToJsonString());
    }

    private sealed class SourceFailureWorker : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token) => throw new WorkerOperationException(
            "Fixture source admission.", -32602, "read-failed", "{\"parameter\":\"softwarePath\",\"exceptionType\":\"AdapterPreconditionException\"}");
        public void Dispose() { }
    }
    [Fact]
    public void Source_candidate_observation_preserves_the_worker_admission_error()
    {
        var result = FoundationCandidateSession.For(new SourceFailureWorker()).Source("14sp1", "ListPlcExternalSources", "fixture",
            new SourceRequest { SoftwarePath = "WrongPLC" }, "preview", false, "", "", default);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
        Assert.Equal("Fixture source admission.", result.Error.Message);
        Assert.Equal(Execution.NotStarted, result.Meta.Execution);
        Assert.False(result.Meta.RequiresSessionReset);
    }

    private sealed class DeleteWorker(bool exists, string release = "14sp1") : IFoundationWorker
    {
        internal int Previews, Writes;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.Equal("DeletePlcExternalSource", operation);
            bool preview = (bool?)args["dryRun"] == true;
            if (preview) Previews++; else Writes++;
            var result = new PlcExternalSourceDeleteResult { Release = release, ProjectFile = @"C:\Projects\Example.ap14", ProcessId = 123,
                SoftwarePath = "devices/Station/PLC_1", SourceName = "Pump.scl", RootIdentity = "root", TargetIdentity = exists ? "target" : "",
                Inventory = exists ? new[] { "Pump.scl" } : Array.Empty<string>(), PlanHash = new string('a', 64),
                Status = exists ? preview ? "planned" : "deleted-verified" : "not-found-not-deleted",
                Attempted = exists && !preview, Executed = exists && !preview, Deleted = exists && !preview };
            return Task.FromResult(JsonSerializer.SerializeToNode(result));
        }
        public void Dispose() { }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Missing_delete_returns_the_preview_without_approval_or_an_apply_dispatch(bool exists)
    {
        var worker = new DeleteWorker(exists); int waits = 0;
        var audit = new AuditLog(Path.GetFullPath(Path.Combine("bin-build/P6-64/audit", Guid.NewGuid().ToString("N"))));
        using var scope = AuditInvocation.UseLog(audit);
        var tool = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "DeletePlcExternalSource"), worker), "14sp1", null,
            () => new(true, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); });
        var args = new { softwarePath = "PLC_1", groupPath = "", externalSourceName = "Pump.scl", dryRun = false, confirm = true,
            expectedProjectFile = @"C:\Projects\Example.ap14", expectedPlanHash = new string('a', 64) };
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
        { Params = new() { Name = "DeletePlcExternalSource", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) } };
        var response = await tool.InvokeAsync(request);
        Assert.True((bool?)response.StructuredContent?["ok"], response.StructuredContent?.ToJsonString());
        Assert.Equal(1, worker.Previews); Assert.Equal(exists ? 1 : 0, worker.Writes); Assert.Equal(exists ? 1 : 0, waits);
        Assert.Equal(exists ? "deleted-verified" : "not-found-not-deleted", (string?)response.StructuredContent?["data"]?["status"]);
        Assert.Equal(exists ? new[] { "request", "start", "end" } : new[] { "request", "end" }, audit.Read().Select(r => r.Event));
        Assert.True(audit.Verify().Passed);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"executed\":false,\"attempted\":false,\"status\":\"planned\"}", false)]
    [InlineData("{\"plan\":{\"operations\":[]}}", true)]
    [InlineData("{\"plan\":{\"operations\":[{}]}}", false)]
    [InlineData("{\"items\":[]}", false)]
    [InlineData("{\"items\":[],\"inventoryComplete\":true}", true)]
    public void No_effect_requires_an_explicit_empty_plan_or_complete_inventory(string data, bool expected)
    {
        Assert.Equal(expected, FoundationV4Result.PreviewHasNoEffect(new JsonObject { ["ok"] = true, ["data"] = JsonNode.Parse(data) }));
        Assert.False(FoundationV4Result.PreviewHasNoEffect(new JsonObject { ["ok"] = false, ["data"] = JsonNode.Parse(data) }));
    }

    [Fact]
    public void Forward_and_backslash_spelling_bind_the_same_plan_without_relaxing_canonical_rules()
    {
        PlcExternalSourceImportRequest Request(string file, string allowed) => new() { Release = "14sp1", Project = @"C:\Projects\Demo.ap14",
            ProcessId = 123, Software = "devices/Station/PLC_1", File = file, AllowedFile = allowed };
        PlcExternalSourceImportPlan Plan(PlcExternalSourceImportRequest r) => PlcExternalSourceImportPolicy.Plan(r,
            _ => new MemoryStream(new byte[] { 65 }), () => Array.Empty<string>(), () => { });
        var back = Plan(Request(@"C:\Sources\Pump.scl", @"C:\Sources\Pump.scl"));
        var forward = Plan(Request("C:/Sources/Pump.scl", @"C:\Sources\Pump.scl"));
        Assert.Equal(back.PlanHash, forward.PlanHash); Assert.Equal(back.FilePath, forward.FilePath);
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["groupPath"] = "", ["filePath"] = "C:/Sources/Pump.scl", ["allowedFilePath"] = @"C:\Sources\Pump.scl" };
        ExternalSourcePlanContract.ValidateRequest(args, JsonSerializer.SerializeToNode(forward));
        foreach (var path in new[] { "C:/Sources/../Pump.scl", "C:/Sources//Pump.scl", "C:/Sources/CON.scl", "//server/share/Pump.scl", "C:Pump.scl" })
        {
            var error = Assert.Throws<AdapterPreconditionException>(() => PlcExternalSourceImportPolicy.ValidateFile(path));
            Assert.Equal("filePath", error.ParamName);
        }
        Assert.Equal("allowedFilePath", Assert.Throws<AdapterPreconditionException>(() => Plan(Request("C:/Sources/Pump.scl", "C:/Other.scl"))).ParamName);
        Assert.Equal("filePath", Assert.Throws<AdapterPreconditionException>(() => PlcExternalSourceWorkflowPolicy.InputHash(new MemoryStream(new byte[] { 239, 187, 191, 65 }))).ParamName);
        Assert.False(ExternalSourcePlanContract.SameSoftware("devices/Station/OtherPLC", "PLC_1"));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Exact_duplicate_in_a_later_table_avoids_probes_and_scans_while_case_only_duplicates_remain_rejected(bool folded)
    {
        var tables = Enumerable.Range(0, 12).Select(t => Enumerable.Range(0, 200).Select(i => $"T{t}_Tag{i}").ToArray()).ToArray();
        string selected = folded ? "t11_tAG199" : "T11_Tag199"; int names = 0, finds = 0;
        string? Find(string[] table, string name) { finds++; return table.FirstOrDefault(s => s == name); }
        string Name(string value) { names++; return value; }
        var watch = Stopwatch.StartNew();
        var before = tables.FirstOrDefault(t => PlcFoundationPolicy.SymbolExists(t, n => Find(t, n), Name, selected));
        double beforeMs = watch.Elapsed.TotalMilliseconds; int beforeNames = names, beforeFinds = finds;
        names = finds = 0; watch.Restart();
        var after = PlcFoundationPolicy.SymbolOwner(tables, t => Find(t, selected) != null,
            t => PlcFoundationPolicy.SymbolExists(t, n => Find(t, n), Name, selected, true));
        double afterMs = watch.Elapsed.TotalMilliseconds;
        Assert.Same(tables[11], before); Assert.Same(before, after);
        Assert.Equal(folded ? beforeNames : 0, names); Assert.Equal(folded ? beforeFinds : 12, finds);
        Directory.CreateDirectory("bin-build/P6-64");
        File.AppendAllText("bin-build/P6-64/symbol-measurements.jsonl", JsonSerializer.Serialize(new { folded, beforeMs, afterMs, beforeNames, afterNames = names, beforeFinds, afterFinds = finds }) + "\n");
    }
}
