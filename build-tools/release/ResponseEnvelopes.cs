using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class ResponseEnvelopes
{
    internal static readonly string[] Ratchet = ["timestamp_now_assignments", "success_assignments"];
    internal static readonly string[] Metrics = ["timestamp_indexers", "success_indexers", "ok_indexers", "timestamp_assignments", "timestamp_now_assignments", "success_assignments", "ok_assignments", "new_json_object", "new_response_message", "throw_mcp_exception", "throw_portal_exception", "timestamp_local_datetime", "datetime_now_roundtrip", "datetime_utcnow", "datetimeoffset_utcnow_roundtrip", "datetimeoffset_now_roundtrip", "created_utc_custom", "B1", "B2", "B3", "B4", "B5a", "B5b", "B5c", "B5d", "B6", "B7", "B8", "B9"];
    internal static string? Literal(Token token)
    {
        if (token.Kind != "literal" || token.Expressions.Count > 0) return null;
        var value = token.Value;
        if (value.StartsWith("@\"", StringComparison.Ordinal)) return value[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        if (value.StartsWith("\"\"\"", StringComparison.Ordinal)) { var width = value.TakeWhile(c => c == '"').Count(); return value[width..^width].Trim(); }
        if (value.StartsWith('"')) try { return JsonNode.Parse(value)?.GetValue<string>(); } catch (System.Text.Json.JsonException) { return null; }
        return null;
    }
    internal static List<(int Lo, int Hi, string Name)> Methods(IReadOnlyList<Token> tokens, IReadOnlyDictionary<int, int> pairs)
    {
        var ranges = new List<(int, int, string)>();
        foreach (var (start, end) in pairs)
        {
            if (tokens[start].Value != "(" || start == 0 || tokens[start - 1].Kind != "identifier") continue;
            var cursor = start - 2;
            while (cursor >= 0 && tokens[cursor].Value is not (";" or "{" or "}" or "=>")) cursor--;
            if (!tokens.Skip(cursor + 1).Take(start - cursor - 2).Any(t => t.Value is "public" or "private" or "internal" or "protected")) continue;
            var body = end + 1;
            if (body < tokens.Count && tokens[body].Value is "{" or "=>")
            {
                var finish = tokens[body].Value == "{" ? pairs[body] : Enumerable.Range(body + 1, tokens.Count - body - 1).FirstOrDefault(i => tokens[i].Value == ";", tokens.Count);
                ranges.Add((body, finish, tokens[start - 1].Value));
            }
        }
        return ranges;
    }
    internal static List<(int Lo, int Hi)> Builders(IReadOnlyList<Token> tokens, IReadOnlyDictionary<int, int> pairs)
    {
        var ns = "";
        var ranges = new List<(int, int)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Value == "namespace") ns = string.Concat(tokens.Skip(i + 1).TakeWhile(t => t.Value is not ("{" or ";")).Select(t => t.Value));
            if (i + 2 >= tokens.Count || tokens[i].Value != "class" || tokens[i + 2].Value != "{") continue;
            if (ns == "TiaMcpServer.ModelContextProtocol" && tokens[i + 1].Value == "ResponseMeta") ranges.Add((i + 2, pairs[i + 2]));
            if (ns == "TiaMcp.Adapters" && tokens[i + 1].Value == "PlcFoundationEngine")
                ranges.AddRange(Methods(tokens, pairs).Where(m => i + 2 < m.Lo && m.Lo < m.Hi && m.Hi < pairs[i + 2] && m.Name == "RunHardwareAddressStep").Select(m => (m.Lo, m.Hi)));
        }
        return ranges;
    }
    internal static (Dictionary<string, int> Counts, Dictionary<string, int> Handwritten) ScanTokens(IReadOnlyList<Token> tokens)
    {
        var pairs = MatchingPairs.Find(tokens);
        var values = tokens.Select(t => t.Kind == "literal" ? Literal(t) : t.Value.TrimStart('@')).ToArray();
        var counts = new Dictionary<string, int>();
        var handwritten = new Dictionary<string, int>();
        var methods = Methods(tokens, pairs);
        var builders = Builders(tokens, pairs);
        void Add(string key, int count = 1) => counts[key] = counts.GetValueOrDefault(key) + count;
        bool At(int index, params string?[] pattern) => index + pattern.Length <= values.Length && values.Skip(index).Take(pattern.Length).SequenceEqual(pattern);
        string Method(int index) => methods.Where(m => m.Lo < index && index < m.Hi).OrderByDescending(m => m.Lo).Select(m => m.Name).FirstOrDefault() ?? "";
        bool Builder(int index) => builders.Any(b => b.Lo < index && index < b.Hi);
        List<(string? Key, int Rhs)> Fields(int opening)
        {
            var fields = new List<(string?, int)>();
            var i = opening + 1;
            while (i < pairs[opening])
            {
                if (i + 1 < values.Length && At(i, "[", values[i + 1], "]", "=") && tokens[i + 1].Kind == "literal") fields.Add((values[i + 1], i + 4));
                i = pairs.TryGetValue(i, out var closing) ? closing + 1 : i + 1;
            }
            return fields;
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == "literal")
            {
                if (token.Expressions.Count > 0)
                {
                    var inner = ScanTokens(token.Expressions);
                    foreach (var (key, count) in inner.Counts) Add(key, count);
                    if (!Builder(i)) foreach (var (key, count) in inner.Handwritten) handwritten[key] = handwritten.GetValueOrDefault(key) + count;
                }
                continue;
            }
            if (i + 2 < values.Length && values[i] == "[" && values[i + 2] == "]" && tokens[i + 1].Kind == "literal" && values[i + 1] is "timestamp" or "success" or "ok") Add(values[i + 1] + "_indexers");
            if (i + 1 < values.Length && At(i, "[", values[i + 1], "]", "=") && tokens[i + 1].Kind == "literal")
            {
                var key = values[i + 1];
                if (key is "timestamp" or "success" or "ok") Add(key + "_assignments");
                var now = key == "timestamp" && At(i + 4, "DateTime", ".", "Now");
                if (now) { Add("timestamp_now_assignments"); if (i + 7 == values.Length || values[i + 7] != ".") Add("timestamp_local_datetime"); }
                if (!Builder(i))
                {
                    if (now) handwritten["timestamp_now_assignments"] = handwritten.GetValueOrDefault("timestamp_now_assignments") + 1;
                    if (key == "success") handwritten["success_assignments"] = handwritten.GetValueOrDefault("success_assignments") + 1;
                }
                if (key == "createdUtc")
                {
                    var tail = values.Skip(i + 4).TakeWhile(v => v is not ("," or ";" or "}")).ToArray();
                    if (tail.TakeLast(6).SequenceEqual(new[] { "ToString", "(", "yyyy-MM-dd HH:mm:ss", ")", "+", "Z" })) Add("created_utc_custom");
                }
                if (key == "timestamp" && At(i + 4, "DateTime", ".", "Now", ".", "ToString", "(", "O", ")")) Add("B9");
            }
            foreach (var (pattern, name) in new (string[], string)[]
            {
                (["new", "JsonObject"], "new_json_object"), (["new", "ResponseMessage"], "new_response_message"),
                (["throw", "new", "McpException"], "throw_mcp_exception"), (["throw", "new", "PortalException"], "throw_portal_exception"),
                (["DateTime", ".", "Now", ".", "ToString", "(", "O", ")"], "datetime_now_roundtrip"), (["DateTime", ".", "UtcNow"], "datetime_utcnow")
            }) if (At(i, pattern)) Add(name);
            foreach (var clock in new[] { "UtcNow", "Now" })
                if (At(i, "DateTimeOffset", ".", clock, ".", "ToString", "(") && (At(i + 6, "o", ")") || At(i + 6, "O", ")"))) Add("datetimeoffset_" + clock.ToLowerInvariant() + "_roundtrip");
            if (At(i, "RuntimeMeta", "(") && Method(i) == "RunPlcSimTool") Add("B5b");
            if (!At(i, "new", "JsonObject") && !At(i, "new", "ResponseMessage")) continue;
            var opening = i + 2;
            if (pairs.TryGetValue(opening, out var end) && values[opening] == "(") opening = end + 1;
            if (opening >= values.Length || values[opening] != "{") continue;
            if (values[i + 1] == "ResponseMessage")
            {
                var body = tokens.Skip(opening + 1).Take(pairs[opening] - opening - 1).ToArray();
                string Text(Token t) => t.Kind == "literal" && t.Value.StartsWith('[') ? string.Join(" ", JsonNode.Parse(t.Value)![2]!.AsArray().Where(p => p is JsonValue).Select(p => p!.GetValue<string>())) : Literal(t) ?? "";
                if (!body.Any(t => t.Value == "Meta") && body.Any(t => Regex.IsMatch(Text(t), @"fail|refus|not found|is null|no project|not available|not accessible|could not|error|are required|must be", RegexOptions.IgnoreCase))) Add("B8");
                continue;
            }
            var entries = Fields(opening);
            var keys = entries.Select(e => e.Key).ToArray();
            var owner = Method(i);
            var local = entries.Any(e => e.Key == "timestamp" && At(e.Rhs, "DateTime", ".", "Now") && (e.Rhs + 3 >= values.Length || values[e.Rhs + 3] != "."));
            if (local && keys.SequenceEqual(new[] { "timestamp", "success" })) Add("B1");
            if (local && keys.SequenceEqual(new[] { "timestamp" })) Add("B2");
            if (local && Array.IndexOf(keys, "success") > 1) Add("B3");
            foreach (var (name, metric) in new[] { ("RunHmiStepTool", "B4"), ("RunOfflineAnalysisTool", "B5a"), ("BatchResult", "B5c"), ("BuildOfflineXmlBuilderReport", "B5d") })
                if (owner == name && keys.Contains("timestamp") && keys.Contains("success")) Add(metric);
            if (keys.Contains("timestamp") && keys.Contains("success") && (owner.EndsWith("Meta", StringComparison.Ordinal) || owner == "Create")) Add("B6");
            if (keys.Contains("ok") && !keys.Contains("success") && !keys.Contains("timestamp")) Add("B7");
        }
        return (counts, handwritten);
    }
    internal static (JsonArray Rows, List<string> Errors) Scan(string root)
    {
        var rows = new JsonArray();
        var errors = new List<string>();
        foreach (var (path, _, source) in SourceCheck.Sources(root, errors).OrderBy(s => s.Path, PythonStringComparer.Instance))
        {
            try
            {
                var (counts, written) = ScanTokens(new CSharpLexer(source).Scan().Tokens);
                rows.Add(new JsonObject { ["path"] = path, ["counts"] = SourceCheck.Json(Metrics.ToDictionary(k => k, k => counts.GetValueOrDefault(k))), ["handwritten"] = SourceCheck.Json(Ratchet.ToDictionary(k => k, k => written.GetValueOrDefault(k))) });
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { errors.Add(path + ": " + ex.Message); }
        }
        return (rows, errors);
    }
}
