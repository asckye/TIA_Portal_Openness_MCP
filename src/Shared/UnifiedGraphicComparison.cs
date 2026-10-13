using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens.Services
{
    internal static class UnifiedGraphicComparison
    {
        private static readonly string[] Geometry = { "Left", "Top", "Width", "Height" };
        internal static string[] Names(string json)
        {
            if (json == null || json.Length > 65536) throw new ArgumentException("itemNamesJson exceeds 64 KiB.");
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("itemNamesJson must be an array of exact object names.");
            if (array.Count == 0 || array.Count > 128) throw new ArgumentException("Select 1..128 objects; split larger selections explicitly.");
            var names = array.Select(n => n?.GetValue<string>() ?? "").ToArray();
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 1024) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ArgumentException("Names must be nonempty, unique and at most 1024 characters. Names are case sensitive and are not normalized.");
            return names;
        }
        private static bool Number(JsonObject row, string field, out decimal number)
        {
            number = 0;
            var evidence = row["fields"]?[field];
            if (evidence?["status"]?.ToString() != "ok" || !decimal.TryParse(evidence["value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
            return (field != "Width" && field != "Height") || number >= 0;
        }
        private static JsonObject? Envelope(IReadOnlyList<JsonObject> rows)
        {
            if (rows.Count == 0 || rows.Any(r => !Geometry.All(f => Number(r, f, out _)))) return null;
            try
            {
                var rectangles = rows.Select(r => { Number(r, "Left", out var l); Number(r, "Top", out var t); Number(r, "Width", out var w); Number(r, "Height", out var h); return new[] { l, t, checked(l + w), checked(t + h) }; }).ToArray();
                var left = rectangles.Min(r => r[0]); var top = rectangles.Min(r => r[1]);
                return new JsonObject { ["left"] = left, ["top"] = top, ["width"] = checked(rectangles.Max(r => r[2]) - left), ["height"] = checked(rectangles.Max(r => r[3]) - top), ["nativeGroupBounds"] = false };
            }
            catch (OverflowException) /* swallow(parse-fallback): overflowed persisted coordinates cannot provide approximate selection bounds */ { return null; }
        }
        // Offline comparison accepts the full array of persisted page Meta objects.
        // Require a contiguous, completed collection so missing pages cannot look unchanged.
        private static (JsonObject scope, Dictionary<string, JsonObject> items) Decode(string json)
        {
            if (json == null || json.Length > 4 * 1024 * 1024) throw new ArgumentException("Snapshot pages exceed 4 MiB.");
            var pages = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("Supply an array of saved page Meta objects, in pageIndex order.");
            if (pages.Count == 0 || pages.Count > 1024) throw new ArgumentException("Expected 1..1024 pages.");
            JsonObject? scope = null; string? id = null; var records = new List<JsonObject>();
            for (int j = 0; j < pages.Count; j++)
            {
                var page = pages[j] as JsonObject ?? throw new ArgumentException("Each page must be a Meta object.");
                var currentScope = page["scope"] as JsonObject;
                if (j == 0) { scope = currentScope; id = page["collectionId"]?.ToString(); }
                if (scope == null || scope["tool"]?.ToString() != "ReadUnifiedGraphicSelection" || string.IsNullOrEmpty(id)
                    || !JsonNode.DeepEquals(scope, currentScope) || id != page["collectionId"]?.ToString()
                    || page["pageIndex"]?.GetValue<int>() != j || page["apiCallSuccess"]?.GetValue<bool>() != true)
                    throw new ArgumentException("Pages must be contiguous, successful and from one graphical-selection collection/scope.");
                bool final = j == pages.Count - 1;
                if (final ? page["traversalComplete"]?.GetValue<bool>() != true || page["nextCursor"] != null || page["truncated"]?.GetValue<bool>() != false
                    : page["nextCursor"] == null || page["traversalComplete"]?.GetValue<bool>() == true)
                    throw new ArgumentException("Missing, out-of-order or unfinished pages; comparison refused.");
                foreach (var row in page["records"] as JsonArray ?? throw new ArgumentException("Missing records."))
                    records.Add(row as JsonObject ?? throw new ArgumentException("Invalid record."));
            }
            var names = Names(scope!["itemNames"]!.ToJsonString());
            var summary = records.Where(r => r["kind"]?.ToString() == "graphicSelectionSummary").ToArray();
            var items = records.Where(r => r["kind"]?.ToString() == "graphicObject").ToArray();
            if (summary.Length != 1 || records.Last() != summary[0] || items.Length != names.Length || records.Count != items.Length + 1
                || items.Any(r => r["geometryComplete"]?.GetValue<bool>() != true || !Geometry.All(f => Number(r, f, out _))))
                throw new ArgumentException("Complete geometry and a final summary are required; incomplete samples cannot prove unchanged coordinates.");
            var map = items.ToDictionary(r => r["name"]!.GetValue<string>(), StringComparer.Ordinal);
            if (names.Any(n => !map.ContainsKey(n))) throw new ArgumentException("Object selection does not match records.");
            return (scope, map);
        }
        internal static JsonObject Compare(string beforePagesJson, string afterPagesJson)
        {
            var before = Decode(beforePagesJson); var after = Decode(afterPagesJson);
            if (!JsonNode.DeepEquals(before.scope, after.scope)) throw new ArgumentException("Both snapshots must have exactly the same project, HMI, screen and itemNames scope.");
            var changes = new JsonArray(); int changed = 0;
            foreach (var entry in before.items)
            {
                var next = after.items[entry.Key]; var previous = entry.Value;
                if (previous["type"] == null || !JsonNode.DeepEquals(previous["type"], next["type"]) || !JsonNode.DeepEquals(previous["path"], next["path"]))
                    throw new ArgumentException("Object path/type changed; geometry comparison refused: " + entry.Key);
                var fields = new JsonObject(); bool moved = false;
                foreach (var field in Geometry)
                {
                    Number(previous, field, out var a); Number(next, field, out var b);
                    fields[field] = new JsonObject { ["before"] = a, ["after"] = b, ["delta"] = checked(b - a), ["changed"] = a != b };
                    moved |= a != b;
                }
                if (moved) changed++;
                changes.Add(new JsonObject { ["name"] = entry.Key, ["path"] = previous["path"]?.DeepClone(), ["changed"] = moved, ["fields"] = fields });
            }
            return new JsonObject { ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = true,
                ["scope"] = before.scope.DeepClone(), ["readOnly"] = true, ["offline"] = true,
                ["coverage"] = "raw Left/Top/Width/Height comparison only", ["expectedCount"] = before.items.Count, ["actualCount"] = changes.Count,
                ["changedObjectCount"] = changed, ["truncated"] = false, ["nextCursor"] = null, ["failureCount"] = 0, ["failures"] = new JsonArray(),
                ["nativeGroupVerified"] = false, ["causalityEstablished"] = false,
                ["limitation"] = "Matching names/paths/types do not prove persistent object identity across deletion/recreation. Coordinate changes alone do not establish grouping, layout constraints or who caused a change.", ["objects"] = changes };
        }
    }
}
