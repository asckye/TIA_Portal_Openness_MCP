using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class B4HostPortsTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void RegistryIncludesExactlyTheUsableB4Tools(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var families = PortedFamilies.All.Where(family => family.Name is "F12" or "F13" or "F16");
        var names = LegacyHostToolRegistry.Create(worker, release, false).Select(tool => tool.ProtocolTool.Name).ToArray();
        foreach (var name in families.SelectMany(family => family.Tools))
            Assert.Equal(PortedFamilies.Available(release, name), names.Contains(name));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("16", "ManagePlcTableEntries", "{\"softwarePath\":\"PLC\",\"tableKind\":\"watch\",\"tablePath\":\"Watch\",\"action\":\"read\"}")]
    [InlineData("20", "ManageMotionAxis", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"createMapping\"}")]
    [InlineData("20", "ManageMotionAxis", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"connectIdent\"}")]
    [InlineData("15.1", "ManageMotionAxis", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"addMasterValue\",\"aspect\":\"conveyorLeading\",\"name\":\"Master\"}")]
    [InlineData("14sp1", "ConfigureMotionHardwareConnection", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"interfaceKind\":\"torque\",\"action\":\"read\"}")]
    [InlineData("18", "GetMotionAxisConfiguration", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Group/Axis\"}")]
    [InlineData("18", "GetTechnologyObjectTree", "{\"softwarePath\":\"PLC\",\"groupPath\":\"Group\"}")]
    [InlineData("17", "ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"read\",\"name\":\"Unit\",\"unitKind\":\"safety\"}")]
    [InlineData("17", "ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"update\",\"name\":\"Unit\",\"properties\":{\"NamespacePreset\":\"Ns\"}}")]
    [InlineData("19", "ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"createFromMasterCopy\",\"name\":\"Unit\",\"libraryName\":\"Library\",\"masterCopyPath\":\"Master\",\"copyMode\":\"Replace\"}")]
    public async Task UnavailableActionsRefuseBeforeWorkerStateAndNativeLookup(string release, string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(tool => tool.ProtocolTool.Name == name);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, worker.Calls);
    }
}
