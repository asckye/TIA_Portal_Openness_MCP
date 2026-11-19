extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class ExtensionFamilyReplyTests
{
    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Unified_preview_has_no_null_members(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedHmiService(
            (_, _) => throw new InvalidOperationException("An offline preview must not call the worker."), () => "fixture");
        var response = service.EnsureUnifiedHmiScreen("HMI", "Overview");
        var data = Host.UnifiedHmiContract.Map("EnsureUnifiedHmiScreen", response, true, false).StructuredContent!["data"]!.AsObject();
        Assert.All(data, field => Assert.NotNull(field.Value));
        Assert.True((bool)data["evidence"]!["dryRun"]!);

        var connection = service.EnsureUnifiedHmiConnection("HMI");
        var connectionData = Host.UnifiedHmiContract.Map("EnsureUnifiedHmiConnection", connection, true, false)
            .StructuredContent!["data"]!.AsObject();
        Assert.All(connectionData, field => Assert.NotNull(field.Value));

        var values = Host.UnifiedHmiContract.Map("EnsureUnifiedHmiScreen", new {
            Message = "Preview", Missing = (string?)null, Enabled = false, Count = 0, Text = "",
            Nested = new { Missing = (string?)null, Enabled = false, Count = 0, Text = "" },
            Meta = new { operationSuccess = true }
        }, true, false).StructuredContent!["data"]!;
        Assert.False(values.AsObject().ContainsKey("missing"));
        Assert.False((bool)values["enabled"]!);
        Assert.Equal(0, (int)values["count"]!);
        Assert.Equal("", (string?)values["text"]);
        Assert.False(values["nested"]!.AsObject().ContainsKey("missing"));
        Assert.False((bool)values["nested"]!["enabled"]!);
        Assert.Equal(0, (int)values["nested"]!["count"]!);
        Assert.Equal("", (string?)values["nested"]!["text"]);
    }

    [Fact]
    public void Captured_V21_pages_pass_from_Get_to_Compare_with_their_raw_geometry()
    {
        using var release = Host.HardwareContract.UseRelease("21");
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var capture = JsonNode.Parse(File.ReadAllText(Path.Combine(root,
            "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/GraphicSelection/V21Comparison.json")))!;
        var pending = new Queue<JsonNode>(new[] { capture["beforePages"]![0]!, capture["afterPages"]![0]! });
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.GraphicSelectionService((operation, args) => {
            Assert.EndsWith(".ExecuteUnified", operation);
            Assert.Equal("GetUnifiedGraphicSelection", (string?)args["request"]?["Operation"]);
            var metadata = pending.Dequeue().DeepClone();
            metadata["extraWorkerField"] = "transport-only";
            return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "Selection", ["Meta"] = metadata } };
        }, () => "AutomaticDipCoatingMachine");
        var tools = new Host.GraphicSelectionTools(service);
        TiaMcp.Logic.V4.Domain.GraphicSelectionPage[] Read()
        {
            var result = tools.ReadUnifiedGraphicSelectionV4("HMI_RT_1", "AutomaticDipCoatingMachine", "/MCP_B6F_Screen",
                new[] { "MCP_B6F_Rect", "MCP_B6F_Text" }).StructuredContent!;
            Assert.True((bool?)result["ok"], result.ToJsonString());
            var page = result["data"]!["page"]!.AsObject();
            Assert.False(page.ContainsKey("extraWorkerField"));
            Assert.False(string.IsNullOrEmpty((string?)page["diagnosticLog"]));
            Assert.False(page.ContainsKey("mayHaveChanged"));
            Assert.True(page.ContainsKey("nextCursor")); Assert.Null(page["nextCursor"]);
            return TiaMcp.Logic.V4.Domain.DomainValidation.Read<TiaMcp.Logic.V4.Domain.GraphicSelectionPage[]>(
                new JsonArray(page.DeepClone()).ToJsonString());
        }
        var before = Read(); var after = Read();
        TiaMcp.Logic.V4.Domain.DomainValidation.GraphicSelections(before, after);
        var comparison = tools.CompareUnifiedGraphicSelectionsV4(before, after).StructuredContent!;
        Assert.True((bool?)comparison["ok"], comparison.ToJsonString());
        var data = comparison["data"]!;
        Assert.Equal(1, (int?)data["changedObjectCount"]); Assert.Equal(2, (int?)data["actualCount"]);
        var changed = Assert.Single(data["objects"]!.AsArray(), item => (bool)item!["changed"]!);
        Assert.Equal(16, (int?)changed!["fields"]!["Left"]!["delta"]);
        Assert.Equal(68, (int?)changed["fields"]!["Top"]!["delta"]);
        Assert.Equal(-28, (int?)changed["fields"]!["Width"]!["delta"]);
        Assert.Empty(pending);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Graphic_selection_page_appears_once(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var page = JsonNode.Parse("""
            {"scope":{"tool":"ReadUnifiedGraphicSelection","softwarePath":"HMI","expectedProject":"Fixture",
                      "project":"Fixture","screenPath":"/Overview","itemNames":["Button"]},
             "collectionId":"fixture","pageIndex":0,"apiCallSuccess":true,"operationSuccess":true,
             "dataComplete":true,"traversalComplete":true,"truncated":false,"actualCount":2,"expectedCount":2,
             "nextCursor":null,"records":[
               {"path":"/Screens/Overview/ScreenItems/Button","status":"ok","kind":"graphicObject","name":"Button",
                "type":"Siemens.Engineering.HmiUnified.UI.Controls.HmiButton","geometryComplete":true,"fields":{
                  "Left":{"path":"/Screens/Overview/ScreenItems/Button/Left","kind":"value","status":"ok","value":0},
                  "Top":{"path":"/Screens/Overview/ScreenItems/Button/Top","kind":"value","status":"ok","value":0},
                  "Width":{"path":"/Screens/Overview/ScreenItems/Button/Width","kind":"value","status":"ok","value":120},
                  "Height":{"path":"/Screens/Overview/ScreenItems/Button/Height","kind":"value","status":"ok","value":40}}},
               {"path":"/Screens/Overview/$selection","status":"ok","kind":"graphicSelectionSummary"}],"failures":[]}
            """)!.AsObject();
        var response = new Host.ResponseMessage { Message = "Selection", Meta = page };
        var result = Host.HmiInspectionContract.Map("GetUnifiedGraphicSelection", response, false, false, pageSize: 20).StructuredContent!;
        var data = result["data"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(page, data["page"]));
        Assert.Equal(new[] { "page", "summary" }, data.Select(field => field.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("Selection", (string?)data["summary"]);
        Assert.Equal("succeeded", (string?)result["meta"]!["outcome"]);
        Assert.True((bool)result["meta"]!["paging"]!["complete"]!);
        string savedPages = new JsonArray(data["page"]!.DeepClone()).ToJsonString();
        var pages = TiaMcp.Logic.V4.Domain.DomainValidation.Read<TiaMcp.Logic.V4.Domain.GraphicSelectionPage[]>(savedPages);
        TiaMcp.Logic.V4.Domain.DomainValidation.GraphicSelections(pages, pages);
        var comparison = toolpipeline::TiaMcpServer.Siemens.Services.UnifiedGraphicComparison.Compare(savedPages, savedPages);
        Assert.Equal(0, (int)comparison["changedObjectCount"]!);

        foreach (string beforeTool in new[] { "GetUnifiedGraphicSelection", "ReadUnifiedGraphicSelection" })
        foreach (string afterTool in new[] { "GetUnifiedGraphicSelection", "ReadUnifiedGraphicSelection" })
        {
            var beforePage = page.DeepClone(); beforePage["scope"]!["tool"] = beforeTool;
            var afterPage = page.DeepClone(); afterPage["scope"]!["tool"] = afterTool;
            string beforeJson = new JsonArray(beforePage).ToJsonString();
            string afterJson = new JsonArray(afterPage).ToJsonString();
            var before = TiaMcp.Logic.V4.Domain.DomainValidation.Read<TiaMcp.Logic.V4.Domain.GraphicSelectionPage[]>(beforeJson);
            var after = TiaMcp.Logic.V4.Domain.DomainValidation.Read<TiaMcp.Logic.V4.Domain.GraphicSelectionPage[]>(afterJson);
            TiaMcp.Logic.V4.Domain.DomainValidation.GraphicSelections(before, after);
            var compatible = toolpipeline::TiaMcpServer.Siemens.Services.UnifiedGraphicComparison.Compare(beforeJson, afterJson);
            Assert.True((bool)compatible["success"]!);
            Assert.Equal(0, (int)compatible["changedObjectCount"]!);

            var differentScreen = JsonNode.Parse(afterJson)!;
            differentScreen[0]!["scope"]!["screenPath"] = "/Other";
            string differentJson = differentScreen.ToJsonString();
            var different = TiaMcp.Logic.V4.Domain.DomainValidation.Read<TiaMcp.Logic.V4.Domain.GraphicSelectionPage[]>(differentJson);
            Assert.Throws<ArgumentException>(() => TiaMcp.Logic.V4.Domain.DomainValidation.GraphicSelections(before, different));
            Assert.Throws<ArgumentException>(() => toolpipeline::TiaMcpServer.Siemens.Services.UnifiedGraphicComparison.Compare(beforeJson, differentJson));
        }


        var other = Host.HmiInspectionContract.Map("GetUnifiedGlobalScript", response, false, false).StructuredContent!["data"]!;
        Assert.True(JsonNode.DeepEquals(page["records"], other["records"]));
        Assert.False(other.AsObject().ContainsKey("page"));
    }

    [Theory]
    [InlineData(nameof(NotSupportedException))]
    [InlineData(nameof(ArgumentException))]
    [InlineData(nameof(InvalidOperationException))]
    public void Plc_B5_failures_after_a_possible_change_keep_the_unknown_outcome(string code)
    {
        using var release = Host.HardwareContract.UseRelease("21");

        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply {
                Failure = new PlcSoftwareFailure { Code = code, Message = "Native result unavailable after a change." },
                MayHaveChanged = true, OutcomeUnknown = true }));
            WorkerProtocol.ValidateExchangeResult(operation, args, json);
            return json;
        }, () => "fixture");
        var body = Host.EngineeringToolContract.Run("GetPlcOpcUaConfiguration", true,
            () => service.GetOpcUaConfig("PLC")).StructuredContent!;

        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
    }

    [Theory]
    [InlineData(nameof(NotSupportedException))]
    [InlineData(nameof(ArgumentException))]
    [InlineData(nameof(InvalidOperationException))]
    public void Plc_B5_read_failures_report_read_failed_without_requiring_a_session_reset(string code)
    {
        using var release = Host.HardwareContract.UseRelease("21");

        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply {
                Failure = new PlcSoftwareFailure { Code = code, Message = "Native result unavailable after a change." },
                MayHaveChanged = true, OutcomeUnknown = true }));
            WorkerProtocol.ValidateExchangeResult(operation, args, json);
            return json;
        }, () => "fixture");
        var body = Host.EngineeringToolContract.Run("GetPlcOpcUaConfiguration", false,
            () => service.GetOpcUaConfig("PLC")).StructuredContent!;

        Assert.Equal("read-failed", (string?)body["meta"]!["outcome"]);
        Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
    }

    [Theory]
    [InlineData("mayHaveExternalEffects")]
    [InlineData("operationStarted")]
    public void Optional_package_side_effect_evidence_keeps_the_write_outcome_unknown(string flag)
    {
        using var scope = EngineHostParity.EnterScope();
        var reply = new OptionalProductReply { Message = "Native result unavailable.", Meta = new() {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false, [flag] = true } };
        var json = JsonNode.Parse(WorkerJson.Serialize(reply))!;
        WorkerProtocol.ValidateExchangeResult("optional-products.ExecuteOptionalProduct", new(), json);
        var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
        var body = Host.OptionalPackageContract.Map("ManageSinumerikArchive", response, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
    }

    [Fact]
    public void An_uncertain_extension_write_still_requires_a_session_reset()
    {
        var reply = new HardwareAddressingReply { Message = "Write readback unavailable.", MayHaveChanged = true,
            OutcomeUnknown = true, Meta = new() { ["operationSuccess"] = false, ["mayHaveChanged"] = true } };
        var json = JsonNode.Parse(WorkerJson.Serialize(reply))!;

        WorkerProtocol.ValidateExchangeResult("startdrive.ExecuteStartdrive", new(), json);

        var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
        var body = Host.DriveToolContract.Map("ManageDriveFunctions", response, true, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plc_B5_message_only_failure_is_a_native_error_without_session_reset(bool legacyMeta)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        const string message = "Export failed: Synthetic native export refusal.";
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply { Message = message,
                Meta = legacyMeta ? new() { ["success"] = false } : null }));
            WorkerProtocol.ValidateExchangeResult(operation, args, json);
            return json;
        }, () => "fixture");
        var result = Host.EngineeringToolContract.Run("ExportAlarmClasses", true, () => service.ExportAlarmClasses("PLC", "fixture.dat"));
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains(message, result.StructuredContent["error"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.NotEqual("unknown", (string?)result.StructuredContent["meta"]!["outcome"]);
    }

    [Theory]
    [InlineData(nameof(NotSupportedException), "UNSUPPORTED_CAPABILITY")]
    [InlineData(nameof(ArgumentException), "NATIVE_OPERATION_FAILED")]
    [InlineData(nameof(InvalidOperationException), "NATIVE_OPERATION_FAILED")]
    public void Plc_B5_typed_failures_cross_the_port_without_faulting_the_session(string code, string expected)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        const string message = "Synthetic OPC UA native refusal.";

        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply {
                Failure = new PlcSoftwareFailure { Code = code, Message = message } }));
            WorkerProtocol.ValidateExchangeResult(operation, args, json);
            return json;
        }, () => "fixture");
        var result = Host.EngineeringToolContract.Run("GetPlcOpcUaConfiguration", false, () => service.GetOpcUaConfig("PLC"));
        Assert.Equal(expected, (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains(message, result.StructuredContent["error"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.NotEqual("unknown", (string?)result.StructuredContent["meta"]!["outcome"]);

    }

    [Theory]
    [InlineData("")]
    [InlineData("\n   at Fixture.Native()")]
    public void Runtime_skeleton_failure_reports_the_native_cause(string diagnostic)
    {
        const string cause = "Configured HMI connection 'Missing' was not found.";
        var response = new Host.ResponseMessage { Message = "Failed creating HMI skeleton", Meta = new JsonObject {
            ["success"] = false, ["mayHaveChanged"] = false, ["nativeMessage"] = cause + diagnostic,
            ["steps"] = new JsonArray(new JsonObject { ["step"] = "exception", ["ok"] = false, ["detail"] = cause + diagnostic }) } };
        var result = Host.UnifiedHmiContract.Map("SetUnifiedHmiRuntimeState", response, true, true).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]!["code"]);
        Assert.Equal(cause, (string?)result["error"]!["message"]);
        Assert.Equal("hmi-before-write", (string?)result["error"]!["details"]!["condition"]);
        Assert.NotEqual("unknown", (string?)result["meta"]!["outcome"]);
        Assert.DoesNotContain("at Fixture.Native", result.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Projectless_probes_preserve_the_existing_response_shape(bool whitelist)
    {
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((operation, _) =>
            JsonNode.Parse(WorkerJson.Serialize(new HardwareProbeReply {
                Lines = new[] { "Project is null" }, FailureCode = "PROJECT_NOT_BOUND" })),
            () => true, () => "fixture");
        var tools = new Host.HardwareNetworkTools(service);
        var result = whitelist ? tools.ProbeHardwareHmiConnectionWhitelistedServicesV4("PLC", "HMI")
            : tools.ProbeHardwareHmiConnectionOwnerCandidatesV4("PLC", "HMI");
        Assert.Equal("PROJECT_NOT_BOUND", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.False(result.StructuredContent["data"]!.AsObject().ContainsKey("target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_probe_root_is_returned_as_the_not_found_target(bool whitelist)
    {
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((operation, _) =>
            JsonNode.Parse(WorkerJson.Serialize(new HardwareProbeReply {
                Lines = new[] { "HMI root: Missing -> <not found>" }, FailureCode = "NOT_FOUND", Target = "Missing" })),
            () => true, () => "fixture");
        var tools = new Host.HardwareNetworkTools(service);
        var result = whitelist ? tools.ProbeHardwareHmiConnectionWhitelistedServicesV4("PLC", "Missing")
            : tools.ProbeHardwareHmiConnectionOwnerCandidatesV4("PLC", "Missing");
        Assert.Equal("NOT_FOUND", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal("Missing", (string?)result.StructuredContent["error"]!["details"]!["target"]);
    }

    [Fact]
    public void Event_script_indentation_is_content_while_diagnostic_stacks_are_removed()
    {
        const string script = "export function Click() {\n    HMIRuntime.Trace('fixture');\n}";
        const string globals = "function helper() {\n    return 1;\n}";
        var response = new Host.ResponseMessage { Message = "events", Meta = new JsonObject {
            ["operationSuccess"] = true, ["records"] = new JsonArray(new JsonObject {
                ["ScriptCode"] = script, ["GlobalDefinitionAreaScriptCode"] = globals,
                ["detail"] = "Native failure\n   at Fixture.Detail()",
                ["error"] = "Native failure\n   at Fixture.Read()", ["stackTrace"] = "private" }) } };
        var result = Host.UnifiedHmiContract.Map("GetUnifiedObjectEvents", response, false, false).StructuredContent!;
        Assert.Equal(script, (string?)result["data"]!["items"]![0]!["ScriptCode"]);
        Assert.Equal(globals, (string?)result["data"]!["items"]![0]!["GlobalDefinitionAreaScriptCode"]);
        Assert.DoesNotContain("at Fixture.Detail", result.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("at Fixture.Read", result.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", result.ToJsonString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> Operations()
    {
        foreach (var operation in new[] {
            "plc-alarms.ExecutePlcB5", "plc-opcua.ExecutePlcB5", "plc-documents.ExecutePlcB5",
            "sivarc.ExecuteSivarc", "startdrive.ExecuteStartdrive", "startdrive.ExecuteDcc",
            "plc-safety.ExecuteSafety", "plc-safety.ExecuteSafetyValidation",
            "cfc-charts.ExecuteOptionalProduct", "optional-products.ExecuteOptionalProduct",
            "version-control.ExecuteVersionControl", "version-control.ExecuteTeamcenter",
            "project-lifecycle.ReadPortalInfo", "project-lifecycle.ConnectIsolatedPortal",
            "project-lifecycle.ArchiveSavedProject", "project-lifecycle.RetrieveProjectArchive",
            "project-lifecycle.ExecuteMultiuserSession", "project-lifecycle.ValidateAutomationContext",
            "project-security.ReadProjectProtection", "project-security.ExecuteProjectUsers",
            "project-security.ExecuteCertificate", "project-security.ExecuteSecurityDeep", "project-security.ExecuteDeviceUsers",
            "engineering-services.ExecuteEngineeringServices" })
            foreach (var verdict in new[] { "normal", "unsupported", "failure" })
                yield return new object[] { operation, verdict };
    }

    private static object Reply(string operation, string verdict)
    {
        var meta = new Dictionary<string, object?> {
            ["operationSuccess"] = verdict == "normal", ["apiCallSuccess"] = verdict == "normal",
            ["mayHaveChanged"] = false };
        if (verdict == "unsupported")
        {
            meta["status"] = "NotSupportedOnVersion";
            meta["v4Rejection"] = "UNSUPPORTED_CAPABILITY";
            meta["rejectionCode"] = "UNSUPPORTED_CAPABILITY";
            meta["operationNotStarted"] = true;
        }
        const string message = "Synthetic native capability verdict.";
        if (operation.EndsWith("ExecutePlcB5", StringComparison.Ordinal))
            return new PlcB5Reply { Message = message, Meta = meta, Ok = verdict == "normal",
                Blocks = new[] { new PlcB5Block { Name = "Main", IsConsistent = true, Attributes = new() {
                    new PlcB5Attribute { Name = "Number", AccessMode = "Read", Value = 1 } } } }, Count = 1,
                Events = new() { new PlcB5DocumentEvent { Kind = "native", Stage = "export", State = "Success", Value = true } },
                Data = new() { ["interfaces"] = new[] { "Interface_1" } } };
        if (operation.EndsWith("ExecuteOptionalProduct", StringComparison.Ordinal))
            return new OptionalProductReply { Message = message, Meta = meta };
        if (operation.StartsWith("version-control.", StringComparison.Ordinal))
            return new VersionControlResult { Message = message, Items = new[] { "Workspace_1" }, Meta = meta };
        if (operation.StartsWith("project-", StringComparison.Ordinal))
            return new SecurityResult { Message = message, Meta = meta };
        return new HardwareAddressingReply { Message = message, Meta = meta };
    }

    [Theory, MemberData(nameof(Operations))]
    public void Contract_replies_validate_without_invented_step_metadata(string operation, string verdict)
    {
        var json = JsonNode.Parse(WorkerJson.Serialize(Reply(operation, verdict)))!;

        WorkerProtocol.ValidateExchangeResult(operation, new(), json);

        Assert.Null(json["Meta"]!["success"]);
        if (operation.EndsWith("ExecutePlcB5", StringComparison.Ordinal))
        {
            json["Blocks"]![0]!["Name"] = 42;
            Assert.Throws<IOException>(() => HardwareFamilyWire.Validate(operation, json));
            json["Blocks"]![0]!["Name"] = "Main";
        }
        json["OutcomeUnknown"] = "false";
        var malformed = Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult(operation, new(), json));

    }

    [Theory]
    [InlineData("sivarc.ExecuteSivarc", "GetSivarcRuleTree", "library")]
    [InlineData("startdrive.ExecuteStartdrive", "GetDriveParameters", "drive")]
    [InlineData("startdrive.ExecuteDcc", "GetDccObject", "library")]
    [InlineData("plc-safety.ExecuteSafety", "GetSafetyBlockSignatures", "security")]
    [InlineData("plc-safety.ExecuteSafetyValidation", "ListSafetyActivationTests", "security")]
    [InlineData("optional-products.ExecuteOptionalProduct", "ListTestSuiteCases", "optional")]
    [InlineData("cfc-charts.ExecuteOptionalProduct", "ManageCfcChartProtection", "optional")]
    [InlineData("plc-alarms.ExecutePlcB5", "ExportAlarmClasses", "engineering")]
    [InlineData("plc-opcua.ExecutePlcB5", "GetPlcOpcUaConfiguration", "engineering")]
    [InlineData("plc-documents.ExecutePlcB5", "GetPlcBlockScopes", "engineering")]
    [InlineData("project-lifecycle.ExecuteMultiuserSession", "ManageMultiuserSession", "security")]
    [InlineData("project-security.ReadProjectProtection", "GetProjectProtection", "security")]
    [InlineData("engineering-services.ExecuteEngineeringServices", "CompareLibraries", "security")]
    [InlineData("version-control.ExecuteVersionControl", "GetVersionControlStatus", "library")]
    public void Extension_verdicts_keep_typed_errors_and_the_worker_message(string operation, string tool, string contract)
    {
        using var scope = EngineHostParity.EnterScope();
        foreach (string verdict in new[] { "normal", "unsupported", "failure" })
        foreach (bool writes in new[] { false, true })
        {
            var json = JsonNode.Parse(WorkerJson.Serialize(Reply(operation, verdict)))!;
            WorkerProtocol.ValidateExchangeResult(operation, new(), json);
            var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
            var result = contract switch {
                "library" => Host.LibraryToolContract.Map(tool, response, writes),
                "engineering" => Host.EngineeringToolContract.Map(tool, response, writes),
                "drive" => Host.DriveToolContract.Map(tool, response, writes, true),
                "security" => Host.SecurityToolContract.MapResult(tool, response, !writes, true),
                _ => Host.OptionalPackageContract.Map(tool, response, writes) };
            var body = result.StructuredContent!;
            Assert.Equal(verdict == "normal", (bool)body["ok"]!);
            Assert.NotEqual("unknown", (string?)body["meta"]!["outcome"]);
            if (verdict == "normal") continue;
            Assert.Equal(verdict == "unsupported" ? "UNSUPPORTED_CAPABILITY" : "NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
            Assert.Contains(response.Message, body["error"]!.ToJsonString(), StringComparison.Ordinal);
        }
    }
}
