extern alias enginefixture;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.BehaviorParity;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;
using TiaMcp.Logic.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

public sealed class BehaviorParityTests
{
    public static IEnumerable<object[]> Cases => BehaviorParityCases.All;
    private sealed class Worker(string scenario) : IFoundationWorker
    {
        internal int Previews, Writes;
        internal bool Followup;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            bool preview = (bool?)args["dryRun"] == true || operation == "ListTags";
            if (preview) Previews++; else Writes++;
            if (scenario == "batch-stale") return Task.FromResult(JsonSerializer.SerializeToNode(new PlcBatchImportResult { Executed = !preview, ProjectFile = "C:/fixture.ap19", SoftwarePath = "CPU/PLC_1", Release = "19", PlanHash = new string('a', 64),
                Items = new[] { new PlcBatchImportItem { RelativePath = "A.xml", InputSha256 = new string('a',64), Planned = new PlcBatchImportObject { Name = "A", Kind = "FC", Number = 1 } } } }));
            if (Followup && operation == "ListTags") return Task.FromResult<JsonNode?>(new JsonArray());
            if (Followup) return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = !preview, ["ProjectFile"] = "C:/fixture.ap19" });
            Exception? error = scenario switch {
                "argument" => new AdapterPreconditionException("Fixture argument refusal.", "name"),
                "wrapped-argument" => new InvalidOperationException("Private wrapper text.", new AdapterPreconditionException("Fixture argument refusal.", "name")),
                "existing-no-overwrite" => new AdapterPreconditionException("Existing object; overwrite=false.", "overwrite"),
                "precondition" => new AdapterPreconditionException("Fixture state refusal.", "softwarePath", false),
                "cancellation" => new OperationCanceledException(),
                "unknown" when !preview => new IOException("Fixture native interruption."), _ => null };
            if (scenario == "native-read") error = new IOException("Fixture native interruption.");
            try
            {
                if (scenario == "missing-directory" || scenario == "file-access-denied")
                    NativeInputPolicy.Read<int>("filePath", () => throw (scenario == "missing-directory" ? (Exception)new DirectoryNotFoundException() : new UnauthorizedAccessException()));
                BehaviorParityCases.ExportAdmission(scenario);
                if (scenario.StartsWith("blocked-export", StringComparison.Ordinal))
                {
                    if (!preview) NativeExportPolicy.RequireApply("inconsistent");
                    return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = false, ["Kind"] = "technology-object", ["Status"] = "inconsistent", ["PlanHash"] = new string('a',64) });
                }
                if (error != null) throw error;
                var selected = PlcReadPathPolicy.Resolve(new[] { new PlcReadCandidate<string> { Value = "PLC_1", ExactPath = "CPU/PLC_1", Device = "CPU", Host = "PLC_1" } }, (string)args["plc"]!);
                Assert.Equal("PLC_1", selected);
                var data = new JsonObject { ["Executed"] = !preview, ["ProjectFile"] = "C:/fixture.ap19" };
                if (scenario == "no-effect") { data["Executed"] = false; data["Status"] = "not-found-not-deleted";
                    data["Attempted"] = false; data["Deleted"] = false; data["TargetIdentity"] = ""; }
                return Task.FromResult<JsonNode?>(data);
            }
            catch (Exception cause)
            {
                var classified = WorkerFailurePolicy.Classify(cause, true, preview);
                throw new WorkerOperationException(cause.Message, classified.Code,
                    classified.Outcome == TiaMcp.WorkerChannel.ChannelOutcome.Unknown ? "unknown"
                    : classified.Outcome == TiaMcp.WorkerChannel.ChannelOutcome.ReadFailed ? "read-failed" : "rejected-before-operation",
                    JsonSerializer.Serialize(new { exceptionType = cause.GetType().Name, parameter = HostFailurePolicy.Parameter(cause) }));
            }
        }
        public void Dispose() { }
    }
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args) => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>())
    { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Dispatch_paths_have_identical_behavior(string scenario, string code, string outcome, string execution)
    {
        var engine = JsonNode.Parse(enginefixture::TiaMcpServer.Tests.BehaviorParityEngine.Run(scenario))!;
        if (Environment.GetEnvironmentVariable("TIA_MCP_PARITY_ENGINE_RELEASE") is string expectedRelease)
            Assert.Equal(expectedRelease, (string?)engine["engineRelease"]);
        engine.AsObject().Remove("engineRelease");
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
        {
            if (scenario.StartsWith("staging-", StringComparison.Ordinal))
            {
                string bundle = Path.GetFullPath(Path.Combine("bin-build/P6-67r/parity-stage", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(bundle);
                var store = new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N")); int approvals = 0;
                try
                {
                    string source = scenario == "staging-cleanup" ? "CleanupStagedImportFiles" : "StageImportFiles";
                    var inner = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source), new Worker(scenario), store);
                    var tool = new FoundationV4Tool(inner, release, null, () => new(true, 1), (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, scenario == "staging-refused" ? "denied" : null)); });
                    var body = (await tool.InvokeAsync(Request(source, BehaviorParityCases.Arguments(scenario)))).StructuredContent!;
                    var row = BehaviorParityCases.Project(body);
                    Assert.Equal(code, (string?)row["code"]); Assert.Equal(outcome, (string?)row["outcome"]); Assert.Equal(execution, (string?)row["execution"]);
                    var stagedFoundation = new JsonObject { ["results"] = new JsonArray(row), ["waits"] = approvals, ["writes"] = store.List()["batches"]!.AsArray().Count, ["previews"] = 0 };
                    Assert.True(JsonNode.DeepEquals(engine, stagedFoundation), scenario + " " + release + "\nengine=" + engine + "\nfoundation=" + stagedFoundation);
                }
                finally { Directory.Delete(bundle, true); }
                continue;
            }
            var worker = new Worker(scenario); int waits = 0;
            FoundationV4Tool Tool(string source) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source), worker), release, null,
                () => new(true, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, scenario == "refused-approval" ? "denied" : null)); });
            async Task<JsonObject> Call(string source, JsonObject args)
            {
                var tool = Tool(source);
                return BehaviorParityCases.Project((await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args))).StructuredContent!);
            }
            var results = new JsonArray(await Call(scenario == "native-read" ? "ReadPlcTags"
                : scenario == "missing-directory" || scenario.StartsWith("batch-", StringComparison.Ordinal) && scenario != "batch-alias" ? "ImportBlocksFromDirectory" : scenario == "missing-file" ? "ImportPlcExternalSource" : "CreatePlcTag", BehaviorParityCases.Arguments(scenario)));
            Assert.Equal(code, (string?)results[0]?["code"]); Assert.Equal(outcome, (string?)results[0]?["outcome"]); Assert.Equal(execution, (string?)results[0]?["execution"]);
            if (scenario.StartsWith("blocked-export", StringComparison.Ordinal) || scenario == "typed-export-refusal")
            {
                worker.Followup = true;
                results.Add(await Call("ReadPlcTags", new JsonObject { ["plc"] = "PLC_1", ["table"] = "T" }));
                results.Add(await Call("CreatePlcTag", BehaviorParityCases.Arguments("followup")));
                Assert.All(results.Skip(1), row => { Assert.Equal("", (string?)row?["code"]); Assert.False((bool?)row?["reset"]); });
            }
            if (scenario == "unknown")
            {
                results.Add(await Call("ReadPlcTags", new JsonObject { ["plc"] = "PLC_1", ["table"] = "T" }));
                results.Add(await Call("CreatePlcTag", BehaviorParityCases.Arguments(scenario)));
                Assert.All(results.Skip(1), row => { Assert.Equal("SESSION_RESET_REQUIRED", (string?)row?["code"]); Assert.Equal("not-started", (string?)row?["execution"]); });
            }
            var foundation = new JsonObject { ["results"] = results, ["waits"] = waits, ["writes"] = worker.Writes, ["previews"] = worker.Previews };
            Assert.True(JsonNode.DeepEquals(engine, foundation), scenario + " " + release + "\nengine=" + engine + "\nfoundation=" + foundation);
        }
    }
}
