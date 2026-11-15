extern alias enginehost;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class DevicePathEnvelopeTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .SelectMany(release => new[] { new object[] { release, true }, new object[] { release, false } });

    private static JsonObject Evidence(string status) => new() {
        ["status"] = status, ["target"] = "G/X", ["candidates"] = new JsonArray("A", "G/B"),
        ["missingDeviceTarget"] = status == "NotFound" ? "G/X" : null,
        ["mayHaveChanged"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["dataComplete"] = false,
        ["nativeMessage"] = status == "NotFound" ? "Device not found: G/X. Candidates: A; G/B."
            : "Device path G/X matches more than one device: A; G/B." };

    private static void Refusal(JsonNode body, string code)
    {
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("G/X", (string?)body["error"]?["details"]?["target"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Missing_device_reports_its_target(string release, bool read)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var response = new Host.ResponseMessage { Meta = Evidence("NotFound") };
        string tool = read ? "GetDeviceAddressing" : "SetDeviceAddress";
        foreach (var body in new[] {
            Host.HardwareToolContract.MapResult(tool, response, read, true).StructuredContent!,
            Host.HardwareContract.Map(tool, response, !read, true).StructuredContent!,
            Host.EngineeringToolContract.Map("CompareProjects", response, !read).StructuredContent! })
        {
            Refusal(body, "NOT_FOUND");
            Assert.Contains("G/B", (string?)body["error"]?["message"]);
        }
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Ambiguous_device_is_a_typed_refusal(string release, bool read)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var response = new Host.ResponseMessage { Meta = Evidence("Ambiguous") };
        foreach (var body in new[] {
            Host.HardwareToolContract.MapResult(read ? "GetDeviceAddressing" : "SetDeviceAddress", response, read, true).StructuredContent!,
            Host.HardwareContract.Map("GetDeviceInfo", response, !read, true).StructuredContent!,
            Host.EngineeringToolContract.Map("CompareProjects", response, !read).StructuredContent!,
            Host.HardwareToolContract.MapResult("CompileDevice", response, read, true).StructuredContent! })
        {
            Refusal(body, "TARGET_AMBIGUOUS");
            Assert.Equal(new[] { "A", "G/B" }, body["error"]!["details"]!["candidates"]!.AsArray().Select(c => (string)c!));
        }
    }

    [Theory]
    [InlineData("GetDeviceInfo")]
    [InlineData("GetDeviceAttributes")]
    [InlineData("GetDeviceIpAddress")]
    [InlineData("ExportDeviceAml")]
    [InlineData("GetPlcLiveValuesS7")]
    [InlineData("GetPlcPutGetAccess")]
    [InlineData("SetPlcPutGetAccess")]
    [InlineData("DescribeObject")]
    [InlineData("GetDevicePlugLocations")]
    public void String_entries_keep_ambiguity_through_wrapped_exceptions(string tool)
    {
        var error = new PlcSoftwareException("Ambiguous", "Device path G/X matches more than one device: A; G/B.", new[] { "A", "G/B" });
        error.Data["target"] = "G/X";
        var wrapped = new InvalidOperationException("Native lookup wrapper", error);
        var body = tool == "GetPlcLiveValuesS7"
            ? Host.RuntimeToolContract.Run(tool, true, false, () => throw wrapped).StructuredContent!
            : tool == "DescribeObject"
                ? Host.EngineeringToolContract.Run(tool, false, () => throw wrapped).StructuredContent!
                : tool is "GetPlcPutGetAccess" or "SetPlcPutGetAccess"
                    ? Host.HardwareToolContract.Invoke(tool, tool == "GetPlcPutGetAccess", true, () => throw wrapped).StructuredContent!
                    : Host.HardwareContract.Run(tool, () => throw wrapped, tool == "ExportDeviceAml", false).StructuredContent!;
        Refusal(body, "TARGET_AMBIGUOUS");
    }

    [Fact]
    public void Existing_worker_evidence_retains_ambiguity_without_a_wire_change()
    {
        var error = new InvalidOperationException("Worker wrapper");
        error.Data["nativeResultEvidence"] = Evidence("Ambiguous");
        Refusal(Host.HardwareContract.Run("GetDeviceInfo", () => throw error, false, false).StructuredContent!, "TARGET_AMBIGUOUS");
        error.Data["foundationPriorMutation"] = true;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)Host.HardwareContract.Run("ExportDeviceAml", () => throw error, true, false)
            .StructuredContent!["error"]?["code"]);
    }

    [Fact]
    public void Ambiguity_after_a_write_stays_unknown()
    {
        var evidence = Evidence("Ambiguous"); evidence["mayHaveChanged"] = true;
        var response = new Host.ResponseMessage { Meta = evidence };
        foreach (var body in new[] {
            Host.HardwareToolContract.MapResult("SetDeviceAddress", response, false, true).StructuredContent!,
            Host.HardwareContract.Map("SetDeviceAddress", response, true, true).StructuredContent!,
            Host.EngineeringToolContract.Map("ShowObjectInEditor", response, true).StructuredContent! })
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        var error = new PlcSoftwareException("Ambiguous", "Device path G/X is ambiguous.", new[] { "A", "G/B" });
        error.Data["target"] = "G/X"; error.Data["mayHaveChanged"] = true;
        Assert.Equal(TiaMcp.WorkerChannel.ChannelOutcome.Unknown, WorkerFailurePolicy.Classify(error, true, false).Outcome);
        Assert.Null(JsonNode.Parse(TiaMcp.PlcWorker.WorkerJson.Evidence(error))!["status"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)Host.HardwareContract.Run("ExportDeviceAml", () => throw error, true, true)
            .StructuredContent!["error"]?["code"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task Worker_evidence_maps_to_the_registered_string_entry(string release, bool read)
    {
        using var worker = new AmbiguousWorker();
        string name = read ? "GetDeviceInfo" : "GetDeviceAttributes";
        var inner = new FoundationTool(worker, release, typeof(Host.HardwareDevicesTools).GetMethod(name + "V4")!, name);
        var tool = new enginehost::TiaMcp.FoundationHost.CanonicalParameterTool(
            new FoundationV4Tool(inner, release, () => new TiaOpenness.Shared.ApprovalSettings(false, 1)), release);
        var args = new JsonObject { ["devicePath"] = new JsonArray("G", "X") };
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(
            DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
                Params = new() { Name = name, Arguments = args.Deserialize<Dictionary<string, JsonElement>>() } });
        Refusal(result.StructuredContent!, "TARGET_AMBIGUOUS");
        Assert.Equal(release, (string?)result.StructuredContent!["meta"]?["releaseKey"]);
        Assert.Equal(1, worker.Lookups);
    }

    [Fact]
    public void Live_read_refuses_an_ambiguous_precheck_before_opening_a_connection()
    {
        var error = new PlcSoftwareException("Ambiguous", "Device path G/X matches more than one device: A; G/B.", new[] { "A", "G/B" });
        error.Data["target"] = "G/X";
        var runtime = new Host.RuntimeTools(_ => throw error, true);
        Refusal(runtime.ReadPlcLiveValuesS7V4("127.0.0.1", new[] { "M0.0" }, devicePath: "G/X").StructuredContent!, "TARGET_AMBIGUOUS");
    }

    private sealed class AmbiguousWorker : IFoundationWorker
    {
        internal int Lookups;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            Lookups++;
            Assert.Equal("device-groups/G/devices/X", (string?)(arguments["path"] ?? arguments["devicePath"]));
            var error = new PlcSoftwareException("Ambiguous", "Device path G/X matches more than one device: A; G/B.", new[] { "A", "G/B" });
            error.Data["target"] = "G/X";
            var classification = WorkerFailurePolicy.Classify(error, true, false);
            Assert.Equal(TiaMcp.WorkerChannel.ChannelOutcome.RejectedBeforeNative, classification.Outcome);
            throw WorkerOperationException.FromChannelFailure(new TiaMcp.WorkerChannel.ChannelFailureException(
                error.Message, classification.Code, classification.Outcome, TiaMcp.PlcWorker.WorkerJson.Evidence(error)));
        }
        public void Dispose() { }
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Non_device_target_evidence_is_not_a_missing_device_identity(string release, bool read)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var evidence = new JsonObject { ["status"] = "NotFound", ["target"] = "plc",
            ["mayHaveChanged"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false };
        var response = new Host.ResponseMessage { Meta = evidence };
        foreach (var body in new[] {
            Host.HardwareToolContract.MapResult("CompileDevice", response, read, true).StructuredContent!,
            Host.HardwareContract.Map("CompileDevice", response, !read, true).StructuredContent!,
            Host.EngineeringToolContract.Map("ManagePasswordPolicy", response, !read).StructuredContent! })
        {
            Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
            Assert.Null(body["error"]?["details"]?["target"]);
        }
    }
}
