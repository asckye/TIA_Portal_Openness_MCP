extern alias enginehost;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class ProjectLifecycleAdmissionTests
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
    public void Registered_lifecycle_tools_are_not_refused_by_the_security_capability_gate(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"action\":\"read\"}")!;
        var declarations = Host.PortedToolDeclarations.Methods(release);
        foreach (var method in declarations)
        {
            var name = method.GetCustomAttributes(typeof(McpServerToolAttribute), false)
                .Cast<McpServerToolAttribute>().Single().Name!;
            if (PortedFamilies.ForTool(name).Name != "F05") continue;
            var tool = new FoundationTool(worker, release, method, name);
            Assert.Null(tool.RefusePortedCapability(arguments));
        }
        Assert.Equal(0, worker.Calls);
    }
}
