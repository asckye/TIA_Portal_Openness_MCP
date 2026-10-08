extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;
using EngineCatalog = enginehost::TiaMcp.LegacyHost.EngineCatalog;
using EngineSlice = enginehost::TiaMcp.LegacyHost.EngineSlice;
using EngineReply = enginehost::TiaMcp.LegacyHost.EngineReply;
using IEngineWorker = enginehost::TiaMcp.LegacyHost.IEngineWorker;
using EngineResult = enginehost::TiaMcp.LegacyHost.EngineResult;
using EngineJournal = enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal;
using EngineAudit = TiaOpenness.Shared.AuditInvocation;
using ExportStore = TiaMcpServer.ModelContextProtocol.ExportStore;

public sealed class EngineSliceTests
{
    [Fact]
    public async Task PreviewApprovalAndApplyFollowEngineOrderAndShareRequestId()
    {
        using var fixture = new Fixture();
        fixture.Slice.Wait = (pending, _, _) => {
            fixture.Events.Add("workbench"); Assert.Equal("engine", pending.Host);
            Assert.Equal(fixture.Worker.Ids.Single(), pending.RequestId);
            return Task.FromResult(new ApprovalOutcome(pending, true, null));
        };
        var result = await fixture.Call("ManagePlcTagDefinition", "{\"dryRun\":false}");
        Assert.True(result.StructuredContent!["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "journal", "precheck", "preview", "approval", "workbench", "session", "caller-input", "invoke", "apply", "outcome", "finish" }, fixture.Events);
        Assert.Equal(2, fixture.Worker.Ids.Count); Assert.Single(fixture.Worker.Ids.Distinct());
        Assert.Equal(fixture.Worker.Ids[0], (string?)result.StructuredContent["meta"]?["requestId"]);
        var projection = fixture.JournalRows.Where(row => row.Contains("callProjection")).ToArray();
        var native = fixture.JournalRows.Where(row => row.Contains("native:fake-worker")).ToArray();
        Assert.NotEmpty(projection); Assert.Equal(4, native.Length);
        Assert.All(projection.Concat(native), row => Assert.Equal(fixture.Worker.Ids[0], (string?)JsonNode.Parse(row)?["id"]));
        var audit = fixture.AuditRows.Select(row => JsonNode.Parse(row)!).ToArray();
        Assert.Equal(new[] { "request", "start", "end" }, audit.Select(row => (string?)row["event"]));
        Assert.All(audit, row => Assert.Equal(fixture.Worker.Ids[0], (string?)row["requestId"]));
    }

    [Fact]
    public async Task SaveReachesApprovalWithoutInventingAPreview()
    {
        using var fixture = new Fixture();
        fixture.Slice.Wait = (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, false, "workbench-unavailable"));
        var result = await fixture.Call("SaveProject", "{}");
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Ids); Assert.DoesNotContain("invoke", fixture.Events);
    }

    [Fact]
    public async Task FailedPreviewDoesNotRequestApprovalOrApply()
    {
        using var fixture = new Fixture(); fixture.Worker.RejectPreview = true;
        fixture.Slice.Wait = (_, _, _) => throw new Exception("Approval must not be requested.");
        var result = await fixture.Call("ManagePlcTagDefinition", "{\"dryRun\":false}");
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Contains(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), w => (string?)w?["code"] == "APPROVAL_PRECHECK_REFUSED");
        Assert.Single(fixture.Worker.Ids);
    }

    [Theory]
    [InlineData(true, true)] [InlineData(false, false)]
    public async Task OnlyNativeIssuedUnknownWritesLockTheGeneration(bool native, bool locks)
    {
        using var fixture = new Fixture(); fixture.Worker.Unknown = true; fixture.Worker.Native = native;
        await fixture.Call("SaveProject", "{}");
        int calls = fixture.Worker.Ids.Count;
        var next = await fixture.Call("GetSessionState", "{}");
        Assert.Equal(locks ? "SESSION_RESET_REQUIRED" : "OUTCOME_UNKNOWN", (string?)next.StructuredContent?["error"]?["code"]);
        Assert.Equal(locks ? calls : calls + 1, fixture.Worker.Ids.Count);
        var local = await fixture.Call("ListExportHandles", "{}"); Assert.True(local.StructuredContent!["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task CallerInputRefusalOccursBeforeWorkerDispatch(bool enabled)
    {
        using var fixture = new Fixture(); new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
        var result = await fixture.Call("SaveProject", "{\"inputPath\":\"C:/missing-p701/input.xml\"}");
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Ids);
        if (enabled) Assert.DoesNotContain("approval", fixture.Events);
    }

    [Fact]
    public async Task ChangedBindingWhileWaitingRefusesApply()
    {
        using var fixture = new Fixture();
        fixture.Slice.Wait = (pending, _, _) => { fixture.Worker.Binding = new JsonObject { ["project"] = "other" }; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        var result = await fixture.Call("SaveProject", "{}");
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]); Assert.Empty(fixture.Worker.Ids);
    }

    [Fact]
    public async Task CancelledQueuedCallReturnsTypedRefusal()
    {
        using var fixture = new Fixture(); fixture.Worker.CancelAcquire = true;
        var result = await fixture.Call("GetSessionState", "{}");
        Assert.Equal("CANCELLED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("tool-queue", (string?)result.StructuredContent?["error"]?["details"]?["stage"]); Assert.Empty(fixture.Worker.Ids);
    }

    [Fact]
    public async Task LocalToolsDoNotAcquireTheNativeLaneAndKeepResponseGuard()
    {
        using var fixture = new Fixture(); fixture.Worker.CancelAcquire = true;
        var result = await fixture.Call("ListExportHandles", "{}");
        Assert.True(result.StructuredContent!["ok"]!.GetValue<bool>()); Assert.Empty(fixture.Worker.Ids);
    }

    [Fact]
    public async Task LargeLocalBuilderResponseIsStoredAndRetrievedInTheHost()
    {
        using var fixture = new Fixture(); fixture.Worker.CancelAcquire = true;
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS");
        string? exportId = null;
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", "500");
            foreach (var name in new[] { "BuildPlcUdt", "GetExportContent" })
                fixture.Catalog.Tools.Single(t => t.Name == name).InputSchema = JsonSerializer.SerializeToElement(new { type = "object" });
            var args = JsonSerializer.Serialize(new { udt = new { name = "LargeFixture", members = Enumerable.Range(0, 50)
                .Select(i => new { name = "Member" + i, datatype = "Bool", comment = new string('x', 100) }) } });
            var built = await fixture.Call("BuildPlcUdt", args);
            Assert.True(built.StructuredContent!["ok"]!.GetValue<bool>());
            string id = (string)built.StructuredContent["data"]!["export"]!["id"]!;
            exportId = id;
            var entry = ExportStore.Get(id); Assert.NotNull(entry);
            var content = await fixture.Call("GetExportContent", JsonSerializer.Serialize(new { exportId = id, length = 100 }));
            Assert.Equal(entry.Content[..100], (string?)content.StructuredContent?["data"]?["text"]);
            var listed = await fixture.Call("ListExportHandles", "{}");
            Assert.Contains(listed.StructuredContent!["data"]!["items"]!.AsArray(), item => (string?)item?["export"]?["id"] == id);
            Assert.Empty(fixture.Worker.Ids);
        }
        finally { ExportStore.Delete(exportId); Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", previous); }
    }

    [Fact]
    public void CatalogRejectsAChangedWorker()
    {
        using var fixture = new Fixture(); File.AppendAllText(fixture.WorkerPath, "changed");
        Assert.Throws<InvalidDataException>(() => new EngineCatalog(fixture.CatalogPath, fixture.WorkerPath));
    }

    [Fact]
    public async Task ProcessLossRefusesBeforeAnyNewApproval()
    {
        using var fixture = new Fixture(); fixture.Worker.Faulted = true;
        fixture.Slice.Wait = (_, _, _) => throw new Exception("A faulted generation cannot ask for approval.");
        var result = await fixture.Call("SaveProject", "{}");
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Ids);
    }

    [Fact]
    public async Task OversizedUnknownWriteRetainsItsSessionLockEvidence()
    {
        using var fixture = new Fixture(); fixture.Worker.OversizedUnknown = true; fixture.Worker.Native = true;
        var result = await fixture.Call("SaveProject", "{}");
        Assert.Equal("LIMIT_EXCEEDED", (string?)result.StructuredContent?["error"]?["code"]);
        var next = await fixture.Call("GetSessionState", "{}");
        Assert.Equal("SESSION_RESET_REQUIRED", (string?)next.StructuredContent?["error"]?["code"]);
        Assert.Single(fixture.Worker.Ids);
    }

    [Theory]
    [InlineData("ListExportHandles", "{\"limit\":2147483648}")]
    [InlineData("ListExportHandles", "{\"limit\":\"invalid\"}")]
    [InlineData("ManagePlcTagDefinition", "{\"dryRun\":\"invalid\"}")]
    public async Task InvalidScalarArgumentsAreRefusedBeforeApprovalAndDispatch(string name, string args)
    {
        using var fixture = new Fixture();
        fixture.Slice.Wait = (_, _, _) => throw new Exception("Invalid inputs must not request approval.");
        var result = await fixture.Call(name, args);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Empty(fixture.Worker.Ids);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "p701-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly ApprovalSettings previous = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        private readonly IDisposable auditScope;
        internal readonly List<string> Events = new(), JournalRows = new();
        internal readonly FakeWorker Worker;
        internal readonly EngineCatalog Catalog;
        internal readonly EngineSlice Slice;
        internal string WorkerPath => Path.Combine(root, "worker.exe");
        internal string CatalogPath => Path.Combine(root, "catalog.json");
        internal IEnumerable<string> AuditRows => Directory.GetFiles(Path.Combine(root, "audit"), "audit-*.jsonl").Order().SelectMany(File.ReadAllLines);
        internal Fixture()
        {
            Directory.CreateDirectory(root); File.WriteAllText(WorkerPath, "fake worker");
            auditScope = EngineAudit.UseLog(new AuditLog(Path.Combine(root, "audit")));
            new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
            var tools = EngineCatalog.Slice.Select(name => new Tool { Name = name,
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { dryRun = new { type = "boolean" }, inputPath = new { type = "string" }, limit = new { type = "integer" } }, additionalProperties = false }) }).ToArray();
            var descriptors = EngineCatalog.Slice.Select(name => new { name,
                classification = new { operation = name is "SaveProject" or "ManagePlcTagDefinition" ? "WRITE" : "READ" },
                parameters = new[] { new { name = "limit", clrType = typeof(int).FullName } }, dryRun = new { present = name == "ManagePlcTagDefinition", @default = true }, candidateFamily = (string?)null }).ToArray();
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(new { formatVersion = 1, release = "21",
                workerSha256 = enginehost::TiaMcp.LegacyHost.EngineWorkerClient.Hash(WorkerPath), tools, liteTools = EngineCatalog.Slice,
                behaviorCapabilities = BehaviorCapabilities.Table(typeof(EngineSliceTests).Assembly, "21"), serverInstructions = "fixture", descriptors }, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            Catalog = new EngineCatalog(CatalogPath, WorkerPath); Worker = new FakeWorker(Events);
            Slice = new EngineSlice(Catalog, Worker) { Stage = Events.Add,
                Wait = (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, true, null)) };
            EngineJournal.ConfigureOutput(JournalRows.Add);
        }
        internal Task<CallToolResult> Call(string name, string json) => Slice.Invoke(Catalog.Tools.Single(t => t.Name == name), JsonNode.Parse(json)!.AsObject(), CancellationToken.None);
        public void Dispose()
        {
            EngineJournal.ConfigureOutput(null); auditScope.Dispose(); previous.Save(ApprovalSettings.SettingsPath);
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeWorker(List<string> events) : IEngineWorker
    {
        internal readonly List<string> Ids = new();
        internal bool RejectPreview, Unknown, Native, CancelAcquire, OversizedUnknown;
        public bool Faulted { get; set; }
        public JsonNode? Binding { get; set; }
        public Task<IDisposable> Acquire(CancellationToken token)
        {
            if (CancelAcquire) throw new OperationCanceledException();
            token.ThrowIfCancellationRequested(); return Task.FromResult<IDisposable>(new Lane());
        }
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Ids.Add(id); events.Add(preview ? "preview" : "apply");
            using var correlation = EngineJournal.UseCorrelation(id);
            using var calls = EngineJournal.BeginNativeCallScope();
            EngineJournal.Native("fake-worker", () => { });
            var outcome = Unknown ? Outcome.Unknown : RejectPreview && preview ? Outcome.RejectedBeforeOperation : Outcome.Succeeded;
            Error? error = Unknown ? new Error("unknown", new OutcomeUnknownDetails("fake", new Dictionary<string, JsonElement>()))
                : RejectPreview && preview ? new Error("invalid", new InvalidArgumentDetails("name", Array.Empty<string>())) : null;
            var result = EngineResult.Wire(Envelope.Create(new JsonObject(), error, new Meta(DateTimeOffset.UtcNow, "21", name, id, outcome,
                Unknown ? Execution.Unknown : error == null ? Execution.ReadOnly : Execution.NotStarted, Unknown,
                BehaviorPolicy.NotApplicable, Unknown ? Completeness.Unknown : error == null ? Completeness.Complete : Completeness.None, null, Array.Empty<Warning>())));
            if (OversizedUnknown) result = EngineResult.Wire(Envelope.Create(new JsonObject { ["nativeOutcomeUnknown"] = true },
                new Error("limit", new LimitExceededDetails("response", 16 * 1024 * 1024, null)),
                new Meta(DateTimeOffset.UtcNow, "21", name, id, Outcome.Failed, Execution.Completed, false,
                    BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>())));
            return Task.FromResult(new EngineReply(result, Native, Binding, null, null));
        }
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public void Dispose() { }
    }
}
