extern alias plcpathfixture;
using PathEngine = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using Block = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock;
using Group = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup;
using PlcType = plcpathfixture::Siemens.Engineering.SW.Types.PlcType;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PlcArtifactRefusalTests
{
    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("changed", true)]
    [InlineData("unreadable", true)]
    [InlineData("multiple", true)]
    [InlineData("ambiguous-name", true)]
    [InlineData("different-case", true)]
    [InlineData("overwrite", true)]
    [InlineData("timeout", true)]
    public void Directory_tag_table_worker_preserves_unproven_imports(string fault, bool uncertain)
    {
        var engine = new PathEngine("19");
        var adapter = engine.Artifacts;
        var tables = engine.Plc.TagTableGroup.TagTables;
        int calls = 0;
        tables.OnImport = table => {
            calls++;
            if (fault == "changed") tables.Add(table);
            if (fault == "different-case") { table.Name = table.Name.ToUpperInvariant(); tables.Add(table); }
            if (fault == "unreadable") tables.OnFind = _ => throw new IOException("Group unavailable");
            if (fault == "timeout") throw new TimeoutException();
            throw new InvalidOperationException("Import refused");
        };
        string Table(string name) => $"<SW.Tags.PlcTagTable ID='0'><AttributeList><Name>{name}</Name>" +
            (fault == "ambiguous-name" ? "<Name>Other</Name>" : "") + "</AttributeList></SW.Tags.PlcTagTable>";
        var file = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, "<Document><Engineering version='V19'/>" + Table("Signals") + (fault == "multiple" ? Table("Other") : "") + "</Document>");
        try
        {
            Assert.Throws<PlcSoftwareException>(() => adapter.ImportPlcTagTable("PLC", "", file, fault == "overwrite"));
            Assert.Equal(1, calls);
            Assert.Equal(uncertain, adapter.MutationStarted);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Same_table_and_type_can_survive_an_interrupted_delete_with_changed_members()
    {
        var engine = new PathEngine("19");
        var table = new plcpathfixture::Siemens.Engineering.SW.Tags.PlcTagTable { Name = "Signals" };
        engine.Plc.TagTableGroup.TagTables.Add(table);
        table.UserConstants.Add(new { Name = "Count", DataTypeName = "Int", Value = "4" });
        table.OnDelete = () => { table.UserConstants.Clear(); throw new InvalidOperationException("Delete interrupted"); };
        var tableAdapter = engine.Artifacts;
        Assert.NotNull(Record.Exception(() => tableAdapter.DeletePlcTagTable("PLC", "Signals", false)));
        Assert.True(tableAdapter.MutationStarted);
        Assert.Same(table, Assert.Single(engine.Plc.TagTableGroup.TagTables));
        Assert.Empty(table.UserConstants);

        var type = new PlcType { Name = "Motor", Declaration = "STRUCT Speed : Int; END_STRUCT" };
        engine.Plc.TypeGroup.Types.Add(type);
        type.OnDelete = () => { type.Declaration = "STRUCT END_STRUCT"; throw new InvalidOperationException("Delete interrupted"); };
        var typeAdapter = engine.Artifacts;
        Assert.Throws<InvalidOperationException>(() => typeAdapter.DeletePlcType("PLC", "Motor", false));
        Assert.True(typeAdapter.MutationStarted);
        Assert.Same(type, Assert.Single(engine.Plc.TypeGroup.Types));
        Assert.Equal("STRUCT END_STRUCT", type.Declaration);
    }

    [Fact]
    public void Failed_delete_can_leave_the_same_block_with_changed_contents()
    {
        var engine = new PathEngine("19");
        var group = new Group { Name = "G", Parent = engine.Plc.BlockGroup };
        engine.Plc.BlockGroup.Groups.Add(group);
        var block = new Block { Name = "Main", Parent = group };
        group.Blocks.Add(block);
        group.OnCall = call => {
            if (call != "delete Main") return;
            block.IsConsistent = false;
            throw new InvalidOperationException("Native deletion interrupted.");
        };
        var adapter = engine.Artifacts;
        Assert.Throws<InvalidOperationException>(() => adapter.DeletePlcBlock("PLC", "G/Main", false));
        Assert.True(adapter.MutationStarted);
        Assert.Same(block, Assert.Single(group.Blocks));
        Assert.False(block.IsConsistent);
    }
}
