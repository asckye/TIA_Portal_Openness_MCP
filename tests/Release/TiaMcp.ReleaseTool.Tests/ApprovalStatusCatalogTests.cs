using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ApprovalStatusCatalogTests
{
    [Fact]
    public void Approval_status_generates_no_argument_offline_examples_on_every_release()
    {
        string root = Repository.FindRoot();
        var data = new CatalogData(root);
        Assert.Equal("READ", data.HostOnly["GetApprovalStatus"]);
        var family = TiaMcp.Adapters.Contracts.PortedFamilies.ForTool("GetApprovalStatus");
        Assert.Equal("F01", family.Name);
        Assert.Equal("host-meta", family.OperationPrefix);
        Assert.Equal("host", TiaMcpServer.ModelContextProtocol.ToolExecution.Table["GetApprovalStatus"]);
        var outputs = CatalogGenerator.Generate(root);
        var runtime = JsonNode.Parse(XDocument.Parse(outputs[CatalogGenerator.Resource]).Descendants("data")
            .Single(element => (string?)element.Attribute("name") == "Catalog").Element("value")!.Value)!;
        foreach (string release in CatalogJson.Keys)
        {
            Assert.True(TiaMcp.Adapters.Contracts.PortedFamilies.Available(release, "GetApprovalStatus"));
            var row = Assert.Single(runtime["releases"]![release]!.AsArray(), value => (string?)value!["currentName"] == "GetApprovalStatus")!;
            Assert.Empty(row["arguments"]!.AsObject());
            Assert.Contains(row["profiles"]!.AsArray(), profile => (string?)profile == "lite");
        }
        var examples = CatalogJson.Read(root, "reference/tool-examples/calls.json");
        ToolUsageGenerator.ValidateFoundation(root, examples, CatalogJson.Read(root, "reference/tool-examples/sequences.json").AsArray());
        Assert.All(examples["profiles"]!.AsObject(), profile =>
            Assert.Empty(profile.Value!["GetApprovalStatus"]!["arguments"]!.AsObject()));
    }
}
