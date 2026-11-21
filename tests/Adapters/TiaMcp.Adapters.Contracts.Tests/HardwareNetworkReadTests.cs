using System.Runtime.InteropServices;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HardwareNetworkReadTests
{
    public static IEnumerable<object[]> Releases => HardwareFeatureReadTests.Releases;
    public static IEnumerable<object[]> EnumerationFailures => Releases.SelectMany(row =>
        new[] { "enumeration", "mid-enumeration", "lost-enumeration", "lost-mid-enumeration", "lost-attribute" }
            .Select(failure => new object[] { row[0], failure }));

    [Theory, MemberData(nameof(Releases))]
    public void Unreadable_network_attribute_keeps_its_name_and_unknown_value(string release)
    {
        var attributes = Assert.IsType<List<HardwareNetworkAttribute>>(ReadReply(release, "attribute"));
        Assert.Equal(new[] { "Address", "MacAddress" }, attributes.Select(a => a.Name));
        Assert.Equal("192.0.2.1", attributes[0].Value);
        Assert.Null(attributes[1].Value);
    }

    [Theory, MemberData(nameof(EnumerationFailures))]
    public void Failed_network_observation_propagates(string release, string failure)
    {
        if (failure.StartsWith("lost-", StringComparison.Ordinal))
            Assert.Throws<COMException>(() => ReadReply(release, failure));
        else
            Assert.Throws<InvalidOperationException>(() => ReadReply(release, failure));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_unbound_network_read_has_a_project_refusal(string release)
    {
        var reply = Assert.IsType<Dictionary<string, object?>>(ReadReply(release, "no-project"));
        Assert.Equal("PROJECT_NOT_BOUND", reply["failureCode"]);
    }

    [Fact]
    public void Topology_prefers_the_item_path_and_preserves_each_distinct_interface_and_node()
    {
        var first = new object();
        var second = new object();
        var node = new object();
        var paths = Hardware.HardwareNetworkPolicy.TopologyNodes(new[] {
            (first, node, "Rack/CPU/Port/PN", true), (first, node, "Rack/CPU/PN", false),
            (first, new object(), "Rack/CPU/PN", false), (second, node, "Rack/CPU/PN_2", false)
        }).ToArray();
        Assert.Equal(3, paths.Length);
        Assert.False(paths[0].Component);
        Assert.Equal("Rack/CPU/PN", paths[0].Path);
        Assert.Same(second, paths[2].Interface);
    }

    [Theory]
    [InlineData(false, "CPU/PN")]
    [InlineData(true, "devices/Station/CPU/PN")]
    public void Unlocated_device_keeps_a_relative_item_path(bool located, string expected)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var device = (Device)fixture.Engine.FamilyObject;
        device.Name = "Station";
        device.DeviceItems.Add(fixture.Cpu);
        if (located) fixture.Project.Devices.Add(device);
        fixture.Engine.NetworkNodes.Add((new object(), "CPU/PN"));
        var result = fixture.Engine.ReadHardwareIpAddress("Station");
        Assert.Equal(expected, Assert.Single(result.Nodes).InterfacePath);
    }

    public static object? ReadReply(string release, string failure)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release, bindProject: failure != "no-project");
        fixture.Engine.RollbackCpu = new NetworkItem(failure);
        return fixture.Engine.HardwareGetDeviceItemNetworkInfo("PLC/CPU");
    }

    private sealed class NetworkItem(string failure) : HardwareObject
    {
        private Exception Error() => failure.StartsWith("lost-", StringComparison.Ordinal)
            ? new COMException("The portal connection was lost.") : new InvalidOperationException("Attribute observation failed.");

        public override IEnumerable<EngineeringAttributeInfo> GetAttributeInfos()
        {
            if (failure is "enumeration" or "lost-enumeration") throw Error();
            return Enumerate();
        }

        private IEnumerable<EngineeringAttributeInfo> Enumerate()
        {
            yield return new EngineeringAttributeInfo { Name = "Address" };
            if (failure is "mid-enumeration" or "lost-mid-enumeration") throw Error();
            yield return new EngineeringAttributeInfo { Name = "MacAddress" };
            yield return new EngineeringAttributeInfo { Name = "Temperature" };
        }

        public override object GetAttribute(string name)
        {
            if (name == "MacAddress" && failure is "attribute" or "lost-attribute") throw Error();
            return name == "Address" ? "192.0.2.1" : "00-00-5E-00-53-01";
        }
    }
}
