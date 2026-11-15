extern alias adaptertests;
extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;
using NativeEngine = adaptertests::TiaMcp.Adapters.PlcFoundationEngine;
using HardwareTools = enginehost::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools;
using HardwareService = enginehost::TiaMcpServer.Siemens.Services.HardwareNetworkPortService;
using HardwareContract = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract;

namespace TiaMcp.FoundationHost.Tests.Operator;

public sealed class HardwareArgumentRefusalTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void ListIoSystems_without_a_scope_names_subnetName(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var tool = ForbiddenTools();
        Refusal(tool.ListIoSystems().StructuredContent!, "subnetName", "exactly one");
        Refusal(tool.ListIoSystems("PN_IE_1", new[] { "PLC" }).StructuredContent!, "subnetName", "exactly one");
    }

    [Theory, MemberData(nameof(Releases))]
    public void ListIoSystems_network_interface_refusal_names_the_declared_item_path(string release)
    {
        var engine = NativeFixture(release);
        using var selected = HardwareContract.UseRelease(release);
        var service = new HardwareService((operation, _) => {
            Assert.Equal("hardware-network.HardwareReadIoSystems", operation);
            return JsonSerializer.SerializeToNode(engine.HardwareReadIoSystems(devicePathJson: new[] { "PLC" }, itemPathJson: new[] { "CPU" }));
        }, () => true, () => "fixture");
        var body = new HardwareTools(service).ListIoSystems(devicePath: new[] { "PLC" }, itemPath: new[] { "CPU" }).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Contains("itemPath", (string?)body["error"]?["message"], StringComparison.Ordinal);
        Assert.DoesNotContain("itemPathJson", (string?)body["error"]?["message"], StringComparison.Ordinal);
        Assert.Contains("NetworkInterface", (string?)body["error"]?["message"], StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_participant_names_participantDevicePath(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        Refusal(ForbiddenTools().ManageNetworkDomain("PN_IE_1", "sync", "addParticipant", "Sync")
            .StructuredContent!, "participantDevicePath", "addParticipant");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_channel_identity_names_channelType(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        foreach (var identity in new[] { (IoType: "", Number: -1), (IoType: "Input", Number: 0) })
            Refusal(ForbiddenTools().SetDeviceItemChannel(new[] { "PLC" }, new[] { "CPU" }, "", identity.IoType, identity.Number,
                V4Json.Deserialize<AttributeMap<Scalar>>("{\"ChannelAddress\":1}"), dryRun: false).StructuredContent!,
                "channelType", "channelNumber");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Empty_channel_attributes_names_attributes(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        Refusal(ForbiddenTools().SetDeviceItemChannel(new[] { "PLC" }, new[] { "CPU" }, "Digital", "Input", 0,
            V4Json.Deserialize<AttributeMap<Scalar>>("{}"), dryRun: false).StructuredContent!, "attributes", "nonempty");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_port_partner_names_partnerDevicePath(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        foreach (string action in new[] { "connect", "disconnect" })
            Refusal(ForbiddenTools().ManagePortInterconnection(new[] { "PLC" }, new[] { "CPU" }, action, dryRun: false)
                .StructuredContent!, "partnerDevicePath", "partnerDevicePath");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Device_item_refusal_names_the_declared_item_path(string release)
    {
        var engine = NativeFixture(release);
        using var selected = HardwareContract.UseRelease(release);
        var service = new HardwareService((operation, _) => {
            Assert.Equal("hardware-network.HardwareReadDeviceItemChannels", operation);
            return JsonSerializer.SerializeToNode(engine.HardwareReadDeviceItemChannels(new[] { "PLC" }, Array.Empty<string>()));
        }, () => true, () => "fixture");
        Refusal(new HardwareTools(service).ListDeviceItemChannels(new[] { "PLC" }, Array.Empty<string>()).StructuredContent!,
            "itemPath", "DeviceItem");
    }

    [Theory]
    [InlineData("localInterfaceItemPathJson", "localInterfaceItemPath")]
    [InlineData("partnerInterfaceItemPathJson", "partnerInterfaceItemPath")]
    public void Connection_refusal_names_the_declared_item_path(string internalName, string declaredName)
    {
        var item = new adaptertests::Siemens.Engineering.HW.DeviceItem();
        var network = new adaptertests::Siemens.Engineering.HW.Features.NetworkInterface();
        network.Nodes.Add(new adaptertests::Siemens.Engineering.HW.Node { Name = "First" });
        network.Nodes.Add(new adaptertests::Siemens.Engineering.HW.Node { Name = "Second" });
        item.Services[typeof(adaptertests::Siemens.Engineering.HW.Features.NetworkInterface)] = network;
        var engine = new NativeEngine("21");
        var method = typeof(NativeEngine).GetMethod("HardwareExactInterfaceNode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var thrown = Assert.Throws<TargetInvocationException>(() => method.Invoke(engine, new object[] { item, "", internalName }));
        var refusal = Assert.IsType<ArgumentException>(thrown.InnerException);
        Assert.Equal(declaredName, refusal.ParamName);
        Assert.Contains(declaredName, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PathJson", refusal.Message, StringComparison.Ordinal);

        using var worker = new BoundWorker();
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var service = new enginehost::TiaMcpServer.Siemens.Services.HardwareServicesPortService((_, _) => JsonSerializer.SerializeToNode(new {
            Message = "ManageCommunicationConnection failed", Meta = new { operationSuccess = false, mayHaveChanged = false,
                status = "InvalidParams", parameter = refusal.ParamName, nativeMessage = refusal.Message }
        }), () => true, () => "fixture");
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.HardwareServicesPortTools(service);
        Refusal(tools.ManageCommunicationConnection(new[] { "PLC" }, Array.Empty<string>(), "create", "S7Connection")
            .StructuredContent!, declaredName, "2 nodes");
    }

    [Theory, MemberData(nameof(Releases))]
    public void EnsureSubnet_apply_with_an_unknown_type_is_an_argument_refusal(string release)
    {
        var engine = NativeFixture(release);
        using var worker = new BoundWorker();
        using var selected = HardwareContract.UseRelease(release);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        var tool = new HardwareTools(new HardwareService((operation, _) => {
            calls++;
            Assert.Equal("hardware-network.HardwareEnsureSubnet", operation);
            return JsonSerializer.SerializeToNode(engine.HardwareEnsureSubnet("PLC/CPU", "Unknown", "PN_IE_1"));
        }, () => true, () => "C:/Fixture/Fixture.ap21"));
        var preview = tool.EnsureSubnet("PLC/CPU", "Unknown", "PN_IE_1").StructuredContent!;
        var body = tool.EnsureSubnet("PLC/CPU", "Unknown", "PN_IE_1", dryRun: false,
            expectedPlanHash: (string)preview["data"]!["planHash"]!,
            expectedProjectFile: (string)preview["data"]!["targetIdentity"]!["identity"]!["projectPath"]!).StructuredContent!;
        Refusal(body, "subnetType", "PROFINET");
        foreach (string allowed in new[] { "PROFINET", "PN", "IndustrialEthernet", "Industrial Ethernet", "PN/IE" })
            Assert.Contains(allowed, (string)body["error"]!["message"]!, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.Equal(0, engine.HardwareEditAccessCalls);
        Assert.False((bool?)body["data"]?["mayHaveChanged"]);

        var accepted = engine.HardwareEnsureSubnet("PLC/CPU", "PN", "PN_IE_1");
        Assert.Equal("NOT_FOUND", accepted.Meta["failureCode"]);
        Assert.Equal(1, engine.HardwareEditAccessCalls);
    }

    private static HardwareTools ForbiddenTools() => new(new HardwareService((_, _) =>
        throw new InvalidOperationException("No native dispatch is expected."), () => true, () => "fixture"));

    private static NativeEngine NativeFixture(string release)
    {
        var factory = typeof(adaptertests::TiaMcp.Adapters.Contracts.Tests.HardwareWriteFailureTests)
            .GetMethod("NativeFixture", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fixture = (System.Runtime.CompilerServices.ITuple)factory.Invoke(null, new object[] { release, true })!;
        return (NativeEngine)fixture[0]!;
    }

    private static void Refusal(JsonNode body, string parameter, string reason)
    {
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/" + parameter, (string?)body["error"]?["details"]?["parameter"]);
        Assert.Contains(reason, (string?)body["error"]?["message"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PathJson", (string?)body["error"]?["message"], StringComparison.Ordinal);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
    }

    private sealed class BoundWorker : enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding { get; } = new JsonObject { ["identity"] = new JsonObject {
            ["processId"] = 123, ["projectPath"] = "C:/Fixture/Fixture.ap21" } };
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview,
            CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
