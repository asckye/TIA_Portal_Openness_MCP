using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

// ConnectProjectToWorkspace puts the UngroupedDevicesGroup devices under one fixed workspace folder (decided 2026-10-19).
public sealed class VciWorkspaceLayoutTests
{
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

    [Fact]
    public void Refused_and_unmappable_ungrouped_devices_are_reported()
    {
        Assert.Equal("P/UngroupedDevices/IM1 | FAILED: VCI refused this ungrouped device (workspace folder UngroupedDevices): The object is not supported",
            VciWorkspaceLayout.NativeRefusal("P/UngroupedDevices/IM1", "The object is not supported"));
        Assert.Equal("P/UngroupedDevices/IM1 | not supported by VCI: no object of this ungrouped device can be mapped (workspace folder UngroupedDevices)",
            VciWorkspaceLayout.NothingMappable("P/UngroupedDevices/IM1"));
    }
}
