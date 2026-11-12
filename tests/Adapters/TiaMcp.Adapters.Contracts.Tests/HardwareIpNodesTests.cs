using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class HardwareIpNodesTests
{
    [Fact]
    public void One_node_printed_in_twelve_forms_is_one_row()
    {
        var forms = new[] { "PLC_A#PROFINET 接口_1", "PLC_A/PROFINET 接口_1",
            "PLC_A/PROFINET 接口_1#端口_1", "PLC_A/PROFINET 接口_1/端口_1",
            "PLC_A/PROFINET 接口_1#端口_2", "PLC_A/PROFINET 接口_1/端口_2" };
        var nodes = forms.Concat(forms.Select(path => "导轨_0/" + path)).Reverse().Select(path => Node(path));
        var result = Assert.Single(HardwareIpNodes.Unique(nodes));
        Assert.Equal("PLC_A/PROFINET 接口_1", result.InterfacePath);
        Assert.Equal("192.0.2.1", result.Address);
    }

    [Fact]
    public void Two_interfaces_stay_two_rows()
    {
        var first = Node("PLC_A/PROFINET 接口_1");
        var second = Node("PLC_A/PROFINET 接口_2"); second.NodeName = "X2";
        Assert.Equal(new[] { first, second }, HardwareIpNodes.Unique(new[] { first, second }));
    }

    [Fact]
    public void Unaddressed_interfaces_stay_apart()
    {
        var nodes = new[] { Node("PLC_A/PROFINET 接口_1"), Node("PLC_A/PROFINET 接口_2") };
        foreach (var node in nodes) { node.Address = ""; node.ConnectedSubnet = ""; }
        Assert.Equal(nodes, HardwareIpNodes.Unique(nodes));
    }

    private static HardwareIpNode Node(string path) => new() { NodeName = "X1", Address = "192.0.2.1",
        NodeType = "Ethernet", ConnectedSubnet = "PN/IE_1", InterfacePath = path, IsIndustrialEthernet = true };
}
