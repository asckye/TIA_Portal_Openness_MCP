using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class SimaticCallTests
{
    [Fact]
    public void V17_export_resolves_the_FC_in_metrics_documentation_and_references()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SimaticMl", "17");
        string file = Path.Combine(directory, "MCP_RC_FB.xml");
        var xml = XDocument.Load(file);
        string code = string.Join("\n", xml.Descendants().Where(e => e.Name.LocalName == "NetworkSource").Select(LadTextRenderer.Code));
        Assert.Contains("\"MCP_RC_FC\"(", code);
        Assert.DoesNotContain("\"\"MCP_RC_FC\"\"", code);
        var doc = OfflineAnalysisLogic.ParseFile(file);
        Assert.Equal("FC:MCP_RC_FC", Assert.Single(doc.BlockCalls));
        Assert.Contains("\"MCP_RC_FC\"(", string.Join("\n", doc.Networks.SelectMany(n => n.SourceLines)));
        var result = PlcOfflineReferences.Analyze(directory, "callees", "MCP_RC_FB", 10, 0, 100);
        var edge = Assert.Single(result["rows"]!.AsArray());
        Assert.Equal("MCP_RC_FB.xml", (string?)edge?["caller"]);
        Assert.Equal("MCP_RC_FC.xml", (string?)edge?["callee"]);
        Assert.Empty(result["warnings"]!.AsArray());
        Assert.Empty(result["unresolvedCalls"]!.AsArray());
    }

    [Fact]
    public void Graph_documentation_labels_the_FC_without_repeating_it_as_an_instance()
    {
        var graph = PlcDocumentationLogic.ParseFlgNet(XElement.Parse("""
            <FlgNet><Parts><Call UId="1"><CallInfo BlockType="FC">
              <Instance Scope="GlobalVariable"><Component Name="Calculate"/></Instance>
            </CallInfo></Call></Parts></FlgNet>
            """));
        var node = Assert.Single(graph.Nodes, n => n.Id == "P1");
        Assert.Equal("Call \"Calculate\"", node.Label);
        Assert.Empty(node.Instance);
    }

    [Theory]
    [InlineData("FB", "GlobalVariable", "Static", "", "inst", false)]
    [InlineData("FB", "LocalVariable", "Static", "Child", "inst", true)]
    [InlineData("FB", "LocalVariable", "InOut", "", "inst", true)]
    [InlineData("FC", "GlobalVariable", "Static", "inst", null, false)]
    [InlineData("FC", null, "Static", "", null, false)]
    [InlineData("FC", "LocalVariable", "Static", "", null, false)]
    public void Instance_selector_depends_on_block_type_scope_and_static_declaration(
        string blockType, string? scope, string section, string callee, string? instancePath, bool local)
    {
        string xml = $$"""
            <Document><SW.Blocks.FB><AttributeList><Name>Controller</Name>
              <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v4">
                <Section Name="{{section}}"><Member Name="inst" Datatype="&quot;Child&quot;"/></Section>
              </Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList>
              <NetworkSource><StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3">
                <Access Scope="Call"><CallInfo BlockType="{{blockType}}">
                  <Instance {{(scope == null ? "" : "Scope=\"" + scope + "\"")}}><Component Name="inst"/></Instance>
                  <Token Text="("/><Token Text=")"/>
                </CallInfo></Access></StructuredText></NetworkSource>
              <ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>
            """;
        var call = XDocument.Parse(xml).Descendants().Single(e => e.Name.LocalName == "CallInfo");
        var target = OfflineAnalysisLogic.ReadCall(call);
        Assert.Equal(blockType, target.BlockType);
        Assert.Equal(callee, target.Callee);
        Assert.Equal(instancePath, target.InstancePath);
        Assert.Equal(local, target.LocalInstance);
        if (blockType == "FC" && callee.Length == 0) Assert.Null(target.Selector);
        else Assert.Same(call.Elements().First(), target.Selector);
        var doc = OfflineAnalysisLogic.ParseText(xml, "Controller.xml");
        if (callee.Length == 0) Assert.Empty(doc.BlockCalls);
        else Assert.Equal(blockType + ":" + callee, Assert.Single(doc.BlockCalls));
        if (blockType == "FB" && callee.Length == 0) Assert.Contains("Unresolved FB instance: inst", doc.Warnings);
        if (blockType == "FC" && callee.Length == 0)
        {
            Assert.Contains("inst()", string.Join("\n", doc.Networks.SelectMany(n => n.SourceLines)));
            var graph = PlcDocumentationLogic.ParseFlgNet(new XElement("FlgNet",
                new XElement("Parts", new XElement("Call", new XAttribute("UId", "1"), call))));
            var node = Assert.Single(graph.Nodes, n => n.Id == "P1");
            string instance = scope == "LocalVariable" ? "#inst" : "inst";
            Assert.Equal(instance, node.Instance);
            Assert.Equal("Call [" + instance + "]", node.Label);
        }
    }
}
