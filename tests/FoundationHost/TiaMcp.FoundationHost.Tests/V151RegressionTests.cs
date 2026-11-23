extern alias adaptertests;
extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaOpenness.Shared;
using Xunit;

public sealed class V151RegressionTests
{
    private sealed class Worker(Func<string, JsonObject, JsonNode?> handle) : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token) => Task.FromResult(
            operation == "ReadState" ? new JsonObject { ["ProjectFile"] = "C:/Fixture/Project.ap15_1", ["ProcessId"] = 42, ["IsAttached"] = true }
            : handle(operation, arguments));
        public void Dispose() { }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Confirmed_ReadAccess_apply_passes_the_text_preview_and_reaches_the_worker(bool explicitDefaults)
    {
        const string tool = "ManagePlcProtection";
        const string plan = "set PlcAccessLevelProvider.PlcProtectionAccessLevel = ReadAccess.";
        var sequence = new List<string>();
        using var worker = new Worker((operation, input) => {
            Assert.Equal("project-security.ExecuteHardwareSecurity", operation);
            var request = input["request"]!;
            bool preview = (bool)input["dryRun"]!;
            sequence.Add(preview ? "preview" : "apply");
            Assert.Equal(preview, (bool?)request["DryRun"]);
            Assert.Equal("setAccessLevel", (string?)request["NormalizedAction"]);
            Assert.Equal("ReadAccess", (string?)request["NormalizedLevel"]);
            Assert.True((bool?)request["ConfirmChange"]);
            Assert.Equal("MCP_RC_PLC", (string?)request["DevicePath"]?[0]);
            Assert.Empty(request["ItemPath"]!.AsArray());
            Assert.Empty(request["Faults"]!.AsObject());
            if (!preview)
            {
                Assert.True((bool?)input["confirm"]);
                Assert.Equal("C:/Fixture/Project.ap15_1", (string?)input["expectedProjectFile"]);
            }
            var meta = new JsonObject { ["dryRun"] = preview, ["action"] = "setAccessLevel", ["cpu"] = "MCP_RC_PLC",
                ["operationSuccess"] = true, ["mayHaveChanged"] = !preview, ["before"] = new JsonObject { ["accessLevel"] = "FullAccess" } };
            if (preview) meta["plan"] = plan;
            else meta["after"] = new JsonObject { ["accessLevel"] = "ReadAccess" };
            return new JsonObject { ["Message"] = preview ? "Preview: " + plan : "ReadAccess applied.", ["Meta"] = meta, ["MayHaveChanged"] = !preview };
        });
        var raw = PortedToolContract.Create(worker, "15.1").Single(t => t.ProtocolTool.Name == tool);
        var approved = new FoundationV4Tool(raw, "15.1", () => new ApprovalSettings(true, 5), (pending, settings, _) => {
            Assert.True(settings.Enabled);
            sequence.Add("approval");
            return Task.FromResult(new ApprovalOutcome(pending, false, null));
        });
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1", worker: worker, replacementTool: approved);
        using var session = fixture.EnterSession();
        var args = JsonNode.Parse("""
            {"devicePath":["MCP_RC_PLC"],"action":"setAccessLevel","accessLevel":"ReadAccess","dryRun":false,"confirmChange":true}
            """)!.AsObject();
        if (explicitDefaults) { args["itemPath"] = new JsonArray(); args["password"] = ""; args["newPassword"] = ""; }
        var result = (await fixture.Call(tool, args)).StructuredContent!;
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal(new[] { "preview", "approval", "apply" }, sequence);
        Assert.Equal("ReadAccess", (string?)result["data"]?["after"]?["accessLevel"]);
        args["accessLevel"] = "Invalid";
        var refusal = (await fixture.Call(tool, args)).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)refusal["error"]?["code"]);
        Assert.Equal("/accessLevel", (string?)refusal["error"]?["details"]?["parameter"]);
        Assert.Equal(3, sequence.Count);
    }

    [Fact]
    public void Protection_refusals_identify_the_missing_input_before_dispatch()
    {
        using var selected = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease("15.1");
        var hardware = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareSecurityPortService(
            (_, _) => throw new InvalidOperationException("Unexpected worker dispatch."), () => true, () => "C:/Fixture/Project.ap15_1");
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareServicesTools(hardware);
        string[] device = { "MCP_RC_PLC" };
        var credential = tools.ManagePlcProtection(device, action: "setAccessPassword", accessLevel: "FullAccess").StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)credential["error"]?["code"]);
        Assert.Equal("/password", (string?)credential["error"]?["details"]?["parameter"]);
        var confirmation = tools.ManagePlcProtection(device, action: "setAccessLevel", accessLevel: "ReadAccess", dryRun: false).StructuredContent!;
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)confirmation["error"]?["code"]);
        Assert.Contains("confirmChange", (string?)confirmation["error"]?["message"]);
    }

    [Theory, MemberData(nameof(VerifiedImportOutcomeTests.Releases), MemberType = typeof(VerifiedImportOutcomeTests))]
    public async Task OpcUa_file_export_refusal_keeps_the_session_settled(string release)
    {
        const string message = "Error when calling method 'Export' of type 'Siemens.Engineering.HW.Utilities.OpcUaExportProvider'.";
        int exports = 0;
        var native = adaptertests::TiaMcp.Adapters.Contracts.Tests.LowBetaRegressionTests.ExportFixture(release);
        native.Project.HwUtilities.Add(new adaptertests::Siemens.Engineering.HW.Utilities.OpcUaExportProvider {
            Identifier = "OPCUAExportProvider", OnExport = (_, target) => {
                exports++;
                File.WriteAllText(target.FullName, "<UANodeSet");
                var failure = new adaptertests::Siemens.Engineering.EngineeringException(message);
                failure.MessageData.Text = message;
                failure.MessageData.DetailText = "MCP_RC_PLC must be successfully compiled.";
                throw failure;
            }
        });
        var state = new WorkerSessionOutcomeState();
        using var worker = new Worker((operation, input) => {
            Assert.Equal("hardware-devices.HardwareManageHardwareUtilities", operation);
            Assert.Equal("exportOpcUa", (string?)input["action"]);
            bool preview = (bool)input["dryRun"]!;
            var reply = native.Engine.HardwareManageHardwareUtilities((string)input["action"]!,
                devicePathJson: input["devicePathJson"]!.Deserialize<string[]>()!, itemPathJson: input["itemPathJson"]!.Deserialize<string[]>()!,
                filePath: (string)input["filePath"]!, dryRun: preview);
            return JsonNode.Parse(WorkerOperationResponse.Complete(reply, preview, state).ResultJson);
        });
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker,
            approvalWait: (card, _, _) => Task.FromResult(new ApprovalOutcome(card, false, null)));
        using var session = fixture.EnterSession();
        string filePath = Path.Combine(fixture.Root, "RC-15.1-A-opcua.xml");
        var result = (await fixture.Call("ManageHardwareUtilities", new { action = "exportOpcUa", devicePath = new[] { "MCP_RC_PLC" },
            itemPath = new[] { "MCP_RC_PLC" }, filePath, dryRun = false })).StructuredContent!;
        Assert.Equal(1, exports);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result["error"]?["code"]);
        Assert.Equal("failed", (string?)result["meta"]?["outcome"]);
        Assert.Contains("must be successfully compiled", (string?)result["error"]?["message"]);
        Assert.Contains("remove any partial file before retrying", (string?)result["error"]?["message"]);
        Assert.Equal(filePath, (string?)result["data"]?["filePath"]);
        Assert.True((bool?)result["data"]?["mayHaveWrittenFiles"]);
        Assert.True((bool?)result["error"]?["details"]?["evidence"]?["mayHaveWrittenFiles"]);
        Assert.Equal("<UANodeSet", File.ReadAllText(filePath));
        Assert.False(state.Uncertain);
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot());
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Psc_export_failure_offers_cleanup_only_after_export_started(bool started)
    {
        string filePath = Path.GetFullPath("card.psc");
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareServicesPortService((_, input) => {
            var reply = adaptertests::TiaMcp.Adapters.Contracts.Tests.MutationEvidenceTests.RunStep("15.1", "ManageHardwareUtilities", meta => {
                meta["action"] = (string?)input["action"];
                meta["filePath"] = (string?)input["filePath"];
                meta["mayHaveWrittenFiles"] = started;
                throw new IOException(started ? "PSC export interrupted." : "Export refuses to overwrite an existing file.");
            });
            return JsonNode.Parse(WorkerJson.Serialize(reply));
        }, () => true, () => "C:/Fixture/Project.ap15_1");
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareServicesPortTools(service);
        var body = tools.ManageHardwareUtilities("exportCardReaderPsc", devicePath: new[] { "PLC" }, filePath: filePath, dryRun: false).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal(filePath, (string?)body["data"]?["filePath"]);
        Assert.Equal(started, (bool?)body["error"]?["details"]?["evidence"]?["mayHaveWrittenFiles"]);
        Assert.Equal(started, ((string)body["error"]!["message"]!).Contains("remove any partial file before retrying", StringComparison.Ordinal));
    }

    [Fact]
    public void Tag_table_preview_refuses_a_missing_HMI_without_import_or_plan_hash()
    {
        using var worker = new WritePreviewCoverageTests.PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("15.1", worker,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        using var session = pipeline.EnterSession();
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(file, """<Document><Engineering version="V15.1"/><Hmi.Tag.TagTable ID="0"><AttributeList><Name>Main</Name></AttributeList></Hmi.Tag.TagTable></Document>""");
            int checks = 0;
            var service = new toolpipeline::TiaMcpServer.Siemens.Services.HmiExchangeService((operation, input) => {
                checks++;
                Assert.Equal("hmi-generic.ExecuteHmi", operation);
                Assert.True((bool?)input["dryRun"]);
                var request = input["request"]!;
                Assert.True((bool?)request["DryRun"]);
                Assert.Equal("ImportHmiTagTable", (string?)request["Operation"]);
                Assert.Equal("HMI_RT_1", (string?)request["SoftwarePath"]);
                Assert.Null(input["confirm"]);
                return new JsonObject { ["MayHaveChanged"] = false, ["OutcomeUnknown"] = false, ["Failure"] = new JsonObject {
                    ["Code"] = "NotFound", ["Message"] = "HMI software not found: HMI_RT_1" } };
            }, () => "C:/Fixture/Project.ap15_1");
            var tool = new toolpipeline::TiaMcpServer.ModelContextProtocol.HmiExchangeTools(service);
            var result = tool.ImportHmiTagTableV4("HMI_RT_1", "", file).StructuredContent!;
            Assert.Equal(1, checks);
            Assert.Equal("NOT_FOUND", (string?)result["error"]?["code"]);
            Assert.Null(result["data"]?["planHash"]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Network_plan_and_transfer_area_preview_refuse_the_recorded_fields()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var plan = await fixture.Call("PlanHardwareNetworkConfiguration", new { plan = new { operations = new[] { new {
            type = "EnsureSubnet", subnetName = "MCP_RC_PN", subnetType = "Modbus", anchorDeviceItemPath = "MCP_RC_PLC" } } } });
        Assert.Equal("INVALID_ARGUMENT", (string?)plan.StructuredContent?["error"]?["code"]);
        Assert.Equal("/plan/operations/0/subnetType", (string?)plan.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Contains("subnetType", (string?)plan.StructuredContent?["error"]?["message"]);
        var transfer = await fixture.Call("ManageTransferArea", new { devicePath = new[] { "MCP_RC_PLC2" },
            itemPath = new[] { "MCP_RC_PLC2", "PROFINET 接口_1" }, action = "create", kind = "standard", name = "MCP_RC_TA2", type = "IN", dryRun = true });
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)transfer.StructuredContent?["error"]?["code"]);
        Assert.Contains("V15.1", (string?)transfer.StructuredContent?["error"]?["message"]);
        Assert.Contains("CD", (string?)transfer.StructuredContent?["error"]?["message"]);
    }

    [Theory, InlineData("ValidateAutomationContext", "NOT_FOUND"), InlineData("ListDriveObjects", "UNSUPPORTED_CAPABILITY")]
    public void Later_read_keeps_caught_admission_evidence_for_the_domain_result(string tool, string code)
    {
        var failure = code == "NOT_FOUND" ? new WorkerOperationException("PLC software not found.", -32603, "rejected-before-operation",
            """{"exceptionType":"AdapterPreconditionException","condition":"not-found","parameter":"expectedPlcSoftwarePath"}""")
            : new WorkerOperationException("The service could not read this item.", -32603, "read-failed",
                """{"exceptionType":"NotSupportedException","unsupportedBy":"target"}""");
        using var worker = new Worker((operation, _) => operation == "ReadSoftwareInfo" ? throw failure
            : new JsonObject { ["MayHaveChanged"] = false, ["OutcomeUnknown"] = true });
        var dispatch = new PortedToolContract.WorkerDispatch(worker, CancellationToken.None);
        Assert.Throws<WorkerOperationException>(() => dispatch.Call("ReadSoftwareInfo", new JsonObject()));
        dispatch.Call("ReadState", new JsonObject());
        Assert.Null(dispatch.LastFailure);
        var result = dispatch.CaughtRefusal("15.1", tool, "read-probe", false, true);
        Assert.Equal(code, (string?)result?.StructuredContent?["error"]?["code"]);
        if (tool == "ValidateAutomationContext")
            Assert.Equal("expectedPlcSoftwarePath", (string?)result?.StructuredContent?["error"]?["details"]?["target"]);
        dispatch.Call("hardware-devices.HardwareCreateDevice", new JsonObject());
        Assert.Null(dispatch.CaughtRefusal("15.1", tool, "write", true, true));
    }
}
