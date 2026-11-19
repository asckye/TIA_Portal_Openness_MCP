extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Xunit;
using HostContract = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract;
using AddressService = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareAddressingService;
using AddressesTools = toolpipeline::TiaMcpServer.ModelContextProtocol.AddressesTools;
using NetworkContract = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareToolContract;
using ResponseJsonReport = toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseJsonReport;
using ResponseMeta = TiaMcpServer.ModelContextProtocol.ResponseMeta;

public sealed class DeviceItemPathFailureTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> PutGetReleases => Releases.Skip(1);

    [Theory, MemberData(nameof(Releases))]
    public void Missing_item_is_not_found_with_the_tried_path(string release)
    {
        using var selected = HostContract.UseRelease(release);
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareDevicesService((operation, _) => {
            Assert.Equal("hardware-devices.HardwareDescribeItemAt", operation); return null;
        }, () => true, () => "C:/Fixture/Fixture.ap21");
        var tool = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(service);
        AssertFailure(tool.GetDeviceItemInfoV4("PLC_A/Missing"), "NOT_FOUND", "PLC_A/Missing");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Preview_of_a_missing_item_is_not_found(string release)
    {
        using var selected = HostContract.UseRelease(release);
        var service = new AddressService((operation, _) => {
            Assert.Equal("hardware-addressing.ReadHardwareIoAddresses", operation); return null;
        }, () => true, () => "C:/Fixture/Fixture.ap21");
        AssertFailure(new AddressesTools(service).SetDeviceItemIoAddressV4("PLC_A/Missing", "Input", 20), "NOT_FOUND", "PLC_A/Missing");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Item_without_the_io_type_names_the_child_that_has_it(string release)
    {
        using var selected = HostContract.UseRelease(release);
        var service = new AddressService((operation, _) => operation switch {
            "hardware-addressing.ReadHardwareIoAddresses" => new JsonArray(),
            "hardware-addressing.DescribeHardwareIoChildren" => new JsonArray("PLC_A/DI_1/DI_1 → Input 0(len 16)"),
            _ => throw new InvalidOperationException("Unexpected operation: " + operation)
        }, () => true, () => "C:/Fixture/Fixture.ap21");
        var result = new AddressesTools(service).SetDeviceItemIoAddressV4("PLC_A/DI_1", "Input", 20);
        AssertFailure(result, "INVALID_ARGUMENT", "PLC_A/DI_1/DI_1 → Input 0(len 16)");
        Assert.Equal("/ioType", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
    }

    [Fact]
    public void Io_address_preview_hash_binds_the_requested_address()
    {
        using var worker = new WritePreviewCoverageTests.PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var scope = pipeline.EnterSession();
        int writes = 0;
        var service = new AddressService((operation, args) => {
            var before = new JsonObject { ["IoType"] = "Input", ["StartAddress"] = 0, ["Length"] = 4 };
            if (operation == "hardware-addressing.ReadHardwareIoAddresses") return new JsonArray(before);
            Assert.Equal("hardware-addressing.SetHardwareIoAddress", operation);
            writes++;
            Assert.Equal("C:/Projects/original.ap21", (string?)args["expectedProjectFile"]);
            var after = before.DeepClone().AsObject();
            after["StartAddress"] = args["startAddress"]!.DeepClone();
            return new JsonObject { ["Ok"] = true, ["Message"] = "Address read back.", ["Before"] = before, ["After"] = after };
        }, () => true, () => "C:/Projects/original.ap21");
        var tools = new AddressesTools(service);
        var preview = tools.SetDeviceItemIoAddressV4("PLC/DI", "Input", 2);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        Assert.Equal(64, hash.Length);
        var stale = tools.SetDeviceItemIoAddressV4("PLC/DI", "Input", 4, false, hash, "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, writes);
        var applied = tools.SetDeviceItemIoAddressV4("PLC/DI", "Input", 2, false, hash, "C:/Projects/original.ap21");
        Assert.True((bool?)applied.StructuredContent?["ok"], applied.StructuredContent?.ToJsonString());
        Assert.Equal(1, writes);
    }

    private const string PutGetReason = "PUT/GET access has no VM-verified Openness attribute for this CPU.";

    [Theory, MemberData(nameof(PutGetReleases))]
    public void Unavailable_put_get_read_is_unsupported_with_the_reason(string release)
    {
        using var selected = HostContract.UseRelease(release);
        var report = new ResponseJsonReport { Ok = false, Message = PutGetReason, Data = new JsonObject {
            ["found"] = false, ["failureCode"] = "UNSUPPORTED_CAPABILITY", ["device"] = "PLC_A", ["message"] = PutGetReason },
            Meta = ResponseMeta.Basic(DateTime.Now, false) };
        AssertFailure(NetworkContract.MapResult("GetPlcPutGetAccess", report, true, false), "UNSUPPORTED_CAPABILITY", "VM-verified");
    }

    [Theory, MemberData(nameof(PutGetReleases))]
    public void Unavailable_put_get_write_keeps_the_reason(string release)
    {
        using var selected = HostContract.UseRelease(release);
        var report = new ResponseJsonReport { Ok = false, Message = PutGetReason, Data = new JsonObject {
            ["ok"] = false, ["failureCode"] = "UNSUPPORTED_CAPABILITY", ["mayHaveChanged"] = false,
            ["device"] = "PLC_A", ["message"] = PutGetReason, ["nativeMessage"] = PutGetReason },
            Meta = ResponseMeta.Basic(DateTime.Now, false) };
        AssertFailure(NetworkContract.MapResult("SetPlcPutGetAccess", report, false, false), "UNSUPPORTED_CAPABILITY", "VM-verified");
    }

    [Theory]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task Loadable_target_option_lists_the_documented_values(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var shared = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, shared);
        using var session = pipeline.EnterSession();
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "GeneratePlcLoadableFile");
        var target = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("targetOption");
        Assert.Equal(new[] { "Plc", "PlcSim" }, target.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        var args = new JsonObject { ["softwarePath"] = "PLC_A", ["objectPaths"] = new JsonArray("Main"), ["objectKind"] = "blocks",
            ["targetOption"] = "", ["filePath"] = "C:/Fixture/Loadable.bin", ["dryRun"] = true };
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = "GeneratePlcLoadableFile",
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
        var result = await tool.InvokeAsync(request);
        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Contains("targetOption", result.StructuredContent?["error"]?.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    private static void AssertFailure(CallToolResult result, string code, string message)
    {
        Assert.True(result.IsError);
        Assert.Equal(code, (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Contains(message, (string?)result.StructuredContent?["error"]?["message"], StringComparison.Ordinal);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }
}
