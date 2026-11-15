extern alias enginehost;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using UnsettledCalls = enginehost::TiaMcp.FoundationHost.UnsettledCalls;
using LateCalls = enginehost::TiaMcp.FoundationHost.LateCalls;
using UnknownSettlement = enginehost::TiaMcp.FoundationHost.UnknownSettlement;

namespace TiaMcp.FoundationHost.Tests;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class UnknownSettlementTests
{
    private static readonly string[] Names = { "CreatePlcTagTable", "CreatePlcTag", "CreatePlcUserConstant", "DeletePlcExternalSource",
        "ImportPlcExternalSource", "ExportPlcBlock", "ExportPlcType", "ExportPlcTagTable", "ExportPlcWatchTable", "ExportTechnologyObject",
        "ExportProjectTexts" };
    public static IEnumerable<object[]> Releases() => NoLockTests.Releases;
    public static IEnumerable<object[]> ReleasesAndOutcomes() => Releases().SelectMany(row => new[] { true, false }
        .Select(applied => new object[] { row[0], applied }));
    public static IEnumerable<object[]> RecoveryOutcomes() => Releases().SelectMany(row => new[] { "applied", "not-applied", "read-failed" }
        .Select(outcome => new object[] { row[0], outcome }));
    public static IEnumerable<object[]> Rows() => Releases().SelectMany(row => Names.SelectMany(tool => new[] { true, false }
        .Select(applied => new object[] { row[0], tool, applied })));

    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path
            .Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "settlement-" + Guid.NewGuid().ToString("N"));
        internal Files() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            string parent = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build") + Path.DirectorySeparatorChar;
            Assert.StartsWith(Path.GetFullPath(parent), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static JsonObject Arguments(string tool, string root) => new() {
        ["plc"] = "PLC_1", ["softwarePath"] = "PLC_1", ["table"] = "Tags", ["group"] = "", ["groupPath"] = "",
        ["name"] = "SettledTable", ["dataType"] = "Bool", ["address"] = "%M0.0", ["value"] = "TRUE",
        ["externalSourceName"] = "Source.scl", ["filePath"] = Path.Combine(root, tool == "ExportProjectTexts" ? "texts.xlsx" : "Source.scl"),
        ["exportPath"] = tool is "ExportPlcBlock" or "ExportPlcType" ? root : Path.Combine(root, "target.xml"),
        ["blockPath"] = "Group%20A/Block", ["typePath"] = "Group%20A/Type", ["preservePath"] = true };

    private static JsonArray Observed(string tool, JsonObject args, bool applied)
    {
        if (tool is "CreatePlcTag" or "CreatePlcUserConstant")
            return applied ? new JsonArray(new JsonObject { ["Name"] = args["name"]!.DeepClone(),
                ["Path"] = "Tags/SettledTable", ["Kind"] = tool == "CreatePlcTag" ? "tag" : "user-constant",
                ["DataType"] = args["dataType"]!.DeepClone(), ["Value"] = args[tool == "CreatePlcTag" ? "address" : "value"]!.DeepClone() }) : new();
        bool present = applied != (tool == "DeletePlcExternalSource");
        return present ? new JsonArray(tool == "CreatePlcTagTable" ? "SettledTable" : "Source.scl") : new();
    }

    [Theory, MemberData(nameof(Rows))]
    public async Task Each_row_settles_only_the_directions_its_evidence_proves(string release, string tool, bool applied)
    {
        using var files = new Files();
        var args = Arguments(tool, files.Root);
        var calls = new UnsettledCalls();
        var results = new LateCalls();
        string digest = UnsettledCalls.WriteDigest(tool, args, "C:/fixture.ap" + release);
        calls.Record("unknown", tool, digest, DateTimeOffset.UtcNow, args);
        var target = Assert.Single(calls.Snapshot()).Target!;
        int reads = 0;
        if (target.ReadOperation == null && applied)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.Scope)!);
            File.WriteAllText(target.Scope, "exported bytes");
        }
        var settlement = await UnknownSettlement.Run(calls, results, "unknown", (operation, arguments) => {
            reads++;
            Assert.Equal(target.ReadOperation, operation);
            Assert.Equal(tool is "CreatePlcTag" or "CreatePlcUserConstant"
                ? "{\"plc\":\"PLC_1\",\"table\":\"Tags\"}" : "{\"softwarePath\":\"PLC_1\"}", arguments.ToJsonString());
            return Task.FromResult<JsonNode?>(Observed(tool, args, applied));
        });
        Assert.Equal(target.ReadOperation == null ? 0 : 1, reads);
        if (tool == "ImportPlcExternalSource" && !applied || tool == "ExportProjectTexts" && applied)
        {
            Assert.Null(settlement);
            Assert.Single(calls.Snapshot());
            Assert.Empty(calls.SettledJson());
            Assert.NotNull(calls.FindWrite(digest));
            return;
        }
        Assert.Equal(applied ? "applied" : "not-applied", settlement?.Outcome);
        Assert.Empty(calls.Snapshot());
        Assert.Equal(applied, calls.FindWrite(digest) != null);
        Assert.Equal(applied ? "applied" : "not-applied", (string?)Assert.Single(calls.SettledJson())!["outcome"]);
        Assert.Equal("unknown", (string?)Assert.Single(calls.SettledJson())!["requestId"]);
    }

    private sealed class Worker(string release) : IFoundationSessionWorker
    {
        internal readonly List<string> Operations = new();
        internal bool Applied = true, ReadFails, InCall, LaneHeld, DetachOnWrite;
        public bool Faulted { get; set; }
        internal string Project => "C:/fixture.ap" + release;
        public string ApprovalIdentity => new JsonObject { ["processId"] = 123, ["projectPath"] = Project }.ToJsonString();
        public bool Bundled => false;
        public bool SharedSession => false;
        public UnsettledCalls Unsettled { get; } = new();
        public LateCalls LateResults { get; } = new();
        public JsonObject? Running => LateResults.Running;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token)
        { Assert.False(LaneHeld); LaneHeld = true; return Task.FromResult<IDisposable?>(new Lane(this)); }
        public void ActivateLane(IDisposable? lane) { }
        private sealed class Lane(Worker owner) : IDisposable { public void Dispose() => owner.LaneHeld = false; }
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Assert.True(LaneHeld);
            Assert.False(InCall);
            Operations.Add(operation);
            InCall = true;
            try
            {
                if (operation == "ReadTagTableNames")
                {
                    Assert.False(Faulted);
                    if (ReadFails) throw new IOException("Synthetic read-back failure.");
                    return Task.FromResult<JsonNode?>(Applied ? new JsonArray("SettledTable") : new JsonArray());
                }
                if (operation == "SaveProject")
                    return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = true, ["ProjectFile"] = Project });
                if (DetachOnWrite) LateCalls.Detach(new JsonObject { ["requestId"] = FoundationWorkerRequest.CorrelationId,
                    ["tool"] = "CreatePlcTagTable", ["sinceUtc"] = DateTimeOffset.UtcNow.ToString("O") });
                throw new WorkerOperationException("Synthetic unknown write.", -32603, "unknown");
            }
            finally { InCall = false; }
        }
        public void Dispose() { }
    }

    private static FoundationV4Tool Tool(Worker worker, string release, string name)
        => new(new FoundationTool(FoundationTools.Definitions.Single(row => row.Name == name), worker), release, () => new(false, 1));
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) => new(
        DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
    private static JsonObject CreateArguments(Worker worker) => new() { ["plc"] = "PLC_1", ["group"] = "", ["name"] = "SettledTable",
        ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = worker.Project };

    private sealed class TextExportWorker(string release, bool applied) : IFoundationSessionWorker
    {
        internal int Exports;
        public string ApprovalIdentity => new JsonObject { ["processId"] = 123, ["projectPath"] = "C:/fixture.ap" + release }.ToJsonString();
        public bool Bundled => false;
        public bool SharedSession => false;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/fixture.ap" + release });
            if (operation == "SaveProject")
                return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = true, ["ProjectFile"] = "C:/fixture.ap" + release });
            Assert.Equal("engineering-services.ExecuteEngineeringServices", operation);
            Assert.Equal("ExportProjectTexts", (string?)arguments["request"]?["Operation"]);
            Exports++;
            // An XLSX ZIP local header interrupted before its remaining fields and content.
            if (applied) File.WriteAllBytes((string)arguments["request"]!["FilePath"]!, new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x14, 0x00 });
            var failure = new WorkerOperationException("Synthetic unknown export.", -32603, "unknown");
            failure.Data["workerOutcomeUnknown"] = true;
            failure.Data["foundationRequestSent"] = true;
            throw failure;
        }
        public void Dispose() { }
    }

    [Theory, MemberData(nameof(ReleasesAndOutcomes))]
    public async Task Registered_project_text_export_settles_on_the_ported_route(string release, bool applied)
    {
        using var files = new Files();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(contextWorker, release);
        using var worker = new TextExportWorker(release, applied);
        var previous = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        try
        {
            new TiaOpenness.Shared.ApprovalSettings(false, 1).Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(item => item.ProtocolTool.Name == "ExportProjectTexts");
            var result = await tool.InvokeAsync(Request("ExportProjectTexts", new() { ["filePath"] = Path.Combine(files.Root, "texts.xlsx"),
                ["sourceCulture"] = "en-US", ["targetCulture"] = "zh-CN", ["dryRun"] = false }));
            var body = result.StructuredContent!;
            Assert.True((string?)body["error"]?["code"] == "OUTCOME_UNKNOWN", body.ToJsonString());
            Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
            Assert.Equal(1, worker.Exports);
            if (applied)
            {
                Assert.Null(body["data"]?["settlement"]);
                Assert.Single(UnsettledCalls.For(worker).Snapshot());
                Assert.NotNull(UnsettledCalls.For(worker).Snapshot()[0].Digest);
                var save = new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(row => row.Name == "SaveProject"), worker),
                    release, () => new(false, 1));
                var saved = await save.InvokeAsync(Request("SaveProject", new() { ["dryRun"] = false, ["confirm"] = true,
                    ["expectedProjectFile"] = "C:/fixture.ap" + release }));
                Assert.Contains(saved.StructuredContent!["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "UNSETTLED_CALLS");
                return;
            }
            Assert.Equal("not-applied", (string?)body["data"]?["settlement"]?["outcome"]);
            Assert.Empty(UnsettledCalls.For(worker).Snapshot());
        }
        finally { previous.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath); }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Envelope_stays_unknown_and_carries_the_settlement(string release)
    {
        using var worker = new Worker(release);
        var result = await Tool(worker, release, "CreatePlcTagTable").InvokeAsync(Request("CreatePlcTagTable", CreateArguments(worker)));
        Assert.True(result.IsError);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("applied", (string?)result.StructuredContent?["data"]?["settlement"]?["outcome"]);
        Assert.Equal(new[] { "CreateTagTable", "ReadTagTableNames" }, worker.Operations);
        Assert.Empty(worker.Unsettled.Snapshot());
        string id = (string)result.StructuredContent!["meta"]!["requestId"]!;
        Assert.Equal("applied", (string?)worker.LateResults.Read(release, id, worker.Unsettled)?.StructuredContent?["data"]?["settlement"]?["outcome"]);
        Assert.Single(SharedSessionState.For(worker).Snapshot(release)["settled"]!.AsArray());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_settled_call_leaves_the_list_and_save_stops_warning(string release)
    {
        using var worker = new Worker(release);
        await Tool(worker, release, "CreatePlcTagTable").InvokeAsync(Request("CreatePlcTagTable", CreateArguments(worker)));
        var result = await Tool(worker, release, "SaveProject").InvokeAsync(Request("SaveProject", new() {
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = worker.Project }));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.DoesNotContain(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "UNSETTLED_CALLS");
        Assert.Empty(worker.Unsettled.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_failed_read_back_leaves_it_unsettled(string release)
    {
        using var worker = new Worker(release) { ReadFails = true };
        var result = await Tool(worker, release, "CreatePlcTagTable").InvokeAsync(Request("CreatePlcTagTable", CreateArguments(worker)));
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Null(result.StructuredContent?["data"]?["settlement"]);
        Assert.Single(worker.Unsettled.Snapshot());
        Assert.Empty(worker.Unsettled.SettledJson());
        Assert.Equal(new[] { "CreateTagTable", "ReadTagTableNames" }, worker.Operations);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task The_write_is_never_sent_again(string release)
    {
        using var worker = new Worker(release);
        var tool = Tool(worker, release, "CreatePlcTagTable");
        var args = CreateArguments(worker);
        await tool.InvokeAsync(Request("CreatePlcTagTable", args));
        var refused = await tool.InvokeAsync(Request("CreatePlcTagTable", args));
        Assert.Equal("same-call-unsettled", (string?)refused.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Equal(new[] { "CreateTagTable", "ReadTagTableNames" }, worker.Operations);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Applied_keeps_the_guard_until_the_caller_reads_the_result(string release)
    {
        using var worker = new Worker(release) { DetachOnWrite = true };
        var tool = Tool(worker, release, "CreatePlcTagTable");
        var args = CreateArguments(worker);
        using var invocation = worker.LateResults.CreateInvocation("CreatePlcTagTable", "outer", CancellationToken.None);
        CallToolResult result;
        using (invocation.Enter())
        {
            result = await tool.InvokeAsync(Request("CreatePlcTagTable", args));
            invocation.Complete(result, worker.Unsettled);
        }
        var refused = await tool.InvokeAsync(Request("CreatePlcTagTable", args));
        Assert.Null(worker.LateResults.Running);
        Assert.Equal("same-call-unsettled", (string?)refused.StructuredContent?["error"]?["details"]?["condition"]);
        string id = (string)result.StructuredContent!["meta"]!["requestId"]!;
        Assert.Equal("applied", (string?)worker.LateResults.Read(release, id, worker.Unsettled)?.StructuredContent?["data"]?["settlement"]?["outcome"]);
        string digest = UnsettledCalls.WriteDigest("CreatePlcTagTable", args, worker.Project);
        Assert.Null(worker.Unsettled.FindWrite(digest));
        Assert.Null(worker.LateResults.FindUnread(digest));
        var next = await tool.InvokeAsync(Request("CreatePlcTagTable", args));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)next.StructuredContent?["error"]?["code"]);
        Assert.Equal(2, worker.Operations.Count(operation => operation == "CreateTagTable"));
    }

    [Theory, MemberData(nameof(ReleasesAndOutcomes))]
    public async Task Settlement_decorates_evicted_summaries_and_preserves_the_correct_guard(string release, bool applied)
    {
        using var files = new Files();
        var calls = new UnsettledCalls();
        var results = new LateCalls();
        calls.Record("old", "CreatePlcTagTable", "old-digest", DateTimeOffset.UtcNow, Arguments("CreatePlcTagTable", files.Root));
        results.RecordUnknown("old", "CreatePlcTagTable", "old-digest", FoundationV4Result.LateFailure(release, "CreatePlcTagTable", "old"));
        for (int i = 0; i < 17; i++)
            results.RecordUnknown("other-" + i, "SaveProject", "digest-" + i, FoundationV4Result.LateFailure(release, "SaveProject", "other-" + i));
        await UnknownSettlement.Run(calls, results, "old", (_, _) => Task.FromResult<JsonNode?>(applied ? new JsonArray("SettledTable") : new JsonArray()));
        Assert.Equal(applied, calls.FindWrite("old-digest") != null);
        Assert.Equal(applied, results.FindUnread("old-digest") != null);
        var summary = results.Read(release, "old", calls)!.StructuredContent!;
        Assert.True((bool?)summary["data"]?["fullResultEvicted"]);
        Assert.Equal("unknown", (string?)summary["meta"]?["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)summary["error"]?["code"]);
        Assert.Equal(applied ? "applied" : "not-applied", (string?)summary["data"]?["settlement"]?["outcome"]);
        Assert.Null(results.FindUnread("old-digest"));
        Assert.Null(calls.FindWrite("old-digest"));
        Assert.Empty(calls.Snapshot());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task One_not_applied_child_does_not_release_the_batch_guard(string release)
    {
        using var files = new Files();
        var calls = new UnsettledCalls();
        var results = new LateCalls();
        var child = FoundationV4Result.LateFailure(release, "CreatePlcTagTable", "child");
        var batch = FoundationV4Result.LateFailure(release, "ApplyToolBatch", "batch");
        batch.StructuredContent!["data"] = new JsonObject { ["items"] = new JsonArray(new JsonObject {
            ["result"] = child.StructuredContent!.DeepClone() }) };
        using var invocation = results.CreateInvocation("ApplyToolBatch", "batch", CancellationToken.None);
        using (invocation.Enter())
        {
            LateCalls.IdentifyWrite("batch-digest");
            calls.Record("child", "CreatePlcTagTable", "child-digest", DateTimeOffset.UtcNow, Arguments("CreatePlcTagTable", files.Root));
            invocation.Complete(batch, calls);
        }
        await UnknownSettlement.Run(calls, results, "child", (_, _) => Task.FromResult<JsonNode?>(new JsonArray()));
        Assert.Equal("batch", results.FindUnread("batch-digest"));
        Assert.Equal("batch", calls.FindWrite("batch-digest")?.RequestId);
        var original = results.Read(release, "child", calls)!.StructuredContent!;
        Assert.Equal("unknown", (string?)original["meta"]?["outcome"]);
        Assert.Null(original["data"]?["settlement"]);
        Assert.Equal("not-applied", (string?)original["data"]?["items"]?[0]?["result"]?["data"]?["settlement"]?["outcome"]);
        Assert.Null(batch.StructuredContent!["data"]?["items"]?[0]?["result"]?["data"]?["settlement"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Not_applied_releases_the_identical_write_guard(string release)
    {
        using var worker = new Worker(release) { Applied = false, DetachOnWrite = true };
        var tool = Tool(worker, release, "CreatePlcTagTable");
        var args = CreateArguments(worker);
        string digest = UnsettledCalls.WriteDigest("CreatePlcTagTable", args, worker.Project);
        using var invocation = worker.LateResults.CreateInvocation("CreatePlcTagTable", "outer", CancellationToken.None);
        CallToolResult result;
        using (invocation.Enter())
        {
            result = await tool.InvokeAsync(Request("CreatePlcTagTable", args));
            invocation.Complete(result, worker.Unsettled);
        }
        Assert.Equal("not-applied", (string?)result.StructuredContent?["data"]?["settlement"]?["outcome"]);
        Assert.Null(worker.LateResults.FindUnread(digest));
        Assert.Null(worker.LateResults.Running);
        Assert.Null(worker.Unsettled.FindWrite(digest));
        Assert.Empty(worker.Unsettled.Snapshot());
        worker.Applied = true;
        var next = await tool.InvokeAsync(Request("CreatePlcTagTable", args));
        Assert.Equal("applied", (string?)next.StructuredContent?["data"]?["settlement"]?["outcome"]);
        Assert.Equal(new[] { "CreateTagTable", "ReadTagTableNames", "CreateTagTable", "ReadTagTableNames" }, worker.Operations);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Tool_with_no_row_stays_listed(string release)
    {
        using var worker = new Worker(release);
        var result = await Tool(worker, release, "CompilePlcSoftware").InvokeAsync(Request("CompilePlcSoftware", new() {
            ["softwarePath"] = "PLC_1", ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = worker.Project }));
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Single(worker.Unsettled.Snapshot());
        Assert.Null(result.StructuredContent?["data"]?["settlement"]);
        Assert.Equal(new[] { "CompileSoftware" }, worker.Operations);
    }

    [Theory]
    [InlineData("ImportPlcBlock", "overwrite", true)]
    [InlineData("ImportPlcType", "overwrite", false)]
    [InlineData("ImportPlcExternalSource", "overwrite", true)]
    [InlineData("CreatePlcTagTable", "group", "Group")]
    [InlineData("CompilePlcSoftware", "dryRun", false)]
    public async Task No_row_means_no_read(string tool, string key, object value)
    {
        using var files = new Files();
        var args = Arguments(tool, files.Root);
        args[key] = JsonSerializer.SerializeToNode(value);
        var calls = new UnsettledCalls();
        calls.Record("unknown", tool, "digest", DateTimeOffset.UtcNow, args);
        var result = await UnknownSettlement.Run(calls, new(), "unknown", (_, _) => throw new Exception("No read is allowed."));
        Assert.Null(result);
        Assert.Single(calls.Snapshot());
        Assert.Null(Assert.Single(calls.Snapshot()).Target);
    }

    [Theory]
    [InlineData("CreatePlcTag", "DataType", "Int")]
    [InlineData("CreatePlcTag", "Value", "%M0.1")]
    [InlineData("CreatePlcUserConstant", "DataType", "Int")]
    [InlineData("CreatePlcUserConstant", "Value", "FALSE")]
    [InlineData("CreatePlcTag", "Path", "Other/SettledTable")]
    public async Task A_conflicting_declaration_cannot_settle(string tool, string field, string value)
    {
        using var files = new Files();
        var args = Arguments(tool, files.Root);
        var observed = Observed(tool, args, true);
        observed[0]![field] = value;
        var calls = new UnsettledCalls();
        calls.Record("unknown", tool, "digest", DateTimeOffset.UtcNow, args);
        Assert.Null(await UnknownSettlement.Run(calls, new(), "unknown", (_, _) => Task.FromResult<JsonNode?>(observed)));
        Assert.Single(calls.Snapshot());
    }

    [Fact]
    public async Task Empty_file_and_malformed_reads_supply_no_definite_evidence()
    {
        using var files = new Files();
        var args = Arguments("ExportPlcTagTable", files.Root);
        File.WriteAllBytes((string)args["exportPath"]!, Array.Empty<byte>());
        var calls = new UnsettledCalls();
        calls.Record("file", "ExportPlcTagTable", "file-digest", DateTimeOffset.UtcNow, args);
        Assert.Null(await UnknownSettlement.Run(calls, new(), "file", (_, _) => throw new Exception("No worker call.")));
        string directory = Path.Combine(files.Root, "directory.xml");
        Directory.CreateDirectory(directory);
        args["exportPath"] = directory;
        calls.Record("directory", "ExportPlcTagTable", "directory-digest", DateTimeOffset.UtcNow, args);
        Assert.Null(await UnknownSettlement.Run(calls, new(), "directory", (_, _) => throw new Exception("No worker call.")));
        foreach (JsonNode? observed in new JsonNode?[] { null, new JsonObject { ["Items"] = new JsonArray() },
            new JsonArray("SettledTable", "SettledTable"), new JsonArray(" "), new JsonArray(new JsonObject { ["Name"] = "Other" }) })
        {
            calls.Record("read", "CreatePlcTagTable", "read-digest", DateTimeOffset.UtcNow, Arguments("CreatePlcTagTable", files.Root));
            Assert.Null(await UnknownSettlement.Run(calls, new(), "read", (_, _) => Task.FromResult(observed)));
        }
        Assert.Equal(3, calls.Snapshot().Count);
        Assert.Empty(calls.SettledJson());
    }

    [Fact]
    public async Task Targets_are_copied_without_content_and_project_changes_clear_settlements()
    {
        using var files = new Files();
        var args = Arguments("ImportPlcExternalSource", files.Root);
        args["sourceText"] = "private source content";
        var calls = new UnsettledCalls();
        calls.ProjectChanged("C:/fixture.ap21");
        calls.Record("unknown", "ImportPlcExternalSource", "digest", DateTimeOffset.UtcNow, args);
        args["filePath"] = "other.scl";
        var target = Assert.Single(calls.Snapshot()).Target;
        Assert.Equal("Source.scl", target?.Name);
        Assert.DoesNotContain("private source content", JsonSerializer.Serialize(target));
        await UnknownSettlement.Run(calls, new(), "unknown", (_, _) => Task.FromResult<JsonNode?>(new JsonArray("Source.scl")));
        Assert.Single(calls.SettledJson());
        calls.ProjectChanged("c:/fixture.ap21");
        Assert.Single(calls.SettledJson());
        calls.ProjectChanged("C:/other.ap21");
        Assert.Empty(calls.SettledJson());
        Assert.Null(calls.FindWrite("digest"));
        calls.Record("another", "CreatePlcTagTable", "digest", DateTimeOffset.UtcNow, Arguments("CreatePlcTagTable", files.Root));
        await UnknownSettlement.Run(calls, new(), "another", (_, _) => Task.FromResult<JsonNode?>(new JsonArray()));
        calls.Clear();
        Assert.Empty(calls.SettledJson());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task A_faulted_worker_defers_the_read(string release)
    {
        using var worker = new Worker(release) { Faulted = true };
        var result = await Tool(worker, release, "CreatePlcTagTable").InvokeAsync(Request("CreatePlcTagTable", CreateArguments(worker)));
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal(new[] { "CreateTagTable" }, worker.Operations);
        Assert.Single(worker.Unsettled.Snapshot());
        Assert.Empty(worker.Unsettled.SettledJson());
    }

    [Theory, MemberData(nameof(RecoveryOutcomes))]
    public async Task Read_back_runs_after_the_rebind_when_the_worker_died(string release, string outcome)
    {
        using var files = new Files();
        string trace = Path.Combine(files.Root, "trace.jsonl"), facts = Path.Combine(files.Root, "processes.json");
        string project = "C:/fixture.ap" + release;
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(facts, new JsonArray(new JsonObject { ["ProcessId"] = Environment.ProcessId,
            ["ProcessStartUtc"] = current.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["ProjectPath"] = project, ["AttachedSessions"] = 0 }).ToJsonString());
        var environment = new Dictionary<string, string?>();
        void Set(string key, string value)
        { environment[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, value); }
        Set("TIA_MCP_FIXTURE_RECOVERY_TRACE", trace);
        Set("TIA_MCP_FIXTURE_RECOVERY_PROCESSES", facts);
        Set("TIA_MCP_PLC_WORKER_FAULT", "crash");
        try
        {
            string exe = TestPaths.WorkerFixture("TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
            TestPaths.WriteFixtureFile(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            using var worker = new WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true) { Bundled = false };
            using var budgets = new ProductionWorkerBudgets(worker);
            await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
            await worker.Call("BindProject", new() { ["projectName"] = "fixture", ["expectedProjectFile"] = project }, CancellationToken.None);
            using var request = FoundationWorkerRequest.Use("lost-create", "CreatePlcTagTable");
            await worker.Call("FaultFixtureArm", new() { ["method"] = "adapter.CreateTagTable" }, CancellationToken.None);
            await Assert.ThrowsAnyAsync<Exception>(() => worker.Call("CreateTagTable", new() { ["dryRun"] = false }, CancellationToken.None));
            Assert.True(worker.Faulted);
            var args = Arguments("CreatePlcTagTable", files.Root);
            if (outcome == "not-applied") args["name"] = "AbsentTable";
            worker.Unsettled.Record("lost-create", "CreatePlcTagTable", "digest", DateTimeOffset.UtcNow, args);
            worker.LateResults.RecordUnknown("lost-create", "CreatePlcTagTable", "digest",
                FoundationV4Result.LateFailure(release, "CreatePlcTagTable", "lost-create"));
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", null);
            if (outcome == "read-failed")
            {
                Set("TIA_MCP_FIXTURE_SETTLEMENT_FAULT", "crash");
                var refused = await Assert.ThrowsAsync<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
                    worker.Call("SaveProject", new() { ["dryRun"] = false }, CancellationToken.None));
                Assert.Equal("tia-not-responding", refused.Condition);
                Assert.False((bool?)refused.Data["foundationRequestSent"]);
            }
            else await worker.Call("ReadBlocks", new(), CancellationToken.None);
            var methods = File.ReadAllLines(trace).Select(line => (string)JsonNode.Parse(line)!["method"]!).ToArray();
            Assert.Equal(1, methods.Count(method => method == "adapter.CreateTagTable"));
            Assert.Equal(1, methods.Count(method => method == "adapter.ReadTagTableNames"));
            Assert.True(Array.LastIndexOf(methods, "adapter.BindProject") < Array.IndexOf(methods, "adapter.ReadTagTableNames"));
            if (outcome == "read-failed")
            {
                Assert.DoesNotContain("adapter.ReadBlocks", methods);
                Assert.DoesNotContain("adapter.SaveProject", methods);
                Assert.Single(worker.Unsettled.Snapshot());
                Assert.Empty(worker.Unsettled.SettledJson());
                Assert.Equal("unknown", (string?)worker.LateResults.Read(release, "lost-create", worker.Unsettled)?.StructuredContent?["meta"]?["outcome"]);
                return;
            }
            Assert.True(Array.IndexOf(methods, "adapter.ReadTagTableNames") < Array.IndexOf(methods, "adapter.ReadBlocks"));
            Assert.Empty(worker.Unsettled.Snapshot());
            Assert.Equal(outcome, (string?)Assert.Single(worker.Unsettled.SettledJson())!["outcome"]);
            Assert.Equal(outcome == "applied", worker.LateResults.FindUnread("digest") != null);
            Assert.Equal(outcome == "applied", worker.Unsettled.FindWrite("digest") != null);
            var result = worker.LateResults.Read(release, "lost-create", worker.Unsettled)!;
            Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
            Assert.Equal(outcome, (string?)result.StructuredContent?["data"]?["settlement"]?["outcome"]);
        }
        finally { foreach (var variable in environment) Environment.SetEnvironmentVariable(variable.Key, variable.Value); }
    }
}
