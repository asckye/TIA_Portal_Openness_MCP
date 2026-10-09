using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class McpText
{
    internal static readonly string[] Kinds = ["description", "exception", "message", "meta", "other-literal"];
    internal static readonly string[] DataCategories = ["project-content", "bilingual-table", "report-artifact", "cli-console", "search-alias"];
    internal static string Unescape(string text) => Regex.Replace(text, @"\\(?:u([0-9a-fA-F]{4})|U([0-9a-fA-F]{8})|x([0-9a-fA-F]{1,4})|(.))", m =>
    {
        var digits = m.Groups.Cast<Group>().Skip(1).Take(3).FirstOrDefault(g => g.Success)?.Value;
        if (digits is not null)
        {
            var code = Convert.ToInt32(digits, 16);
            return code <= 0xffff ? ((char)code).ToString() : char.ConvertFromUtf32(code);
        }
        return m.Groups[4].Value switch { "n" => "\n", "r" => "\r", "t" => "\t", "0" => "\0", var value => value };
    }, RegexOptions.Singleline);
    internal static (string Text, JsonNode? Identity) LiteralParts(Token token)
    {
        if (token.Kind != "literal" || token.Value.StartsWith('\'')) return ("", null);
        var value = token.Value;
        if (value.StartsWith('['))
        {
            var parts = JsonNode.Parse(value)!.AsArray();
            var text = string.Concat(parts[2]!.AsArray().Where(p => p is JsonValue).Select(p => p!.GetValue<string>()));
            var identity = new JsonArray(parts[0]!.DeepClone(), parts[1]!.DeepClone(), new JsonArray(parts[2]!.AsArray().Select(p => p is JsonValue ? p.DeepClone() : null).ToArray()));
            if (parts[1]!.GetValue<int>() < 3 && !parts[0]!.GetValue<string>().Contains('@')) text = Unescape(text);
            return (text, identity);
        }
        if (value.StartsWith(':')) return (value[1..], JsonValue.Create(value));
        if (value.EndsWith("u8", StringComparison.Ordinal)) value = value[..^2];
        if (value.StartsWith("@\"", StringComparison.Ordinal)) return (value[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal), JsonValue.Create(token.Value));
        if (value.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            var width = value.TakeWhile(c => c == '"').Count();
            return (value[width..^width], JsonValue.Create(token.Value));
        }
        if (value.StartsWith('"')) return (Unescape(value[1..^1]), JsonValue.Create(token.Value));
        return ("", null);
    }
    internal static int ExpressionEnd(IReadOnlyList<Token> tokens, IReadOnlyDictionary<int, int> pairs, int start)
    {
        var i = start;
        while (i < tokens.Count)
        {
            if (tokens[i].Value is ";" or "," or "}" or ")" or "]") break;
            i = pairs.TryGetValue(i, out var end) ? end + 1 : i + 1;
        }
        return i;
    }
    internal static List<(int Lo, int Hi, string Kind)> SinkRanges(IReadOnlyList<Token> tokens, IReadOnlyDictionary<int, int> pairs)
    {
        var ranges = new List<(int, int, string)>();
        var values = tokens.Select(t => t.Kind == "identifier" ? t.Value.TrimStart('@') : t.Value).ToArray();
        string At(int i) => i >= 0 && i < values.Length ? values[i] : "";
        for (var i = 0; i < tokens.Count; i++)
        {
            var value = values[i];
            if (value is "Description" or "DescriptionAttribute" && At(i + 1) == "(" && pairs.Any(p => tokens[p.Key].Value == "[" && p.Key < i && i < p.Value)) ranges.Add((i + 1, pairs[i + 1], "description"));
            if (value == "new")
            {
                var opening = i + 1;
                while (opening < tokens.Count && (tokens[opening].Kind == "identifier" || At(opening) is "." or "::")) opening++;
                if (pairs.TryGetValue(opening, out var end) && At(opening) == "(" && (opening > i + 1 && At(opening - 1).EndsWith("Exception", StringComparison.Ordinal) || opening == i + 1 && At(i - 1) == "throw")) ranges.Add((opening, end, "exception"));
            }
            if (value is "=" or "+=" or "??=" or "=>" && i > 0)
            {
                var lhs = At(i - 1);
                if (lhs == "]" && i >= 3) lhs = LiteralParts(tokens[i - 2]).Text;
                string? kind = lhs.ToLowerInvariant() switch { "message" or "error" => "message", "meta" => "meta", _ => null };
                if (i >= 4 && At(i - 1) == "]" && At(i - 4).Equals("meta", StringComparison.OrdinalIgnoreCase)) kind = "meta";
                if (kind is not null) ranges.Add((i, ExpressionEnd(tokens, pairs, i + 1), kind));
            }
            if (value == "ResponseMeta" && At(i + 1) == "." && At(i + 3) == "(") ranges.Add((i + 3, pairs[i + 3], "meta"));
        }
        return ranges;
    }
    internal static List<JsonObject> ScanSource(string source, string path, string project)
    {
        var rows = new List<JsonObject>();
        void Visit(IReadOnlyList<Token> tokens, string inherited)
        {
            var ranges = SinkRanges(tokens, MatchingPairs.Find(tokens));
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind != "literal") continue;
                var kind = ranges.Where(r => r.Lo < i && i < r.Hi).Select(r => r.Kind).Append(inherited).MinBy(k => Array.IndexOf(Kinds, k))!;
                var (text, identity) = LiteralParts(token);
                var count = text.EnumerateRunes().Count(r => r.Value == 0x3007 || r.Value is >= 0x3400 and <= 0x4dbf or >= 0x4e00 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x2ffff or >= 0x30000 and <= 0x323af);
                if (count > 0) rows.Add(new JsonObject { ["kind"] = kind, ["fingerprint"] = SourceCheck.Fingerprint(kind, identity), ["path"] = path, ["line"] = SourceCheck.Line(source, token.OffsetStart), ["project"] = project, ["cjk"] = count, ["literal"] = identity });
                if (token.Expressions.Count > 0) Visit(token.Expressions, kind);
            }
        }
        Visit(new CSharpLexer(source).Scan().Tokens, "other-literal");
        return rows;
    }
    internal static (List<JsonObject> Guarded, List<JsonObject> Consumed) Partition(IEnumerable<JsonObject> rows, IEnumerable<JsonObject> allowed)
    {
        var remaining = allowed.GroupBy(r => r["fingerprint"]!.GetValue<string>()).ToDictionary(g => g.Key, g => new Queue<JsonObject>(g));
        var guarded = new List<JsonObject>();
        var consumed = new List<JsonObject>();
        foreach (var row in rows)
        {
            if (remaining.TryGetValue(row["fingerprint"]!.GetValue<string>(), out var matches) && matches.Count > 0)
            {
                var review = matches.Dequeue();
                var copy = (JsonObject)row.DeepClone();
                copy["reason"] = review["reason"]!.DeepClone();
                copy["dataCategory"] = review["dataCategory"]?.DeepClone();
                consumed.Add(copy);
            }
            else guarded.Add(row);
        }
        return (guarded, consumed);
    }
    internal static List<JsonObject> ReadAllowed(JsonNode data, bool updating)
    {
        var entries = SourceCheck.Baseline(data, "entries", Kinds);
        if (data["allowlist"] is not JsonArray allowed) throw new ReleaseException("Missing reviewed allowlist");
        var reviews = SourceCheck.Baseline(new JsonObject { ["format"] = 1, ["entries"] = allowed.DeepClone() }, "entries", Kinds);
        foreach (var row in entries.Concat(reviews))
            if (row["cjk"] is not JsonValue cjk || !cjk.TryGetValue<int>(out var count) || count < 1 || !(row["literal"] is JsonArray || row["literal"] is JsonValue literal && literal.TryGetValue<string>(out _)) || row["fingerprint"]?.GetValue<string>() != SourceCheck.Fingerprint(row["kind"]!.GetValue<string>(), row["literal"])) throw new ReleaseException("Invalid literal entry");
        foreach (var row in allowed)
            if (row!["reason"] is not JsonValue reason || !reason.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || !updating && !DataCategories.Contains(row["dataCategory"]?.GetValue<string>())) throw new ReleaseException("Allowlist needs a data literal and a nonempty review reason");
        if (entries.Count > 0 && !updating) throw new ReleaseException("First-party MCP baseline entries must be empty");
        return allowed.Select(r => r!.AsObject()).ToList();
    }
}
