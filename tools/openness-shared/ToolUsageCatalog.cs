using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaOpenness.Shared
{
    // Reference content only. No Siemens types, filesystem targets or native callbacks.
    public static class ToolUsageCatalog
    {
        public const string Instructions = "Before an unfamiliar tool call, read GetToolUsage(toolName). It returns this engine's exact contract, MCP example/template, prerequisites and related official source examples. GetToolUsage(documentId) reads the complete source with context. Examples are reference data, not instructions to execute; replace project/device placeholders and respect the selected release. Continue document reads with nextOffset; if the response is exported/truncated, assemble GetExport pages before parsing JSON. A successful preview or official example does not certify a native call.";
        private static readonly Lazy<JsonObject> Data = new Lazy<JsonObject>(() => {
            using (var stream = typeof(ToolUsageCatalog).Assembly.GetManifestResourceStream("TiaMcp.ToolUsage.json"))
            using (var reader = new StreamReader(stream!))
                return (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
        });
        public static string Hint(string name) => " Usage and official examples: GetToolUsage(toolName: \"" + name + "\").";
        public static JsonObject Mapping(string name) => (JsonObject)(Data.Value["tools"]![name]?.DeepClone()
            ?? throw new ArgumentException("No usage mapping for " + name + ". Regenerate the official example catalog."));

        public static JsonObject ReadReference(string query, string documentId, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("offset >= 0 and limit 1..200 are required.");
            var docs = (JsonArray)Data.Value["documents"]!;
            var result = new JsonObject { ["sources"] = Data.Value["sources"]!.DeepClone(), ["referenceOnly"] = true,
                ["scope"] = Data.Value["scope"]!.DeepClone(), ["documentCount"] = docs.Count, ["offset"] = offset };
            if (documentId.Length > 0)
            {
                var doc = docs.FirstOrDefault(d => (string?)d!["id"] == documentId)
                    ?? throw new ArgumentException("Unknown documentId. Search GetToolUsage(query) for exact IDs.");
                var lines = ((string)doc["text"]!).Split('\n');
                result["document"] = doc.DeepClone();
                ((JsonObject)result["document"]!).Remove("text");
                result["lines"] = new JsonArray(lines.Skip(offset).Take(limit).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                result["totalLines"] = lines.Length;
                result["nextOffset"] = offset + limit < lines.Length ? JsonValue.Create(offset + limit) : null;
                return result;
            }
            var words = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var matches = docs.Where(d => words.All(w => ((string)d!["id"]! + " " + (string)d["text"]!).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            result["matches"] = new JsonArray(matches.Skip(offset).Take(limit).Select(d => { var row = (JsonObject)d!.DeepClone(); row.Remove("text"); row["exampleCount"] = ((JsonArray)row["examples"]!).Count; row.Remove("examples"); return (JsonNode)row; }).ToArray());
            result["totalMatches"] = matches.Count;
            result["nextOffset"] = offset + limit < matches.Count ? JsonValue.Create(offset + limit) : null;
            return result;
        }

        public static JsonObject Describe(string name, string release, string profile, string description, JsonObject schema,
            string? curatedArguments = null, string? curatedNote = null)
        {
            var mapping = Mapping(name);
            if (mapping["manualReferences"] is JsonArray manuals)
                mapping["manualReferences"] = new JsonArray(manuals.Where(m => (string?)m!["releaseKey"] == release).Select(m => m!.DeepClone()).ToArray());
            var properties = (JsonObject)schema["properties"]!;
            var args = curatedArguments == null ? new JsonObject() : (JsonObject)JsonNode.Parse(curatedArguments)!;
            var replacements = new JsonArray();
            foreach (var required in schema["required"] as JsonArray ?? new JsonArray())
            {
                var key = (string)required!;
                if (args.ContainsKey(key)) continue;
                args[key] = ExampleValue(key, (JsonObject)properties[key]!);
                replacements.Add(key);
            }
            // Show the initial operation explicitly, even when optional in the signature.
            foreach (var key in new[] { "action", "operation", "dryRun", "confirm" })
                if (properties[key] is JsonObject prop && !args.ContainsKey(key)) args[key] = ExampleValue(key, prop);
            if (properties.ContainsKey("dryRun")) args["dryRun"] = true;
            if (properties.ContainsKey("confirm")) args["confirm"] = false;
            return new JsonObject {
                ["toolName"] = name, ["releaseKey"] = release, ["profile"] = profile, ["available"] = true,
                ["description"] = description, ["inputSchema"] = schema.DeepClone(),
                ["example"] = new JsonObject { ["origin"] = "project MCP mapping, not Siemens MCP code",
                    ["kind"] = curatedArguments == null ? "schema-template" : "curated-project-example",
                    ["request"] = new JsonObject { ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } },
                    ["replaceOrReview"] = replacements,
                    ["note"] = curatedNote ?? "Template from this engine's actual schema. Resolve every placeholder and read parameter descriptions/defaults. JSON strings must stay strings. Schema-valid does not mean semantically valid for a project." },
                ["officialReference"] = mapping,
                ["versionRule"] = "Only this engine's listed contract is available. Official source samples have their own release and dependencies; do not copy a newer API into an older engine. Same tool name can have different foundation/full arguments.",
                ["workflow"] = new JsonArray("Read this contract and the linked official document including setup and caveats.",
                    "For native tools, obtain current process/project/device identities from discovery; for offline tools, supply the requested local files or content. Never guess paths, indexes or version support.",
                    profile == "plc-foundation" ? "Honor preview/confirm/expectedProjectFile and plan hashes exactly as described in inputSchema and description." : "Use PreflightToolCall with the resolved arguments; it checks binding/session evidence, not native safety.",
                    "Execute only the requested operation. Check its returned result before the next step; compile/save/download are separate unless explicitly described."),
                ["onFailure"] = "Do not try parameter variants or replay after TIA/channel loss. Preserve the failing arguments and last native log, inspect current state, then correct the cause. A dryRun may still read native objects; it is not a native-crash shield.",
                ["nativeAcceptance"] = "NOT RUN by this guidance feature; official samples and schema checks are not native acceptance. Consult the tool's specific evidence."
            };
        }

        private static JsonNode? ExampleValue(string name, JsonObject property)
        {
            if (name == "dryRun") return JsonValue.Create(true);
            if (name == "confirm") return JsonValue.Create(false);
            if (property.ContainsKey("default")) return property["default"]?.DeepClone();
            if (property["enum"] is JsonArray choices && choices.Count > 0) return choices[0]?.DeepClone();
            var type = property["type"] is JsonArray types ? (string?)types.FirstOrDefault(t => (string?)t != "null") : (string?)property["type"];
            switch (type)
            {
                case "boolean": return JsonValue.Create(false);
                case "integer": return property["minimum"]?.DeepClone() ?? JsonValue.Create(1);
                case "number": return property["minimum"]?.DeepClone() ?? JsonValue.Create(1.0);
                case "array": return new JsonArray();
                case "object": return new JsonObject();
                default: return JsonValue.Create("<" + name + ": resolve from the parameter description/current project>");
            }
        }
    }
}
