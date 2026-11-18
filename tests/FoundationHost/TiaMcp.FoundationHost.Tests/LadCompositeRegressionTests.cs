extern alias toolpipeline;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class LadCompositeRegressionTests
{
    private static readonly XNamespace Ns = OfflineLadderBuilders.FlgNetNamespace;
    private static string Root => TestPaths.RepositoryRoot(AppContext.BaseDirectory);
    private static JsonObject Symbol(string name) => new() { ["global"] = new JsonArray("MCP_B6_LadDb", name) };
    private static JsonObject Contact(string name, string kind = "no") => new() { ["kind"] = kind, ["operand"] = Symbol(name) };
    private static JsonObject Index() => new() { ["global"] = new JsonArray("MCP_B6_LadDb",
        new JsonObject { ["name"] = "Values", ["index"] = new JsonArray(1) }) };

    // Element-by-element reconstruction of raw-A 023, network 1 (b42e58cf).
    private static JsonObject Block(int stage)
    {
        var series = new JsonArray(Contact("Start"), Contact("Stop", "nc"));
        if (stage >= 1) series.Add(new JsonObject { ["kind"] = "or",
            ["paths"] = new JsonArray(new JsonArray(Contact("Auto")), new JsonArray(Contact("Manual"))) });
        if (stage >= 2) series.Add(new JsonObject { ["kind"] = "compare", ["op"] = "gt", ["type"] = "Int", ["left"] = Symbol("Count"),
            ["right"] = new JsonObject { ["literal"] = new JsonObject { ["type"] = "Int", ["value"] = "0" } } });
        var outputs = new JsonArray(new JsonObject { ["kind"] = "set", ["operand"] = Symbol("Active") });
        if (stage >= 3)
        {
            var move = new JsonObject { ["kind"] = "move",
                ["in"] = new JsonObject { ["literal"] = new JsonObject { ["type"] = "Int", ["value"] = "7" } }, ["out"] = new JsonArray(Index()) };
            if (stage >= 4) move["then"] = new JsonArray(new JsonObject { ["kind"] = "move", ["in"] = Index(),
                ["out"] = new JsonArray(Symbol("LastValue")) });
            outputs.Add(move);
        }
        var rung = new JsonObject { ["series"] = series, ["outputs"] = outputs };
        if (stage >= 5)
        {
            var edge = Contact("Reset", "rising"); edge["memory"] = Symbol("EdgeMem");
            rung["fanOut"] = new JsonArray(new JsonObject { ["series"] = new JsonArray(edge),
                ["outputs"] = new JsonArray(new JsonObject { ["kind"] = "reset", ["operand"] = Symbol("Active") }) });
        }
        return new JsonObject { ["blockName"] = "MCP_B6_LadFc", ["blockNumber"] = 9602, ["titleZhCn"] = "条件与定时",
            ["networks"] = new JsonArray(new JsonObject { ["rung"] = rung }) };
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void Composite_bisection_has_typed_array_indices_and_preserves_OR_compare_ENO_and_edge_wires(int stage)
    {
        var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation).Read(Block(stage).ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        string xml = ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml;
        var flg = XDocument.Parse(xml).Descendants(Ns + "FlgNet").Single();
        FlgNetRungXmlBuilder.Verify(flg);
        var indices = flg.Descendants(Ns + "Component").Where(component => (string?)component.Attribute("AccessModifier") == "Array").ToArray();
        Assert.Equal(stage < 3 ? 0 : stage < 4 ? 1 : 2, indices.Length);
        foreach (var index in indices)
        {
            var access = Assert.Single(index.Elements(Ns + "Access"));
            Assert.Equal("LiteralConstant", (string?)access.Attribute("Scope"));
            Assert.Equal("DInt", access.Element(Ns + "Constant")!.Element(Ns + "ConstantType")!.Value);
            Assert.Equal("1", access.Element(Ns + "Constant")!.Element(Ns + "ConstantValue")!.Value);
        }
        XElement Part(string name) => flg.Descendants(Ns + "Part").First(part => (string?)part.Attribute("Name") == name);
        XElement Wire(XElement part, string pin) => flg.Descendants(Ns + "Wire").Single(w => w.Elements().First() is { } source
            && (string?)source.Attribute("UId") == (string?)part.Attribute("UId") && (string?)source.Attribute("Name") == pin);
        if (stage >= 2)
        {
            Assert.Equal("Int", Part("Gt").Element(Ns + "TemplateValue")!.Value);
            Assert.Contains(Wire(Part("O"), "out").Elements(Ns + "NameCon"), pin => (string?)pin.Attribute("Name") == "pre");
        }
        if (stage >= 4)
        {
            Assert.Null(Part("Move").Attribute("DisabledENO"));
            Assert.Contains(Wire(Part("Move"), "eno").Elements(Ns + "NameCon"), pin => (string?)pin.Attribute("Name") == "en");
        }
        if (stage >= 5)
        {
            Assert.Contains(Wire(Part("Gt"), "out").Elements(Ns + "NameCon"), pin =>
                (string?)pin.Attribute("UId") == (string?)Part("PContact").Attribute("UId") && (string?)pin.Attribute("Name") == "in");
            Assert.Contains(Wire(Part("PContact"), "out").Elements(Ns + "NameCon"), pin =>
                (string?)pin.Attribute("UId") == (string?)Part("RCoil").Attribute("UId"));
        }
        string golden = Path.Combine(Root, "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/LadRung/21", "beta6-composite-" + stage + ".xml");
        if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1") File.WriteAllText(golden, xml, new UTF8Encoding(false));
        Assert.Equal(File.ReadAllBytes(golden), Encoding.UTF8.GetBytes(xml));
        var sdkRoot = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        if (!string.IsNullOrEmpty(sdkRoot))
        {
            var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            schemas.Add(Ns.NamespaceName, Path.Combine(sdkRoot, "TIA_V21_PublicAPI/V21/Schemas/SW.PlcBlocks.LADFBD_v5.xsd"));
            schemas.Compile();
            var issues = new List<string>();
            new XDocument(new XElement(flg)).Validate(schemas, (_, e) => issues.Add(e.Message));
            Assert.Empty(issues);
        }
    }

    [Theory, InlineData("rung"), InlineData("rung-boxes")]
    public async Task Maintained_operation_names_are_retrievable_and_return_executable_builder_arguments(string operation)
    {
        using var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, "21", false);
        var guide = roster.Single(tool => tool.ProtocolTool.Name == "GetToolUsage");
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        var result = await guide.InvokeAsync(new RequestContext<CallToolRequestParams>(server) { Params = new() { Name = "GetToolUsage",
            Arguments = new Dictionary<string, JsonElement> { ["toolName"] = JsonSerializer.SerializeToElement("BuildPlcLadFcBlock"),
                ["operation"] = JsonSerializer.SerializeToElement(operation) } } });
        var body = JsonNode.Parse(Assert.IsType<TextContentBlock>(result.Content.Single()).Text)!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Contains(body["data"]!["operations"]!.AsArray(), row => (string?)row!["operation"] == operation);
        var arguments = body["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject();
        Assert.False(arguments.ContainsKey(""));
        Assert.False(arguments.ContainsKey("operation"));
        var tool = roster.Single(tool => tool.ProtocolTool.Name == "BuildPlcLadFcBlock");
        var built = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(server) { Params = new() { Name = tool.ProtocolTool.Name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } });
        Assert.True((bool?)built.StructuredContent!["ok"], built.StructuredContent.ToJsonString());
    }
}
