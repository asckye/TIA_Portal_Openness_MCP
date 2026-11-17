using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Libraries;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class LibraryCapabilitiesTests
{
    [Theory]
    [InlineData("14sp1", false)][InlineData("15.1", false)][InlineData("16", false)][InlineData("17", false)]
    [InlineData("18", false)][InlineData("19", false)][InlineData("20", true)][InlineData("21", true)]
    public void Instantiation_modes_follow_the_official_overloads(string release, bool modes)
    {
        Assert.Null(LibraryCapabilities.Unsupported(release, "ManageLibraryMasterCopy", "instantiate"));
        Assert.Null(LibraryCapabilities.Unsupported(release, "ManageLibraryTypeVersion", "instantiate"));
        foreach (string mode in LibraryPolicy.CopyModes)
            Assert.Equal(modes, LibraryCapabilities.Unsupported(release, "ManageLibraryMasterCopy", "instantiate", copyMode: mode) == null);
        foreach (string mode in LibraryPolicy.UpdatePathsModes)
            Assert.Equal(modes, LibraryCapabilities.Unsupported(release, "ManageLibraryTypeVersion", "instantiate", updatePathsMode: mode) == null);
    }

    [Theory]
    [InlineData("block")][InlineData("blockGroup")][InlineData("tagTable")][InlineData("device")]
    [InlineData("classicScreen")][InlineData("classicTagTable")]
    public void Empty_target_group_selects_root_for_container_targets(string kind)
        => LibraryPolicy.ValidateInstantiationTarget(kind, kind == "device" ? "" : "ExactSoftware", "", "");

    [Fact]
    public void Device_target_navigation_preserves_root_and_decodes_each_name_after_splitting()
    {
        var child = new DeviceGroup { Name = "50%Group" };
        var parent = new DeviceGroup { Name = "Folder/One", Groups = new[] { child } };
        var root = new { DeviceGroups = new[] { parent } };
        string[] Parts(string path) => PlcObjectPath.Parts(path, true, "targetGroupPath");
        Assert.Same(root, HardwareGroupOperations.Group(root, "", "DeviceGroups", Parts));
        Assert.Same(child, HardwareGroupOperations.Group(root, "Folder%2FOne/50%25Group", "DeviceGroups", Parts));
        Assert.Throws<HardwareAddressingException>(() => HardwareGroupOperations.Group(root, "Folder/One", "DeviceGroups", Parts));
    }

    private sealed class DeviceGroup
    {
        public string Name { get; set; } = "";
        public DeviceGroup[] Groups { get; set; } = Array.Empty<DeviceGroup>();
    }

    [Fact]
    public void Instantiation_refuses_unknown_kinds_missing_software_and_missing_tag_tables()
    {
        Assert.Throws<ArgumentException>(() => LibraryPolicy.ValidateInstantiationTarget("screenItem", "HMI", "", ""));
        Assert.Throws<ArgumentException>(() => LibraryPolicy.ValidateInstantiationTarget("block", "", "", ""));
        Assert.Throws<ArgumentException>(() => LibraryPolicy.ValidateInstantiationTarget("device", "PLC", "", ""));
        Assert.Throws<ArgumentException>(() => LibraryPolicy.ValidateInstantiationTarget("tag", "PLC", "", ""));
        LibraryPolicy.ValidateInstantiationTarget("tag", "PLC", "Group/Table", "Rename");
        Assert.Throws<ArgumentException>(() => LibraryPolicy.ValidateInstantiationTarget("block", "PLC", "", "rename"));
        foreach (string kind in new[] { "device", "classicScreen", "classicTagTable" })
            Assert.Throws<NotSupportedException>(() => LibraryPolicy.ValidateInstantiationTarget(kind, kind == "device" ? "" : "HMI", "", "Rename"));
    }

    [Fact]
    public void Version_classes_choose_only_supported_targets_and_modes()
    {
        Assert.Equal("block", LibraryPolicy.InstantiationVersionKind("CodeBlockLibraryTypeVersion", ""));
        Assert.Equal("classicScreen", LibraryPolicy.InstantiationVersionKind("ScreenLibraryTypeVersion", ""));
        Assert.Equal("block", LibraryPolicy.InstantiationVersionKind("CodeBlockLibraryTypeVersion", "ThrowIfPathsConflict"));
        Assert.Throws<NotSupportedException>(() => LibraryPolicy.InstantiationVersionKind("ScreenLibraryTypeVersion", "ThrowIfPathsConflict"));
        Assert.Throws<ArgumentException>(() => LibraryPolicy.InstantiationVersionKind("CodeBlockLibraryTypeVersion", "Guess"));
        Assert.Throws<NotSupportedException>(() => LibraryPolicy.InstantiationVersionKind("OtherVersion", ""));
        foreach (string kind in new[] { "PlcTypeLibraryTypeVersion", "PlcDocumentLibraryTypeVersion" })
            Assert.Contains("ManagePlcDocuments action=createFromLibraryType",
                Assert.Throws<NotSupportedException>(() => LibraryPolicy.InstantiationVersionKind(kind, "")).Message);
    }

    [Fact]
    public void Overview_counts_stop_at_the_cap()
    {
        var result = LibraryCapabilities.OverviewCounts(51, 1, 50);
        Assert.Equal(50, result["typeItems"]);
        Assert.Equal(1, result["masterCopyItems"]);
        Assert.Equal(true, result["truncated"]);
        Assert.Equal(false, result["dataComplete"]);
        Assert.Equal(true, LibraryCapabilities.OverviewCounts(50, 1, 50)["dataComplete"]);
    }
}
