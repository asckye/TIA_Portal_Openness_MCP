using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

internal static class ReleaseMigrationGenerator
{
    internal const string Output = "docs/releases/v4.0.0-tool-migration.md";
    private static string Code(string value) => "`" + value + "`";
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
    internal static string SchemaType(JsonNode schema)
    {
        if (schema["anyOf"] is JsonArray || schema["oneOf"] is JsonArray) return string.Join('|', Sorted((schema["anyOf"] ?? schema["oneOf"])!.AsArray().Select(s => SchemaType(s!)).Distinct()));
        if (schema["type"] is JsonArray types) return string.Join('|', Sorted(Strings(types).Distinct()));
        var type = Text(schema["type"]); if (type == "array") return "array<" + SchemaType(schema["items"] ?? new JsonObject()) + ">";
        return type.Length > 0 ? type : schema["properties"] != null ? "object" : "unspecified";
    }
    private static string Required(string name, HashSet<string> required) => required.Contains(name) ? "required" : "optional";
    private static string ParameterList(JsonNode tool)
    {
        var schema = tool["inputSchema"]!; var props = schema["properties"]!.AsObject(); var required = Set(Strings(schema["required"]));
        return props.Count == 0 ? "none" : string.Join(", ", Sorted(props.Select(p => p.Key)).Select(name => $"{Code(name)}: {Code(SchemaType(props[name]!))} ({Required(name, required)})"));
    }
    internal static (List<(string Old, string New)> Changes, string[] Required) ParameterChanges(JsonNode oldTool, JsonNode newTool)
    {
        var oldSchema = oldTool["inputSchema"]!; var newSchema = newTool["inputSchema"]!; var oldProps = oldSchema["properties"]!.AsObject(); var newProps = newSchema["properties"]!.AsObject();
        var oldRequired = Set(Strings(oldSchema["required"])); var newRequired = Set(Strings(newSchema["required"])); var pairs = new Dictionary<string, string>(); var used = new HashSet<string>();
        foreach (var name in Sorted(oldProps.Select(p => p.Key)))
        {
            var candidate = newProps.ContainsKey(name) ? name : name.EndsWith("Json", StringComparison.Ordinal) && newProps.ContainsKey(name[..^4]) ? name[..^4] : name == "json" && newProps.ContainsKey("spec") ? "spec" : null;
            if (candidate == null) continue; Require(used.Add(candidate), "Ambiguous parameter mapping: " + Text(newTool["name"]) + "/" + candidate); pairs[name] = candidate;
        }
        var changes = new List<(string, string)>(); var required = new HashSet<string>();
        foreach (var old in Sorted(oldProps.Select(p => p.Key)))
        {
            var oldDesc = $"{Code(old)}: {Code(SchemaType(oldProps[old]!))} ({Required(old, oldRequired)})";
            if (!pairs.TryGetValue(old, out var next)) { changes.Add((oldDesc, "—")); continue; }
            var newDesc = $"{Code(next)}: {Code(SchemaType(newProps[next]!))} ({Required(next, newRequired)})";
            if (old != next || SchemaType(oldProps[old]!) != SchemaType(newProps[next]!) || oldRequired.Contains(old) != newRequired.Contains(next)) changes.Add((oldDesc, newDesc));
            if (newRequired.Contains(next) && !oldRequired.Contains(old)) required.Add(next);
        }
        foreach (var next in Sorted(newProps.Select(p => p.Key).Except(used))) { changes.Add(("—", $"{Code(next)}: {Code(SchemaType(newProps[next]!))} ({Required(next, newRequired)})")); if (newRequired.Contains(next)) required.Add(next); }
        return (changes, Sorted(required).ToArray());
    }
    internal static string Render(string root, bool legacyNames = false)
    {
        var mapping = Read(root, "reference/tool-catalog/v3-v4-renames.json").AsObject().ToDictionary(p => p.Key, p => Text(p.Value)); var families = PortedFamilies.Load(root);
        var oldDocs = new Dictionary<string, JsonNode>(); var newDocs = new Dictionary<string, JsonNode>();
        foreach (var key in Keys) { oldDocs[key] = Read(root, $"manifest/history/contracts-v3/baseline/{key}.json"); newDocs[key] = Read(root, $"manifest/contracts/v4/baseline/{key}.json"); Require(Text(oldDocs[key]["release"]) == key && Text(newDocs[key]["release"]) == key, "Contract release key mismatch: " + key); }
        var oldNames = Set(oldDocs.Values.SelectMany(d => d["tools"]!.AsArray().Select(t => Text(t!["name"]))));
        Require(Set(mapping.Keys).SetEquals(oldNames), "Frozen rename coverage differs from archived contracts");
        var policy = Source(root, "src/EngineHost/SharedToolCatalog.cs"); var removed = Set(Matches(policy.Split("Removed =", 2)[1].Split("};", 2)[0], "\"(\\w+)\""));
        var lines = new List<string> { "# 4.0.0 tool and input migration tables", "", "Generated from the 3.x archive at manifest/history/contracts-v3/baseline, the shipped V4 baselines at manifest/contracts/v4/baseline, and the reviewed current-to-V4 map in " + (legacyNames ? "docs/development/phase6-review.md." : "reference/tool-catalog/v3-v4-renames.json."), "", "Each release table lists every archived 3.x name and its V4 destination, including unchanged names. The generator verifies every archived name's release availability, every mapping target against that release's V4 baseline, and the complete V4 inventory after additions. Historical names below are not registered as aliases.", "" };
        foreach (var key in Keys)
        {
            var oldTools = oldDocs[key]["tools"]!.AsArray().ToDictionary(t => Text(t!["name"]), t => t!); var newTools = newDocs[key]["tools"]!.AsArray().ToDictionary(t => Text(t!["name"]), t => t!);
            var targets = oldTools.Keys.ToDictionary(n => n, n => mapping[n]); var retired = targets.Where(p => key is "20" or "21" && removed.Contains(p.Value)).ToDictionary();
            var withheld = targets.Where(p => PortedFamilies.Withheld(families, key).Contains(p.Value)).ToDictionary();
            foreach (var old in retired.Keys.Concat(withheld.Keys)) targets.Remove(old);
            foreach (var p in targets) Require(newTools.ContainsKey(p.Value), key + ": archived tool maps to missing V4 tool: " + p.Key);
            // A port extends the old-release roster only through the reviewed per-tool table.
            foreach (var name in PortedFamilies.Additions(families, key)) Require(newTools.ContainsKey(name), "Reviewed port absent from V4 baseline: " + key + "/" + name);
            var additions = Sorted(newTools.Keys.Except(targets.Values)).ToArray(); var groups = targets.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray());
            Require(groups.All(g => g.Value.Length == 1 || g.Key == "GetToolUsage"), "Unapproved migration merge: " + key);
            var merged = groups.Where(p => p.Value.Length > 1).ToDictionary(p => p.Key, p => Sorted(p.Value.Where(n => n != p.Key)).ToArray()); var mergedNames = Set(merged.Values.SelectMany(v => v));
            var renamed = targets.Count(p => p.Key != p.Value && !mergedNames.Contains(p.Key)); var unchanged = targets.Count(p => p.Key == p.Value);
            lines.AddRange([$"### Release {key}", "", $"3.x catalog: **{oldTools.Count} tools**; V4 baseline: **{newTools.Count} tools**. Unchanged names: {unchanged}; renamed names: {renamed}; merged legacy entries: {merged.Values.Sum(v => v.Length)}; V4-only tools: {additions.Length}.", "", "| Change | 3.x name(s) | V4 name |", "|---|---|---|"]);
            foreach (var old in Sorted(targets.Keys)) lines.Add($"| {(mergedNames.Contains(old) ? "Merge" : old == targets[old] ? "Unchanged" : "Rename")} | {Code(old)} | {Code(targets[old])} |");
            foreach (var old in Sorted(retired.Keys)) lines.Add($"| Removed duplicate lifecycle (P7-04) | {Code(old)} | {Code(retired[old])} retired; use the shared Foundation session |");
            foreach (var old in Sorted(withheld.Keys))
            {
                string reason = "Removed: " + PortedFamilies.WithheldReason(families, key, withheld[old]);
                if (key == "20" && withheld[old] is "GetOpcUaAccessControl" or "ManageOpcUaAccessControl")
                    reason = "Removed: no API on this release";
                lines.Add($"| {reason} | {Code(old)} | {Code(withheld[old])} is not listed on {key} |");
            }
            if (retired.Count == 0 && withheld.Count == 0) lines.Add("| Removed without replacement | None | — |");
            foreach (var name in additions) lines.Add($"| Added in 4.0 | — | {Code(name)} |");
            lines.AddRange(["", "#### Parameter changes", "", "| 3.x input | V4 input |", "|---|---|"]);
            var rows = new List<(string Name, string Old, string New)>(); var required = new HashSet<(string Name, string Parameter)>();
            foreach (var old in Sorted(oldTools.Keys)) { if (retired.ContainsKey(old) || withheld.ContainsKey(old) || mergedNames.Contains(old)) continue; var (changes, needed) = ParameterChanges(oldTools[old], newTools[targets[old]]); rows.AddRange(changes.Select(p => (old, p.Old, p.New))); foreach (var n in needed) required.Add((targets[old], n)); }
            foreach (var name in additions) foreach (var n in Strings(newTools[name]["inputSchema"]!["required"])) required.Add((name, n));
            if (rows.Count == 0) lines.Add("| None | None |"); else foreach (var row in rows) lines.Add($"| {(row.Old != "—" ? Code(row.Name) + ": " : "")}{Cell(row.Old)} | {Cell(row.New)} |");
            if (merged.Count > 0)
            {
                lines.AddRange(["", "Merged entry input schemas", "", "| Retired 3.x entry | 3.x input | Shared V4 destination inputs |", "|---|---|---|"]);
                foreach (var target in Sorted(merged.Keys)) foreach (var alias in merged[target]) lines.Add($"| {Code(alias)} → {Code(target)} | {Cell(ParameterList(oldTools[alias]))} | {Cell(ParameterList(newTools[target]))} |");
            }
            if (additions.Length > 0)
            {
                lines.AddRange(["", "New tools and their required inputs", "", "| V4-only tool | Required inputs in the shipped V4 baseline |", "|---|---|"]);
                foreach (var name in additions) { var schema = newTools[name]["inputSchema"]!; var needed = Strings(schema["required"]); lines.Add($"| {Code(name)} | {(needed.Length == 0 ? "none" : string.Join(", ", needed.Select(p => Code(p) + ": " + Code(SchemaType(schema["properties"]![p]!)))))} |"); }
            }
            if (required.Count > 0) { lines.AddRange(["", "New required inputs", "", "| Tool | Required input introduced in V4 |", "|---|---|"]); foreach (var row in required.OrderBy(p => p.Name, PythonStringComparer.Instance).ThenBy(p => p.Parameter, PythonStringComparer.Instance)) lines.Add($"| {Code(row.Name)} | {Code(row.Parameter)} |"); }
            else lines.AddRange(["", "New required inputs: **none** for pre-existing tool mappings.", ""]);
            lines.Add("");
        }
        return string.Join('\n', lines).TrimEnd() + "\n";
    }
}
