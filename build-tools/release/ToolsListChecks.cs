using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

internal static class ToolsListChecks
{
    internal static Func<string, string> Classifier(string source, string metadata = "")
    {
        var sessions = Set(Matches(Regex.Match(source, @"SessionNames = \{([^}]+)\}").Groups[1].Value, "\"([^\"]+)\""));
        var body = source.Split("if (SessionNames.Contains(name))", 2)[1]; var rules = new List<(List<(string Kind, string[] Parts)> Alternatives, string Operation)>();
        var matches = Regex.Matches(body, "if \\((.*?)\\) return \\(\"([A-Z-]+)\", true\\);");
        foreach (Match m in matches)
        {
            var alternatives = new List<(string, string[])>();
            foreach (var term in m.Groups[1].Value.Split(" || ", StringSplitOptions.None)) { var match = Regex.Match(term, "^(Starts|Has)\\((\"[^\"]+\"(?:, \"[^\"]+\")*)\\)$"); Require(match.Success, "Unsupported ToolTaxonomy rule: " + term); alternatives.Add((match.Groups[1].Value, Matches(match.Groups[2].Value, "\"([^\"]+)\""))); }
            rules.Add((alternatives, m.Groups[2].Value));
        }
        var remainder = Regex.Replace(body, "if \\((.*?)\\) return \\(\"([A-Z-]+)\", true\\);", ""); remainder = Regex.Replace(remainder, @"//[^\n]*", "");
        Require(Regex.IsMatch(remainder, "^\\s*return \\(\"SESSION\", true\\);\\s*return \\(\"UNSPECIFIED\", true\\);\\s*}\\s*}\\s*}\\s*$"), "Unparsed ToolTaxonomy logic");
        var declared = Regex.Matches(metadata, "\\[\"([^\"]+)\"\\] = new Classification\\(\"[^\"]+\", \"[^\"]+\", \"([A-Z-]+)\",").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        return name => declared.TryGetValue(name, out var op) ? op : sessions.Contains(name) ? "SESSION" : rules.FirstOrDefault(r => r.Alternatives.Any(a => a.Parts.Any(p => a.Kind == "Starts" ? name.StartsWith(p, StringComparison.Ordinal) : name.Contains(p, StringComparison.Ordinal)))).Operation ?? "UNSPECIFIED";
    }
    internal static void CompareNames(IEnumerable<string> rows, HashSet<string> expected, string label) { var names = rows.ToArray(); Require(names.Distinct().Count() == names.Length && Set(names).SetEquals(expected), label + ": duplicate or stale names"); }
    internal static void CompareOperation(string old, string actual, JsonObject baseline, JsonNode review, JsonObject additions)
    {
        if (additions.ContainsKey(old)) { Require(!baseline.ContainsKey(old) && actual == Text(additions[old]!["operation"]), "New operation differs: " + old); return; }
        var expected = Text(baseline[old] ?? throw new ReleaseException("Unknown frozen operation: " + old));
        if (review["EXCEPTIONS"]?[old] is JsonArray exception) { Require(Text(exception[0]) == expected && Text(exception[1]) != expected && Text(exception[2]).Trim().Length > 0, "Unreviewed operation exception: " + old); expected = Text(exception[1]); }
        Require(actual == expected, "Operation changed: " + old + " -> " + actual + ", expected " + expected);
    }
    internal static void CompareFoundation(string current, string source, string actual, JsonObject baseline, JsonNode review)
    {
        var entry = review["NEW_FOUNDATION_TOOLS"]![current] ?? throw new ReleaseException("Unknown Foundation addition: " + current);
        Require(source == Text(entry[0]) && Text(entry[2]).Trim().Length > 0 && !baseline.ContainsKey(current) && !baseline.ContainsKey(source) && actual == Text(entry[1]), "Unreviewed Foundation mapping/operation: " + current);
    }
    internal static int Run(string root, string manifestPath)
    {
        var data = new CatalogData(root); var manifest = Read(root, manifestPath); var expected = Set(data.Rosters["20"].Concat(data.Rosters["21"]));
        CompareNames(manifest["tools"]!.AsArray().Select(t => Text(t!["name"])), expected, "V20/V21 union"); Require(manifest["toolCount"]!.GetValue<int>() == expected.Count, "toolCount differs from roster");
        var listed = manifest["tools"]!.AsArray().ToDictionary(t => Text(t!["name"]), t => t!);
        var runtime = JsonNode.Parse(XDocument.Load(Path.Combine(root, CatalogGenerator.Resource)).Root!.Elements("data").Single(d => (string?)d.Attribute("name") == "Catalog").Element("value")!.Value)!;
        var baseline = Read(root, "scripts/checks/write-guard-operations-v3.3.0.json")["operations"]!.AsObject(); var review = Read(root, "reference/tool-catalog/operation-review.json"); var withdrawn = review["WITHDRAWN"]!.AsObject(); var newFoundation = review["NEW_FOUNDATION_TOOLS"]!.AsObject();
        Require(Set(baseline.Select(p => p.Key).Concat(data.Additions.Select(p => p.Key).Except(data.HostOnly.Keys))).SetEquals(data.Mapping.Keys), "Frozen operations plus reviewed additions differ");
        Require(review["EXCEPTIONS"]!.AsObject().All(p => baseline.ContainsKey(p.Key)), "Unused operation exception");
        var metadata = Source(root, "src/Logic/ModelContextProtocol/ToolMetadata.cs"); var declared = Matches(metadata, "\\[\"([^\"]+)\"\\] = new Classification\\("); CompareNames(declared, Set(expected.Concat(withdrawn.Select(p => p.Key))), "Explicit classification coverage");
        var classify = Classifier(Source(root, "src/Logic/ModelContextProtocol/ToolTaxonomy.cs"), metadata);
        var descriptions = Set(data.Registrations.Keys.Except(data.HostOnly.Keys)); Require(descriptions.SetEquals(expected.Except(newFoundation.Select(p => p.Key)).Except(data.HostOnly.Keys).Concat(withdrawn.Select(p => p.Key))), "Engine descriptions differ from reviewed product roster");
        var seen = new HashSet<string>(); var seenNew = new HashSet<string>();
        foreach (var key in new[] { "20", "21" })
        {
            var rows = runtime["releases"]![key]!.AsArray(); CompareNames(rows.Select(r => Text(r!["currentName"])), data.Rosters[key], "Runtime V" + key);
            foreach (var row in rows)
            {
                var old = Text(row!["sourceName"]); var current = Text(row["currentName"]); var final = Text(row["name"]); var operation = classify(current);
                Require(Text(listed[current]["operation"]) == operation, "Stale manifest operation: " + current);
                if (data.HostOnly.TryGetValue(current, out var hostOperation)) { Require(old == current && current == final && operation == hostOperation, "Host-only P8 operation changed"); continue; }
                if (newFoundation.ContainsKey(current)) { CompareFoundation(current, old, operation, baseline, review); CompareFoundation(current, old, classify(final), baseline, review); seenNew.Add(current); continue; }
                if (review["REBASED_SOURCES"]![current] != null) { Require(old == current, "Stale Foundation document sourceName"); old = Text(review["REBASED_SOURCES"]![current]); }
                Require(data.Mapping[old] == current, "Stale runtime sourceName: " + current); CompareOperation(old, operation, baseline, review, data.Additions); CompareOperation(old, classify(final), baseline, review, data.Additions); seen.Add(old);
            }
        }
        foreach (var old in CatalogData.Merged) CompareOperation(old, Text(listed[data.Mapping[old]]["operation"]), baseline, review, data.Additions);
        Require(Set(seen.Concat(CatalogData.Merged).Concat(withdrawn.Select(p => Text(p.Value)))).SetEquals(baseline.Select(p => p.Key).Concat(data.Additions.Select(p => p.Key).Except(data.HostOnly.Keys))), "Uncompared baseline tools");
        Require(seenNew.SetEquals(newFoundation.Select(p => p.Key)), "Foundation addition coverage differs");
        foreach (var (current, old) in withdrawn) Require(baseline.ContainsKey(Text(old)) && data.Mapping[Text(old)] == current && !listed.ContainsKey(current), "Withdrawn tool exposed: " + current);
        // Batch 4 still owns the snapshot verifier; preserve its source/default/output-schema assertions.
        var snapshot = ProcessRunner.Run(Environment.GetEnvironmentVariable("PYTHON") ?? "python", ["-B", "scripts/checks/Snapshot-ToolContracts.py", "verify"], root); ProcessRunner.RequireSuccess(snapshot, "V4 contract verification");
        var contracts = Read(root, "manifest/contracts/v4/baseline/21.json")["tools"]!.AsArray().ToDictionary(t => Text(t!["name"]), t => t!);
        foreach (var p in listed) Require(Set(Strings(p.Value["parameters"])).SetEquals(contracts[p.Key]["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key)), "Guard parameter roster differs: " + p.Key);
        Console.WriteLine($"Tool list: V20={data.Rosters["20"].Count}, V21={data.Rosters["21"].Count}, union={expected.Count}; names match.");
        Console.WriteLine($"Operation comparison: {baseline.Count} released tools, {seen.Count} runtime mappings, {CatalogData.Merged.Count} merged guides, {withdrawn.Count} withdrawn, {seenNew.Count} reviewed Foundation additions, {data.HostOnly.Count} host-only P8 tools, {review["EXCEPTIONS"]!.AsObject().Count} justified exceptions; 0 unexplained differences."); return 0;
    }
}
