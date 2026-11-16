using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class LowBetaRegressionTests
{
    [Fact]
    public void Subnet_allowed_values_are_exposed_as_a_read_only_list()
    {
        object values = TiaMcp.Adapters.Hardware.SubnetTypePolicy.AllowedValues;
        Assert.IsAssignableFrom<IReadOnlyList<string>>(values);
        Assert.False(values is string[]);
        Assert.True(Assert.IsAssignableFrom<ICollection<string>>(values).IsReadOnly);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("deviceItem")]
    public void Engineering_selection_messages_use_declared_path_names(string kind)
    {
        var error = Assert.Throws<ArgumentException>(() => EngineeringServicesPolicy.ObjectSelection(kind,
            Array.Empty<string>(), Array.Empty<string>(), "", "", ""));
        Assert.Contains("devicePath", error.Message);
        Assert.DoesNotContain("PathJson", error.Message);
        error = Assert.Throws<ArgumentException>(() => EngineeringServicesPolicy.ObjectSelection("deviceItem",
            new[] { "PLC" }, Array.Empty<string>(), "", "", ""));
        Assert.Contains("itemPath", error.Message);
        Assert.DoesNotContain("PathJson", error.Message);
    }

    [Theory]
    [InlineData("exportOpcUa", true)]
    [InlineData("exportOpcUa", false)]
    [InlineData("exportCardReaderPsc", true)]
    [InlineData("exportCardReaderPsc", false)]
    public void Wrong_export_owner_is_refused_before_any_write_on_preview_and_apply(string action, bool dryRun)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        string file = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + (action == "exportOpcUa" ? ".xml" : ".psc"));
        var reply = fixture.Engine.HardwareManageHardwareUtilities(action, devicePathJson: new[] { "PLC" },
            itemPathJson: action == "exportOpcUa" ? Array.Empty<string>() : new[] { "CPU" }, filePath: file, dryRun: dryRun);
        Assert.Equal("InvalidParams", reply.Meta["status"]);
        Assert.Equal("itemPath", reply.Meta["parameter"]);
        Assert.False(reply.MayHaveChanged);
        Assert.False(reply.OutcomeUnknown);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("DownloadPlcToFolder", true)]
    [InlineData("ManagePlcDataBlockSnapshot", false)]
    public void File_only_online_exports_never_claim_an_unknown_project_write(string operation, bool changed)
    {
        Assert.False(OnlineCompletionPolicy.OutcomeUnknown(new OnlineRequest { Operation = operation, Action = "exportSnapshot", DryRun = false },
            false, true, changed, true, false, null));
    }
}
