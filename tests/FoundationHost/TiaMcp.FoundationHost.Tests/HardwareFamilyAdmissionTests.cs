extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class HardwareFamilyAdmissionTests
{
    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Unattached_connection_refuses_without_claiming_an_unknown_write(string release)
    {
        using var selected = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease(release);
        var service = new enginehost::TiaMcpServer.Siemens.Services.HardwareNetworkPortService(
            (_, _) => throw new InvalidOperationException("No native dispatch is expected."), () => false, () => "");
        var tool = new enginehost::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(service);
        var result = tool.ConnectDeviceNodesToProfinetSubnet("Fixture/CPU", "Fixture/HMI");
        Assert.Equal("PROJECT_NOT_BOUND", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }

    public static IEnumerable<object[]> CapabilityBranches()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
            foreach (var branch in new[] {
                ("ExchangeSystemDiagnosticsSettings", "export", "", -1, false, 17),
                ("CreateHardwareCatalogDevice", "", "", -1, false, 18),
                ("ListTransferAreas", "", "", -1, false, 15),
                ("ListNetworkDomains", "", "", -1, false, 16),
                ("ManageTransferArea", "create", "", 1, false, 16),
                ("ManageTransferArea", "create", "multicast", -1, false, 20),
                ("ManageDeviceServiceObjects", "read", "webApplications", -1, false, 21),
                ("ManageDeviceServiceObjects", "export", "telecontrolDataPoints", -1, false, 20),
                ("ManageDeviceServiceObjects", "import", "telecontrolDataPoints", -1, false, 20),
                ("ManageDeviceServiceObjects", "read", "telecontrolDataPoints", -1, false, 21),
                ("ManageDeviceServiceObjects", "read", "certificateServices", -1, false, 18),
                ("ManageHardwareUtilities", "normalizeTypeIdentifier", "", -1, false, 17),
                ("ManageHardwareUtilities", "exportCardReaderPsc", "", -1, false, 15),
                ("ManageHardwareUtilities", "exportCardReaderPsc", "", -1, true, 20),
                ("ListCommunicationConnections", "", "", -1, false, 21) })
                yield return new object[] { release, branch.Item1, branch.Item2, branch.Item3,
                    branch.Item4, branch.Item5, version < branch.Item6 };
        }
    }

    [Theory, MemberData(nameof(CapabilityBranches))]
    public void Api_availability_is_independent_of_session_and_native_acceptance(string release, string tool,
        string action, string family, int position, bool password, bool unsupported)
    {
        Assert.Equal(unsupported, HardwareCapabilities.Unsupported(release, tool, action, family, position,
            hasPassword: password) != null);
        string workerOperation = tool switch {
            "CreateHardwareCatalogDevice" => "HardwareCreateCatalogDevice",
            "ListNetworkDomains" => "HardwareReadNetworkDomains",
            "ListTransferAreas" => "HardwareReadTransferAreas",
            "ListCommunicationConnections" => "HardwareReadCommunicationConnections",
            _ => "Hardware" + tool };
        Assert.Equal(unsupported, HardwareCapabilities.Unsupported(release, "hardware-network." + workerOperation,
            action, family, position, hasPassword: password) != null);
    }

    [Theory]
    [InlineData("14sp1", "ListTransferAreas", "{\"devicePath\":[\"PLC\"],\"itemPath\":[]}")]
    [InlineData("15.1", "ListNetworkDomains", "{\"subnetName\":\"PN\"}")]
    [InlineData("16", "ExchangeSystemDiagnosticsSettings", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"action\":\"export\",\"filePath\":\"fixture.xml\"}")]
    [InlineData("17", "CreateHardwareCatalogDevice", "{\"keyword\":\"Fixture\",\"deviceName\":\"PLC\"}")]
    [InlineData("19", "ManageDeviceServiceObjects", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"family\":\"webApplications\"}")]
    [InlineData("20", "ManageDeviceServiceObjects", "{\"devicePath\":[\"PLC\"],\"itemPath\":[],\"family\":\"telecontrolDataPoints\"}")]
    public async Task Missing_api_refuses_before_ReadState(string release, string toolName, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == toolName);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = toolName, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void Array_and_scalar_reads_do_not_poison_the_session()
    {
        var state = new WorkerOutcomeState();
        state.AcceptResult("hardware-devices.HardwareLegacySearchInstalledGsdDevices", new(), new JsonArray());
        state.AcceptResult("hardware-devices.HardwareReadItemTree", new(), JsonValue.Create("fixture"));
        state.RequireUsable();
        Assert.False(state.Poisoned);
    }

    [Fact]
    public void Invalid_write_evidence_is_rejected_instead_of_promoted()
    {
        var reply = JsonNode.Parse("{\"Ok\":false,\"Message\":\"failed\",\"MayHaveChanged\":false,\"RequiresSessionReset\":true}");
        Assert.Throws<IOException>(() => HardwareFamilyWire.Validate("HardwarePlugModule", reply));
        reply!["MayHaveChanged"] = true;
        HardwareFamilyWire.Validate("HardwarePlugModule", reply);
    }
}
