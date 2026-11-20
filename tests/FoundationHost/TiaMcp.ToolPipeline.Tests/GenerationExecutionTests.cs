using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.Generation;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class GenerationExecutionTests : IDisposable
{
    // check-all fingerprints runtime outputs concurrently; mutable evidence must live outside those inputs.
    private readonly string root;
    private readonly string? previousDataDirectory;
    private readonly string repository;
    private int writes, approvals;
    private string? textTreeFirmware;
    private readonly JsonArray softwarePaths = new();
    private string bindingGeneration = "1";
    private string? approvalReason, failure;
    private int stopAt = 2;
    private bool changeIdentity, mismatch, incomplete, noPortal, missingTool;
    private DateTimeOffset clock = DateTimeOffset.UtcNow;
    private bool expireAtApproval, advanceAfterWrite, cancelBetweenSteps;
    private readonly StandardPackage package;
    public GenerationExecutionTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))) directory = directory.Parent;
        repository = directory!.FullName;
        root = Path.Combine(repository, "bin-build", "generation-execution", Guid.NewGuid().ToString("N"));
        previousDataDirectory = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.Combine(root, "data"));
        package = new StandardPackageStore(Path.Combine(repository, "templates", "standards"), Path.Combine(root, "standards")).Load("tiamcp.basic", "1.0.0");
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previousDataDirectory);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "bin-build", "generation-execution")) + Path.DirectorySeparatorChar,
            Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    [Fact]
    public void Mutable_evidence_is_outside_the_runtime_input_inventory()
    {
        Assert.False(Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(root, "data"), Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY"));
        var session = Session("19");
        var result = Apply(session, Plan(session, "19"));
        string run = (string)result.StructuredContent!["data"]!["runId"]!;
        Assert.True(File.Exists(Path.Combine(root, "runs", run + ".jsonl")));
        Assert.Equal("succeeded", (string?)new GenerationRunStore(Path.Combine(root, "runs")).Read(run)["status"]);
    }
    private JsonObject Machine(string release)
    {
        var machine = JsonNode.Parse(Encoding.UTF8.GetString(package.ReadFile("examples/machine.json")))!.AsObject();
        machine["target"]!["release"] = release;
        machine["target"]!["project"] = new JsonObject { ["mode"] = "existing", ["projectIdentity"] = "C:/Projects/Test.ap" + release, ["softwarePath"] = "PLC1" };
        machine["stations"] = new JsonArray(machine["stations"]![0]!.DeepClone());
        return machine;
    }
    private GenerationExecutionSession Session(string release)
    {
        EngineHostConfiguration.ReleaseKey = release;
        EngineHostConfiguration.Worker = new FixtureWorker();
        EngineHostConfiguration.Current.Catalog = new FixtureCatalog();
        return new GenerationExecutionSession(() => root, (name, args, check) => {
            if (name == "GetSessionState") return Result(name, new JsonObject { ["sessionState"] = new JsonObject {
                ["connected"] = !noPortal, ["binding"] = new JsonObject { ["identity"] = new JsonObject {
                ["projectPath"] = noPortal ? null : "C:/Projects/Test.ap" + release, ["processId"] = noPortal ? null : 1234,
                ["processStartUtc"] = "2026-10-10T00:00:00Z", ["generation"] = bindingGeneration } } } });
            if (name == "GetOpennessWorkerStatus") return SessionToolContract.Map(name, new ResponseMessage {
                Meta = new JsonObject { ["worker"] = new JsonObject { ["binding"] = null }, ["operationSuccess"] = true } }, false, false);
            if (name == "GetProjectTree" && textTreeFirmware != null) return Result(name, new JsonObject {
                ["value"] = "Project\r\n  Device: PLC1\r\n    Item: CPU\r\n      PlcSoftware: PLC1 [softwarePath=devices/PLC1/CPU]\r\n"
                    + "    Item: HMI\r\n      HmiTarget: RT [softwarePath=devices/PLC1/HMI]\r\n"
            });
            if (name == "GetDeviceItemInfo")
            {
                Assert.Equal("devices/PLC1/CPU", (string?)args["deviceItemPath"]);
                return Result(name, new JsonObject { ["attributes"] = new JsonArray(
                    new JsonObject { ["name"] = "OrderNumber", ["value"] = "6ES7 513-1AL02-0AB0" },
                    new JsonObject { ["name"] = "FirmwareVersion", ["value"] = textTreeFirmware }) });
            }
            if (name == "GetProjectTree") return Result(name, new JsonObject { ["devices"] = new JsonArray(new JsonObject { ["kind"] = "device", ["name"] = "PLC1", ["articleNumber"] = "6ES7 513-1AL02-0AB0", ["version"] = "V2.0" }) });
            if (name == "GetSoftwareTree") return Result(name, new JsonObject { ["paths"] = softwarePaths.DeepClone() }, incomplete ? "partial" : "complete");
            if (name.StartsWith("List", StringComparison.Ordinal)) return Result(name, new JsonObject { ["items"] = new JsonArray() });
            if (check?.Invoke() is { } refused) return refused;
            if (mismatch)
            {
                var request = PendingApproval.Create("foundation", release, name, "{\"changed\":true}", "{}", 120);
                Assert.Equal("denied", GenerationStepApproval.Match(request)!.Reason);
                return McpServer.V4Reject(name, TiaMcp.Logic.V4.HostBehavior.Confirmation("denied", request.PlanHash, request.RequestId));
            }
            var delegated = GenerationStepApproval.Match(PendingApproval.Create("foundation", release, name, args.ToJsonString(), "{}", 120));
            Assert.NotNull(delegated); Assert.Null(delegated!.Reason);
            writes++;
            if (advanceAfterWrite && writes == 1) clock = clock.AddMinutes(11);
            if (changeIdentity && writes == 1) bindingGeneration = "2";
            if (writes == stopAt && failure == "throw") throw new IOException("Injected dispatch failure");
            if (writes == stopAt && failure != null) return McpServer.V4Result(name, new JsonObject(),
                new TiaMcp.Logic.V4.Error("Injected failure", new TiaMcp.Logic.V4.PreconditionFailedDetails("fixture", null)),
                failure == "unknown" ? TiaMcp.Logic.V4.Outcome.Unknown : TiaMcp.Logic.V4.Outcome.Failed,
                failure == "unknown" ? TiaMcp.Logic.V4.Execution.Unknown : TiaMcp.Logic.V4.Execution.Completed,
                failure == "unknown" ? TiaMcp.Logic.V4.Completeness.Unknown : TiaMcp.Logic.V4.Completeness.None);
            return Result(name, new JsonObject());
        }, name => Descriptor(name), (pending, _, _) => {
            approvals++; pending.Validate(); Assert.Equal("plan", pending.Kind); Assert.NotNull(pending.PlanJson);
            Assert.True(pending.Operations.Length > 1);
            if (expireAtApproval) clock = clock.AddMinutes(11);
            return Task.FromResult(new ApprovalOutcome(pending, false, approvalReason));
        }, () => clock, () => cancelBetweenSteps && writes > 0);
    }
    private sealed class FixtureCatalog : IToolCatalogView
    {
        public IReadOnlyDictionary<string, ToolDescriptor> All { get; } = new Dictionary<string, ToolDescriptor>();
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<ToolDescriptor> Lite => Array.Empty<ToolDescriptor>();
        public JsonArray BehaviorCapabilities { get; } = new();
        public ToolDescriptor? Find(string name, bool includeUnavailable = false) => null;
    }
    private sealed class FixtureWorker : IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(new JsonObject());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
    private ToolDescriptor? Descriptor(string name)
    {
        if (name is "CreateDevice" or "CreatePlcBlockGroup" or "MovePlcBlockToGroup")
        {
            var owner = name == "CreateDevice" ? typeof(TiaMcpServer.ModelContextProtocol.HardwareDevicesTools)
                : typeof(TiaMcpServer.ModelContextProtocol.PlcOrganisationTools);
            string member = name == "MovePlcBlockToGroup" ? "MoveBlockToGroupV4" : name + "V4";
            TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata.Create(name, owner.GetMethod(member)!,
                _ => throw new InvalidOperationException("Metadata only."), out var current, EngineHostConfiguration.ReleaseKey);
            return current;
        }

        // This suite injects ordinary step outcomes; the staged suite drives the real source policies.
        if (name == "ImportPlcExternalSource" || name == "GenerateBlocksFromExternalSource") return null;
        if (missingTool && name == "CreatePlcTag") return null;
        var schemas = BaselineSchemas(Path.Combine(repository, "manifest", "contracts", "v4", "baseline", EngineHostConfiguration.ReleaseKey + ".json"));
        if (!schemas.TryGetValue(name, out var schema)) return null;
        return new ToolDescriptor(name, "", new Tool { Name = name, InputSchema = schema }, null, "", Array.Empty<ToolParameterDescriptor>(), new ToolDryRunDescriptor(true, true), null, "foundation");
    }
    // The baselines are 10-21 MB. Parsing one on every plan and execution step allocated about 850 GB per shard run and drove
    // thousands of background GCs, the load under which the test host crashed with an AccessViolationException (see
    // bin-build/refactor/engine-host-av). Each file is parsed once; duplicate tool names still fail.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, JsonElement>> Schemas = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, JsonElement> BaselineSchemas(string path) => Schemas.GetOrAdd(path, file =>
        JsonNode.Parse(File.ReadAllText(file))!["tools"]!.AsArray().ToDictionary(t => (string)t!["name"]!, t => JsonSerializer.SerializeToElement(t!["inputSchema"]), StringComparer.Ordinal));
    private static CallToolResult Result(string name, JsonObject data, string completeness = "complete")
    {
        var result = McpServer.V4Result(name, data);
        result.StructuredContent!["meta"]!["completeness"] = completeness;
        return result;
    }
    private JsonObject Plan(GenerationExecutionSession session, string release)
        => session.Plan(package, Machine(release).ToJsonString()).StructuredContent!["data"]!["plan"]!.AsObject();
    private static CallToolResult Apply(GenerationExecutionSession session, JsonObject plan, bool preview = false)
        => session.Apply((string)plan["planId"]!, (string)plan["planHash"]!, (string)plan["target"]!["projectIdentity"]!, null, preview);
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(r => new object[] { r });

    [Fact]
    public void Unit_inventory_keeps_block_and_type_identities_separate_from_root_objects()
    {
        var session = Session("17");
        var empty = Plan(session, "17");
        const string unit = "Cell";
        void Row(string kind, string name, string rootPath, string relative, string selector)
        {
            softwarePaths.Add(new JsonObject { ["kind"] = kind, ["name"] = name, ["unitName"] = unit,
                ["path"] = "PLC1/units/Cell" + rootPath, ["objectPath"] = relative, ["selectorParameter"] = selector });
        }
        Row("unit", unit, "", unit, "unitName");
        Row("block-group", "Library", "/blocks/Library", "Library", "groupPath");
        Row("block", "FB_MotorDol", "/blocks/Library/FB_MotorDol", "Library/FB_MotorDol", "blockPath");
        Row("type-group", "Library", "/types/Library", "Library", "groupPath");
        Row("type", "UDT_MotorDolStatus", "/types/Library/UDT_MotorDolStatus", "Library/UDT_MotorDolStatus", "typePath");
        Row("tag-group", "IO", "/tag-tables/IO", "IO", "groupPath");
        Row("tag-table", "Signals", "/tag-tables/IO/Signals", "IO/Signals", "table");
        Row("source-group", "Sources", "/external-sources/Sources", "Sources", "groupPath");
        Row("external-source", "Program", "/external-sources/Sources/Program", "Sources/Program", "externalSourceName");
        var scoped = Plan(session, "17");
        Assert.Equal(new[] { "block:units/Cell/Library/FB_MotorDol", "type:units/Cell/Library/UDT_MotorDolStatus" },
            scoped["observed"]!["PLC1"]!["objectKeys"]!.AsArray().Select(n => (string?)n));
        Assert.True(JsonNode.DeepEquals(empty["conflicts"], scoped["conflicts"]));
        softwarePaths.Add(new JsonObject { ["kind"] = "block", ["name"] = "FB_MotorDol", ["objectPath"] = "FB_MotorDol",
            ["path"] = "PLC1/blocks/FB_MotorDol", ["selectorParameter"] = "blockPath" });
        var both = Plan(session, "17");
        Assert.Contains("block:FB_MotorDol", both["observed"]!["PLC1"]!["objectKeys"]!.AsArray().Select(n => (string?)n));
        Assert.Contains("FB_MotorDol", both["conflicts"]!.ToJsonString());
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Unknown_software_inventory_row_reports_kind_and_object_path()
    {
        softwarePaths.Add(new JsonObject { ["kind"] = "unknown-kind", ["name"] = "Thing", ["objectPath"] = "Group/Thing" });
        var error = Assert.Throws<GenerationCallRejectionException>(() => Plan(Session("17"), "17"));
        Assert.Contains("unknown-kind", error.Message);
        Assert.Contains("Group/Thing", error.Message);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Existing_text_tree_station_uses_native_article_and_firmware()
    {
        var structured = Plan(Session("14sp1"), "14sp1");
        textTreeFirmware = "V2.0";
        var observed = Plan(Session("14sp1"), "14sp1");
        Assert.True(JsonNode.DeepEquals(structured["conflicts"], observed["conflicts"]));
        textTreeFirmware = "V1.0";
        var changed = Plan(Session("14sp1"), "14sp1");
        Assert.Contains("firmware", changed["conflicts"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, writes);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Plan_uses_the_session_state_binding(string release)
    {
        var plan = Plan(Session(release), release);
        Assert.Equal("C:/Projects/Test.ap" + release, (string?)plan["target"]?["projectIdentity"]);
        Assert.Equal(0, writes);
    }

    [Theory, MemberData(nameof(Releases))]
    public void SameInputSameHashAndPreviewHasNoApprovalOrWrite(string release)
    {
        var session = Session(release); var first = Plan(session, release); var second = Plan(session, release);
        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.True((bool?)Apply(session, first, true).StructuredContent!["ok"]);
        Assert.Equal(0, approvals); Assert.Equal(0, writes); Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }
    [Theory, MemberData(nameof(Releases))]
    public void FailureAtKStopsAndRecordsAllRemainingSteps(string release)
    {
        failure = "failed";
        var session = Session(release); var plan = Plan(session, release); var result = Apply(session, plan);
        Assert.Equal(2, writes); Assert.Equal(1, approvals);
        Assert.Equal("partial", (string?)result.StructuredContent!["meta"]!["outcome"]);
        var steps = result.StructuredContent!["data"]!["steps"]!.AsArray();
        Assert.Equal("succeeded", (string?)steps[0]!["status"]); Assert.Equal("failed", (string?)steps[1]!["status"]);
        Assert.All(steps.Skip(2), step => Assert.Equal("NOT_EXECUTED", (string?)step!["status"]));
        var restart = new GenerationRunStore(Path.Combine(root, "runs"));
        Assert.Equal("failed", (string?)restart.Read((string)result.StructuredContent["data"]!["runId"]!)["status"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void UnknownNeverRetriesOrReplays(string release)
    {
        failure = "unknown"; var session = Session(release); var plan = Plan(session, release); var result = Apply(session, plan);
        Assert.Equal("unknown", (string?)result.StructuredContent!["meta"]!["outcome"]); Assert.Equal(2, writes);
        Apply(session, plan); Assert.Equal(2, writes);
    }
    public static IEnumerable<object[]> Refusals => Releases.SelectMany(r => new[] { "denied", "timeout" }.Select(reason => new object[] { r[0], reason }));
    [Theory, MemberData(nameof(Refusals))]
    public void ApprovalRefusalHasNoDispatchAndPersists(string release, string reason)
    {
        approvalReason = reason; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal(0, writes); Assert.Equal(1, approvals); Assert.Equal("rejected-before-operation", (string?)result.StructuredContent["data"]!["status"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void IdentityChangesBetweenStepsStopBeforeNextWrite(string release)
    {
        changeIdentity = true; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal(1, writes); Assert.Equal("partial", (string?)result.StructuredContent!["meta"]!["outcome"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void StaleHashAndWrongProjectRefuseBeforeApproval(string release)
    {
        var session = Session(release); var plan = Plan(session, release);
        Assert.Throws<GenerationCallRejectionException>(() => session.Apply((string)plan["planId"]!, "sha256:" + new string('a',64), "C:/Projects/Test.ap" + release, null, false));
        Assert.Throws<GenerationCallRejectionException>(() => session.Apply((string)plan["planId"]!, (string)plan["planHash"]!, "wrong", null, false));
        Assert.Equal(0, approvals); Assert.Equal(0, writes);
    }
    [Theory, MemberData(nameof(Releases))]
    public void ArgumentMismatchStopsBeforeDispatch(string release)
    {
        mismatch = true; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal(0, writes); Assert.Equal(1, approvals); Assert.Equal("rejected-before-operation", (string?)result.StructuredContent!["meta"]!["outcome"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void UnregisteredStepMakesItsPhaseUnavailable(string release)
    {
        missingTool = true; var session = Session(release); var plan = Plan(session, release);
        Assert.Contains(plan["unavailable"]!.AsArray(), row => (string?)row!["phase"] == "plcTags");
        Assert.Throws<ArgumentException>(() => session.Apply((string)plan["planId"]!, (string)plan["planHash"]!, (string)plan["target"]!["projectIdentity"]!, new[] { "plcTags" }, false));
        Assert.Equal(0, approvals); Assert.Equal(0, writes);
    }
    [Theory, MemberData(nameof(Releases))]
    public void PartialReadbackAndMissingPortalRefusePlanning(string release)
    {
        incomplete = true; var session = Session(release); Assert.Throws<GenerationCallRejectionException>(() => Plan(session, release));
        noPortal = true; incomplete = false; var error = Assert.Throws<GenerationCallRejectionException>(() => Plan(session, release));
        Assert.Contains("ConnectPortal", error.Message); Assert.Equal(0, writes);
    }
    [Fact]
    public void RestartPreservesInterruptedIntentAsUnknown()
    {
        var store = new GenerationRunStore(Path.Combine(root, "runs"));
        string run = store.Begin(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["id"] = "s1" }, new JsonObject { ["id"] = "s2" }) });
        store.Append(run, new JsonObject { ["event"] = "step-start", ["stepId"] = "s1", ["requestId"] = "r1" });
        var restored = new GenerationRunStore(Path.Combine(root, "runs")).Read(run);
        Assert.Equal("unknown", (string?)restored["steps"]![0]!["status"]); Assert.Equal("NOT_EXECUTED", (string?)restored["steps"]![1]!["status"]);
        Assert.Equal("unknown", (string?)restored["status"]);
        Assert.Throws<ArgumentException>(() => store.Read("../escape"));
    }
    [Theory, MemberData(nameof(Releases))]
    public void DispatchExceptionIsUnknownAndRetainsEvidence(string release)
    {
        failure = "throw"; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal("unknown", (string?)result.StructuredContent!["meta"]!["outcome"]);
        Assert.Contains("Injected dispatch failure", result.StructuredContent["data"]!.ToJsonString());
        Assert.Equal(2, writes);
    }
    [Theory, MemberData(nameof(Releases))]
    public void FirstStepFailureKeepsFailedOutcome(string release)
    {
        failure = "failed"; stopAt = 1; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal("failed", (string?)result.StructuredContent!["meta"]!["outcome"]); Assert.Equal(1, writes);
        Assert.All(result.StructuredContent["data"]!["steps"]!.AsArray().Skip(1), s => Assert.Equal("NOT_EXECUTED", (string?)s!["status"]));
    }
    [Fact]
    public void ApplyRequestCarriesPlanHashBeforeItsStartRow()
    {
        var session = Session("19"); var plan = Plan(session, "19");
        var log = new AuditLog(Path.Combine(root, "apply-audit"));
        using var useLog = AuditInvocation.UseLog(log);
        using (var invocation = AuditInvocation.Begin(true, "foundation", "19", "ApplyGenerationPlan"))
            invocation!.Complete(Apply(session, plan).StructuredContent!.ToJsonString());
        var rows = log.Read();
        Assert.Equal(new[] { "request", "start", "end" }, rows.Select(row => row.Event));
        Assert.Equal((string?)plan["planHash"], rows[0].PlanHash);
        Assert.Equal("succeeded", rows[2].Outcome);
        Assert.True(log.Verify().Passed);
    }
    [Fact]
    public void CorrelatedAuditRowsRetainLegacyHashChain()
    {
        var log = new AuditLog(Path.Combine(root, "audit"));
        log.Append("request", "legacy", "foundation", "19", "CreatePlcTag");
        log.Append("request", "step-request", "foundation", "19", "CreatePlcTag", planHash: new string('a',64),
            planId: "plan", stepId: "step", runId: "run", parentRequestId: "parent");
        log.Append("end", "step-request", "foundation", "19", "CreatePlcTag", outcome: "succeeded",
            planId: "plan", stepId: "step", runId: "run", parentRequestId: "parent");
        Assert.True(log.Verify().Passed); Assert.Equal(3, log.Verify().Count);
        var rows = log.Read(); Assert.Null(rows[0].PlanId); Assert.Equal("parent", rows[1].ParentRequestId); Assert.Equal("run", rows[2].RunId);
        Assert.Equal(new string('a', 64), rows[1].PlanHash);
    }
    [Theory, MemberData(nameof(Releases))]
    public void ExpiryIsCheckedAfterApprovalButNotBetweenWrites(string release)
    {
        advanceAfterWrite = true; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal("succeeded", (string?)result.StructuredContent!["data"]!["status"]);
        writes = 0; advanceAfterWrite = false; expireAtApproval = true; session = Session(release);
        result = Apply(session, Plan(session, release)); Assert.Equal(0, writes);
        Assert.Contains("expired", result.StructuredContent!["data"]!["stoppingReason"]!.ToString());
    }
    [Theory, MemberData(nameof(Releases))]
    public void CancellationHasItsOwnStopTypeAndKeepsEarlierSteps(string release)
    {
        cancelBetweenSteps = true; var session = Session(release); var result = Apply(session, Plan(session, release));
        Assert.Equal("cancelled", (string?)result.StructuredContent!["data"]!["status"]);
        Assert.Equal("partial", (string?)result.StructuredContent!["meta"]!["outcome"]); Assert.Equal(1, writes);
    }
    [Fact]
    public void StepAuditHashMeansOnlyTheToolHash()
    {
        var log = new AuditLog(Path.Combine(root, "step-audit"));
        using var use = AuditInvocation.UseLog(log);
        foreach (var hash in new string?[] { null, new string('a', 64) })
        using (AuditInvocation.GenerationStep("plan", "step", "run", "parent", hash, Guid.NewGuid().ToString("N")))
        using (var invocation = AuditInvocation.Begin(true, "foundation", "19", "CreatePlcTag"))
        { invocation!.RecordRequest(new string('b', 64)); invocation.Start(); invocation.End("succeeded"); }
        var requests = log.Read().Where(r => r.Event == "request").ToArray();
        Assert.Null(requests[0].PlanHash); Assert.Equal(new string('a',64), requests[1].PlanHash);
        Assert.True(log.Verify().Passed);
    }
    [Fact]
    public void SuccessfulRunChangesReplanSeedAndBindingAlsoChangesPlanId()
    {
        var session = Session("19"); var first = Plan(session, "19"); Apply(session, first);
        var second = Plan(session, "19"); Assert.NotEqual((string?)first["planId"], (string?)second["planId"]);
        bindingGeneration = "2"; var third = Plan(session, "19"); Assert.NotEqual((string?)second["planId"], (string?)third["planId"]);
    }
}
