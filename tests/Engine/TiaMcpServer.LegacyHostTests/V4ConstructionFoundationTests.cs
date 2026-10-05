using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class V4ConstructionFoundationTests
{
    private const string Udt = """{"name":"UDT<&\"","members":[{"name":"Ready","datatype":"Bool","externalWritable":true,"comment":"<tag>&中文"}]}""";
    private const string Db = """{"dbName":"DB<&中文","dbNumber":42,"staticMembers":[{"name":"Ready","datatype":"Bool","externalWritable":false,"commentZhCn":"<tag>&中文","startValue":"TRUE"},{"name":"Counter","datatype":"Int"}]}""";
    private const string Tag = """{"tableName":"Tags<&","tags":[{"name":"Run","dataTypeName":"Bool","logicalAddress":"%Q0.0"}]}""";
    private const string St = """{"firstUid":100,"operations":[{"op":"if","condition":"#Ready"},{"op":"assignment","target":"#Counter","value":"1<&","indent":2},{"op":"else"},{"op":"assignment","target":"#Counter","source":"#Other"},{"op":"endif"}]}""";
    private const string Call = """{"callName":"调用FC","parameters":[{"name":"In","section":"Input","dataType":"Int","sourceKind":"constant","value":"42"},{"name":"Out","section":"Output","dataType":"Bool","symbolPath":["数据","Ready"]}]}""";
    private const string Block = """{"blockName":"Test","blockNumber":1,"inputs":[],"outputs":[],"structuredText":{"operations":[{"op":"assignment","target":"#Ready","value":"TRUE"}]}}""";
    private static string Lad(string call) => """{"blockName":"Caller","blockNumber":42,"networks":[{"callJson":CALL}]}""".Replace("CALL", call);
    private static string Source(string file, int line) => "tests/Engine/TiaMcpServer.LegacyHostTests/" + file + ".cs:" + line;
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Legacy(string kind, string json, string release = "21", bool innerOnly = false)
    {
        var result = kind switch
        {
            "udt" => OfflineXmlBuilders.Build("BuildPlcUdtXml", release, json),
            "tagtable" => OfflineXmlBuilders.Build("BuildPlcTagTableXml", release, json),
            "globaldb" => OfflineCompositionBuilders.Build("BuildPlcGlobalDbXml", release, json),
            "st" => OfflineCompositionBuilders.Build("BuildStructuredTextXml", release, json, innerOnly),
            "fc" => OfflineBlockCompositionBuilders.Build("ComposePlcFcBlockXml", release, json),
            "fb" => OfflineBlockCompositionBuilders.Build("ComposePlcFbBlockXml", release, json),
            "call" => OfflineLadderBuilders.Build("BuildFlgNetCallXml", release, json),
            "lad" => OfflineLadderBuilders.Build("ComposePlcLadFcBlockXml", release, json),
            _ => throw new ArgumentException(kind)
        };
        return result["Xml"]!.GetValue<string>();
    }

    // Only documented alias conversions used by these fixtures. Production DTOs accept none
    // of these legacy spellings; conflicting aliases remain errors instead of being erased.
    private static string ConvertSample(string json)
    {
        var node = JsonNode.Parse(json)!;
        void Rename(JsonObject obj, string from, string to)
        {
            if (!obj.ContainsKey(from)) return;
            if (obj.ContainsKey(to)) throw new ArgumentException("Conflicting sample aliases.");
            var value = obj[from]; obj.Remove(from); obj[to] = value;
        }
        void Visit(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (value is not JsonObject obj) return;
            if (obj.ContainsKey("op") || obj.ContainsKey("kind") || obj.ContainsKey("type"))
            {
                Rename(obj, "kind", "op"); Rename(obj, "type", "op");
                string op = obj["op"]!.GetValue<string>();
                obj["op"] = op switch { "assignment" => "assign", "ifheader" => "if", "elseif" => "elsif", "end_if" => "endif", "new_line" => "newline", _ => op };
                Rename(obj, "conditionVariable", "condition"); Rename(obj, "variable", "condition");
                if (obj["op"]!.GetValue<string>() == "assign") Rename(obj, "value", "literalValue");
                if (obj["op"]!.GetValue<string>() == "literal") Rename(obj, "literalValue", "value");
            }
            if (obj.ContainsKey("section")) Rename(obj, "value", "constantValue");
            Rename(obj, "callJson", "call");
            foreach (var child in obj.ToArray()) Visit(child.Value);
        }
        Visit(node);
        return node.ToJsonString(Compact);
    }

    private static ConstructionSpec Read(string kind, string json) => kind switch
    {
        "st" => V4Json.Deserialize<StructuredTextSpec>(json), "call" => V4Json.Deserialize<FlgNetCallSpec>(json),
        "lad" => V4Json.Deserialize<LadFcBlockSpec>(json), _ => PlcArtifactSpec.Deserialize(kind, json)
    };

    private static string Candidate(string kind, string legacy, string release = "21", bool innerOnly = false) =>
        ConstructionAdapter.BuildCandidate(Read(kind, ConvertSample(legacy)), release, ConstructionProfile.Foundation, innerOnly).Xml;

    public static IEnumerable<object[]> EquivalenceSamples()
    {
        var basics = new[]
        {
            ("udt", Udt, Source("OfflineXmlBuilderTests", 17)), ("tagtable", Tag, Source("OfflineXmlBuilderTests", 18)),
            ("globaldb", Db, Source("OfflineCompositionTests", 14)), ("st", St, Source("OfflineCompositionTests", 15)),
            ("fc", Block, Source("OfflineBlockCompositionTests", 15)), ("fb", Block, Source("OfflineBlockCompositionTests", 15)),
            ("call", Call, Source("OfflineLadderTests", 14)), ("lad", Lad(Call), Source("OfflineLadderTests", 15))
        };
        foreach (var (kind, json, source) in basics)
        {
            yield return new object[] { kind, json, true, source };
            yield return new object[] { kind, "{}", false, source };
            yield return new object[] { kind, json.Insert(1, "\"unknown\":1,"), false, source };
        }
        foreach (var bad in new[]
        {
            Udt.Replace("Bool", ""), Udt.Replace("true", "\"true\""), Udt.Replace("\"comment\":\"<tag>&中文\"", "\"comment\":null"),
            Udt.Replace("\"comment\":", "\"startValue\":\"1\",\"comment\":"), """{"name":"A","members":[]}""",
            """{"name":"A","members":[{"name":"x","datatype":"Bool"},{"name":"X","datatype":"Bool"}]}"""
        }) yield return new object[] { "udt", bad, false, Source("OfflineXmlBuilderTests", 101) };
        foreach (var bad in new[] { Tag.Replace("%Q0.0", "Q0.0"), Tag.Replace("\"logicalAddress\":", "\"comment\":\"ignored\",\"logicalAddress\":"), """{"tableName":"A","tags":[]}""" })
            yield return new object[] { "tagtable", bad, false, Source("OfflineXmlBuilderTests", 117) };
        foreach (var bad in new[] { Db.Replace("\"dbNumber\":42", "\"dbNumber\":0"), Db.Replace("false", "null"), Db.Replace("Counter", "Ready"), Db.Replace("TRUE", "x\\ry"), Db.Replace("<tag>&中文", " "), Db.Replace("\"dbNumber\":42", "\"dbNumber\":2147483648") })
            yield return new object[] { "globaldb", bad, false, Source("OfflineCompositionTests", 81) };
        foreach (var bad in new[]
        {
            """{"operations":[]}""", """{"firstUid":0,"operations":[{"op":"newline"}]}""",
            """{"firstUid":2147483647,"operations":[{"op":"newline"}]}""", """{"operations":[{"op":"blank","count":0}]}""",
            """{"operations":[{"op":"blank","count":4097}]}""", """{"operations":[{"op":"newline","indent":1}]}""",
            """{"operations":[{"op":"assignment","target":"x","value":"1","source":"y"}]}""",
            """{"operations":[{"op":"symbol","name":" a"}]}""", """{"operations":[{"op":"symbol","name":"\"DB\".a"}]}""",
            """{"operations":[{"op":"token","text":"x\t"}]}""", """{"operations":[{"op":"literal","value":"x\r"}]}""",
            """{"operations":[{"op":"line","items":[]}]}""", """{"operations":[{"op":"line","items":[{"sym":"x","raw":";"}]}]}"""
        }) yield return new object[] { "st", bad, false, Source("OfflineCompositionTests", 135) };
        const string allOps = """{"operations":[{"kind":"ifheader","conditionVariable":"a"},{"type":"elseif","variable":"b"},{"op":"else","indent":1},{"op":"end_if"},{"op":"token","text":"<Injected/>"},{"op":"blank","count":2},{"op":"new_line"},{"op":"literal","literalValue":"TRUE"},{"op":"line","items":[{"sym":"a"},{"token":":="},{"lit":"1"},{"raw":";"}]}]}""";
        yield return new object[] { "st", allOps, true, Source("OfflineCompositionTests", 151) };
        foreach (var (op, name) in new[] { ("symbol", "#a.b"), ("symbol", "\"DB.a\""), ("global", "DB.a"), ("local", "a") })
            yield return new object[] { "st", JsonSerializer.Serialize(new { operations = new[] { new { op, name } } }), true, Source("OfflineCompositionTests", 147) };
        foreach (var kind in new[] { "fc", "fb" })
        {
            foreach (var bad in new[] { Block.Replace("\"blockNumber\":1", "\"blockNumber\":0"), Block.Replace("\"inputs\":[]", "\"inputs\":null"), Block.Replace("TRUE", "x\\r"), Block.Replace("#Ready", "#Ready\\t"), Block.Replace("\"structuredText\":", "\"structuredTextInnerXml\":\"<!DOCTYPE x>\",\"structuredText\":") })
                yield return new object[] { kind, bad, false, Source("OfflineBlockCompositionTests", 67) };
            var absent = Block.Replace("\"inputs\":[],\"outputs\":[],", "");
            yield return new object[] { kind, absent, kind == "fb", Source("OfflineBlockCompositionTests", 84) };
            var rich = JsonNode.Parse(Block)!;
            const string text = "<&\"'中文\r\n\ttext";
            rich["blockName"] = text; rich["commentZhCn"] = text; rich["titleZhCn"] = text; rich["networkCommentZhCn"] = text; rich["networkTitleZhCn"] = text;
            rich["inputs"] = new JsonArray(new JsonObject { ["name"] = text, ["datatype"] = text, ["commentZhCn"] = text });
            yield return new object[] { kind, rich.ToJsonString(Compact), true, Source("OfflineBlockCompositionTests", 54) };
        }
        foreach (var kind in new[] { "call", "lad" })
        {
            foreach (var bad in new[] { Call.Replace("调用FC", "Bad/FC"), Call.Replace("\"Input\"", "\"input\""), Call.Replace("\"Input\"", "\"InOut\""), Call.Replace("\"Input\"", "\"Output\""), Call.Replace("\"Int\"", "\"int\""), Call.Replace("\"In\"", "\"en\""), Call.Replace("\"Out\"", "\"iN\""), Call.Replace("\"value\":\"42\"", "\"value\":42"), Call.Replace("\"value\":\"42\"", "\"value\":\"42\",\"symbolPath\":[\"x\"]"), Call.Replace("[\"数据\",\"Ready\"]", "[]"), Call.Replace("[\"数据\",\"Ready\"]", "[\"bad/path\"]") })
                yield return new object[] { kind, kind == "lad" ? Lad(bad) : bad, false, Source("OfflineLadderTests", 56) };
            const string empty = """{"callName":"Empty","parameters":[]}""";
            yield return new object[] { kind, kind == "lad" ? Lad(empty) : empty, true, Source("OfflineLadderTests", 48) };
            var rich = JsonNode.Parse(Call)!;
            rich["parameters"]![0]!["value"] = "</ConstantValue><Injected/>\r\n\t<&中文😀";
            yield return new object[] { kind, kind == "lad" ? Lad(rich.ToJsonString(Compact)) : rich.ToJsonString(Compact), true, Source("OfflineLadderTests", 51) };
        }
        yield return new object[] { "lad", """{"blockName":"L","blockNumber":1,"networks":[]}""", false, Source("OfflineLadderTests", 106) };
        yield return new object[] { "fc", Block.Replace("\"inputs\":[]", "\"inputs\":[{\"name\":\"ret_val\",\"datatype\":\"Bool\"}]"), false, Source("OfflineBlockCompositionTests", 86) };
    }

    [Theory]
    [MemberData(nameof(EquivalenceSamples))]
    public void FoundationDecisionAndXmlBytes(string kind, string json, bool accepted, string source)
    {
        Assert.Contains(".cs:", source);
        string? oldXml = null, newXml = null;
        var oldError = Record.Exception(() => oldXml = Legacy(kind, json));
        var newError = Record.Exception(() => newXml = Candidate(kind, json));
        Assert.Equal(accepted, oldError == null);
        Assert.Equal(oldError == null, newError == null);
        if (accepted) Assert.Equal(Encoding.UTF8.GetBytes(oldXml!), Encoding.UTF8.GetBytes(newXml!));
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void DeclarationVersionsAndCandidateOnly(string release)
    {
        foreach (var (kind, json) in new[] { ("udt", Udt), ("globaldb", Db) })
            Assert.Equal(Encoding.UTF8.GetBytes(Legacy(kind, json, release)), Encoding.UTF8.GetBytes(Candidate(kind, json, release)));
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("14sp1")][InlineData("15.1")][InlineData("16")]
    [InlineData("17")][InlineData("18")][InlineData("19")][InlineData("20")][InlineData("V21")][InlineData("21 ")]
    public void ExplicitV21OnlyOutput(string? release)
    {
        foreach (var (kind, json) in new[] { ("tagtable", Tag), ("st", St), ("call", Call), ("fc", Block), ("fb", Block), ("lad", Lad(Call)) })
        {
            Assert.NotNull(Record.Exception(() => Legacy(kind, json, release!)));
            Assert.Throws<ArgumentException>(() => Candidate(kind, json, release!));
        }
    }

    [Fact]
    public void StructuredTextInnerBytes() => Assert.Equal(Encoding.UTF8.GetBytes(Legacy("st", St, innerOnly: true)), Encoding.UTF8.GetBytes(Candidate("st", St, innerOnly: true)));

    [Theory]
    [InlineData("\\ud800")][InlineData("\\udc00")][InlineData("\\ud800x")][InlineData("\\u0000")]
    public void InvalidUnicodeRejected(string escape)
    {
        Assert.NotNull(Record.Exception(() => Candidate("call", Call.Replace("调用FC", escape))));
        Assert.NotNull(Record.Exception(() => Candidate("st", St.Replace("1<&", escape))));
    }

    [Theory]
    [InlineData(4096, true)][InlineData(4097, false)]
    public void StringBudget(int length, bool accepted)
    {
        var json = JsonNode.Parse(Udt)!; json["members"]![0]!["comment"] = new string('a', length);
        FoundationDecisionAndXmlBytes("udt", json.ToJsonString(Compact), accepted, Source("OfflineXmlBuilderTests", 126));
    }

    [Theory]
    [InlineData(1000, true)][InlineData(1001, false)]
    public void ItemBudgets(int count, bool accepted)
    {
        var json = new JsonObject { ["name"] = "U", ["members"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["name"] = "m" + i, ["datatype"] = "Bool" }).ToArray()) };
        FoundationDecisionAndXmlBytes("udt", json.ToJsonString(Compact), accepted, Source("OfflineXmlBuilderTests", 125));
        var st = new JsonObject { ["operations"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)new JsonObject { ["op"] = "newline" }).ToArray()) };
        FoundationDecisionAndXmlBytes("st", st.ToJsonString(Compact), accepted, Source("OfflineCompositionTests", 193));
        var call = new JsonObject { ["callName"] = "Limit", ["parameters"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["name"] = "p" + i, ["section"] = "Input", ["dataType"] = "Bool", ["symbolPath"] = new JsonArray("x") }).ToArray()) };
        FoundationDecisionAndXmlBytes("call", call.ToJsonString(Compact), accepted, Source("OfflineLadderTests", 82));
    }

    [Theory]
    [InlineData(64, true)][InlineData(65, false)]
    public void NetworkBudget(int count, bool accepted)
    {
        var lad = JsonNode.Parse(Lad(Call))!;
        lad["networks"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)new JsonObject { ["callJson"] = JsonNode.Parse(Call) }).ToArray());
        FoundationDecisionAndXmlBytes("lad", lad.ToJsonString(Compact), accepted, Source("OfflineLadderTests", 108));
    }

    [Theory]
    [InlineData(32, true)][InlineData(33, false)]
    public void SymbolDepthBudget(int count, bool accepted)
    {
        var call = JsonNode.Parse(Call)!;
        call["parameters"]![1]!["symbolPath"] = new JsonArray(Enumerable.Repeat("x", count).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        FoundationDecisionAndXmlBytes("call", call.ToJsonString(Compact), accepted, Source("OfflineLadderTests", 81));
    }

    [Fact]
    public void CombinedBudgetsCannotBeSplitAcrossSectionsOrNetworks()
    {
        var block = JsonNode.Parse(Block)!;
        JsonArray Members(int start, int count) => new(Enumerable.Range(start, count).Select(i => (JsonNode)new JsonObject { ["name"] = "m" + i, ["datatype"] = "Bool" }).ToArray());
        block["inputs"] = Members(0, 600); block["outputs"] = Members(600, 401);
        foreach (var kind in new[] { "fc", "fb" }) FoundationDecisionAndXmlBytes(kind, block.ToJsonString(Compact), false, Source("OfflineBlockCompositionTests", 101));
        var parameters = new JsonArray(Enumerable.Range(0, 501).Select(i => (JsonNode)new JsonObject { ["name"] = "p" + i, ["section"] = "Input", ["dataType"] = "Bool", ["symbolPath"] = new JsonArray("x") }).ToArray());
        var call = new JsonObject { ["callName"] = "Run", ["parameters"] = parameters };
        var lad = JsonNode.Parse(Lad(Call))!;
        lad["networks"] = new JsonArray(new JsonObject { ["callJson"] = call }, new JsonObject { ["callJson"] = call.DeepClone() });
        FoundationDecisionAndXmlBytes("lad", lad.ToJsonString(Compact), false, Source("OfflineLadderTests", 109));
    }

    [Fact]
    public void NormalizedJsonBudgetIsIndependentOfIndentationAndEscapes()
    {
        Assert.Equal(Candidate("udt", Udt), ConstructionAdapter.BuildCandidate(V4Json.Deserialize<UdtSpec>(new string(' ', 262144) + Udt), "21", ConstructionProfile.Foundation).Xml);
        // Exact ASCII normalized boundary, while every individual string remains <=4096.
        var operations = new JsonArray(Enumerable.Range(0, 64).Select(_ => (JsonNode)new JsonObject { ["op"] = "token", ["text"] = new string('a', 4096) }).ToArray());
        var root = new JsonObject { ["operations"] = operations };
        int excess = root.ToJsonString(Compact).Length - 262144;
        operations[63]!["text"] = new string('a', 4096 - excess);
        string exact = root.ToJsonString(Compact);
        Assert.Equal(262144, exact.Length);
        Assert.NotNull(V4Json.Deserialize<StructuredTextSpec>(exact.Replace("a", "\\u0061")));
        operations[63]!["text"] = new string('a', 4097 - excess);
        Assert.Throws<ArgumentException>(() => V4Json.Deserialize<StructuredTextSpec>(root.ToJsonString(Compact)));
    }

    [Theory]
    [InlineData(16, true)][InlineData(17, false)]
    public void DepthBudget(int depth, bool accepted)
    {
        string json = new string('[', depth) + "1" + new string(']', depth);
        using var document = JsonDocument.Parse(json);
        var error = Record.Exception(() => ConstructionJson.Budget(document.RootElement, Compact));
        Assert.Equal(accepted, error == null);
        Assert.NotNull(Record.Exception(() => V4Json.Deserialize<StructuredTextSpec>("{\"operations\":" + json + "}")));
    }

    [Theory]
    [InlineData(1048576, true)][InlineData(1048577, false)]
    public void XmlBudgetAndNoExternalResolution(int length, bool accepted)
    {
        string xml = "<x>" + new string('a', length - 7) + "</x>";
        Assert.Equal(accepted, Record.Exception(() => ConstructionAdapter.VerifyOutput(xml, false)) == null);
        Assert.Throws<XmlException>(() => ConstructionAdapter.VerifyOutput("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///must-not-resolve'>]><x>&e;</x>", false));
    }

    [Theory]
    [InlineData("udt")][InlineData("globaldb")][InlineData("call")][InlineData("lad")][InlineData("st")][InlineData("fc")][InlineData("fb")]
    public void OutputExpansionBudget(string kind)
    {
        string json;
        if (kind == "udt" || kind == "globaldb")
        {
            var members = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode)new JsonObject { ["name"] = "m" + i, ["datatype"] = "Bool", ["comment"] = new string('&', 4000) }).ToArray());
            json = kind == "udt" ? new JsonObject { ["name"] = "U", ["members"] = members }.ToJsonString(Compact)
                : new JsonObject { ["dbName"] = "D", ["dbNumber"] = 1, ["staticMembers"] = members }.ToJsonString(Compact);
        }
        else if (kind == "call" || kind == "lad")
        {
            var call = new JsonObject { ["callName"] = "Expansion", ["parameters"] = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode)new JsonObject { ["name"] = "p" + i, ["section"] = "Input", ["dataType"] = "Int", ["sourceKind"] = "constant", ["value"] = new string('&', 4000) }).ToArray()) };
            json = kind == "call" ? call.ToJsonString(Compact) : Lad(call.ToJsonString(Compact));
        }
        else
        {
            var st = new JsonObject { ["operations"] = new JsonArray(Enumerable.Range(0, 230).Select(_ => (JsonNode)new JsonObject { ["op"] = "symbol", ["name"] = string.Join('.', Enumerable.Repeat("a", 200)) }).ToArray()) };
            var block = JsonNode.Parse(Block)!; block["structuredText"] = st;
            json = kind == "st" ? st.ToJsonString(Compact) : block.ToJsonString(Compact);
        }
        Assert.True(json.Length < 262144);
        Assert.NotNull(Read(kind, ConvertSample(json)));
        FoundationDecisionAndXmlBytes(kind, json, false, Source("OfflineXmlBuilderTests", 127) + "; " + Source("OfflineLadderTests", 85) + "; " + Source("OfflineCompositionTests", 194));
    }
}
