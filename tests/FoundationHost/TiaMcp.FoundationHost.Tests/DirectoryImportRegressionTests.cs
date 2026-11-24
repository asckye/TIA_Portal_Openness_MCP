extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Organisation = toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService;

[Collection(BundleSelectionCollection.Name)]
public sealed class DirectoryImportRegressionTests
{
    [Theory]
    [InlineData(0, false, "rejected-before-operation")]
    [InlineData(1, false, "partial")]
    [InlineData(1, true, "unknown")]
    public void Technology_batch_distinguishes_confirmed_files_from_returned_identities(int completed, bool unconfirmed, string outcome)
    {
        const string tool = "ImportTechnologyObjectsFromDirectory";
        using var release = Host.HardwareContract.UseRelease("16");
        var names = completed == 1 ? new List<string> { "AxisA", "AxisB" } : [];
        if (unconfirmed) names.Add("PartlyRead");
        var reason = unconfirmed ? "An imported object identity could not be read." : "Technology object already exists.";
        var batch = new { Imported = names, Failed = new[] { new { Path = "Axis.xml", Error = reason } },
            Meta = new { success = false, mayHaveChanged = names.Count > 0, completedImports = completed, writeOutcomeUnknown = unconfirmed } };
        var service = new Organisation((_, _) => JsonSerializer.SerializeToNode(new { Batch = batch, MayHaveChanged = names.Count > 0,
            OutcomeUnknown = unconfirmed }), () => true, () => "Fixture.ap16");
        var tools = new Host.PlcTechnologyTools(service);
        var body = Host.EngineeringToolContract.Run(tool, true, () => tools.ImportTechnologyObjectsFromDirectory("PLC", "", "inputs")).StructuredContent!;
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
        Assert.Equal(reason, (string?)body["data"]?["failed"]?[0]?["error"]);
        Assert.Equal(names.Count, body["data"]!["imported"]!.AsArray().Count);
        if (outcome == "partial") Assert.Equal(completed, (int?)body["error"]?["details"]?["succeeded"]);
        if (unconfirmed) Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
    }

    [Theory]
    [InlineData(0, false, "rejected-before-operation", false)]
    [InlineData(1, false, "partial", false)]
    [InlineData(1, true, "unknown", false)]
    [InlineData(0, true, "unknown", true)]
    public void Plc_tag_table_batch_uses_the_failing_files_worker_verdict(int completed, bool dispatched, string outcome, bool missingConfirmation)
    {
        const string tool = "ImportPlcTagTablesFromDirectory";
        using var release = Host.HardwareContract.UseRelease("15.1");
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            int last = dispatched ? completed + 2 : completed;
            for (int i = 0; i <= last; i++) File.WriteAllText(Path.Combine(root, $"{i}.xml"),
                $"<Document><Engineering version='V15.1'/><SW.Tags.PlcTagTable ID='0'><AttributeList><Name>Tags{i}</Name></AttributeList></SW.Tags.PlcTagTable></Document>");
            int calls = 0;
            var service = new Organisation((_, args) => {
                if ((string?)args["request"]?["Action"] == "softwarePath") return new JsonObject { ["Message"] = "PLC" };
                if (calls++ < completed) return new JsonObject { ["Found"] = true, ["MayHaveChanged"] = true };
                if (missingConfirmation) return new JsonObject();
                return JsonSerializer.SerializeToNode(new { MayHaveChanged = dispatched, OutcomeUnknown = dispatched,
                    Failure = new { Code = "ImportFailed", Message = "No confirmed import." } });
            }, () => true, () => "Fixture.ap15_1");
            var body = Host.PlcToolContract.Run(tool, true, true,
                () => service.ImportPlcTagTablesFromDirectory("PLC", "", root)).StructuredContent!;
            Assert.Equal(completed + 1, calls);
            Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
            Assert.Equal(Path.Combine(root, $"{completed}.xml"), (string?)body["data"]?["failed"]?[0]?["path"]);
            Assert.Equal(missingConfirmation ? "The worker did not confirm the imported tag table." : "No confirmed import.",
                (string?)body["data"]?["failed"]?[0]?["error"]);
            Assert.Equal(completed, body["data"]!["imported"]!.AsArray().Count);
            Assert.Single(body["data"]!["failed"]!.AsArray());
            if (dispatched)
                Assert.Equal(new[] { Path.Combine(root, $"{completed + 1}.xml"), Path.Combine(root, $"{completed + 2}.xml") },
                    body["data"]!["evidence"]!["notAttempted"]!.AsArray().Select(path => (string)path!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, 0, false, "rejected-before-operation")]
    [InlineData(false, 1, false, "partial")]
    [InlineData(false, 1, true, "unknown")]
    [InlineData(true, 0, false, "rejected-before-operation")]
    [InlineData(true, 1, false, "partial")]
    [InlineData(true, 1, true, "unknown")]
    public void Block_and_program_directory_imports_mark_attempts_at_native_dispatch(bool program, int completed, bool dispatched, string outcome)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var request = new PlcBatchImportRequest {
                Release = "17", Directory = root, Program = program, Project = "Fixture.ap17", Software = "PLC", ProcessId = 42
            };
            for (int i = 0; i <= completed; i++) File.WriteAllText(Path.Combine(root, $"{i}.xml"), BatchImportTests.Xml($"FC_{i}", number: i + 1));
            int visits = 0;
            PlcBatchImportResult Run() => PlcBatchImportPolicy.Run(request, [], () => { }, (file, planned, dispatch) => {
                if (visits++ < completed) { dispatch(); return [planned]; }
                if (dispatched) dispatch();
                throw new IOException(@"Collection unavailable. C:\Private Folder\customer.xml");
            });
            var preview = Run();
            request.ExpectedHash = preview.PlanHash; request.Order = preview.Items.Select(item => item.RelativePath).ToArray();
            request.Confirm = true; request.DryRun = false; request.ExpectedProject = request.Project;
            var batch = Run();
            Assert.Equal(dispatched, batch.NativeOutcomeUnknown);
            Assert.Equal(dispatched, batch.Items.Last().Attempted);
            if (!dispatched)
            {
                Assert.Equal("import-preparation-failed", batch.Items.Last().Failure);
                Assert.Equal("Collection unavailable. <path>", batch.Items.Last().NativeMessage);
            }
            var wire = JsonSerializer.SerializeToNode(batch)!.AsObject();
            BatchImportContract.Validate(wire, false);
            if (!dispatched)
            {
                wire["Items"]![completed]!["Attempted"] = true;
                Assert.Throws<InvalidDataException>(() => BatchImportContract.Validate(wire, false));
            }
            var data = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var mapped = TiaMcp.Logic.V4.PlcBatchImportResultMapping.Result(data, "17",
                program ? "ImportPlcProgramFromDirectory" : "ImportPlcBlocksFromDirectory", "batch", false);
            var body = JsonNode.Parse(TiaMcp.Logic.V4.V4Json.Serialize(mapped))!;
            Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
            if (!dispatched) Assert.Contains("Collection unavailable.", (string?)body["data"]?["items"]?[completed]?["result"]?["error"]?["message"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, MemberData(nameof(VerifiedImportOutcomeTests.Releases), MemberType = typeof(VerifiedImportOutcomeTests))]
    public async Task Served_examples_identify_the_source_of_the_localized_default_table(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        foreach (var name in new[] { "DescribeHmiTag", "DescribeHmiTagTable", "ExportHmiTagTable" })
        {
            var reply = await fixture.Call("GetToolUsage", new { toolName = name });
            Assert.True((bool?)reply["ok"], reply.ToJsonString());
            var example = reply["data"]!["example"]!;
            Assert.Equal("/Default tag table", (string?)example["request"]?["params"]?["arguments"]?["tagTableName"]);
            Assert.Contains("localized", (string?)example["note"]);
            Assert.Contains("ListHmiTagTables", (string?)example["note"]);
            Assert.Contains("ListPlcTagTables", (string?)example["note"]);
        }
    }

    internal sealed class Worker(Func<string, JsonObject, JsonNode?> handle) : IFoundationWorker, Engine.IEngineWorker
    {
        internal const string Project = "C:/Fixture/Project.ap15_1";
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode Binding => new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = Project, ["processId"] = 42 } };
        public JsonObject Snapshot() => new() { ["binding"] = Binding, ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("Unexpected restart.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
            => throw new InvalidOperationException("Unexpected engine dispatch.");
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public Task<JsonNode?> Call(string operation, JsonObject input, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ProjectFile"] = Project,
                ["ProcessId"] = 42, ["IsAttached"] = true });
            return Task.FromResult(handle(operation, input));
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Refused_screen_directory_apply_leaves_no_unsettled_request()
    {
        const string tool = "ImportHmiScreensFromDirectory";
        const string reason = "Cannot verify the import target identities for Hmi.Screen.Screen. Use an official exported XML file.";
        int applies = 0;
        using var worker = new Worker((operation, input) => {
            Assert.Equal("hmi-generic.ExecuteHmi", operation);
            if ((bool?)input["dryRun"] == true) return new JsonObject();
            Assert.Equal(tool, (string?)input["request"]?["Operation"]);
            applies++;
            string file = Path.Combine((string)input["request"]!["Dir"]!, "tagtable__默认变量表.xml");
            return JsonSerializer.SerializeToNode(new { MayHaveChanged = false, OutcomeUnknown = false, Value = new {
                Imported = Array.Empty<string>(), Failed = new[] { new { Path = file, Error = reason } },
                Meta = new { success = false, items = new[] { new { target = file, evidence = new {
                    operationSuccess = false, status = "InvalidParams", nativeMessage = reason,
                    exceptionType = "PlcSoftwareException", batchStopped = true } } } }
            } });
        });
        var raw = PortedToolContract.Create(worker, "15.1").Single(t => t.ProtocolTool.Name == tool);
        var entry = new FoundationV4Tool(raw, "15.1", () => new ApprovalSettings(false, 5));
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1", worker: worker, replacementTool: entry);
        using var session = fixture.EnterSession();
        File.WriteAllText(Path.Combine(fixture.Root, "tagtable__默认变量表.xml"),
            "<Document><Engineering version='V15.1'/><Hmi.Tag.TagTable ID='0'><AttributeList><Name>默认变量表</Name></AttributeList></Hmi.Tag.TagTable></Document>");
        var args = new JsonObject { ["softwarePath"] = "HMI_RT_1", ["folderPath"] = "", ["dir"] = fixture.Root };
        var preview = (await fixture.Call(tool, args)).StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        args["dryRun"] = false;
        Assert.True((bool?)preview["data"]?["targetBound"], preview.ToJsonString());
        args["expectedPlanHash"] = preview["data"]!["planHash"]!.DeepClone();
        args["expectedProjectFile"] = Worker.Project;
        var body = (await fixture.Call(tool, args)).StructuredContent!;
        Assert.Equal(1, applies);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Empty(Engine.UnsettledCalls.For(worker).Snapshot());
    }

    [Fact]
    public async Task Protection_apply_requires_confirmation_when_the_workbench_is_unavailable()
    {
        const string tool = "ManagePlcProtection";
        int previews = 0;
        using var worker = new Worker((operation, input) => {
            Assert.Equal("project-security.ExecuteHardwareSecurity", operation);
            Assert.True((bool?)input["dryRun"]);
            previews++;
            return JsonSerializer.SerializeToNode(new { Message = "Preview", MayHaveChanged = false, Meta = new {
                dryRun = true, action = "setAccessLevel", cpu = "MCP_RC_PLC", operationSuccess = true, mayHaveChanged = false,
                before = new { accessLevel = "FullAccess" }, plan = "set PlcAccessLevelProvider.PlcProtectionAccessLevel = ReadAccess." } });
        });
        var raw = PortedToolContract.Create(worker, "15.1").Single(t => t.ProtocolTool.Name == tool);
        int approvals = 0;
        var gated = new FoundationV4Tool(raw, "15.1", () => new ApprovalSettings(true, 5), (pending, settings, _) => {
            Assert.True(settings.Enabled);
            approvals++;
            return Task.FromResult(new ApprovalOutcome(pending, false, "workbench-unavailable"));
        });
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1", worker: worker, replacementTool: gated);
        using var session = fixture.EnterSession();
        var result = (await fixture.Call(tool, new { devicePath = new[] { "MCP_RC_PLC" }, action = "setAccessLevel",
            accessLevel = "ReadAccess", dryRun = false, confirmChange = true })).StructuredContent!;
        Assert.Equal(1, previews);
        Assert.Equal(1, approvals);
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result["error"]?["code"]);
        Assert.Equal("workbench-unavailable", (string?)result["error"]?["details"]?["reason"]);
        Assert.False(result["error"]!["details"]!.AsObject().ContainsKey("parameter"));
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }
}
