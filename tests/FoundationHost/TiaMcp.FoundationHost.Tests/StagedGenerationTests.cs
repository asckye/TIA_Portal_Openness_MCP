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
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "tiamcp-staged-generation", Guid.NewGuid().ToString("N"));
        internal readonly string Release, Project;
        internal readonly bool NewProject;
        internal bool Created, DeviceCreated;
        internal string? TransitionFault;
        internal string BindingGeneration = "1";
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
        internal readonly Dictionary<string, JsonObject> Tags = new();
        internal readonly HashSet<string> Tables = new(), Groups = new();
        internal bool CompileFails, RecordRace, RecordIo;
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        internal Fixture(string release, int maximumSteps = 10000, bool newProject = false)
        {
            Release = release; Project = Path.Combine(Root, "project", "Fixture.ap" + (release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release)).Replace('\\', '/');
            NewProject = newProject;
            if (newProject) Project = GenerationProjectPath.CreationFile(release, Path.Combine(Root, "project"), "Fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(Project)!);
            Planner = new PlanningFixture(release); Planner.Machine.Target.Project.ProjectIdentity = Project;
            if (newProject)
            {
                Planner.Machine.Target.Project.Mode = "new"; Planner.Machine.Target.Project.ProjectIdentity = null;
                Planner.Machine.Target.Project.Directory = Path.Combine(Root, "project"); Planner.Machine.Target.Project.Name = "Fixture";
            }
            Worker = new Worker(this);
            Configuration.ReleaseKey = release; Configuration.Worker = Worker;
            var tools = new[] { "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource" }.Select(name =>
                new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == name), Worker), release, null,
                    () => new ApprovalSettings(true, 1), (pending, _, _) => {
                        StepWaits++;
                        return Task.FromResult(GenerationStepApproval.Match(pending) ?? throw new Exception("Source requested a second approval."));
                    })).ToArray();
            Catalog? catalog = null;
            Session = new Session(() => Root, Call, name => catalog.Find(name) ?? Ordinary(name), (pending, _, _) => {
                Approvals++; pending.Validate(); OnApproval?.Invoke();
                return Task.FromResult(new ApprovalOutcome(pending, ApprovalDisabled, ApprovalReason));
            }, () => Time, () => Cancelled, maximumSteps: maximumSteps);
            var record = new FoundationV4Tool(new GenerationTool("PublishGenerationRecord", release, executionSession: Session), release, null,
                () => new ApprovalSettings(true, 1), (pending, _, _) => { StepWaits++; return Task.FromResult(GenerationStepApproval.Match(pending) ?? throw new Exception("Record requested a second approval.")); });
            var registered = tools.Append(record).ToArray(); catalog = new Catalog(registered);
            Host.ConfigureToolBridge(catalog, new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, Worker, lifetime, registered), () => false, new HashSet<string>());
        }
        internal JsonObject Plan(string[]? adopt = null)
        {
            LastPlan = Session.Plan(Planner.Package(), GenerationDocuments.Canonical(Planner.Machine), adopt).StructuredContent!["data"]!["plan"]!.AsObject();
            return LastPlan;
        }
        internal JsonObject Apply(JsonObject? plan = null, string[]? phases = null)
        {
            plan ??= LastPlan ?? Plan();
            var request = new ModelContextProtocol.Server.RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer, ApprovalPrecheckTests.ServerProxy>())
                { Params = new CallToolRequestParams { Name = "ApplyGenerationPlan", Arguments = new Dictionary<string, JsonElement>() } };
            using var progress = Host.UseProgressRequest(request, CancellationToken.None);
            return Session.Apply((string)plan["planId"]!, (string)plan["planHash"]!, Project, phases, false).StructuredContent!.AsObject();
        }
        private CallToolResult Call(string name, JsonObject args, Func<CallToolResult?>? check)
        {
            if (name == "GetSessionState") return Host.V4Result(name, new JsonObject());
            if (name == "GetOpennessWorkerStatus") return Host.V4Result(name, new JsonObject { ["worker"] = new JsonObject { ["binding"] = new JsonObject { ["identity"] = Identity() } } });
            if (name == "GetProjectTree" && NewProject && !DeviceCreated && TransitionFault != "nonempty") return Host.V4Result(name, new JsonObject { ["tree"] = "```\nFixture\n  Ungrouped devices\n\n```" });
            if (name == "GetProjectTree") return Host.V4Result(name, new JsonObject { ["devices"] = new JsonArray(new JsonObject { ["kind"] = "device", ["name"] = "PLC1", ["articleNumber"] = "synthetic.article", ["version"] = "V1", ["slot"] = 0 }) });
            if (name == "GetSoftwareTree") return Host.V4Result(name, new JsonObject { ["paths"] = new JsonArray(Worker.Objects.Select(o => (JsonNode)new JsonObject { ["kind"] = o.Kind, ["name"] = o.Name, ["objectPath"] = o.Path })
                .Concat(Groups.Select(g => (JsonNode)new JsonObject { ["kind"] = "block-group", ["name"] = g.Split('/').Last(), ["objectPath"] = g })).ToArray()) });
            if (name is "GetPlcBlockInfo" or "GetPlcTypeInfo")
            { var value = Worker.Objects.Single(o => o.Path == (string?)args[name == "GetPlcBlockInfo" ? "blockPath" : "typePath"]); return Host.V4Result(name, new JsonObject { ["modifiedDate"] = value.ModifiedDate.ToString("O") }); }
            if (name == "GetPlcObjectFingerprints")
            { var value = Worker.Objects.Single(o => o.Path == (string?)args["objectPath"]); return Host.V4Result(name, new JsonObject { ["meta"] = new JsonObject { ["records"] = new JsonArray(new JsonObject { ["id"] = "Code", ["value"] = value.ModifiedDate.ToString("O") }) } }); }
            if (name == "ReadPlcUserConstants") return Host.V4Result(name, new JsonObject { ["items"] = new JsonArray() });
            if (name.StartsWith("List", StringComparison.Ordinal))
            {
                if (name == "ListPlcExternalSources" && ReadFailure && Worker.Previews > 0) throw new InvalidOperationException("Injected inventory read failure.");
                var items = name == "ListPlcExternalSources" ? Worker.Sources.Keys.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()
                    : name == "ListPlcTagTables" ? Tables.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()
                    : name == "ListPlcTags" ? Tags.Values.Where(t => (string?)t["table"] == (string?)args["table"]).Select(t => (JsonNode?)t.DeepClone()).ToArray() : Array.Empty<JsonNode?>();
                if (name == "ListPlcExternalSources" && Mutation == "sourcesBefore" && Worker.Previews > 0) items = items.Concat(new[] { (JsonNode?)JsonValue.Create("unexpected.scl") }).ToArray();
                return Host.V4Result(name, new JsonObject { ["items"] = new JsonArray(items) });
            }
            if (new[] { "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource", "PublishGenerationRecord" }.Contains(name))
            {
                if (name == "PublishGenerationRecord" && RecordRace && (bool?)args["dryRun"] != true)
                { RecordRace = false; File.WriteAllText((string)args["recordPath"]!, "changed by fixture"); }
                if (name == "PublishGenerationRecord" && RecordIo && (bool?)args["dryRun"] != true)
                { Directory.CreateDirectory((string)args["recordPath"]!); RecordIo = false; }
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
            if (name == "CompilePlcDiagnostics" && CompileFails) return Host.V4Result(name, new JsonObject { ["errorCount"] = 1 });
            if (name == "CreateDevice") DeviceCreated = true;
            if (name == "CreatePlcTagTable") Tables.Add((string)args["name"]!);
            if (name == "CreatePlcTag") Tags[(string)args["name"]!] = new JsonObject { ["name"] = (string)args["name"]!, ["table"] = (string)args["table"]!, ["dataType"] = (string)args["dataType"]!, ["value"] = (string)args["address"]! };
            if (name == "CreatePlcBlockGroup") Groups.Add((string)args["groupPath"]!);
            if (name == "CreateProject")
            {
                if (TransitionFault == "rejected") return Host.V4Reject(name, new TiaMcp.Logic.V4.Error("Injected creation refusal.", new TiaMcp.Logic.V4.PreconditionFailedDetails("fixture", null)));
                Created = true; BindingGeneration = TransitionFault == "epoch" ? "1" : "2";
            }
            if (name == "MovePlcBlockToGroup")
            {
                var block = Worker.Objects.Single(o => o.Name == (string)args["blockName"]!);
                block.Path = (string)args["groupPath"]! + "/" + block.Name;
            }
            OnWrite?.Invoke();
            return Host.V4Result(name, new JsonObject());
        }
        internal JsonObject Identity() => new() { ["projectPath"] = NewProject && !Created ? null : Created && TransitionFault == "unbound" ? "" : Created && TransitionFault == "path" ? "C:/Wrong/Wrong.ap21" : Created && TransitionFault == "normalized" ? Project.ToLowerInvariant().Replace('\\', '/') : Project,
            ["processId"] = Created && TransitionFault == "process" ? 124 : 123,
            ["processStartUtc"] = Created && TransitionFault == "start" ? "2026-10-10T01:00:00Z" : "2026-10-10T00:00:00Z", ["generation"] = BindingGeneration };
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
            else if (operation == "DeletePlcExternalSource")
            {
                string name = (string)args["externalSourceName"]!;
                var request = new PlcExternalSourceDeleteRequest { Release = fixture.Release, Project = fixture.Project, ProcessId = 123, Software = "PLC1", Name = name,
                    RootIdentity = "root", DryRun = dry, Confirm = (bool?)args["confirm"] == true, ExpectedProject = expected, ExpectedHash = hash };
                var deleted = PlcExternalSourceDeletePolicy.Run(request, () => Sources.Keys.Select(s => new PlcExternalSourceDeleteItem { Name = s, Identity = "identity:" + s, ParentVerified = true }),
                    () => { }, _ => { }, _ => { Writes++; Sources.Remove(name); fixture.OnWrite?.Invoke(); });
                return Task.FromResult(JsonSerializer.SerializeToNode(deleted, deleted.GetType()));
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
        Assert.True(f.Worker.Generations > 0); Assert.Empty(f.Worker.Sources); Assert.Equal(f.Worker.Generations * 3, f.Worker.Writes);
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
        using var f = new Fixture(release); var result = f.Apply(phases: GenerationPlanner.Phases.Where(p => p is not ("cleanup" or "save" or "record")).ToArray()); Assert.True((bool?)result["ok"]);
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
    [Theory, MemberData(nameof(Releases))]
    public void ActiveRunFencesOtherWritesAndKeepsReadsAndOwnGrant(string release)
    {
        using var f = new Fixture(release);
        int refusals = 0;
        f.OnWrite = () => {
            var blocked = Host.DispatchGenerationStep("ImportPlcExternalSource", new ToolArguments(JsonSerializer.SerializeToElement(new {
                softwarePath = "PLC1", groupPath = "", filePath = "C:/Other.scl", dryRun = false, confirm = true,
                expectedProjectFile = f.Project, expectedPlanHash = new string('a', 64) })), () => throw new Exception("fenced write dispatched"));
            Assert.Equal("PRECONDITION_FAILED", (string?)blocked.StructuredContent?["error"]?["code"]);
            Assert.Equal("generation-run-active", (string?)blocked.StructuredContent?["error"]?["details"]?["condition"]);
            Assert.NotEmpty((string)blocked.StructuredContent!["data"]!["runId"]!); refusals++;
            Assert.Null(Host.GenerationFenceRefusal("ListPlcExternalSources", "{\"softwarePath\":\"PLC1\"}"));
            Assert.Null(GenerationWriteFence.RefusedRun(new object(), release, "SaveProject", "{}"));
        };
        var result = f.Apply(); Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.True(refusals > 0);
        Assert.Null(GenerationWriteFence.RefusedRun(f.Worker.SessionKey, release, "SaveProject", "{}"));
        Assert.Equal(new[] { "acquire", "release" }, result["data"]!["events"]!.AsArray().OfType<JsonObject>()
            .Where(e => (string?)e["event"] == "fence").Select(e => (string?)e["action"]));
    }
    public static IEnumerable<object[]> StopTypes => Releases.SelectMany(r => new[] { "success", "failed", "unknown", "throw", "diverged", "effect", "cancelled", "cap", "shutdown" }.Select(s => new object[] { r[0], s }));
    [Theory, MemberData(nameof(StopTypes))]
    public void FenceReleasesForEveryStopAndShutdown(string release, string stop)
    {
        using var f = new Fixture(release, maximumSteps: stop == "cap" ? 1 : 10000);
        if (stop is "failed" or "unknown" or "throw") f.Failure = stop;
        if (stop == "diverged") f.Mutation = "inputSha256";
        if (stop == "effect") f.ExtraEffect = true;
        if (stop == "cancelled") f.OnWrite = () => f.Cancelled = true;
        if (stop == "shutdown") f.OnWrite = () => GenerationWriteFence.Shutdown(f.Worker.SessionKey);
        var result = f.Apply();
        Assert.Equal(stop switch { "success" => "succeeded", "throw" => "unknown", "effect" => "effect-diverged", "shutdown" => "cancelled", _ => stop }, (string?)result["data"]!["status"]);
        Assert.Null(GenerationWriteFence.RefusedRun(f.Worker.SessionKey, release, "SaveProject", "{}"));
        if (stop == "shutdown") Assert.True(GenerationWriteFence.Ended(f.Worker.SessionKey));
    }
    [Theory, MemberData(nameof(Releases))]
    public void RequestCancellationFinishesTheRunningWriteAndStopsBeforeTheNext(string release)
    {
        using var f = new Fixture(release); using var cancellation = new CancellationTokenSource();
        var request = new ModelContextProtocol.Server.RequestContext<CallToolRequestParams>(System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer, ApprovalPrecheckTests.ServerProxy>())
            { Params = new CallToolRequestParams { Name = "ApplyGenerationPlan" } };
        var plan = f.Plan(); f.OnWrite = () => {
            cancellation.Cancel(); Assert.False(Host.WorkerDispatchCancellation.IsCancellationRequested);
        };
        using var progress = Host.UseProgressRequest(request, cancellation.Token);
        var result = f.Session.Apply((string)plan["planId"]!, (string)plan["planHash"]!, f.Project, null, false).StructuredContent!;
        Assert.Equal("cancelled", (string?)result["data"]!["status"]);
        Assert.Equal("succeeded", (string?)result["data"]!["steps"]![0]!["status"]);
        Assert.All(result["data"]!["steps"]!.AsArray().Skip(1), row => Assert.Equal("NOT_EXECUTED", (string?)row!["status"]));
        Assert.Equal(1, f.Approvals);
    }
    [Theory, MemberData(nameof(Releases))]
    public void NewProjectRebindsWithinTheSameApprovedRun(string release)
    {
        using var f = new Fixture(release, newProject: true); var plan = f.Plan();
        Assert.Equal("CreateProject", (string?)plan["steps"]![0]!["tool"]);
        Assert.Equal("transition", (string?)plan["steps"]![0]!["review"]?["mode"]);
        Assert.Equal(f.Project, (string?)plan["target"]!["projectIdentity"]);
        Assert.DoesNotContain(plan["steps"]!.AsArray(), row => (string?)row!["tool"] == "ArchiveSavedProject");
        Assert.All(plan["steps"]!.AsArray().Where(row => row!["arguments"]!["expectedProjectFile"] != null), row => Assert.Equal(f.Project, (string?)row!["arguments"]!["expectedProjectFile"]));
        var result = f.Apply(plan); Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.Equal(1, f.Approvals);
        Assert.Equal(f.Project, (string?)result["data"]!["createdProjects"]![0]); Assert.True(f.Worker.Generations > 0);
        Assert.Single(result["data"]!["events"]!.AsArray().Where(row => (string?)row!["event"] == "transition"));
    }
    public static IEnumerable<object[]> TransitionFaults => Releases.SelectMany(r => new[] { "rejected", "path", "process", "start", "epoch", "nonempty" }.Select(f => new object[] { r[0], f }));
    [Theory, MemberData(nameof(TransitionFaults))]
    public void NewProjectFaultStopsRetainsCreationAndCanReplanExisting(string release, string fault)
    {
        using var f = new Fixture(release, newProject: true); f.TransitionFault = fault; var result = f.Apply();
        Assert.Equal(fault == "rejected" ? "rejected-before-operation" : "partial", (string?)result["meta"]!["outcome"]);
        Assert.Equal(fault == "rejected" ? "rejected-before-operation" : "diverged", (string?)result["data"]!["status"]);
        Assert.Equal(0, f.Worker.Writes); Assert.Equal(fault != "rejected", f.Created);
        if (fault != "rejected")
        {
            Assert.Equal("transition-diverged", (string?)result["data"]!["stoppingReason"]);
            Assert.NotEmpty(result["data"]!["createdProjects"]!.AsArray());
            f.TransitionFault = null;
            f.Planner.Machine.Target.Project.Mode = "existing"; f.Planner.Machine.Target.Project.ProjectIdentity = f.Project;
            f.Planner.Machine.Target.Project.Directory = null; f.Planner.Machine.Target.Project.Name = null;
            Assert.Equal("existing", (string?)f.Plan()["target"]!["mode"]);
        }
        Assert.All(result["data"]!["steps"]!.AsArray().Skip(1), row => Assert.Equal("NOT_EXECUTED", (string?)row!["status"]));
        Assert.Null(GenerationWriteFence.RefusedRun(f.Worker.SessionKey, release, "SaveProject", "{}"));
        if (fault == "path")
        {
            using var unbound = new Fixture(release, newProject: true); unbound.TransitionFault = "unbound";
            var missing = unbound.Apply();
            Assert.Equal("partial", (string?)missing["meta"]!["outcome"]);
            Assert.Equal("transition-diverged", (string?)missing["data"]!["stoppingReason"]);
            Assert.Equal(unbound.Project, (string?)missing["data"]!["createdProjects"]![0]);
            Assert.Equal(0, unbound.Worker.Writes); Assert.True(unbound.Created);
            Assert.All(missing["data"]!["steps"]!.AsArray().Skip(1), row => Assert.Equal("NOT_EXECUTED", (string?)row!["status"]));
            Assert.Null(GenerationWriteFence.RefusedRun(unbound.Worker.SessionKey, release, "SaveProject", "{}"));
        }
    }
    [Theory, MemberData(nameof(Releases))]
    public void PredictedProjectFileMatchesAdapterAndMutationIdentity(string release)
    {
        foreach (var directory in new[] { "C:/Projects", "c:/Projects/../Other/", @"\\server\share\folder\.\child\.." })
        {
            string predicted = GenerationProjectPath.CreationFile(release, directory, "Fixture");
            Assert.Equal(PlcLifecyclePolicy.CreationFile(release, directory, "Fixture"), predicted);
            MutationIdentityPolicy.ValidateTarget(false, true, predicted, "CreateProject", release, "", directory, "Fixture", _ => throw new Exception("must be unbound"));
        }
        foreach (var name in new[] { "CON", "a/b", "..", "A.", "A ", "x:y" })
            Assert.Throws<ArgumentException>(() => GenerationProjectPath.CreationFile(release, "C:/Projects", name));
    }

    [Theory, MemberData(nameof(Releases))]
    public void LaterNewProjectFailureRetainsTheCreatedProjectAndNewBinding(string release)
    {
        using var f = new Fixture(release, newProject: true); f.Failure = "failed";
        var result = f.Apply(); Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
        Assert.Equal("failed", (string?)result["data"]!["status"]); Assert.True(f.Created);
        Assert.Equal(f.Project, (string?)result["data"]!["createdProjects"]![0]);
        Assert.Equal(f.Project, (string?)f.Identity()["projectPath"]);
        Assert.Equal("succeeded", (string?)result["data"]!["steps"]![0]!["status"]);
        Assert.Null(GenerationWriteFence.RefusedRun(f.Worker.SessionKey, release, "SaveProject", "{}"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void NewBindingAcceptsNormalizedCaseInsensitiveProjectFile(string release)
    {
        using var f = new Fixture(release, newProject: true); f.TransitionFault = "normalized";
        var result = f.Apply(); Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(1, f.Approvals); Assert.True(f.Worker.Generations > 0);
        var lineage = new GenerationRunStore(Path.Combine(f.Root, "runs")).Lineage((string)f.Identity()["projectPath"]!, release);
        Assert.Equal((string?)result["data"]!["runId"], lineage!["runId"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void FullRunPublishesPostSaveWitnessesAndReplansToZeroSteps(string release)
    {
        using var f = new Fixture(release); var first = f.Plan(); var result = f.Apply(first);
        Assert.True((bool?)result["ok"], result.ToJsonString());
        var record = GenerationRecord.Parse(File.ReadAllText(GenerationRecordFile.DefaultPath(f.Project)));
        Assert.All(record.Objects, o => { Assert.Equal("owned", o.State); Assert.NotNull(o.Witness); });
        Assert.All(record.Objects.Where(o => o.Key.Contains("/block/")), o => Assert.Equal(release is "20" or "21" ? "plc-object-fingerprints" : "modified-date", o.Witness!.Kind));
        var second = f.Plan(); Assert.Empty(second["steps"]!.AsArray());
        Assert.Contains(second["skipped"]!.AsArray(), s => (string?)s!["reason"] == "owned-unchanged");
        Assert.NotEqual((string?)first["planId"], (string?)second["planId"]); Assert.Equal(second.ToJsonString(), f.Plan().ToJsonString());
        Assert.Equal(1, f.Approvals);
    }
    [Theory, MemberData(nameof(Releases))]
    public void ManualBlockChangeConflictsAndNeverPlansAnOverwrite(string release)
    {
        using var f = new Fixture(release); Assert.True((bool?)f.Apply()["ok"]);
        var block = f.Worker.Objects.First(); block.ModifiedDate = block.ModifiedDate.AddMinutes(1);
        var plan = f.Plan(); string key = "PLC1/" + block.Kind + "/" + block.Name;
        Assert.Contains(plan["conflicts"]!.AsArray(), c => (string?)c!["key"] == key && (string?)c["reason"] == "modified-after-generation");
        Assert.DoesNotContain(plan["steps"]!.AsArray(), s => (string?)s!["key"] == key);
    }
    [Theory, MemberData(nameof(Releases))]
    public void ChangedDesiredContentRequiresRegenerationAndNeverOverwrites(string release)
    {
        using var f = new Fixture(release); Assert.True((bool?)f.Apply()["ok"]);
        f.Planner.Sources["sources/motor.scl"] = f.Planner.Sources["sources/motor.scl"].Replace("#Run := #Feedback;", "#Run := NOT #Feedback;");
        var plan = f.Plan(); Assert.Contains(plan["conflicts"]!.AsArray(), c => (string?)c!["key"] == "PLC1/block/FB_Motor" && (string?)c["reason"] == "regeneration-required");
        Assert.DoesNotContain(plan["steps"]!.AsArray(), s => (string?)s!["key"] == "PLC1/block/FB_Motor");
    }
    [Theory, MemberData(nameof(Releases))]
    public void TamperedRecordFallsBackToDurableWitnessFacts(string release)
    {
        using var f = new Fixture(release); var result = f.Apply(); Assert.True((bool?)result["ok"]);
        string path = GenerationRecordFile.DefaultPath(f.Project); var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["objects"]![0]!["expectedFingerprint"] = "sha256:" + new string('f', 64); File.WriteAllText(path, node.ToJsonString());
        Assert.Throws<ArgumentException>(() => GenerationRecord.Parse(File.ReadAllText(path)));
        Assert.Empty(f.Plan()["steps"]!.AsArray());
    }
    [Theory, MemberData(nameof(Releases))]
    public void PreviousRecordRaceStopsPartialAndKeepsWitnessesForReplanning(string release)
    {
        using var f = new Fixture(release); f.RecordRace = true; var result = f.Apply();
        Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
        Assert.Contains(result["data"]!["events"]!.AsArray(), e => (string?)e!["event"] == "witness");
        Assert.Empty(f.Plan()["steps"]!.AsArray()); Assert.False(f.Worker.Uncertain);
        var preview = f.Session.Publish(GenerationRecordFile.DefaultPath(f.Project), "", (string)result["data"]!["runId"]!, true, "").StructuredContent!["data"]!;
        Assert.Equal("changed by fixture", File.ReadAllText(GenerationRecordFile.DefaultPath(f.Project)));
        Assert.NotEmpty((string)preview["planHash"]!);
    }
    [Theory, MemberData(nameof(Releases))]
    public void UnknownExistingObjectNeedsExplicitAdoptionAndGetsAnAdoptedWitness(string release)
    {
        using var f = new Fixture(release); f.Failure = "unknown"; var result = f.Apply(); Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        f.Failure = null; f.Worker.Uncertain = false; f.Worker.Sources.Clear();
        f.Worker.Objects.Add(new PlcExternalSourceObject { Kind = "block", Name = "FB_Motor", Path = "FB_Motor", ModifiedDate = new DateTime(2026, 10, 10) });
        var plan = f.Plan(); Assert.Contains(plan["conflicts"]!.AsArray(), c => (string?)c!["key"] == "PLC1/block/FB_Motor" && (string?)c["reason"] == "unknown-outcome");
        var adopted = f.Plan(new[] { "PLC1/block/FB_Motor" }); Assert.Contains(adopted["skipped"]!.AsArray(), s => (string?)s!["key"] == "PLC1/block/FB_Motor" && (string?)s["reason"] == "adopted");
        Assert.True((bool?)f.Apply(adopted)["ok"]);
        var record = GenerationRecord.Parse(File.ReadAllText(GenerationRecordFile.DefaultPath(f.Project)));
        var item = Assert.Single(record.Objects.Where(o => o.Key == "PLC1/block/FB_Motor")); Assert.Equal("adopted", item.State); Assert.NotNull(item.Witness);
        Assert.Empty(f.Plan()["steps"]!.AsArray());
    }
    [Theory, MemberData(nameof(Releases))]
    public void CompileFailureRetainsSourcesAndSuccessfulCompileDeletesOnlyThisRunSources(string release)
    {
        using (var f = new Fixture(release))
        {
            f.Worker.Sources["manual.scl"] = "retained"; f.CompileFails = true; var result = f.Apply();
            Assert.Equal("partial", (string?)result["meta"]!["outcome"]); Assert.Equal("failed", (string?)result["data"]!["status"]);
            Assert.True(f.Worker.Sources.Count > 1);
            Assert.All(result["data"]!["steps"]!.AsArray().Where(s => (string?)s!["phase"] is "cleanup" or "save" or "record"), s => Assert.Equal("NOT_EXECUTED", (string?)s!["status"]));
        }
        using (var f = new Fixture(release))
        {
            f.Worker.Sources["manual.scl"] = "retained"; Assert.True((bool?)f.Apply()["ok"]); Assert.Equal(new[] { "manual.scl" }, f.Worker.Sources.Keys);
        }
    }
    [Theory, MemberData(nameof(Releases))]
    public void StandalonePreviewDetectsAChangedPreviousRecordBeforeWriting(string release)
    {
        using var f = new Fixture(release); var result = f.Apply(); Assert.True((bool?)result["ok"]);
        string path = GenerationRecordFile.DefaultPath(f.Project), runId = (string)result["data"]!["runId"]!;
        var preview = f.Session.Publish(path, "", runId, true, "").StructuredContent!["data"]!;
        File.AppendAllText(path, " "); var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => f.Session.Publish(path, "", runId, false, (string)preview["planHash"]!)); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory, MemberData(nameof(Releases))]
    public void PublicationFileFailureLeavesSaveSuccessfulAndRunPartialWithoutPoisoningTheWorker(string release)
    {
        using var f = new Fixture(release); f.RecordIo = true; var result = f.Apply();
        Assert.Equal("partial", (string?)result["meta"]!["outcome"]); Assert.Equal("partial", (string?)result["data"]!["status"]);
        Assert.Contains(result["data"]!["steps"]!.AsArray(), s => (string?)s!["tool"] == "SaveProject" && (string?)s["status"] == "succeeded");
        Assert.Contains(result["data"]!["events"]!.AsArray(), e => (string?)e!["event"] == "witness");
        var record = Assert.Single(result["data"]!["steps"]!.AsArray(), s => (string?)s!["tool"] == "PublishGenerationRecord");
        Assert.Contains((string?)record!["result"]!["error"]!["code"], new[] { "IO_FAILED", "PARTIAL_FAILURE" });
        Assert.False(f.Worker.Uncertain); Assert.Empty(f.Worker.Sources);
    }

    [Theory, MemberData(nameof(Releases))]
    public void OwnedRetainedSourceIsDeletedAndReimportedBeforeFillingAMissingBlock(string release)
    {
        using var f = new Fixture(release); var initial = f.Plan(); Assert.True((bool?)f.Apply(initial)["ok"]);
        var block = f.Worker.Objects.Single(o => o.Name == "FB_Motor"); f.Worker.Objects.Remove(block);
        var generate = initial["steps"]!.AsArray().Single(s => (string?)s!["key"] == "PLC1/block/FB_Motor");
        string source = (string)generate!["arguments"]!["externalSourceName"]!;
        var import = initial["steps"]!.AsArray().Single(s => (string?)s!["tool"] == "ImportPlcExternalSource" && (string?)s["expect"]!["contains"] == source);
        f.Worker.Sources[source] = File.ReadAllText((string)import!["arguments"]!["filePath"]!);
        string path = GenerationRecordFile.DefaultPath(f.Project); var record = GenerationRecord.Parse(File.ReadAllText(path));
        var owned = record.Objects.Single(o => o.Key == "PLC1/externalSource/" + source); owned.Witness!.Values["present"] = JsonSerializer.SerializeToElement(true);
        record.RecordHash = GenerationRecord.Hash(record); File.WriteAllText(path, GenerationDocuments.Canonical(record));
        var plan = f.Plan(); var replacement = Assert.Single(plan["steps"]!.AsArray().Where(s => ((string?)s!["key"])?.EndsWith("/replace") == true));
        Assert.Equal(StepReviewRules.Delete, (string?)replacement!["review"]!["rule"]);
        var newImport = plan["steps"]!.AsArray().Single(s => (string?)s!["tool"] == "ImportPlcExternalSource");
        Assert.Contains(newImport!["dependsOn"]!.AsArray(), n => (string?)n == (string?)replacement["id"]);
        var result = f.Apply(plan); Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.Empty(f.Worker.Sources); Assert.Empty(f.Plan()["steps"]!.AsArray());
    }

}
