using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.ExternalSources;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.FoundationHost;
using Xunit;

// In-memory traversal evidence only; exact SDK compilation independently checks the native shapes.
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed class TestUnit
    {
        public PlcBlockGroup Blocks = new();
        public PlcTypeGroup Types = new();
        public PlcTagTableGroup Tags = new();
        public PlcExternalSourceGroup Sources = new();
    }
    public static class PlcUnitScope
    {
        public static IEnumerable<KeyValuePair<string, object>> Units(PlcSoftware plc)
            => plc.UnitScopes.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value));
        public static PlcBlockGroup Blocks(PlcSoftware plc, object unit) => ((TestUnit)unit).Blocks;
        public static PlcTypeGroup Types(PlcSoftware plc, object unit) => ((TestUnit)unit).Types;
        public static PlcTagTableGroup Tags(PlcSoftware plc, object unit) => ((TestUnit)unit).Tags;
        public static PlcExternalSourceGroup Sources(PlcSoftware plc, object unit) => ((TestUnit)unit).Sources;
    }
}
public sealed class UnitTreeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("devices/站\\一/PLC")]
    public void Software_selection_refuses_null_and_raw_backslashes(string? input)
    {
        var candidate = new PlcReadCandidate<int> { Value = 1, ExactPath = "devices/站%5C一/PLC" };
        var error = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => PlcReadPathPolicy.Resolve(new[] { candidate }, input!));
        Assert.Equal("softwarePath", error.ParamName);
        Assert.Equal(1, PlcReadPathPolicy.Resolve(new[] { candidate }, candidate.ExactPath));
    }

    [Theory]
    [InlineData("devices/中文 站/CPU", "devices/%E4%B8%AD%E6%96%87%20%E7%AB%99/CPU")]
    [InlineData("devices/A%2FB/50%25", "devices/A%2FB/50%25")]
    public void Saved_software_addresses_match_readable_inventory(string readable, string saved)
    {
        var candidates = new[] { new PlcReadCandidate<int> { Value = 1, ExactPath = readable } };
        Assert.Equal(1, PlcReadPathPolicy.Resolve(candidates, readable));
        Assert.Equal(1, PlcReadPathPolicy.Resolve(candidates, saved));
    }

    [Fact]
    public void UnitsHaveIndependentTopLevelIdentitiesAndAllFourGroups()
    {
        var engine = new PlcFoundationEngine();
        engine.Software.BlockGroup.Blocks.Add(new PlcBlock { Name = "Same" });
        foreach (var name in new[] { "Unit A/B", "Unit_2" })
        {
            var unit = new TiaMcp.Adapters.Native.Plc.TestUnit();
            unit.Blocks.Blocks.Add(new PlcBlock { Name = "Same" });
            unit.Types.Types.Add(new PlcType { Name = "Data" });
            unit.Tags.TagTables.Add(new PlcTagTable { Name = "Tags" });
            unit.Sources.ExternalSources.Add(new PlcExternalSource { Name = "Code.scl" });
            engine.Software.UnitScopes.Add(name, unit);
        }
        var actual = JsonSerializer.SerializeToNode(engine.ReadSoftwareTree("D"))!.AsObject();
        var paths = actual["Meta"]!["paths"]!.AsArray();
        Assert.Equal(2, paths.Count(path => (string?)path?["kind"] == "unit"));
        Assert.Equal(3, paths.Count(path => (string?)path?["kind"] == "block"));
        Assert.Contains(paths, path => (string?)path?["path"] == "devices/D/CPU/units/Unit A%2FB/blocks/Same"
            && (string?)path?["objectPath"] == "Same" && (string?)path?["unitName"] == "Unit A/B");
        foreach (var kind in new[] { "block-group", "type-group", "tag-group", "source-group" })
            Assert.Equal(2, paths.Count(path => (string?)path?["kind"] == kind && path?["unitName"] != null));
        SoftwareReadContract.Wrap("GetSoftwareTree", actual, new JsonSerializerOptions());
        var unitPath = paths.First(path => (string?)path?["kind"] == "tag-table")!;
        unitPath["unitName"] = "Another";
        Assert.Throws<InvalidDataException>(() => SoftwareReadContract.Wrap("GetSoftwareTree", actual, new JsonSerializerOptions()));
    }
}
