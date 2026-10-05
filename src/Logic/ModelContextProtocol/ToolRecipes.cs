using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Multi-step sequences. Real-machine trials exposed call-order dependencies: protection before hardware compile
    // before download, PLC side before HMI side, and dryRun before execution. Historical native runs and the acceptance
    // status of reference-only recipes are recorded in docs/reference/real-machine-ledger.md and the campaign plans.
    // Values are curated-example placeholders. Build-time validation checks that each step names a real tool and fits
    // its signature. Zero dependencies; linked into the offline suite.
    public static class ToolRecipes
    {
        public sealed class Step
        {
            public string Tool { get; }
            public string ArgumentsJson { get; }
            public string Expect { get; }
            public Step(string tool, string argumentsJson, string expect) { Tool = tool; ArgumentsJson = argumentsJson; Expect = expect; }
        }

        public sealed class Recipe
        {
            public string Topic { get; }
            public string Purpose { get; }
            public string Preconditions { get; }
            public string Notes { get; }
            public IReadOnlyList<Step> Steps { get; }
            public Recipe(string topic, string purpose, string preconditions, string notes, params Step[] steps)
            { Topic = topic; Purpose = purpose; Preconditions = preconditions; Notes = notes; Steps = steps; }
        }

        private static readonly Recipe[] Rows = TiaOpenness.Shared.ToolUsageCatalog.Sequences()
            .Where(r => (string?)r?["profile"] == "full-engine")
            .Select(r => new Recipe((string)r!["topic"]!, (string)r["purpose"]!,
                (string)r["preconditions"]!, (string)r["notes"]!,
                r["steps"]!.AsArray().Select(s => new Step((string)s!["tool"]!,
                    s["arguments"]!.ToJsonString(), (string)s["expect"]!)).ToArray())).ToArray();

        private static readonly Dictionary<string, Recipe> Index = Rows.ToDictionary(r => r.Topic, r => r, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<Recipe> All => Rows;

        public static Recipe? Find(string? topic) => topic != null && Index.TryGetValue(topic.Trim(), out var r) ? r : null;

        public static JsonObject Selection(string? topic)
        {
            if (string.IsNullOrWhiteSpace(topic)) return new JsonObject { ["exampleKind"] = "sequence" };
            var recipe = Find(topic);
            if (recipe == null) throw new ArgumentException("Unknown recipe topic.");
            return new JsonObject { ["exampleId"] = "sequence/" + recipe.Topic, ["exampleKind"] = "sequence" };
        }

        public static JsonArray ForRelease(string release, IEnumerable<string> roster, string? topic = null)
        {
            var selection = Selection(topic);
            return TiaOpenness.Shared.ToolUsageCatalog.Examples(release, "full-engine", roster,
                exampleId: (string?)selection["exampleId"] ?? "", exampleKind: "sequence")["examples"]!.AsArray();
        }

        /// <summary>Every step must parse, name a real tool and use exact parameter names; required parameters must be present.</summary>
        public static IReadOnlyList<string> ValidateAgainst(Func<string, IReadOnlyList<KeyValuePair<string, bool>>?> parametersOf)
        {
            var problems = new List<string>();
            foreach (var r in Rows)
            {
                int n = 0;
                foreach (var step in r.Steps)
                {
                    n++;
                    string where = r.Topic + " step " + n + " (" + step.Tool + ")";
                    JsonObject? args;
                    try { args = JsonNode.Parse(step.ArgumentsJson) as JsonObject; }
                    catch (JsonException jx) { problems.Add(where + ": not valid JSON (" + jx.Message + ")"); continue; }
                    if (args == null) { problems.Add(where + ": arguments must be a JSON object"); continue; }
                    var specs = parametersOf(step.Tool);
                    if (specs == null) { problems.Add(where + ": no tool of that name"); continue; }
                    foreach (var key in args.Select(kv => kv.Key))
                        if (!specs.Any(sp => string.Equals(sp.Key, key, StringComparison.Ordinal)))
                            problems.Add(where + ": '" + key + "' is not a parameter");
                    foreach (var sp in specs)
                        if (sp.Value && args[sp.Key] == null)
                            problems.Add(where + ": required parameter '" + sp.Key + "' missing");
                }
            }
            return problems;
        }
    }
}
