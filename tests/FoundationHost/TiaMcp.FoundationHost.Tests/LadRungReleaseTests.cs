extern alias toolpipeline;
using System.Text;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaGitAddIn.Services.SimaticMl;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Xunit.Abstractions;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;

public sealed class LadRungReleaseTests(ITestOutputHelper output)
{
    private static string Root => TestPaths.RepositoryRoot(AppContext.BaseDirectory);
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };

    public static IEnumerable<object[]> ElementsByRelease()
    {
        foreach (string release in Releases)
        {
            foreach (var element in LadRungBuilderTests.Elements())
            {
                var rung = JsonNode.Parse((string)element[1])!;
                if (release == "14sp1" && (string)element[0] == "indexed-local")
                    rung["outputs"]![0]!["in"]!["global"]![1] = "Values";
                yield return new object[] { release, (string)element[0], rung.ToJsonString() };
            }
            yield return new object[] { release, "legacy-call", "" };
        }
    }

    private static JsonObject Block(string rungJson) => new()
    {
        ["blockName"] = "FC_Rung", ["blockNumber"] = 42,
        ["inputs"] = new JsonArray(new JsonObject { ["name"] = "Local", ["datatype"] = "Int" }),
        ["outputs"] = new JsonArray(),
        ["networks"] = new JsonArray(rungJson.Length == 0
            ? new JsonObject { ["call"] = JsonNode.Parse("""{"callName":"FC_User","parameters":[{"name":"Value","section":"Input","dataType":"Int","sourceKind":"constant","constantValue":"1"},{"name":"Result","section":"Output","dataType":"Int","symbolPath":["DB","Value"]}]}""") }
            : new JsonObject { ["rung"] = JsonNode.Parse(rungJson) })
    };

    [Theory]
    [MemberData(nameof(ElementsByRelease))]
    public void GoldenEntryPointsReadersAndOfficialSchemas(string release, string element, string rungJson)
    {
        var block = Block(rungJson);
        var historical = LadRungBuilderTests.HistoricalFanOut(element, block, release);
        var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation, null, release)
            .Read(block.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        var candidate = ConstructionAdapter.BuildLadCandidateForRelease(input.Value!, release, ConstructionProfile.Foundation);
        string xml = candidate.Xml;
        Assert.Equal(xml, ConstructionAdapter.BuildLadCandidateForRelease(input.Value!, release, ConstructionProfile.Foundation).Xml);
        var offline = OfflineLadderBuilders.BuildForRelease("BuildPlcLadFcBlock", release, block.ToJsonString());
        Assert.Equal(xml, offline["Xml"]!.GetValue<string>());
        Assert.Equal(release, offline["Data"]!["outputReleaseKey"]!.GetValue<string>());
        Assert.Equal("NOT RUN", candidate.NativeAcceptance);
        Assert.False(candidate.SchemaValidated);
        Assert.False(candidate.ImportValidated);
        Assert.False(candidate.ProgramSemanticsValidated);
        var engine = PlcBuilderToolJson.ComposeLadFcBlockForRelease(block.ToJsonString(), release);
        Assert.True(XNode.DeepEquals(XDocument.Parse(xml), XDocument.Parse(engine["xml"]!.GetValue<string>())));
        xml = historical ?? xml;
        var document = XDocument.Parse(xml);
        var format = FlgNetFormat.ForRelease(release);
        var flg = document.Descendants(format.Namespace + "FlgNet").Single();
        Assert.All(flg.DescendantsAndSelf(), e => Assert.Equal(format.Namespace, e.Name.Namespace));
        Assert.Equal(format.Declaration.EngineeringVersion, (string?)document.Root!.Element("Engineering")!.Attribute("version"));
        var attrs = document.Descendants("SW.Blocks.FC").Single().Element("AttributeList")!;
        Assert.Equal(format.Declaration.HasObjectNamespace, attrs.Element("Namespace") != null);
        Assert.Equal(release == "21", attrs.Element("SetENOAutomatically") != null);
        var sections = attrs.Element("Interface")!.Elements().Single();
        Assert.All(sections.DescendantsAndSelf(), e => Assert.Equal(format.Declaration.InterfaceNamespace, e.Name.Namespace));
        Assert.Equal(release != "14sp1", format.ArrayIndex);
        string golden = Path.Combine(Root, "tests/FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/LadRung", release, element + ".xml");
        if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, xml, new UTF8Encoding(false));
        }
        Assert.Equal(File.ReadAllBytes(golden), Encoding.UTF8.GetBytes(xml));
        string text = LadTextRenderer.Render(xml);
        Assert.DoesNotContain("不支持", text);
        foreach (var access in flg.Element(format.Namespace + "Parts")!.Elements(format.Namespace + "Access"))
            Assert.Contains(LadTextRenderer.Operand(access).text, text);
        var parsed = SimaticMlParser.ParseText(xml, SimaticMlParserLimits.Default, TiaGitAddIn.Models.Comparison.PlcRevisionSide.Left, default);
        Assert.True(parsed.IsSuccess);
        var unknown = new HashSet<string>();
        Assert.Contains("<svg", PlcLadderDrawing.NetworkSvg(parsed.Model!.Blocks.Single().CompileUnits.Single().Network!, _ => null, unknown));
        Assert.Empty(unknown);
        ValidateSchemas(release, format, flg, sections);
    }

    private void ValidateSchemas(string release, FlgNetFormat format, XElement flg, XElement sections)
    {
        string? sdkRoot = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        if (string.IsNullOrEmpty(sdkRoot))
        {
            output.WriteLine("[XSD NOT RUN] Set TIA_MCP_TEST_PUBLIC_API_ROOT; native acceptance remains NOT RUN.");
            return;
        }
        string folder = release == "14sp1" ? "TIA_V14SP1_PublicAPI/V14 SP1" : "TIA_V" + release + "_PublicAPI/V" + release;
        string directory = Path.Combine(sdkRoot, folder.Replace('/', Path.DirectorySeparatorChar), "Schemas");
        foreach (var fragment in new[] { (Xml: flg, Schema: format.SchemaFile), (Xml: sections, Schema: format.Declaration.InterfaceSchemaFile) })
        {
            var issues = new List<string>();
            var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            schemas.ValidationEventHandler += (_, args) => issues.Add(args.Message);
            schemas.Add(fragment.Xml.Name.NamespaceName, Path.Combine(directory, fragment.Schema));
            schemas.Compile();
            new XDocument(new XElement(fragment.Xml)).Validate(schemas, (_, args) => issues.Add(args.Message));
            Assert.True(issues.Count == 0, release + " " + fragment.Schema + ": " + string.Join("; ", issues));
            output.WriteLine("[XSD] " + release + " " + fragment.Schema);
        }
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void CallOnlyRungsUseTheRungStructuralCheck(string release)
    {
        foreach (string type in new[] { "FC", "FB" })
        {
            var call = new JsonObject { ["kind"] = "call", ["block"] = type + "_User", ["blockType"] = type, ["pins"] = new JsonArray() };
            if (type == "FB") call["instance"] = "DB_User";
            var rung = new JsonObject { ["series"] = new JsonArray(), ["outputs"] = new JsonArray(call) };
            var block = Block(rung.ToJsonString());
            var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation, null, release)
                .Read(block.ToJsonString(), "ladFcBlock");
            Assert.True(input.IsValid);
            string expected = ConstructionAdapter.BuildLadCandidateForRelease(input.Value!, release, ConstructionProfile.Foundation).Xml;
            Assert.Equal(expected, OfflineLadderBuilders.BuildForRelease("BuildPlcLadFcBlock", release, block.ToJsonString())["Xml"]!.GetValue<string>());
            var flg = XDocument.Parse(expected).Descendants(FlgNetFormat.ForRelease(release).Namespace + "FlgNet").Single();
            FlgNetRungXmlBuilder.Verify(flg);
            Assert.Equal(type, (string?)flg.Descendants(FlgNetFormat.ForRelease(release).Namespace + "CallInfo").Single().Attribute("BlockType"));
        }
    }

    [Theory]
    [InlineData("global")][InlineData("local")][InlineData("instance")]
    public void V14IndexRejectionNamesTheExactPointer(string scope)
    {
        const string indexed = """{"name":"SECRET_CANARY","index":[1]}""";
        string operand = "{\"" + (scope == "local" ? "local" : "global") + "\":[\"Local\"," + indexed + "]}";
        string rung = scope == "instance"
            ? "{\"series\":[],\"outputs\":[{\"kind\":\"timer\",\"type\":\"TON\",\"instance\":" + operand + ",\"pt\":{\"typed\":\"T#1s\"}}]}"
            : "{\"series\":[],\"outputs\":[{\"kind\":\"move\",\"in\":" + operand + ",\"out\":[{\"global\":[\"Q\"]}]}]}";
        var input = new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation, null, "14sp1")
            .Read(Block(rung).ToJsonString(), "ladFcBlock");
        Assert.False(input.IsValid);
        Assert.Equal(ErrorCode.InvalidArgument, input.Error!.Code);
        string path = "/ladFcBlock/networks/0/rung/outputs/0/" + (scope == "instance" ? "instance/global" : "in/" + scope) + "/1/index";
        Assert.Contains(path, V4Json.Serialize(input.Error));
        Assert.DoesNotContain("SECRET_CANARY", V4Json.Serialize(input.Error));
        Assert.True(new ConstructionInput<LadFcBlockSpec>(ConstructionProfile.Foundation, null, "15.1")
            .Read(Block(rung).ToJsonString(), "ladFcBlock").IsValid);
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void PublicRegistrationAndReleaseEnumsRemainV21Only(string release)
    {
        using var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        Assert.Equal(release == "21", roster.Any(t => t.ProtocolTool.Name == "BuildPlcLadFcBlock"));
        var foundation = new FoundationV4Tool(new OfflineLadderTool("BuildPlcLadFcBlock", "ladFcBlockJson"), "21");
        var engine = Host.DeclaredToolMetadata.CreateToolCore(typeof(Host.XmlBuilderTools).GetMethod("ComposePlcLadFcBlockXmlV4")!, null,
            _ => new Host.XmlBuilderTools());
        var enumValues = foundation.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("outputReleaseKey").GetProperty("enum");
        Assert.Equal(new[] { "21" }, enumValues.EnumerateArray().Select(v => v.GetString()));
        // The engine registration retains its implicit V21 target and exposes no release selector.
        Assert.Equal(new[] { "ladFcBlock" }, engine.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var arguments = Block("");
        Assert.Equal("V21", (string?)XDocument.Parse(PlcBuilderToolJson.ComposeLadFcBlock(arguments.ToJsonString())["xml"]!.GetValue<string>())
            .Root!.Element("Engineering")!.Attribute("version"));
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public async Task UsageRetrievalKeepsTheSequenceAndBuilderOnV21(string release)
    {
        using var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        var guide = roster.Single(t => t.ProtocolTool.Name == "GetToolUsage");
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        async Task<JsonNode> Call(string args)
        {
            var result = await guide.InvokeAsync(new RequestContext<CallToolRequestParams>(server)
            {
                Params = new() { Name = "GetToolUsage", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args) }
            });
            return JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
        }
        var sequence = await Call("""{"exampleId":"sequence/plc-lad-rung-build-import"}""");
        var example = sequence["data"]!["examples"]!.AsArray().Single()!;
        Assert.Equal(release == "21", (bool?)example["available"]);
        Assert.Equal(new[] { "21" }, example["releaseKeys"]!.AsArray().Select(v => (string?)v));
        var usage = await Call("""{"toolName":"BuildPlcLadFcBlock"}""");
        if (release == "21")
        {
            Assert.True((bool?)usage["ok"] == true, usage.ToJsonString());
            Assert.Contains("timer pt uses Time", usage["data"]!.ToJsonString());
            Assert.Contains("Output/InOut pins require symbols", usage["data"]!.ToJsonString());
        }
        else Assert.Equal("INVALID_ARGUMENT", (string?)usage["error"]!["code"]);
    }
}
