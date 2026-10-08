using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // One worked call per frequently used tool, placed next to its signature so callers can correct a refused call
    // without guessing parameters against TIA. The table is appended to protocol descriptions (McpServer.Profile.cs)
    // and used by FindTools / PreviewToolCall / CallTool refusals. The tools-list build gate checks every example
    // against the real signatures (the HttpTests generate-tools-list mode calls ValidateAgainst) to prevent drift.
    // Zero dependencies: linked into the offline suite.
    public static class ToolExamples
    {
        public sealed class Example
        {
            public string Tool { get; }
            public string ArgumentsJson { get; }
            public string Note { get; }
            public Example(string tool, string argumentsJson, string note) { Tool = tool; ArgumentsJson = argumentsJson; Note = note; }
        }

        // Inline discovery and GetToolUsage read the same version-aware data.
#if TIA_V20
        private const string ExampleRelease = "20";
#else
        private const string ExampleRelease = "21";
#endif
        private static readonly Example[] Rows = TiaOpenness.Shared.ToolUsageCatalog.InlineCalls(ExampleRelease, engineSource: true)
            .Select(r => new Example((string)r!["tool"]!, r["arguments"]!.ToJsonString(), (string)r["note"]!)).ToArray();

        private static readonly Dictionary<string, Example> Index = Rows.ToDictionary(r => r.Tool, r => r, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<Example> All => Rows;

        public static Example? Find(string? tool)
            => tool != null && Index.TryGetValue(tool.Trim(), out var e) ? e : null;

        /// <summary>The sentence appended to a tool description: "Example: {...} (note)."</summary>
        public static string Render(Example e)
            => "Example: " + e.ArgumentsJson + (e.Note.Length > 0 ? " (" + e.Note + ")" : "") + ".";

        // Placeholders such as <Folder/Name> must read as written, not as < escapes.
        private static readonly JsonSerializerOptions PlainJson = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public const string DerivedNote = "derived from the signature - placeholder values, read the parameter descriptions";

        /// <summary>The curated example, or one derived from the signature so that every tool has a worked call.</summary>
        public static Example FindOrDerive(string tool, IReadOnlyList<PreflightLogic.ParameterSpec> specs)
            => Find(tool) ?? Derive(tool, specs);

        /// <summary>
        /// A placeholder example for the tools without a curated one - the required parameters with values taken
        /// from explicit example metadata, the declared alternatives (first one) or the parameter's name.
        /// Never wrong on the shape (keys come from the signature), possibly wrong on the value - the note says so.
        /// </summary>
        public static Example Derive(string tool, IReadOnlyList<PreflightLogic.ParameterSpec> specs)
        {
            var args = new JsonObject();
            foreach (var spec in specs)
            {
                if (!spec.Required) continue;
                args[spec.Name] = PlaceholderValue(tool, spec);
            }
            return new Example(tool, args.ToJsonString(PlainJson), DerivedNote);
        }

        internal static JsonNode PlaceholderValue(string tool, PreflightLogic.ParameterSpec spec)
        {
            var name = spec.Name;
            switch (spec.Kind)
            {
                case "boolean": return JsonValue.Create(spec.DefaultText == null ? true : string.Equals(spec.DefaultText, "true", StringComparison.OrdinalIgnoreCase));
                case "integer": return JsonValue.Create(long.TryParse(spec.DefaultText ?? "", out var l) ? l : 1);
                case "number": return JsonValue.Create(double.TryParse(spec.DefaultText ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 1.0);
            }
            if (spec.Kind != "string") return JsonValue.Create("<" + name + ">");
            if (spec.ExampleJson != null) return JsonNode.Parse(spec.ExampleJson)!;
            var hint = ToolMetadata.Parameter(tool, name);
            if (hint != null) return JsonNode.Parse(hint.ExampleJson)!;
            if (spec.AllowedValues.Count > 0) return JsonValue.Create(spec.AllowedValues[0]);
            return JsonValue.Create("<" + name + ">");
        }

        /// <summary>Description plus the example sentence when one exists; unchanged otherwise.</summary>
        public static string Decorate(string tool, string description)
        {
            var e = Find(tool);
            if (e == null) return description;
            var d = description ?? "";
            return (d.Length == 0 || d.EndsWith(" ") ? d : d + " ") + Render(e);
        }

        /// <summary>
        /// Every example must parse and must fit its tool: exact parameter spelling, every required parameter present.
        /// The caller supplies the real signatures (reflection over the built engine in the tools-list gate; the linked
        /// tools in the offline suite). Unknown tools are reported too - an example for a renamed tool is dead weight.
        /// </summary>
        public static IReadOnlyList<string> ValidateAgainst(Func<string, IReadOnlyList<KeyValuePair<string, bool>>?> parametersOf)
        {
            var problems = new List<string>();
            foreach (var e in Rows)
            {
                JsonObject? args;
                try { args = JsonNode.Parse(e.ArgumentsJson) as JsonObject; }
                catch (JsonException jx) { problems.Add(e.Tool + ": example is not valid JSON (" + jx.Message + ")"); continue; }
                if (args == null) { problems.Add(e.Tool + ": example must be a JSON object"); continue; }
                var specs = parametersOf(e.Tool);
                if (specs == null) { problems.Add(e.Tool + ": no tool of that name (example is stale)"); continue; }
                foreach (var key in args.Select(kv => kv.Key))
                    if (!specs.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal)))
                        problems.Add(e.Tool + ": example uses '" + key + "' which is not a parameter (exact spelling required)");
                foreach (var s in specs)
                    if (s.Value && args[s.Key] == null)
                        problems.Add(e.Tool + ": example lacks the required parameter '" + s.Key + "'");
            }
            return problems;
        }
    }
}
