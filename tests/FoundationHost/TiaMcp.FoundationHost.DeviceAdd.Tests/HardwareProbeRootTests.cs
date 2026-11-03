using Siemens.Engineering.HW;
using TiaMcp.Adapters.Native.Hardware;
using Xunit;

public sealed class HardwareProbeRootTests
{
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
