using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class RatchetChecks
{
    internal static readonly Dictionary<string, string> Baselines = new()
    {
        ["swallowed"] = "swallowed-exceptions-baseline.json", ["comments"] = "comment-hygiene-baseline.json",
        ["mcp-text"] = "mcp-text-baseline.json", ["envelopes"] = "response-envelope-baseline.json"
    };
    internal static int Run(string root, Options options)
    {
        var kind = options.Get("Kind") ?? throw new ReleaseException("-Kind swallowed|comments|mcp-text|envelopes is required", 64);
        if (!Baselines.TryGetValue(kind, out var filename)) throw new ReleaseException("Unknown ratchet kind: " + kind, 64);
        var path = Path.GetFullPath(options.Get("Baseline", Path.Combine(root, "scripts/checks", filename)));
        var update = options.Has("UpdateBaseline");
        if (options.Has("AllowGrowth") && !update) throw new ReleaseException("-AllowGrowth requires -UpdateBaseline", 64);
        var initialize = update && options.Has("AllowGrowth") && !File.Exists(path);
        if (options.Get("ReviewData") is not null && (kind != "mcp-text" || !update)) throw new ReleaseException("-ReviewData requires -Kind mcp-text -UpdateBaseline", 64);
        if (kind == "envelopes") return Envelopes(root, path, update, options.Has("AllowGrowth"), options.Get("Output"));
        var errors = new List<string>();
        var rows = new List<JsonObject>();
        foreach (var (name, project, source) in SourceCheck.Sources(root, errors, kind != "mcp-text"))
        {
            try
            {
                if (kind == "swallowed") { var found = SwallowedExceptions.ScanSource(source, name, project); rows.AddRange(found.Rows); errors.AddRange(found.Errors); }
                else if (kind == "comments") rows.AddRange(CommentHygiene.ScanSource(source, name, project));
                else rows.AddRange(McpText.ScanSource(source, name, project));
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { errors.Add(name + ": " + ex.Message); }
        }
        if (options.Get("Output") is { } output) SourceCheck.Write(output, new JsonObject { ["rows"] = SourceCheck.Json(rows), ["errors"] = SourceCheck.Json(errors) });
        try
        {
            var data = initialize ? new JsonObject { ["format"] = 1, ["catches"] = new JsonArray(), ["entries"] = new JsonArray(), ["allowlist"] = new JsonArray() } : JsonNode.Parse(File.ReadAllText(path))!;
            var field = kind == "swallowed" ? "catches" : "entries";
            var previous = SourceCheck.Baseline(data, field, kind switch { "swallowed" => ["empty", "discarding"], "comments" => CommentHygiene.Kinds, _ => McpText.Kinds });
            var current = kind == "swallowed" ? SwallowedExceptions.BaselineRows(rows) : rows.Select(CommentHygiene.WithoutProject).ToList();
            List<JsonObject> consumed = [];
            if (kind == "mcp-text")
            {
                var allowed = McpText.ReadAllowed(data, update);
                if (options.Get("ReviewData") is { } reviewPath)
                {
                    allowed = [];
                    foreach (var review in JsonNode.Parse(File.ReadAllText(reviewPath))!.AsArray())
                    {
                        if (!McpText.DataCategories.Contains(review!["dataCategory"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(review["reason"]?.GetValue<string>())) throw new ReleaseException("Data review requires an approved category and reason");
                        var matches = rows.Where(r => new[] { "path", "line", "literal" }.All(k => JsonNode.DeepEquals(r[k], review[k]))).ToArray();
                        if (matches.Length != 1) throw new ReleaseException("Data review must identify one current literal");
                        var row = (JsonObject)matches[0].DeepClone();
                        row["reason"] = review["reason"]!.DeepClone(); row["dataCategory"] = review["dataCategory"]!.DeepClone(); allowed.Add(row);
                    }
                }
                var partition = McpText.Partition(rows, allowed);
                current = partition.Guarded.Select(CommentHygiene.WithoutProject).ToList();
                consumed = partition.Consumed.Select(CommentHygiene.WithoutProject).ToList();
            }
            var added = SourceCheck.Unmatched(current, previous);
            var removed = SourceCheck.Unmatched(previous, current);
            Console.WriteLine($"Baseline: {current.Count} current, {added.Count} added, {removed.Count} disappeared; {rows.Count} inventory entries.");
            if (update && errors.Count == 0)
            {
                if (kind is "comments" or "mcp-text" && options.Has("AllowGrowth") && File.Exists(path)) throw new ReleaseException("-AllowGrowth is only permitted when creating a reviewed initial baseline");
                if (kind == "mcp-text" && current.Count > 0) throw new ReleaseException($"Refusing first-party MCP baseline text: {current.Count} Chinese literal(s) require translation or exact data review");
                if (added.Count > 0 && !options.Has("AllowGrowth")) throw new ReleaseException("Refusing baseline growth: " + added.Count + " new/changed entries");
                var description = kind switch
                {
                    "swallowed" => "sha256 of tokenized try body, catch clause (including filter), catch body; paths/lines are informational; duplicate fingerprints retain their multiplicity",
                    "comments" => "sha256 of category and normalized comment line/literal; Leftovers is a file count; paths/lines are informational; multiplicity is retained",
                    _ => "sha256 of sink category and own literal text/segments; interpolation expressions counted separately; paths/lines informational; multiplicity retained"
                };
                var result = new JsonObject { ["format"] = 1, ["fingerprint"] = description, [field] = SourceCheck.Sorted(current) };
                if (kind == "mcp-text") result["allowlist"] = SourceCheck.Sorted(consumed);
                SourceCheck.Write(path, result);
            }
            else foreach (var row in kind == "mcp-text" ? current : added) errors.Add($"{row["path"]}:{row["line"]}: new/changed {row["kind"]} {row["fingerprint"]}");
        }
        catch (Exception ex) when (ex is IOException or ReleaseException or System.Text.Json.JsonException or InvalidOperationException) { errors.Add(ex.Message); }
        return SourceCheck.Report(kind + " ratchet", errors);
    }
    private static int Envelopes(string root, string path, bool update, bool allowGrowth, string? output)
    {
        var (rows, errors) = ResponseEnvelopes.Scan(root);
        var current = ResponseEnvelopes.Ratchet.ToDictionary(k => k, k => rows.Sum(r => r!["handwritten"]![k]!.GetValue<int>()));
        if (output is not null) SourceCheck.Write(output, new JsonObject { ["files"] = rows.DeepClone(), ["totals"] = SourceCheck.Json(ResponseEnvelopes.Metrics.ToDictionary(k => k, k => rows.Sum(r => r!["counts"]![k]!.GetValue<int>()))), ["handwritten"] = SourceCheck.Json(current), ["errors"] = SourceCheck.Json(errors) });
        try
        {
            var data = update && allowGrowth && !File.Exists(path) ? new JsonObject { ["format"] = 1, ["counts"] = SourceCheck.Json(ResponseEnvelopes.Ratchet.ToDictionary(k => k, _ => 0)) } : JsonNode.Parse(File.ReadAllText(path))!;
            if (data is not JsonObject || data["format"]?.ToJsonString() != "1" || data["counts"] is not JsonObject previous || !previous.Select(p => p.Key).Order().SequenceEqual(ResponseEnvelopes.Ratchet.Order()) || previous.Any(p => p.Value is not JsonValue n || !n.TryGetValue<int>(out var value) || value < 0)) throw new ReleaseException("Invalid response envelope baseline");
            var growth = current.Where(p => p.Value > previous[p.Key]!.GetValue<int>()).Select(p => $"{p.Key} grew from {previous[p.Key]} to {p.Value}").ToArray();
            if (update && errors.Count == 0)
            {
                if (growth.Length > 0 && !allowGrowth) throw new ReleaseException("Refusing baseline growth; -AllowGrowth is only for reviewed initialization");
                SourceCheck.Write(path, new JsonObject { ["format"] = 1, ["description"] = "Path-independent totals of hand-written assignments; timestamp includes DateTime.Now.ToString. The qualified ResponseMeta builder is counted in the inventory but excluded from this ratchet.", ["counts"] = SourceCheck.Json(current) }, true);
            }
            else errors.AddRange(growth);
        }
        catch (Exception ex) when (ex is IOException or ReleaseException or System.Text.Json.JsonException) { errors.Add(ex.Message); }
        Console.WriteLine("Hand-written ratchet: " + TiaMcp.BuildCommon.PythonJson.Dumps(current, sortKeys: true));
        return SourceCheck.Report("Response-envelope check", errors);
    }
}
