extern alias toolpipeline;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaGitAddIn.Services.SimaticMl;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class LadRungBuilderTests
{
    private static readonly XNamespace Ns = OfflineLadderBuilders.FlgNetNamespace;
    private static string Root => TestPaths.RepositoryRoot(AppContext.BaseDirectory);
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
    private static JsonObject Symbol(string name = "A", string scope = "global") => new() { [scope] = new JsonArray(name) };
    private static JsonObject Contact(string kind = "no", string name = "A") => new() { ["kind"] = kind, ["operand"] = Symbol(name) };
    private static JsonObject Coil(string kind = "coil", string name = "Q") => new() { ["kind"] = kind, ["operand"] = Symbol(name) };
    private static JsonObject Rung(JsonArray? series = null, JsonArray? outputs = null) => new()
        { ["series"] = series ?? new JsonArray(), ["outputs"] = outputs ?? new JsonArray(Coil()) };
    private static JsonObject Move() => new()
        { ["kind"] = "move", ["in"] = new JsonObject { ["literal"] = new JsonObject { ["type"] = "Int", ["value"] = "42" } },
            ["out"] = new JsonArray(Symbol("Destination")) };
    private static JsonObject Compare(string op = "gt") => new()
        { ["kind"] = "compare", ["op"] = op, ["type"] = "Int", ["left"] = Symbol("Value"),
            ["right"] = new JsonObject { ["literal"] = new JsonObject { ["type"] = "Int", ["value"] = "10" } } };
    private static JsonObject Block(JsonNode rung) => new()
        { ["blockName"] = "FC_Rung", ["blockNumber"] = 42, ["inputs"] = new JsonArray(new JsonObject { ["name"] = "Local", ["datatype"] = "Int" }),
            ["outputs"] = new JsonArray(), ["networks"] = new JsonArray(new JsonObject { ["rung"] = rung }) };

    internal static string EmitRejectedGolden(JsonObject block, string release)
    {
        var spec = new LadFcBlockSpec(JsonSerializer.SerializeToElement(block));
        var refusal = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation, null, release).Read(spec.Json, "ladFcBlock");
        Assert.False(refusal.IsValid);
        var details = Assert.IsType<InvalidArgumentDetails>(refusal.Error!.Details);
        Assert.Equal("/ladFcBlock/networks/0/rung/fanOut/0/series/0", details.Parameter);
        Assert.Contains("own network", details.Expected);
        Assert.Throws<InputRejectionException>(() => ConstructionAdapter.BuildLadCandidateForRelease(spec, release, ConstructionProfile.Foundation));
        string json = block.ToJsonString();
        Assert.Throws<ArgumentException>(() => OfflineLadderBuilders.BuildForRelease("BuildPlcLadFcBlock", release, json));
        Assert.Throws<ArgumentException>(() => PlcBuilderToolJson.ComposeLadFcBlockForRelease(json, release));

        // Keep exercising the historical XML emitter and golden after this shape is refused at admission.
        var network = Assert.Single(spec.Networks);
        var inputs = (spec.Inputs ?? Array.Empty<Member>())
            .Select(member => new PlcBlockMemberDefinition(member.Name, member.Datatype, member.EffectiveComment));
        var flg = FlgNetRungXmlBuilder.Build(network.Rung!, FlgNetFormat.ForRelease(release), Array.Empty<string>());
        var xml = PlcLadFcBlockXmlComposer.Compose(spec.BlockName, spec.BlockNumber, inputs, Array.Empty<PlcBlockMemberDefinition>(),
            new[] { new PlcLadFcBlockXmlComposer.LadNetwork(flg, network.TitleZhCn ?? "", network.CommentZhCn ?? "") },
            spec.CommentZhCn ?? "", spec.TitleZhCn ?? "", release);
        return ConstructionAdapter.Entitize(xml);
    }

    internal static string? HistoricalFanOut(string element, JsonObject block, string release)
    {
        if (element != "fanout") return null;
        string historical = EmitRejectedGolden(block, release);
        // A compare can start this output branch. Exercise every entry point on the supported variant as well.
        block["networks"]![0]!["rung"]!["fanOut"]![0]!["series"]![0] = Compare();
        return historical;
    }

    public static IEnumerable<object[]> Elements()
    {
        foreach (var box in LadRungBoxBuilderTests.Elements()) yield return box;
        foreach (string kind in new[] { "no", "nc", "rising", "falling" })
        {
            var contact = Contact(kind);
            if (kind is "rising" or "falling") contact["memory"] = Symbol("EdgeMemory");
            yield return new object[] { kind, Rung(new JsonArray(contact)).ToJsonString() };
        }
        foreach (string op in new[] { "eq", "ne", "gt", "ge", "lt", "le" })
            yield return new object[] { op, Rung(new JsonArray(Compare(op))).ToJsonString() };
        foreach (string kind in new[] { "coil", "set", "reset" })
            yield return new object[] { kind, Rung(outputs: new JsonArray(Coil(kind))).ToJsonString() };
        yield return new object[] { "or", Rung(new JsonArray(new JsonObject { ["kind"] = "or",
            ["paths"] = new JsonArray(new JsonArray(Contact()), new JsonArray(Contact("nc", "B"))) })).ToJsonString() };
        var fan = Rung(new JsonArray(Contact()));
        fan["fanOut"] = new JsonArray(new JsonObject { ["series"] = new JsonArray(Contact("no", "B")), ["outputs"] = new JsonArray(Coil("reset", "OtherQ")) });
        yield return new object[] { "fanout", fan.ToJsonString() };
        yield return new object[] { "move", Rung(outputs: new JsonArray(Move())).ToJsonString() };
        var move = Move(); move["then"] = new JsonArray(Move(), Coil());
        yield return new object[] { "move-eno", Rung(outputs: new JsonArray(move)).ToJsonString() };
        move = Move(); move["in"] = new JsonObject { ["constant"] = "MAX_VALUE" };
        move["out"] = new JsonArray(Symbol("First"), Symbol("Second"));
        yield return new object[] { "constant", Rung(outputs: new JsonArray(move)).ToJsonString() };
        move = Move(); move["in"] = new JsonObject { ["typed"] = "T#500ms" };
        yield return new object[] { "typed", Rung(outputs: new JsonArray(move)).ToJsonString() };
        move = Move(); move["in"] = new JsonObject
            { ["global"] = new JsonArray("DB", new JsonObject { ["name"] = "Values", ["index"] = new JsonArray(-1, 2) }, "Value") };
        move["out"] = new JsonArray(Symbol("Local", "local"));
        yield return new object[] { "indexed-local", Rung(outputs: new JsonArray(move)).ToJsonString() };
        var time = Compare(); time["type"] = "LTime";
        time["right"] = new JsonObject { ["typed"] = "LT#1s" };
        yield return new object[] { "ltime", Rung(new JsonArray(time)).ToJsonString() };
    }

    [Theory]
    [MemberData(nameof(Elements))]
    public void GoldenDeterminismEntryPointsAndReaders(string element, string rungJson)
    {
        var block = Block(Parse(rungJson));
        var historical = HistoricalFanOut(element, block, "21");
        var contract = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation);
        var input = contract.Read(block.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        string xml = ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml;
        Assert.Equal(xml, ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml);
        Assert.Equal(xml, OfflineLadderBuilders.Build("BuildPlcLadFcBlock", "21", block.ToJsonString())["Xml"]!.GetValue<string>());
        var engineXml = PlcBuilderToolJson.ComposeLadFcBlock(block.ToJsonString())["xml"]!.GetValue<string>();
        Assert.True(XNode.DeepEquals(XDocument.Parse(xml), XDocument.Parse(engineXml)));
        xml = historical ?? xml;
        var flg = XDocument.Parse(xml).Descendants(Ns + "FlgNet").Single();
        string golden = Path.Combine(Root, "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/LadRung/21", element + ".xml");
        bool regenerate = Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1";
        if (regenerate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, xml, new UTF8Encoding(false));
        }
        Assert.Equal(File.ReadAllBytes(golden), Encoding.UTF8.GetBytes(xml));
        var rendered = LadTextRenderer.Render(xml);
        Assert.DoesNotContain("不支持", rendered);
        foreach (var access in flg.Element(Ns + "Parts")!.Elements(Ns + "Access"))
            Assert.Contains(LadTextRenderer.Operand(access).text, rendered);
        var parsed = SimaticMlParser.ParseText(xml, SimaticMlParserLimits.Default, TiaGitAddIn.Models.Comparison.PlcRevisionSide.Left, default);
        Assert.True(parsed.IsSuccess);
        var network = parsed.Model!.Blocks.Single().CompileUnits.Single().Network!;
        var unknown = new HashSet<string>();
        string svg = PlcLadderDrawing.NetworkSvg(network, _ => null, unknown);
        Assert.Empty(unknown);
        Assert.Contains("<svg", svg);
        if (regenerate) Assert.Fail($"LAD goldens were regenerated: {golden}. Unset TIA_MCP_UPDATE_GOLDEN and rerun to verify.");
    }

    [Theory]
    [InlineData("MCPVerify_FC_LAD.xml", 0, "and")]
    [InlineData("MCPVerify_FC_LAD.xml", 1, "or")]
    [InlineData("MCPVerify_FC_LAD.xml", 2, "set")]
    [InlineData("MCPVerify_FC_LAD.xml", 3, "reset")]
    [InlineData("MCPVerify_FC_LAD.xml", 4, "gt")]
    [InlineData("MCPVerify_FC_LAD.xml", 5, "move")]
    [InlineData("MCPVerify_FB_LAD_v3.xml", 3, "lt")]
    public void CookbookPartsAndWireTopologyAreEquivalent(string file, int networkIndex, string kind)
    {
        var expected = XDocument.Load(Path.Combine(Root, "templates/mcp-full-e2e-verify/plc/blocks", file))
            .Descendants(Ns + "FlgNet").ElementAt(networkIndex);
        var accesses = expected.Element(Ns + "Parts")!.Elements(Ns + "Access").ToArray();
        JsonObject Op(int index)
        {
            var access = accesses[index];
            if ((string?)access.Attribute("Scope") == "LiteralConstant")
                return new JsonObject { ["literal"] = new JsonObject { ["type"] = access.Descendants(Ns + "ConstantType").Single().Value,
                    ["value"] = access.Descendants(Ns + "ConstantValue").Single().Value } };
            return new JsonObject { ["local"] = new JsonArray(access.Descendants(Ns + "Component")
                .Select(c => (JsonNode)JsonValue.Create(c.Attribute("Name")!.Value)!).ToArray()) };
        }
        JsonObject Cond(int index) => new() { ["kind"] = "no", ["operand"] = Op(index) };
        var series = new JsonArray();
        JsonObject output;
        if (kind is "gt" or "lt")
        {
            series.Add(new JsonObject { ["kind"] = "compare", ["op"] = kind,
                ["type"] = expected.Descendants(Ns + "TemplateValue").Single().Value, ["left"] = Op(0), ["right"] = Op(1) });
            output = new JsonObject { ["kind"] = "coil", ["operand"] = Op(2) };
        }
        else if (kind == "move") output = new JsonObject { ["kind"] = "move", ["in"] = Op(0), ["out"] = new JsonArray(Op(1)) };
        else
        {
            if (kind == "and") { series.Add(Cond(0)); series.Add(Cond(1)); }
            else if (kind == "or") series.Add(new JsonObject { ["kind"] = "or",
                ["paths"] = new JsonArray(new JsonArray(Cond(0)), new JsonArray(Cond(1))) });
            else series.Add(Cond(0));
            output = new JsonObject { ["kind"] = kind is "set" or "reset" ? kind : "coil", ["operand"] = Op(accesses.Length - 1) };
        }
        var locals = accesses.Descendants(Ns + "Component").Select(c => c.Attribute("Name")!.Value).ToArray();
        var rung = ConstructionJson.Deserialize<LadRung>(Rung(series, new JsonArray(output)).ToJsonString(), locals);
        var actual = FlgNetRungXmlBuilder.Build(rung, new FlgNetFormat(), locals);
        Assert.Equal(Canonical(expected), Canonical(actual));
    }

    private static string Canonical(XElement flg)
    {
        var copy = new XElement(flg);
        var parts = copy.Element(Ns + "Parts")!.Descendants().Where(p => p.Attribute("UId") != null)
            .Concat(copy.Descendants(Ns + "OpenCon")).ToArray();
        var ids = parts.Select((p, i) => (Id: p.Attribute("UId")!.Value, Index: i.ToString(CultureInfo.InvariantCulture)))
            .ToDictionary(p => p.Id, p => p.Index);
        foreach (var element in copy.Descendants().Where(e => e.Attribute("UId") != null))
            if (element.Name.LocalName == "Wire") element.Attribute("UId")!.Remove();
            else element.SetAttributeValue("UId", ids[element.Attribute("UId")!.Value]);
        foreach (var element in copy.DescendantsAndSelf())
            element.ReplaceAttributes(element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToArray());
        var wires = copy.Element(Ns + "Wires")!;
        foreach (var wire in wires.Elements().ToArray()) wire.ReplaceNodes(wire.Elements().OrderBy(e => e.ToString(SaveOptions.DisableFormatting)).ToArray());
        wires.ReplaceNodes(wires.Elements().OrderBy(w => w.ToString(SaveOptions.DisableFormatting)).ToArray());
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    [Fact]
    public void TimerMatchesCookbookTopologyWithGlobalInstanceForAnFc()
    {
        var expected = XDocument.Load(Path.Combine(Root, "templates/mcp-full-e2e-verify/plc/blocks/MCPVerify_FB_LAD_v3.xml"))
            .Descendants(Ns + "FlgNet").Single(f => f.Descendants(Ns + "Part").Any(p => (string?)p.Attribute("Name") == "TON"));
        // The cookbook is an FB with a static timer; the generated FC requires a global instance.
        expected.Descendants(Ns + "Instance").Single().SetAttributeValue("Scope", "GlobalVariable");
        var contact = Contact(); contact["operand"] = Symbol("Trig", "local");
        var coil = Coil(); coil["operand"] = Symbol("OutTonQ", "local");
        var timer = new JsonObject { ["kind"] = "timer", ["type"] = "TON", ["instance"] = Symbol("tonInst"),
            ["pt"] = new JsonObject { ["typed"] = "T#500ms" }, ["q"] = new JsonArray(coil) };
        var locals = new[] { "Trig", "OutTonQ" };
        var rung = ConstructionJson.Deserialize<LadRung>(Rung(new JsonArray(contact), new JsonArray(timer)).ToJsonString(), locals);
        Assert.Equal(Canonical(expected), Canonical(FlgNetRungXmlBuilder.Build(rung, new FlgNetFormat(), locals)));
    }

    [Theory]
    [InlineData(2)][InlineData(8)]
    public void OrPathsAndSharedPrefixEachUseOneSourceWire(int count)
    {
        var paths = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonArray(Contact("no", "Path" + i))).ToArray());
        var rung = Rung(new JsonArray(Contact(), new JsonObject { ["kind"] = "or", ["paths"] = paths }), new JsonArray(Coil(), Move()));
        rung["fanOut"] = new JsonArray(new JsonObject
            { ["series"] = new JsonArray(Compare(), Contact("no", "Tail")), ["outputs"] = new JsonArray(Coil("set")) });
        var input = new ConstructionInput<LadFcBlockSpec>().Read(Block(rung).ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid);
        var xml = XDocument.Parse(ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml);
        var flg = xml.Descendants(Ns + "FlgNet").Single();
        var sourceKeys = flg.Element(Ns + "Wires")!.Elements().Select(w => w.Elements().First().ToString()).ToArray();
        Assert.Equal(sourceKeys.Length, sourceKeys.Distinct().Count());
        string text = LadTextRenderer.Render(xml.ToString());
        for (int i = 0; i < count; i++) Assert.Contains("Path" + i, text);
        Assert.Contains(flg.Descendants(Ns + "Wire"), wire => wire.Elements(Ns + "NameCon").Count() == count + 1);
    }

    private sealed class CountingTool : McpServerTool
    {
        internal int Calls;
        public override Tool ProtocolTool => new OfflineLadderTool("BuildPlcLadFcBlock", "ladFcBlockJson").ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Rejected inputs must never reach this tool."); }
    }

    public static IEnumerable<object[]> InvalidRungs()
    {
        foreach (var invalid in LadRungBoxBuilderTests.InvalidRungs()) yield return invalid;
        yield return new object[] { """{"series":[],"outputs":[]}""", "/outputs" };
        yield return new object[] {
            """{"series":[{"kind":"no","operand":{"constant":"SECRET_CANARY"}}],"outputs":[{"kind":"coil","operand":{"global":["Q"]}}]}""",
            "/series/0/operand" };
        yield return new object[] { """{"series":[{"kind":"rising","operand":{"global":["A"]},"memory":{"local":["SECRET_CANARY"]}}],"outputs":[{"kind":"coil","operand":{"global":["Q"]}}]}""", "/series/0/memory" };
        var rung = Rung(new JsonArray(Contact())); rung["series"]![0]!["operand"] = Symbol("SECRET_CANARY", "local");
        yield return new object[] { rung.ToJsonString(), "/series/0/operand" };
        rung = Rung(new JsonArray(Contact())); rung["series"]![0]!["operand"] = Symbol("SECRET_CANARY/path");
        yield return new object[] { rung.ToJsonString(), "/series/0/operand/global/0" };
        var move = Move(); move["in"] = new JsonObject { ["typed"] = "SECRET_CANARY" };
        yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/in/typed" };
        move = Move(); move["out"] = new JsonArray(new JsonObject { ["constant"] = "SECRET_CANARY" });
        yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/out/0" };
        move = Move(); move["then"] = new JsonArray();
        yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/then" };
        var coil = Coil(); coil["then"] = new JsonArray(Coil());
        yield return new object[] { Rung(outputs: new JsonArray(coil)).ToJsonString(), "/outputs/0" };
        rung = Rung(new JsonArray(new JsonObject { ["kind"] = "or", ["paths"] = new JsonArray(new JsonArray(), new JsonArray(Contact())) }));
        yield return new object[] { rung.ToJsonString(), "/series/0/paths/0" };
        rung = Rung(new JsonArray(new JsonObject { ["kind"] = "or", ["paths"] = new JsonArray(
            new JsonArray(new JsonObject { ["kind"] = "or", ["paths"] = new JsonArray(new JsonArray(Contact()), new JsonArray(Contact())) }),
            new JsonArray(Contact())) }));
        yield return new object[] { rung.ToJsonString(), "/series/0/paths/0/0" };
        var cmp = Compare(); cmp["op"] = "SECRET_CANARY";
        yield return new object[] { Rung(new JsonArray(cmp)).ToJsonString(), "/series/0/op" };
        cmp = Compare(); cmp["type"] = "SECRET_CANARY";
        yield return new object[] { Rung(new JsonArray(cmp)).ToJsonString(), "/series/0/type" };
        move = Move(); move["in"] = new JsonObject { ["constant"] = "SECRET_CANARY/path" };
        yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/in/constant" };
        rung = Rung(); rung["fanOut"] = new JsonArray(new JsonObject { ["series"] = new JsonArray(), ["outputs"] = new JsonArray(Coil()) });
        yield return new object[] { rung.ToJsonString(), "/fanOut/0/series" };
        rung = Rung(); rung["fanOut"] = new JsonArray(new JsonObject { ["series"] = new JsonArray(Contact()), ["outputs"] = new JsonArray() });
        yield return new object[] { rung.ToJsonString(), "/fanOut/0/outputs" };
        var indexed = new JsonObject { ["global"] = new JsonArray("DB",
            new JsonObject { ["name"] = "Values", ["index"] = new JsonArray(1.5) }) };
        var contact = Contact(); contact["operand"] = indexed;
        yield return new object[] { Rung(new JsonArray(contact)).ToJsonString(), "/series/0/operand/global/1/index/0" };
        contact = Contact(); contact["operand"] = Symbol("local", "local");
        yield return new object[] { Rung(new JsonArray(contact)).ToJsonString(), "/series/0/operand" };
        coil = Coil(); coil["operand"] = new JsonObject { ["literal"] = new JsonObject { ["type"] = "Bool", ["value"] = "TRUE" } };
        yield return new object[] { Rung(outputs: new JsonArray(coil)).ToJsonString(), "/outputs/0/operand" };
        rung = Rung(); rung["Series"] = new JsonArray(Contact());
        yield return new object[] { rung.ToJsonString(), "" };
        cmp = Compare(); cmp["left"] = Symbol("SECRET_CANARY", "local");
        rung = Rung(); rung["fanOut"] = new JsonArray(new JsonObject
            { ["series"] = new JsonArray(Contact(), cmp), ["outputs"] = new JsonArray(Coil()) });
        yield return new object[] { rung.ToJsonString(), "/fanOut/0/series/1/left" };
        foreach (var kind in new[] { "no", "nc", "rising", "falling" })
        {
            var head = Contact(kind);
            if (kind is "rising" or "falling") head["memory"] = Symbol("Mem");
            var branch = new JsonObject { ["series"] = new JsonArray(head), ["outputs"] = new JsonArray(Coil()) };
            var split = Rung(new JsonArray(Compare()));
            split["fanOut"] = new JsonArray(branch);
            yield return new object[] { split.ToJsonString(), "/fanOut/0/series/0" };
            split["outputs"] = new JsonArray();
            split["fanOut"]!.AsArray().Insert(0, new JsonObject { ["series"] = new JsonArray(Compare()), ["outputs"] = new JsonArray(Coil()) });
            yield return new object[] { split.ToJsonString(), "/fanOut/1/series/0" };
        }
        foreach (string value in new[] { "", " ", "\t\r\n" })
        {
            move = Move(); move["in"]!["literal"]!["value"] = value;
            yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/in/literal/value" };
        }
        foreach (string value in new[] { "T#", "TIME#", "LT#", "S5T#" })
        {
            move = Move(); move["in"] = new JsonObject { ["typed"] = value };
            yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/in/typed" };
        }
        foreach (string type in new[] { "Time", "LTime" })
        {
            move = Move(); move["in"]!["literal"]!["type"] = type;
            yield return new object[] { Rung(outputs: new JsonArray(move)).ToJsonString(), "/outputs/0/in/literal/type" };
        }
    }

    [Theory]
    [InlineData("series", 33)][InlineData("outputs", 17)][InlineData("fanOut", 9)]
    [InlineData("paths", 9)][InlineData("path", 17)][InlineData("components", 33)]
    [InlineData("indices", 7)][InlineData("moveOut", 9)][InlineData("then", 9)]
    public void RungArrayUpperBoundsRemainLimits(string field, int size)
    {
        JsonArray Repeat(Func<JsonNode> item) => new(Enumerable.Range(0, size).Select(_ => item()).ToArray());
        var rung = Rung();
        switch (field)
        {
            case "series": rung["series"] = Repeat(() => Contact()); break;
            case "outputs": rung["outputs"] = Repeat(() => Coil()); break;
            case "fanOut": rung["fanOut"] = Repeat(() => new JsonObject
                { ["series"] = new JsonArray(Contact()), ["outputs"] = new JsonArray(Coil()) }); break;
            case "paths": case "path": rung["series"] = new JsonArray(new JsonObject { ["kind"] = "or",
                ["paths"] = field == "paths" ? Repeat(() => new JsonArray(Contact()))
                    : new JsonArray(Repeat(() => Contact()), new JsonArray(Contact())) }); break;
            case "components": rung["outputs"]![0]!["operand"] = new JsonObject { ["global"] = Repeat(() => JsonValue.Create("A")!) }; break;
            case "indices": rung["outputs"]![0]!["operand"] = new JsonObject { ["global"] = new JsonArray(
                new JsonObject { ["name"] = "A", ["index"] = Repeat(() => JsonValue.Create(1)!) }) }; break;
            default:
                var move = Move();
                move[field == "moveOut" ? "out" : "then"] = Repeat(() => field == "moveOut" ? Symbol("Q") : Coil());
                rung["outputs"] = new JsonArray(move);
                break;
        }
        var input = new ConstructionInput<LadFcBlockSpec>().Read(Block(rung).ToJsonString(), "ladFcBlock");
        Assert.False(input.IsValid);
        Assert.Equal(ErrorCode.LimitExceeded, input.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidRungs))]
    public async Task RejectionsHaveExactPointersAndNeverCallInnerTool(string rung, string pointer)
    {
        var inner = new CountingTool();
        var tool = new FoundationV4Tool(inner, "21");
        var arguments = new JsonObject { ["ladFcBlock"] = Block(Parse(rung)), ["outputReleaseKey"] = "21" };
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new() { Name = "BuildPlcLadFcBlock",
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
        var result = await tool.InvokeAsync(request);
        var body = result.StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        var pointers = body["error"]!["details"]!["parameters"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "/ladFcBlock/networks/0/rung" + pointer }, pointers);
        if (pointer.EndsWith("/series/0", StringComparison.Ordinal))
            Assert.Contains("Put this logic in its own network", (string?)body["error"]!["details"]!["expected"]);
        Assert.DoesNotContain("SECRET_CANARY", body.ToJsonString());
        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void NetworkDiscriminatorIsExclusive(bool both)
    {
        var block = Block(Rung());
        var network = block["networks"]![0]!.AsObject();
        if (both) network["call"] = Parse("""{"callName":"FC_Call","parameters":[]}""");
        else network.Remove("rung");
        Assert.False(new ConstructionInput<LadFcBlockSpec>().Read(block.ToJsonString(), "ladFcBlock").IsValid);
    }

    [Fact]
    public void DirectTypedDeserializationCannotBypassRungSemantics()
    {
        var contact = Contact(); contact["operand"] = Symbol("Undeclared", "local");
        Assert.Throws<ArgumentException>(() => V4Json.Deserialize<LadFcBlockSpec>(Block(Rung(new JsonArray(contact))).ToJsonString()));
        var valid = Contact(); valid["operand"] = Symbol("Local", "local");
        var spec = V4Json.Deserialize<LadFcBlockSpec>(Block(Rung(new JsonArray(valid))).ToJsonString());
        Assert.Contains("LocalVariable", ConstructionAdapter.BuildCandidate(spec, "21", ConstructionProfile.Foundation).Xml);
    }

    [Theory]
    [InlineData(ConstructionProfile.Foundation)][InlineData(ConstructionProfile.FullEngine)]
    public void MixedNetworksKeepTheCallFragmentBytesAndAllowFanOutOnlyOutputs(ConstructionProfile profile)
    {
        var rung = Rung(outputs: new JsonArray());
        rung["fanOut"] = new JsonArray(new JsonObject { ["series"] = new JsonArray(Contact()), ["outputs"] = new JsonArray(Coil()) });
        var block = Block(rung);
        block["networks"]!.AsArray().Insert(0, new JsonObject { ["call"] = Parse("""{"callName":"FC_Ready","parameters":[]}""") });
        var spec = new ConstructionInput<LadFcBlockSpec>(profile).Read(block.ToJsonString(), "ladFcBlock").Value!;
        var candidate = ConstructionAdapter.BuildCandidate(spec, "21", profile);
        var networks = XDocument.Parse(candidate.Xml).Descendants(Ns + "FlgNet").ToArray();
        Assert.Equal(2, networks.Length);
        Assert.Equal(FlgNetCallXmlBuilder.BuildFlgNet("FC_Ready", Array.Empty<FlgNetCallParameter>()).ToString(), networks[0].ToString());
        Assert.False(candidate.SchemaValidated);
        Assert.False(candidate.ImportValidated);
        Assert.False(candidate.ProgramSemanticsValidated);
        Assert.Equal("NOT RUN", candidate.NativeAcceptance);
    }

    [Theory]
    [InlineData(256, true)][InlineData(257, false)]
    public void PartBudgetCountsAccessesAndPartsBeforeEmission(int count, bool accepted)
    {
        var outputs = new JsonArray(Enumerable.Range(0, 16).Select(_ => (JsonNode)Move()).ToArray());
        foreach (var output in outputs) output!["out"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode)Symbol("Q" + i)).ToArray());
        var series = new JsonArray(Enumerable.Range(0, 32).Select(_ => (JsonNode)Contact()).ToArray());
        var branchSeries = new JsonArray(Enumerable.Range(0, 15).Select(_ => (JsonNode)Contact()).ToArray());
        branchSeries[0] = Compare();
        outputs[0]!["out"]!.AsArray().RemoveAt(7); // One extra compare operand, one fewer MOVE destination.
        if (count == 257) { branchSeries[1]!["kind"] = "rising"; branchSeries[1]!["memory"] = Symbol("Mem"); }
        var rung = Rung(series, outputs);
        rung["fanOut"] = new JsonArray(new JsonObject { ["series"] = branchSeries, ["outputs"] = new JsonArray(Coil()) });
        var result = new ConstructionInput<LadFcBlockSpec>().Read(Block(rung).ToJsonString(), "ladFcBlock");
        Assert.Equal(accepted, result.IsValid);
        if (!accepted) Assert.Equal(ErrorCode.LimitExceeded, result.Error!.Code);
        else Assert.Equal(256, XDocument.Parse(ConstructionAdapter.BuildCandidate(result.Value!, "21", ConstructionProfile.Foundation).Xml)
            .Descendants(Ns + "Parts").Single().Elements().Count());
    }

    [Fact]
    public void DeepIndexedCompareInOrInsideFanOutFitsTheExistingDepthLimit()
    {
        var cmp = Compare();
        cmp["left"] = new JsonObject { ["global"] = new JsonArray("DB", new JsonObject { ["name"] = "A", ["index"] = new JsonArray(1) }) };
        var rung = Rung(); rung["fanOut"] = new JsonArray(new JsonObject { ["series"] = new JsonArray(new JsonObject { ["kind"] = "or",
            ["paths"] = new JsonArray(new JsonArray(cmp), new JsonArray(Compare())) }), ["outputs"] = new JsonArray(Coil()) });
        var result = new ConstructionInput<LadFcBlockSpec>().Read(Block(rung).ToJsonString(), "ladFcBlock");
        Assert.True(result.IsValid, V4Json.Serialize(result.Error));
    }

    [Theory]
    [InlineData(16, true)][InlineData(17, false)]
    public void DepthLimitIsExactlySixteen(int depth, bool accepted)
    {
        var tail = Move();
        tail["in"] = new JsonObject { ["global"] = new JsonArray("DB", new JsonObject { ["name"] = "A", ["index"] = new JsonArray(1) }) };
        if (depth == 17) tail["in"]!["global"]![1]!["index"] = new JsonArray(new JsonArray(1));
        for (int i = 0; i < 3; i++) { var head = Move(); head["then"] = new JsonArray(tail); tail = head; }
        var result = new ConstructionInput<LadFcBlockSpec>().Read(Block(Rung(outputs: new JsonArray(tail))).ToJsonString(), "ladFcBlock");
        Assert.Equal(accepted, result.IsValid);
        if (!accepted) Assert.Equal(ErrorCode.LimitExceeded, result.Error!.Code);
    }

    [Theory]
    [InlineData("T#1s")][InlineData("TIME#1s")][InlineData("LT#1ns")][InlineData("S5T#1s")]
    public void TypedConstantsAndLiteralTextArePreserved(string value)
    {
        var move = Move(); move["in"] = new JsonObject { ["typed"] = value };
        var literal = Move();
        const string text = "</ConstantValue><Injected/>\r\n\t<&中文😀";
        literal["in"]!["literal"]!["value"] = text;
        var block = Block(Rung(outputs: new JsonArray(move, literal)));
        var spec = new ConstructionInput<LadFcBlockSpec>().Read(block.ToJsonString(), "ladFcBlock").Value!;
        string xml = ConstructionAdapter.BuildCandidate(spec, "21", ConstructionProfile.Foundation).Xml;
        Assert.Equal(xml, PlcBuilderToolJson.ComposeLadFcBlock(block.ToJsonString())["xml"]!.GetValue<string>());
        Assert.Equal(xml, OfflineLadderBuilders.Build("BuildPlcLadFcBlock", "21", block.ToJsonString())["Xml"]!.GetValue<string>());
        var doc = XDocument.Parse(xml);
        Assert.Equal(new[] { value, text }, doc.Descendants(Ns + "ConstantValue").Select(e => e.Value));
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "Injected");
    }

    [Theory]
    [InlineData("plc-foundation", "BuildFlgNetCall")][InlineData("full-engine", "BuildFlgNetCall")]
    [InlineData("plc-foundation", "BuildPlcLadFcBlock")][InlineData("full-engine", "BuildPlcLadFcBlock")]
    public void DescriptionsKeepReleaseCorrectRungAndUnsupportedLadRoutes(string profile, string name)
    {
        bool call = name == "BuildFlgNetCall";
        var tool = profile == "plc-foundation" ? new OfflineLadderTool(name, call ? "flgNetJson" : "ladFcBlockJson")
            : Host.DeclaredToolMetadata.CreateToolCore(typeof(Host.XmlBuilderTools)
                .GetMethod(call ? "BuildFlgNetCallXmlV4" : "ComposePlcLadFcBlockXmlV4")!, null, _ => new Host.XmlBuilderTools());
        string description = tool.ProtocolTool.Description!;
        Assert.Contains("timers, counters and math", description);
        Assert.Contains("author S7DCL text and import with ImportPlcBlocksDocuments", description);
        Assert.Contains("exported donor block", description);
        if (call)
        {
            Assert.Contains("rung networks on V21", description);
            Assert.Contains("all general LAD on V20", description);
        }
    }

    [Theory]
    [InlineData("plc-foundation")][InlineData("full-engine")]
    public async Task MaintainedRungExampleFitsBothRegistrations(string profile)
    {
        var example = Parse(File.ReadAllText(Path.Combine(Root, "reference/tool-examples/calls.json")))
            ["profiles"]![profile]!["BuildPlcLadFcBlock"]!["operations"]!["rung"]!["arguments"]!.AsObject();
        McpServerTool tool = profile == "plc-foundation"
            ? new FoundationV4Tool(new OfflineLadderTool("BuildPlcLadFcBlock", "ladFcBlockJson"), "21")
            : Host.DeclaredToolMetadata.CreateToolCore(typeof(Host.XmlBuilderTools).GetMethod("ComposePlcLadFcBlockXmlV4")!, null,
                _ => new Host.XmlBuilderTools());
        var schema = new InputSchema(tool.ProtocolTool.InputSchema);
        var validation = new InputContract<JsonElement>(schema, new InputBudget()).Read(JsonSerializer.SerializeToElement(example), "arguments");
        Assert.True(validation.IsValid, V4Json.Serialize(validation.Error));
        var args = profile == "full-engine" ? new JsonObject { ["ladFcBlock"] = example["ladFcBlock"]!.DeepClone() } : example;
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new() { Name = "BuildPlcLadFcBlock", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
        var result = await tool.InvokeAsync(request);
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Equal("NOT RUN", (string?)result.StructuredContent?["data"]?["nativeAcceptance"]);
    }

    [Theory]
    [InlineData("missing-reference")][InlineData("duplicate-input")][InlineData("unknown-pin")][InlineData("duplicate-source")]
    public void StructuralCheckRejectsBrokenNets(string defect)
    {
        var rung = ConstructionJson.Deserialize<LadRung>(Rung(new JsonArray(Contact())).ToJsonString());
        var flg = FlgNetRungXmlBuilder.Build(rung, new FlgNetFormat(), Array.Empty<string>());
        var wire = flg.Element(Ns + "Wires")!.Elements().First();
        if (defect == "missing-reference") wire.Element(Ns + "NameCon")!.SetAttributeValue("UId", "9999");
        else if (defect == "duplicate-input") wire.Add(new XElement(wire.Element(Ns + "NameCon")!));
        else if (defect == "unknown-pin") wire.Element(Ns + "NameCon")!.SetAttributeValue("Name", "unknown");
        else { var clone = new XElement(wire); clone.SetAttributeValue("UId", "9999"); wire.AddAfterSelf(clone); }
        Assert.Throws<InvalidOperationException>(() => FlgNetRungXmlBuilder.Verify(flg));
    }

    [Theory]
    [InlineData("21", true)][InlineData("20", false)][InlineData("14sp1", false)]
    public void RegistrationAndSchemaStayV21Only(string release, bool available)
    {
        using var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        Assert.Equal(available, roster.Any(t => t.ProtocolTool.Name == "BuildPlcLadFcBlock"));
        if (!available) return;
        var tool = roster.Single(t => t.ProtocolTool.Name == "BuildPlcLadFcBlock");
        var arguments = new JsonObject { ["ladFcBlock"] = Block(Rung()), ["outputReleaseKey"] = "21" };
        var validation = new InputContract<JsonElement>(new InputSchema(tool.ProtocolTool.InputSchema), new InputBudget())
            .Read(JsonSerializer.SerializeToElement(arguments), "arguments");
        Assert.True(validation.IsValid, V4Json.Serialize(validation.Error));
        Assert.Equal("21", tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("outputReleaseKey").GetProperty("enum")[0].GetString());
    }
}
