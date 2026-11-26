extern alias adaptertests;
extern alias toolpipeline;
extern alias foundationhost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Reflection;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;
using Foundation = foundationhost::TiaMcp.FoundationHost;

public sealed class HonestWriteOutcomeTests
{
    [Theory, InlineData("DeleteEmptyUnifiedHmiScreenGroup", "groupPath"), InlineData("ManageSivarcRuleContainer", "path")]
    public void Nonempty_container_retains_the_documented_native_refusal(string tool, string parameter)
    {
        var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.MutationEvidenceTests.RunStep("21", tool, _ =>
            throw AdapterPreconditionException.Failed("Nonempty container: Line", "target-not-empty", parameter));
        var response = new Host.ResponseMessage { Message = reply.Message, Meta = JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() };
        var body = Body(tool == "ManageSivarcRuleContainer" ? Host.LibraryToolContract.Map(tool, response, false, true)
            : Host.HmiInspectionContract.Map(tool, response, false, true));
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("target-not-empty", (string?)body["error"]?["details"]?["condition"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Fact]
    public void Goto_screen_recipe_refuses_without_emitting_placeholder_script()
    {
        var body = Body(new Host.HmiOfflineTools().BuildUnifiedHmiButtonActionScriptV4("goto-screen", "Tapped", targetScreen: "Main"));
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.DoesNotContain("TODO", body.ToJsonString());
    }

    [Fact]
    public void Classic_graphic_conflict_surfaces_the_actual_replacement_option()
    {
        const string reason = "HMI import target already exists: Home. Set importOptions=Override explicitly to replace it.";
        var reply = new Host.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
            ["status"] = "InvalidState", ["v4FailureCode"] = "PRECONDITION_FAILED", ["nativeMessage"] = reason } };
        var body = Body(Host.ClassicMotionToolContract.Map("ManageClassicHmiGraphic", reply, true, false));
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Contains("importOptions=Override", (string?)body["error"]?["message"]);
    }

    [Fact]
    public void Global_script_text_occurs_once_with_analysis_locations_preserved()
    {
        const string path = "/Scripts/Line/Line.hmi.js";
        const string script = "export function start() { return 42; }";
        var location = new JsonObject { ["path"] = path, ["start"] = 0, ["end"] = script.Length };
        var records = new JsonArray(
            new JsonObject { ["kind"] = "nativeFile", ["path"] = path, ["rawText"] = script },
            new JsonObject { ["kind"] = "scriptAnalysis", ["path"] = path, ["rawText"] = script, ["functions"] = new JsonArray(
                new JsonObject { ["name"] = "start", ["rawText"] = script, ["body"] = "{ return 42; }", ["evidence"] = location.DeepClone() }) });
        var reply = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = true, ["records"] = records } };
        var body = Body(Host.HmiInspectionContract.Map("GetUnifiedGlobalScript", reply, false, false));
        Assert.True((bool?)body["ok"]);
        var rows = body["data"]!["records"]!.AsArray();
        Assert.Equal(script, (string?)rows[0]!["rawText"]);
        Assert.Null(rows[1]!["rawText"]);
        Assert.Null(rows[1]!["functions"]![0]!["body"]);
        Assert.Null(rows[1]!["functions"]![0]!["rawText"]);
        Assert.True(JsonNode.DeepEquals(location, rows[1]!["functions"]![0]!["evidence"]));
    }

    [Fact]
    public void Cwc_manifest_refusal_names_the_canonical_source_directory()
    {
        using var scope = EngineHostParity.EnterScope();
        string directory = Directory.CreateTempSubdirectory("cwc-missing-manifest-").FullName;
        try
        {
            var body = Body(new Host.V21EcosystemTools().ManageUnifiedCwcPackageV4(directory));
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal("/sourceDirectory", (string?)body["error"]?["details"]?["parameter"]);
            Assert.Contains("sourceDirectory", (string?)body["error"]?["message"]);
            Assert.DoesNotContain("'directory'", (string?)body["error"]?["message"]);
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public void V20_graphic_list_admission_names_the_missing_public_API()
    {
        const string name = "ListUnifiedEngineeringObjects";
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var scope = new HardwareAddressingAdmissionTests.SessionFixture(worker, "20");
        var tool = PortedToolContract.Create(worker, "20").OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == name);
        var args = new Dictionary<string, JsonElement> {
            ["softwarePath"] = JsonSerializer.SerializeToElement("HMI"), ["category"] = JsonSerializer.SerializeToElement("graphicLists") };
        var result = Body(tool.RefusePortedCapability(args)!);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result["error"]?["code"]);
        Assert.Contains("HmiGraphicLists", (string?)result["error"]?["message"]);
        Assert.Contains("V21", (string?)result["error"]?["message"]);
    }

    [Fact]
    public void Empty_sivarc_container_path_names_the_missing_input()
    {
        using var scope = EngineHostParity.EnterScope();
        var body = Body(new Host.SivarcTools(null!).ManageSivarcRuleContainerV4("screens", "folder", ""));
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/path", (string?)body["error"]?["details"]?["parameter"]);
    }

    [Fact]
    public void Invalid_sivarc_category_lists_the_declared_values()
    {
        using var scope = EngineHostParity.EnterScope();
        var body = Body(new Host.OptionalEngineeringTools(null!).ListSivarcRules("invalid"));
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/category", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal(new[] { "screens", "tags", "advancedTags", "alarms", "copies", "textLists" },
            body["error"]!["details"]!["allowedValues"]!.AsArray().Select(value => (string)value!));
    }

    [Fact]
    public void Faceplate_read_surfaces_the_failed_traversal_reason()
    {
        const string reason = "Selected screen item is not a Unified faceplate container.";
        var reply = new Host.ResponseMessage { Meta = new JsonObject {
            ["apiCallSuccess"] = false, ["failures"] = new JsonArray(new JsonObject { ["reason"] = reason, ["code"] = "TraversalFailed" }) } };
        var body = Body(Host.HmiInspectionContract.Map("GetUnifiedFaceplateInstance", reply, false, false));
        Assert.Contains(reason, (string?)body["error"]?["message"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
    }

    [Theory]
    [InlineData(true, false, "OUTCOME_UNKNOWN", "unknown")]
    [InlineData(true, true, "PRECONDITION_FAILED", "not-started")]
    [InlineData(false, false, "PRECONDITION_FAILED", "not-started")]
    public void Exchange_write_exception_requires_dispatch_and_no_refusal_proof_for_unknown(bool sent, bool known, string code, string execution)
    {
        using var scope = EngineHostParity.EnterScope();
        var failure = new InvalidOperationException("Text import did not return.");
        failure.Data["foundationRequestSent"] = sent;
        if (known) failure.Data["foundationKnownNoMutation"] = true;
        var body = Body(Host.PlcExchangeContract.Run("ImportProjectTexts", () => throw failure, true, true));
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal(execution, (string?)body["meta"]?["execution"]);
        if (execution == "unknown") Assert.Equal("native-exchange", (string?)body["data"]?["executionEvidence"]?["stage"]);
    }

    [Theory, InlineData("file", "NOT_FOUND"), InlineData("argument", "INVALID_ARGUMENT"),
        InlineData("unsupported", "UNSUPPORTED_CAPABILITY"), InlineData("admission", "PRECONDITION_FAILED")]
    public void Sent_exchange_write_preserves_typed_refusals(string kind, string code)
    {
        using var scope = EngineHostParity.EnterScope();
        Exception failure = kind switch {
            "file" => new FileNotFoundException("Input was not found."),
            "argument" => new ArgumentException("Invalid input.", nameof(kind)),
            "unsupported" => new NotSupportedException("Unavailable for this release."),
            _ => new AdapterPreconditionException("Text import refused.", "filePath", false)
        };
        failure.Data["foundationRequestSent"] = true;
        var body = Body(Host.PlcExchangeContract.Run("ImportProjectTexts", () => throw failure, true, true));
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Theory, InlineData("rejected-before-operation"), InlineData("unknown")]
    public async Task Ported_text_import_tracks_only_the_unconfirmed_worker_write_as_unsettled(string outcome)
    {
        const string tool = "ImportProjectTexts";
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(contextWorker, "18");
        using var worker = new ProjectTextWorker(outcome);
        var method = typeof(Host.EngineeringServicesTools).GetMethod(nameof(Host.EngineeringServicesTools.ImportProjectTextsV4))!;
        var registered = new Foundation.FoundationV4Tool(new Foundation.FoundationTool(worker, "18", method, tool), "18", () => new(false, 1));
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
            Params = new() { Name = tool, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"filePath":"C:/fixture/texts.xlsx","updateSourceLanguage":false,"dryRun":false}""") } };
        var body = Body(await registered.InvokeAsync(request));
        Assert.Equal(1, worker.Imports);
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
        var unsettled = toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot();
        if (outcome == "unknown")
        {
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal((string?)body["meta"]?["requestId"], Assert.Single(unsettled).RequestId);
        }
        else
        {
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Empty(unsettled);
        }
    }

    private sealed class ProjectTextWorker(string outcome) : Foundation.IFoundationWorker
    {
        internal int Imports;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 18, ["IsAttached"] = true, ["ProjectFile"] = "C:/fixture.ap18" });
            Assert.Equal("engineering-services.ExecuteEngineeringServices", operation);
            Assert.Equal("ImportProjectTexts", (string?)arguments["request"]?["Operation"]);
            Imports++;
            throw new Foundation.WorkerOperationException("Native import refused.", -32603, outcome) { Data = { ["foundationRequestSent"] = true } };
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("ListDriveObjects", "21", null, true, "Service is not provided by the selected object.")]
    [InlineData("ListDriveObjects", "21", null, false, "API member missing on this release.")]
    [InlineData("ListDccCharts", "21", null, true, "Service is not provided by the selected object.")]
    [InlineData("ListDccCharts", "21", null, false, "API member missing on this release.")]
    [InlineData("ManageDeviceUsers", "21", null, true, "Service is not provided by the selected object.")]
    [InlineData("ManageDeviceUsers", "21", null, false, "API member missing on this release.")]
    [InlineData("GetClassicHmiScreenTree", "21", null, true, "Service is not provided by the selected object.")]
    [InlineData("GetClassicHmiScreenTree", "21", null, false, "API member missing on this release.")]
    [InlineData("GetDeviceAddressing", "21", null, true, "Service is not provided by the selected object.")]
    [InlineData("GetDeviceAddressing", "21", null, false, "API member missing on this release.")]
    [InlineData("SetDeviceItemChannel", "17", null, true, "ChannelAddress is not writable on this channel per GetAttributeInfos.")]
    [InlineData("SetDeviceItemChannel", "17", null, false, "Channel API unavailable in the installed SDK.")]
    [InlineData("ExchangeMotionCamData", "17", "export", true, "Selected object is not a service provider.")]
    [InlineData("ExchangeMotionCamData", "14sp1", "export", false, "Official service unavailable in installed API: CamDataSupport")]
    [InlineData("ManagePlcSafety", "17", "read", true, "SafetyAdministration unavailable: not an S7-1200/1500 F-CPU or Safety option not installed.")]
    [InlineData("ManagePlcSafety", "17", "generateBaseId", false, "SafetyBaseIdProvider requires V21.")]
    [InlineData("ExportSafetyPrintout", "17", "export", true, "SafetyPrintout unavailable: not an S7-1200/1500 F-CPU or Safety option not installed.")]
    [InlineData("ExportSafetyPrintout", "16", "export", false, "The requested Safety operation is unavailable on this release.")]
    [InlineData("ManageSinumerikSafetyMode", "17", "read", true, "The selected device has no SafetyModeProvider.")]
    [InlineData("ManageSinumerikSafetyMode", "21", "read", false, "SafetyModeProvider is absent from the selected release's SDK.")]
    public void Capability_refusals_keep_their_origin_through_the_tool_contract(string tool, string release, string? action, bool target, string reason)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.MutationEvidenceTests.RunStep(release, tool, meta => {
            if (action != null) meta["action"] = action;
            throw target ? StepFailureStatus.UnsupportedByTarget(reason) : new NotSupportedException(reason);
        });
        Assert.Equal("NotSupportedOnVersion", reply.Meta["status"]);
        Assert.Equal(target, reply.Meta.TryGetValue("unsupportedBy", out var origin) && Equals(origin, "target"));
        var response = new Host.ResponseMessage { Message = reply.Message, Meta = JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() };
        var body = (tool switch {
            "GetDeviceAddressing" => Host.HardwareContract.Map(tool, response, false, true),
            "GetClassicHmiScreenTree" => Host.ClassicMotionToolContract.Map(tool, response, true, false),
            "ListDriveObjects" or "ListDccCharts" => Host.DriveToolContract.Map(tool, response, false, false),
            "SetDeviceItemChannel" => Host.HardwareToolContract.MapResult(tool, response, false, true),
            "ExchangeMotionCamData" => Host.ClassicMotionToolContract.Map(tool, response, false, true),
            "ManageSinumerikSafetyMode" => Host.OptionalPackageContract.Map(tool, response, true),
            _ => Host.SecurityToolContract.MapResult(tool, response, false, true)
        }).StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal(target ? tool + " cannot run on the selected object: " + reason
            : CapabilityMessage(release, action == null ? tool : tool + "." + action, reason), (string?)body["error"]?["message"]);
        Assert.Equal(release, (string?)body["error"]?["details"]?["releaseKey"]);
        Assert.Equal(tool, (string?)body["error"]?["details"]?["capability"]);
        Assert.Equal(action, (string?)body["error"]?["details"]?["action"]);
    }

    [Fact]
    public void Wrapped_dispatch_marker_preserves_an_unconfirmed_exchange_write()
    {
        var native = new IOException("Reply interrupted.") { Data = { ["foundationRequestSent"] = true } };
        var body = Body(Host.PlcExchangeContract.Run("ImportProjectTexts", () => throw new TargetInvocationException(native), true, true));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
    }

    [Theory]
    [InlineData(false, false, "PRECONDITION_FAILED", "not-started")]
    [InlineData(true, false, "OUTCOME_UNKNOWN", "unknown")]
    [InlineData(true, true, "PRECONDITION_FAILED", "not-started")]
    public void Exchange_OpennessError_write_uses_dispatch_and_mutation_evidence(bool sent, bool knownNoMutation, string code, string execution)
    {
        using var scope = EngineHostParity.EnterScope();
        var failure = new TiaMcpServer.Siemens.PortalException(TiaMcpServer.Siemens.PortalErrorCode.OpennessError, "Text import failed.");
        failure.Data["foundationRequestSent"] = sent;
        if (knownNoMutation) failure.Data["foundationKnownNoMutation"] = true;
        var body = Body(Host.PlcExchangeContract.Run("ImportProjectTexts", () => throw failure, true, true));
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal(execution, (string?)body["meta"]?["execution"]);
    }

    [Fact]
    public void Exchange_portal_release_gap_keeps_its_original_message()
    {
        using var scope = EngineHostParity.EnterScope();
        var failure = new TiaMcpServer.Siemens.PortalException(TiaMcpServer.Siemens.PortalErrorCode.NotSupportedOnVersion, "Native diagnostic.");
        var body = Body(Host.PlcExchangeContract.Run("ImportProjectTexts", () => throw failure, true, true));
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal($"ImportProjectTexts is unavailable on TIA V{Host.McpServer.ReleaseKey}.", (string?)body["error"]?["message"]);
    }

    [Fact]
    public void Failed_Plcsim_scenario_with_version_status_keeps_its_native_failure_code()
    {
        var response = new { ok = false, status = "NotSupportedOnVersion", steps = new[] { new { ok = false } } };
        var body = Body(Host.RuntimeToolContract.Map("RunPlcSimAdvancedTestScenario", response, false, true));
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void B5_native_write_failure_keeps_dispatch_and_requires_explicit_unchanged_evidence(bool proved)
    {
        using var release = Host.HardwareContract.UseRelease("17");
        var service = new Services.PlcB5PortService((_, _) => {
            var result = new JsonObject { ["Failure"] = new JsonObject { ["Code"] = "OpennessError", ["Message"] = "Import refused." } };
            if (proved) { result["MayHaveChanged"] = false; result["OutcomeUnknown"] = false; }
            return result;
        }, () => "fixture.ap17");
        var failure = Assert.Throws<TiaMcpServer.Siemens.PortalException>(() => service.ImportAlarmClasses("PLC", "alarms.dat"));
        Assert.Equal(true, failure.Data["foundationRequestSent"]);
        Assert.Equal(proved, failure.Data["foundationKnownNoMutation"]);
        var body = Body(Host.EngineeringToolContract.Run("ImportAlarmClasses", true, () => throw new TargetInvocationException(failure)));
        Assert.Equal(proved ? "NATIVE_OPERATION_FAILED" : "OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.DoesNotContain("before its write", (string?)body["error"]?["message"]);
    }

    [Fact]
    public void B5_typed_write_failure_without_mutation_evidence_is_unknown()
    {
        using var release = Host.HardwareContract.UseRelease("17");
        var service = new Services.PlcB5PortService((_, _) => new JsonObject {
            ["Failure"] = new JsonObject { ["Code"] = "NotFound", ["Message"] = "Import refused." }
        }, () => "fixture.ap17");
        var body = Body(Host.EngineeringToolContract.Run("ImportAlarmClasses", true, () => service.ImportAlarmClasses("PLC", "alarms.dat")));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
    }

    [Theory, InlineData(null), InlineData("null"), InlineData("false"), InlineData("\"false\""), InlineData("{}")]
    public void Unsupported_B5_write_requires_readable_mutation_evidence(string? flagJson)
    {
        using var release = Host.HardwareContract.UseRelease("17");
        var reply = new JsonObject {
            ["Failure"] = new JsonObject { ["Code"] = nameof(NotSupportedException), ["Message"] = "Import refused." },
            ["OutcomeUnknown"] = false
        };
        if (flagJson != null) reply["MayHaveChanged"] = JsonNode.Parse(flagJson);
        var service = new Services.PlcB5PortService((_, _) => reply, () => "fixture.ap17");
        var body = Body(Host.EngineeringToolContract.Run("ImportAlarmClasses", true, () => service.ImportAlarmClasses("PLC", "alarms.dat")));
        Assert.Equal(flagJson == "false" ? "UNSUPPORTED_CAPABILITY" : "OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
    }

    [Fact]
    public void Missing_Umc_user_is_refused_by_name_before_delete()
    {
        var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.MutationEvidenceTests.RunStep("17", "ManageUmcUsers", meta => {
            meta["action"] = "delete";
            throw new PlcSoftwareException("NotFound", "Exact UMC user not found in the project: Operator") { Data = { ["parameter"] = "name" } };
        });
        var response = new Host.ResponseMessage { Message = reply.Message, Meta = JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() };
        var body = Host.SecurityToolContract.MapResult("ManageUmcUsers", response, false, true).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    public static IEnumerable<object[]> RetrievalRefusals()
        => new[] { "15.1", "16", "17", "18", "19", "20", "21" }.SelectMany(release =>
            new[] { "AlreadyExists", "InvalidParams", "NotFound", "PreconditionFailed" }.SelectMany(status =>
                new[] { false, true }.Select(dryRun => new object[] { release, status, dryRun })));

    [Theory, MemberData(nameof(RetrievalRefusals))]
    public void Retrieval_refusals_retain_code_and_field_identity(string release, string status, bool dryRun)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string parameter = status == "NotFound" ? "archivePath" : "destinationDirectory";
        var evidence = new JsonObject {
            ["mayHaveChanged"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false,
            ["status"] = status, ["parameter"] = parameter,
            ["nativeMessage"] = "Retrieval refused.\npassword=hunter2"
        };
        if (status == "PreconditionFailed")
        { evidence["precondition"] = "project-open"; evidence["refusal"] = "Close the open project/session explicitly.\npassword=hunter2"; }
        var body = Host.SecurityToolContract.MapResult("RetrieveProjectArchive",
            new Host.ResponseMessage { Message = "RetrieveProjectArchive failed", Meta = evidence }, dryRun, true, "hunter2").StructuredContent!;
        Assert.Equal(status switch { "AlreadyExists" => "ALREADY_EXISTS", "InvalidParams" => "INVALID_ARGUMENT", "NotFound" => "NOT_FOUND",
            _ => "PRECONDITION_FAILED" }, (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        if (status == "InvalidParams") Assert.Equal("/destinationDirectory", (string?)body["error"]?["details"]?["parameter"]);
        else Assert.Equal(status == "PreconditionFailed" ? "project-open" : status == "AlreadyExists" ? null : parameter,
            (string?)body["error"]?["details"]?[status == "PreconditionFailed" ? "condition" : "target"]);
        Assert.DoesNotContain("hunter2", body.ToJsonString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> KnownRefusals()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (string family in new[] { "hardware", "network", "plc", "hmi", "engineering", "drive", "library", "security", "optional",
            "hmi-exchange", "hmi-inspection", "classic-motion" })
        foreach (string status in new[] { "NotFound", "InvalidParams", "AlreadyExists", "NotSupportedOnVersion" })
        foreach (bool writes in new[] { false, true })
            yield return new object[] { release, family, status, writes };
    }

    private static string RefusalTool(string family) => family switch {
        "hardware" => "SetCpuAttributes", "network" => "SetDeviceItemChannel", "plc" => "CreatePlcTypeGroup",
        "hmi" => "ManageUnifiedScreenLayout", "engineering" => "ManagePlcBlockProtection", "drive" => "SetDriveParameters",
        "library" => "ImportLibraryTypeDocuments", "security" => "ManagePlcProtection", "optional" => "ManageCfcChartProtection",
        "classic-motion" => "ManagePlcSupervision", "hmi-exchange" => "ListHmiTags", "hmi-inspection" => "GetHmiProgramInfo",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    private static string CapabilityMessage(string release, string tool, string reason)
        => $"{tool} is unavailable on TIA {(release == "14sp1" ? "V14 SP1" : "V" + release)}. {reason}";

    private static JsonNode KnownRefusalResult(string family, JsonObject evidence, bool writes, string message = "Generic step failure.")
    {
        var response = new Host.ResponseMessage { Message = message, Meta = evidence };
        string tool = RefusalTool(family);
        return (family switch {
            "hardware" => Host.HardwareContract.Map(tool, response, writes, true),
            "network" => Host.HardwareToolContract.MapResult(tool, response, !writes, true),
            "plc" => Host.PlcToolContract.Map(tool, response, writes, true),
            "hmi" => Host.UnifiedHmiContract.Map(tool, response, writes, true),
            "engineering" => Host.EngineeringToolContract.Map(tool, response, writes),
            "drive" => Host.DriveToolContract.Map(tool, response, writes, true),
            "library" => Host.LibraryToolContract.Map(tool, response, writes),
            "security" => Host.SecurityToolContract.MapResult(tool, response, !writes, true),
            "optional" => Host.OptionalPackageContract.Map(tool, response, writes),
            "classic-motion" => Host.ClassicMotionToolContract.Map(tool, response, !writes, true),
            "hmi-exchange" => Host.HmiExchangeContract.Map(tool, response, writes, true),
            _ => Host.HmiInspectionContract.Map(tool, response, writes, true)
        }).StructuredContent!;
    }

    [Theory, MemberData(nameof(KnownRefusals))]
    public void Known_refusal_maps_for_each_family(string release, string family, string status, bool writes)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = KnownRefusalResult(family, new JsonObject {
            ["tool"] = "X", ["mayHaveChanged"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false,
            ["dataComplete"] = false, ["status"] = status, ["parameter"] = "target",
            ["nativeMessage"] = "Exact library type not found.\n   password=hunter2"
        }, writes);
        string code = status switch {
            "NotFound" => "NOT_FOUND", "InvalidParams" => "INVALID_ARGUMENT", "AlreadyExists" => "ALREADY_EXISTS",
            _ => "UNSUPPORTED_CAPABILITY" };
        Assert.Equal(code, (string?)body["error"]?["code"]);
        const string reason = "Exact library type not found.";
        Assert.Equal(status == "NotSupportedOnVersion" ? CapabilityMessage(release, RefusalTool(family), reason) : reason,
            (string?)body["error"]?["message"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("none", (string?)body["meta"]?["completeness"]);
        Assert.DoesNotContain("hunter2", body.ToJsonString(), StringComparison.Ordinal);
        if (status == "InvalidParams") Assert.Equal("/target", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
        if (status == "NotSupportedOnVersion") Assert.Equal(release, (string?)body["error"]?["details"]?["releaseKey"]);
    }

    [Theory, MemberData(nameof(KnownRefusals))]
    public void Known_refusal_never_applies_after_a_change(string release, string family, string status, bool writes)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = KnownRefusalResult(family, new JsonObject {
            ["tool"] = "X", ["mayHaveChanged"] = true, ["operationSuccess"] = false, ["apiCallSuccess"] = false,
            ["dataComplete"] = false, ["status"] = status, ["nativeMessage"] = "Private worker reason."
        }, writes);
        if (writes)
        {
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.DoesNotContain("Private worker reason", body["error"]!.ToJsonString(), StringComparison.Ordinal);
        }
        Assert.NotEqual("rejected-before-operation", (string?)body["meta"]?["outcome"]);
    }

    [Theory, MemberData(nameof(KnownRefusals))]
    public void Worker_failure_codes_do_not_override_a_known_unchanged_refusal(string release, string family, string status, bool writes)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string expected = status switch {
            "NotFound" => "NOT_FOUND", "InvalidParams" => "INVALID_ARGUMENT", "AlreadyExists" => "ALREADY_EXISTS",
            _ => "UNSUPPORTED_CAPABILITY" };
        foreach (string code in new[] { expected, status, "NATIVE_OPERATION_FAILED", "PRECONDITION_FAILED" })
        foreach (string field in new[] { "v4FailureCode", "rejectionCode", "both" })
        {
            var evidence = new JsonObject {
                ["tool"] = "X", ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
                ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["operationNotStarted"] = true,
                ["dataComplete"] = false, ["status"] = status, ["parameter"] = "target",
                ["nativeMessage"] = "Exact target refused.\n   password=hunter2"
            };
            if (field != "rejectionCode") evidence["v4FailureCode"] = code;
            if (field != "v4FailureCode") evidence["rejectionCode"] = code;
            var body = KnownRefusalResult(family, evidence, writes);
            Assert.Equal(expected, (string?)body["error"]?["code"]);
            const string reason = "Exact target refused.";
            Assert.Equal(status == "NotSupportedOnVersion" ? CapabilityMessage(release, RefusalTool(family), reason) : reason,
                (string?)body["error"]?["message"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Equal("none", (string?)body["meta"]?["completeness"]);
            Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
            if (status == "NotSupportedOnVersion") Assert.Equal(release, (string?)body["error"]?["details"]?["releaseKey"]);
            if (status == "InvalidParams") Assert.Equal("/target", (string?)body["error"]?["details"]?["parameter"]);
            Assert.DoesNotContain("hunter2", body.ToJsonString(), StringComparison.Ordinal);
            if (writes)
            {
                evidence["mayHaveChanged"] = true;
                // Worker rejection codes cannot turn a write with possible effects into a safe retry.
                evidence["operationNotStarted"] = false;
                var changed = KnownRefusalResult(family, evidence, true);
                Assert.Equal("OUTCOME_UNKNOWN", (string?)changed["error"]?["code"]);
                Assert.DoesNotContain("Exact target refused.", changed["error"]!.ToJsonString(), StringComparison.Ordinal);
                Assert.DoesNotContain("hunter2", changed["error"]!.ToJsonString(), StringComparison.Ordinal);
                Assert.NotEqual("rejected-before-operation", (string?)changed["meta"]?["outcome"]);
            }
        }
    }

    [Theory]
    [InlineData("library"), InlineData("drive"), InlineData("engineering"), InlineData("security"), InlineData("optional")]
    public void Known_refusal_keeps_the_redacted_top_level_worker_message(string family)
    {
        using var scope = Host.HardwareContract.UseRelease("14sp1");
        var evidence = new JsonObject {
            ["status"] = "NotSupportedOnVersion", ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
            ["operationSuccess"] = false, ["apiCallSuccess"] = false
        };
        var body = KnownRefusalResult(family, evidence, true, "Synthetic native capability verdict.\n   password=hunter2");
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal(CapabilityMessage("14sp1", RefusalTool(family), "Synthetic native capability verdict."), (string?)body["error"]?["message"]);
        Assert.Equal("14sp1", (string?)body["error"]?["details"]?["releaseKey"]);
        Assert.DoesNotContain("hunter2", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NotFound"), InlineData("InvalidParams"), InlineData("AlreadyExists"), InlineData("NotSupportedOnVersion")]
    public void Classic_capture_retains_typed_hardware_and_plc_failures(string status)
    {
        foreach (Exception cause in new Exception[] {
            new HardwareAddressingException(status, "Exact target refused."), new PlcSoftwareException(status, "Exact target refused.") })
        {
            var evidence = new JsonObject();
            var wrapped = new System.Reflection.TargetInvocationException(cause);
            Assert.Same(wrapped, Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                Host.ClassicMotionToolContract.Capture(evidence, _ => throw wrapped)));
            Assert.Equal(status, (string?)evidence["v4FailureCode"]);
            Assert.False((bool?)evidence["mayHaveChanged"]);
            Assert.False((bool?)evidence["mayHaveWrittenFiles"]);
        }
    }

    [Fact]
    public void Optional_refusal_prefers_the_native_reason()
    {
        var body = Host.OptionalPackageContract.Map("ManageCfcChartProtection", new Host.ResponseMessage {
            Message = "Generic step failure.", Meta = new JsonObject {
                ["operationNotStarted"] = true, ["operationSuccess"] = false, ["rejectionCode"] = "PRECONDITION_FAILED",
                ["nativeMessage"] = "The chart is protected.\n   password=hunter2"
            } }, true).StructuredContent!;
        Assert.Equal("The chart is protected.", (string?)body["error"]?["message"]);
        Assert.DoesNotContain("hunter2", body["error"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mayHaveWrittenFiles"), InlineData("operationStarted"), InlineData("requiresSessionReset")]
    [InlineData("requiresExplicitRebind"), InlineData("writeOutcomeUnknown"), InlineData("mayHaveExternalEffects")]
    [InlineData("connectionUnavailable")]
    public void Known_refusal_requires_unchanged_evidence(string field)
    {
        var evidence = new JsonObject { ["status"] = "NotFound", ["mayHaveChanged"] = false, [field] = true };
        if (field is "requiresSessionReset" or "requiresExplicitRebind")
            Assert.NotNull(TiaMcp.Logic.V4.HostBehavior.KnownRefusal(evidence, false, "21", "Fixture"));
        else Assert.Null(TiaMcp.Logic.V4.HostBehavior.KnownRefusal(evidence, false, "21", "Fixture"));
        evidence[field] = false;
        Assert.NotNull(TiaMcp.Logic.V4.HostBehavior.KnownRefusal(evidence, false, "21", "Fixture"));
        evidence.Remove("mayHaveChanged");
        Assert.Null(TiaMcp.Logic.V4.HostBehavior.KnownRefusal(evidence, false, "21", "Fixture"));
        evidence["mayHaveChanged"] = "false";
        Assert.Null(TiaMcp.Logic.V4.HostBehavior.KnownRefusal(evidence, false, "21", "Fixture"));
        evidence["mayHaveChanged"] = false;
        Assert.Null(TiaMcp.Logic.V4.HostBehavior.KnownRefusal(evidence, true, "21", "Fixture"));
    }

    [Theory]
    [InlineData("NOT_FOUND", "NotFound")]
    [InlineData("INVALID_ARGUMENT", "InvalidArgument")]
    [InlineData("ALREADY_EXISTS", "AlreadyExists")]
    [InlineData("NotSupported", "UnsupportedCapability")]
    [InlineData("UNSUPPORTED_CAPABILITY", "UnsupportedCapability")]
    public void Known_refusal_accepts_status_aliases_and_has_a_safe_fallback(string status, string code)
    {
        var error = TiaMcp.Logic.V4.HostBehavior.KnownRefusal(new JsonObject { ["status"] = status, ["mayHaveChanged"] = false }, null, "21", "Fixture")!;
        Assert.Equal(code, error.Code.ToString());
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        if (status == "INVALID_ARGUMENT") Assert.Equal("/arguments", Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error.Details).Parameter);
    }

    [Fact]
    public void Password_policy_exception_in_evidence_keeps_its_reason()
    {
        const string message = "PasswordPolicySettingsException: Password policy cannot be changed.";
        var data = new JsonObject { ["evidence"] = new JsonObject { ["error"] = message } };
        TiaMcp.Logic.V4.HostBehavior.SanitizeEvidence(data, evidence: false);
        Assert.Equal(message, (string?)data["evidence"]?["error"]);
    }

    [Fact]
    public void Both_native_message_text_keys_are_sanitized()
    {
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = true,
            ["messageData"] = new JsonObject { ["text"] = "Reason\n   password=hunter2", ["detailText"] = "Detail\n   password=hunter2" },
            ["detailMessageData"] = new JsonArray(new JsonObject { ["text"] = "Inner\n   password=hunter2" }) } };
        var body = Host.PlcToolContract.Map("DescribePlcBlockLogic", response, false, true).StructuredContent!;
        Assert.Equal("Reason", (string?)body["data"]?["evidence"]?["messageData"]?["text"]);
        Assert.Equal("Inner", (string?)body["data"]?["evidence"]?["detailMessageData"]?[0]?["text"]);
        Assert.DoesNotContain("hunter2", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Native_detail_text_keeps_the_reason_and_removes_multiline_diagnostics()
    {
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = false, ["mayHaveChanged"] = false,
            ["status"] = "ReadOrWriteFailed", ["nativeMessage"] = "Native reason", ["messageData"] = new JsonObject {
                ["detailText"] = "Native reason\n   at Private.Native.Invoke()" },
            ["detailMessageData"] = new JsonArray(new JsonObject { ["detailText"] = "Inner reason\nPrivate diagnostic continuation" }) } };
        var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", response, true, true).StructuredContent!;
        Assert.Equal("Native reason", (string?)body["data"]?["evidence"]?["messageData"]?["detailText"]);
        Assert.Equal("Inner reason", (string?)body["data"]?["evidence"]?["detailMessageData"]?[0]?["detailText"]);
        Assert.DoesNotContain("Private", body.ToJsonString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> LogicTextCases()
    {
        foreach (string release in ReleaseEnds.OldestAndNewest(
            new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }, _ => "outcome", release => release))
        {
            yield return new object[] { release, "SCL", "IF #a THEN\n   #b := TRUE;\n   at := 1;\nEND_IF;" };
            yield return new object[] { release, "LAD", "Network 1\n   #b\n   at contact" };
        }
    }

    [Theory, MemberData(nameof(LogicTextCases))]
    public void Described_logic_keeps_indented_scl_and_lad_text(string release, string language, string text)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var response = new Host.ResponseBlockLogic { Readable = text, Language = language,
            Meta = new JsonObject { ["operationSuccess"] = true } };
        var body = Host.PlcToolContract.Map("DescribePlcBlockLogic", response, false, true).StructuredContent!;
        Assert.Equal(text, (string?)body["data"]?["readable"]);
    }

    [Theory]
    [InlineData("session"), InlineData("hmi")]
    public void Indented_scripts_survive_the_shared_result_cleaners(string contract)
    {
        const string text = "function read() {\n   at = 1;\n   return at;\n}";
        var response = new { readable = text, scriptCode = text, globalDefinitionAreaScriptCode = text,
            meta = new JsonObject { ["operationSuccess"] = true } };
        var body = (contract == "session" ? Host.SessionToolContract.Map("GetPortalInfo", response, false, true)
            : Host.HmiExchangeContract.Map("GetHmiProgramInfo", response, false, true)).StructuredContent!;
        foreach (string key in new[] { "readable", "scriptCode", "globalDefinitionAreaScriptCode" })
            Assert.Equal(text, (string?)body["data"]?[key]);
    }

    [Theory]
    [InlineData("plc")]
    [InlineData("session")]
    [InlineData("hmi")]
    public void Exception_text_in_evidence_is_still_reduced(string contract)
    {
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = false,
            ["nativeMessage"] = "KeyNotFoundException: The given key was not present.\n   at Private.Method()",
            ["stackTrace"] = "Private.Method()" } };
        var body = (contract switch {
            "plc" => Host.PlcToolContract.Map("DescribePlcBlockLogic", response, false, true),
            "session" => Host.SessionToolContract.Map("GetPortalInfo", response, false, true),
            _ => Host.HmiExchangeContract.Map("ListHmiTags", response, false, true)
        }).StructuredContent!;
        Assert.Equal("KeyNotFoundException: The given key was not present.", (string?)body["data"]?["evidence"]?["nativeMessage"]);
        Assert.DoesNotContain("Private.Method", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Library_failure_message_carries_the_native_detail()
    {
        string text = NativeFailureText.Compose("Error when calling method Import", "Action is not supported within ExclusiveAccess.",
            Array.Empty<string>());
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = false, ["apiCallSuccess"] = false,
            ["mayHaveChanged"] = false, ["status"] = "ReadOrWriteFailed", ["nativeMessage"] = text } };
        var body = Host.LibraryToolContract.Map("ImportLibraryTypeDocuments", response, true).StructuredContent!;
        Assert.Contains("Action is not supported within ExclusiveAccess.", (string?)body["error"]?["message"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("14sp1"), InlineData("21")]
    public void Missing_provider_is_unsupported_capability(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = false,
            ["mayHaveChanged"] = false, ["status"] = "NotSupportedOnVersion", ["nativeMessage"] = "Provider is unavailable for this CPU." } };
        var body = Host.PlcToolContract.Map("GetPlcBlockFingerprints", response, false, true).StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal(CapabilityMessage(release, "GetPlcBlockFingerprints", "Provider is unavailable for this CPU."), (string?)body["error"]?["message"]);
    }

    [Theory]
    [InlineData(true, "Success", 0, true, true)]
    [InlineData(false, "Failure", 1, true, true)]
    [InlineData(false, "Success", 0, true, true)]
    [InlineData(false, "Failure", 0, true, false)]
    [InlineData(false, "PartialSuccess", 0, true, true)]
    [InlineData(false, "unknown", 0, true, true)]
    [InlineData(false, "Failure", 0, false, true)]
    [InlineData(false, "failure", 0, true, true)]
    public void Absent_import_target_cannot_erase_native_import_evidence(bool success, string state, int identities, bool identitiesRead, bool unknown)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "import-evidence");
        var request = new PlcDocumentImportRequest {
            Release = "21", Project = "C:/Fixture.ap21", Software = "PLC", Directory = directory,
            Name = "Demo", ProcessId = 1, TargetIdentity = "target"
        };
        byte[] code = System.Text.Encoding.ASCII.GetBytes("DATA_BLOCK \"Demo\" VAR Flag : Bool; END_VAR END_DATA_BLOCK");
        PlcDocumentImportResult Run() => PlcDocumentImportPolicy.Run(request,
            () => new[] { Path.Combine(directory, "Demo.s7dcl") }, _ => new MemoryStream(code),
            () => Array.Empty<string>(), _ => false, () => { },
            () => new PlcDocumentImportNative {
                State = state, Success = success, ExistsVerified = false, IdentitiesRead = identitiesRead,
                Identities = Enumerable.Range(0, identities).Select(i => "Other" + i).ToArray()
            }, _ => false);
        request.ExpectedHash = Run().PlanHash;
        request.ExpectedProject = request.Project;
        request.DryRun = false;
        request.Confirm = true;
        var result = Run();
        Assert.Equal(unknown ? "unknown" : "failed", result.Status);
        Assert.Equal(unknown, result.MayHaveChanged);
        Assert.Equal(unknown, result.OutcomeUnknown);
        var arguments = new JsonObject {
            ["softwarePath"] = request.Software, ["groupPath"] = request.Group, ["importPath"] = request.Directory,
            ["fileNameWithoutExtension"] = request.Name, ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = request.Project, ["expectedPlanHash"] = request.ExpectedHash
        };
        DocumentImportContract.Validate(JsonSerializer.SerializeToNode(result), arguments);
    }

    [Theory]
    [InlineData("INVALID_ARGUMENT")]
    [InlineData("UNSUPPORTED_CAPABILITY")]
    public void Captured_worker_failure_preserves_its_classification(string code)
    {
        using var scope = EngineHostParity.EnterScope();
        var response = new Host.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false,
            ["status"] = "ReadOrWriteFailed", ["v4FailureCode"] = code, ["nativeMessage"] = "Captured refusal."
        } };
        var body = Host.UnifiedHmiContract.Map("EnsureUnifiedHmiScreen", response, true, true).StructuredContent!;
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
    }

    [Fact]
    public void Rejected_exchange_write_reports_admission_stage()
    {
        using var scope = EngineHostParity.EnterScope();
        var body = Host.PlcExchangeContract.Run("ImportPlcBlocksDocuments",
            () => throw new AdapterPreconditionException("Refused before native write.", "fixture", null), true, true).StructuredContent!;
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("admission", (string?)body["data"]?["executionEvidence"]?["stage"]);
        Assert.Equal(0, (int?)body["data"]?["executionEvidence"]?["issued"]);
        Assert.Equal(0, (int?)body["data"]?["executionEvidence"]?["confirmed"]);
    }

    [Fact]
    public void Library_detail_messages_survive_and_unchanged_import_is_failed()
    {
        using var scope = EngineHostParity.EnterScope();
        var reply = new HardwareAddressingReply { Meta = new() {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false, ["status"] = "ReadOrWriteFailed",
            ["nativeMessage"] = "Library import refused.", ["error"] = "private stack",
            ["messageData"] = new Dictionary<string, object?> { ["text"] = "Native reason", ["detailText"] =
                "Action is not supported within ExclusiveAccess." },
            ["detailMessageData"] = new[] { new Dictionary<string, object?> { ["text"] = "Type unchanged", ["detailText"] = "No version created." } } } };
        var json = JsonNode.Parse(WorkerJson.Serialize(new { Step = reply, Probe = (object?)null, Import = (object?)null,
            MayHaveChanged = false, OutcomeUnknown = false }))!;
         WorkerProtocol.ValidateExchangeResult("libraries.ExecuteLibrary", new(), json);
        var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json["Step"]!.ToJsonString())!;
        var body = Host.LibraryToolContract.Map("ImportLibraryTypeDocuments", response, true).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Contains("Action is not supported within ExclusiveAccess.", body.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("No version created.", body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private stack", body.ToJsonString(), StringComparison.Ordinal);

        Assert.NotEqual("unknown", (string?)body["meta"]!["outcome"]);
    }

    [Fact]
    public void Exclusive_access_refusal_in_plc_organisation_is_rejected_before_operation()
    {
        using var release = Host.HardwareContract.UseRelease("21");

        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcSoftwareReply {
                Failure = new PlcSoftwareFailure { Code = nameof(AdapterPreconditionException), Message = "TIA Portal did not grant exclusive access." },
                MayHaveChanged = false, OutcomeUnknown = false }));
            WorkerProtocol.ValidateExchangeResult(operation, arguments, json);
            return json;
        }, () => true, () => "fixture");
        var body = Host.PlcToolContract.Run("CreatePlcTypeGroup", true, true,
            () => service.CreatePlcTypeGroup("PLC", "Group", false)).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);

    }

    [Theory]
    [InlineData("network", false), InlineData("plc", false), InlineData("engineering", true), InlineData("security", true)]
    public void Definite_step_failure_survives_worker_serialization(string contract, bool refusal)
    {
        using var scope = EngineHostParity.EnterScope();
        var reply = new HardwareAddressingReply { Message = "Native setter refused the value.", Meta = new() {
            ["tool"] = "SetDeviceAddress", ["success"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false,
            ["status"] = refusal ? "PreconditionFailed" : "ReadOrWriteFailed",
            ["precondition"] = "password-policy", ["refusal"] = EngineeringCredentialRules.PasswordPolicyRefusal,
            ["nativeMessage"] = "Native setter refused the value." } };
        var json = JsonNode.Parse(WorkerJson.Serialize(reply))!;

        WorkerProtocol.ValidateExchangeResult("hardware-addressing.UpdateHardwareAddress", new(), json);
        var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
        var body = (contract switch {
            "network" => Host.HardwareToolContract.MapResult("SetDeviceItemChannel", response, false, true),
            "plc" => Host.PlcToolContract.Map("ManagePlcBlockProtection", response, true, true),
            "engineering" => Host.EngineeringToolContract.Map("ManagePlcBlockProtection", response, true),
            _ => Host.SecurityToolContract.MapResult("ManagePlcProtection", response, false, true)
        }).StructuredContent!;
        Assert.Equal(refusal ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.NotEqual("unknown", (string?)body["meta"]!["outcome"]);

    }
    [Fact]
    public void Plc_group_validation_is_rejected_before_dispatch()
    {
        var service = new Services.PlcOrganisationPortService(
            (_, _) => throw new System.InvalidOperationException("No worker call expected."), () => true, () => "fixture");
        var body = Host.PlcToolContract.Run("CreatePlcTypeGroup", true, true,
            () => service.CreatePlcTypeGroup("PLC", "", false)).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_empty_PLC_group_validation_precedes_project_lookup(bool hasProject)
    {
        foreach (string release in ReleaseEnds.OldestAndNewest(
            new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }, _ => "outcome", release => release))
        {
            using var selected = Host.HardwareContract.UseRelease(release);
            int projectChecks = 0, workerCalls = 0;
            var service = new Services.PlcOrganisationPortService(
                (_, _) => { workerCalls++; throw new InvalidOperationException("No worker call expected."); },
                () => { projectChecks++; return hasProject; }, () => "fixture");
            var body = Host.PlcToolContract.Run("DeleteEmptyPlcBlockGroup", true, true,
                () => service.DeleteEmptyPlcBlockGroup("PLC", "Program blocks", false)).StructuredContent!;
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal((0, 0), (projectChecks, workerCalls));
        }
    }

    [Theory]
    [InlineData("blocks", "Program blocks")]
    [InlineData("types", "PLC data types")]
    [InlineData("tags", "")]
    [InlineData("technology", "")]
    [InlineData("watchTables", "")]
    [InlineData("externalSources", "")]
    public void Every_PLC_group_action_validates_its_family_path_before_project_lookup(string family, string path)
    {
        using var selected = Host.HardwareContract.UseRelease("21");
        foreach (string action in new[] { "create", "rename", "deleteEmpty" })
        {
            var service = new Services.PlcOrganisationPortService(
                (_, _) => throw new InvalidOperationException("No worker call expected."),
                () => throw new InvalidOperationException("No project lookup expected."), () => "fixture");
            var body = Host.PlcToolContract.Run("ManagePlcUserGroup", true, true,
                () => service.ManagePlcUserGroup("PLC", family, path, action, "Next", false)).StructuredContent!;
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            Assert.Equal("/groupPath", (string?)body["error"]?["details"]?["parameter"]);
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string contract in new[] { "hardware", "network", "plc", "hmi", "engineering", "drive", "library", "security", "optional" })
            foreach (string kind in new[] { "refusal", "unchanged", "uncertain", "refusal-uncertain" })
                yield return new object[] { contract, kind };
    }

    [Theory, MemberData(nameof(Cases))]
    public void Definite_worker_outcomes_do_not_require_a_reset(string contract, string kind)
    {
        bool uncertain = kind == "uncertain" || kind == "refusal-uncertain";
        var evidence = new JsonObject {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false,
            ["mayHaveChanged"] = uncertain,
            ["status"] = kind.StartsWith("refusal", System.StringComparison.Ordinal) ? "PreconditionFailed" : "ReadOrWriteFailed",
            ["nativeMessage"] = "Native setter refused the value.",
            ["precondition"] = "exclusive-access", ["refusal"] = "TIA Portal did not grant exclusive access." };
        var response = new Host.ResponseMessage { Message = "Native setter refused the value.", Meta = evidence };
        CallToolResult result = contract switch {
            "hardware" => Host.HardwareContract.Map("SetCpuAttributes", response, true, true),
            "network" => Host.HardwareToolContract.MapResult("SetDeviceItemChannel", response, false, true),
            "plc" => Host.PlcToolContract.Map("CreatePlcTypeGroup", response, true, true),
            "hmi" => Host.UnifiedHmiContract.Map("ManageUnifiedScreenLayout", response, true, true),
            "engineering" => Host.EngineeringToolContract.Map("ManagePlcBlockProtection", response, true),
            "drive" => Host.DriveToolContract.Map("SetDriveParameters", response, true, true),
            "library" => Host.LibraryToolContract.Map("ImportLibraryTypeDocuments", response, true),
            "security" => Host.SecurityToolContract.MapResult("ManagePlcProtection", response, false, true),
            _ => Host.OptionalPackageContract.Map("ManageCfcChartProtection", response, true) };
        var body = result.StructuredContent!;
        Assert.Equal(kind == "refusal" ? "PRECONDITION_FAILED" : kind == "unchanged" ? "NATIVE_OPERATION_FAILED" : "OUTCOME_UNKNOWN",
            (string?)body["error"]?["code"]);
        Assert.Equal(kind == "refusal" ? "rejected-before-operation" : kind == "unchanged" ? "failed" : "unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal(uncertain, (string?)body["meta"]?["outcome"] == "unknown");
        if (kind == "refusal") Assert.Contains("did not grant exclusive access", (string?)body["error"]?["message"]);
        if (kind == "unchanged") Assert.Equal("Native setter refused the value.", (string?)body["error"]?["details"]?["nativeMessage"]);
    }
    [Theory, InlineData(false), InlineData(true)]
    public void Export_failure_without_project_change_is_failed(bool fileExists)
    {
        string path = Path.Combine(Path.GetTempPath(), "alarm-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var result = Body(Host.EngineeringToolContract.Export("ExportAlarmInstanceTexts", path, () => new Host.ResponseMessage {
            Meta = new JsonObject { ["operationSuccess"] = false, ["apiCallSuccess"] = false,
                ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = fileExists, ["nativeMessage"] = "Native export refused." } }));
        Assert.Equal("failed", (string?)result["meta"]?["outcome"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal(fileExists, (bool?)result["data"]?["evidence"]?["mayHaveWrittenFiles"]);
    }
    [Theory]
    [InlineData("16"), InlineData("21")]
    public void Export_failure_keeps_a_message_only_reason(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string path = Path.Combine(Path.GetTempPath(), "alarm-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var body = Body(Host.EngineeringToolContract.Export("ExportAlarmTextLists", path, () => new Host.ResponseMessage {
            Message = "Export failed: Error when calling method 'ExportToXlsx' of type 'Siemens.Engineering.SW.Alarm.PlcAlarmTextListProvider'.",
            Meta = new JsonObject { ["success"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false }
        }));
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Contains("ExportToXlsx", (string?)body["error"]?["details"]?["nativeMessage"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("16"), InlineData("21")]
    public void Missing_text_list_is_a_documented_refusal(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string path = Path.Combine(Path.GetTempPath(), "alarm-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var body = Body(Host.EngineeringToolContract.Export("ExportAlarmTextLists", path, () => new Host.ResponseMessage {
            Message = "Export failed: There is no text list on this controller.",
            Meta = new JsonObject { ["success"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false,
                ["status"] = "PreconditionFailed", ["precondition"] = "plc-text-lists",
                ["refusal"] = "This PLC has no alarm text list; create one in TIA or with ManagePlcAlarmTextList createFromMasterCopy." }
        }));
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("plc-text-lists", (string?)body["error"]?["details"]?["condition"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
    }

    [Theory, InlineData(true, true), InlineData(true, false), InlineData(false, true)]
    public void Missing_text_list_after_export_wrote_a_file_has_unknown_outcome(bool fileRemains, bool workerSawFile)
    {
        using var scope = EngineHostParity.EnterScope();
        string path = Path.Combine(Path.GetTempPath(), "alarm-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var body = Body(Host.EngineeringToolContract.Export("ExportAlarmTextLists", path, () =>
            {
                File.WriteAllBytes(path, [0x50, 0x4b, 0x03, 0x04]);
                if (!fileRemains) File.Delete(path);
                return new Host.ResponseMessage {
                    Meta = new JsonObject { ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false,
                        ["mayHaveWrittenFiles"] = workerSawFile, ["nativeMessage"] = "There is no text list on this controller.",
                        ["status"] = "PreconditionFailed", ["precondition"] = "plc-text-lists", ["refusal"] = "This PLC has no alarm text list." }
                };
            }));
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal("export-file", (string?)body["error"]?["details"]?["stage"]);
            Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
            Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
            Assert.True((bool?)body["data"]?["mayHaveChanged"]);
        }
        finally { File.Delete(path); }
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Download_to_folder_preview_without_native_state_succeeds(bool preview)
    {
        var result = Host.RuntimeToolContract.Map("DownloadPlcToFolder", new Host.ResponseMessage {
            Meta = new JsonObject { ["operationSuccess"] = true, ["dryRun"] = preview, ["mayHaveChanged"] = !preview }
        }, preview, true).StructuredContent!;
        Assert.Equal(preview ? "succeeded" : "unknown", (string?)result["meta"]?["outcome"]);
    }
    [Fact]
    public void Force_table_list_is_success()
    {
        var service = new Services.PlcOrganisationPortService((_, _) =>
            new JsonObject { ["Names"] = new JsonArray("Forces") }, () => true, () => "fixture");
        var body = Body(new Host.PlcWatchTools(service).GetPlcForceTablesV4("PLC"));
        Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
        Assert.Equal("Forces", (string?)body["data"]?["items"]?[0]);
    }
    [Theory, InlineData(false), InlineData(true)]
    public void Validation_refusal_is_invalid_argument(bool theme)
    {
        var service = new ValidationService();
        var request = new TiaMcp.Adapters.Contracts.UnifiedHmiRequest {
            Operation = theme ? "ApplyUnifiedHmiScreenDesign" : "EnsureUnifiedHmiTag",
            DesignJson = "{\"screen\":{},\"items\":[],\"theme\":{\"name\":\"Example\"}}", DryRun = false };
        var body = Body(Host.UnifiedHmiContract.Run(request.Operation, true, true, () => service.Validate(request)));
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
    }
    private static JsonObject Body(CallToolResult result) => result.StructuredContent!.AsObject();
    private sealed class ValidationService : Services.UnifiedHmiPortService
    {
        internal ValidationService() : base((_, _) => throw new InvalidOperationException("No worker dispatch expected."), () => "fixture") { }
        internal object Validate(UnifiedHmiRequest request) => Invoke<object>(request);
    }

    public static IEnumerable<object[]> MissingReleaseFixtures => ReleaseEnds.OldestAndNewest(
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from missing in new[] { "TMP_EXPORT/_verify", "docs/hmi_templates", "docs/hmi_template_mappings/tia-hmi-template-plc-mapping.release.json" }
        select new object[] { release, missing }, row => (string)row[1], row => (string)row[0]);

    [Theory, MemberData(nameof(MissingReleaseFixtures))]
    public void Missing_release_fixtures_name_the_relative_folder(string release, string missing)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string workspace = Path.Combine(Path.GetTempPath(), "vmfix10b-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            if (missing != "TMP_EXPORT/_verify") Directory.CreateDirectory(Path.Combine(workspace, "TMP_EXPORT", "_verify"));
            if (missing.EndsWith(".json", StringComparison.Ordinal)) Directory.CreateDirectory(Path.Combine(workspace, "docs", "hmi_templates"));
            string reports = Path.Combine(workspace, "reports");
            var body = Body(Host.OfflineContracts.Run("RunOfflineReleaseValidationSuite",
                () => new Host.ResponseJsonReport { Data = Host.OfflineReleaseValidationSuite.Run(workspace, reports) }, true));
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Contains(missing, (string?)body["error"]?["message"], StringComparison.Ordinal);
            Assert.DoesNotContain("<path>", (string?)body["error"]?["message"], StringComparison.Ordinal);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.False(Directory.Exists(reports));
        }
        finally { Directory.Delete(workspace, true); }
    }

    [Theory]
    [InlineData("hmi")]
    [InlineData("security")]
    [InlineData("hardware")]
    public void Attached_portal_without_a_project_maps_to_project_not_bound(string family)
    {
        using var scope = EngineHostParity.EnterScope();
        var response = JsonNode.Parse("{\"message\":\"Project is null\",\"meta\":{\"timestamp\":\"2026-10-14T12:00:00\",\"tool\":\"GetHmiProgramInfo\","
            + "\"success\":false,\"error\":\"Project is null\",\"status\":\"InvalidState\",\"operationSuccess\":false}}")!.AsObject();
        var legacy = new Host.ResponseMessage { Message = (string?)response["message"], Meta = response["meta"]!.DeepClone().AsObject() };
        var result = family switch {
            "security" => Host.SecurityToolContract.MapResult("ManageSyslogServers", legacy, false, true),
            "hardware" => Host.HardwareContract.Map("ManageIoSystem", legacy, true, true),
            _ => Host.HmiInspectionContract.Map("GetHmiProgramInfo", response, false, true)
        };
        var body = Body(result);
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Fact]
    public void Archive_failure_with_a_partial_native_file_is_unknown()
    {
        using var scope = EngineHostParity.EnterScope();
        var response = JsonNode.Parse("{\"message\":\"Native archive operation completed; inspect success and validation. Retrieval is not tested.\","
            + "\"meta\":{\"timestamp\":\"2026-10-14T12:00:00\",\"tool\":\"ArchiveSavedProject\",\"success\":false,"
            + "\"mayHaveChanged\":false,\"dryRun\":false,\"path\":\"D:\\\\Code\\\\TIA_wt\\\\VMFIX-3-PORT\\\\tests\\\\A" +
                "dapters\\\\TiaMcp.Adapters.Contracts.Tests\\\\bin\\\\Debug\\\\net10.0\\\\archive-1f219e0f03cf4c3a9d8ceb" +
                "67684237bc\\\\project.zap21\","
            + "\"kind\":\"NativeProjectArchive\",\"mode\":\"Compressed\",\"retrievalTested\":false,\"projectSavedByTool\":false,"
            + "\"mayHaveWrittenFiles\":true,\"capability\":\"supported\",\"targetReplaced\":false,\"recoveryRequired\":false,"
            + "\"validation\":\"Not " +
                "performed\",\"format\":\"zap21\",\"temporaryPath\":\"D:\\\\Code\\\\TIA_wt\\\\VMFIX-3-PORT\\\\tests\\\\A" +
                "dapters\\\\TiaMcp.Adapters.Contracts.Tests\\\\bin\\\\Debug\\\\net10.0\\\\archive-1f219e0f03cf4c3a9d8ceb" +
                "67684237bc\\\\.tia-export-1e18bbfc88604e93a4e396db5336c7fb.zap21\","
            + "\"error\":\"Exception has been thrown by the target of an invocation.\",\"partialArtifactExists\":true,"
            + "\"operationSuccess\":false}}")!.AsObject();
        var body = Body(Host.HmiInspectionContract.Map("ArchiveSavedProject", response, true, true));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
    }
}
