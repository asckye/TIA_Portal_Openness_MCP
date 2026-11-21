extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class PreviewToolCallTruthTests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();
    public static IEnumerable<object[]> ProjectCopyAndDownloadReleases() => new[] { "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> UnifiedHmiReleases() => new[] { "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> NonProgramWrites() => Releases().SelectMany(row => new[] {
        new object[] { row[0], "CreateProject" }, new object[] { row[0], "ManageGlobalLibrary" }
    });

    [Fact]
    public async Task Cached_session_is_named_when_no_project_is_bound()
    {
        var report = await toolpipeline::TiaMcpServer.ModelContextProtocol.CapabilitySelfTestLogic.Run(
            () => true, Array.Empty<string>, () => throw new InvalidOperationException(),
            () => new() { IsConnected = true, Project = "-", Session = "Session1" }, () => false,
            (_, _) => throw new InvalidOperationException(), () => "", false, false, false, "", "");
        Assert.NotNull(report.Items);
        var check = Assert.Single(report.Items, item => item.Id == "project.open");
        Assert.Contains("Session 'Session1' is bound", check.Detail);
    }

    [Fact]
    public void Update_rate_limit_maps_to_the_external_read_failure()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var response = new toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseMessage { Message = "GitHub API rate limit reached.",
            Meta = new JsonObject { ["success"] = false, ["releaseApiUrl"] = "https://example.invalid/releases", ["httpStatus"] = 429 } };
        var body = toolpipeline::TiaMcpServer.ModelContextProtocol.SessionToolContract.Map("CheckProductUpdate", response, false, false).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
    }

    [Fact]
    public void Comparison_refusal_identifies_the_operation_without_a_security_label()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var response = new toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseMessage {
            Meta = new JsonObject { ["operationSuccess"] = false, ["status"] = "InvalidState", ["mayHaveChanged"] = false } };
        var result = toolpipeline::TiaMcpServer.ModelContextProtocol.SecurityToolContract.MapResult("CompareProjects", response, true, false);
        Assert.DoesNotContain("security", (string?)result.StructuredContent?["error"]?["message"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("security-state", (string?)result.StructuredContent?["error"]?["details"]?["condition"]);
    }

    [Fact]
    public void Umc_consistency_requires_server_scope_and_the_v18_api()
    {
        var invalid = Assert.Throws<ArgumentException>(() => toolpipeline::TiaMcpServer.Siemens.SecurityDeepLogic
            .ValidateUmcRequest("user", "checkConsistency", "", "", "", "", "", false, true));
        Assert.Contains("kind=server", invalid.Message);
        Assert.Equal("UMC consistency/synchronization requires V18 or later.",
            TiaMcp.Adapters.Contracts.SecurityCapabilities.Unsupported("17", "ManageUmcUsers", "checkConsistency", "server"));
        Assert.Null(TiaMcp.Adapters.Contracts.SecurityCapabilities.Unsupported("18", "ManageUmcUsers", "checkConsistency", "server"));
    }

    [Fact]
    public void Syslog_validation_uses_public_property_names()
    {
        var fault = Assert.Throws<ArgumentException>(() => toolpipeline::TiaMcpServer.Siemens.SecurityDeepLogic.ValidateSyslogRequest(
            "project", "update", "Station", new JsonObject(), "[]", "", 0, false, true));
        Assert.Contains("properties", fault.Message);
        Assert.DoesNotContain("propertiesJson", fault.Message);
    }

    [Fact]
    public void Batch_rejects_the_nonadmitted_tool_before_its_arguments()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var call = new ToolCall("CallTool", new ToolArguments(JsonSerializer.SerializeToElement(new { invalid = true })));
        var error = Host.ValidateBatch(new[] { call }, false, out _);
        Assert.Contains("CallTool", error!.Message);
        Assert.Equal("/operations/0/name", Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error.Details).Parameter);
    }

    [Fact]
    public void Export_page_reports_the_requested_and_effective_limits()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        string id = toolpipeline::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "fixture", "retained", 4).id;
        var body = new toolpipeline::TiaMcpServer.ModelContextProtocol.ExportTools().GetExport(id, limit: 20001).StructuredContent!;
        var warnings = body["meta"]!["warnings"]!.AsArray();
        Assert.Equal(new[] { "PAGE_LIMIT_CAPPED", "UNVERIFIED_BEHAVIOR" }, warnings.Select(row => (string?)row?["code"]));
        var warning = Assert.Single(warnings, row => (string?)row?["code"] == "PAGE_LIMIT_CAPPED")!;
        Assert.Equal(20000, (int?)body["meta"]?["paging"]?["limit"]);
        Assert.Equal(20001, (int?)warning["details"]?["requested"]);
    }

    [Fact]
    public void Sinumerik_safety_mode_describes_the_native_safety_choices()
    {
        var mode = TiaMcpServer.ModelContextProtocol.ToolMetadata.Parameter("ManageSinumerikSafetyMode", "mode")!;
        Assert.Equal(new[] { "None", "NcSI", "DbSI" }, mode.AllowedValues);
        Assert.Equal("NcSI", JsonSerializer.Deserialize<string>(mode.ExampleJson));
    }

    [Fact]
    public void Diagnostic_report_requires_a_suite_and_preserves_its_verdict()
    {
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.OfflineSuiteTools();
        string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"ok":true,"items":[]}""");
            var refusal = tools.BuildReleaseDiagnosticReportV4(path).StructuredContent!;
            Assert.False((bool?)refusal["ok"]);
            File.WriteAllText(path, """{"mode":"tia-mcp-offline-release-validation-suite","ok":false,"offlineOnly":true,"items":[]}""");
            var report = tools.BuildReleaseDiagnosticReport(path).Data!;
            Assert.False((bool?)report["suiteOk"]);
            Assert.DoesNotContain(report["recommendedNextActions"]!.AsArray(), item => item!.ToString().Contains("离线发布套件已通过"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Recursive_call_explains_the_refusal()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var call = Host.CallTool("CallTool", new ToolArguments(JsonSerializer.SerializeToElement(new { name = "GetSessionState" })));
        Assert.Equal("INVALID_ARGUMENT", (string?)call.StructuredContent?["error"]?["code"]);
        Assert.Contains("cannot invoke itself", (string?)call.StructuredContent?["error"]?["message"]);
    }

    [Fact]
    public void Preview_returns_the_form_validator_error_without_dispatch()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var args = new { form = "blockFilter", mode = "set", fields = new { projectPath = "fixture" } };
        var result = Host.PreviewToolCall("PrefillWorkbenchForm", new ToolArguments(JsonSerializer.SerializeToElement(args)));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("/fields", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["data"]?["validationError"]?["code"]);
        Assert.Equal(0, fixture.Worker.NativeCalls);
    }

    [Fact]
    public void V16_test_library_save_points_to_the_missing_declared_title()
    {
        using var fixture = new PreviewHostFixture("16");
        using var session = fixture.Pipeline.EnterSession();
        var input = JsonSerializer.Deserialize<JsonElement>("""
            {"action":"save","testCase":{"schemaVersion":1,"id":"interlocks","private-caller-key":1,"kind":"testSuite",
            "testSuite":{"category":"application","kind":"case","names":["TC_Interlock"],"runAll":false}}}
            """);
        var body = Host.PreviewToolCall("ManagePlcTestLibrary", new ToolArguments(input)).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/testCase/title", (string?)body["error"]?["details"]?["parameter"]);
        Assert.DoesNotContain("private-caller-key", body["error"]!.ToJsonString());
        Assert.Equal(0, fixture.Worker.NativeCalls);
    }

    [Fact]
    public void Preview_case_hint_keeps_the_public_binding_error_code()
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var input = new ToolArguments(JsonSerializer.SerializeToElement(new { ExportId = "fixture" }));
        var preview = Host.PreviewToolCall("GetExportContent", input).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)preview["error"]?["code"]);
        Assert.Equal("INVALID_ARGUMENT", (string?)preview["data"]?["validationError"]?["code"]);
        Assert.All(preview["data"]!["validationErrors"]!.AsArray(), error => Assert.Equal("INVALID_ARGUMENT", (string?)error?["code"]));
        Assert.NotEmpty(preview["data"]!["caseFixes"]!.AsArray());
        Assert.Equal(0, fixture.Worker.NativeCalls);
    }

    [Theory, InlineData("ManagePlcCertificate"), InlineData("ManageSyslogServers")]
    public void Plc_security_configuration_preview_names_the_hardware_compile(string name)
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview(name, new { });
        Assert.Contains("CompileDevice", data["precautions"]!.ToJsonString());
    }

    [Theory, InlineData("[1]"), InlineData("null"), InlineData("{")]
    public void A_non_object_on_the_exception_path_returns_the_argument_error(string input)
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var result = Host.TargetFailure("ManageDeviceUsers", new ArgumentException("Invalid arguments."), false, input);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("/arguments", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
    }

    [Theory, MemberData(nameof(UnifiedHmiReleases))]
    public void Hmi_write_names_the_hmi_compile(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("SetUnifiedHmiButtonEventScriptCode", new {
            hmiSoftwarePath = "HMI_RT_1", screenName = "Screen_1", buttonName = "Button_1", eventType = "Tapped", scriptCode = "return;"
        });
        string precautions = data["precautions"]!.ToJsonString();
        Assert.Contains("CompileHmiDiagnostics", precautions);
        Assert.DoesNotContain("CompilePlcSoftware", precautions);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Hardware_write_names_the_device_compile(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("SetDeviceItemAttribute", new { deviceItemPath = "PLC_1/CPU", attributeName = "Name", value = "CPU_1" });
        string precautions = data["precautions"]!.ToJsonString();
        Assert.Contains("CompileDevice", precautions);
        Assert.DoesNotContain("CompilePlcSoftware", precautions);
    }

    [Theory, MemberData(nameof(NonProgramWrites))]
    public void Write_outside_the_program_names_no_compile(string release, string tool)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        object arguments = tool == "CreateProject"
            ? new { directoryPath = "C:/Projects", projectName = "NewProject" }
            : new { action = "create", filePath = "C:/Libraries/Fixture" };
        var data = Preview(tool, arguments);
        string precautions = data["precautions"]!.ToJsonString();
        Assert.Contains("No compile step is implied", precautions);
        Assert.DoesNotContain("CompilePlcSoftware", precautions);
        Assert.DoesNotContain("Modifies the open project", precautions);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Plc_write_keeps_the_program_compile(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("CreatePlcTagTable", new { plc = "PLC_1", group = "", name = "Tags" });
        Assert.Contains("CompilePlcSoftware then SaveProject", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Openness_group_tool_names_the_windows_group(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("EnsureOpennessUserGroup", new { });
        Assert.True((bool?)data["changesState"]);
        Assert.Contains("Windows Openness group", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(ProjectCopyAndDownloadReleases))]
    public void SaveProjectCopy_runs_at_once_and_changes_state(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("SaveProjectCopy", new { newProjectPath = "C:/Projects/Copy" });
        Assert.False((bool?)data["dryRun"]!["supported"]);
        Assert.False((bool?)data["dryRun"]!["effective"]);
        Assert.True((bool?)data["changesState"]);
        Assert.DoesNotContain("does not change the project", data["precautions"]!.ToJsonString());
        Assert.True((bool?)data["prerequisites"]!["needsProject"]);
        Assert.Contains("EXECUTES on this call", (string?)data["summary"]);
        Assert.Contains("dryRun: not available; this call runs for real.", data.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void ConnectOnlinePlc_is_not_called_read_only(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        if (release == "14sp1")
        {
            Assert.Null(Host.CatalogView.Find("ConnectOnlinePlc"));
            return;
        }
        var data = Preview("ConnectOnlinePlc", new { softwarePath = "PLC_1", ipAddress = "192.168.1.10" });
        Assert.True((bool?)data["changesState"]);
        Assert.DoesNotContain("read-only", data["precautions"]!.ToJsonString());
        Assert.Contains("Changes the online connection state", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(ProjectCopyAndDownloadReleases))]
    public void DownloadPlc_default_is_a_preview(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("DownloadPlc", new { softwarePath = "PLC_1", targetIpAddress = "192.168.1.10" });
        Assert.True((bool?)data["dryRun"]!["supported"]);
        Assert.Null(data["dryRun"]!["given"]);
        Assert.True((bool?)data["dryRun"]!["effective"]);
        Assert.False((bool?)data["changesState"]);
        string precautions = data["precautions"]!.ToJsonString();
        Assert.DoesNotContain("maintainer", precautions);
        Assert.DoesNotContain("confirm flag", precautions);
        Assert.Contains("expectedPlanHash", precautions);
        Assert.Contains("expectedProjectFile", precautions);
        Assert.Contains("it is a preview (dryRun)", (string?)data["summary"]);
    }

    [Theory, MemberData(nameof(ProjectCopyAndDownloadReleases))]
    public void DownloadPlc_with_dryRun_false_executes(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("DownloadPlc", new { softwarePath = "PLC_1", targetIpAddress = "192.168.1.10", dryRun = false });
        Assert.False((bool?)data["dryRun"]!["effective"]);
        Assert.True((bool?)data["changesState"]);
        Assert.Contains("EXECUTES (dryRun=false)", (string?)data["summary"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Write_tool_with_a_confirm_flag_names_it(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("CreatePlcTagTable", new { plc = "PLC_1", group = "", name = "Tags" });
        Assert.Contains("confirm=true", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void GetSessionState_changes_nothing(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("GetSessionState", new { });
        Assert.False((bool?)data["dryRun"]!["supported"]);
        Assert.False((bool?)data["dryRun"]!["effective"]);
        Assert.False((bool?)data["changesState"]);
        Assert.Contains("does not change the project", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Prefill_refused_fields_are_not_ready(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("PrefillWorkbenchForm", new { form = "blockFilter", mode = "set", fields = new { namePattern = "^X_" } });
        Assert.False((bool?)data["ok"]);
        Assert.Contains("NOT READY", (string?)data["summary"]);
        Assert.Contains("Set for blockFilter requires exactly these fields: filter.", data["typeProblems"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Prefill_right_fields_are_ready(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("PrefillWorkbenchForm", new { form = "blockFilter", mode = "set", fields = new { filter = "^X_" } });
        Assert.True((bool?)data["ok"]);
        Assert.StartsWith("READY", (string?)data["summary"]);
    }

    private static JsonObject Preview(string name, object arguments)
    {
        Assert.NotNull(Host.CatalogView.Find(name));
        var result = Host.PreviewToolCall(name, new ToolArguments(JsonSerializer.SerializeToElement(arguments)));
        return Assert.IsType<JsonObject>(result.StructuredContent!["data"]);
    }
}

internal sealed class PreviewHostFixture : IDisposable
{
    internal readonly CachedWorker Worker = new();
    internal readonly toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline Pipeline;
    private readonly string release = Context.ReleaseKey;
    private readonly string profile = Host.ResolvedProfile();

    internal PreviewHostFixture(string key)
    {
        Context.ReleaseKey = key;
        Host.SetProfileOverride("full");
        Pipeline = new(key, Worker, LegacyHostToolRegistry.Create(Worker, key, false, hostPorts: false));
    }

    public void Dispose()
    {
        Pipeline.Dispose();
        Worker.Dispose();
        Context.ReleaseKey = release;
        Host.SetProfileOverride(profile);
    }

    internal sealed class CachedWorker : toolpipeline::TiaMcp.FoundationHost.IEngineWorker, IFoundationWorker
    {
        internal int Calls, NativeCalls;
        internal bool Bound = true;
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode? Binding => Bound ? new JsonObject { ["identity"] = new JsonObject {
            ["projectPath"] = "C:/Projects/Fixture.ap21", ["projectName"] = "Fixture", ["processId"] = 42 } } : null;
        public JsonObject Snapshot() => new() { ["session"] = new JsonObject {
            ["isConnected"] = Bound, ["project"] = Bound ? "Fixture" : "-", ["session"] = "-" } };
        public Task<JsonObject> Status(CancellationToken token)
        {
            Calls++;
            return Task.FromResult(Snapshot());
        }
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++; NativeCalls++;
            throw new InvalidOperationException("Diagnostics must use cached session state.");
        }
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(
            string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            Calls++; NativeCalls++;
            throw new InvalidOperationException("Diagnostics must not dispatch a tool.");
        }
        public void Dispose() { }
    }
}
