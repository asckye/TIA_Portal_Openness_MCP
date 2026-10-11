extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters;
using TiaMcp.FoundationHost;
using TiaMcp.Generation.Planning.Tests;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using Session = enginehost::TiaMcpServer.ModelContextProtocol.GenerationExecutionSession;
using Configuration = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration;

public sealed class StagedGenerationTests
{
    public static IEnumerable<object[]> Releases => PlanningFixture.Releases.Select(r => new object[] { r });
    public static IEnumerable<object[]> DecidingFields => PlanningFixture.Releases.SelectMany(r => new[] {
        "status", "release", "projectFile", "processId", "softwarePath", "groupPath", "filePath", "inputSha256", "byteCount", "requestedSourceName", "sourcesBefore",
        "sourceName", "generationOption", "resultBasis", "objectsBefore", "sourceIdentity", "planHash" }.Select(f => new object[] { r, f }));

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(AppContext.BaseDirectory, "staged-generation", Guid.NewGuid().ToString("N"));
        internal readonly string Release, Project;
        internal readonly Worker Worker;
        internal readonly PlanningFixture Planner;
        internal readonly Session Session;
        internal int Approvals, StepWaits;
        internal string? Mutation, Failure;
        internal bool ApprovalDisabled = true, Stale, ExtraEffect, PreviewRefused, ReadFailure, Cancelled;
        internal string? ApprovalReason;
        internal DateTimeOffset Time = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
        internal Action? OnApproval, OnWrite;
        internal JsonObject? LastPlan;
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        internal Fixture(string release, int maximumSteps = 10000)
        {
            Release = release; Project = "C:/Projects/Fixture.ap" + (release == "14sp1" ? "14" : release == "15.1" ? "15" : release);
            Planner = new PlanningFixture(release); Planner.Machine.Target.Project.ProjectIdentity = Project;
            Worker = new Worker(this);
            Configuration.ReleaseKey = release; Configuration.Worker = Worker;
            var tools = new[] { "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource" }.Select(name =>
                new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == name), Worker), release, null,
                    () => new ApprovalSettings(true, 1), (pending, _, _) => {
                        StepWaits++;
                        return Task.FromResult(GenerationStepApproval.Match(pending) ?? throw new Exception("Source requested a second approval."));
                    })).ToArray();
            var catalog = new Catalog(tools);
            Host.ConfigureToolBridge(catalog, new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, Worker, lifetime, tools), () => false, new HashSet<string>());
            Session = new Session(() => Root, Call, name => catalog.Find(name) ?? Ordinary(name), (pending, _, _) => {
                Approvals++; pending.Validate(); OnApproval?.Invoke();
                return Task.FromResult(new ApprovalOutcome(pending, ApprovalDisabled, ApprovalReason));
            }, () => Time, () => Cancelled, maximumSteps: maximumSteps);
        }
        internal JsonObject Plan()
        {
            LastPlan = Session.Plan(Planner.Package(), GenerationDocuments.Canonical(Planner.Machine)).StructuredContent!["data"]!["plan"]!.AsObject();
            return LastPlan;
        }
        internal JsonObject Apply(JsonObject? plan = null)
        {
            plan ??= LastPlan ?? Plan();
            var request = new ModelContextProtocol.Server.RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer, ApprovalPrecheckTests.ServerProxy>())
                { Params = new CallToolRequestParams { Name = "ApplyGenerationPlan", Arguments = new Dictionary<string, JsonElement>() } };
            using var progress = Host.UseProgressRequest(request, CancellationToken.None);
            return Session.Apply((string)plan["planId"]!, (string)plan["planHash"]!, Project, null, false).StructuredContent!.AsObject();
        }
        private CallToolResult Call(string name, JsonObject args, Func<CallToolResult?>? check)
        {
            if (name == "GetSessionState") return Host.V4Result(name, new JsonObject());
            if (name == "GetOpennessWorkerStatus") return Host.V4Result(name, new JsonObject { ["worker"] = new JsonObject { ["binding"] = new JsonObject { ["identity"] = Identity() } } });
            if (name == "GetProjectTree") return Host.V4Result(name, new JsonObject { ["devices"] = new JsonArray(new JsonObject { ["kind"] = "device", ["name"] = "PLC1", ["articleNumber"] = "synthetic.article", ["version"] = "V1" }) });
            if (name == "GetSoftwareTree") return Host.V4Result(name, new JsonObject { ["paths"] = new JsonArray(Worker.Objects.Select(o => (JsonNode)new JsonObject { ["kind"] = o.Kind, ["name"] = o.Name, ["objectPath"] = o.Path }).ToArray()) });
            if (name.StartsWith("List", StringComparison.Ordinal))
            {
                if (name == "ListPlcExternalSources" && ReadFailure && Worker.Previews > 0) throw new InvalidOperationException("Injected inventory read failure.");
                var items = name == "ListPlcExternalSources" ? Worker.Sources.Keys.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray() : Array.Empty<JsonNode?>();
                if (name == "ListPlcExternalSources" && Mutation == "sourcesBefore" && Worker.Previews > 0) items = items.Concat(new[] { (JsonNode?)JsonValue.Create("unexpected.scl") }).ToArray();
                return Host.V4Result(name, new JsonObject { ["items"] = new JsonArray(items) });
            }
            if (new[] { "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource" }.Contains(name))
            {
                var result = Host.DispatchGenerationStep(name, new ToolArguments(JsonSerializer.SerializeToElement(args)), check ?? (() => null));
                // Inject only the executor's preview, after the real current policy and host result mapping.
                if ((bool?)args["dryRun"] == true && Mutation != null && result.StructuredContent?["data"] is JsonObject data)
                {
                    bool generateField = new[] { "sourceName", "generationOption", "resultBasis", "objectsBefore", "sourceIdentity" }.Contains(Mutation);
                    if (generateField == (name == "GenerateBlocksFromExternalSource"))
                        data[Mutation] = Mutation == "processId" || Mutation == "byteCount" ? JsonValue.Create(99999)
                            : Mutation == "objectsBefore" ? new JsonArray(new JsonObject { ["kind"] = "block", ["path"] = "FB_Motor", ["name"] = "FB_Motor" }) : JsonValue.Create(Mutation == "sourceIdentity" || Mutation == "planHash" ? "" : "changed");
                }
                return result;
            }
            if (check?.Invoke() is { } rejected) return rejected;
            if (name == "MovePlcBlockToGroup")
            {
                var block = Worker.Objects.Single(o => o.Name == (string)args["blockName"]!);
                block.Path = (string)args["groupPath"]! + "/" + block.Name;
            }
            OnWrite?.Invoke();
            return Host.V4Result(name, new JsonObject());
        }
        internal JsonObject Identity() => new() { ["projectPath"] = Project, ["processId"] = 123, ["processStartUtc"] = "2026-10-10T00:00:00Z", ["generation"] = "1" };
        private Descriptor? Ordinary(string name)
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path != null && !File.Exists(Path.Combine(path.FullName, "CLAUDE.md"))) path = path.Parent;
            var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(path!.FullName, "manifest/contracts/v4/baseline", Release + ".json")))!;
            var row = baseline["tools"]!.AsArray().SingleOrDefault(t => (string?)t!["name"] == name);
            return row == null ? null : new Descriptor(name, "", new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(row["inputSchema"]) }, null, "", Array.Empty<enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor>(), new(true, true), null, "foundation");
        }
        public void Dispose() { lifetime.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Catalog : enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; }
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable => All;
        public IReadOnlyList<Descriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Descriptor? Find(string name, bool includeUnavailable = false) => All.TryGetValue(name, out var tool) ? tool : null;
        internal Catalog(IEnumerable<FoundationV4Tool> tools) => All = tools.ToDictionary(t => t.ProtocolTool.Name, t => new Descriptor(t.ProtocolTool.Name,
            t.ProtocolTool.Description!, t.ProtocolTool, new("L1", "PLC", "WRITE", false, true), "source fixture", Array.Empty<enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor>(), new(true, true), null, "foundation"));
    }
    private sealed class Worker(Fixture fixture) : IFoundationSessionWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal readonly Dictionary<string, string> Sources = new();
        internal readonly List<PlcExternalSourceObject> Objects = new();
        internal int Writes, Previews, Generations;
        internal bool Uncertain;
        public bool Faulted => false;
        public JsonNode? Binding => fixture.Identity();
        public object SessionKey { get; } = new();
        public string ApprovalIdentity => fixture.Identity().ToJsonString();
        public bool Poisoned => Uncertain;
        public bool Bundled => false;
        public bool SharedSession => true;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request) => null;
        public void MarkUncertain() { Uncertain = true; Host.MarkSharedSessionUncertain(); }
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => Task.FromResult(Snapshot());
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["IsAttached"] = true, ["ProcessId"] = 123, ["ProjectPath"] = fixture.Project, ["ReleaseKey"] = fixture.Release });
            bool dry = (bool?)args["dryRun"] == true;
            if (dry) Previews++;
            if (fixture.PreviewRefused && dry) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Injected preview refusal.", "softwarePath");
            if (!dry && fixture.Stale) { Objects.Add(Object("Unexpected")); fixture.Stale = false; }
            var hash = (string?)args["expectedPlanHash"] ?? "";
            var expected = (string?)args["expectedProjectFile"] ?? "";
            PlcExternalSourceWorkflowResult result;
            if (operation == "ImportPlcExternalSource")
            {
                string path = (string)args["filePath"]!, name = Path.GetFileName(path);
                using var input = File.OpenRead(path);
                result = PlcExternalSourceWorkflowPolicy.Import(new PlcExternalSourceImportResult { Operation = operation, Release = fixture.Release, ProjectFile = fixture.Project,
                    ProcessId = 123, SoftwarePath = "PLC1", FilePath = path, RequestedSourceName = name }, input, dry, hash, !dry, expected, () => Sources.Keys, () => { }, (n, p) => {
                        Writes++; Sources[n] = File.ReadAllText(p); fixture.OnWrite?.Invoke(); return n;
                    });
            }
            else if (operation == "GenerateBlocksFromExternalSource")
            {
                string name = (string)args["externalSourceName"]!;
                if (fixture.Failure == "throw" && !dry) throw new IOException("Injected worker exit.");
                PlcExternalSourceObject[] generated = Array.Empty<PlcExternalSourceObject>();
                result = PlcExternalSourceWorkflowPolicy.Generate(new PlcExternalSourceGenerationResult { Operation = operation, Release = fixture.Release, ProjectFile = fixture.Project,
                    ProcessId = 123, SoftwarePath = "PLC1", SourceName = name, SourceIdentity = "identity:" + name }, dry, hash, !dry, expected, () => Objects.ToArray(), () => { }, () => {
                        Writes++; Generations++;
                        if (fixture.Failure == "unknown") throw new IOException("Unknown native result.");
                        if (fixture.Failure == "failed") throw new ArgumentException("Known generation diagnostic.");
                        generated = Regex.Matches(Sources[name], "(?m)^(TYPE|FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK) \\\"([^\\\"]+)\\\"")
                            .Select(m => Object(m.Groups[2].Value, m.Groups[1].Value == "TYPE" ? "type" : "block")).ToArray();
                        Objects.AddRange(generated);
                        if (fixture.ExtraEffect) Objects.Add(Object("Undeclared"));
                        fixture.OnWrite?.Invoke();
                    }, () => fixture.Release == "14sp1" ? Array.Empty<PlcExternalSourceObject>() : generated, ex => ex is ArgumentException);
            }
            else throw new NotSupportedException(operation);
            return Task.FromResult(JsonSerializer.SerializeToNode(result, result.GetType()));
        }
        private static PlcExternalSourceObject Object(string name, string kind = "block") => new() { Kind = kind, Path = name, Name = name, TypeName = kind == "block" ? "PlcBlock" : "PlcType", ProgrammingLanguage = "SCL", ModifiedDate = new DateTime(2026, 10, 10), IsConsistent = true };
    }

    [Theory, MemberData(nameof(Releases))]
    public void StatefulPoliciesExecuteOnceWithFactsAndOnePlanApproval(string release)
    {
        using var f = new Fixture(release); var result = f.Apply();
        Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.Equal(1, f.Approvals);
        Assert.True(f.Worker.Generations > 0); Assert.Equal(f.Worker.Sources.Count * 2, f.Worker.Writes);
        Assert.Contains(result["data"]!["events"]!.AsArray(), e => (string?)e!["event"] == "fact");
    }
    [Theory, MemberData(nameof(DecidingFields))]
    public void EveryDecidingFieldAndWitnessMismatchStopsBeforeThatSourceWrite(string release, string field)
    {
        using var f = new Fixture(release); f.Mutation = field; var result = f.Apply();
        Assert.Equal("diverged", (string?)result["data"]?["status"]);
        bool generation = new[] { "sourceName", "generationOption", "resultBasis", "objectsBefore", "sourceIdentity" }.Contains(field);
        Assert.Equal(generation ? 1 : 0, f.Worker.Writes); Assert.Equal(0, f.Worker.Generations);
        Assert.Contains(result["data"]!["events"]!.AsArray(), e => (string?)e!["event"] == "divergence");
    }
    [Theory, MemberData(nameof(Releases))]
    public void OwnApplyHashCheckRefusesInventoryInsertedAfterReview(string release)
    {
        using var f = new Fixture(release); f.OnWrite = () => { if (f.Worker.Writes > 0) f.Stale = true; };
        var result = f.Apply(); Assert.Equal("rejected-before-operation", (string?)result["data"]!["status"]);
        Assert.Equal(1, f.Worker.Writes); Assert.Equal(0, f.Worker.Generations);
    }
    [Theory, MemberData(nameof(Releases))]
    public void ExtraGeneratedObjectStopsAsEffectDivergence(string release)
    {
        using var f = new Fixture(release); f.ExtraEffect = true; var result = f.Apply();
        Assert.Equal("effect-diverged", (string?)result["data"]!["status"]); Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
        Assert.Equal(2, f.Worker.Writes); Assert.Equal(1, f.Worker.Generations);
    }
    [Theory, MemberData(nameof(Releases))]
    public void UnknownStopsWithoutRetryAndRunSurvivesRestart(string release)
    {
        using var f = new Fixture(release); f.Failure = "unknown"; var plan = f.Plan(); var result = f.Apply(plan);
        Assert.Equal("unknown", (string?)result["data"]!["status"]); Assert.Equal(2, f.Worker.Writes);
        f.Apply(plan); Assert.Equal(2, f.Worker.Writes);
        var restored = new GenerationRunStore(Path.Combine(f.Root, "runs")).Read((string)result["data"]!["runId"]!);
        Assert.Equal("unknown", (string?)restored["status"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void PartialRunReplanHasNewLineageIdentity(string release)
    {
        using var f = new Fixture(release); f.Failure = "failed"; var first = f.Plan(); var result = f.Apply(first);
        Assert.Equal("partial", (string?)result["meta"]!["outcome"]); Assert.Equal("failed", (string?)result["data"]!["status"]);
        var second = f.Plan(); Assert.NotEqual((string?)first["planId"], (string?)second["planId"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void FirstPreviewRefusalDoesNotConsumeAndCanRunAgain(string release)
    {
        using var f = new Fixture(release); f.PreviewRefused = true; var plan = f.Plan(); var result = f.Apply(plan);
        Assert.Equal("rejected-before-operation", (string?)result["data"]!["status"]); Assert.Equal(0, f.Worker.Writes);
        // V20/V21's earlier backup is an executed step, so its plan remains consumed.
        if (release is "20" or "21") return;
        f.PreviewRefused = false; Assert.True((bool?)f.Apply(plan)["ok"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void ApprovedRunOutlivesPlanExpiryButApprovalStillRequiresFreshness(string release)
    {
        using var f = new Fixture(release); f.OnWrite = () => f.Time = f.Time.AddMinutes(11);
        Assert.True((bool?)f.Apply()["ok"]);
        using var expired = new Fixture(release); expired.OnApproval = () => expired.Time = expired.Time.AddMinutes(11);
        Assert.Equal("rejected-before-operation", (string?)expired.Apply()["data"]!["status"]); Assert.Equal(0, expired.Worker.Writes);
    }
    [Theory, MemberData(nameof(Releases))]
    public void CapsStopAtStepBoundaryAndRetainCompletedResults(string release)
    {
        using var f = new Fixture(release, maximumSteps: 1); var result = f.Apply();
        Assert.Equal("cap", (string?)result["data"]!["status"]); Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void DurationCapAndCancellationRetainTheFirstWrite(string release)
    {
        using var capped = new Fixture(release); capped.OnWrite = () => capped.Time = capped.Time.AddHours(4);
        Assert.Equal("cap", (string?)capped.Apply()["data"]!["status"]);
        using var cancelled = new Fixture(release); cancelled.OnWrite = () => cancelled.Cancelled = true;
        var result = cancelled.Apply(); Assert.Equal("cancelled", (string?)result["data"]!["status"]);
        Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void InventoryReadExceptionStopsBeforeThePendingSourceWrite(string release)
    {
        using var f = new Fixture(release); f.ReadFailure = true;
        var result = f.Apply(); Assert.Equal("rejected-before-operation", (string?)result["data"]!["status"]);
        Assert.Equal(0, f.Worker.Writes); Assert.Contains("InvalidOperationException", (string?)result["data"]!["stoppingReason"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void WorkerExitIsUnknownWithoutReplay(string release)
    {
        using var f = new Fixture(release); f.Failure = "throw"; var result = f.Apply();
        Assert.Equal("unknown", (string?)result["data"]!["status"]); Assert.Equal(1, f.Worker.Writes);
        Assert.All(result["data"]!["steps"]!.AsArray().SkipWhile(s => (string?)s!["status"] != "unknown").Skip(1), s => Assert.Equal("NOT_EXECUTED", (string?)s!["status"]));
    }
    [Theory, MemberData(nameof(Releases))]
    public void ApprovalEnabledAlsoUsesOnlyOnePlanApproval(string release)
    {
        using var f = new Fixture(release); f.ApprovalDisabled = false;
        Assert.True((bool?)f.Apply()["ok"]); Assert.Equal(1, f.Approvals); Assert.True(f.StepWaits > 0);
    }
    [Theory]
    [InlineData("14sp1", "denied")]
    [InlineData("19", "timeout")]
    [InlineData("20", "denied")]
    [InlineData("21", "timeout")]
    public void PlanRefusalDispatchesNothing(string release, string reason)
    {
        using var f = new Fixture(release); f.ApprovalReason = reason; var result = f.Apply();
        Assert.Equal(0, f.Worker.Writes); Assert.Equal(0, f.Worker.Previews); Assert.Equal(1, f.Approvals);
        Assert.Equal("rejected-before-operation", (string?)result["data"]!["status"]);
    }
    [Theory, MemberData(nameof(Releases))]
    public void SingleStepGrantCoversOnlyReviewedArgumentsAndThisPreviewHash(string release)
    {
        using var f = new Fixture(release); var plan = GenerationDocuments.Load<GenerationPlan>(f.Plan().ToJsonString());
        var step = plan.Steps.First(s => s.Review != null);
        var pending = PendingApproval.Create("foundation", release, "ApplyGenerationPlan", "{}", "{}", 120);
        pending.Kind = "plan"; pending.PlanJson = GenerationDocuments.Canonical(plan); pending.PlanHash = plan.PlanHash;
        var approval = new ApprovalOutcome(pending, true, null); string hash = new string('a',64), previewId = Guid.NewGuid().ToString("N");
        var final = StepReviewRules.FinalArguments(step, new JsonObject { ["planHash"] = hash });
        using (GenerationStepApproval.Enter(approval, step, final, previewId, hash))
        {
            Assert.Null(GenerationStepApproval.Match(PendingApproval.Create("foundation", release, step.Tool, final.ToJsonString(), "{}", 120))!.Reason);
            Assert.Equal("denied", GenerationStepApproval.Match(PendingApproval.Create("foundation", release, step.Tool, final.ToJsonString(), "{}", 120))!.Reason);
            var forged = final.DeepClone().AsObject(); forged["expectedPlanHash"] = new string('b',64);
            Assert.Equal("denied", GenerationStepApproval.Match(PendingApproval.Create("foundation", release, step.Tool, forged.ToJsonString(), "{}", 120))!.Reason);
            Assert.Throws<InvalidOperationException>(() => GenerationStepApproval.Enter(approval, step, final, previewId, hash));
        }
        var different = final.DeepClone().AsObject(); different["filePath"] = "C:/Other.scl";
        Assert.Throws<InvalidOperationException>(() => GenerationStepApproval.Enter(approval, step, different, previewId, hash));
        Assert.Throws<InvalidOperationException>(() => GenerationStepApproval.Enter(approval, step, final, previewId, new string('b',64)));
        Assert.Throws<InvalidOperationException>(() => GenerationStepApproval.Enter(approval, step, final));
    }
    [Theory, MemberData(nameof(Releases))]
    public void DeleteRuleUsesTheRunIdentityAndVerifiesSingleRemoval(string release)
    {
        using var f = new Fixture(release); var result = f.Apply(); Assert.True((bool?)result["ok"]);
        var plan = GenerationDocuments.Load<GenerationPlan>(f.LastPlan!.ToJsonString()); var facts = new RunFacts(plan);
        foreach (var row in result["data"]!["events"]!.AsArray().OfType<JsonObject>())
        {
            var step = plan.Steps.SingleOrDefault(s => s.Id == (string?)row["stepId"]); if (step == null) continue;
            if ((string?)row["event"] == "step-preview") facts.CapturePreview(step, row["projection"]!.AsObject());
            if ((string?)row["event"] == "step-end" && (string?)row["status"] == "succeeded") facts.Accept(step, row["result"]!["data"]!.AsObject());
        }
        var import = plan.Steps.First(s => s.Tool == "ImportPlcExternalSource"); string name = import.Expect.Contains!;
        var generate = plan.Steps.Single(s => s.Tool == "GenerateBlocksFromExternalSource" && s.Arguments["externalSourceName"].GetString() == name);
        Dictionary<string, JsonElement> Fields(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
        var delete = new GenerationPlanStepsItem { Id = "cleanup", Tool = "DeletePlcExternalSource", Arguments = Fields(new { softwarePath = "PLC1", groupPath = "", externalSourceName = name, dryRun = false, confirm = true, expectedProjectFile = f.Project }),
            Review = new GenerationStepReview { Rule = StepReviewRules.Delete, Expect = Fields(new { softwarePath = "PLC1", groupPath = "", sourceName = new { fact = import.Id + ".sourceName" }, sourcesBefore = "observed+run" }),
                Witness = new Dictionary<string, string> { ["planHash"] = "sha256", ["targetIdentity"] = "fact:" + generate.Id + ".sourceIdentity" }, Effect = Fields(new { sourcesRemoved = new[] { name } }) } };
        IEnumerable<PlcExternalSourceDeleteItem> Inventory() => f.Worker.Sources.Keys.Select(s => new PlcExternalSourceDeleteItem { Name = s, Identity = "identity:" + s, ParentVerified = true });
        var request = new PlcExternalSourceDeleteRequest { Release = release, Project = f.Project, ProcessId = 123, Software = "PLC1", Name = name, RootIdentity = "root", DryRun = true };
        var preview = PlcExternalSourceDeletePolicy.Run(request, Inventory, () => { }, _ => { }, _ => throw new Exception("preview wrote"));
        JsonObject Wire(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        var projection = Wire(preview); projection["sourcesBefore"] = JsonSerializer.SerializeToNode(f.Worker.Sources.Keys);
        Assert.True(StepReviewRules.Compare(plan, delete, projection, facts, 123).Matches);
        var swapped = projection.DeepClone().AsObject(); swapped["targetIdentity"] = "replacement";
        Assert.False(StepReviewRules.Compare(plan, delete, swapped, facts, 123).Matches);
        request.DryRun = false; request.ExpectedHash = preview.PlanHash; request.Confirm = true; request.ExpectedProject = f.Project;
        int attempts = 0;
        var applied = PlcExternalSourceDeletePolicy.Run(request, Inventory, () => { }, _ => { }, _ => { attempts++; f.Worker.Sources.Remove(name); });
        var effect = Wire(applied); effect["sourcesAfter"] = JsonSerializer.SerializeToNode(f.Worker.Sources.Keys);
        Assert.True(StepReviewRules.VerifyEffect(plan, delete, effect, facts).Matches); Assert.Equal(1, attempts);
        facts.Accept(delete, effect); Assert.DoesNotContain(name, facts.Sources("PLC1"));
    }
}
