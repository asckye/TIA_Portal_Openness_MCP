using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.LegacyHost;

/// <summary>Pure candidate assembly; only Compose methods, never file or probe helpers.</summary>
internal static class OfflineBlockCompositionBuilders
{
    internal const string InterfaceNamespace = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    internal static JsonObject Build(string toolName, string release, string json)
    {
        if (release != "21") throw new ArgumentException("Explicit output release 21 required.");
        bool fb = toolName == "ComposePlcFbBlockXml";
        if (!fb && toolName != "ComposePlcFcBlockXml") throw new ArgumentException("Unknown composer.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > OfflineCompositionBuilders.MaxJsonCharacters) throw new ArgumentException("Invalid JSON size.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = parsed.RootElement;
        RequireDecodableStrings(root);
        var common = new[] { "blockName", "name", "blockNumber", "number", "inputs", "outputs", "structuredText", "commentZhCn", "blockCommentZhCn", "comment", "titleZhCn", "blockTitleZhCn", "title", "networkCommentZhCn", "networkComment", "networkTitleZhCn", "networkTitle" };
        Properties(root, fb ? common.Concat(new[] { "inouts", "inOuts", "inOut", "statics", "staticMembers", "static", "temps", "tempMembers", "temp" }).ToArray() : common);
        var name = Text(root, true, "blockName", "name");
        var numberValue = Alias(root, true, "blockNumber", "number")!.Value;
        if (numberValue.ValueKind != JsonValueKind.Number || !numberValue.TryGetInt32(out int number) || number <= 0) throw new ArgumentException("Positive integer required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The unchanged FC helper adds this return member itself. Prevent a generated collision.
        if (!fb) names.Add("Ret_Val");
        int count = 0;
        PlcBlockMemberDefinition[] Members(bool required, params string[] aliases)
        {
            var value = Alias(root, required, aliases);
            if (!value.HasValue) return Array.Empty<PlcBlockMemberDefinition>();
            if (value.Value.ValueKind != JsonValueKind.Array || value.Value.GetArrayLength() > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Bounded array required.");
            return value.Value.EnumerateArray().Select(row =>
            {
                if (++count > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Total interface member limit exceeded.");
                Properties(row, "name", "datatype", "dataType", "commentZhCn", "comment", "commentZh");
                var memberName = Text(row, true, "name");
                if (!names.Add(memberName)) throw new ArgumentException("Duplicate interface name.");
                var comment = Text(row, false, "commentZhCn", "comment", "commentZh");
                if (comment.Length > 0 && string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("Whitespace-only member comment unsupported.");
                return new PlcBlockMemberDefinition(memberName, Text(row, true, "datatype", "dataType"), comment);
            }).ToArray();
        }
        var inputs = Members(!fb, "inputs");
        var outputs = Members(!fb, "outputs");
        var inouts = fb ? Members(false, "inouts", "inOuts", "inOut") : Array.Empty<PlcBlockMemberDefinition>();
        var statics = fb ? Members(false, "statics", "staticMembers", "static") : Array.Empty<PlcBlockMemberDefinition>();
        var temps = fb ? Members(false, "temps", "tempMembers", "temp") : Array.Empty<PlcBlockMemberDefinition>();
        var content = Alias(root, true, "structuredText")!.Value;
        if (content.ValueKind != JsonValueKind.Object) throw new ArgumentException("Structured operation object required.");
        var inner = OfflineCompositionBuilders.Build("BuildStructuredTextXml", "21", content.GetRawText(), true)["Xml"]!.GetValue<string>();
        var commentText = Text(root, false, "commentZhCn", "blockCommentZhCn", "comment");
        var title = Text(root, false, "titleZhCn", "blockTitleZhCn", "title");
        var networkComment = Text(root, false, "networkCommentZhCn", "networkComment");
        var networkTitle = Text(root, false, "networkTitleZhCn", "networkTitle");
        var document = fb
            ? PlcFbBlockXmlComposer.Compose(name, number, inputs, outputs, inouts, statics, temps, inner, commentText, title, networkComment, networkTitle)
            : PlcFcBlockXmlComposer.Compose(name, number, inputs, outputs, inner, commentText, title, networkComment, networkTitle);
        // Preserve text CR and attribute CR/LF/tab on every OS; default helper Save replaces line endings.
        using var buffer = new Utf8Writer();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, NewLineHandling = NewLineHandling.Entitize })) document.Save(writer);
        var xml = buffer.ToString();
        if (xml.Length > OfflineCompositionBuilders.MaxXmlCharacters) throw new ArgumentException("Output size limit exceeded.");
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = OfflineCompositionBuilders.MaxXmlCharacters });
        var checkedDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (checkedDoc.Root?.Name != "Document" || (string?)checkedDoc.Root.Element("Engineering")?.Attribute("version") != "V21" || checkedDoc.Descendants("SW.Blocks." + (fb ? "FB" : "FC")).Count() != 1 || checkedDoc.Descendants(XName.Get("Sections", InterfaceNamespace)).Count() != 1 || checkedDoc.Descendants(XName.Get("StructuredText", OfflineCompositionBuilders.StructuredTextNamespace)).Count() != 1) throw new InvalidOperationException("Unexpected helper format.");
        JsonObject Flags() => new() { ["offlineOnly"] = true, ["outputReleaseKey"] = "21", ["schemaValidated"] = false, ["importValidated"] = false, ["programSemanticsValidated"] = false };
        var meta = Flags();
        meta["success"] = true; meta["nativeAcceptance"] = "NOT RUN";
        meta["provenance"] = "Existing V21 SCL FC/FB Compose helpers, unchanged; text-preserving XML serialization; no version conversion";
        meta["validationScope"] = "Bounded structured inputs and generated complete-document XML well-formedness only";
        var data = Flags();
        data["ok"] = true; data["xmlParseOk"] = true; data["mode"] = fb ? "plc-compose-fb-block-xml" : "plc-compose-fc-block-xml"; data["xml"] = xml; data["error"] = null;
        data["summary"] = new JsonObject { ["blockName"] = name, ["blockNumber"] = number, ["inputCount"] = inputs.Length, ["outputCount"] = outputs.Length, ["inOutCount"] = inouts.Length, ["staticCount"] = statics.Length, ["tempCount"] = temps.Length };
        data["safetyPolicy"] = new JsonObject { ["tia"] = "No connection, worker, import, compile, instance DB creation or project modification.", ["write"] = "Returns XML in memory only; no files are read or written." };
        return new JsonObject { ["Message"] = "Candidate SCL block XML composed; schema, program semantics and import validation pending.", ["Meta"] = meta, ["Ok"] = true, ["Data"] = data, ["Errors"] = null, ["Warnings"] = new JsonArray(OfflineCompositionBuilders.Warning), ["OutputPath"] = null, ["OutputFiles"] = null, ["Xml"] = xml };
    }
    private static void RequireDecodableStrings(JsonElement value)
    {
        // JsonDocument can retain escaped unpaired surrogates until a string is decoded.
        // Classify only decoding failures as bad input, without masking helper-state bugs.
        try
        {
            if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) RequireDecodableStrings(item);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    _ = property.Name;
                    RequireDecodableStrings(property.Value);
                }
        }
        catch (InvalidOperationException) { throw new ArgumentException("Invalid Unicode string."); }
    }
    private static void Properties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Object required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("Unknown or duplicate property.");
    }
    private static JsonElement? Alias(JsonElement root, bool required, params string[] aliases)
    {
        var found = aliases.Where(key => root.TryGetProperty(key, out _)).ToArray();
        if (found.Length > 1 || (required && found.Length == 0)) throw new ArgumentException("Missing or conflicting alias.");
        return found.Length == 0 ? null : root.GetProperty(found[0]);
    }
    private static string Text(JsonElement root, bool required, params string[] aliases)
    {
        var value = Alias(root, required, aliases);
        if (!value.HasValue) return "";
        if (value.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("String required.");
        var text = value.Value.GetString()!;
        if ((required && string.IsNullOrWhiteSpace(text)) || text.Length > OfflineCompositionBuilders.MaxStringCharacters) throw new ArgumentException("Invalid string size.");
        XmlConvert.VerifyXmlChars(text);
        return text;
    }
    private sealed class Utf8Writer : StringWriter { public override Encoding Encoding => Encoding.UTF8; }
}
