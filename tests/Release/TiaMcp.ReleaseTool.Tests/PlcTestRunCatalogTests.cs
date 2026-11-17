using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlcTestRunCatalogTests
{
    [Fact]
    public void Batch_runner_is_generated_on_all_releases_with_full_profile_and_online_write_classification()
    {
        string root = Repository.FindRoot();
        var outputs = CatalogGenerator.Generate(root);
        var resource = XDocument.Parse(outputs[CatalogGenerator.Resource]);
        var catalog = JsonNode.Parse(resource.Descendants("data").Single(e => (string?)e.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string release in CatalogJson.Keys)
        {
            var row = Assert.Single(catalog["releases"]![release]!.AsArray(), r => (string?)r?["currentName"] == "RunPlcTestLibrary")!;
            Assert.Contains("full", CatalogJson.Strings(row["profiles"]));
            Assert.DoesNotContain("lite", CatalogJson.Strings(row["profiles"]));
            Assert.Equal("full-engine", (string?)row["exampleSource"]);
            Assert.True((bool?)row["arguments"]?["dryRun"]);
        }
        Assert.Equal("ONLINE-WRITE", new CatalogData(root).HostOnly["RunPlcTestLibrary"]);
        Assert.Equal("host", TiaMcpServer.ModelContextProtocol.ToolExecution.Table["RunPlcTestLibrary"]);
    }
}
