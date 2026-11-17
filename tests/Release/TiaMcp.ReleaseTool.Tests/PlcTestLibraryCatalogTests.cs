using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlcTestLibraryCatalogTests
{
    [Fact]
    public void New_host_family_tool_is_generated_on_all_releases_without_a_frozen_source_row_and_stays_full_only()
    {
        string root = Repository.FindRoot();
        var outputs = CatalogGenerator.Generate(root);
        var resource = XDocument.Parse(outputs[CatalogGenerator.Resource]);
        var catalog = JsonNode.Parse(resource.Descendants("data").Single(e => (string?)e.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string release in CatalogJson.Keys)
        {
            var row = Assert.Single(catalog["releases"]![release]!.AsArray(), r => (string?)r?["currentName"] == "ManagePlcTestLibrary")!;
            Assert.Contains("full", CatalogJson.Strings(row["profiles"]));
            Assert.DoesNotContain("lite", CatalogJson.Strings(row["profiles"]));
            Assert.Equal("FILE", new CatalogData(root).HostOnly["ManagePlcTestLibrary"]);
            Assert.Equal("host", TiaMcpServer.ModelContextProtocol.ToolExecution.Table["ManagePlcTestLibrary"]);
        }
        var calls = CatalogJson.Read(root, "reference/tool-examples/calls.json");
        ToolUsageGenerator.ValidateFoundation(root, calls, new JsonArray());
        calls["profiles"]!["plc-foundation"]!["UnregisteredLibraryTool"] = new JsonObject { ["arguments"] = new JsonObject() };
        Assert.Throws<ReleaseException>(() => ToolUsageGenerator.ValidateFoundation(root, calls, new JsonArray()));
    }
}
