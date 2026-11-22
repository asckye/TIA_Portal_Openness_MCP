extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Inputs;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Builders = TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class V14HostRegressionTests
{
    private static string Capture(string row) => Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
        "tests", "FoundationHost", "TiaMcp.FoundationHost.Tests", "Fixtures", "Rc1V14Sp1", row + ".json");

    [Theory]
    [InlineData("221-GetUnifiedRuntimeAlarms")]
    [InlineData("222-GetUnifiedRuntimeTags")]
    [InlineData("156-WriteUnifiedRuntimeTags")]
    public void Missing_runtime_pipe_is_an_online_precondition(string row)
    {
        var original = JsonNode.Parse(File.ReadAllText(Capture(row)))!["body"]!;
        var data = original["data"]!.AsObject();
        string tool = (string)original["meta"]!["tool"]!;
        var result = Host.RuntimeToolContract.Map(tool, new Host.ResponseJsonReport { Ok = false, Data = data }, true, true).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal("plc-online", (string?)result["error"]?["details"]?["condition"]);
        Assert.Equal((string?)data["pipeName"], (string?)result["error"]?["details"]?["target"]);
    }

    [Theory]
    [InlineData(403, "GitHub API rate limit reached from this address (60 unauthenticated calls per hour)", "rate limited, try later")]
    [InlineData(403, "GitHub API answered 403", "GitHub API answered 403")]
    [InlineData(null, null, "GitHub could not be reached")]
    public void Update_failure_preserves_the_external_cause(int? status, string? apiProblem, string message)
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var evidence = new JsonObject { ["success"] = false, ["releaseApiUrl"] = "https://example.invalid/releases",
            ["httpStatus"] = status, ["apiProblem"] = apiProblem };
        var result = Host.SessionToolContract.Map("CheckProductUpdate", new Host.ResponseStringList { Meta = evidence }, false, false).StructuredContent!;
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)result["error"]?["code"]);
        Assert.Contains(message, (string?)result["error"]?["message"]);
        Assert.Equal("read-failed", (string?)result["meta"]?["outcome"]);
    }

    [Fact]
    public void All_release_reports_refuse_the_same_non_suite_before_writing()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var tools = new Host.OfflineSuiteTools();
        string path = Capture("222-GetUnifiedRuntimeTags");
        string output = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
        foreach (var result in new[] { tools.BuildReleaseDiagnosticReportV4(path), tools.BuildReleaseManifestV4(path),
            tools.BuildReleaseRunbookV4(path), tools.RebuildReleaseHandoffArtifactsV4(path, output) })
        {
            var error = result.StructuredContent!["error"];
            Assert.Equal("INVALID_ARGUMENT", (string?)error?["code"]);
            Assert.Equal("/offlineReleaseSuiteJsonPath", (string?)error?["details"]?["parameter"]);
            Assert.Contains("OfflineReleaseValidationSuite", (string?)error?["message"]);
        }
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void All_release_tools_accept_the_report_written_by_the_offline_suite()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var workspace = Directory.CreateTempSubdirectory("release-suite-");
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, args) => OnlineMonitoringSafetyRuleTests.Observe(fixture, operation, args);
            var plc = workspace.CreateSubdirectory("TMP_EXPORT/_verify");
            Builders.PlcUdtXmlBuilder.BuildDocument("UDT_Fault", [new Builders.PlcUdtMemberDefinition("Active", "Bool")], "14sp1")
                .Save(Path.Combine(plc.FullName, "UDT_Fault.xml"));
            Builders.PlcGlobalDbXmlBuilder.BuildDocument("Sim_Data", 16, [new Builders.PlcDbMemberDefinition("Running", "Bool")], "14sp1")
                .Save(Path.Combine(plc.CreateSubdirectory("Sim_Data_roundtrip.xml").FullName, "Sim_Data.xml"));
            workspace.CreateSubdirectory("docs/hmi_templates");
            var mapping = workspace.CreateSubdirectory("docs/hmi_template_mappings");
            File.WriteAllText(Path.Combine(mapping.FullName, "tia-hmi-template-plc-mapping.release.json"), """{"Templates":[]}""");
            var suite = Host.OfflineReleaseValidationSuite.Run(workspace.FullName, Path.Combine(workspace.FullName, "reports"));
            string path = (string)suite["jsonPath"]!;
            var tools = new Host.OfflineSuiteTools();
            foreach (var (result, format) in new[] {
                (tools.BuildReleaseDiagnosticReportV4(path), "tia-mcp-release-diagnostic-report-v1"),
                (tools.BuildReleaseManifestV4(path), "tia-mcp-release-manifest-v1"),
                (tools.BuildReleaseRunbookV4(path), "tia-mcp-release-runbook-v1"),
                (tools.RebuildReleaseHandoffArtifactsV4(path, Path.Combine(workspace.FullName, "handoff")), "tia-mcp-release-handoff-artifacts-v1") })
            {
                var body = result.StructuredContent!;
                Assert.True((bool?)body["ok"], body.ToJsonString());
                Assert.Equal(format, (string?)body["data"]?["format"]);
            }
            Assert.Equal(0, fixture.Worker.NativeCalls);
        }
        finally
        {
            Host.HostToolServices.Override = prior;
            workspace.Delete(true);
        }
    }

    [Fact]
    public void All_release_tools_refuse_a_missing_suite_before_writing()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        string path = Path.Combine(fixture.Root, "missing.json");
        string output = Path.Combine(fixture.Root, "handoff");
        var tools = new Host.OfflineSuiteTools();
        foreach (var result in new[] { tools.BuildReleaseDiagnosticReportV4(path), tools.BuildReleaseManifestV4(path),
            tools.BuildReleaseRunbookV4(path), tools.RebuildReleaseHandoffArtifactsV4(path, output) })
        {
            Assert.Equal("NOT_FOUND", (string?)result.StructuredContent!["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent["meta"]?["execution"]);
        }
        Assert.False(Directory.Exists(output));
        var writeFailure = Host.OfflineContracts.Run("BuildReleaseHandoffArtifacts", () => throw new FileNotFoundException(), writes: true);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)writeFailure.StructuredContent!["error"]?["code"]);
    }

    [Fact]
    public void Runbook_commands_select_the_session_release()
    {
        var runbook = TiaMcpServer.ModelContextProtocol.ReleaseRunbookBuilder.Build(new JsonObject {
            ["workspaceRoot"] = @"C:\Workspace", ["suiteDirectory"] = @"C:\Reports" }, new JsonObject(), "14sp1");
        var commands = runbook["quickStartCommands"]!.AsArray().Select(command => (string)command!).ToArray();
        Assert.StartsWith("dotnet build", commands[0]);
        Assert.All(commands.Skip(1), command => Assert.Contains("--release-key 14sp1", command));
        Assert.Contains("V14 SP1", runbook["handoffChecklist"]!.ToJsonString());
        Assert.DoesNotContain("V21", runbook.ToJsonString());
    }

    [Theory]
    [InlineData("FindTools", "export")]
    [InlineData("GetOpennessGuidance", "")]
    public async Task Partial_pages_declare_partial_completeness(string name, string query)
    {
        using var fixture = new DiscoveryFixture("14sp1");
        var result = await fixture.Call(name, new { query, limit = 1 });
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.False((bool?)result["meta"]?["paging"]?["complete"]);
        Assert.Equal("partial", (string?)result["meta"]?["completeness"]);
    }

    [Theory]
    [InlineData("GetOpennessGuidance", "total", 0, false, "partial")]
    [InlineData("GetOpennessGuidance", "total", 1, true, "complete")]
    [InlineData("ComparePlcBlockDocuments", "hunkCount", 0, false, "complete")]
    public void Paging_preserves_source_completeness(string tool, string countField, int offset, bool lastPage, string completeness)
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var response = new Host.ResponseMessage { Meta = new JsonObject {
            ["success"] = true, ["dataComplete"] = true, ["offset"] = offset, ["limit"] = 1, [countField] = 2 } };
        var result = Host.OfflineContracts.Map(tool, response).StructuredContent!;
        Assert.True((bool?)result["data"]?["dataComplete"]);
        Assert.Equal(lastPage, (bool?)result["meta"]?["paging"]?["complete"]);
        Assert.Equal(completeness, (string?)result["meta"]?["completeness"]);
    }

    [Theory]
    [InlineData("GetOpennessGuidance", "partial")]
    [InlineData("GetV21EcosystemCatalog", "complete")]
    public async Task Catalog_pages_preserve_the_source_completeness_field(string tool, string firstPageCompleteness)
    {
        using var fixture = new DiscoveryFixture("21");
        JsonObject Source(int offset) => (tool == "GetOpennessGuidance"
            ? new Host.EcosystemTools().ReadOpennessGuidance(offset: offset, limit: 1)
            : new Host.V21EcosystemTools().ReadV21EcosystemCatalog(offset: offset, limit: 1)).Meta!;
        var first = await fixture.Call(tool, new { limit = 1 });
        Assert.Equal((bool?)Source(0)["dataComplete"], (bool?)first["data"]?["dataComplete"]);
        Assert.False((bool?)first["meta"]?["paging"]?["complete"]);
        Assert.Equal(firstPageCompleteness, (string?)first["meta"]?["completeness"]);
        int total = (int)first["meta"]!["paging"]!["total"]!;
        var last = await fixture.Call(tool, new { offset = total - 1, limit = 1 });
        Assert.Equal((bool?)Source(total - 1)["dataComplete"], (bool?)last["data"]?["dataComplete"]);
        Assert.True((bool?)last["meta"]?["paging"]?["complete"]);
        Assert.Equal("complete", (string?)last["meta"]?["completeness"]);
    }

    [Theory]
    [InlineData("DescribeService", "Software", "MCP_RC_PLC", "PlcChecksumProvider")]
    [InlineData("InvokeObject", "Project", "", "GetAttribute")]
    public void Missing_reflection_member_identifies_the_member_and_object(string tool, string kind, string path, string member)
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var service = new Services.ReflectionPortService((_, _) => new JsonObject { ["Observation"] = new JsonObject {
            ["ObjectKind"] = kind, ["ObjectPath"] = path, ["Meta"] = new JsonObject { ["success"] = false, ["v4Rejection"] = "NOT_FOUND" } } }, () => "");
        var tools = new Host.ReflectionTools(service);
        var result = (tool == "InvokeObject" ? tools.InvokeObjectV4(kind, path, member) : tools.DescribeServiceV4(kind, path, member)).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)result["error"]?["code"]);
        Assert.Equal(member, (string?)result["error"]?["details"]?["target"]);
        Assert.Contains(kind, (string?)result["error"]?["message"]);
        Assert.Contains(member, (string?)result["error"]?["message"]);
    }

    [Fact]
    public async Task Missing_confirmation_refuses_before_the_approval_preview()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var call = Host.McpServer.PrecheckBeforeApproval("WritePlcWebVars", """{"dryRun":false}""",
            _ => throw new InvalidOperationException("The tool must refuse before preview."), CancellationToken.None);
        var result = (await call)!.StructuredContent!;
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result["error"]?["code"]);
        Assert.Contains("confirmWrite=true", (string?)result["error"]?["message"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }

    [Fact]
    public async Task Bound_project_tree_is_read_without_requesting_a_connection()
    {
        var report = await Host.CapabilitySelfTestLogic.Run(() => true, Array.Empty<string>, () => throw new InvalidOperationException(),
            () => new() { IsConnected = true, Project = "Fixture" }, () => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), () => "Fixture/PLC", false, true, false, "", "");
        Assert.Equal("Fixture/PLC", report.ProjectTree);
        Assert.True(report.Ok);
    }

    [Fact]
    public void Case_correction_is_independent_of_an_undeclared_field()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var args = new ToolArguments(JsonSerializer.SerializeToElement(new { softwarepath = "MCP_RC_PLC", bogus = 1 }));
        var data = Host.McpServer.PreviewToolCall("ListPlcBlocks", args).StructuredContent!["data"]!;
        Assert.Single(data["caseFixes"]!.AsArray());
        Assert.DoesNotContain("Missing required", data.ToJsonString());
        Assert.Equal(2, data["validationErrors"]!.AsArray().Count);
        Assert.Contains("softwarePath", (string?)data["validationErrors"]?[0]?["message"]);
        Assert.DoesNotContain("bogus", data.ToJsonString());
    }

    [Fact]
    public async Task Error_report_writes_both_files_in_the_requested_export_directory()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        string target = Path.Combine(fixture.Root, "reports");
        var result = (await fixture.Call("GenerateErrorReport", new { errorCode = "Fixture", summary = "Fixture", exportDirectory = target }))
            .StructuredContent!;
        Assert.True((bool?)result["ok"], result.ToJsonString());
        foreach (string key in new[] { "markdownPath", "jsonPath" })
        {
            string file = (string)result["data"]![key]!;
            Assert.Equal(target, Path.GetDirectoryName(file));
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public void Block_preview_preserves_reference_warning_and_recommends_available_backup()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var capture = JsonNode.Parse(File.ReadAllText(Capture("219-DeletePlcBlock")))!["body"]!["data"]!;
        var native = new JsonObject();
        foreach (string field in new[] { "warnings", "crossReferenceAvailable", "crossReferenceUnavailableReason", "crossReferenceQueried",
            "resolvedBlockPath", "pinnedBlockNumber" }) native[field] = capture[field]?.DeepClone();
        var port = new Services.PlcOrganisationPortService((_, _) => new JsonObject { ["Data"] = native }, () => true, () => "");
        var preview = new Host.PlcOrganisationTools(port).DeletePlcBlockV4("MCP_RC_PLC", (string)native["resolvedBlockPath"]!, crossReferences: true);
        var data = preview.StructuredContent!["data"]!;
        Assert.DoesNotContain("ExportPlcBlockDocuments", data.ToJsonString());
        Assert.Contains("Back up the block", data["warnings"]!.ToJsonString());
        Assert.Contains("CrossReferenceService requires V18 or later", data["warnings"]!.ToJsonString());
    }

    [Fact]
    public void Classic_script_refusal_identifies_the_selected_object()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var data = JsonNode.Parse(File.ReadAllText(Capture("007-ManageClassicHmiScript")))!["body"]!["data"]!.AsObject();
        var result = Host.ClassicMotionToolContract.Map("ManageClassicHmiScript", new Host.ResponseMessage { Meta = data }, true, true).StructuredContent!;
        Assert.Equal("UnsupportedOnTarget", (string?)result["data"]?["status"]);
        Assert.Contains("cannot run on the selected object", (string?)result["error"]?["message"]);
        Assert.DoesNotContain("Unified", result.ToJsonString());
    }

    [Fact]
    public void Library_probe_and_reuse_plan_respect_the_session_release()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        string library = Path.Combine(fixture.Root, "Library");
        Directory.CreateDirectory(library);
        string selected = Path.Combine(library, "Machine.al14");
        File.WriteAllText(selected, "");
        File.WriteAllText(Path.Combine(library, "Machine.al21"), "");
        var service = new Services.LibraryPortService((_, args) =>
        {
            Assert.Equal(selected, (string?)args["request"]?["LibraryPath"]);
            return new JsonObject { ["Probe"] = new JsonObject { ["Ok"] = true } };
        }, () => "");
        Assert.True(new Host.LibraryTools(service).ProbeGlobalLibrary(library).Ok);
        var plan = new Host.HmiOfflineTools().PlanGlobalLibraryTemplateReuse(library).Data!;
        foreach (string field in new[] { "reconstructionPlan", "validationGates", "recommendedMcpTools" })
        {
            Assert.DoesNotContain("Unified", plan[field]!.ToJsonString());
            Assert.DoesNotContain("SyntaxCheck", plan[field]!.ToJsonString());
        }
    }

    [Theory]
    [MemberData(nameof(B3HostPortsTests.Releases), MemberType = typeof(B3HostPortsTests))]
    public async Task Artifact_usage_fits_the_served_budget_and_preserves_the_input_contract(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        const string tool = "BuildAndImportPlcArtifact";
        var result = await fixture.Call("GetToolUsage", new { toolName = tool });
        Assert.True((bool?)result["ok"], result.ToJsonString());
        int length = result.ToJsonString().Length;
        Assert.True(length < 40000, $"{release}/{tool} returned {length} characters");
        var listed = fixture.Tools.Single(entry => entry.ProtocolTool.Name == tool).ProtocolTool.InputSchema;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(listed.GetRawText()), result["data"]!["inputSchema"]));
        Assert.Equal(tool, (string?)result["data"]?["example"]?["request"]?["params"]?["name"]);
    }

    [Fact]
    public async Task Usage_returns_applicable_descriptions_and_a_callable_block_example()
    {
        using var fixture = new DiscoveryFixture("14sp1");
        async Task<JsonNode> Usage(string tool) => (await fixture.Call("GetToolUsage", new { toolName = tool }))["data"]!;
        Assert.DoesNotContain("PlcChecksumProvider", (await Usage("DescribeService"))["inputSchema"]!.ToJsonString());
        var comparison = await Usage("CompareSoftwareToOnline");
        Assert.DoesNotContain("ConnectOnlinePlc", comparison["description"]!.ToString());
        Assert.DoesNotContain("ConnectOnlinePlc", comparison["examples"]!.ToJsonString());
        Assert.Contains("only installed GSDML files", (await Usage("SearchInstalledGsdDevices"))["description"]!.ToString());
        Assert.DoesNotContain("in V20/V21", (await Usage("DeleteHmiTag"))["description"]!.ToString());
        Assert.Contains(".al14", (await Usage("ProbeGlobalLibrary"))["inputSchema"]!.ToJsonString());
        Assert.DoesNotContain("capability absent", (await Usage("ExportTechnologyObject"))["example"]!.ToJsonString());
        var invocation = (await Usage("InvokeObject"))["example"]!["request"]!["params"]!["arguments"]!;
        Assert.Equal("Block", (string?)invocation["objectKind"]);
        Assert.Equal("HeaderAuthor", (string?)invocation["args"]?[0]);
        var getToolUsage = fixture.Tools.Single(tool => tool.ProtocolTool.Name == "GetToolUsage");
        var result = await ToolHintOnceTests.Invoke(getToolUsage, new { toolName = "BuildAndImportPlcArtifact" });
        int length = result.ToJsonString().Length;
        Assert.True(length < 40000, $"BuildAndImportPlcArtifact usage returned {length} characters");
        var artifact = result["data"]!;
        Assert.NotNull(artifact["inputSchema"]);
    }

    [Theory]
    [InlineData("14sp1", false, false, false, false)]
    [InlineData("15.1", true, false, false, false)]
    [InlineData("16", true, false, false, false)]
    [InlineData("17", true, false, false, false)]
    [InlineData("18", true, true, false, false)]
    [InlineData("19", true, true, true, false)]
    [InlineData("20", true, true, true, true)]
    [InlineData("21", true, true, true, true)]
    public async Task Descriptions_and_reuse_plan_follow_release_capabilities(string release, bool connect, bool catalog, bool applyDesign, bool export)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        async Task<JsonNode> Usage(string tool) => (await fixture.Call("GetToolUsage", new { toolName = tool })).StructuredContent!["data"]!;
        Assert.Equal(connect, (await Usage("CompareSoftwareToOnline"))["description"]!.ToString().Contains("ConnectOnlinePlc"));
        Assert.Equal(export, (await Usage("DeletePlcBlock"))["description"]!.ToString().Contains("ExportPlcBlockDocuments"));
        string gsd = (await Usage("SearchInstalledGsdDevices"))["description"]!.ToString();
        Assert.Equal(catalog, gsd.Contains("installed hardware catalog"));
        Assert.Equal(!catalog, gsd.Contains("only installed GSDML files"));
        Assert.Equal(catalog, gsd.Contains("SearchHardwareCatalog"));
        string suffix = (await Usage("DescribeService"))["inputSchema"]!["properties"]!["serviceTypeSuffix"]!["description"]!.ToString();
        Assert.Equal(connect, suffix.Contains("ICompilable. PlcChecksumProvider"));
        var plan = new Host.HmiOfflineTools().PlanGlobalLibraryTemplateReuse(fixture.Root).Data!["reconstructionPlan"]!.ToJsonString();
        Assert.Equal(applyDesign, plan.Contains("ApplyUnifiedHmiScreenDesign"));
        Assert.Equal(applyDesign, plan.Contains("action SyntaxCheck"));
    }
}
