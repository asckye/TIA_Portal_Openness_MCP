extern alias adaptertests;
extern alias toolpipeline;
extern alias foundationhost;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using HardwareContract = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareContract;
using HardwareTools = toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools;
using HardwareService = toolpipeline::TiaMcpServer.Siemens.Services.HardwareNetworkPortService;

namespace TiaMcp.FoundationHost.Tests.Operator;

public sealed class HardwareReadFlagsTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> EnumerationFailures => adaptertests::TiaMcp.Adapters.Contracts.Tests.HardwareNetworkReadTests.EnumerationFailures;

    [Theory, MemberData(nameof(Releases))]
    public void Network_info_unreadable_attribute_is_partial(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var body = new HardwareTools(NativeNetworkService(release, "attribute")).GetDeviceItemNetworkInfo("PLC/CPU").StructuredContent!;
        Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        Assert.Contains(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "INCOMPLETE_DATA");
        var attributes = body["data"]!["attributes"]!.AsArray();
        Assert.Equal(new[] { "Address", "MacAddress" }, attributes.Select(a => (string?)a?["name"]));
        Assert.Null(attributes[1]!["value"]);
    }

    [Theory, MemberData(nameof(EnumerationFailures))]
    public void Network_info_failed_observation_is_read_failed(string release, string failure)
    {
        using var selected = HardwareContract.UseRelease(release);
        var body = new HardwareTools(NativeNetworkService(release, failure)).GetDeviceItemNetworkInfo("PLC/CPU").StructuredContent!;
        Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("unknown", (string?)body["meta"]?["completeness"]);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Network_info_without_a_bound_project_is_project_not_bound(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var service = new HardwareService((_, _) => throw new InvalidOperationException("An unbound host must not dispatch."),
            () => false, () => "fixture");
        ProjectRefusal(new HardwareTools(service).GetDeviceItemNetworkInfo("PLC/CPU").StructuredContent!);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Network_info_without_a_worker_project_is_project_not_bound(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        ProjectRefusal(new HardwareTools(NativeNetworkService(release, "no-project")).GetDeviceItemNetworkInfo("PLC/CPU").StructuredContent!);
    }

    [Fact]
    public void Network_wire_refusal_accepts_only_the_project_not_bound_shape()
    {
        const string operation = "HardwareGetDeviceItemNetworkInfo";
        foundationhost::TiaMcp.FoundationHost.HardwareFamilyWire.Validate(operation,
            new JsonObject { ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." });
        var samples = FamilyReplyShapes.For("hardware-network." + operation, "System.Object");
        Assert.Contains(samples, sample => (sample.Reply as JsonObject)?["failureCode"]?.GetValue<string>() == "PROJECT_NOT_BOUND");
        Assert.All(samples, sample => foundationhost::TiaMcp.FoundationHost.HardwareFamilyWire.Validate(operation, sample.Arguments, sample.Reply));
        Assert.Throws<IOException>(() => foundationhost::TiaMcp.FoundationHost.HardwareFamilyWire.Validate(operation,
            new JsonObject { ["failureCode"] = "NOT_FOUND", ["message"] = "No project open." }));
        Assert.Throws<IOException>(() => foundationhost::TiaMcp.FoundationHost.HardwareFamilyWire.Validate(operation,
            new JsonObject { ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open.", ["extra"] = true }));
    }

    private static void ProjectRefusal(JsonNode body)
    {
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("none", (string?)body["meta"]?["completeness"]);
        Assert.False(body["error"]!["details"]!.AsObject().ContainsKey("target"));
    }

    private static HardwareService NativeNetworkService(string release, string failure) => new((operation, _) => {
        Assert.Equal("hardware-network.HardwareGetDeviceItemNetworkInfo", operation);
        var reply = JsonSerializer.SerializeToNode(adaptertests::TiaMcp.Adapters.Contracts.Tests.HardwareNetworkReadTests.ReadReply(release, failure));
        foundationhost::TiaMcp.FoundationHost.HardwareFamilyWire.Validate("HardwareGetDeviceItemNetworkInfo", reply);
        return reply;
    }, () => true, () => "fixture");

    [Theory, MemberData(nameof(Releases))]
    public void Network_info_read_with_attributes_is_complete(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var service = NetworkService(JsonNode.Parse("[{\"Name\":\"Address\",\"Value\":\"192.0.2.1\",\"DataType\":\"String\",\"IsWritable\":false}]"));
        var result = new HardwareTools(service).GetDeviceItemNetworkInfo("PLC/CPU");
        var body = result.StructuredContent!;
        Assert.False(result.IsError);
        Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
        Assert.Equal("read-only", (string?)body["meta"]?["execution"]);
        Assert.Equal("complete", (string?)body["meta"]?["completeness"]);
        Assert.Null(body["error"]);
        Assert.DoesNotContain(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "INCOMPLETE_DATA");
        Assert.Equal("attribute-name heuristic", (string?)body["data"]?["selection"]);
        Assert.Equal("192.0.2.1", (string?)Assert.Single(body["data"]!["attributes"]!.AsArray())?["value"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Network_info_for_a_missing_item_is_not_found(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var result = new HardwareTools(NetworkService(null)).GetDeviceItemNetworkInfo("PLC/Missing");
        var body = result.StructuredContent!;
        Assert.True(result.IsError);
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("/deviceItemPath", (string?)body["error"]?["details"]?["target"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal("none", (string?)body["meta"]?["completeness"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Network_info_empty_selection_is_complete(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var body = new HardwareTools(NetworkService(new JsonArray())).GetDeviceItemNetworkInfo("PLC/CPU").StructuredContent!;
        Assert.Equal("complete", (string?)body["meta"]?["completeness"]);
        Assert.Empty(body["data"]!["attributes"]!.AsArray());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Feature_read_preserves_presence_and_page_completeness(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var factory = typeof(adaptertests::TiaMcp.Adapters.Contracts.Tests.HardwareWriteFailureTests)
            .GetMethod("NativeFixture", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var fixture = (System.Runtime.CompilerServices.ITuple)factory.Invoke(null, new object[] { release, true })!;
        var engine = (adaptertests::TiaMcp.Adapters.PlcFoundationEngine)fixture[0]!;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareServicesPortService((operation, arguments) => {
            Assert.Equal("hardware-devices.HardwareReadHardwareFeatures", operation);
            return JsonSerializer.SerializeToNode(engine.HardwareReadHardwareFeatures(new[] { "PLC" }, new[] { "CPU" },
                (int)arguments["offset"]!, (int)arguments["limit"]!));
        }, () => true, () => "fixture");
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareServicesPortTools(service);
        var body = tools.GetHardwareFeatures(new[] { "PLC" }, new[] { "CPU" }).StructuredContent!;
        Assert.True((bool?)body["data"]?["dataComplete"]);
        var unprobed = body["data"]!["records"]!.AsArray().Single(row => (string?)row?["feature"] == "AddressController");
        Assert.True(unprobed!.AsObject().ContainsKey("present"));
        Assert.Null(unprobed!["present"]);
        Assert.Equal("type absent on this version", (string?)unprobed["skipped"]);
        Assert.Contains(release, (string?)body["data"]?["scope"], StringComparison.Ordinal);
        Assert.DoesNotContain("V21 XML", (string?)body["data"]?["scope"], StringComparison.Ordinal);
        var page = tools.GetHardwareFeatures(new[] { "PLC" }, new[] { "CPU" }, limit: 1).StructuredContent!;
        Assert.False((bool?)page["data"]?["dataComplete"]);
        Assert.Equal("partial", (string?)page["meta"]?["completeness"]);
    }

    private static HardwareService NetworkService(JsonNode? reply) => new((operation, arguments) => {
        Assert.Equal("hardware-network.HardwareGetDeviceItemNetworkInfo", operation);
        Assert.StartsWith("PLC/", (string?)arguments["deviceItemPath"]);
        return reply;
    }, () => true, () => "fixture");
}
