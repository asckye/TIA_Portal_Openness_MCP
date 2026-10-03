using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.58: verified multi-step sequences. Trial-and-error on the real machine mostly came from the ORDER of calls
    // (protection before hardware compile before download, PLC side before HMI side, dryRun before the real run), not
    // from single calls. Historical sequences were run on the maintainer's VM; newer reference-only recipes state their native acceptance status (docs/reference/real-machine-ledger.md and
    // the campaign plans); values are the placeholders of the curated examples. Validated at build time like the
    // examples (each step names a real tool and fits its signature). Zero dependencies; linked into the offline suite.
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
