using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class B5HostPortsTests
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
    public void RegistryIncludesExactlyTheUsableB5Tools(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var families = PortedFamilies.All.Where(family => family.Name is "F10" or "F14" or "F15");
        var names = LegacyHostToolRegistry.Create(worker, release, false).Select(tool => tool.ProtocolTool.Name).ToArray();
        foreach (var name in families.SelectMany(family => family.Tools))
            Assert.Equal(PortedFamilies.Available(release, name), names.Contains(name));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("15.1", "ManageOpcUaInterface", "{\"softwarePath\":\"PLC\",\"interfaceName\":\"Main\",\"interfaceType\":\"SimaticInterface\",\"action\":\"read\"}")]
    [InlineData("15.1", "GenerateOpcUaModelledInterface", "{\"softwarePath\":\"PLC\",\"interfaceName\":\"Main\",\"namespaceUri\":\"urn:fixture\",\"outputPath\":\"C:/Exports/Main.xml\",\"unitName\":\"Unit\"}")]
    [InlineData("17", "ExchangePlcAlarmTextLists", "{\"softwarePath\":\"PLC\",\"action\":\"export\",\"filePath\":\"C:/Exports/Main.xlsx\",\"unitName\":\"Safety\",\"unitKind\":\"safety\"}")]
    [InlineData("19", "ManagePlcAlarmTextList", "{\"softwarePath\":\"PLC\",\"action\":\"createFromMasterCopy\",\"masterCopyPath\":\"Master\",\"copyMode\":\"Replace\"}")]
    [InlineData("20", "ManagePlcDocuments", "{\"softwarePath\":\"PLC\",\"action\":\"createFromMasterCopy\",\"masterCopyPath\":\"Master\"}")]
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
