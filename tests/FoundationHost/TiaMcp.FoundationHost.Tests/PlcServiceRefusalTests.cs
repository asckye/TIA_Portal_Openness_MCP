extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;
using Xunit;

public sealed class PlcServiceRefusalTests
{
    [Theory]
    [InlineData("GetPlcBlockFingerprints", "FingerprintDataProvider", true)]
    [InlineData("GetPlcBlockFingerprints", "FingerprintDataProvider", false)]
    [InlineData("GeneratePlcLoadableFile", "LoadableProvider", true)]
    [InlineData("GeneratePlcLoadableFile", "LoadableProvider", false)]
    public void A_missing_runtime_service_is_a_precondition_at_the_registered_tool(string tool, string provider, bool preview)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        string[] owners = { "PlcSoftware PLC_1", "DeviceItem CPU", "Device Station" };
        var error = PlcServiceLookup.Missing(provider, "PLC_1", "OrderNumber:CPU/V3.1", owners);
        var meta = (Dictionary<string, object?>)error.Data["nativeResultEvidence"]!;
        meta["mayHaveChanged"] = false;
        StepFailureStatus.RecordTarget(error, meta);
        var port = new Services.OnlineDownloadService((operation, args) =>
        {
            Assert.Equal("plc-online.ExecuteOnline", operation);
            Assert.Equal(tool, (string?)args["request"]!["Operation"]);
            return new JsonObject { ["Value"] = new JsonObject { ["Meta"] = JsonSerializer.SerializeToNode(meta), ["Message"] = tool + " failed" } };
        }, () => "fixture.ap21");
        var body = (tool == "GeneratePlcLoadableFile"
            ? new Host.NativeExchangeTools(port).GeneratePlcLoadableFileV4("PLC_1", new[] { "FC_A" }, "blocks", "Plc", "output.bin", preview)
            : new Host.OnlineDataTools(port).ReadPlcBlockFingerprintsV4("PLC_1", "192.0.2.1", dryRun: preview)).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.Equal("service-not-provided", (string?)body["error"]!["details"]!["condition"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
        var evidence = body["data"]!["evidence"] ?? body["data"]!;
        Assert.Equal(provider, (string?)evidence["service"]);
        Assert.Equal(owners, evidence["searched"]!.Deserialize<string[]>());
        Assert.Equal("OrderNumber:CPU/V3.1", (string?)evidence["typeIdentifier"]);
        Assert.Equal("CPU", (string?)evidence["orderNumber"]);
        Assert.Equal("V3.1", (string?)evidence["firmware"]);
    }

    [Fact]
    public void Engineering_export_refuses_a_missing_parent_before_invoking_the_export()
    {
        using var release = Host.HardwareContract.UseRelease("21");
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "export.xml");
        bool invoked = false;
        var body = Host.EngineeringToolContract.Export("ExportTechnologyObject", path, () =>
        {
            invoked = true;
            return new Host.ResponseMessage();
        }).StructuredContent!;
        Assert.False(invoked);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.Equal("output-parent-missing", (string?)body["error"]!["details"]!["condition"]);
        Assert.Equal("exportPath", (string?)body["error"]!["details"]!["parameter"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
    }

    [Fact]
    public void Technology_create_refusal_keeps_the_verified_native_evidence_at_the_host()
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var step = new HardwareAddressingReply();
        step.Meta["typeIdentifier"] = "PID_Compact";
        step.Meta["version"] = "2.3";
        step.Meta["mayHaveChanged"] = false;
        step.Meta["operationSuccess"] = false;
        step.Meta["rollbackOutcome"] = "restored";
        step.Meta["rollbackCompleted"] = true;
        step.Meta["nativeMessage"] = "The type or version of the library element \"PID_Compact.2.3\" does not exist or is not a valid technology object.";
        Assert.True(PlcTechnologyRefusal.Record(step, "PID_Compact", "2.3"));
        JsonNode HostResult() => Host.EngineeringToolContract.Run("ManageTechnologyObject", true, () =>
            new Host.ResponseMessage { Message = step.Message, Meta = JsonSerializer.SerializeToNode(step.Meta)!.AsObject() }).StructuredContent!;
        var body = HostResult();
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.Equal("technology-type-version-unavailable", (string?)body["error"]!["details"]!["condition"]);
        var evidence = body["data"]!["evidence"]!;
        Assert.Equal("PID_Compact", (string?)evidence["typeIdentifier"]);
        Assert.Equal("2.3", (string?)evidence["version"]);
        Assert.True((bool?)evidence["nothingCreated"]);
        Assert.Contains("Technology objects > Add new object", (string?)body["error"]!["message"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);

        step.Meta["writeOutcomeUnknown"] = true;
        step.Meta["rollbackCompleted"] = false;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)HostResult()["error"]!["code"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loadable_output_parent_refusal_reaches_the_client(bool preview)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var error = AdapterPreconditionException.OutputParentMissing("filePath");
        var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = false, ["parameter"] = StepFailureStatus.Parameter(error) };
        StepFailureStatus.RecordTarget(error, meta);
        var body = Host.PlcExchangeContract.Map("GeneratePlcLoadableFile", new Host.ResponseMessage {
            Meta = JsonSerializer.SerializeToNode(meta)!.AsObject() }, !preview, true).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.Equal("output-parent-missing", (string?)body["error"]!["details"]!["condition"]);
        Assert.Equal("filePath", (string?)body["error"]!["details"]!["parameter"]);
    }
}
