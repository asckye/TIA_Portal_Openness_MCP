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
        Assert.Contains(paths, path => (string?)path?["path"] == "devices/D/CPU/units/Unit%20A%2FB/blocks/Same"
            && (string?)path?["objectPath"] == "Same" && (string?)path?["unitName"] == "Unit A/B");
        foreach (var kind in new[] { "block-group", "type-group", "tag-group", "source-group" })
            Assert.Equal(2, paths.Count(path => (string?)path?["kind"] == kind && path?["unitName"] != null));
        SoftwareReadContract.Wrap("GetSoftwareTree", actual, new JsonSerializerOptions());
        var unitPath = paths.First(path => (string?)path?["kind"] == "tag-table")!;
        unitPath["unitName"] = "Another";
        Assert.Throws<InvalidDataException>(() => SoftwareReadContract.Wrap("GetSoftwareTree", actual, new JsonSerializerOptions()));
    }
}
