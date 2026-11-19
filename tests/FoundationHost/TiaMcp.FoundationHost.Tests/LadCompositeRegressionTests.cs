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
        var block = Block(stage);
        string? historical = null;
        if (stage == 5)
        {
            historical = LadRungBuilderTests.EmitRejectedGolden(block, "21");
            var rung = block["networks"]![0]!["rung"]!.AsObject();
            var reset = rung["fanOut"]![0]!.DeepClone();
            rung.Remove("fanOut");
            block["networks"]!.AsArray().Add(new JsonObject { ["rung"] = reset });
        }
        var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation).Read(block.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        string xml = ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml;
        if (stage == 5) Assert.Equal(2, XDocument.Parse(xml).Descendants(Ns + "FlgNet").Count());
        xml = historical ?? xml;
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
        var issues = V21SchemaIssues(flg);
        Assert.Empty(issues);
    }

    private static List<string> V21SchemaIssues(XElement flg)
    {
        var issues = new List<string>();
        var sdkRoot = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        if (!string.IsNullOrEmpty(sdkRoot))
        {
            var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            schemas.Add(Ns.NamespaceName, Path.Combine(sdkRoot, "TIA_V21_PublicAPI/V21/Schemas/SW.PlcBlocks.LADFBD_v5.xsd"));
            schemas.Compile();
            new XDocument(new XElement(flg)).Validate(schemas, (_, e) => issues.Add(e.Message));
        }
        return issues;
    }

    private static void AssertPublishedRung(XElement[] networks)
    {
        var network = Assert.Single(networks);
        var parts = network.Descendants(Ns + "Part").Select(part => (string?)part.Attribute("Name")).ToArray();
        Assert.DoesNotContain(parts, name => name is "PContact" or "NContact" or "RCoil");
        Assert.Equal(new[] { "Contact", "Contact", "Contact", "Contact", "Gt", "Lt", "Move", "Move", "O", "SCoil" },
            parts.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("operand", (string?)Assert.Single(network.Descendants(Ns + "Negated")).Attribute("Name"));
        Assert.Single(network.Descendants(Ns + "Powerrail"));
        Assert.Contains(network.Descendants(Ns + "Wire"), wire => wire.Elements(Ns + "NameCon")
            .Select(pin => (string?)pin.Attribute("Name")).SequenceEqual(new[] { "eno", "en" }));
    }

    [Theory]
    [InlineData("rising", "PContact")]
    [InlineData("falling", "NContact")]
    public async Task Public_builder_refuses_unverified_edge_contacts(string kind, string nativePart)
    {
        var block = Block(0);
        var edge = Contact("Reset", kind);
        edge["memory"] = Symbol("EdgeMem");
        block["networks"]![0]!["rung"]!["series"] = new JsonArray(edge);
        var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation).Read(block.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid);
        var candidate = ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation);
        Assert.Contains(XDocument.Parse(candidate.Xml).Descendants(Ns + "Part"), part => (string?)part.Attribute("Name") == nativePart);

        using var worker = new FakeWorker();
        var tool = LegacyHostToolRegistry.Create(worker, "21", false).Single(t => t.ProtocolTool.Name == "BuildPlcLadFcBlock");
        var args = new JsonObject { ["ladFcBlock"] = block, ["outputReleaseKey"] = "21" };
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = tool.ProtocolTool.Name, Arguments = args.Deserialize<Dictionary<string, JsonElement>>() } });
        var reply = result.StructuredContent!;
        Assert.True(result.IsError, reply.ToJsonString());
        Assert.Equal("PRECONDITION_FAILED", (string?)reply["error"]?["code"]);
        Assert.Equal("edge-contact-unverified", (string?)reply["error"]?["details"]?["condition"]);
        Assert.Null(reply["data"]?["xml"]);

        using var engineWorker = new WritePreviewCoverageTests.PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", engineWorker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var engine = new toolpipeline::TiaMcpServer.ModelContextProtocol.XmlBuilderTools();
        var full = engine.ComposePlcLadFcBlockXmlV4(input.Value!).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)full["error"]?["code"]);
        Assert.Equal("edge-contact-unverified", (string?)full["error"]?["details"]?["condition"]);
        Assert.Null(full["data"]?["xml"]);
        Assert.Equal(0, engineWorker.Calls);
    }

    [Theory, InlineData("plc-foundation"), InlineData("full-engine")]
    public void Maintained_rung_source_contains_only_the_verified_network(string profile)
    {
        var profiles = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "reference/tool-examples/calls.json")))!["profiles"]!;
        var args = profiles[profile]!["BuildPlcLadFcBlock"]!["operations"]!["rung"]!["arguments"]!;
        var contract = profile == "full-engine" ? ConstructionProfile.FullEngine : ConstructionProfile.Foundation;
        var input = new ConstructionInput<LadFcBlockSpec>(contract).Read(args["ladFcBlock"]!.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        var candidate = ConstructionAdapter.BuildCandidate(input.Value!, "21", contract);
        var networks = XDocument.Parse(candidate.Xml).Descendants(Ns + "FlgNet").ToArray();
        AssertPublishedRung(networks);
        Assert.All(networks, network => Assert.Empty(V21SchemaIssues(network)));
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
        var networks = XDocument.Parse((string)built.StructuredContent["data"]!["xml"]!).Descendants(Ns + "FlgNet").ToArray();
        Assert.All(networks, network => Assert.Empty(V21SchemaIssues(network)));
        if (operation == "rung")
        {
            AssertPublishedRung(networks);
        }
    }
}
