using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Vci;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

// ConnectProjectToWorkspace puts the UngroupedDevicesGroup devices under one fixed workspace folder (decided 2026-10-19).
public sealed class VciWorkspaceLayoutTests
{
    [Theory]
    [InlineData(@"\Motor.xml", "")]
    [InlineData(@"\PLC_1\Blocks\Line.Motor.xml", "PLC_1/Blocks")]
    [InlineData(@"PLC_1\Blocks\Line.Motor.xml", "PLC_1/Blocks")]
    public void Legacy_mapping_directory_resolves_from_the_workspace_root(string relativePath, string expectedFolder)
    {
        string root = Path.Combine(Path.GetTempPath(), "VCI");
        string expected = Path.Combine(root, expectedFolder.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expected, VciWorkspaceLayout.LegacyMappingDirectory(root, relativePath).FullName);
    }

    [Fact]
    public void The_ungrouped_folder_is_fixed_and_language_independent()
        => Assert.Equal("UngroupedDevices", VciWorkspaceLayout.UngroupedFolder);

    [Theory]
    [InlineData("UngroupedDevices")]
    [InlineData("ungroupeddevices")]
    [InlineData("UNGROUPEDDEVICES")]
    public void A_root_device_or_group_owning_the_folder_is_found(string folder)
    {
        var owner = VciWorkspaceLayout.UngroupedFolderOwner(new[] { ("PLC_1", "PLC_1"), ("Line A", folder), ("Line B", "UngroupedDevices") });
        Assert.Equal("Line A", owner);
    }

    [Fact]
    public void Other_folders_never_conflict()
    {
        Assert.Null(VciWorkspaceLayout.UngroupedFolderOwner(new[] { ("PLC_1", "PLC_1"), ("Ungrouped devices", "Ungrouped devices"),
            ("Nicht gruppierte Geraete", "Nicht gruppierte Geraete"), ("UngroupedDevices_1", "UngroupedDevices_1") }));
        Assert.Null(VciWorkspaceLayout.UngroupedFolderOwner(Array.Empty<(string, string)>()));
    }

    [Fact]
    public void The_conflict_is_a_typed_refusal_that_maps_nothing()
    {
        var refusal = VciWorkspaceLayout.UngroupedFolderTaken("Line A");
        Assert.IsType<AdapterPreconditionException>(refusal);
        Assert.False(refusal.IsArgument);
        Assert.Contains("'UngroupedDevices'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'Line A'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was mapped.", refusal.Message, StringComparison.Ordinal);
    }

    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true)]
    public void Format_refusals_descend_only_through_ordinary_containers(bool leafRefuses, bool ungroupedDevice)
    {
        var attempts = new VciExportAttempts();
        var visited = new List<string>();
        var mapped = new List<string>();
        var diagnostics = new List<string>();
        void Walk(string node, bool descendable)
        {
            visited.Add(node);
            var formats = attempts.ReadFormats(() => descendable || leafRefuses ? throw new InvalidOperationException(node + " refused")
                : new[] { "XML" }, descendable, descendable && ungroupedDevice, error => diagnostics.Add(error.Message));
            if (formats == null) return;
            if (formats.Count > 0) mapped.Add(node);
            else if (descendable) Walk("FC_1", false);
        }

        Walk("Device", true);
        Assert.Equal(ungroupedDevice ? new[] { "Device" } : new[] { "Device", "FC_1" }, visited);
        Assert.Equal(leafRefuses || ungroupedDevice ? Array.Empty<string>() : new[] { "FC_1" }, mapped);
        Assert.Equal(ungroupedDevice ? 0 : 1, attempts.UnsupportedContainers);
        Assert.Equal(leafRefuses || ungroupedDevice ? 1 : 0, attempts.QueryRefusals);
        Assert.Equal(leafRefuses ? new[] { "Device refused", "FC_1 refused" } : new[] { "Device refused" }, diagnostics);
    }

    [Fact]
    public void Refused_and_unmappable_ungrouped_devices_are_reported()
    {
        Assert.Equal("P/UngroupedDevices/IM1 | FAILED: VCI refused this ungrouped device (workspace folder UngroupedDevices): The object is not supported",
            VciWorkspaceLayout.NativeRefusal("P/UngroupedDevices/IM1", "The object is not supported"));
        Assert.Equal("P/UngroupedDevices/IM1 | not supported by VCI: no object of this ungrouped device can be mapped (workspace folder UngroupedDevices)",
            VciWorkspaceLayout.NothingMappable("P/UngroupedDevices/IM1"));
    }
}
