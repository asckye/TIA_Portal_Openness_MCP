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
            if (scenario.StartsWith("compile-", StringComparison.Ordinal))
            {
                var data = BehaviorParityCases.CompileData(); if (scenario == "compile-zero-count") data["errorCount"] = 0;
                data["executed"] = !preview;
                data["messages"] = data["errors"]!.DeepClone(); data["warnings"] = new JsonArray(); data["info"] = new JsonArray();
                if (preview) { data["state"] = null; data["errorCount"] = null; data["warningCount"] = null; data["errors"] = new JsonArray(); data["messages"] = new JsonArray(); }
                return Task.FromResult<JsonNode?>(new JsonObject(data.Select(p => new KeyValuePair<string, JsonNode?>(char.ToUpperInvariant(p.Key[0]) + p.Key.Substring(1), p.Value?.DeepClone()))));
            }
            if (scenario is "batch-inconsistent" or "batch-protected" or "batch-unknown-consistency")
            {
                var data = BehaviorParityCases.BatchData(scenario);
                // Foundation mapping accepts the worker's PascalCase transport fields.
                JsonNode? Pascal(JsonNode? node) => node is JsonArray rows ? new JsonArray(rows.Select(Pascal).ToArray()) : node is JsonObject obj
                    ? new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(char.ToUpperInvariant(p.Key[0]) + p.Key.Substring(1), Pascal(p.Value)))) : node?.DeepClone();
                return Task.FromResult(Pascal(data));
            }
            if (scenario.StartsWith("single-", StringComparison.Ordinal))
            {
                JsonObject data;
                try { data = BehaviorParityCases.SingleBackup(scenario, preview); }
                catch (AdapterPreconditionException cause) { throw new WorkerOperationException(cause.Message, -32603, "rejected-before-operation", JsonSerializer.Serialize(new { exceptionType = cause.GetType().Name, parameter = cause.ParamName })); }
                if (!preview && scenario == "single-import-failed") throw new WorkerOperationException("Fixture native interruption.", -32603, "unknown", JsonSerializer.Serialize(new {
                    exceptionType = "IOException", recoveryDirectory = data["recoveryDirectory"], recoveryStatus = data["recoveryStatus"], recoveryWarning = data["recoveryWarning"], recoverySkipped = data["recoverySkipped"] }));
                return Task.FromResult<JsonNode?>(new JsonObject(data.Select(p => new KeyValuePair<string, JsonNode?>(char.ToUpperInvariant(p.Key[0]) + p.Key.Substring(1), p.Value?.DeepClone()))));
            }
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
                if (scenario == "missing-directory" || scenario == "file-access-denied" || scenario == "file-locked")
                    NativeInputPolicy.Read<int>("filePath", () => throw (scenario == "missing-directory" ? (Exception)new DirectoryNotFoundException()
                        : scenario == "file-locked" ? new IOException("Fixture sharing violation.", unchecked((int)0x80070020)) : new UnauthorizedAccessException()));
                BehaviorParityCases.ExportAdmission(scenario);
                if (scenario.StartsWith("blocked-export", StringComparison.Ordinal))
                {
                    NativeExportPolicy.RequireApply("inconsistent");
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
        if(scenario.StartsWith("disabled-",StringComparison.Ordinal)) { await DisabledBatch(engine,scenario,code);return; }
        bool enabled=!scenario.EndsWith("-disabled",StringComparison.Ordinal);
        if(!enabled) scenario=scenario.Substring(0,scenario.Length-9);
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
        {
            if (scenario.StartsWith("staging-", StringComparison.Ordinal))
            {
                string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(bundle);
                var store = new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N")); int approvals = 0;
                try
                {
                    string source = BehaviorParityCases.StagingTool(scenario);
                    var arguments = BehaviorParityCases.StagingArguments(scenario, store, bundle, release);
                    var inner = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source), new Worker(scenario), store);
                    var tool = new FoundationV4Tool(inner, release, null, () => new(enabled, 1), (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, !enabled, scenario == "staging-refused" ? "denied" : null)); });
                    var body = (await tool.InvokeAsync(Request(source, arguments))).StructuredContent!;
                    var row = BehaviorParityCases.Project(body);
                    Assert.Equal(code, (string?)row["code"]); Assert.Equal(outcome, (string?)row["outcome"]); Assert.Equal(execution, (string?)row["execution"]);
                    Assert.False((bool?)row["nativeWarning"]);
                    if (scenario is "staging-extra-cleanup" or "staging-modified-cleanup") Assert.NotNull(row["stagingWarning"]);
                    if (enabled && scenario is ("staging-name" or "staging-cleanup" or "staging-live-cleanup")) Assert.NotNull(row["precheckWarning"]);
                    var stagedFoundation = new JsonObject { ["results"] = new JsonArray(row), ["waits"] = approvals, ["writes"] = store.List()["batches"]!.AsArray().Count, ["previews"] = 0 };
                    Assert.True(JsonNode.DeepEquals(engine, stagedFoundation), scenario + " " + release + "\nengine=" + engine + "\nfoundation=" + stagedFoundation);
                }
                finally { Directory.Delete(bundle, true); }
                continue;
            }
            var worker = new Worker(scenario); int waits = 0;
            FoundationV4Tool Tool(string source) => new(new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == source), worker), release, null,
                () => new(enabled, 1), (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, !enabled, scenario == "refused-approval" ? "denied" : null)); });
            async Task<JsonObject> Call(string source, JsonObject args)
            {
                if (scenario is "single-legacy-group-missing" or "single-type-group-missing" or "single-table-group-missing") { args["dryRun"] = false; args["confirm"] = true; args["expectedProjectFile"] = "C:/fixture.ap19"; }
                if (source == "CompileSoftware") { args["confirm"] = true; args["expectedProjectFile"] = "C:/fixture.ap19"; }
                var tool = Tool(source);
                var body = (await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args))).StructuredContent!;
                if (source == "CompileSoftware") Assert.True((string?)body["error"]?["code"] == "COMPILE_ERRORS", body.ToJsonString());
                return BehaviorParityCases.Project(body);
            }
            var results = new JsonArray(await Call(scenario=="single-type-group-missing" ? "ImportType" : scenario=="single-table-group-missing" ? "ImportPlcTagTable" : scenario == "single-legacy-group-missing" ? "ImportBlock" : scenario.StartsWith("compile-", StringComparison.Ordinal) ? "CompileSoftware" : scenario == "native-read" ? "ReadPlcTags"
                : scenario == "missing-directory" || scenario.StartsWith("batch-", StringComparison.Ordinal) && scenario != "batch-alias" ? "ImportBlocksFromDirectory" : scenario == "missing-file" ? "ImportPlcExternalSource" : "CreatePlcTag", BehaviorParityCases.Arguments(scenario)));
            Assert.Equal(code, (string?)results[0]?["code"]); Assert.Equal(outcome, (string?)results[0]?["outcome"]); Assert.Equal(execution, (string?)results[0]?["execution"]);
            if (enabled && scenario is ("argument" or "single-group-missing" or "single-legacy-group-missing" or "batch-inconsistent"))
            { Assert.NotNull(results[0]?["precheckWarning"]); Assert.False((bool?)results[0]?["nativeWarning"]); }
            if (scenario is "single-inconsistent" or "single-export-refused" or "single-import-failed" or "single-native-warning")
            {
                var details = results[0]!["backupDetails"]!;
                Assert.Equal("Group/Uncompiled", (string?)details["objects"]?[0]?["object"]);
                Assert.Equal(scenario == "single-inconsistent" ? "inconsistent" : "export-or-validation-refused (IOException)", (string?)details["objects"]?[0]?["reason"]);
                Assert.True(details.AsObject().ContainsKey("recoveryDirectory"));
                Assert.Equal(scenario == "single-inconsistent" ? null : Path.GetFullPath("bin-build/P6-68"), (string?)details["recoveryDirectory"]);
                Assert.Equal(scenario == "single-native-warning", (bool?)results[0]?["nativeWarning"]);
            }
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
    private sealed class LocalBatchWorker(TiaMcpServer.Tests.BatchReplacementPolicyTests.AdmissionFixture fixture) : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation,JsonObject args,CancellationToken token)
        {
            if(operation=="ListTags") return Task.FromResult<JsonNode?>(new JsonArray());
            if(operation=="CreateTag") return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"]=true,["ProjectFile"]="C:/fixture.ap19" });
            try
            {
                var result=JsonSerializer.SerializeToNode(fixture.Execute(args));WorkerProtocol.ValidateExchangeResult(operation,args,result);
                return Task.FromResult(result);
            }
            catch(Exception cause)
            {
                var classified=WorkerFailurePolicy.Classify(cause,true,false);
                throw new WorkerOperationException(cause.Message,classified.Code,classified.Outcome==TiaMcp.WorkerChannel.ChannelOutcome.Unknown ? "unknown" : "rejected-before-operation",
                    JsonSerializer.Serialize(new { exceptionType=cause.GetType().Name,parameter=HostFailurePolicy.Parameter(cause),isArgument=cause is AdapterPreconditionException p ? p.IsArgument : (bool?)null }));
            }
        }
        public void Dispose() { }
    }
    private static async Task DisabledBatch(JsonNode engine,string value,string code)
    {
        bool program=value.StartsWith("disabled-program-",StringComparison.Ordinal);
        foreach(string release in new[]{"14sp1","15.1","16","17","18","19"})
        {
            // Foundation compiles its own policy copy: do not route execution through the engine fixture assembly.
            using var fixture=new TiaMcpServer.Tests.BatchReplacementPolicyTests.AdmissionFixture(value.Substring(program ? 17 : 15),program);
            using var worker=new LocalBatchWorker(fixture);
            async Task<JsonNode> Call(string source,JsonObject args)
            {
                var tool=new FoundationV4Tool(new FoundationTool(FoundationTools.Definitions.Single(d=>d.Name==source),worker),release,null,
                    ()=>new(false,1),(pending,current,_)=> { Assert.False(current.Enabled);return Task.FromResult(new ApprovalOutcome(pending,true,null)); });
                return (await tool.InvokeAsync(Request(tool.ProtocolTool.Name,args))).StructuredContent!;
            }
            var body=await Call(program ? "ImportPlcProgramFromDirectory" : "ImportBlocksFromDirectory",fixture.Arguments());
            Assert.Equal(code,(string?)body["error"]?["code"]);Assert.Equal("rejected-before-operation",(string?)body["meta"]?["outcome"]);Assert.Equal("not-started",(string?)body["meta"]?["execution"]);
            Assert.False((bool?)body["meta"]?["requiresSessionReset"]);Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(),w=>(string?)w?["code"]=="APPROVAL_PRECHECK_REFUSED");
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(),w=>(string?)w?["code"]=="APPROVAL_DISABLED");
            Assert.Equal(value.EndsWith("backup-io",StringComparison.Ordinal) ? new[]{"directory","backup"} : value.Substring(program ? 17 : 15)=="recheck-io" ? new[]{"directory"} : Array.Empty<string>(),fixture.Calls);
            var results=new JsonArray(BehaviorParityCases.Project(body));
            results.Add(BehaviorParityCases.Project(await Call("ReadPlcTags",new JsonObject { ["plc"]="PLC_1",["table"]="T" })));
            results.Add(BehaviorParityCases.Project(await Call("CreatePlcTag",BehaviorParityCases.Arguments("followup"))));
            Assert.All(results.Skip(1),row=> { Assert.Equal("",(string?)row?["code"]);Assert.False((bool?)row?["reset"]); });
            var foundation=new JsonObject { ["results"]=results,["calls"]=JsonSerializer.SerializeToNode(fixture.Calls) };
            Assert.True(JsonNode.DeepEquals(engine,foundation),value+" "+release+"\nengine="+engine+"\nfoundation="+foundation);
        }
    }

}
