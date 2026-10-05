using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.Json.Nodes;

namespace TiaOpenness.Shared
{
    // Reference content only. No Siemens types, filesystem targets or native callbacks.
    public static class ToolUsageCatalog
    {
        public const string Instructions = "GetToolUsage(toolName, operation) provides this release's call examples, input origins and result interpretation. GetToolUsage(language) lists programming examples; exampleId reads a complete example or call sequence. GetToolUsage(query/documentId) searches/reads the embedded Siemens references. Examples identify placeholders, version requirements and validation evidence.";
        private static readonly Lazy<JsonObject> Data = new Lazy<JsonObject>(() => {
            using (var stream = typeof(ToolUsageCatalog).Assembly.GetManifestResourceStream("TiaMcp.ToolUsage.json"))
            using (var reader = new StreamReader(stream!))
                return (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
        });
        private static readonly Lazy<JsonObject> Profiles = new Lazy<JsonObject>(() => {
            var resources = new ResourceManager("TiaMcp.Logic.ModelContextProtocol.ToolProfiles", typeof(ToolUsageCatalog).Assembly);
            return JsonNode.Parse(resources.GetString("Catalog", System.Globalization.CultureInfo.InvariantCulture)!)!.AsObject();
        });

        public static JsonArray ProfileEntries(string release, int contractVersion = 4)
        {
            if (contractVersion != 4) throw new ArgumentException("Unsupported contract version.");
            return (JsonArray)(Profiles.Value["releases"]?[release]?.DeepClone() ?? new JsonArray());
        }

        private static JsonObject? ProfileEntry(string name, string release) => (Profiles.Value["releases"]?[release] as JsonArray)?
            .OfType<JsonObject>().FirstOrDefault(row => (string?)row["currentName"] == name || (string?)row["sourceName"] == name);

        public static string RegisteredName(string sourceName, string release)
            => (string?)ProfileEntry(sourceName, release)?["currentName"] ?? sourceName;

        public static JsonObject GuideSelection(string topic)
        {
            switch (topic.Trim().ToLowerInvariant())
            {
                case "workflow": return new JsonObject { ["exampleId"] = "sequence/connect-project" };
                case "openness-workflow": return new JsonObject { ["query"] = "openness-base" };
                case "startdrive-bico": return new JsonObject { ["toolName"] = "ManageStartdriveParameter", ["operation"] = "read" };
                case "hmi": return new JsonObject { ["language"] = "hmi-javascript" };
                case "errors": return new JsonObject();
                default: return new JsonObject { ["language"] = topic };
            }
        }
        public static string Hint(string name) => " Examples: GetToolUsage(toolName: \"" + name + "\"), optionally operation or language.";
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

        public static JsonArray Sequences() => (JsonArray)Data.Value["sequences"]!.DeepClone();
        public static JsonObject Notes(string name) => (JsonObject)(Data.Value["toolNotes"]?[name]?.DeepClone() ?? new JsonObject());

        private static JsonObject? CallExample(string name, string release, string profile)
        {
            var candidate = TiaMcp.Logic.V4.BehaviorCapabilities.CandidateExample(release, RegisteredName(name, release));
            if (candidate != null) return new JsonObject { ["arguments"] = candidate, ["parameters"] = new JsonObject(),
                ["note"] = "Resolve the exact installed catalog TypeIdentifier, preview, then apply that plan once with confirmation and the expected project file." };
            var entry = profile == "full-engine" ? ProfileEntry(name, release) : null;
            var source = (string?)entry?["currentName"] ?? name;
            var row = Data.Value["calls"]?["profiles"]?[profile]?[source]?.DeepClone();
            if (row == null) return null;
            if (entry != null) row["arguments"] = entry["arguments"]!.DeepClone();
            var extension = (string?)Data.Value["calls"]?["releaseExtensions"]?[release] ?? release;
            // Only our explicit format tokens are replaced; braces in JSON/code are preserved.
            return JsonNode.Parse(row.ToJsonString().Replace("{extension}", extension).Replace("{release}", release)
                .Replace("{major}", release == "14sp1" ? "14" : release == "15.1" ? "15" : release))!.AsObject();
        }

        public static JsonArray InlineCalls(string release)
        {
            var result = new JsonArray();
            foreach (var pair in Data.Value["calls"]!["profiles"]!["full-engine"]!.AsObject())
                if ((bool?)pair.Value?["inline"] == true)
                {
                    var row = CallExample(pair.Key, release, "full-engine")!;
                    result.Add(new JsonObject { ["tool"] = RegisteredName(pair.Key, release), ["arguments"] = row["arguments"]!.DeepClone(),
                        ["note"] = "Sample targets require binding; GetToolUsage explains this release's parameters and results." });
                }
            return result;
        }

        public static JsonObject Examples(string release, string profile, IEnumerable<string> roster,
            string language = "", string exampleId = "", string toolName = "", string exampleKind = "all")
        {
            if (exampleKind != "all" && exampleKind != "sequence" && exampleKind != "language")
                throw new ArgumentException("exampleKind must be all/sequence/language.");
            var available = new HashSet<string>(roster, StringComparer.OrdinalIgnoreCase);
            var languages = (JsonArray)Data.Value["languages"]!;
            if (language.Length > 0)
            {
                var selected = languages.FirstOrDefault(l => string.Equals((string?)l!["id"], language, StringComparison.OrdinalIgnoreCase)
                    || l["aliases"]!.AsArray().Any(a => string.Equals((string?)a, language, StringComparison.OrdinalIgnoreCase)));
                if (selected == null) throw new ArgumentException("Unknown language. Read empty GetToolUsage for the language list.");
                language = (string)selected["id"]!;
            }
            // Explicit LINQ avoids the full engine's imported params Concat overload,
            // which appends a JsonArray as one JsonNode instead of joining its rows.
            var records = Enumerable.Concat((JsonArray)Data.Value["examples"]!, (IEnumerable<JsonNode?>)(JsonArray)Data.Value["sequences"]!).Where(e =>
                (language.Length == 0 || (string?)e!["language"] == language) &&
                (exampleId.Length == 0 || (string?)e!["id"] == exampleId) &&
                (toolName.Length == 0 || e!["tools"] is JsonArray ts && ts.Any(t => (string?)t == toolName)
                    || e!["steps"] is JsonArray steps && steps.Any(s => (string?)s!["tool"] == toolName))).ToList();
            if (exampleId.Length > 0 && records.Count == 0) throw new ArgumentException("Unknown exampleId for these selectors.");
            var items = new JsonArray();
            foreach (var record in records)
            {
                var row = (JsonObject)record!.DeepClone();
                bool sequenceRecord = row["steps"] is JsonArray;
                if (exampleKind == "sequence" && !sequenceRecord || exampleKind == "language" && sequenceRecord) continue;
                bool releaseMatches = row["releaseKeys"]!.AsArray().Any(v => (string?)v == release);
                bool profileMatches = row["profile"] == null || (string?)row["profile"] == profile;
                if (exampleKind != "all" && (!releaseMatches || !profileMatches)) continue;
                row["releaseMatches"] = releaseMatches;
                row["profileMatches"] = profileMatches;
                if (row["tools"] is JsonArray names)
                    row["availableTools"] = new JsonArray(names.Where(n => available.Contains((string)n!)).Select(n => n!.DeepClone()).ToArray());
                if (row["steps"] is JsonArray sequence)
                    row["available"] = releaseMatches && profileMatches && sequence.All(s => available.Contains((string)s!["tool"]!));
                if (exampleId.Length == 0)
                {
                    row.Remove("files"); row.Remove("steps"); row.Remove("corrections");
                }
                items.Add(row);
            }
            var languageList = (JsonArray)languages.DeepClone();
            foreach (var row in languageList)
                row!["tools"] = new JsonArray(row["tools"]!.AsArray().Where(t => available.Contains((string)t!)).Select(t => t!.DeepClone()).ToArray());
            var result = new JsonObject { ["releaseKey"] = release, ["profile"] = profile, ["language"] = language,
                ["languages"] = languageList, ["examples"] = items,
                ["retrieval"] = "Use exampleId to read files/content or complete steps. releaseMatches is source scope, not native verification; availableTools are filtered to this engine." };
            if (language == "csharp")
                result["sourceDocuments"] = new JsonArray(Data.Value["documents"]!.AsArray().Where(d => ((string)d!["id"]!).EndsWith(".cs", StringComparison.Ordinal))
                    .Select(d => (JsonNode)new JsonObject { ["documentId"] = d!["id"]!.DeepClone(), ["url"] = d["url"]!.DeepClone(), ["examples"] = d["examples"]!.DeepClone() }).ToArray());
            return result;
        }

        public static JsonObject Describe(string name, string release, string profile, string description, JsonObject schema,
            string? curatedArguments = null, string? curatedNote = null, string operation = "",
            IEnumerable<string>? roster = null, JsonObject? resultContract = null, Func<JsonObject, string>? callProblem = null)
        {
            var sourceName = profile == "full-engine" ? (string?)ProfileEntry(name, release)?["currentName"] ?? name : name;
            var mapping = Mapping(sourceName);
            if (mapping["manualReferences"] is JsonArray manuals)
                mapping["manualReferences"] = new JsonArray(manuals.Where(m => (string?)m!["releaseKey"] == release).Select(m => m!.DeepClone()).ToArray());
            var properties = (JsonObject)schema["properties"]!;
            var releaseKeys = Data.Value["toolNotes"]?[name]?["releaseKeys"] as JsonArray;
            string releaseProblem = releaseKeys != null && !releaseKeys.Any(r => (string?)r == release)
                ? "This registered wrapper has no native implementation for release " + release + "; see interpretation and the tool description." : "";
            string selector = properties.ContainsKey("action") ? "action" : properties.ContainsKey("operation") ? "operation" : "";
            var choices = selector.Length == 0 ? new JsonArray() : properties[selector]!["enum"] as JsonArray ?? new JsonArray();
            choices = new JsonArray(choices.Where(c => !string.IsNullOrEmpty((string?)c)).Select(c => c!.DeepClone()).ToArray());
            if (operation.Length > 0 && !choices.Any(c => string.Equals((string?)c, operation, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Unknown operation for this tool/release. Read its operations list.");
            if (operation.Length > 0) operation = (string)choices.First(c => string.Equals((string?)c, operation, StringComparison.OrdinalIgnoreCase))!;
            var curated = curatedArguments == null ? null : (JsonObject)JsonNode.Parse(curatedArguments)!;
            bool matches = curated != null && (operation.Length == 0 || (string?)(curated[selector] ?? properties[selector]?["default"]) == operation);
            var call = CallExample(name, release, profile);
            JsonNode? callArguments = call?["arguments"]?.DeepClone();
            if (operation.Length > 0)
            {
                if (call?["operations"]?[operation]?["arguments"] is JsonObject overrides)
                    foreach (var pair in overrides) callArguments![pair.Key] = pair.Value?.DeepClone();
                else callArguments = null;
            }
            var args = callArguments is JsonObject concrete ? (JsonObject)concrete.DeepClone()
                : matches ? (JsonObject)curated!.DeepClone() : new JsonObject();
            foreach (var key in args.Select(p => p.Key).Where(k => !properties.ContainsKey(k)).ToArray()) args.Remove(key);
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
            if (operation.Length > 0) args[selector] = operation;
            if (properties.ContainsKey("dryRun")) args["dryRun"] = true;
            if (properties.ContainsKey("confirm")) args["confirm"] = false;
            var origins = new JsonObject();
            var available = new HashSet<string>(roster ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
            {
                var source = Data.Value["parameterSources"]?[property.Key] as JsonObject;
                origins[property.Key] = call?["parameters"]?[property.Key]?.DeepClone() ?? source?.DeepClone()
                    ?? new JsonObject { ["meaning"] = property.Value?["description"]?.DeepClone() ?? JsonValue.Create("Supply the value required by this parameter's schema.") };
                origins[property.Key]!["exampleValue"] = args[property.Key]?.DeepClone()
                    ?? origins[property.Key]!["exampleValue"]?.DeepClone() ?? ExampleValue(property.Key, property.Value!.AsObject());
                origins[property.Key]!["schemaDescription"] = property.Value?["description"]?.DeepClone();
                if (origins[property.Key]!["meaning"] == null)
                    origins[property.Key]!["meaning"] = property.Value?["description"]?.DeepClone() ?? source?["meaning"]?.DeepClone();
                if (origins[property.Key]!["tools"] == null && source?["tools"] != null) origins[property.Key]!["tools"] = source["tools"]!.DeepClone();
                if (property.Key.EndsWith("Json", StringComparison.Ordinal) || property.Key == "json" || property.Key == "spec")
                    origins[property.Key]!["encoding"] = "Serialize the inner value once as a JSON string unless inputSchema accepts an object/array.";
                if (origins[property.Key]!["tools"] is JsonArray originTools)
                    origins[property.Key]!["tools"] = new JsonArray(originTools.Where(t => available.Contains((string)t!)).Select(t => t!.DeepClone()).ToArray());
            }
            var operations = new JsonArray();
            foreach (var choice in choices)
            {
                var candidate = (JsonObject)args.DeepClone(); candidate[selector] = choice!.DeepClone();
                var variants = new JsonArray();
                string discriminator = name == "ManageDeviceServiceObjects" ? "family" : name == "ManagePlcDocuments" ? "objectKind" : name == "ManageSivarcBlockDefinition" ? "kind" : "";
                var contexts = discriminator.Length > 0 ? properties[discriminator]?["enum"] as JsonArray : null;
                bool chosenContext = false;
                foreach (var context in contexts ?? new JsonArray(new JsonNode?[] { null }))
                {
                    var variant = (JsonObject)candidate.DeepClone();
                    if (context != null) variant[discriminator] = context.DeepClone();
                    string reason = releaseProblem.Length > 0 ? releaseProblem : callProblem?.Invoke(variant) ?? "";
                    variants.Add(new JsonObject { ["context"] = context == null ? null : new JsonObject { [discriminator] = context.DeepClone() },
                        ["availableInRelease"] = reason.Length == 0, ["reason"] = reason.Length == 0 ? null : reason });
                    if (operation == (string?)choice && reason.Length == 0 && context != null && !chosenContext)
                    { args[discriminator] = context.DeepClone(); chosenContext = true; }
                }
                operations.Add(new JsonObject { ["operation"] = choice.DeepClone(), ["selector"] = selector,
                    ["variants"] = variants, ["exampleQuery"] = new JsonObject { ["toolName"] = name, ["operation"] = choice.DeepClone() } });
            }
            string selectedProblem = releaseProblem.Length > 0 ? releaseProblem : callProblem?.Invoke(args) ?? "";
            var response = new JsonObject {
                ["toolName"] = name, ["releaseKey"] = release, ["profile"] = profile, ["available"] = true,
                ["description"] = description, ["inputSchema"] = schema.DeepClone(),
                ["example"] = new JsonObject { ["origin"] = "project MCP mapping, not Siemens MCP code",
                    ["kind"] = callArguments != null ? "parameterized-call-example" : matches ? "curated-project-example" : "schema-template",
                    ["request"] = new JsonObject { ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } },
                    ["replaceOrReview"] = replacements,
                    ["note"] = call != null ? (string?)call["note"] ?? "Example names and paths describe a sample project. Resolve every target against this session; angle-bracket values are bindings, including those inside JSON strings. Operation-specific optional inputs are included; parameterSources also shows individual alternatives and their encoding. Native availability still depends on this device and installed options."
                        : matches ? curatedNote : "Required arguments and the selected operation are shown. Add the optional parameters required by this operation using inputSchema and parameterSources. Placeholders are not engineering target values.",
                    ["validation"] = call?["validation"]?.DeepClone() ?? JsonValue.Create("Schema/contract checked; native execution of this example NOT RUN."),
                    ["releaseProblem"] = selectedProblem.Length == 0 ? null : selectedProblem },
                ["officialReference"] = mapping,
                ["operations"] = operations, ["parameterSources"] = origins,
                ["resultContract"] = resultContract?.DeepClone() ?? call?["resultContract"]?.DeepClone() ?? new JsonObject { ["description"] = "Result structure and operation-specific fields are described in the exact tool description above." },
                ["resultReading"] = call?["resultReading"]?.DeepClone() ?? (Data.Value["resultReading"]?[(string?)resultContract?["type"] ?? "default"] ?? Data.Value["resultReading"]!["default"])!.DeepClone(),
                ["examples"] = Examples(release, profile, available, toolName: name)["examples"]!.DeepClone()
            };
            if (call?["execution"] != null) response["example"]!["execution"] = call["execution"]!.DeepClone();
            if (call?["executionTest"] != null) response["example"]!["executionTest"] = call["executionTest"]!.DeepClone();
            response["example"]!["bindings"] = new JsonArray(args.Where(p => p.Value?.ToJsonString().Contains("<") == true || p.Value?.ToJsonString().Contains("\\u003C") == true)
                .Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
            response["example"]!["bindingMeaning"] = "Resolve angle-bracket bindings (also inside JSON) from this operation's target read/self-description before calling. Sample names and paths must also be replaced with the intended target.";
            if (call?["releaseNotes"]?[release] != null) response["example"]!["releaseNote"] = call["releaseNotes"]![release]!.DeepClone();
            if (Data.Value["toolNotes"]?[name] is JsonNode notes && (notes["profile"] == null || (string?)notes["profile"] == profile)) response["interpretation"] = notes.DeepClone();
            return response;
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
