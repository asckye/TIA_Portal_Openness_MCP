extern alias adaptertests;
extern alias toolpipeline;
extern alias validationfixture;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using TiaMcpServer.ModelContextProtocol;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;
using Fixture = adaptertests::TiaMcp.Adapters.Contracts.Tests.HardwareWriteFailureTests;
using Step = adaptertests::TiaMcp.Adapters.Contracts.Tests.MutationEvidenceTests;
using Xunit;

public sealed class ReturnedWriteOutcomeTests
{
    [Theory]
    [InlineData("ExportHmiConnection")][InlineData("ExportHmiProgram")][InlineData("ExportHmiScreen")]
    [InlineData("ExportHmiTagTable")][InlineData("ImportHmiTagTable")]
    public void Missing_HMI_is_a_typed_lookup_refusal_before_any_native_write(string tool)
    {
        using var release = Host.HardwareContract.UseRelease("15.1");
        var engine = new validationfixture::TiaMcp.Adapters.PlcFoundationEngine {
            LookupFailure = AdapterPreconditionException.NotFound("Fixture target unavailable.", "softwarePath") };
        var service = new Services.HmiExchangeService((_, args) => {
            var request = args["request"]!.Deserialize<HmiRequest>()!;
            return JsonSerializer.SerializeToNode(engine.ExecuteHmi(request, request.DryRun));
        }, () => "Project.ap15_1");
        var tools = new Host.HmiExchangeTools(service);
        JsonNode Run() => (tool switch {
            "ExportHmiConnection" => tools.ExportHmiConnectionV4("HMI", "Connection", "new.xml"),
            "ExportHmiProgram" => tools.ExportHmiProgramV4("HMI", "exports"),
            "ExportHmiScreen" => tools.ExportHmiScreenV4("HMI", "Overview", "new.xml"),
            "ExportHmiTagTable" => tools.ExportHmiTagTableV4("HMI", "Tags", "new.xml"),
            _ => Host.HmiExchangeContract.Run(tool, true, true, () => tools.ImportHmiTagTable("HMI", "/", "tags.xml"))
        }).StructuredContent!;
        var body = Run();
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(0, engine.NativeCalls);
        engine.NativeMayHaveChanged = true;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)Run()["error"]?["code"]);
    }

    [Theory, InlineData("GetHmiProgramInfo"), InlineData("DescribeHmiSoftware"), InlineData("DescribeHmiScreen"),
        InlineData("DescribeHmiTagTable"), InlineData("DescribeHmiTag"), InlineData("DescribeHmiScreenItem")]
    public void Inspection_reports_a_missing_HMI_through_the_normal_tool_entry_point(string tool)
    {
        using var release = Host.HardwareContract.UseRelease(tool == "DescribeHmiScreenItem" ? "21" : "15.1");
        var engine = new validationfixture::TiaMcp.Adapters.PlcFoundationEngine {
            LookupFailure = AdapterPreconditionException.NotFound("Fixture target unavailable.", "softwarePath") };
        var service = new Services.HmiDescribeService((_, args) => {
            var request = args["request"]!.Deserialize<HmiRequest>()!;
            return JsonSerializer.SerializeToNode(engine.ExecuteHmi(request, request.DryRun));
        }, () => "Project.ap15_1");
        var tools = new Host.HmiDescribeTools(service);
        var body = (tool switch {
            "GetHmiProgramInfo" => tools.GetHmiProgramInfoV4("HMI"),
            "DescribeHmiSoftware" => tools.DescribeHmiSoftwareV4("HMI"),
            "DescribeHmiScreen" => tools.DescribeHmiScreenV4("HMI", "Screen"),
            "DescribeHmiTagTable" => tools.DescribeHmiTagTableV4("HMI", "Table"),
            "DescribeHmiTag" => tools.DescribeHmiTagV4("HMI", "Table", "Tag"),
            _ => tools.DescribeHmiScreenItemV4("HMI", "Screen", "Item")
        }).StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal(0, engine.NativeCalls);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Hmi_compile_uses_the_typed_worker_lookup_and_mutation_flags(bool changed)
    {
        var missing = AdapterPreconditionException.NotFound("Fixture target unavailable.", "softwarePath");
        var service = new Services.PlcOrganisationPortService((_, _) => JsonSerializer.SerializeToNode(new PlcSoftwareReply {
            MayHaveChanged = changed, OutcomeUnknown = changed, Failure = new PlcSoftwareFailure { Code = missing.GetType().Name, Message = missing.Message,
                Evidence = new() { ["status"] = StepFailureStatus.Of(missing), ["parameter"] = missing.ParamName } }
        }), () => true, () => "Project.ap15_1");
        var body = new Host.PlcCompilePortTools(service).CompileAndDiagnoseHmiV4("HMI").StructuredContent!;
        Assert.Equal(changed ? "OUTCOME_UNKNOWN" : "NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal(changed ? "unknown" : "not-started", (string?)body["meta"]?["execution"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void False_CAx_export_is_settled_and_identifies_the_native_log_and_possible_partial_file(bool partialFile)
    {
        using var files = new Files();
        using var release = Host.HardwareContract.UseRelease("16");
        var output = Path.Combine(files.Root, "MCP_RC_HMI.aml");
        var log = output + "." + Guid.NewGuid().ToString("N") + ".log";
        var state = new WorkerSessionOutcomeState();
        var service = new Services.HardwareAmlService((_, _) => {
            File.WriteAllText(log, "CAx export failed.");
            if (partialFile) File.WriteAllText(output, """<CAEXFile SchemaVersion="2.15">""");
            var reply = new HardwareAmlExportReply { DeviceName = "MCP_RC_HMI", FilePath = output, State = "False",
                Success = false, NativeBooleanResult = false, Messages = ["CAx Export returned bool; native log: " + log] };
            return JsonNode.Parse(WorkerOperationResponse.Complete(reply, false, state).ResultJson);
        }, () => true, () => "Project.ap16");
        var body = new Host.HardwareAmlTools(service).ExportDeviceAmlV4("MCP_RC_HMI", output).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        Assert.False(state.Uncertain);
        Assert.False(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.HasUnknownOutcome(body));
        Assert.False((bool?)body["data"]?["evidence"]?["nativeBooleanResult"]);
        Assert.Equal(output, (string?)body["data"]?["filePath"]);
        Assert.Contains(log, (string?)body["data"]?["messages"]?[0]);
        Assert.Contains("partial", (string?)body["error"]?["message"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rebuild", (string?)body["error"]?["message"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(partialFile, File.Exists(output));
        Assert.True(File.Exists(log));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void False_CAx_import_survives_worker_completion_with_bounded_log_evidence(bool logExists)
    {
        using var files = new Files();
        var source = Path.Combine(files.Root, "source.aml");
        File.WriteAllText(source, """<CAEXFile SchemaVersion="2.15" FileName="source.aml"><InstanceHierarchy Name="Project"/></CAEXFile>""");
        var raw = Fixture.ImportWithLog(source, Path.Combine(files.Root, "cax.log"), log => {
            if (logExists) File.WriteAllLines(log.FullName, Enumerable.Range(1, 90)
                .Select(i => $@"Device {i} could not import password=""fixture-secret with spaces"" C:\private\fixture.aml"));
        });
        var state = new WorkerSessionOutcomeState();
        var wire = JsonNode.Parse(WorkerOperationResponse.Complete(raw, false, state).ResultJson)!.AsObject();
        Assert.False(state.Uncertain);
        Assert.DoesNotContain("fixture-secret", wire.ToJsonString());
        var service = new Services.HardwareAmlService((_, _) => wire, () => true, () => "Project.ap15_1");
        var body = Host.HardwareContract.Map("ImportDeviceAml", service.ImportDeviceAml(source, "cax.log", confirmImport: true, dryRun: false),
            true, true).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        var evidence = body["data"]!["evidence"]!;
        Assert.True((bool?)evidence["mayHaveChanged"]);
        Assert.True((bool?)evidence["apiCallSuccess"]);
        Assert.False((bool?)evidence["operationSuccess"]);
        Assert.False((bool?)evidence["nativeResult"]);
        Assert.Equal("inspect devices per log", (string?)evidence["cleanupRequired"]?[0]?["action"]);
        var retained = body["error"]!["details"]!["evidence"]!;
        Assert.Equal(logExists, retained["logFile"] != null);
        if (logExists)
        {
            var excerpt = retained["logExcerpt"]!.AsArray();
            Assert.Equal(40, excerpt.Count);
            Assert.InRange(Encoding.UTF8.GetByteCount(string.Join("\n", excerpt.Select(line => (string?)line))), 1, 4096);
            Assert.Contains("Device 1 could not import", (string?)excerpt[0]);
            Assert.DoesNotContain("fixture-secret", body.ToJsonString());
            Assert.DoesNotContain("private", body.ToJsonString());
        }
        else Assert.True((bool?)retained["logFileMissing"]);
    }

    [Fact]
    public void Returned_unsuccessful_transfer_keeps_messages_and_possible_device_changes()
    {
        var step = Step.RunStep("21", "ImportDeviceAml", meta => {
            meta["apiCallSuccess"] = true; meta["operationSuccess"] = false; meta["mayHaveChanged"] = true;
            meta["status"] = "ImportFailed"; meta["nativeResult"] = "Error"; meta["logFile"] = null; meta["dataComplete"] = false;
            meta["messages"] = new[] { @"Error: device rejected C:\private\device.aml" };
            meta["cleanupRequired"] = new[] { new { action = "inspect devices per log" } };
            return "CAx import returned Error.";
        });
        Assert.False(step.OutcomeUnknown); Assert.True(step.MayHaveChanged);
        var meta = JsonSerializer.SerializeToNode(step.Meta)!.AsObject();
        var body = Host.HardwareContract.Map("ImportDeviceAml", new Host.ResponseMessage { Meta = meta }, true, true).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        Assert.Equal("Error: device rejected <path>", (string?)body["error"]?["details"]?["evidence"]?["messages"]?[0]);
        meta["writeOutcomeUnknown"] = true;
        var unknown = Host.HardwareContract.Map("ImportDeviceAml", new Host.ResponseMessage { Meta = meta }, true, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)unknown["error"]?["code"]);
    }

    [Fact]
    public void A_completed_override_with_mismatching_readback_keeps_its_backup_in_the_worker_reply()
    {
        using var files = new Files();
        const string xml = "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>FC_A</Name><Number>7</Number>" +
            "<ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections/></Interface></AttributeList></SW.Blocks.FC></Document>";
        int imports = 0;
        void Export(string path) => File.WriteAllText(path, imports == 0 ? xml : xml.Replace(">7<", ">8<"));
        var preview = new JsonObject();
        PlcVerifiedImport.Execute(xml, "PLC/FC_A", files.Root, true, "", Export, _ => imports++, () => { }, preview);
        var step = Step.RunStep("15.1", "ImportPlcBlockVerified", meta => {
            var plan = new JsonObject();
            string report = PlcVerifiedImport.Execute(xml, "PLC/FC_A", files.Root, false, (string)preview["token"]!, Export,
                _ => imports++, () => { }, plan);
            foreach (var pair in plan) meta[pair.Key] = PlcVerifiedImport.PlainValue(pair.Value);
            return report;
        });
        var state = new WorkerSessionOutcomeState();
        var response = WorkerOperationResponse.Complete(new PlcSoftwareReply { Meta = step.Meta, Message = step.Message,
            MayHaveChanged = step.MayHaveChanged, OutcomeUnknown = step.OutcomeUnknown }, false, state);
        Assert.False(state.Uncertain);
        var reply = JsonSerializer.Deserialize<PlcSoftwareReply>(response.ResultJson)!;
        var body = Host.PlcToolContract.Map("ImportPlcBlockVerified", reply, true, true).StructuredContent!;
        Assert.Equal(1, imports);
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        var evidence = body["error"]!["details"]!["evidence"]!;
        Assert.Equal("complete", (string?)evidence["phase"]);
        Assert.True((bool?)evidence["importReturned"]);
        Assert.Equal("DocumentReadbackMismatch", (string?)evidence["status"]);
        Assert.Equal("before.xml", (string?)evidence["cleanupRequired"]?[0]?["restoreFrom"]);
        Assert.Equal(xml, File.ReadAllText(Path.Combine((string)body["data"]!["evidence"]!["evidenceDirectory"]!, "before.xml")));
        var meta = JsonSerializer.SerializeToNode(step.Meta)!.AsObject();
        meta["cleanupRequired"]![0]!["restoreFrom"] = Path.Combine(files.Root, "outside.xml");
        var outside = Host.PlcToolContract.Map("ImportPlcBlockVerified", new Host.ResponseMessage { Meta = meta }, true, true).StructuredContent!;
        Assert.Equal("<path>", (string?)outside["data"]?["evidence"]?["cleanupRequired"]?[0]?["restoreFrom"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Model_generation_uses_the_post_failure_file_observation(bool partial)
    {
        using var files = new Files();
        using var release = Host.HardwareContract.UseRelease("15.1");
        var output = Path.Combine(files.Root, "interface.xml");
        var service = new Services.OpcUaService((_, _) => new JsonObject { ["Data"] = Fixture.FailedGeneration(output, partial) }, () => "Project.ap15_1");
        var body = new Host.OpcUaTools(service).GenerateOpcUaModelledInterfaceV4("PLC", "Interface", "urn:fixture", output, dryRun: false).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("completed", (string?)body["meta"]?["execution"]);
        var evidence = body["data"]!["evidence"]!;
        Assert.Equal(partial, (bool?)evidence["outputExists"]);
        Assert.Equal(partial, (bool?)evidence["mayHaveChanged"]);
        Assert.Equal(partial, File.Exists(output));
        Assert.Equal(partial ? "writeOutput" : "readDataTypes", (string?)evidence["phase"]);
        Assert.Equal(partial ? "interface.xml" : null, (string?)evidence["cleanupRequired"]?[0]?["target"]);
    }

    [Fact]
    public void Model_generation_keeps_an_unobservable_output_unknown()
    {
        using var release = Host.HardwareContract.UseRelease("15.1");
        using var files = new Files();
        var output = Path.Combine(files.Root, "new.xml");
        var service = new Services.OpcUaService((_, _) => new JsonObject { ["Data"] = Fixture.FailedGeneration(output, true, unreadable: true) },
            () => "Project.ap15_1");
        var body = new Host.OpcUaTools(service).GenerateOpcUaModelledInterfaceV4("PLC", "Main", "urn:fixture", output, dryRun: false).StructuredContent!;
        Assert.Null(body["data"]?["evidence"]?["outputExists"]);
        Assert.True((bool?)body["data"]?["evidence"]?["mayHaveChanged"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
        Assert.Equal("writeOutput", (string?)body["data"]?["evidence"]?["phase"]);
    }

    [Theory, InlineData("NotSupportedException", "UNSUPPORTED_CAPABILITY"), InlineData("ArgumentException", "INVALID_ARGUMENT")]
    public void Model_generation_preserves_typed_admission_errors(string type, string expectedCode)
    {
        using var release = Host.HardwareContract.UseRelease("15.1");
        var service = new Services.OpcUaService((_, _) => JsonSerializer.SerializeToNode(new PlcB5Reply {
            Failure = new PlcSoftwareFailure { Code = type, Message = "Fixture generator admission refusal.",
                Evidence = new() { ["v4FailureCode"] = expectedCode, ["parameter"] = "interfaceName" } }
        }), () => "Project.ap15_1");
        var body = new Host.OpcUaTools(service).GenerateOpcUaModelledInterfaceV4("PLC", "Main", "urn:fixture", "new.xml", dryRun: false).StructuredContent!;
        Assert.Equal(expectedCode, (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "outcome-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Root, true);
    }
}
