using System.Text.Json;
using Siemens.Engineering.HW;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HardwareManagementNativeTests
{
    private const string TargetType = "OrderNumber:6ES7 513-1AM03-0AB0/V3.0";

    [Theory]
    [InlineData("MoveItem", true)]
    [InlineData("MoveItem", false)]
    [InlineData("COPYITEM", true)]
    [InlineData("COPYITEM", false)]
    [InlineData("unknown", true)]
    [InlineData("unknown", false)]
    [InlineData("", true)]
    [InlineData("", false)]
    public void Unknown_action_refuses_before_hardware_lookup_or_edit_access(string action, bool preview)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        fixture.Engine.DeviceInventoryFailure = new InvalidOperationException("Hardware lookup must not run.");
        int editAccess = fixture.Engine.HardwareEditAccessCalls, exclusive = fixture.Portal.ExclusiveCalls, nativeCalls = 0;
        fixture.Portal.EngineeringCall = () => nativeCalls++;
        var result = fixture.Engine.HardwareManageHardwareObject(new[] { "PLC" }, action, new[] { "CPU" }, new[] { "PLC" },
            Array.Empty<string>(), 2, preview);
        Assert.Equal("InvalidParams", result.Meta["status"]);
        Assert.Equal("action", result.Meta["parameter"]);
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.False(result.MayHaveChanged);
        Assert.False(result.OutcomeUnknown);
        Assert.False(result.Meta.ContainsKey("source"));
        Assert.Equal(editAccess, fixture.Engine.HardwareEditAccessCalls);
        Assert.Equal(exclusive, fixture.Portal.ExclusiveCalls);
        Assert.Equal(0, nativeCalls);
    }

    [Fact]
    public void Type_change_preview_keeps_current_type_order_number_and_the_exact_catalog_entry_without_writing()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        fixture.Cpu.AttributeReader = name => name == "TypeIdentifier" ? "OldType" : "6ES7 513-1AM03-0AB0";
        fixture.Portal.HardwareCatalog.Entries.Add(new() { TypeIdentifier = TargetType });
        var result = fixture.Engine.HardwareManageHardwareObject(new[] { "PLC" }, "changeType", new[] { "CPU" }, typeIdentifier: TargetType);
        Assert.Equal(true, result.Meta["operationSuccess"]);
        Assert.Equal("OldType", result.Meta["currentTypeIdentifier"]);
        Assert.Equal("6ES7 513-1AM03-0AB0", result.Meta["orderNumber"]);
        Assert.Equal(TargetType, result.Meta["targetTypeIdentifier"]);
        Assert.Contains(TargetType, JsonSerializer.Serialize(result.Meta["catalogEntry"]));
        Assert.Equal("6ES7 513-1AM03-0AB0", Assert.Single(fixture.Portal.HardwareCatalog.Queries));
        Assert.Contains("addresses", (string)result.Meta["dependencyImpact"]!);
        Assert.Equal(0, fixture.Cpu.ChangeTypeCalls);
        Assert.False(result.MayHaveChanged);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("different")]
    [InlineData("absent")]
    [InlineData("unreadable")]
    public void Type_change_is_issued_once_and_resolves_a_fresh_item_for_readback(string state)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        bool stale = false;
        fixture.Cpu.AttributeReader = name => stale ? throw new InvalidOperationException("Old proxy is stale.") : "OldType";
        fixture.Portal.HardwareCatalog.Entries.Add(new() { TypeIdentifier = TargetType });
        fixture.Cpu.ChangingType = requested => {
            Assert.Equal(TargetType, requested);
            stale = true;
            var device = fixture.Project.Devices.Single();
            device.DeviceItems.Clear();
            if (state == "absent") return;
            device.DeviceItems.Add(new DeviceItem { Name = "CPU", Parent = device, AttributeReader = _ => state == "unreadable"
                ? throw new InvalidOperationException("Fresh type readback unavailable.") : state == "matching" ? TargetType : "DifferentType" });
        };
        var result = fixture.Engine.HardwareManageHardwareObject(new[] { "PLC" }, "changeType", new[] { "CPU" }, dryRun: false,
            typeIdentifier: TargetType);
        Assert.Equal(1, fixture.Cpu.ChangeTypeCalls);
        Assert.True(result.MayHaveChanged);
        Assert.Equal(state == "matching", result.Meta["operationSuccess"]);
        Assert.Equal(state == "unreadable", result.OutcomeUnknown);
        if (state == "unreadable")
        {
            Assert.Equal(false, result.Meta["rollbackCompleted"]);
            Assert.Contains("ChangeType", JsonSerializer.Serialize(result.Meta["cleanupRequired"]));
        }
        else
        {
            Assert.Equal(state == "matching", result.Meta["verified"]);
            Assert.Equal(true, result.Meta["postStateKnown"]);
            Assert.False(result.Meta.ContainsKey("rollbackAttempted"));
            if (state != "absent") Assert.Equal(state == "matching" ? TargetType : "DifferentType", result.Meta["readbackTypeIdentifier"]);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("different-case")]
    public void Type_change_requires_exactly_one_case_sensitive_catalog_match_before_mutation(string catalog)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        if (catalog == "duplicate")
        {
            fixture.Portal.HardwareCatalog.Entries.Add(new() { TypeIdentifier = TargetType });
            fixture.Portal.HardwareCatalog.Entries.Add(new() { TypeIdentifier = TargetType });
        }
        if (catalog == "different-case") fixture.Portal.HardwareCatalog.Entries.Add(new() { TypeIdentifier = TargetType.ToLowerInvariant() });
        var result = fixture.Engine.HardwareManageHardwareObject(new[] { "PLC" }, "changeType", new[] { "CPU" }, dryRun: false,
            typeIdentifier: TargetType);
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.Equal(0, fixture.Cpu.ChangeTypeCalls);
        Assert.False(result.MayHaveChanged);
        Assert.Equal("typeIdentifier", result.Meta["parameter"]);
    }

    [Theory]
    [InlineData("System:Subnet.Ethernet", "Ethernet")]
    [InlineData("System:Subnet.Profibus", "Profibus")]
    public void Subnet_creation_previews_a_free_name_and_reads_back_the_created_subnet(string type, string netType)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var preview = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "createSubnet", typeIdentifier: type, subnetName: "New");
        Assert.Equal(true, preview.Meta["operationSuccess"]);
        Assert.Equal(true, preview.Meta["nameAvailable"]);
        Assert.False(preview.MayHaveChanged);
        Assert.Empty(fixture.Project.Subnets);
        Assert.Equal(0, fixture.Project.Subnets.CreateCalls);
        var result = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "createSubnet", dryRun: false,
            typeIdentifier: type, subnetName: "New");
        Assert.Equal(1, fixture.Project.Subnets.CreateCalls);
        Assert.Equal(type, Assert.Single(fixture.Project.Subnets).TypeIdentifier);
        Assert.Equal(true, result.Meta["operationSuccess"]);
        Assert.Equal(true, result.Meta["verified"]);
        var after = Assert.IsType<Dictionary<string, object?>>(result.Meta["after"]);
        Assert.Equal("New", after["name"]);
        Assert.Equal(type, after["typeIdentifier"]);
        Assert.Equal(netType, after["netType"]);
        Assert.False(result.OutcomeUnknown);
    }

    [Theory, InlineData(true), InlineData(false)]
    public void An_existing_subnet_refuses_before_creation(bool preview)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var existing = new Subnet { Name = "Existing" };
        fixture.Project.Subnets.Add(existing);
        var result = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "createSubnet", dryRun: preview,
            typeIdentifier: "System:Subnet.Ethernet", subnetName: "Existing");
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.False(result.MayHaveChanged);
        Assert.Same(existing, Assert.Single(fixture.Project.Subnets));
        Assert.Equal(0, fixture.Project.Subnets.CreateCalls);
    }

    [Fact]
    public void A_create_that_throws_after_inserting_is_cleaned_up_by_the_pre_registered_journal_entry()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        fixture.Project.Subnets.Created = _ => throw new InvalidOperationException("Native failure after insertion.");
        var result = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "createSubnet", dryRun: false,
            typeIdentifier: "System:Subnet.Ethernet", subnetName: "New");
        Assert.Equal(1, fixture.Project.Subnets.CreateCalls);
        Assert.Empty(fixture.Project.Subnets);
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.Equal(true, result.Meta["rollbackCompleted"]);
        Assert.False(result.MayHaveChanged);
    }

    [Fact]
    public void Deletion_previews_both_node_interface_paths_and_io_systems_then_verifies_absence()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var subnet = new Subnet { Name = "PN_IE_1" };
        var secondInterface = new DeviceItem { Name = "PN/IE", Parent = fixture.Project.Devices.Single() };
        subnet.Nodes.Add(new Node { Name = "Node1", Parent = fixture.Cpu });
        subnet.Nodes.Add(new Node { Name = "Node2", Parent = secondInterface });
        subnet.IoSystems.Add(new IoSystem { Name = "IO_1" });
        subnet.Deleting = () => fixture.Project.Subnets.Remove(subnet);
        fixture.Project.Subnets.Add(subnet);
        var preview = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "deleteSubnet", subnetName: subnet.Name);
        Assert.Equal(true, preview.Meta["operationSuccess"]);
        Assert.Equal(2, preview.Meta["connectedNodeCount"]);
        var nodes = Assert.IsType<Dictionary<string, object?>[]>(preview.Meta["connectedNodes"]);
        Assert.Equal(new[] { "PLC", "CPU" }, nodes[0]["interfacePath"]);
        Assert.Equal(new[] { "PLC", "PN/IE" }, nodes[1]["interfacePath"]);
        Assert.Equal(new[] { "IO_1" }, preview.Meta["ioSystems"]);
        Assert.Equal(0, subnet.DeleteCalls);
        var result = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "deleteSubnet", dryRun: false, subnetName: subnet.Name);
        Assert.Equal(1, subnet.DeleteCalls);
        Assert.Empty(fixture.Project.Subnets);
        Assert.Equal(true, result.Meta["verifiedAbsent"]);
        Assert.Equal(true, result.Meta["operationSuccess"]);
        Assert.True(result.MayHaveChanged);
        Assert.False(result.OutcomeUnknown);
    }

    [Fact]
    public void A_delete_that_throws_after_removing_is_not_reported_as_restored()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var subnet = new Subnet { Name = "PN_IE_1" };
        subnet.Deleting = () => { fixture.Project.Subnets.Remove(subnet); throw new InvalidOperationException("Delete readback unavailable."); };
        fixture.Project.Subnets.Add(subnet);
        var result = fixture.Engine.HardwareManageHardwareObject(Array.Empty<string>(), "deleteSubnet", dryRun: false, subnetName: subnet.Name);
        Assert.Equal(1, subnet.DeleteCalls);
        Assert.Empty(fixture.Project.Subnets);
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.True(result.MayHaveChanged);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal(false, result.Meta["rollbackCompleted"]);
        Assert.Contains("Subnet.Delete", JsonSerializer.Serialize(result.Meta["cleanupRequired"]));
    }
}
