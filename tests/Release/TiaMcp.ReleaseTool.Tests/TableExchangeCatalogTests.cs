using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class TableExchangeCatalogTests
{
    [Fact]
    public void New_offline_tool_generates_on_every_release_without_a_historical_engine_contract()
    {
        string root = Repository.FindRoot();
        var data = new CatalogData(root);
        Assert.Equal("FILE", data.HostOnly["ConvertEngineeringTable"]);
        Assert.All(data.Archives.Values, archive => Assert.False(archive.ContainsKey("ConvertEngineeringTable")));
        var outputs = CatalogGenerator.Generate(root);
        var runtime = JsonNode.Parse(XDocument.Parse(outputs[CatalogGenerator.Resource]).Descendants("data")
            .Single(e => (string?)e.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string release in CatalogJson.Keys)
        {
            var row = Assert.Single(runtime["releases"]![release]!.AsArray(), r => (string?)r!["currentName"] == "ConvertEngineeringTable")!;
            Assert.Equal(4, (int)row["envelopeVersion"]!);
            Assert.Equal("plcTagTable", (string?)row["arguments"]?["kind"]);
            Assert.Equal("toTable", (string?)row["arguments"]?["direction"]);
        }
    }
}
