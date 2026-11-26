using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Engine.Tests.Shared;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class LadRungBoxBuilderTests
{
    private static readonly XNamespace Ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
    private static JsonObject Symbol(string name) => new() { ["global"] = new JsonArray(name) };
    private static JsonObject Coil(string name = "Ready") => new() { ["kind"] = "coil", ["operand"] = Symbol(name) };
    private static JsonObject Contact(string name = "Reset") => new() { ["kind"] = "no", ["operand"] = Symbol(name) };
    private static JsonObject Literal(string type = "Int") => new()
        { ["literal"] = new JsonObject { ["type"] = type, ["value"] = "10" } };
    private static JsonObject Rung(JsonObject output) => new()
        { ["series"] = new JsonArray(Contact("Enable")), ["outputs"] = new JsonArray(output) };
    private static JsonObject Timer(string type = "TON") => new()
    {
        ["kind"] = "timer", ["type"] = type,
        ["instance"] = new JsonObject { ["global"] = new JsonArray("DB_State", "Timer") },
        ["pt"] = new JsonObject { ["typed"] = "T#500ms" }
    };
    private static JsonObject Counter(string type = "CTU", string valueType = "Int")
    {
        var result = new JsonObject { ["kind"] = "counter", ["type"] = type, ["instance"] = Symbol("DB_Counter"),
            ["valueType"] = valueType, ["pv"] = Literal(valueType) };
        if (type != "CTD") result["reset"] = new JsonArray(Contact());
        if (type != "CTU") result["load"] = new JsonArray(Contact("Load"));
        if (type == "CTUD") result["down"] = new JsonArray(Contact("Down"));
        return result;
    }
    private static JsonObject Pin(string name = "Command", string section = "Input") => new()
        { ["name"] = name, ["section"] = section, ["type"] = "Int", ["value"] = Symbol(name + "Value") };
    private static JsonObject Call(string type = "FC")
    {
        var result = new JsonObject { ["kind"] = "call", ["block"] = type + "_User", ["blockType"] = type,
            ["pins"] = new JsonArray(Pin(), Pin("Result", "Output"), Pin("State", "InOut")) };
        if (type == "FB") result["instance"] = "DB_User";
        return result;
    }
    private static JsonObject Block(JsonObject output) => new()
    {
        ["blockName"] = "FC_Boxes", ["blockNumber"] = 43,
        ["networks"] = new JsonArray(new JsonObject { ["rung"] = Rung(output) })
    };

    [Fact]
    public async Task ExistingOfflineLadderChecksKeepEveryAssertion()
    {
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        await OfflineLadderTests.Run(server, (passed, message) => Assert.True(passed, message));
    }

    [Fact]
    public void ExistingDeclarationChecksKeepEveryAssertion()
        => DeclarationXmlFormatTests.Run((passed, message) => Assert.True(passed, message));
    private static XElement Build(JsonObject output)
    {
        var input = new ConstructionInput<LadFcBlockSpec>().Read(Block(output).ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        return XDocument.Parse(ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml)
            .Descendants(Ns + "FlgNet").Single();
    }

    [Fact]
    public void Direct_builder_refuses_an_indexed_symbol_in_the_v14_sp1_wire_format()
    {
        var coil = Coil();
        coil["operand"] = new JsonObject { ["global"] = new JsonArray("DB", new JsonObject { ["name"] = "Flags", ["index"] = new JsonArray(1) }) };
        var input = new ConstructionInput<LadFcBlockSpec>().Read(Block(coil).ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        var rung = input.Value!.Networks.Single().Rung!;
        Assert.Throws<ArgumentException>(() => FlgNetRungXmlBuilder.Build(rung, FlgNetFormat.ForRelease("14sp1"), Array.Empty<string>()));
        Assert.Contains("AccessModifier=\"Array\"", FlgNetRungXmlBuilder.Build(rung, FlgNetFormat.ForRelease("17"), Array.Empty<string>()).ToString());
    }

    [Fact]
    public void User_motion_block_can_be_called_by_its_declared_name()
    {
        var call = Call("FB");
        call["block"] = "MC_Conveyor";
        Assert.Equal("MC_Conveyor", (string?)Build(call).Descendants(Ns + "CallInfo").Single().Attribute("Name"));
    }

    public static IEnumerable<object[]> Elements()
    {
        foreach (string type in new[] { "TON", "TOF", "TP" })
        {
            yield return new object[] { type.ToLowerInvariant() + "-open", Rung(Timer(type)).ToJsonString() };
            var timer = Timer(type); timer["q"] = new JsonArray(Coil()); timer["et"] = Symbol("Elapsed");
            yield return new object[] { type.ToLowerInvariant(), Rung(timer).ToJsonString() };
        }
        foreach (string type in new[] { "CTU", "CTD", "CTUD" })
        {
            yield return new object[] { type.ToLowerInvariant() + "-open", Rung(Counter(type)).ToJsonString() };
            var counter = Counter(type); counter["q"] = new JsonArray(Coil()); counter["cv"] = Symbol("Count");
            if (type == "CTUD") counter["qd"] = new JsonArray(Coil("Empty"));
            yield return new object[] { type.ToLowerInvariant(), Rung(counter).ToJsonString() };
        }
        foreach (string type in new[] { "FC", "FB" })
        {
            yield return new object[] { "call-" + type.ToLowerInvariant(), Rung(Call(type)).ToJsonString() };
            var call = Call(type); call["then"] = new JsonArray(Coil());
            yield return new object[] { "call-" + type.ToLowerInvariant() + "-eno", Rung(call).ToJsonString() };
            call = Call(type); call["pins"] = new JsonArray();
            yield return new object[] { "call-" + type.ToLowerInvariant() + "-empty", Rung(call).ToJsonString() };
        }
    }

    public static IEnumerable<object[]> InvalidRungs()
    {
        object[] Invalid(JsonObject output, string pointer) => new object[] { Rung(output).ToJsonString(), "/outputs/0" + pointer };
        foreach (var output in new[] { Timer(), Counter() })
        {
            output["instance"] = new JsonObject { ["local"] = new JsonArray("SECRET_CANARY") };
            yield return Invalid(output, "/instance");
        }
        foreach (string value in new[] { "LT#1s", "LT#500ms" })
        {
            var timer = Timer(); timer["pt"] = new JsonObject { ["typed"] = value };
            yield return Invalid(timer, "/pt");
        }
        var longTimer = Timer(); longTimer["pt"] = new JsonObject { ["typed"] = "LTIME#1s" };
        yield return Invalid(longTimer, "/pt/typed");
        foreach (var value in new[] { Literal(), new JsonObject { ["constant"] = "SECRET_CANARY" },
            new JsonObject { ["typed"] = "T#1s" } })
        {
            var call = Call(); call["pins"]![2]!["value"] = value;
            yield return Invalid(call, "/pins/2/value");
        }
        var item = Timer(); item["et"] = Literal(); yield return Invalid(item, "/et");
        item = Timer(); item["q"] = new JsonArray(); yield return Invalid(item, "/q");
        item = Timer(); item["then"] = new JsonArray(Coil()); yield return Invalid(item, "");
        item = Timer(); item["type"] = "SECRET_CANARY"; yield return Invalid(item, "/type");
        item = Counter(); item["cv"] = Literal(); yield return Invalid(item, "/cv");
        item = Counter(); item["pv"] = Literal("DInt"); yield return Invalid(item, "/pv/literal/type");
        item = Counter(); item["valueType"] = "Real"; yield return Invalid(item, "/valueType");
        item = Counter(); item["then"] = new JsonArray(Coil()); yield return Invalid(item, "");
        foreach (var pair in new[] { ("CTU", "reset"), ("CTD", "load"), ("CTUD", "reset"), ("CTUD", "load"), ("CTUD", "down") })
        {
            item = Counter(pair.Item1); item.Remove(pair.Item2); yield return Invalid(item, "/" + pair.Item2);
        }
        foreach (var pair in new[] { ("CTD", "reset"), ("CTU", "load"), ("CTU", "down"), ("CTD", "down") })
        {
            item = Counter(pair.Item1); item[pair.Item2] = new JsonArray(Contact()); yield return Invalid(item, "/" + pair.Item2);
        }
        item = Counter(); item["qd"] = new JsonArray(Coil()); yield return Invalid(item, "/qd");
        foreach (string field in new[] { "reset", "load", "down" })
        {
            item = Counter("CTUD"); item[field] = new JsonArray(); yield return Invalid(item, "/" + field);
        }
        item = Call("FB"); item.Remove("instance"); yield return Invalid(item, "/instance");
        item = Call(); item["instance"] = "SECRET_CANARY"; yield return Invalid(item, "/instance");
        item = Call("FB"); item["instance"] = "DB.SECRET_CANARY"; yield return Invalid(item, "/instance");
        item = Call("FB"); item["instance"] = Symbol("SECRET_CANARY"); yield return Invalid(item, "/instance");
        foreach (string name in new[] { "en", "ENO", "bad/name" })
        {
            item = Call(); item["pins"]![0]!["name"] = name; yield return Invalid(item, "/pins/0/name");
        }
        item = Call(); item["pins"]![1]!["name"] = "command"; yield return Invalid(item, "/pins/1/name");
        item = Call(); item["pins"]![1]!["value"] = Literal(); yield return Invalid(item, "/pins/1/value");
        item = Call(); item["pins"]![0]!["section"] = "SECRET_CANARY"; yield return Invalid(item, "/pins/0/section");
        item = Call(); item["pins"]![0]!["type"] = "SECRET_CANARY"; yield return Invalid(item, "/pins/0/type");
        item = Call(); item["pins"]![0]!["value"] = new JsonObject { ["local"] = new JsonArray("SECRET_CANARY") };
        yield return Invalid(item, "/pins/0/value");
        foreach (string name in new[] { "MC_Power", "TON", "ctud", "Library.Block", "SECRET_CANARY/path" })
        {
            item = Call(); item["block"] = name; yield return Invalid(item, "/block");
        }
        foreach (string name in new[] { "TSEND_C", "SEL", "LIMIT", "NORM_X", "SCALE_X", "MOVE_BLK", "GET", "PUT", "TRCV_C", "RD_SYS_T" })
        {
            item = Call(); item["block"] = name; yield return Invalid(item, "/block");
        }
        foreach (string name in new[] { "TCON", "TDISCON", "TSEND", "TRCV", "TUSEND", "TURCV", "RDREC", "WRREC" })
        {
            item = Call(); item["block"] = name; yield return Invalid(item, "/block");
        }
        item = Call(); item["blockType"] = "SFB"; yield return Invalid(item, "/blockType");
        item = Call(); item["library"] = "SECRET_CANARY"; yield return Invalid(item, "");
        item = Call(); item["then"] = new JsonArray(); yield return Invalid(item, "/then");
    }

    [Theory]
    [InlineData("SInt")][InlineData("Int")][InlineData("DInt")][InlineData("USInt")]
    [InlineData("UInt")][InlineData("UDInt")][InlineData("LInt")][InlineData("ULInt")]
    public void CounterValueTypesAndAuxiliaryPinsAreExplicit(string type)
    {
        var flg = Build(Counter("CTUD", type));
        var part = flg.Descendants(Ns + "Part").Single(p => (string?)p.Attribute("Name") == "CTUD");
        Assert.Equal("1.0", (string?)part.Attribute("Version"));
        Assert.Equal(type, part.Element(Ns + "TemplateValue")!.Value);
        Assert.Equal("GlobalVariable", (string?)part.Element(Ns + "Instance")!.Attribute("Scope"));
        var pins = flg.Descendants(Ns + "NameCon").Where(p => (string?)p.Attribute("UId") == (string?)part.Attribute("UId"))
            .Select(p => p.Attribute("Name")!.Value).ToArray();
        Assert.Equal(new[] { "CU", "CD", "R", "LD", "PV", "QU", "QD", "CV" }.OrderBy(x => x, StringComparer.Ordinal),
            pins.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(3, flg.Descendants(Ns + "OpenCon").Count());
        var rail = flg.Descendants(Ns + "Wire").Single(w => w.Element(Ns + "Powerrail") != null);
        Assert.Equal(4, rail.Elements(Ns + "NameCon").Count());
    }

    [Fact]
    public void CallPinOrderDirectionsAndInstanceIdsAreDeterministic()
    {
        var flg = Build(Call("FB"));
        var call = flg.Descendants(Ns + "Call").Single();
        var info = call.Element(Ns + "CallInfo")!;
        Assert.Equal(new[] { "Command", "Result", "State" }, info.Elements(Ns + "Parameter").Select(p => p.Attribute("Name")!.Value));
        var instance = info.Element(Ns + "Instance")!;
        Assert.Equal(int.Parse(call.Attribute("UId")!.Value, CultureInfo.InvariantCulture) + 1,
            int.Parse(instance.Attribute("UId")!.Value, CultureInfo.InvariantCulture));
        Assert.Equal("DB_User", (string?)instance.Element(Ns + "Component")!.Attribute("Name"));
        foreach (var wire in flg.Descendants(Ns + "Wire").Where(w => w.Elements(Ns + "NameCon")
            .Any(c => (string?)c.Attribute("UId") == (string?)call.Attribute("UId"))))
        {
            var pin = wire.Elements(Ns + "NameCon").Single(c => (string?)c.Attribute("UId") == (string?)call.Attribute("UId"));
            if ((string?)pin.Attribute("Name") == "Result") Assert.Same(pin, wire.Elements().First());
            else Assert.NotSame(pin, wire.Elements().First());
        }
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void TextReadbackKeepsTheCompleteGlobalTimerInstancePath(bool connected)
    {
        var timer = Timer();
        if (connected) timer["q"] = new JsonArray(Coil());
        var input = new ConstructionInput<LadFcBlockSpec>().Read(Block(timer).ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid);
        string xml = ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml;
        Assert.Contains("TON[\"DB_State\".Timer]", LadTextRenderer.Render(xml));
    }

    [Theory]
    [InlineData("pins", 65)][InlineData("reset", 17)][InlineData("load", 17)][InlineData("down", 17)]
    [InlineData("q", 9)][InlineData("qd", 9)]
    public void NewArraysKeepTheirUpperBounds(string field, int size)
    {
        var output = field == "pins" ? Call() : Counter("CTUD");
        output[field] = new JsonArray(Enumerable.Range(0, size)
            .Select(i => (JsonNode)(field == "pins" ? Pin("P" + i) : field is "q" or "qd" ? Coil() : Contact())).ToArray());
        var input = new ConstructionInput<LadFcBlockSpec>().Read(Block(output).ToJsonString(), "ladFcBlock");
        Assert.False(input.IsValid);
        Assert.Equal(ErrorCode.LimitExceeded, input.Error!.Code);
    }

    [Theory]
    [InlineData("plc-foundation", ConstructionProfile.Foundation)]
    [InlineData("full-engine", ConstructionProfile.FullEngine)]
    public void MaintainedBoxExamplesAndSequenceUseTheTypedContract(string profile, ConstructionProfile contractProfile)
    {
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!;
        var block = calls["profiles"]![profile]!["BuildPlcLadFcBlock"]!["operations"]!["rung-boxes"]!
            ["arguments"]!["ladFcBlock"]!;
        var input = new ConstructionInput<LadFcBlockSpec>(contractProfile).Read(block.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        var candidate = ConstructionAdapter.BuildCandidate(input.Value!, "21", contractProfile);
        var flg = XDocument.Parse(candidate.Xml).Descendants(Ns + "FlgNet").ToArray();
        Assert.Equal(3, flg.Length);
        Assert.Contains(flg[0].Descendants(Ns + "Part"), p => (string?)p.Attribute("Name") == "TON");
        Assert.Contains(flg[1].Descendants(Ns + "Part"), p => (string?)p.Attribute("Name") == "CTU");
        Assert.Equal("FB", (string?)flg[2].Descendants(Ns + "CallInfo").Single().Attribute("BlockType"));
        Assert.Equal("NOT RUN", candidate.NativeAcceptance);
        var sequences = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/sequences.json")))!.AsArray();
        var sequence = sequences.Single(s => (string?)s!["id"] == "sequence/plc-lad-rung-build-import")!;
        var step = sequence["steps"]!.AsArray().Single(s => (string?)s!["tool"] == "BuildPlcLadFcBlock")!;
        input = new ConstructionInput<LadFcBlockSpec>(contractProfile)
            .Read(step["arguments"]!["ladFcBlock"]!.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        var sequenceNetworks = XDocument.Parse(ConstructionAdapter.BuildCandidate(input.Value!, "21", contractProfile).Xml)
            .Descendants(Ns + "FlgNet").ToArray();
        Assert.Equal(4, sequenceNetworks.Length);
        Assert.DoesNotContain(sequenceNetworks.SelectMany(network => network.Descendants(Ns + "Part")),
            part => (string?)part.Attribute("Name") is "PContact" or "NContact" or "RCoil");
        Assert.Single(sequenceNetworks[0].Descendants(Ns + "Powerrail"));
        for (int i = 0; i < flg.Length; i++) Assert.True(XNode.DeepEquals(flg[i], sequenceNetworks[i + 1]));

        // The main rung and three box networks still compose as the original four-network block.
        var mainBlock = calls["profiles"]![profile]!["BuildPlcLadFcBlock"]!["operations"]!["rung"]!["arguments"]!["ladFcBlock"]!;
        input = new ConstructionInput<LadFcBlockSpec>(contractProfile).Read(mainBlock.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        var mainNetwork = Assert.Single(XDocument.Parse(ConstructionAdapter.BuildCandidate(input.Value!, "21", contractProfile).Xml)
            .Descendants(Ns + "FlgNet"));
        Assert.True(XNode.DeepEquals(mainNetwork, sequenceNetworks[0]));
        var main = mainBlock["networks"]![0]!;
        var boxesWithMain = block.DeepClone();
        boxesWithMain["networks"]!.AsArray().Insert(0, main.DeepClone());
        var sequenceMain = step["arguments"]!["ladFcBlock"]!["networks"]![0]!;
        Assert.True(JsonNode.DeepEquals(main["rung"], sequenceMain["rung"]));
        input = new ConstructionInput<LadFcBlockSpec>(contractProfile).Read(boxesWithMain.ToJsonString(), "ladFcBlock");
        Assert.True(input.IsValid, V4Json.Serialize(input.Error));
        Assert.Equal(4, XDocument.Parse(ConstructionAdapter.BuildCandidate(input.Value!, "21", contractProfile).Xml)
            .Descendants(Ns + "FlgNet").Count());
    }

    [Theory]
    [InlineData(256, true)][InlineData(257, false)]
    public void CallAccessesAndCallsCountTowardTheExistingPartBudget(int count, bool accepted)
    {
        var block = Block(Call());
        var outputs = block["networks"]![0]!["rung"]!["outputs"]!.AsArray();
        outputs.Clear();
        foreach (int pins in new[] { 64, 64, 64, count == 256 ? 58 : 59 })
        {
            var call = Call();
            call["pins"] = new JsonArray(Enumerable.Range(0, pins).Select(i => (JsonNode)Pin("P" + i)).ToArray());
            outputs.Add(call);
        }
        var input = new ConstructionInput<LadFcBlockSpec>().Read(block.ToJsonString(), "ladFcBlock");
        Assert.Equal(accepted, input.IsValid);
        if (!accepted) Assert.Equal(ErrorCode.LimitExceeded, input.Error!.Code);
        else Assert.Equal(count, XDocument.Parse(ConstructionAdapter.BuildCandidate(input.Value!, "21", ConstructionProfile.Foundation).Xml)
            .Descendants(Ns + "Parts").Single().Elements().Count());
    }

    [Fact]
    public void NestedOutputsKeepAuxiliaryRailsAndPassOrder()
    {
        var timer = Timer();
        var counter = Counter("CTUD");
        var call = Call("FB"); call["then"] = new JsonArray(Coil());
        counter["q"] = new JsonArray(call); counter["qd"] = new JsonArray(Coil("Empty"));
        timer["q"] = new JsonArray(counter);
        var flg = Build(timer);
        Assert.Equal(new[] { "Contact", "TON", "Contact", "Contact", "Contact", "CTUD", "Call", "Coil", "Coil" },
            flg.Element(Ns + "Parts")!.Elements().Where(p => p.Name.LocalName != "Access")
                .Select(p => (string?)p.Attribute("Name") ?? p.Name.LocalName));
        int[] accessIds = flg.Element(Ns + "Parts")!.Elements(Ns + "Access")
            .Select(p => int.Parse(p.Attribute("UId")!.Value, CultureInfo.InvariantCulture)).ToArray();
        int[] partIds = flg.Element(Ns + "Parts")!.Elements().Where(p => p.Name.LocalName != "Access")
            .Select(p => int.Parse(p.Attribute("UId")!.Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.True(accessIds.Max() < partIds.Min());
        foreach (var instance in flg.Descendants(Ns + "Instance"))
        {
            var part = instance.Ancestors().First(p => p.Name.LocalName is "Part" or "Call");
            Assert.Equal(int.Parse(part.Attribute("UId")!.Value, CultureInfo.InvariantCulture) + 1,
                int.Parse(instance.Attribute("UId")!.Value, CultureInfo.InvariantCulture));
        }
        foreach (var open in flg.Descendants(Ns + "OpenCon"))
            Assert.Equal(int.Parse(open.Parent!.Attribute("UId")!.Value, CultureInfo.InvariantCulture) + 1,
                int.Parse(open.Attribute("UId")!.Value, CultureInfo.InvariantCulture));
        Assert.Equal(4, flg.Descendants(Ns + "Wire").Single(w => w.Element(Ns + "Powerrail") != null)
            .Elements(Ns + "NameCon").Count());
        FlgNetRungXmlBuilder.Verify(flg);
    }

    [Theory]
    [InlineData("timer-input")][InlineData("counter-input")][InlineData("call-input")]
    [InlineData("open-source")][InlineData("instance-collision")]
    public void BoxStructuralCheckRejectsBrokenConnections(string defect)
    {
        var flg = Build(defect == "counter-input" ? Counter("CTUD") : defect == "call-input" ? Call("FB") : Timer());
        if (defect == "instance-collision")
        {
            var instance = flg.Descendants(Ns + "Instance").Single();
            instance.SetAttributeValue("UId", instance.Ancestors(Ns + "Part").Single().Attribute("UId")!.Value);
        }
        else if (defect == "open-source")
        {
            var wire = flg.Descendants(Ns + "OpenCon").First().Parent!;
            wire.ReplaceNodes(wire.Elements().Reverse().ToArray());
        }
        else
        {
            string name = defect == "timer-input" ? "PT" : defect == "counter-input" ? "R" : "State";
            flg.Descendants(Ns + "Wire").Single(w => w.Elements(Ns + "NameCon")
                .Any(c => (string?)c.Attribute("Name") == name)).Remove();
        }
        Assert.Throws<InvalidOperationException>(() => FlgNetRungXmlBuilder.Verify(flg));
    }
}
