using Siemens.Engineering.HW;
using TiaMcp.Adapters.Native.Hardware;
using Xunit;

public sealed class HardwareProbeRootTests
{
    [Fact]
    public void Printed_paths_resolve_on_every_root()
    {
        Device Station(string name) => new() { Name = name, DeviceItems = new() {
            new() { Name = "CPU", DeviceItems = new() { new() { Name = "PROFINET 接口_1" } } } } };
        var root = Station("Root");
        var grouped = Station("Station/C%");
        var loose = Station("Loose");
        var nested = new DeviceUserGroup { Name = "Sub/B" }; nested.Devices.Add(grouped);
        var groups = new[] { new DeviceUserGroup { Name = "Group/A%", Groups = new() { nested } } };
        var ungrouped = new DeviceGroup(); ungrouped.Devices.Add(loose);
        var devices = new[] { root };
        foreach (var device in new[] { root, grouped, loose })
        {
            var prefix = ProjectDeviceWalk.DevicePathPrefix(devices, groups, ungrouped, device);
            Assert.NotNull(prefix);
            Assert.Same(device.DeviceItems[0].DeviceItems[0],
                ProjectDeviceWalk.FindItem(devices, groups, ungrouped, prefix + "/CPU/PROFINET 接口_1"));
        }
        Assert.Equal("device-groups/Group%2FA%25/groups/Sub%2FB/devices/Station%2FC%25",
            ProjectDeviceWalk.DevicePathPrefix(devices, groups, ungrouped, grouped));
        Assert.Null(ProjectDeviceWalk.DevicePathPrefix(devices, groups, ungrouped, Station("Absent")));
    }

    [Fact]
    public void Cpu_sharing_the_device_name_resolves_the_printed_path()
    {
        var port = new DeviceItem { Name = "端口_1" };
        var network = new DeviceItem { Name = "PROFINET 接口_1", DeviceItems = new() { port } };
        var cpu = new DeviceItem { Name = "PLC_A", DeviceItems = new() { network } };
        var devices = new[] { new Device { Name = "PLC_A", DeviceItems = new() { new() { Name = "导轨_0" }, cpu } } };
        Assert.Same(network, ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/PROFINET 接口_1"));
        Assert.Same(network, ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/PLC_A/PROFINET 接口_1"));
        Assert.Same(port, ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/PROFINET 接口_1/端口_1"));
        Assert.Null(ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/Missing"));
    }

    [Fact]
    public void Rack_child_resolves_through_its_associated_items()
    {
        var module = new DeviceItem { Name = "DI_1" };
        var rack = new DeviceItem { Name = "导轨_0", Items = new() { module } };
        var devices = new[] { new Device { Name = "PLC_A", DeviceItems = new() { rack, module } } };
        Assert.Same(module, ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/导轨_0/DI_1"));
        Assert.Same(module, ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/DI_1"));
        Assert.Null(ProjectDeviceWalk.FindItem(devices, null, null, "PLC_A/导轨_0/Missing"));
    }

    [Fact]
    public void Device_anchor_never_borrows_another_devices_item()
    {
        var cpu = new DeviceItem { Name = "A", DeviceItems = new() { new() { Name = "X" } } };
        var devices = new[] { new Device { Name = "A" }, new Device { Name = "B", DeviceItems = new() { cpu } } };
        Assert.Null(ProjectDeviceWalk.FindItem(devices, null, null, "A/X"));
    }

    [Theory]
    [InlineData("+S1-K1")]
    [InlineData("Station A")]
    [InlineData("+S1/Station A/+S1-K1")]
    [InlineData("device-groups/%2BS1/devices/Station%20A")]
    [InlineData("device-groups/%2BS1/devices/Station%20A/%2BS1-K1")]
    public void Grouped_probe_roots_resolve_by_name_and_device_group_path(string path)
    {
        var item = new DeviceItem { Name = "+S1-K1" };
        var device = new Device { Name = "Station A", DeviceItems = new() { item } };
        var group = new DeviceUserGroup { Name = "+S1" }; group.Devices.Add(device);
        Assert.Same(item, ProjectDeviceWalk.FindItem(Array.Empty<Device>(), new[] { group }, null, path));
    }

    [Theory]
    [InlineData("device-groups/Group%2FA/groups/Sub%20B/devices/Station%2FC/CPU%2F1")]
    [InlineData("CPU/1")]
    public void Nested_groups_and_escaped_names_preserve_segment_identity(string path)
    {
        var item = new DeviceItem { Name = "CPU/1" };
        var nested = new DeviceUserGroup { Name = "Sub B" };
        nested.Devices.Add(new Device { Name = "Station/C", DeviceItems = new() { item } });
        var group = new DeviceUserGroup { Name = "Group/A", Groups = new() { nested } };
        Assert.Same(item, ProjectDeviceWalk.FindItem(Array.Empty<Device>(), new[] { group }, null, path));
        Assert.Null(ProjectDeviceWalk.FindItem(Array.Empty<Device>(), new[] { group }, null, path + "/Missing"));
    }
}
