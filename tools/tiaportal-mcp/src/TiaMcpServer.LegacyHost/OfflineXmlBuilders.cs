using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.LegacyHost;

/// <summary>Pure-memory candidate generation. No SDK, file, process, or project access.</summary>
internal static class OfflineXmlBuilders
{
    internal const int MaxJsonCharacters = 262144;
    internal const int MaxItems = 1000;
    internal const int MaxStringCharacters = 4096;
    internal const int MaxXmlCharacters = 1048576;
    internal const string Warning = "V21 candidate XML only. Target-installed Siemens XSD validation and TIA import validation have NOT RUN. Not import-ready; no lower-version compatibility is claimed.";

    internal static JsonObject Build(string toolName, string outputReleaseKey, string json)
    {
        var format = toolName == "BuildPlcUdtXml" ? PlcDeclarationXmlFormat.ForRelease(outputReleaseKey) : null;
        if (format == null && outputReleaseKey != "21")
            throw new ArgumentException("Only explicit outputReleaseKey='21' candidate output is implemented. Other output releases are unavailable; no version rewriting is performed.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters)
            throw new ArgumentException("Builder JSON must contain 1 to 262144 characters.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        string xml, mode;
        JsonObject summary;
        if (toolName == "BuildPlcUdtXml")
        {
            Properties(root, "name", "udtName", "members");
            var name = Text(root, true, "name", "udtName");
            var rows = Rows(root, "members");
            var members = rows.Select(row =>
            {
                Properties(row, "name", "datatype", "dataType", "externalWritable", "commentZhCn", "comment", "commentZh");
                return new PlcUdtMemberDefinition(Text(row, true, "name"), Text(row, true, "datatype", "dataType"),
                    Boolean(row, "externalWritable"), Text(row, false, "commentZhCn", "comment", "commentZh"));
            }).ToArray();
            xml = PlcUdtXmlBuilder.BuildXml(name, members, outputReleaseKey);
            mode = "plc-build-udt-xml";
            summary = new JsonObject { ["udtName"] = name, ["memberCount"] = members.Length };
        }
        else if (toolName == "BuildPlcTagTableXml")
        {
            Properties(root, "tableName", "name", "tags");
            var name = Text(root, true, "tableName", "name");
            var rows = Rows(root, "tags");
            var tags = rows.Select(row =>
            {
                Properties(row, "name", "dataTypeName", "datatype", "dataType", "logicalAddress", "address");
                return new PlcTagDefinition(Text(row, true, "name"), Text(row, true, "dataTypeName", "datatype", "dataType"),
                    Text(row, true, "logicalAddress", "address"));
            }).ToArray();
            xml = PlcTagTableXmlBuilder.BuildXml(name, tags);
            mode = "plc-build-tag-table-xml";
            summary = new JsonObject { ["tableName"] = name, ["tagCount"] = tags.Length };
        }
        else throw new ArgumentException("Unknown offline XML builder.");
        if (xml.Length > MaxXmlCharacters) throw new ArgumentException("Generated XML exceeds one Mi character.");
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters
        });
        var parsed = XDocument.Load(reader);
        if (parsed.Root?.Name != "Document" || (string?)parsed.Root.Element("Engineering")?.Attribute("version") != (format?.EngineeringVersion ?? "V21"))
            throw new InvalidOperationException("Builder returned an unexpected output format.");
        var data = new JsonObject
        {
            ["ok"] = true, ["mode"] = mode, ["offlineOnly"] = true, ["xmlParseOk"] = true,
            ["schemaValidated"] = false, ["importValidated"] = false, ["outputReleaseKey"] = outputReleaseKey,
            ["interfaceNamespace"] = format?.InterfaceNamespace.NamespaceName, ["interfaceSchemaFile"] = format?.InterfaceSchemaFile,
            ["error"] = null, ["xml"] = xml, ["summary"] = summary,
            ["safetyPolicy"] = new JsonObject { ["tia"] = "No connection, import or project modification.", ["write"] = "Returns XML in memory only; no files are read or written." }
        };
        return new JsonObject
        {
            ["Message"] = "Candidate XML built in memory; schema and import validation pending.",
            ["Meta"] = new JsonObject
            {
                ["success"] = true, ["offlineOnly"] = true, ["outputReleaseKey"] = outputReleaseKey,
                ["schemaValidated"] = false, ["importValidated"] = false, ["nativeAcceptance"] = "NOT RUN",
                ["provenance"] = format != null ? "Flat UDT generation with the target release interface schema and object attributes; not an XML converter" : "Existing PlcTagTableXmlBuilder V21 generator; no version conversion",
                ["validationScope"] = "bounded input and XML well-formedness only"
            },
            ["Ok"] = true, ["Data"] = data, ["Errors"] = null, ["Warnings"] = new JsonArray(format != null ? PlcDeclarationXmlFormat.Warning : Warning),
            ["OutputPath"] = null, ["OutputFiles"] = null, ["Xml"] = xml
        };
    }

    private static void Properties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException("Duplicate, unknown or case-mismatched property: " + property.Name);
    }

    private static JsonElement[] Rows(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0 || value.GetArrayLength() > MaxItems)
            throw new ArgumentException(name + " must be an array of 1 to 1000 objects.");
        return value.EnumerateArray().ToArray();
    }

    private static string Text(JsonElement root, bool required, params string[] aliases)
    {
        var found = aliases.Where(alias => root.TryGetProperty(alias, out _)).ToArray();
        if (found.Length > 1) throw new ArgumentException("Supply only one alias for " + aliases[0] + ".");
        if (found.Length == 0)
        {
            if (required) throw new ArgumentException("Missing required string: " + aliases[0]);
            return "";
        }
        var value = root.GetProperty(found[0]);
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Expected JSON string: " + found[0]);
        var text = value.GetString()!;
        if ((required && string.IsNullOrWhiteSpace(text)) || text.Length > MaxStringCharacters)
            throw new ArgumentException("Invalid or oversized string: " + found[0]);
        XmlConvert.VerifyXmlChars(text);
        return text;
    }

    private static bool Boolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            throw new ArgumentException("Expected JSON boolean: " + name);
        return value.GetBoolean();
    }
}
