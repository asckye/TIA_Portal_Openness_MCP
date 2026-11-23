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

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void OpcUa_compile_refusal_preserves_the_native_failure_and_file_evidence(string release)
    {
        var fixture = ExportFixture(release);
        var cpu = fixture.Cpu;
        int exports = 0;
        const string message = "Error when calling method 'Export' of type 'Siemens.Engineering.HW.Utilities.OpcUaExportProvider'.";
        const string detail = "MCP_RC_PLC must be successfully compiled.";
        fixture.Project.HwUtilities.Add(new Siemens.Engineering.HW.Utilities.OpcUaExportProvider {
            Identifier = "OPCUAExportProvider", OnExport = (item, _) => {
                exports++;
                Assert.Same(cpu, item);
                var failure = new Siemens.Engineering.EngineeringException(message);
                failure.MessageData.Text = message;
                failure.MessageData.DetailText = detail;
                throw failure;
            }
        });
        string file = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".xml");
        var result = fixture.Engine.HardwareManageHardwareUtilities("exportOpcUa", devicePathJson: new[] { cpu.Name },
            itemPathJson: new[] { cpu.Name }, filePath: file, dryRun: false);
        Assert.Equal(1, exports);
        Assert.Equal("ReadOrWriteFailed", result.Meta["status"]);
        Assert.Contains(detail, (string?)result.Meta["nativeMessage"]);
        Assert.Equal(false, result.Meta["apiCallSuccess"]);
        Assert.Equal(true, result.Meta["mayHaveWrittenFiles"]);
        Assert.False(result.OutcomeUnknown);
        Assert.False(result.MayHaveChanged);
    }

    public static (PlcFoundationEngine Engine, Siemens.Engineering.Project Project, Siemens.Engineering.HW.DeviceItem Cpu) ExportFixture(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        fixture.Cpu.Name = fixture.Project.Devices.Single().Name = "MCP_RC_PLC";
        return (fixture.Engine, fixture.Project, fixture.Cpu);
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
