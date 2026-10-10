using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PlcWatchTechnologyPortTests
{
    [Theory]
    [InlineData("14sp1", 4, 6)]
    [InlineData("15.1", 11, 7)]
    [InlineData("16", 11, 9)]
    [InlineData("17", 12, 9)]
    [InlineData("18", 12, 9)]
    [InlineData("19", 12, 9)]
    [InlineData("20", 12, 9)]
    [InlineData("21", 12, 9)]
    public void RegistrationRequiresAtLeastOneUsableAction(string release, int watch, int technology)
    {
        Assert.Equal(watch, PortedFamilies.ForTool("TraceTagCause").Tools.Count(t => PortedFamilies.Available(release, t)));
        Assert.Equal(technology, PortedFamilies.ForTool("ManageTechnologyObject").Tools.Count(t => PortedFamilies.Available(release, t)));
        foreach (var family in PortedFamilies.All.Where(f => f.Name is "F12" or "F13"))
            foreach (var tool in family.Tools)
            {
                Assert.True(PortedFamilies.ToolReleases.ContainsKey(tool));
                Assert.Equal(!PortedFamilies.Available(release, tool), PlcSoftwareCapabilities.Unsupported(release, tool) != null);
            }
    }

    [Theory]
    [InlineData("15.1", "read", true)]
    [InlineData("15.1", "deleteEntry", true)]
    [InlineData("15.1", "createComment", false)]
    [InlineData("16", "read", true)]
    [InlineData("16", "createComment", false)]
    [InlineData("17", "read", false)]
    [InlineData("17", "deleteEntry", false)]
    public void EntryActionsRequireTheirTypedApi(string release, string action, bool refused)
        => Assert.Equal(refused, PlcB4Capabilities.Unsupported(release, new PlcSoftwareRequest { Operation = "ManagePlcTableEntries", Action = action }) != null);

    [Theory]
    [InlineData("14sp1", "connect", "torque", true)]
    [InlineData("15.1", "connect", "torque", false)]
    [InlineData("15.1", "addMasterValue", "conveyorTrackingLeadingValues", true)]
    [InlineData("16", "addMasterValue", "conveyorTrackingLeadingValues", false)]
    [InlineData("15.1", "connect", "synchronousDelayed", true)]
    [InlineData("16", "connect", "synchronousDelayed", false)]
    [InlineData("20", "connectIdent", "", true)]
    [InlineData("21", "connectIdent", "", false)]
    [InlineData("20", "createMapping", "", true)]
    [InlineData("21", "createMapping", "", false)]
    [InlineData("20", "updateMapping", "", true)]
    [InlineData("21", "updateMapping", "", false)]
    [InlineData("20", "deleteMapping", "", true)]
    [InlineData("21", "deleteMapping", "", false)]
    [InlineData("20", "addMasterValue", "superimposingSetPoint", true)]
    [InlineData("21", "addMasterValue", "superimposingSetPoint", false)]
    public void MotionActionsHaveExplicitReleaseBoundaries(string release, string action, string aspect, bool refused)
        => Assert.Equal(refused, PlcB4Capabilities.Unsupported(release, new PlcSoftwareRequest { Operation = "ManageMotionAxis", Action = action, Aspect = aspect }) != null);

    [Theory]
    [InlineData("14sp1", true)]
    [InlineData("15.1", false)]
    public void TorqueConfigurationRequiresV15_1(string release, bool refused)
        => Assert.Equal(refused, PlcB4Capabilities.Unsupported(release, new PlcSoftwareRequest { Operation = "ConfigureMotionHardwareConnection", Aspect = "torque" }) != null);

    [Theory]
    [InlineData("ManageTechnologyObject")]
    [InlineData("ManageMotionAxis")]
    [InlineData("GetMotionAxisConfiguration")]
    [InlineData("ConfigureMotionHardwareConnection")]
    [InlineData("ExchangeMotionCamData")]
    [InlineData("GetTechnologyObjectTree")]
    [InlineData("ImportTechnologyObject")]
    [InlineData("ImportTechnologyObjectsFromDirectory")]
    public void TechnologyUserGroupsRequireV19(string tool)
    {
        var request = new PlcSoftwareRequest { Operation = tool, Path = "Group/Axis", GroupPath = "Group" };
        Assert.NotNull(PlcB4Capabilities.Unsupported("18", request));
        Assert.Null(PlcB4Capabilities.Unsupported("19", request));
        request.Path = "Axis"; request.GroupPath = "";
        Assert.Null(PlcB4Capabilities.Unsupported("18", request));
    }

    [Theory]
    [InlineData("16", true)]
    [InlineData("17", true)]
    [InlineData("18", false)]
    public void UnitNamespaceRequiresV18(string release, bool refused)
    {
        var request = new PlcSoftwareRequest { Operation = "ManagePlcSoftwareUnit", Properties = new() { ["NamespacePreset"] = new HardwareScalar { Kind = "string", Text = "Fixture" } } };
        Assert.Equal(refused, PlcB4Capabilities.Unsupported(release, request) != null);
    }

    [Theory]
    [InlineData("F12", "plc-watch")]
    [InlineData("F13", "plc-technology")]
    public void DispatchHasOnlyTheExplicitOrganisationOperation(string family, string prefix)
    {
        Assert.Equal(new[] { "ExecutePlcOrganisation" }, WorkerOperations.FamilyNames(family));
        Assert.True(WorkerOperations.IsFamilyOperation(prefix + ".ExecutePlcOrganisation"));
        Assert.False(WorkerOperations.IsFamilyOperation(prefix + ".RunAnything"));
    }

    [Fact]
    public void MotionTargetAndWatchRowsSurviveTheTypedWire()
    {
        var request = new PlcSoftwareRequest { MotionTarget = new MotionConnectionTarget { Mode = "channel", DevicePath = new[] { "Device" }, ItemPath = new[] { "CPU", "Input" }, SecondItemPath = new[] { "Output" }, ChannelType = "Digital", ChannelIoType = "Input", ChannelNumber = 3, HasConnectOption = true, ConnectOption = "AllowAllModules" } };
        var target = JsonSerializer.Deserialize<PlcSoftwareRequest>(JsonSerializer.Serialize(request))!.MotionTarget;
        Assert.Equal("channel", target!.Mode);
        Assert.Equal(3, target.ChannelNumber);
        Assert.Equal("Input", target.ItemPath![1]);
        Assert.Equal("Output", Assert.Single(target.SecondItemPath!));
        Assert.True(target.HasConnectOption);
        var row = JsonSerializer.Deserialize<PlcWatchRow>(JsonSerializer.Serialize(new PlcWatchRow { Name = "Tag", Address = "%MW2", Format = "Word" }))!;
        Assert.Equal("%MW2", row.Address);
        Assert.Equal("Word", row.Format);
    }
}
