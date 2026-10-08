using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

/// <summary>Conservative candidate-only adapter. Never invokes helper probe/file methods.</summary>
internal static class OfflineLadderBuilders
{
    internal const string FlgNetNamespace = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
    internal const int MaxNetworks = 64;
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal" };

    internal static JsonObject Build(string toolName, string release, string json)
    {
        if (release != "21") throw new ArgumentException("Explicit release 21 required.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > OfflineCompositionBuilders.MaxJsonCharacters) throw new ArgumentException("Invalid JSON size.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = parsed.RootElement;
        Decode(root);
        XDocument document;
        JsonObject summary;
        string mode;
        int parameterCount = 0;
        if (toolName == "BuildFlgNetCallXml")
        {
            var call = Call(root, ref parameterCount);
            document = new XDocument(new XDeclaration("1.0", "utf-8", null), call);
            summary = new JsonObject { ["callName"] = (string?)call.Descendants(Ns + "CallInfo").Single().Attribute("Name"), ["parameterCount"] = parameterCount };
            mode = "plc-build-flgnet-call-xml";
        }
        else if (toolName == "ComposePlcLadFcBlockXml")
        {
            Properties(root, "blockName", "name", "blockNumber", "number", "inputs", "outputs", "networks", "commentZhCn", "blockCommentZhCn", "comment", "titleZhCn", "blockTitleZhCn", "title");
            var blockName = Identifier(Text(root, true, "blockName", "name"));
            var numberValue = Alias(root, true, "blockNumber", "number")!.Value;
            if (numberValue.ValueKind != JsonValueKind.Number || !numberValue.TryGetInt32(out int number) || number <= 0) throw new ArgumentException("Positive integer required.");
            var memberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ret_Val" };
            int memberCount = 0;
            PlcBlockMemberDefinition[] Members(string key) => Rows(root, false, OfflineCompositionBuilders.MaxItems, key).Select(row =>
            {
                if (++memberCount > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Member limit exceeded.");
                Properties(row, "name", "datatype", "dataType", "commentZhCn", "comment", "commentZh");
                var name = Identifier(Text(row, true, "name"));
                if (!memberNames.Add(name)) throw new ArgumentException("Duplicate member.");
                var comment = Text(row, false, "commentZhCn", "comment", "commentZh");
                if (comment.Length > 0 && string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("Whitespace-only member comment unsupported.");
                return new PlcBlockMemberDefinition(name, DataType(Text(row, true, "datatype", "dataType")), comment);
            }).ToArray();
            var inputs = Members("inputs"); var outputs = Members("outputs");
            var rows = Rows(root, true, MaxNetworks, "networks");
            if (rows.Length == 0) throw new ArgumentException("At least one network required.");
            var networks = new List<PlcLadFcBlockXmlComposer.LadNetwork>();
            foreach (var row in rows)
            {
                Properties(row, "callJson", "call", "titleZhCn", "title", "commentZhCn", "comment");
                var call = Call(Alias(row, true, "callJson", "call")!.Value, ref parameterCount);
                networks.Add(new PlcLadFcBlockXmlComposer.LadNetwork(call, Text(row, false, "titleZhCn", "title"), Text(row, false, "commentZhCn", "comment")));
            }
            document = PlcLadFcBlockXmlComposer.Compose(blockName, number, inputs, outputs, networks, Text(root, false, "commentZhCn", "blockCommentZhCn", "comment"), Text(root, false, "titleZhCn", "blockTitleZhCn", "title"));
            summary = new JsonObject { ["blockName"] = blockName, ["blockNumber"] = number, ["networkCount"] = networks.Count, ["inputCount"] = inputs.Length, ["outputCount"] = outputs.Length, ["parameterCount"] = parameterCount };
            mode = "plc-compose-lad-fc-block-xml";
        }
        else throw new ArgumentException("Unknown ladder tool.");
        using var buffer = new Utf8Writer();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, NewLineHandling = NewLineHandling.Entitize })) document.Save(writer);
        var xml = buffer.ToString();
        if (xml.Length > OfflineCompositionBuilders.MaxXmlCharacters) throw new ArgumentException("Output limit exceeded.");
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = OfflineCompositionBuilders.MaxXmlCharacters });
        var verified = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (toolName == "BuildFlgNetCallXml")
        {
            if (verified.Root?.Name != Ns + "FlgNet") throw new InvalidOperationException("Unexpected helper root.");
        }
        else
        {
            if (verified.Root?.Name != "Document" || (string?)verified.Root.Element("Engineering")?.Attribute("version") != "V21" || verified.Descendants("SW.Blocks.FC").Count() != 1 || verified.Descendants(XName.Get("Sections", OfflineBlockCompositionBuilders.InterfaceNamespace)).Count() != 1) throw new InvalidOperationException("Unexpected composer document.");
            var ids = verified.Descendants().Attributes("ID").Select(x => x.Value).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new InvalidOperationException("Document ID collision.");
        }
        foreach (var network in verified.Descendants(Ns + "FlgNet")) VerifyNetwork(network);
        JsonObject Flags() => new() { ["offlineOnly"] = true, ["outputReleaseKey"] = "21", ["schemaValidated"] = false, ["importValidated"] = false, ["programSemanticsValidated"] = false };
        var meta = Flags(); meta["success"] = true; meta["nativeAcceptance"] = "NOT RUN";
        meta["provenance"] = "Unchanged V21 FlgNetCallXmlBuilder and PlcLadFcBlockXmlComposer pure methods; no version conversion";
        meta["validationScope"] = "Bounded structured inputs, generated XML well-formedness, per-network identifiers and wire references only; no symbol/type resolution";
        var data = Flags(); data["ok"] = true; data["xmlParseOk"] = true; data["mode"] = mode; data["xml"] = xml; data["error"] = null; data["summary"] = summary;
        data["safetyPolicy"] = new JsonObject { ["tia"] = "No connection, worker, import, compile or project modification.", ["write"] = "Returns XML in memory only; no file reads or writes." };
        return new JsonObject { ["Message"] = "Candidate LAD call XML generated; schema, import and program validation pending.", ["Meta"] = meta, ["Ok"] = true, ["Data"] = data, ["Errors"] = null, ["Warnings"] = new JsonArray(OfflineCompositionBuilders.Warning), ["OutputPath"] = null, ["OutputFiles"] = null, ["Xml"] = xml };
    }
    private static XNamespace Ns => FlgNetNamespace;
    private static XElement Call(JsonElement root, ref int total)
    {
        Properties(root, "callName", "name", "parameters");
        var name = Identifier(Text(root, true, "callName", "name"));
        var parameters = new List<FlgNetCallParameter>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "eno" };
        foreach (var row in Rows(root, true, OfflineCompositionBuilders.MaxItems, "parameters"))
        {
            if (++total > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Parameter limit exceeded.");
            Properties(row, "name", "parameterName", "section", "dataType", "datatype", "type", "sourceKind", "source", "kind", "symbolPath", "symbol", "value", "constantValue");
            var port = Identifier(Text(row, true, "name", "parameterName"));
            if (!names.Add(port)) throw new ArgumentException("Duplicate or reserved port.");
            var section = Text(row, true, "section");
            if (section is not ("Input" or "Output")) throw new ArgumentException("Unsupported port direction.");
            var type = DataType(Text(row, true, "dataType", "datatype", "type"));
            var source = Text(row, false, "sourceKind", "source", "kind");
            bool constant = source is "constant" or "literal" or "literalconstant";
            if (!constant && source is not ("" or "global" or "globalvariable")) throw new ArgumentException("Unsupported source kind.");
            var path = Alias(row, false, "symbolPath", "symbol");
            var value = Alias(row, false, "value", "constantValue");
            if (constant)
            {
                if (section != "Input" || path.HasValue || !value.HasValue) throw new ArgumentException("Constant requires input and no symbol.");
                parameters.Add(FlgNetCallParameter.Constant(port, section, type, Text(row, true, "value", "constantValue")));
            }
            else
            {
                if (!path.HasValue || value.HasValue) throw new ArgumentException("Global source requires a symbol only.");
                string[] components;
                if (row.TryGetProperty("symbolPath", out var array))
                {
                    if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) throw new ArgumentException("Bounded symbol path required.");
                    components = array.EnumerateArray().Select(x => Identifier(String(x, true))).ToArray();
                }
                else components = Text(row, true, "symbol").Split('.').Select(Identifier).ToArray();
                if (components.Length > 32) throw new ArgumentException("Symbol depth exceeded.");
                parameters.Add(FlgNetCallParameter.Global(port, section, type, components));
            }
        }
        var result = FlgNetCallXmlBuilder.BuildFlgNet(name, parameters);
        VerifyNetwork(result);
        return result;
    }
    private static void VerifyNetwork(XElement network)
    {
        var parts = network.Element(Ns + "Parts")?.Elements().ToArray() ?? throw new InvalidOperationException("Missing parts.");
        var wires = network.Element(Ns + "Wires")?.Elements(Ns + "Wire").ToArray() ?? throw new InvalidOperationException("Missing wires.");
        var ids = parts.Concat(wires).Select(e => (string?)e.Attribute("UId") ?? "").ToArray();
        if (ids.Any(id => !int.TryParse(id, out int n) || n < 1) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new InvalidOperationException("UId collision.");
        var call = parts.Single(e => e.Name == Ns + "Call");
        var info = call.Element(Ns + "CallInfo")!;
        var parameters = info.Elements(Ns + "Parameter").ToArray();
        if ((string?)info.Attribute("BlockType") != "FC" || wires.Length != parameters.Length + 1 || parts.Length != parameters.Length + 1) throw new InvalidOperationException("Unexpected call shape.");
        var accessIds = parts.Where(e => e.Name == Ns + "Access").Select(e => (string)e.Attribute("UId")!).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < wires.Length; i++)
        {
            var ends = wires[i].Elements().ToArray();
            if (ends.Length != 2) throw new InvalidOperationException("Unexpected wire shape.");
            var port = ends.Single(e => e.Name == Ns + "NameCon");
            if ((string?)port.Attribute("UId") != (string?)call.Attribute("UId")) throw new InvalidOperationException("Broken call reference.");
            if (i == 0)
            {
                if (ends[0].Name != Ns + "Powerrail" || (string?)port.Attribute("Name") != "en") throw new InvalidOperationException("Invalid enable wire.");
            }
            else
            {
                var parameter = parameters[i - 1];
                var ident = ends.Single(e => e.Name == Ns + "IdentCon");
                if (!accessIds.Remove((string)ident.Attribute("UId")!) || (string?)port.Attribute("Name") != (string?)parameter.Attribute("Name") || (ends[0] == port) != ((string?)parameter.Attribute("Section") == "Output")) throw new InvalidOperationException("Broken port wire.");
            }
        }
        if (accessIds.Count != 0) throw new InvalidOperationException("Unwired access.");
    }
    private static string DataType(string text) => Types.Contains(text) ? text : throw new ArgumentException("Unsupported datatype.");
    private static string Identifier(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_') || text.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) throw new ArgumentException("Simple identifier required.");
        return text;
    }
    private static void Decode(JsonElement value)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Decode(child);
            else if (value.ValueKind == JsonValueKind.Object) foreach (var p in value.EnumerateObject()) { _ = p.Name; Decode(p.Value); }
        }
        catch (InvalidOperationException) { throw new ArgumentException("Invalid Unicode string."); }
    }
    private static void Properties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Object required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject()) if (!seen.Add(p.Name) || !allowed.Contains(p.Name, StringComparer.Ordinal)) throw new ArgumentException("Unknown or duplicate property.");
    }
    private static JsonElement? Alias(JsonElement value, bool required, params string[] aliases)
    {
        var found = aliases.Where(key => value.TryGetProperty(key, out _)).ToArray();
        if (found.Length > 1 || (required && found.Length == 0)) throw new ArgumentException("Missing or conflicting alias.");
        return found.Length == 0 ? null : value.GetProperty(found[0]);
    }
    private static string Text(JsonElement value, bool required, params string[] aliases) => Alias(value, required, aliases) is JsonElement text ? String(text, required) : "";
    private static string String(JsonElement value, bool required)
    {
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("String required.");
        var text = value.GetString()!;
        if ((required && string.IsNullOrWhiteSpace(text)) || text.Length > OfflineCompositionBuilders.MaxStringCharacters) throw new ArgumentException("Invalid string size.");
        XmlConvert.VerifyXmlChars(text); return text;
    }
    private static JsonElement[] Rows(JsonElement value, bool required, int max, params string[] aliases)
    {
        var array = Alias(value, required, aliases);
        if (!array.HasValue) return Array.Empty<JsonElement>();
        if (array.Value.ValueKind != JsonValueKind.Array || array.Value.GetArrayLength() > max) throw new ArgumentException("Bounded array required.");
        return array.Value.EnumerateArray().ToArray();
    }
    private sealed class Utf8Writer : StringWriter { public override Encoding Encoding => Encoding.UTF8; }
}
