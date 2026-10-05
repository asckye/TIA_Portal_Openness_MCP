using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.LegacyHost;

/// <summary>Bounded in-memory adaptation of existing V21 generators; never invokes their file/probe methods.</summary>
internal static class OfflineCompositionBuilders
{
    internal const int MaxJsonCharacters = 262144;
    internal const int MaxXmlCharacters = 1048576;
    internal const int MaxItems = 1000;
    internal const int MaxStringCharacters = 4096;
    internal const string StructuredTextNamespace = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";
    internal const string Warning = "V21 candidate XML only. Target-installed Siemens XSD and TIA import validation have NOT RUN. Not import-ready; no lower-version compatibility or PLC program correctness is claimed.";

    internal static JsonObject Build(string name, string outputReleaseKey, string json, bool innerOnly = false)
    {
        var format = name == "BuildPlcGlobalDbXml" ? PlcDeclarationXmlFormat.ForRelease(outputReleaseKey) : null;
        if (format == null && outputReleaseKey != "21") throw new ArgumentException("Explicit outputReleaseKey 21 required.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters) throw new ArgumentException("Invalid JSON size.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        string xml, mode, scope;
        JsonObject summary;
        if (name == "BuildPlcGlobalDbXml")
        {
            if (innerOnly) throw new ArgumentException("GlobalDB does not support innerOnly.");
            Properties(root, "dbName", "name", "dbNumber", "number", "staticMembers", "members");
            var dbName = LiteralText(Text(root, true, "dbName", "name"));
            var number = Integer(root, null, 1, int.MaxValue, "dbNumber", "number");
            var members = Rows(root, "staticMembers", "members").Select(row =>
            {
                Properties(row, "name", "datatype", "dataType", "externalWritable", "commentZhCn", "comment", "commentZh", "startValue");
                bool? writable = null;
                if (row.TryGetProperty("externalWritable", out var value))
                {
                    if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) throw new ArgumentException("Boolean required.");
                    writable = value.GetBoolean();
                }
                return new PlcDbMemberDefinition(Text(row, true, "name"), Text(row, true, "datatype", "dataType"),
                    writable, OptionalElementText(Text(row, false, "commentZhCn", "comment", "commentZh")), OptionalElementText(Text(row, false, "startValue")));
            }).ToArray();
            xml = PlcGlobalDbXmlBuilder.BuildXml(dbName, number, members, outputReleaseKey);
            mode = "plc-build-global-db-xml";
            scope = "bounded input and complete document XML well-formedness only";
            summary = new JsonObject { ["dbName"] = dbName, ["dbNumber"] = number, ["memberCount"] = members.Length };
        }
        else if (name == "BuildStructuredTextXml")
        {
            Properties(root, "firstUid", "operations");
            // Leave enough headroom for every node allowed by the bounded input, avoiding helper integer rollover.
            var builder = new StructuredTextXmlBuilder(Integer(root, 21, 1, 1000000000, "firstUid"));
            var operations = Rows(root, "operations");
            foreach (var op in operations)
            {
                // Validate object and all keys before interpreting an operation.
                Properties(op, "op", "kind", "type", "indent", "condition", "conditionVariable", "variable", "target", "source", "fromSymbol", "literalValue", "value", "text", "count", "name", "items");
                var kind = Text(op, true, "op", "kind", "type").Trim().ToLowerInvariant();
                switch (kind)
                {
                    case "if": case "ifheader": case "elsif": case "elseif": case "elsifheader":
                        OpProperties(op, "indent", "condition", "conditionVariable", "variable");
                        var condition = SymbolText(Text(op, true, "condition", "conditionVariable", "variable"));
                        if (kind is "if" or "ifheader") builder.IfHeader(condition, Indent(op));
                        else builder.ElsIfHeader(condition, Indent(op));
                        break;
                    case "else": case "endif": case "end_if":
                        OpProperties(op, "indent");
                        if (kind == "else") builder.ElseLine(Indent(op)); else builder.EndIf(Indent(op));
                        break;
                    case "assign": case "assignment":
                        OpProperties(op, "indent", "target", "source", "fromSymbol", "literalValue", "value");
                        var target = SymbolText(Text(op, true, "target"));
                        var hasSource = Alias(op, false, "source", "fromSymbol").HasValue;
                        var hasLiteral = Alias(op, false, "literalValue", "value").HasValue;
                        if (hasSource == hasLiteral) throw new ArgumentException("Exactly one source or literal is required.");
                        if (hasSource) builder.AssignFromSymbol(target, SymbolText(Text(op, true, "source", "fromSymbol")), Indent(op));
                        else builder.Assignment(target, LiteralText(Text(op, true, "literalValue", "value")), Indent(op));
                        break;
                    case "token":
                        OpProperties(op, "indent", "text");
                        if (Indent(op) > 0) builder.Blank(Indent(op));
                        builder.Token(AttributeText(Text(op, true, "text")));
                        break;
                    case "blank":
                        OpProperties(op, "count"); builder.Blank(Integer(op, 1, 1, 4096, "count")); break;
                    case "newline": case "new_line":
                        OpProperties(op); builder.NewLine(); break;
                    case "global": case "local": case "symbol":
                        OpProperties(op, "indent", "name");
                        var symbol = AttributeText(Text(op, true, "name"));
                        if (Indent(op) > 0) builder.Blank(Indent(op));
                        if (kind == "global")
                        {
                            if (symbol.Contains('"') || symbol.Contains('#') || symbol.Split('.').Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Unsupported global path.");
                            builder.GlobalVariable(symbol.Split('.'));
                        }
                        else if (kind == "local")
                        {
                            if (symbol != symbol.Trim() || symbol.Contains('.') || symbol.Contains('#') || symbol.Contains('"')) throw new ArgumentException("Single local name required.");
                            builder.LocalVariable(symbol);
                        }
                        else builder.Symbol(SymbolText(symbol));
                        break;
                    case "literal":
                        OpProperties(op, "indent", "value", "literalValue");
                        if (Indent(op) > 0) builder.Blank(Indent(op));
                        builder.LiteralConstant(LiteralText(Text(op, true, "value", "literalValue"))); break;
                    case "line":
                        OpProperties(op, "indent", "items");
                        if (Indent(op) > 0) builder.Blank(Indent(op));
                        var items = Rows(op, "items");
                        string? lastToken = null;
                        for (var i = 0; i < items.Length; i++)
                        {
                            var item = items[i];
                            Properties(item, "sym", "token", "lit", "raw");
                            if (item.EnumerateObject().Count() != 1) throw new ArgumentException("Exactly one line item kind required.");
                            var key = item.EnumerateObject().Single().Name;
                            var text = key == "lit" ? LiteralText(Text(item, true, key)) : AttributeText(Text(item, true, key));
                            if (i > 0 && key != "raw" && !(key == "token" && text is ")" or "," or ";")) builder.Blank();
                            if (key == "sym") builder.Symbol(SymbolText(text));
                            else if (key == "lit") builder.LiteralConstant(text);
                            else builder.Token(text);
                            lastToken = key is "token" or "raw" ? text : null;
                            if (i % 32 == 31) RequireOutputBound(builder.BuildInnerXml());
                        }
                        if (lastToken != ";") builder.Token(";");
                        builder.NewLine(); break;
                    default: throw new ArgumentException("Unsupported operation.");
                }
                RequireOutputBound(builder.BuildInnerXml());
            }
            xml = innerOnly ? builder.BuildInnerXml() : builder.BuildStructuredTextXml();
            mode = "plc-build-structured-text-xml";
            scope = innerOnly ? "bounded input and fragment XML well-formedness in original StructuredText/v4 namespace context only" : "bounded input and StructuredText fragment XML well-formedness only";
            summary = new JsonObject { ["innerOnly"] = innerOnly, ["operationCount"] = operations.Length, ["fragmentNamespace"] = StructuredTextNamespace };
        }
        else throw new ArgumentException("Unknown builder.");
        RequireOutputBound(xml);
        // A fragment is parsed in its intended namespace context; never strip or rewrite namespace declarations.
        var toParse = innerOnly ? "<StructuredText xmlns=\"" + StructuredTextNamespace + "\">" + xml + "</StructuredText>" : xml;
        using var reader = XmlReader.Create(new StringReader(toParse), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters + 256
        });
        var parsed = XDocument.Load(reader);
        if (name == "BuildPlcGlobalDbXml")
        {
            if (parsed.Root?.Name != "Document" || (string?)parsed.Root.Element("Engineering")?.Attribute("version") != format!.EngineeringVersion) throw new InvalidOperationException("Unexpected document format.");
        }
        else if (parsed.Root?.Name != XName.Get("StructuredText", StructuredTextNamespace)) throw new InvalidOperationException("Unexpected fragment format.");
        var data = new JsonObject
        {
            ["ok"] = true, ["mode"] = mode, ["offlineOnly"] = true, ["xmlParseOk"] = true,
            ["schemaValidated"] = false, ["importValidated"] = false, ["programSemanticsValidated"] = false, ["outputReleaseKey"] = outputReleaseKey,
            ["interfaceNamespace"] = format?.InterfaceNamespace.NamespaceName, ["interfaceSchemaFile"] = format?.InterfaceSchemaFile,
            ["error"] = null, ["xml"] = xml, ["summary"] = summary,
            ["safetyPolicy"] = new JsonObject { ["tia"] = "No connection, import or project modification.", ["write"] = "Returns XML in memory only; no files are read or written." }
        };
        return new JsonObject
        {
            ["Message"] = "Candidate XML built in memory; schema, program semantics and import validation pending.",
            ["Meta"] = new JsonObject
            {
                ["success"] = true, ["offlineOnly"] = true, ["outputReleaseKey"] = outputReleaseKey, ["schemaValidated"] = false,
                ["importValidated"] = false, ["programSemanticsValidated"] = false, ["nativeAcceptance"] = "NOT RUN",
                ["provenance"] = format != null ? "Flat GlobalDB generation with the target release interface schema and object attributes; not an XML converter" : "Existing V21 StructuredText generator; no version conversion", ["validationScope"] = scope
            },
            ["Ok"] = true, ["Data"] = data, ["Errors"] = null, ["Warnings"] = new JsonArray(format != null ? PlcDeclarationXmlFormat.Warning : Warning),
            ["OutputPath"] = null, ["OutputFiles"] = null, ["Xml"] = xml
        };
    }

    private static void RequireOutputBound(string xml)
    {
        if (xml.Length > MaxXmlCharacters) throw new ArgumentException("Output size limit exceeded.");
    }
    private static int Indent(JsonElement op) => Integer(op, 0, 0, 4096, "indent");
    private static void OpProperties(JsonElement op, params string[] allowed) => Properties(op, new[] { "op", "kind", "type" }.Concat(allowed).ToArray());
    private static void Properties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Object required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("Duplicate or unknown property.");
    }
    private static JsonElement? Alias(JsonElement root, bool required, params string[] aliases)
    {
        var found = aliases.Where(key => root.TryGetProperty(key, out _)).ToArray();
        if (found.Length > 1 || (required && found.Length == 0)) throw new ArgumentException("Missing or conflicting aliases.");
        return found.Length == 0 ? null : root.GetProperty(found[0]);
    }
    private static JsonElement[] Rows(JsonElement root, params string[] aliases)
    {
        var value = Alias(root, true, aliases)!.Value;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > MaxItems) throw new ArgumentException("Invalid array size.");
        return value.EnumerateArray().ToArray();
    }
    private static string Text(JsonElement root, bool required, params string[] aliases)
    {
        var value = Alias(root, required, aliases);
        if (!value.HasValue) return "";
        if (value.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("String required.");
        var text = value.Value.GetString()!;
        if ((required && string.IsNullOrWhiteSpace(text)) || text.Length > MaxStringCharacters) throw new ArgumentException("Invalid string size.");
        XmlConvert.VerifyXmlChars(text);
        return text;
    }
    private static int Integer(JsonElement root, int? fallback, int min, int max, params string[] aliases)
    {
        var value = Alias(root, !fallback.HasValue, aliases);
        if (!value.HasValue) return fallback!.Value;
        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt32(out var number) || number < min || number > max) throw new ArgumentException("Invalid integer.");
        return number;
    }
    private static string AttributeText(string value)
    {
        // SecurityElement.Escape in the legacy helper does not entitize XML-normalized attribute whitespace.
        if (value.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) throw new ArgumentException("Unsupported XML attribute whitespace.");
        return value;
    }
    private static string OptionalElementText(string value)
    {
        if (value.Length > 0 && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Whitespace-only optional element is unsupported.");
        return LiteralText(value);
    }
    private static string LiteralText(string value)
    {
        // XML normalizes CR in element content. LF and tab remain representable without changing helper bytes.
        if (value.Contains('\r')) throw new ArgumentException("Unsupported XML element carriage return.");
        return value;
    }
    private static string SymbolText(string value)
    {
        AttributeText(value);
        // Existing helper strips quotes and splits dots: reject quote syntax it cannot represent faithfully.
        if (value != value.Trim()) throw new ArgumentException("Whitespace around symbols is unsupported.");
        var text = value.StartsWith('#') ? value[1..] : value;
        if (text.Contains('"'))
        {
            if (value.StartsWith('#') || text.Length < 3 || text[0] != '"' || text[^1] != '"' || text.Count(c => c == '"') != 2) throw new ArgumentException("Unsupported quoted path.");
            text = text[1..^1];
        }
        if (text.Split('.').Any(segment => string.IsNullOrWhiteSpace(segment) || segment != segment.Trim())) throw new ArgumentException("Invalid symbol path.");
        return value;
    }
}
