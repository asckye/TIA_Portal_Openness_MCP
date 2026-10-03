using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // 2.7.46: pure logic for ManagePlcTagDefinition. PlcTag.Comment / PlcUserConstant.Comment are MultilingualText (official:
    // "The multilingual comment of the PlcTag"), not scalars - the real project refused every comment write with
    // "Complex/reference property requires a dedicated adapter: Siemens.Engineering.MultilingualText". The comment is therefore
    // split off propertiesJson here and written per culture through MultilingualText.Items by the engine.
    internal static class PlcTagEditingLogic
    {
        internal static readonly string[] Actions = { "read", "create", "update", "delete" };
        internal static readonly string[] Kinds = { "tag", "constant" };

        internal sealed class Request
        {
            public string Action = ""; public string Kind = ""; public bool Writes;
            public JsonObject Scalars = new JsonObject();
            // culture null = the project's editing language
            public (string? Culture, string Text)[] Comments = Array.Empty<(string?, string)>();
        }

        internal static Request Validate(string action, string kind, string name, string dataType, string addressOrValue, string propertiesJson, bool dryRun)
        {
            if (Array.IndexOf(Kinds, kind) < 0) throw new ArgumentException("kind must be tag/constant.");
            if (Array.IndexOf(Actions, action) < 0) throw new ArgumentException("Invalid action.");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact name required.");
            var changes = JsonNode.Parse(string.IsNullOrWhiteSpace(propertiesJson) ? "{}" : propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
            if (changes.ContainsKey("Name")) throw new ArgumentException("Renaming is not supported by this tool.");
            if ((action == "read" || action == "delete") && changes.Count != 0) throw new ArgumentException("No properties allowed for read/delete.");
            if (action == "create" && (string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(addressOrValue))) throw new ArgumentException("Creation requires explicit dataType and addressOrValue.");
            if (action == "update" && changes.Count == 0) throw new ArgumentException("update needs at least one property (scalars, or Comment as a string / {culture: text} object).");
            var r = new Request { Action = action, Kind = kind, Writes = action != "read" && !dryRun };
            var scalars = new JsonObject();
            foreach (var pair in changes)
            {
                if (pair.Key == "Comment") { r.Comments = ParseComment(pair.Value); continue; }
                scalars[pair.Key] = pair.Value?.DeepClone();
            }
            r.Scalars = scalars;
            return r;
        }

        // "text" -> editing language; {"zh-CN":"文本","en-US":"text"} -> per culture (each must be active in the project).
        internal static (string? Culture, string Text)[] ParseComment(JsonNode? node)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var text)) return new (string?, string)[] { (null, text) };
            if (node is JsonObject o)
            {
                if (o.Count == 0) throw new ArgumentException("Comment object must map at least one culture (e.g. \"zh-CN\") to a string.");
                var list = new List<(string?, string)>();
                foreach (var pair in o)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 16) throw new ArgumentException("Comment culture names look like \"zh-CN\" / \"en-US\": " + pair.Key);
                    if (pair.Value is not JsonValue cv || !cv.TryGetValue<string>(out var ct)) throw new ArgumentException("Comment[" + pair.Key + "] must be a string.");
                    list.Add((pair.Key, ct));
                }
                return list.ToArray();
            }
            throw new ArgumentException("Comment must be a string (written to the editing language) or an object {culture: text}.");
        }
    }
}
