using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hardware;
using Xunit;

public sealed class HardwareIpNodesTests
{
    [Fact]
    public void Probe_types_use_the_public_engineering_base_or_interface_and_hide_internal_names()
    {
        Assert.Equal("Siemens.Engineering.HW.ProbeTarget", HardwarePublicType.Name(new Siemens.Engineering.HW.ProbeTarget()));
        Assert.Equal("Siemens.Engineering.HW.ProbeTarget", HardwarePublicType.Name(new InternalProbeTarget()));
        Assert.Equal("Siemens.Engineering.HW.ProbeTarget", HardwarePublicType.Name(new Siemens.Engineering.HW.Internal.ProbeTarget()));
        Assert.Equal("Siemens.Engineering.HW.IProbeService", HardwarePublicType.Name(new InternalProbeService()));
        Assert.Equal("<unknown public engineering type>", HardwarePublicType.Name(new object()));
    }

    private sealed class InternalProbeTarget : Siemens.Engineering.HW.ProbeTarget { }
    private sealed class InternalProbeService : Siemens.Engineering.HW.IProbeService { }

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

namespace Siemens.Engineering.HW
{
    public class ProbeTarget { }
    public interface IProbeService { }
}

namespace Siemens.Engineering.HW.Internal
{
    public sealed class ProbeTarget : HW.ProbeTarget { }
}
