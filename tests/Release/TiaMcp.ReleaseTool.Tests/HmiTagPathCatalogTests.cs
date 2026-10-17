using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class HmiTagPathCatalogTests
{
    [Fact]
    public void Current_pattern_arguments_use_the_library_without_mutating_frozen_evidence()
    {
        var source = new JsonObject { ["tagTablePath"] = "exact", ["tagName"] = "Ready" };
        var curated = new JsonObject { ["tagTablePath"] = "/Table", ["tagName"] = "Other" };
        var result = CatalogGenerator.CurrentArguments("DeleteHmiTag", source, curated);
        Assert.Equal("/Table", (string?)result["tagTablePath"]);
        Assert.Equal("Ready", (string?)result["tagName"]);
        Assert.Equal("exact", (string?)source["tagTablePath"]);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("/")]
    [InlineData("/Group//Table")]
    [InlineData("/Group/..")]
    public void An_invalid_curated_path_fails_catalog_generation(string path)
    {
        var source = new JsonObject { ["tagTablePath"] = "/Table" };
        var curated = new JsonObject { ["tagTablePath"] = path };
        Assert.Throws<ReleaseException>(() => CatalogGenerator.CurrentArguments("DeleteHmiTag", source, curated));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    public void A_nonstring_curated_path_cannot_fall_back_to_the_frozen_value(string json)
    {
        var source = new JsonObject { ["tagTablePath"] = "/Table" };
        var curated = new JsonObject { ["tagTablePath"] = JsonNode.Parse(json) };
        Assert.Throws<ReleaseException>(() => CatalogGenerator.CurrentArguments("DeleteHmiTag", source, curated));
    }

    [Fact]
    public void All_live_release_examples_are_valid_and_engine_source_examples_keep_every_unvalidated_frozen_value()
    {
        string root = Repository.FindRoot();
        var resource = CatalogGenerator.Generate(root)[CatalogGenerator.Resource];
        var document = System.Xml.Linq.XDocument.Parse(resource);
        var catalog = JsonNode.Parse(document.Descendants("data").Single(e => (string?)e.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            var row = catalog["releases"]![release]!.AsArray().Single(r => (string?)r!["currentName"] == "DeleteHmiTag")!;
            Assert.Equal("/Table", (string?)row["arguments"]?["tagTablePath"]);
        }
        // GetToolUsage serves the engine-source rows on 20/21: the pattern-checked table path takes the
        // maintained call-library value, and every other argument stays byte-equal to the frozen fixture.
        var frozen = CatalogJson.Read(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceExamples.json")["DeleteHmiTag"]!["arguments"]!.AsObject();
        var library = CatalogJson.Read(root, "reference/tool-examples/calls.json")["profiles"]!["full-engine"]!["DeleteHmiTag"]!["arguments"]!;
        string pattern = TiaMcpServer.ModelContextProtocol.ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.Pattern!;
        Assert.Equal("exact", (string?)frozen["tagTablePath"]);
        foreach (string release in new[] { "20", "21" })
        {
            var arguments = catalog["engineSourceReleases"]![release]!.AsArray().Single(r => (string?)r!["currentName"] == "DeleteHmiTag")!["arguments"]!.AsObject();
            Assert.Equal(frozen.Select(p => p.Key), arguments.Select(p => p.Key));
            foreach (var (name, value) in frozen.Where(p => p.Key != "tagTablePath"))
                Assert.True(JsonNode.DeepEquals(value, arguments[name]), release + "/" + name);
            Assert.Equal((string?)library["tagTablePath"], (string?)arguments["tagTablePath"]);
            Assert.Equal("/Table", (string?)arguments["tagTablePath"]);
            Assert.Matches(pattern, (string?)arguments["tagTablePath"]);
        }
    }
}
