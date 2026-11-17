using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class EditorSelectionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("device")]
    [InlineData("network")]
    [InlineData("topology")]
    public void Device_views_use_only_the_official_selection(string view)
        => EngineeringServicesPolicy.EditorSelection("device", new[] { "Station" }, Array.Empty<string>(), "", "", view);

    [Theory]
    [InlineData("plcWatchTable")]
    [InlineData("plcForceTable")]
    public void Table_editor_support_starts_at_v15_1_without_extending_identifier_targets(string kind)
    {
        EngineeringServicesPolicy.EditorSelection(kind, Array.Empty<string>(), Array.Empty<string>(), "PLC", "组/表%2F一", "");
        Assert.NotNull(EngineeringServicesCapabilities.Unsupported("14sp1", "ShowObjectInEditor", kind));
        foreach (string release in new[] { "15.1", "16", "17", "18", "19", "20", "21" })
            Assert.Null(EngineeringServicesCapabilities.Unsupported(release, "ShowObjectInEditor", kind));
        Assert.Throws<ArgumentException>(() => EngineeringServicesPolicy.ObjectSelection(kind,
            Array.Empty<string>(), Array.Empty<string>(), "PLC", "Table", ""));
    }

    [Theory]
    [InlineData("plcBlock", "network")]
    [InlineData("plcWatchTable", "device")]
    [InlineData("plcForceTable", "topology")]
    [InlineData("deviceItem", "network")]
    [InlineData("device", "Network")]
    public void Invalid_view_combinations_are_refused(string kind, string view)
    {
        var error = Assert.Throws<ArgumentException>(() => EngineeringServicesPolicy.EditorSelection(kind,
            new[] { "Station" }, new[] { "CPU" }, "PLC", "Table", view));
        Assert.Equal("view", error.ParamName);
    }
}
