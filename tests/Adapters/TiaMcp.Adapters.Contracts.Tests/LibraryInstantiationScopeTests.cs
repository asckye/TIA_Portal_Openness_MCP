using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using Xunit;
using LibraryEngine = TiaMcp.Adapters.PlcFoundationEngine;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class LibraryInstantiationScopeTests
{
    [Fact]
    public void Production_block_scope_contains_system_groups_types_and_only_shared_units()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var plc = fixture.Engine.FamilyPlc;
        plc.BlockGroup.Blocks.Add(new OB { Name = "RootBlock" });
        var system = new PlcBlockGroup { Name = "System" };
        var nested = new PlcBlockGroup { Name = "Nested" };
        nested.Blocks.Add(new OB { Name = "SystemBlock" }); system.Groups.Add(nested); plc.BlockGroup.SystemBlockGroups.Add(system);
        var types = new PlcTypeGroup { Name = "TypesGroup" }; types.Types.Add(new HardwareObject { Name = "RootType" }); plc.TypeGroup.Groups.Add(types);
        var systemTypes = new PlcTypeGroup { Name = "SystemTypes" }; systemTypes.Types.Add(new HardwareObject { Name = "SystemType" });
        plc.TypeGroup.SystemTypeGroups.Add(systemTypes);
        foreach (bool namespaced in new[] { false, true })
        {
            var unit = new PlcUnit { Name = namespaced ? "Private" : "Shared", NamespacePreset = namespaced ? "Own" : "" };
            unit.BlockGroup.Blocks.Add(new OB { Name = namespaced ? "PrivateBlock" : "UnitBlock" });
            unit.TypeGroup.Types.Add(new HardwareObject { Name = namespaced ? "PrivateType" : "UnitType" });
            plc.UnitProvider.UnitGroup.Units.Add(unit);
            var safety = new PlcSafetyUnit { Name = namespaced ? "PrivateSafety" : "SharedSafety", NamespacePreset = namespaced ? "Safe" : "" };
            safety.BlockGroup.Blocks.Add(new OB { Name = namespaced ? "PrivateSafetyBlock" : "SafetyBlock" });
            safety.TypeGroup.Types.Add(new HardwareObject { Name = namespaced ? "PrivateSafetyType" : "SafetyType" });
            plc.UnitProvider.UnitGroup.SafetyUnits.Add(safety);
        }
        var rows = fixture.Engine.LibraryInstantiationScope("block", "PLC", "").ToArray();
        Assert.Equal(new[] { "RootBlock", "RootType", "SafetyBlock", "SafetyType", "SystemBlock", "SystemType", "UnitBlock", "UnitType" },
            rows.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains(rows, row => row.Path == "$system/System/Nested/SystemBlock");
        Assert.Contains(rows, row => row.Path == "$units/Shared/UnitBlock");
        Assert.Contains(rows, row => row.Path == "$safetyUnits/SharedSafety/SafetyBlock");
    }

    [Theory]
    [InlineData("RootType")][InlineData("UnitBlock")][InlineData("SafetyBlock")]
    public void Production_block_scope_refuses_shared_type_and_unit_names(string name)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var plc = fixture.Engine.FamilyPlc;
        plc.TypeGroup.Types.Add(new HardwareObject { Name = "RootType" });
        var unit = new PlcUnit { Name = "Shared" }; unit.BlockGroup.Blocks.Add(new OB { Name = "UnitBlock" }); plc.UnitProvider.UnitGroup.Units.Add(unit);
        var safety = new PlcSafetyUnit { Name = "Safe" }; safety.BlockGroup.Blocks.Add(new OB { Name = "SafetyBlock" });
        plc.UnitProvider.UnitGroup.SafetyUnits.Add(safety);
        var meta = new Dictionary<string, object?>();
        var failure = Assert.Throws<PlcSoftwareException>(() => LibraryEngine.InstantiateLibraryContentsCore("block", "PLC", "", new[] { name }, "", true,
            meta, _ => throw new InvalidOperationException("No CreateFrom allowed."), _ => plc.BlockGroup.Blocks,
            () => fixture.Engine.LibraryInstantiationScope("block", "PLC", ""), () => new System.IO.MemoryStream(), () => new System.IO.MemoryStream()));
        Assert.Equal("ALREADY_EXISTS", failure.Code);
    }

    [Fact]
    public void Production_tag_scope_contains_tags_and_constants_across_all_tables_and_groups()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var root = fixture.Engine.FamilyPlc.TagTableGroup;
        var first = new PlcTagTable { Name = "First" }; first.Tags.Add(new HardwareObject { Name = "Tag1" });
        first.UserConstants.Add(new HardwareObject { Name = "Constant1" }); root.TagTables.Add(first);
        var group = new PlcTagTableGroup { Name = "Nested" };
        var second = new PlcTagTable { Name = "Second" }; second.Tags.Add(new HardwareObject { Name = "Tag2" });
        second.UserConstants.Add(new HardwareObject { Name = "Constant2" }); group.TagTables.Add(second); root.Groups.Add(group);
        Assert.Equal(new[] { "Constant1", "Constant2", "Tag1", "Tag2" }, fixture.Engine.LibraryInstantiationScope("tag", "PLC", "First")
            .Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(new[] { "First", "Second" }, fixture.Engine.LibraryInstantiationScope("tagTable", "PLC", "").Select(row => row.Name));
        var failure = Assert.Throws<PlcSoftwareException>(() => LibraryEngine.InstantiateLibraryContentsCore("tag", "PLC", "First", new[] { "Constant2" },
            "", true, new(), _ => throw new InvalidOperationException(), _ => first.Tags,
            () => fixture.Engine.LibraryInstantiationScope("tag", "PLC", "First"), () => new System.IO.MemoryStream(), () => new System.IO.MemoryStream()));
        Assert.Equal("ALREADY_EXISTS", failure.Code);
    }

    [Fact]
    public void Production_device_scope_uses_all_three_project_roots()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        fixture.Project.Devices.Clear(); fixture.Project.Devices.Add(new Device { Name = "Root" });
        var parent = new DeviceUserGroup { Name = "Parent" }; var child = new DeviceUserGroup { Name = "Child" };
        child.Devices.Add(new Device { Name = "Grouped" }); parent.Groups.Add(child); fixture.Project.DeviceGroups.Add(parent);
        fixture.Project.UngroupedDevicesGroup.Name = "Ungrouped"; fixture.Project.UngroupedDevicesGroup.Devices.Add(new Device { Name = "Loose" });
        var rows = fixture.Engine.LibraryInstantiationScope("device", "", "").ToArray();
        Assert.Equal(new[] { "Root", "Grouped", "Loose" }, rows.Select(row => row.Name));
        Assert.Equal(new[] { "Root", "Parent/Child/Grouped", "Ungrouped/Loose" }, rows.Select(row => row.Path));
    }

    [Fact]
    public void Production_classic_scope_walks_screen_and_tag_folder_trees()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(); var hmi = new HmiTarget();
        hmi.ScreenFolder.Screens.Add(new Screen { Name = "RootScreen" });
        var nested = new ScreenFolder { Name = "Folder/One" }; nested.Screens.Add(new Screen { Name = "DeepScreen" }); hmi.ScreenFolder.Folders.Add(nested);
        hmi.TagFolder = new OfficialApiAuditTests.Folder { TagTables = new[] { new OfficialApiAuditTests.Table { Name = "RootTable" } },
            Folders = new[] {
                new OfficialApiAuditTests.Folder { Name = "Nested", TagTables = new[] { new OfficialApiAuditTests.Table { Name = "DeepTable" } } } } };
        fixture.Engine.ResolveEngineeringHmiSoftware = _ => hmi;
        Assert.Equal(new[] { "RootScreen", "Folder%2FOne/DeepScreen" }, fixture.Engine.LibraryInstantiationScope("classicScreen", "HMI", "")
            .Select(row => row.Path));
        Assert.Equal(new[] { "RootTable", "Nested/DeepTable" }, fixture.Engine.LibraryInstantiationScope("classicTagTable", "HMI", "")
            .Select(row => row.Path));
    }

    [Fact]
    public void Production_block_group_scope_is_parent_only_and_discloses_its_limit()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(); var root = fixture.Engine.FamilyPlc.BlockGroup;
        var parent = new PlcBlockGroup { Name = "Parent" }; var copy = new PlcBlockGroup { Name = "Copy", Parent = parent };
        copy.Blocks.Add(new OB { Name = "HiddenInside" }); parent.Groups.Add(copy); root.Groups.Add(parent);
        root.Groups.Add(new PlcBlockGroup { Name = "Outside" });
        Assert.Equal(new[] { "Copy" }, fixture.Engine.LibraryInstantiationScope("blockGroup", "PLC", "Parent").Select(row => row.Name));
        var meta = new Dictionary<string, object?>();
        LibraryEngine.InstantiateLibraryContentsCore("blockGroup", "PLC", "Parent", new[] { "New" }, "", true, meta,
            _ => throw new InvalidOperationException(), _ => parent.Groups, () => fixture.Engine.LibraryInstantiationScope("blockGroup", "PLC", "Parent"),
            () => new System.IO.MemoryStream(), () => new System.IO.MemoryStream());
        Assert.Contains("blocks inside the copied group are not checked against PLC-wide names", (string)meta["scope"]!);
    }

    [Fact]
    public void Production_version_instantiation_calls_the_committed_gate()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(); var type = new LibraryType { Name = "Motor" };
        var source = new CodeBlockLibraryTypeVersion { TypeObject = type, State = LibraryTypeVersionState.InWork };
        Assert.Throws<NotSupportedException>(() => fixture.Engine.InstantiateLibraryVersion(type, source, "PLC", "", "", true, new()));
        Assert.Empty(fixture.Engine.FamilyCalls);
        Assert.Equal(0, fixture.Engine.FamilyPlc.BlockGroup.Blocks.CreateFromCalls);
        source.State = LibraryTypeVersionState.Committed;
        var meta = new Dictionary<string, object?>();
        fixture.Engine.InstantiateLibraryVersion(type, source, "PLC", "", "", true, meta);
        Assert.True(meta.ContainsKey("targets")); Assert.Equal(0, fixture.Engine.FamilyPlc.BlockGroup.Blocks.CreateFromCalls);
    }

    [Fact]
    public void Preview_reads_each_scope_symbol_name_once_for_many_requested_names()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(); var table = new PlcTagTable { Name = "Table" };
        fixture.Engine.FamilyPlc.TagTableGroup.TagTables.Add(table);
        var symbols = Enumerable.Range(0, 50).Select(index => new CountedName("Existing" + index)).ToArray();
        foreach (var symbol in symbols) table.Tags.Add(symbol);
        LibraryEngine.InstantiateLibraryContentsCore("tag", "PLC", "Table", Enumerable.Range(0, 100).Select(index => "Missing" + index).ToArray(),
            "", true, new(), _ => throw new InvalidOperationException(), _ => table.Tags,
            () => fixture.Engine.LibraryInstantiationScope("tag", "PLC", "Table"), () => new System.IO.MemoryStream(), () => new System.IO.MemoryStream());
        Assert.All(symbols, symbol => Assert.Equal(1, symbol.Reads));
    }

    private sealed class CountedName(string value) : HardwareObject
    {
        internal int Reads;
        public new string Name { get { Reads++; return value; } }
    }
}
