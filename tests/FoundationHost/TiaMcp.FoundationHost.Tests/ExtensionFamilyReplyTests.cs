extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class ExtensionFamilyReplyTests
{
    [Theory]
    [InlineData(nameof(NotSupportedException), false)]
    [InlineData(nameof(NotSupportedException), true)]
    [InlineData(nameof(ArgumentException), false)]
    [InlineData(nameof(ArgumentException), true)]
    [InlineData(nameof(InvalidOperationException), false)]
    [InlineData(nameof(InvalidOperationException), true)]
    public void Plc_B5_failures_after_a_possible_change_keep_the_unknown_outcome(string code, bool writes)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var state = new WorkerOutcomeState();
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply {
                Failure = new PlcSoftwareFailure { Code = code, Message = "Native result unavailable after a change." },
                MayHaveChanged = true, RequiresSessionReset = true }));
            state.AcceptResult(operation, args, json);
            return json;
        }, () => "fixture");
        var body = Host.EngineeringToolContract.Run("GetPlcOpcUaConfiguration", writes,
            () => service.GetOpcUaConfig("PLC")).StructuredContent!;
        Assert.True(state.Poisoned);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        Assert.True((bool)body["meta"]!["requiresSessionReset"]!);
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
        new WorkerOutcomeState().AcceptResult("optional-products.ExecuteOptionalProduct", new(), json);
        var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
        var body = Host.OptionalPackageContract.Map("ManageSinumerikArchive", response, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        Assert.True((bool)body["meta"]!["requiresSessionReset"]!);
    }

    [Fact]
    public void An_uncertain_extension_write_still_requires_a_session_reset()
    {
        var reply = new HardwareAddressingReply { Message = "Write readback unavailable.", MayHaveChanged = true,
            RequiresSessionReset = true, Meta = new() { ["operationSuccess"] = false, ["mayHaveChanged"] = true,
                ["requiresSessionReset"] = true } };
        var json = JsonNode.Parse(WorkerJson.Serialize(reply))!;
        var state = new WorkerOutcomeState();
        state.AcceptResult("startdrive.ExecuteStartdrive", new(), json);
        Assert.True(state.Poisoned);
        var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
        var body = Host.DriveToolContract.Map("ManageDriveFunctions", response, true, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        Assert.True((bool)body["meta"]!["requiresSessionReset"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plc_B5_message_only_failure_is_a_native_error_without_session_reset(bool legacyMeta)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        const string message = "Export failed: Synthetic native export refusal.";
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply { Message = message,
                Meta = legacyMeta ? new() { ["success"] = false } : null }));
            new WorkerOutcomeState().AcceptResult(operation, args, json);
            return json;
        }, () => "fixture");
        var result = Host.EngineeringToolContract.Run("ExportAlarmClasses", true, () => service.ExportAlarmClasses("PLC", "fixture.dat"));
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains(message, result.StructuredContent["error"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.False((bool)result.StructuredContent["meta"]!["requiresSessionReset"]!);
    }

    [Theory]
    [InlineData(nameof(NotSupportedException), "UNSUPPORTED_CAPABILITY")]
    [InlineData(nameof(ArgumentException), "NATIVE_OPERATION_FAILED")]
    [InlineData(nameof(InvalidOperationException), "NATIVE_OPERATION_FAILED")]
    public void Plc_B5_typed_failures_cross_the_port_without_faulting_the_session(string code, string expected)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        const string message = "Synthetic OPC UA native refusal.";
        var state = new WorkerOutcomeState();
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcB5PortService((operation, args) => {
            var json = JsonNode.Parse(WorkerJson.Serialize(new PlcB5Reply {
                Failure = new PlcSoftwareFailure { Code = code, Message = message } }));
            state.AcceptResult(operation, args, json);
            return json;
        }, () => "fixture");
        var result = Host.EngineeringToolContract.Run("GetPlcOpcUaConfiguration", false, () => service.GetOpcUaConfig("PLC"));
        Assert.Equal(expected, (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains(message, result.StructuredContent["error"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.False((bool)result.StructuredContent["meta"]!["requiresSessionReset"]!);
        Assert.False(state.Poisoned);
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
        Assert.False((bool)result["meta"]!["requiresSessionReset"]!);
        Assert.DoesNotContain("at Fixture.Native", result.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Projectless_probes_preserve_the_existing_response_shape(bool whitelist)
    {
        var service = new enginehost::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((operation, _) =>
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
        var service = new enginehost::TiaMcpServer.Siemens.Services.HardwareNetworkPortService((operation, _) =>
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
        var state = new WorkerOutcomeState();
        state.AcceptResult(operation, new(), json);
        state.RequireUsable();
        Assert.False(state.Poisoned);
        Assert.Null(json["Meta"]!["success"]);
        if (operation.EndsWith("ExecutePlcB5", StringComparison.Ordinal))
        {
            json["Blocks"]![0]!["Name"] = 42;
            Assert.Throws<IOException>(() => HardwareFamilyWire.Validate(operation, json));
            json["Blocks"]![0]!["Name"] = "Main";
        }
        json["RequiresSessionReset"] = "false";
        var malformed = Assert.Throws<IOException>(() => state.AcceptResult(operation, new(), json));
        state.Failed(true, malformed);
        Assert.True(state.Poisoned);
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
            new WorkerOutcomeState().AcceptResult(operation, new(), json);
            var response = JsonSerializer.Deserialize<Host.ResponseMessage>(json.ToJsonString())!;
            var result = contract switch {
                "library" => Host.LibraryToolContract.Map(tool, response, writes),
                "engineering" => Host.EngineeringToolContract.Map(tool, response, writes),
                "drive" => Host.DriveToolContract.Map(tool, response, writes, true),
                "security" => Host.SecurityToolContract.MapResult(tool, response, !writes, true),
                _ => Host.OptionalPackageContract.Map(tool, response, writes) };
            var body = result.StructuredContent!;
            Assert.Equal(verdict == "normal", (bool)body["ok"]!);
            Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
            if (verdict == "normal") continue;
            Assert.Equal(verdict == "unsupported" ? "UNSUPPORTED_CAPABILITY" : "NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
            Assert.Contains(response.Message, body["error"]!.ToJsonString(), StringComparison.Ordinal);
        }
    }
}
