extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class V14Sp1RegressionTests
{
    private static JsonNode Evidence(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(
        TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
        "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/V14Sp1", file)))!;

    internal sealed class AmlWorker : IFoundationWorker
    {
        internal int Exports;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = "C:/Fixture/Project.ap14", ["ProcessId"] = 42, ["IsAttached"] = true });
            Assert.Equal("hardware-aml.ExportHardwareAml", operation);
            Exports++;
            throw new WorkerOperationException("Output parent must already exist.", -32603, "rejected-before-operation",
                """{"parameter":"exportPath","exceptionType":"AdapterPreconditionException","condition":"output-parent-missing"}""");
        }
        public void Dispose() { }
    }

    [Theory, MemberData(nameof(VerifiedImportOutcomeTests.Releases), MemberType = typeof(VerifiedImportOutcomeTests))]
    public async Task Aml_admission_and_worker_precondition_leave_the_session_settled(string release)
    {
        using var worker = new AmlWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker);
        using var session = fixture.EnterSession();
        var devicePath = new[] { "MCP_RC_PLC" };
        var missing = Path.Combine(fixture.Root, "missing", "RC-14sp1-A-plc.aml");
        var historical = Evidence("076-ExportDeviceAml.json");
        var tool = (string)historical["meta"]!["tool"]!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)historical["error"]!["code"]);
        var first = await fixture.Call(tool, new { devicePath, exportFile = missing });
        Rejected(first.StructuredContent!, "PRECONDITION_FAILED");
        Assert.Equal(0, worker.Exports);
        var second = await fixture.Call("ExportDeviceAml", new { devicePath, exportFile = Path.Combine(fixture.Root, "RC-14sp1-A-plc.aml") });
        Rejected(second.StructuredContent!, "PRECONDITION_FAILED");
        Assert.Equal(1, worker.Exports);
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot());
    }

    [Theory]
    [InlineData("ImportHmiScreensFromDirectory")]
    [InlineData("ImportHmiTagTablesFromDirectory")]
    public void Missing_input_directory_identifies_the_directory(string tool)
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "exports");
        var error = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.Validate(tool,
            new JsonObject { ["dir"] = missing, ["dryRun"] = true }));
        Assert.Contains("directory does not exist", error.Message);
    }

    [Fact]
    public void Invalid_bit_offset_is_rejected_before_target_access()
    {
        var tool = new Host.RuntimeTools(_ => throw new InvalidOperationException("Unexpected PUT/GET lookup."), true);
        var reply = tool.ReadPlcLiveValuesS7V4("127.0.0.1", new[] { "DB1.DBX0.9" }, devicePath: "MCP_RC_PLC");
        Rejected(reply.StructuredContent!, "INVALID_ARGUMENT");
        Assert.Equal("/items", (string?)reply.StructuredContent!["error"]?["details"]?["parameter"]);
    }

    [Fact]
    public async Task Artifact_error_reports_only_members_of_the_dependency_cycle()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        var result = await fixture.Call("PlanArtifactImportOrder", new { artifacts = new[] {
            new { id = "A", dependencies = new[] { "B" } }, new { id = "B", dependencies = new[] { "A" } },
            new { id = "C", dependencies = new[] { "Missing" } } } });
        Rejected(result.StructuredContent!, "INVALID_ARGUMENT");
        Assert.Contains("A -> B -> A", (string?)result.StructuredContent!["error"]?["message"]);
    }

    [Fact]
    public async Task Missing_import_order_identifies_the_public_parameter_before_dispatch()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        var reply = await fixture.Call("ImportPlcBlocksFromDirectory", new {
            softwarePath = "devices/MCP_RC_PLC/MCP_RC_PLC", groupPath = "", dir = fixture.Root, dryRun = false, confirm = true,
            expectedPlanHash = new string('a', 64), expectedProjectFile = "C:/Fixture/Project.ap14" });
        Rejected(reply.StructuredContent!, "INVALID_ARGUMENT");
        Assert.Contains("importOrder", (string?)reply.StructuredContent!["error"]!["message"]);
        Assert.Equal("/importOrder", (string?)reply.StructuredContent!["error"]!["details"]!["parameter"]);
    }

    private sealed class ProgramPreviewWorker : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = "C:/Fixture/Project.ap14", ["ProcessId"] = 42, ["IsAttached"] = true });
            Assert.Equal("ImportPlcProgramFromDirectory", operation);
            Assert.True((bool)arguments["dryRun"]!);
            var request = new PlcBatchImportRequest((string)arguments["softwarePath"]!, (string)arguments["sourceDir"]!, program: true) {
                Project = "C:/Fixture/Project.ap14", Release = "14sp1", ProcessId = 42,
                Order = arguments["importOrder"]!.AsArray().Select(item => (string)item!).ToArray() };
            return Task.FromResult(System.Text.Json.JsonSerializer.SerializeToNode(PlcBatchImportPolicy.Run(request,
                Array.Empty<PlcBatchImportObject>(), () => { }, (_, _) => throw new InvalidOperationException("Preview cannot import."))));
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Applying_another_order_after_an_explicitly_ordered_preview_identifies_importOrder()
    {
        using var worker = new ProgramPreviewWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1", worker: worker);
        using var session = fixture.EnterSession();
        var args = new JsonObject { ["softwarePath"] = "devices/MCP_RC_PLC/MCP_RC_PLC", ["sourceDir"] = fixture.Root,
            ["importOrder"] = new JsonArray("MCP_RC_UDT2.xml", "MCP_RC_DB2.xml") };
        File.WriteAllText(Path.Combine(fixture.Root, "MCP_RC_UDT2.xml"), BatchImportTests.Xml("MCP_RC_UDT2", "UDT", producer: "V14 SP1"));
        File.WriteAllText(Path.Combine(fixture.Root, "MCP_RC_DB2.xml"), BatchImportTests.Xml("MCP_RC_DB2", "GlobalDB", producer: "V14 SP1"));
        const string tool = "ImportPlcProgramFromDirectory";
        var preview = (await fixture.Call(tool, args)).StructuredContent!;
        Assert.True((bool)preview["ok"]!, preview.ToJsonString());
        args["expectedPlanHash"] = (string)preview["data"]!["planHash"]!;
        args["expectedProjectFile"] = "C:/Fixture/Project.ap14";
        args["importOrder"] = new JsonArray("MCP_RC_DB2.xml", "MCP_RC_UDT2.xml");
        args["dryRun"] = false;
        args["confirm"] = true;
        var refused = (await fixture.Call(tool, args)).StructuredContent!;
        Rejected(refused, "INVALID_ARGUMENT");
        Assert.Contains("order differs from the approved plan", (string?)refused["error"]?["message"]);
    }

    [Fact]
    public void A_different_program_order_is_distinct_from_a_changed_file()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        string file = Path.Combine(fixture.Root, "MCP_RC_DB2.xml");
        File.Copy(Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/V14Sp1/155-global-db.xml"), file);
        File.WriteAllText(Path.Combine(fixture.Root, "MCP_RC_UDT2.xml"), BatchImportTests.Xml("MCP_RC_UDT2", "UDT", producer: "V14 SP1"));
        var request = new PlcBatchImportRequest("devices/MCP_RC_PLC/MCP_RC_PLC", fixture.Root, program: true) {
            Release = "14sp1", Project = "Project.ap14", ExpectedProject = "Project.ap14", ProcessId = 42 };
        PlcBatchImportResult Run() => PlcBatchImportPolicy.Run(request, Array.Empty<PlcBatchImportObject>(), () => { },
            (_, _) => throw new InvalidOperationException("Stale plans must never import."));
        request.ExpectedHash = Run().PlanHash;
        request.DryRun = false;
        request.Confirm = true;
        request.Order = new[] { "MCP_RC_UDT2.xml", "MCP_RC_DB2.xml" };
        var changedOrder = Assert.Throws<AdapterPreconditionException>(() => Run());
        Assert.Equal("importOrder", changedOrder.ParamName);
        Assert.Contains("order differs from the approved plan", changedOrder.Message);
        File.AppendAllText(file, Environment.NewLine);
        Assert.Equal("expectedPlanHash", Assert.Throws<AdapterPreconditionException>(() => Run()).ParamName);
    }

    [Fact]
    public void Offline_analysis_requires_an_OB_root()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        File.WriteAllText(Path.Combine(fixture.Root, "MCP_RC_FC.xml"), BatchImportTests.Xml("MCP_RC_FC"));
        var result = new Host.PlcOfflineTools().AnalyzePlcReferencesV4(fixture.Root, "unreachable");
        Rejected(result.StructuredContent!, "INVALID_ARGUMENT");
        Assert.Contains("No OB roots", (string?)result.StructuredContent!["error"]?["message"]);
    }

    [Fact]
    public void Network_info_preserves_successful_attribute_observations()
    {
        var attributes = Evidence("050-network-attributes.json").AsArray();
        foreach (var row in attributes) row!["writeProbeError"] = null;
        var reply = new Host.ResponseJsonReport { Ok = true, Data = new JsonObject { ["attributes"] = attributes } };
        var body = Host.HardwareToolContract.MapResult("GetDeviceItemNetworkInfo", reply, true, true).StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.Equal("IoController", (string?)body["data"]?["attributes"]?[0]?["value"]);
        Assert.All(body["data"]!["attributes"]!.AsArray(), row => Assert.Null(row!["writeProbeError"]));
        Assert.Equal("PROFINET 接口_1", (string?)body["data"]!["attributes"]![2]!["value"]);
    }

    [Fact]
    public void Missing_hardware_attribute_reports_the_observed_name_and_target()
    {
        var data = Evidence("057-missing-attribute.json").AsObject();
        string name = (string)data["attributeName"]!;
        Assert.DoesNotContain(data["availableAttributes"]!.AsArray(), item => (string?)item == name);
        var result = Host.HardwareContract.Map("SetDeviceItemAttribute", new Host.ResponseJsonReport { Ok = false, Meta = data }, true, true);
        Rejected(result.StructuredContent!, "PRECONDITION_FAILED");
        Assert.Contains(name, (string?)result.StructuredContent!["error"]?["message"]);
    }

    [Fact]
    public void Current_project_language_precondition_matches_the_public_error()
    {
        var data = Evidence("103-language-evidence.json").AsObject();
        string culture = (string)data["before"]!["editingCulture"]!;
        Assert.Equal(culture, (string?)data["before"]!["referenceCulture"]);
        var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = (bool)data["mayHaveChanged"]! };
        MutationEvidence.RecordStepFailure(AdapterPreconditionException.Failed((string)data["nativeMessage"]!, "current-project-language", "culture"), meta);
        foreach (var pair in meta) data[pair.Key] = System.Text.Json.JsonSerializer.SerializeToNode(pair.Value);
        var result = Host.PlcExchangeContract.Map("ManageProjectLanguage", new Host.ResponseMessage { Meta = data }, false, false).StructuredContent!;
        Rejected(result, "PRECONDITION_FAILED");
        Assert.Equal((string?)result["data"]?["v4FailureCode"], (string?)result["error"]?["code"]);
        Assert.Equal("current-project-language", (string?)result["error"]?["details"]?["condition"]);
    }

    [Fact]
    public void Library_overview_detects_nested_observation_errors()
    {
        var recorded = Evidence("045-library-type-folder.json").AsObject();
        var folder = new Dictionary<string, object?> { ["path"] = (string?)recorded["path"], ["name"] = (string?)recorded["name"] };
        var overview = new Dictionary<string, object?> { ["typeFolder"] = folder };
        Assert.False(LibraryCapabilities.IncompleteOverview(overview));
        folder["statusError"] = (string?)recorded["statusError"];
        Assert.True(LibraryCapabilities.IncompleteOverview(overview));
    }

    private static void Rejected(JsonNode body, string code)
    {
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }
}
