using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hardware;
using Xunit;

public sealed class DevicePathResolverTests
{
    private sealed class Fixture
    {
        internal readonly Device Root = new() { Name = "Root" };
        internal readonly Device Nested = new() { Name = "S7-1500/ET200MP station_1%" };
        internal readonly Device Loose = new() { Name = "Loose" };
        internal readonly DeviceUserGroup Group = new() { Name = "G/A%" };
        internal readonly DeviceUserGroup Sub = new() { Name = "Sub/B%" };
        internal readonly DeviceGroup Ungrouped = new();
        internal Fixture() { Sub.Devices.Add(Nested); Group.Groups.Add(Sub); Ungrouped.Devices.Add(Loose); }
        internal Device[] Roots => new[] { Root };
        internal DeviceUserGroup[] Groups => new[] { Group };
        internal IEnumerable<(Device Device, string[] Path)> Paths => new[] {
            (Root, new[] { Root.Name }), (Nested, new[] { Group.Name, Sub.Name, Nested.Name }),
            (Loose, new[] { ProjectDeviceWalk.UngroupedSegment, Loose.Name }) };
    }

    [Fact]
    public void Canonical_printed_device_paths_resolve()
    {
        var f = new Fixture();
        foreach (var (device, _) in f.Paths)
        {
            string prefix = ProjectDeviceWalk.DevicePathPrefix(f.Roots, f.Groups, f.Ungrouped, device)!;
            Assert.Same(device, ProjectDeviceWalk.FindDevice(f.Roots, f.Groups, f.Ungrouped, prefix));
            Assert.Same(device, ProjectDeviceWalk.ResolveDevice(f.Roots, f.Groups, f.Ungrouped, new[] { prefix }, false));
        }
    }

    [Fact]
    public void Joined_array_of_slash_names_resolves()
    {
        var device = new Device { Name = "S7-1500/ET200MP station_1" };
        var group = new DeviceUserGroup { Name = "G" }; group.Devices.Add(device);
        Assert.Same(device, ProjectDeviceWalk.FindDevice(Array.Empty<Device>(), new[] { group }, null, "G/" + device.Name));
    }

    [Fact]
    public void String_and_array_entries_agree()
    {
        var f = new Fixture();
        foreach (var (device, path) in f.Paths)
        {
            Assert.Same(device, ProjectDeviceWalk.ResolveDevice(f.Roots, f.Groups, f.Ungrouped, path, false));
            Assert.Same(device, ProjectDeviceWalk.FindDevice(f.Roots, f.Groups, f.Ungrouped, string.Join("/", path)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ambiguous_string_path_is_refused_with_candidates(bool text)
    {
        var group = new DeviceUserGroup { Name = "G" }; group.Devices.Add(new() { Name = "Dup" });
        var root = new[] { new Device { Name = "Dup" } };
        var error = Assert.Throws<PlcSoftwareException>(() => text
            ? ProjectDeviceWalk.FindDevice(root, new[] { group }, null, "Dup")
            : ProjectDeviceWalk.ResolveDevice(root, new[] { group }, null, new[] { "Dup" }, false));
        Assert.Equal("Ambiguous", error.Code);
        Assert.Equal("Dup", error.Data["target"]);
        Assert.Equal(new[] { "Dup", "G/Dup" }, error.Candidates);
        Assert.Contains("Device path Dup matches more than one device: Dup; G/Dup.", error.Message);
    }

    [Fact]
    public void Cpu_alias_applies_to_text_only()
    {
        var device = new Device { Name = "Station", DeviceItems = new() { new() { Name = "PLC_1" } } };
        Assert.Same(device, ProjectDeviceWalk.FindDevice(new[] { device }, null, null, "PLC_1"));
        Assert.Null(ProjectDeviceWalk.ResolveDevice(new[] { device }, null, null, new[] { "PLC_1" }, false));
        var exact = new Device { Name = "PLC_1" };
        Assert.Same(exact, ProjectDeviceWalk.FindDevice(new[] { device, exact }, null, null, "PLC_1"));
    }

    [Fact]
    public void Grouped_array_reads_no_unrelated_group()
    {
        var device = new Device { Name = "D" };
        var group = new DeviceUserGroup { Name = "G" }; group.Devices.Add(device);
        IEnumerable<DeviceUserGroup> Groups()
        {
            yield return group;
            throw new InvalidOperationException("An unrelated group was read.");
        }
        Assert.Same(device, ProjectDeviceWalk.ResolveDevice(null, Groups(), null, new[] { "G", "D" }, false));
    }

    [Fact]
    public void Missing_device_lists_up_to_twenty_candidates()
    {
        var devices = Enumerable.Range(0, 25).Select(i => new Device { Name = "D" + i }).ToArray();
        Assert.Null(ProjectDeviceWalk.ResolveDevice(devices, null, null, new[] { "G", "X" }, false));
        var error = ProjectDeviceWalk.MissingDevice(devices, null, null, new[] { "G", "X" });
        Assert.Equal("NotFound", error.Status);
        Assert.Equal("G/X", error.Data["target"]);
        Assert.Equal(devices.Take(20).Select(d => d.Name), Assert.IsType<string[]>(error.Data["candidates"]));
        Assert.StartsWith("Device not found: G/X. Candidates: D0; D1;", error.Message);
        Assert.DoesNotContain("D20", error.Message);
    }

    [Fact]
    public void Exact_arrays_keep_reserved_words_and_slashes_as_names()
    {
        var device = new Device { Name = "D/X" };
        var group = new DeviceUserGroup { Name = "devices" }; group.Devices.Add(device);
        Assert.Same(device, ProjectDeviceWalk.ResolveDevice(null, new[] { group }, null, new[] { "devices", "D/X" }, false));
        Assert.Null(ProjectDeviceWalk.ResolveDevice(null, new[] { group }, null, new[] { "devices", "D", "X" }, false));
    }

    [Fact]
    public void Repeated_locations_of_one_device_are_not_ambiguous()
    {
        var device = new Device { Name = "D" };
        var group = new DeviceUserGroup { Name = "G" }; group.Devices.Add(device);
        Assert.Same(device, ProjectDeviceWalk.FindDevice(new[] { device, device }, new[] { group }, null, "d"));
    }
}
