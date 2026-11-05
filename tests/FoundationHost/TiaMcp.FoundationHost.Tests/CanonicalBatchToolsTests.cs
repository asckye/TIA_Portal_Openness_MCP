using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class CanonicalBatchToolsTests
{
    private static readonly JsonObject Binding = new() {
        ["projectPath"] = "C:/Project.ap21", ["projectName"] = "Project", ["processId"] = 42, ["generation"] = 1 };
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = args.Deserialize<Dictionary<string, JsonElement>>() } };

    [Fact]
    public async Task Whole_batch_preview_has_every_canonical_hash_and_one_approval_executes_in_order()
    {
        using var files = new Files();
        var target = new Target("ImportPlcTagTable"); var batch = Batch("ImportPlcTagTablesFromDirectory", target);
        int approvals = 0;
        batch.Approve = (pending, settings, token) => {
            approvals++; Assert.Contains("batchPlan", pending.ParametersJson); return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        var args = files.Arguments(); var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        var data = preview.StructuredContent!["data"]!.AsObject();
        Assert.True((bool?)preview.StructuredContent["ok"]); Assert.Equal(3, data["items"]!.AsArray().Count);
        Assert
            .All(data["items"]!.AsArray(), item => {
                Assert.Equal(64, ((string)item!["canonicalPlanHash"]!).Length); Assert.True((bool?)item["preview"]?["ok"]); });
        Assert.Empty(target.Writes); Assert.Equal(0, approvals);
        Apply(args, data);
        var request = Request(batch.ProtocolTool.Name, args); var original = request.Params;
        var result = await batch.InvokeAsync(request);
        Assert.Same(original, request.Params); Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Equal(1, approvals); Assert.Equal(new[] { "A.xml", "B.xml", "C.xml" }, target.Writes);
        Assert.All(result.StructuredContent!["data"]!["items"]!.AsArray(), row => Assert.Equal("completed", (string?)row!["status"]));
    }

    [Fact]
    public async Task Tag_table_batch_preserves_unit_scope_and_import_options_in_preview_and_execution()
    {
        using var files = new Files(); var target = new Target("ImportPlcTagTable"); var batch = Batch("ImportPlcTagTablesFromDirectory", target);
        var args = files.Arguments(); args["unitName"] = "UnitA";
        args["swImportOptions"] = new JsonArray(); args["importOptions"] = new JsonArray("SkipInactiveCultures");
        var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        Assert.All(target.Arguments, values =>
        {
            Assert.Equal("UnitA", (string?)values["unitName"]);
            Assert.Empty(values["swImportOptions"]!.AsArray());
            Assert.Equal("SkipInactiveCultures", (string?)Assert.Single(values["importOptions"]!.AsArray()));
            Assert.True((bool?)values["dryRun"]);
        });
        Assert.Equal(3, target.Arguments.Count); Assert.Empty(target.Writes);
        var originalHash = (string?)preview.StructuredContent!["data"]!["planHash"];
        args["unitName"] = "UnitB";
        var changed = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.NotEqual(originalHash, (string?)changed.StructuredContent!["data"]!["planHash"]);
        args["unitName"] = "UnitA"; target.Arguments.Clear();
        batch.Approve = (pending, settings, token) => Task.FromResult(new ApprovalOutcome(pending, false, null));
        Apply(args, preview.StructuredContent!["data"]!.AsObject());
        var applied = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True((bool?)applied.StructuredContent?["ok"]);
        var writes = target.Arguments.Where(values => (bool?)values["dryRun"] == false).ToArray();
        Assert.Equal(3, writes.Length);
        Assert.All(writes, values =>
        {
            Assert.Equal("UnitA", (string?)values["unitName"]);
            Assert.Empty(values["swImportOptions"]!.AsArray());
            Assert.Equal("SkipInactiveCultures", (string?)Assert.Single(values["importOptions"]!.AsArray()));
        });
    }

    [Fact]
    public async Task First_failure_retains_canonical_diagnostics_and_marks_remaining_items_not_started()
    {
        using var files = new Files(); var target = new Target("ImportPlcTagTable") { FailWrite = 2 }; var batch =
            Batch("ImportPlcTagTablesFromDirectory", target);
        batch.Approve = (pending, settings, token) => Task.FromResult(new ApprovalOutcome(pending, false, null));
        var args = files.Arguments(); var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Apply(args, preview.StructuredContent!["data"]!.AsObject());
        var result = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args)); var rows = result.StructuredContent!["data"]!["items"]!.AsArray();
        Assert.True(result.IsError); Assert.Equal("partial", (string?)result.StructuredContent["meta"]?["outcome"]);
        Assert.Equal(new[] { "completed", "failed", "not-started" }, rows.Select(row => (string)row!["status"]!).ToArray());
        Assert.Equal(new[] { "A.xml", "B.xml" }, target.Writes);
        Assert.Equal("original canonical diagnostic", (string?)rows[1]!["result"]?["data"]?["diagnostic"]);
    }

    [Fact]
    public async Task Failure_of_the_first_item_retains_every_result_without_claiming_partial_success()
    {
        using var files = new Files(); var target = new Target("ImportPlcTagTable") { FailWrite = 1 }; var batch =
            Batch("ImportPlcTagTablesFromDirectory", target);
        batch.Approve = (pending, settings, token) => Task.FromResult(new ApprovalOutcome(pending, false, null));
        var args = files.Arguments(); var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Apply(args, preview.StructuredContent!["data"]!.AsObject());
        var result = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True(result.IsError); Assert.Equal("failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        var rows = result.StructuredContent!["data"]!["items"]!.AsArray();
        Assert.Equal(new[] { "failed", "not-started", "not-started" }, rows.Select(row => (string)row!["status"]!).ToArray());
        Assert.Equal("original canonical diagnostic", (string?)rows[0]!["result"]?["data"]?["diagnostic"]);
        Assert.Equal(new[] { "A.xml" }, target.Writes);
    }

    [Fact]
    public async Task An_item_change_while_awaiting_approval_prevents_every_write()
    {
        using var files = new Files(); var target = new Target("ImportPlcTagTable"); var batch = Batch("ImportPlcTagTablesFromDirectory", target);
        batch.Approve = (pending, settings, token) => {
            File.AppendAllText(Path.Combine(files.Directory, "B.xml"), " "); return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        var args = files.Arguments(); var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Apply(args, preview.StructuredContent!["data"]!.AsObject());
        var result = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True(result.IsError); Assert.Equal("PLAN_STALE", (string?)result.StructuredContent?["error"]?["code"]); Assert.Empty(target.Writes);
    }

    [Fact]
    public async Task A_later_import_file_change_stops_before_that_canonical_write()
    {
        using var files = new Files(); var target = new Target("ImportPlcTagTable");
        target.AfterWrite = count => { if (count == 1) File.AppendAllText(Path.Combine(files.Directory, "B.xml"), " "); };
        var batch = Batch("ImportPlcTagTablesFromDirectory", target);
        batch.Approve = (pending, settings, token) => Task.FromResult(new ApprovalOutcome(pending, false, null));
        var args = files.Arguments(); var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Apply(args, preview.StructuredContent!["data"]!.AsObject());
        var result = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True(result.IsError); Assert.Equal(new[] { "A.xml" }, target.Writes);
        var rows = result.StructuredContent!["data"]!["items"]!.AsArray();
        Assert.Equal(new[] { "completed", "failed", "not-started" }, rows.Select(row => (string)row!["status"]!).ToArray());
        Assert.Equal("PLAN_STALE", (string?)rows[1]!["result"]?["error"]?["code"]);
    }

    [Theory]
    [InlineData("ExportPlcWatchTablesToDirectory", "ListPlcWatchTables", "ExportPlcWatchTable")]
    [InlineData("ExportTechnologyObjectsToDirectory", "ListTechnologyObjects", "ExportTechnologyObject")]
    public async Task Export_preview_uses_the_canonical_inventory_and_exact_per_item_paths(string batchName, string readerName, string exportName)
    {
        var reader = new Target(readerName) { Inventory = readerName == "ListPlcWatchTables"
            ? new JsonArray("Group/Watch") : new JsonArray(new JsonObject { ["folder"] = "Group", ["name"] = "Axis" }) };
        var export = new Target(exportName, "20");
        var native = new Target(batchName, "20") { HashInEvidence = true };
        var source = Declaration(batchName); var roster = new Dictionary<string, McpServerTool> {
            [readerName] = reader, [exportName] = export, [batchName] = native };
        var batch = new CanonicalBatchTools.BatchTool(source, roster, "20", () => Binding.DeepClone().AsObject());
        var args = new JsonObject {
            ["softwarePath"] = "PLC", [batchName.Contains("Watch", StringComparison.Ordinal) ? "dir" : "exportDir"] = Path.GetTempPath() };
        var result = await batch.InvokeAsync(Request(batchName, args));
        Assert.True((bool?)result.StructuredContent?["ok"]); var item = Assert.Single(result.StructuredContent!["data"]!["items"]!.AsArray());
        bool watch = batchName == "ExportPlcWatchTablesToDirectory";
        Assert.Equal(watch ? exportName : batchName, (string?)item!["tool"]);
        if (watch) Assert.Contains("Group", (string?)item["arguments"]?["exportPath"]);
        else Assert.Equal(Path.GetTempPath(), (string?)item["arguments"]?["exportDir"]);
        Assert.Empty(export.Writes); Assert.Empty(native.Writes); Assert.Equal(watch ? 1 : 0, reader.Calls);
        if (watch) Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Assert.Single(export.ExportRoots));
        else Assert.Null(Assert.Single(native.ExportRoots));
        Assert.Null(CanonicalBatchTools.CurrentExportRoot.Value);
        int approvals = 0;
        batch.Approve = (pending, settings, token) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        batch.CompleteApproval = (_, _) => Task.CompletedTask;
        Apply(args, result.StructuredContent!["data"]!.AsObject());
        Assert.True((bool?)(await batch.InvokeAsync(Request(batchName, args))).StructuredContent?["ok"]);
        Assert.Equal(1, approvals); Assert.Single(watch ? export.Writes : native.Writes);
    }

    [Fact]
    public async Task Repair_and_compile_are_two_canonical_steps_under_the_same_batch_hash()
    {
        using var files = new Files(); var import = new Target("ImportPlcBlock"); var compile = new Target("CompilePlcDiagnostics");
        var batch = new CanonicalBatchTools
            .BatchTool(Declaration("RepairAndReimportPlcBlock"), new Dictionary<string, McpServerTool> {
                [import.ProtocolTool.Name] = import, [compile.ProtocolTool.Name] = compile }, "21", () => Binding.DeepClone().AsObject());
        var args = new JsonObject { ["softwarePath"] = "PLC", ["importPath"] = Path.Combine(files.Directory, "A.xml") };
        var result = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert
            .Equal(new[] { "ImportPlcBlock", "CompilePlcDiagnostics" }, result.StructuredContent!["data"]!["items"]!.AsArray()
                .Select(row => (string)row!["tool"]!).ToArray());
        Assert.Empty(import.Writes); Assert.Empty(compile.Writes);
        int approvals = 0;
        batch.Approve = (pending, settings, token) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        batch.CompleteApproval = (_, _) => Task.CompletedTask;
        Apply(args, result.StructuredContent!["data"]!.AsObject());
        Assert.True((bool?)(await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args))).StructuredContent?["ok"]);
        Assert.Equal(1, approvals); Assert.Single(import.Writes); Assert.Single(compile.Writes);
    }

    [Fact]
    public async Task A_canonical_preview_that_blocks_apply_refuses_the_batch_before_approval()
    {
        var reader = new Target("ListPlcWatchTables") { Inventory = new JsonArray("Axis") };
        var export = new Target("ExportPlcWatchTable", "20") { BlockPreview = true };
        var batch = new CanonicalBatchTools
            .BatchTool(Declaration("ExportPlcWatchTablesToDirectory"), new Dictionary<string, McpServerTool> {
                [reader.ProtocolTool.Name] = reader, [export.ProtocolTool.Name] = export }, "20", () => Binding.DeepClone().AsObject());
        int approvals = 0;
        batch.Approve = (pending, settings, token) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        batch.CompleteApproval = (_, _) => Task.CompletedTask;
        var args = new JsonObject { ["softwarePath"] = "PLC", ["dir"] = Path.GetTempPath() };
        var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True(preview.StructuredContent?["data"] is JsonObject, preview.StructuredContent?.ToJsonString());
        var plan = preview.StructuredContent!["data"]!.AsObject();
        Assert.True(preview.IsError); Assert.False((bool?)plan["canApply"]);
        var item = Assert.Single(plan["items"]!.AsArray());
        Assert.True((bool?)item!["preview"]?["ok"]); Assert.True((bool?)item["preview"]?["data"]?["applyBlocked"]);
        Apply(args, plan);
        Assert.True((await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args))).IsError);
        Assert.Equal(0, approvals); Assert.Empty(export.Writes);
    }

    [Fact]
    public void A_large_approved_batch_keeps_the_last_item_grant_when_display_is_bounded()
    {
        var items = new JsonArray();
        for (int i = 0; i < 140; i++) items
            .Add(new JsonObject {
                ["tool"] = "ImportPlcTagTable", ["arguments"] = new JsonObject { ["importPath"] = "C:/" + i + ".xml", ["dryRun"] = true },
                    ["canonicalPlanHash"] = new string('a', 64)
            });
        var plan = new JsonObject { ["items"] = items, ["expectedProjectFile"] = "C:/Project.ap21" };
        var approved =
            new ApprovalOutcome(PendingApproval
                .Create("test", "21", "ImportPlcTagTablesFromDirectory", new JsonObject { ["batchPlan"] = plan }.ToJsonString(), null, 120), false, null);
        var args = new JsonObject {
            ["importPath"] = "C:/139.xml", ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/Project.ap21", ["expectedPlanHash"] =
                new string('a', 64)
        };
        using var scope = GenerationStepApproval.EnterBatchItem(approved, 139, "ImportPlcTagTable", args);
        Assert.NotNull(GenerationStepApproval.Match(PendingApproval.Create("test", "21", "ImportPlcTagTable", args.ToJsonString(), null, 120)));
    }

    [Theory]
    [InlineData("14sp1", true)][InlineData("15.1", true)][InlineData("16", true)][InlineData("17", true)]
    [InlineData("18", true)][InlineData("19", true)][InlineData("20", true)][InlineData("21", true)]
    public async Task Technology_export_preserves_the_native_route_where_canonical_export_is_unavailable(string release, bool native)
    {
        var source = new Target("ExportTechnologyObjectsToDirectory", release) { HashInEvidence = native };
        var reader = new Target("ListTechnologyObjects") { Inventory = new JsonArray(new JsonObject { ["name"] = "Axis" }) };
        var export = new Target("ExportTechnologyObject", release);
        var roster = new Dictionary<string, McpServerTool> { [source.ProtocolTool.Name] = source,
            [reader.ProtocolTool.Name] = reader, [export.ProtocolTool.Name] = export };
        var batch = new CanonicalBatchTools.BatchTool(Declaration(source.ProtocolTool.Name), roster, release, () => Binding.DeepClone().AsObject()) {
            Approve = (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, false, null)), CompleteApproval = (_, _) => Task.CompletedTask };
        using var files = new Files();
        var args = new JsonObject { ["softwarePath"] = "PLC", ["exportDir"] = Path.Combine(files.Directory, "new") };
        var preview = await batch.InvokeAsync(Request(source.ProtocolTool.Name, args));
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        Assert.Equal(native ? 1 : 0, source.Calls); Assert.Empty(source.Writes); Assert.Empty(export.Writes);
        Apply(args, preview.StructuredContent!["data"]!.AsObject());
        Assert.True((bool?)(await batch.InvokeAsync(Request(source.ProtocolTool.Name, args))).StructuredContent?["ok"]);
        Assert.Equal(native ? 1 : 0, source.Writes.Count); Assert.Equal(native ? 0 : 1, export.Writes.Count);
        if (native) Assert.Equal(new string('a', 64),
            (string?)source.Arguments.Single(values => (bool?)values["dryRun"] == false)["expectedPlanHash"]);
    }

    [Theory]
    [InlineData("..")][InlineData("%2e%2e")][InlineData("Group%2fOther")][InlineData("Group%5cOther")]
    public async Task Group_names_cannot_escape_the_export_directory(string group)
    {
        using var files = new Files();
        var reader = new Target("ListPlcWatchTables") { Inventory = new JsonArray(group + "/Watch") };
        var export = new Target("ExportPlcWatchTable");
        var batch = new CanonicalBatchTools.BatchTool(Declaration("ExportPlcWatchTablesToDirectory"),
            new Dictionary<string, McpServerTool> { [reader.ProtocolTool.Name] = reader, [export.ProtocolTool.Name] = export },
            "20", () => Binding.DeepClone().AsObject());
        var directory = Path.Combine(files.Directory, "new");
        var result = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, new JsonObject { ["softwarePath"] = "PLC", ["dir"] = directory }));
        Assert.True(result.IsError); Assert.Empty(export.Arguments); Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Export_name_filters_preserve_watch_selection_and_native_batch_selector(bool watch)
    {
        var reader = new Target(watch ? "ListPlcWatchTables" : "ListTechnologyObjects") { Inventory = watch
            ? new JsonArray("Group/MixedName") : new JsonArray(new JsonObject { ["folder"] = "Group", ["name"] = "MixedName" }) };
        var target = new Target(watch ? "ExportPlcWatchTable" : "ExportTechnologyObjectsToDirectory", "20");
        var name = watch ? "ExportPlcWatchTablesToDirectory" : "ExportTechnologyObjectsToDirectory";
        var batch = new CanonicalBatchTools.BatchTool(Declaration(name), new Dictionary<string, McpServerTool> {
            [reader.ProtocolTool.Name] = reader, [target.ProtocolTool.Name] = target }, "20", () => Binding.DeepClone().AsObject());
        var args = new JsonObject { ["softwarePath"] = "PLC", [watch ? "dir" : "exportDir"] = Path.GetTempPath(),
            ["regexName"] = "^mixedname$" };
        var result = await batch.InvokeAsync(Request(name, args));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Single(result.StructuredContent!["data"]!["items"]!.AsArray());
        Assert.Single(target.Arguments);
        if (!watch) Assert.Equal("^mixedname$", (string?)target.Arguments[0]["regexName"]);
    }

    [Fact]
    public async Task Only_a_batch_supplies_the_validated_root_to_the_native_export_request()
    {
        using var files = new Files(); using var worker = new RootedExportWorker();
        var target = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "ExportPlcWatchTable"), worker), "20");
        Assert.DoesNotContain("plannedExportRoot", target.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
        var directory = Path.Combine(files.Directory, "planned");
        var direct = await target.InvokeAsync(Request("ExportPlcWatchTable", new JsonObject {
            ["softwarePath"] = "PLC", ["watchTableName"] = "Group/Table", ["exportPath"] = Path.Combine(directory, "Group", "Table.xml") }));
        Assert.True(direct.IsError); Assert.Null(worker.Roots.Single());
        var reader = new Target("ListPlcWatchTables") { Inventory = new JsonArray("Group/Table") };
        var batch = new CanonicalBatchTools.BatchTool(Declaration("ExportPlcWatchTablesToDirectory"),
            new Dictionary<string, McpServerTool> { [reader.ProtocolTool.Name] = reader, [target.ProtocolTool.Name] = target },
            "20", () => Binding.DeepClone().AsObject());
        var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, new JsonObject { ["softwarePath"] = "PLC", ["dir"] = directory }));
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        Assert.Equal(directory, worker.Roots.Last()); Assert.False(Directory.Exists(directory));
        Assert.Null(CanonicalBatchTools.CurrentExportRoot.Value);
    }

    private sealed class RootedExportWorker : IFoundationWorker
    {
        internal List<string?> Roots { get; } = new();
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            string? root = (string?)args["plannedExportRoot"]; Roots.Add(root);
            var result = TiaMcp.Adapters.PlcSpecialExportPolicy.Run("watch-table", "20", "C:/Project.ap21", "PLC",
                (string)args["watchTableName"]!, (string)args["exportPath"]!, true, true, "", () => { }, _ => { },
                allowPlannedDirectories: root != null, plannedExportRoot: root ?? "");
            return Task.FromResult(JsonSerializer.SerializeToNode(result));
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Technology_exports_use_one_native_route_and_result_shape_on_all_eight_releases()
    {
        string? shape = null, appliedShape = null, envelopeShape = null, appliedEnvelopeShape = null;
        static string Shape(JsonNode? node) => node switch {
            JsonObject value => "{" + string.Join(",", value.OrderBy(p => p.Key).Select(p => p.Key + ":" + Shape(p.Value))) + "}",
            JsonArray value => "[" + string.Join(",", value.Select(Shape)) + "]",
            null => "null",
            _ => node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? "boolean" : node.GetValueKind().ToString()
        };
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            using var files = new Files();
            const string name = "ExportTechnologyObjectsToDirectory";
            var source = new Target(name, release) { HashInEvidence = true };
            int approvals = 0;
            var batch = new CanonicalBatchTools.BatchTool(Declaration(name), new Dictionary<string, McpServerTool> { [name] = source },
                release, () => Binding.DeepClone().AsObject()) {
                Approve = (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); },
                CompleteApproval = (_, _) => Task.CompletedTask };
            var args = new JsonObject { ["softwarePath"] = "PLC", ["exportDir"] = Path.Combine(files.Directory, "planned") };
            var preview = await batch.InvokeAsync(Request(name, args));
            Assert.True((bool?)preview.StructuredContent?["ok"], release + ": " + preview.StructuredContent?.ToJsonString());
            Assert.Equal(1, source.Calls); Assert.Empty(source.Writes);
            envelopeShape ??= Shape(preview.StructuredContent); Assert.Equal(envelopeShape, Shape(preview.StructuredContent));
            var plan = preview.StructuredContent!["data"]!.AsObject(); var item = Assert.Single(plan["items"]!.AsArray())!.AsObject();
            Assert.Equal(name, (string?)item["tool"]);
            string actual = string.Join(",", plan.Select(p => p.Key).Order()) + ":" + string.Join(",", item.Select(p => p.Key).Order());
            shape ??= actual; Assert.Equal(shape, actual);
            Apply(args, plan);
            var applied = await batch.InvokeAsync(Request(name, args));
            Assert.True((bool?)applied.StructuredContent?["ok"], release + ": " + applied.StructuredContent?.ToJsonString());
            Assert.Single(source.Writes); Assert.Equal(1, approvals);
            appliedEnvelopeShape ??= Shape(applied.StructuredContent); Assert.Equal(appliedEnvelopeShape, Shape(applied.StructuredContent));
            var appliedPlan = applied.StructuredContent!["data"]!.AsObject();
            var appliedItem = Assert.Single(appliedPlan["items"]!.AsArray())!.AsObject();
            Assert.Equal(name, (string?)appliedItem["tool"]);
            string currentShape = string.Join(",", appliedPlan.Select(p => p.Key).Order()) + ":" + string.Join(",", appliedItem.Select(p => p.Key).Order());
            appliedShape ??= currentShape; Assert.Equal(appliedShape, currentShape);
        }
    }

    [Fact]
    public async Task Tag_table_batch_name_filter_ignores_case_like_the_native_import()
    {
        using var files = new Files(); var target = new Target("ImportPlcTagTable");
        var batch = Batch("ImportPlcTagTablesFromDirectory", target);
        var args = files.Arguments(); args["regexName"] = "^a$";
        var preview = await batch.InvokeAsync(Request(batch.ProtocolTool.Name, args));
        Assert.True((bool?)preview.StructuredContent?["ok"]);
        Assert.Single(preview.StructuredContent!["data"]!["items"]!.AsArray());
        Assert.Equal("A.xml", Path.GetFileName((string?)Assert.Single(target.Arguments)["importPath"]));
        Assert.Empty(target.Writes);
    }

    private static CanonicalBatchTools.BatchTool Batch(string name, Target target) =>
        new(Declaration(name), new Dictionary<string, McpServerTool> { [target.ProtocolTool.Name] = target }, "21", () => Binding.DeepClone().AsObject()) {
        CompleteApproval = (_, _) => Task.CompletedTask };
    private static void Apply(JsonObject args, JsonObject preview)
    {
        args["dryRun"] = false; args["confirm"] = true; args["expectedPlanHash"] = preview["planHash"]!.DeepClone(); args["expectedProjectFile"] =
            preview["expectedProjectFile"]!.DeepClone();
    }
    private static Tool Declaration(string name)
    {
        var properties = new JsonObject();
        foreach (var key in new[] { "softwarePath", "dir", "exportDir", "importPath", "folderPath", "groupPath", "regexName", "unitName" }) properties[key] =
            new JsonObject { ["type"] = "string" };
        foreach (var key in new[] { "swImportOptions", "importOptions" }) properties[key] = new JsonObject {
            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        properties["compileAfter"] = new JsonObject { ["type"] = "boolean", ["default"] = true };
        return new Tool {
            Name = name, InputSchema = JsonSerializer
                .SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties })
        };
    }
    private sealed class Target(string name, string release = "21") : McpServerTool
    {
        internal int Calls;
        internal int FailWrite;
        internal bool BlockPreview;
        internal bool HashInEvidence;
        internal Action<int>? AfterWrite;
        internal JsonArray? Inventory;
        internal List<string> Writes { get; } = new();
        internal List<JsonObject> Arguments { get; } = new();
        internal List<string?> ExportRoots { get; } = new();
        public override Tool ProtocolTool { get; } = new() {
            Name = name, InputSchema = JsonSerializer
                .SerializeToElement(new {
                    type = "object", properties = new {
                        confirm = new { type = "boolean" }, expectedProjectFile = new { type = "string" }, expectedPlanHash = new { type = "string" } }
                })
        };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Calls++; ExportRoots.Add(CanonicalBatchTools.CurrentExportRoot.Value);
            var args = JsonSerializer.SerializeToNode(request.Params!.Arguments)!.AsObject();
            Arguments.Add(args.DeepClone().AsObject());
            bool write = (bool?)args["dryRun"] == false;
            if (write) {
                var grant = GenerationStepApproval.Match(PendingApproval.Create("test", release, name, args.ToJsonString(), null, 120)); Assert.NotNull(grant);
                Assert.Null(grant.Reason); Assert.True((bool?)args["confirm"]); Assert.Equal("C:/Project.ap21", (string?)args["expectedProjectFile"]); Writes
                    .Add(Path.GetFileName((string?)args["importPath"] ?? name));
            }
            if (write) AfterWrite?.Invoke(Writes.Count);
            bool failure = write && Writes.Count == FailWrite;
            var data = Inventory == null ? new JsonObject {
                ["planHash"] = new string('a', 64), ["diagnostic"] = failure ? "original canonical diagnostic" : "preview" } : new JsonObject {
                ["items"] = Inventory.DeepClone() };
            if (HashInEvidence)
            {
                data.Remove("planHash"); data["evidence"] = new JsonObject { ["planHash"] = new string('a', 64) };
            }
            if (BlockPreview) { data["applyBlocked"] = true; data["applyBlockedReason"] = "Native semantics not verified."; }
            return ValueTask
                .FromResult(FoundationV4Result
                    .Composite(release, name, "test", data, failure ? Outcome.Failed : Outcome.Succeeded, write ? Execution.Completed : Execution.ReadOnly,
                failure ? new Error("canonical failure", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>())) : null));
        }
    }
    private sealed class Files : IDisposable
    {
        internal string Directory { get; }
        internal Files()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root
                .Parent;
            Directory = Path.Combine(root!.FullName, "bin-build", "r2-batch-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Directory);
            foreach (string name in new[] { "C", "A", "B" }) File
                .WriteAllText(Path.Combine(Directory, name + ".xml"),
                    "<Document><Engineering version=\"V21\"/><SW.Tags.PlcTagTable ID=\"0\"><AttributeList><Name>" + name
                    + "</Name></AttributeList></SW.Tags.PlcTagTable></Document>");
        }
        internal JsonObject Arguments() => new() { ["softwarePath"] = "PLC", ["dir"] = Directory };
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
