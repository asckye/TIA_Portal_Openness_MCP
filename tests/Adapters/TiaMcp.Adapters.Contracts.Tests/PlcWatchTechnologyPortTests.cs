using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PlcWatchTechnologyPortTests
{
    [Fact]
    public void Parameter_preview_respects_the_Value_attribute_access_mode()
    {
        const string name = "Retain.CtrlParams.Gain";
        var refusal = Assert.Throws<AdapterPreconditionException>(() => PlcTechnologyRefusal.RequireWritableParameter(name, "Read"));
        Assert.Equal("parameter", refusal.ParamName);
        Assert.Contains(name, refusal.Message);
        PlcTechnologyRefusal.RequireWritableParameter(name, "ReadWrite");
        PlcTechnologyRefusal.RequireWritableParameter(name, null);
    }

    [Theory]
    [InlineData("21", "TO_PositioningAxis", "6.0", "create", true)]
    [InlineData("21", "TO_PositioningAxis", "5.0", "create", false)]
    [InlineData("21", "TO_SpeedAxis", "5.0", "create", false)]
    [InlineData("21", "PID_Compact", "2.3", "create", false)]
    [InlineData("21", "Unverified", "5.0", "create", true)]
    [InlineData("21", "Unverified", "6.0", "read", false)]
    [InlineData("21", "TO_PositioningAxis", "6.0", "read", false)]
    [InlineData("14sp1", "TO_PositioningAxis", "6.0", "create", false)]
    [InlineData("15.1", "TO_PositioningAxis", "6.0", "create", false)]
    [InlineData("16", "TO_PositioningAxis", "6.0", "create", false)]
    [InlineData("17", "TO_PositioningAxis", "6.0", "create", false)]
    [InlineData("18", "TO_PositioningAxis", "6.0", "create", false)]
    [InlineData("19", "TO_PositioningAxis", "6.0", "create", false)]
    [InlineData("20", "TO_PositioningAxis", "6.0", "create", false)]
    public void TechnologyCreateGuardsTheVerifiedV21Pairs(string release, string type, string version, string action, bool refused)
    {
        string? reason = PlcB4Capabilities.Unsupported(release, new PlcSoftwareRequest {
            Operation = "ManageTechnologyObject", TypeIdentifier = type, Version = version, Action = action });
        Assert.Equal(refused, reason != null);
        if (refused)
        {
            Assert.Contains("real V21 crash", reason!);
            Assert.Contains("not an API difference", reason!);
            foreach (string pair in new[] { "TO_PositioningAxis 5.0", "TO_SpeedAxis 5.0", "PID_Compact 2.3" }) Assert.Contains(pair, reason!);
        }
    }

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
    [InlineData("15.1", "createTable", false)]
    [InlineData("16", "read", true)]
    [InlineData("16", "createComment", false)]
    [InlineData("16", "createTable", false)]
    [InlineData("17", "read", false)]
    [InlineData("17", "deleteEntry", false)]
    public void EntryActionsRequireTheirTypedApi(string release, string action, bool refused)
        => Assert.Equal(refused, PlcB4Capabilities.Unsupported(release, new PlcSoftwareRequest { Operation = "ManagePlcTableEntries", Action = action }) != null);

    [Theory]
    [InlineData("watch")]
    [InlineData("force")]
    public void OfflineImportAcceptsOnlyTheSelectedNativeTableKind(string tableKind)
    {
        string selected = tableKind == "watch" ? "Watch" : "Force";
        string other = tableKind == "watch" ? "Force" : "Watch";
        string Table(string kind, int id) => $"<SW.WatchAndForceTables.Plc{kind}Table ID=\"{id}\"><AttributeList><Name>Table{id}</Name></AttributeList>" +
            "<ObjectList><SW.WatchAndForceTables.PlcTableCommentEntry ID=\"100\" CompositionName=\"Entries\"><AttributeList/>" +
            $"</SW.WatchAndForceTables.PlcTableCommentEntry></ObjectList></SW.WatchAndForceTables.Plc{kind}Table>";
        string path = Path.Combine(Path.GetTempPath(), "tia-watch-import-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<Document><Engineering version=\"V21\"/>" + Table(selected, 0) + Table(selected, 1) + "</Document>");
            Assert.Equal(2, WatchTableImportValidation.Validate(path, tableKind));
            Assert.Equal(new[] { "Table0", "Table1" }, WatchTableImportValidation.ValidateNames(path, tableKind));
            if (tableKind == "watch") Assert.Equal(2, WatchTableImportValidation.Validate(path));
            File.WriteAllText(path, "<Document>" + Table(other, 0) + "</Document>");
            Assert.Throws<ArgumentException>(() => WatchTableImportValidation.Validate(path, tableKind));
            File.WriteAllText(path, "<Document>" + Table(selected, 0) + Table(other, 1) + "</Document>");
            Assert.Throws<ArgumentException>(() => WatchTableImportValidation.Validate(path, tableKind));
            File.WriteAllText(path, "<!DOCTYPE Document [<!ENTITY x 'forbidden'>]><Document>" + Table(selected, 0) + "</Document>");
            Assert.Throws<System.Xml.XmlException>(() => WatchTableImportValidation.Validate(path, tableKind));
            File.WriteAllText(path, "<Document/>");
            Assert.Throws<ArgumentException>(() => WatchTableImportValidation.Validate(path, tableKind));
            Assert.Throws<ArgumentException>(() => WatchTableImportValidation.Validate(path, "unknown"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("watch", "<Name> Table</Name>")]
    [InlineData("force", "<Name>Table </Name>")]
    [InlineData("watch", "")]
    [InlineData("watch", "<Name/>")]
    [InlineData("watch", "<Name>One</Name><Name>Two</Name>")]
    [InlineData("force", "")]
    public void Offline_import_needs_an_exact_table_identity_for_rollback(string tableKind, string nameXml)
    {
        string path = Path.Combine(Path.GetTempPath(), "tia-watch-name-" + Guid.NewGuid().ToString("N") + ".xml");
        string kind = tableKind == "watch" ? "Watch" : "Force";
        try
        {
            File.WriteAllText(path, "<Document><SW.WatchAndForceTables.Plc" + kind + "Table ID=\"0\"><AttributeList>"
                + nameXml + "</AttributeList></SW.WatchAndForceTables.Plc" + kind + "Table></Document>");
            Assert.Throws<ArgumentException>(() => WatchTableImportValidation.ValidateNames(path, tableKind));
        }
        finally { File.Delete(path); }
    }

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
