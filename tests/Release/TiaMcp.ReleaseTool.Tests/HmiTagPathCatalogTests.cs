using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class HmiTagPathCatalogTests
{
    [Fact]
    public void Current_arguments_use_the_library_without_mutating_frozen_evidence()
    {
        var source = new JsonObject { ["tagTablePath"] = "exact", ["tagName"] = "Ready", ["dryRun"] = false };
        var curated = new JsonObject { ["tagTablePath"] = "/Table", ["tagName"] = "Other" };
        var result = CatalogGenerator.CurrentArguments("DeleteHmiTag", source, curated);
        Assert.True(JsonNode.DeepEquals(curated, result));
        Assert.Equal("exact", (string?)source["tagTablePath"]);
        Assert.Equal("Ready", (string?)source["tagName"]);
    }

    [Fact]
    public void Without_a_library_entry_the_frozen_arguments_must_still_satisfy_the_pattern()
    {
        var valid = new JsonObject { ["tagTablePath"] = "/Table", ["tagName"] = "Ready" };
        Assert.True(JsonNode.DeepEquals(valid, CatalogGenerator.CurrentArguments("DeleteHmiTag", valid, null)));
        var invalid = new JsonObject { ["tagTablePath"] = "exact" };
        Assert.Throws<ReleaseException>(() => CatalogGenerator.CurrentArguments("DeleteHmiTag", invalid, null));
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
    public void Served_release_examples_are_the_call_library_and_the_frozen_engine_examples_stay_unchanged()
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
        // GetToolUsage serves the engine-source rows on 20/21: every argument comes from the maintained
        // call library (real-machine V21 test 2026-10-31: frozen engine values were wrong examples), while the
        // frozen fixture stays byte-equal comparison and rejection evidence.
        var frozen = CatalogJson.Read(root, "tests/Engine/TiaMcp.Engine.Tests/Fixtures/EngineSourceExamples.json")["DeleteHmiTag"]!["arguments"]!.AsObject();
        var calls = CatalogJson.Read(root, "reference/tool-examples/calls.json")["profiles"]!["full-engine"]!.AsObject();
        string pattern = TiaMcpServer.ModelContextProtocol.ToolMetadata.Parameter("DeleteHmiTag", "tagTablePath")!.Pattern!;
        Assert.Equal("exact", (string?)frozen["tagTablePath"]);
        foreach (string release in new[] { "20", "21" })
        {
            foreach (var row in catalog["engineSourceReleases"]![release]!.AsArray())
            {
                string name = (string)row!["currentName"]!;
                if (calls[name]?["arguments"] is JsonObject library)
                    Assert.True(JsonNode.DeepEquals(library, row["arguments"]), release + "/" + name);
            }
            var arguments = catalog["engineSourceReleases"]![release]!.AsArray().Single(r => (string?)r!["currentName"] == "DeleteHmiTag")!["arguments"]!.AsObject();
            Assert.Equal("/Table", (string?)arguments["tagTablePath"]);
            Assert.Matches(pattern, (string?)arguments["tagTablePath"]);
        }
    }
}
