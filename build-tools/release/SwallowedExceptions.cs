using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class SwallowedExceptions
{
    internal static readonly string[] Categories = ["cleanup", "teardown", "logging-failure", "probe-optional", "enumerate-optional", "native-fallback", "parse-fallback", "env-probe", "ui", "fail-open-guard", "privacy"];
    private static readonly string[] LogMethods = ["Log", "log", "LogDiag", "LogExceptionSafe", "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical"];
    internal static IEnumerable<Token> CodeTokens(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Kind != "literal") yield return token;
            foreach (var nested in CodeTokens(token.Expressions)) yield return nested;
        }
    }
    internal static string Classify(IReadOnlyList<Token> body, string? exception, IReadOnlyList<Token> filters)
    {
        if (!body.Any(t => t.Kind != "directive")) return "empty";
        var code = CodeTokens(filters.Concat(body)).ToArray();
        for (var i = 0; i < code.Length; i++)
            if (code[i].Kind == "identifier" && (code[i].Value == "throw" || exception is not null && code[i].Value.TrimStart('@') == exception && (i == 0 || code[i - 1].Value is not ("." or "?." or "::")))) return "handled";
        var values = code.Select(t => t.Value.TrimStart('@')).ToArray();
        for (var i = 0; i + 1 < values.Length; i++)
        {
            var value = values[i];
            if (values[i + 1] != "(") continue;
            if (LogMethods.Contains(value)) return "handled";
            var receiver = i >= 2 && values[i - 1] is "." or "?." ? values[i - 2] : "";
            if (receiver == "SwallowedExceptions" && value == "Note" || receiver == "InvocationJournal" && value is "Write" or "Native" ||
                receiver is "Console" or "Debug" or "Trace" && value is "Write" or "WriteLine" or "Fail" or "Assert" or "TraceError" or "TraceWarning" or "TraceInformation" ||
                receiver is "logger" or "_logger" or "Logger" or "log" or "_log" or "Log" && value is "Trace" or "Debug" or "Info" or "Information" or "Warn" or "Warning" or "Error" or "Fatal" or "Critical" || receiver == "_activity" && value == "Append" ||
                value is "Write" or "WriteLine" && i >= 4 && values[i - 4] == "Console" && values[i - 3] == "." && values[i - 2] is "Error" or "Out" && values[i - 1] == ".") return "handled";
        }
        return "discarding";
    }
    internal sealed record Catch(JsonObject Row, int Start, int Opening, int End, int Following) { internal string? Category { get; set; } }
    internal static (List<JsonObject> Rows, List<string> Errors) ScanSource(string source, string path, string project)
    {
        var (code, comments) = new CSharpLexer(source).Scan();
        var tokens = code.ToArray();
        var pairs = MatchingPairs.Find(tokens);
        var catches = new List<Catch>();
        var seen = new HashSet<int>();
        var errors = new List<string>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind != "identifier" || token.Value != "try") continue;
            var opening = index + 1;
            if (opening >= tokens.Length || tokens[opening].Value != "{") throw new ArgumentException($"line {SourceCheck.Line(source, token.OffsetStart)}: try without a block");
            var closing = pairs[opening];
            var cursor = closing + 1;
            while (cursor < tokens.Length && tokens[cursor].Value == "catch")
            {
                var start = cursor++;
                seen.Add(start);
                string? exception = null;
                Token[] filters = [];
                if (tokens[cursor].Value == "(")
                {
                    var end = pairs[cursor];
                    var declaration = tokens[(cursor + 1)..end];
                    if (declaration.Length >= 2 && declaration[^1].Kind == "identifier" && declaration[^2].Value is not ("." or "::")) exception = declaration[^1].Value.TrimStart('@');
                    cursor = end + 1;
                }
                if (tokens[cursor].Value == "when")
                {
                    if (tokens[++cursor].Value != "(") throw new ArgumentException("when without parentheses");
                    var end = pairs[cursor];
                    filters = tokens[(cursor + 1)..end];
                    cursor = end + 1;
                }
                if (tokens[cursor].Value != "{") throw new ArgumentException("catch without a block");
                var finish = pairs[cursor];
                var body = tokens[(cursor + 1)..finish];
                var payload = new[] { SourceCheck.Normalized(tokens[(opening + 1)..closing]), SourceCheck.Normalized(tokens[start..cursor]), SourceCheck.Normalized(body) };
                var row = new JsonObject { ["fingerprint"] = SourceCheck.Hash(payload), ["path"] = path, ["line"] = SourceCheck.Line(source, tokens[start].OffsetStart), ["project"] = project, ["kind"] = Classify(body, exception, filters), ["category"] = null,
                    ["start"] = tokens[start].Start, ["opening"] = tokens[cursor].End, ["end"] = tokens[finish].End, ["following"] = finish + 1 < tokens.Length ? tokens[finish + 1].Start : source.EnumerateRunes().Count() };
                catches.Add(new Catch(row, tokens[start].OffsetStart, tokens[cursor].OffsetEnd, tokens[finish].OffsetEnd, finish + 1 < tokens.Length ? tokens[finish + 1].OffsetStart : source.Length));
                cursor = finish + 1;
            }
        }
        for (var i = 0; i < tokens.Length; i++) if (tokens[i].Kind == "identifier" && tokens[i].Value == "catch" && !seen.Contains(i)) errors.Add($"{path}:{SourceCheck.Line(source, tokens[i].OffsetStart)}: catch has no matching try");
        foreach (var comment in comments)
        {
            if (!Regex.IsMatch(comment.Value, @"\A(?:/\*|//)\s*swallow\s*\(")) continue;
            var location = $"{path}:{SourceCheck.Line(source, comment.OffsetStart)}";
            var match = Regex.Match(comment.Value, @"\A/\*\s*swallow\(([^()]*)\):\s*(.*?)\s*\*/\z", RegexOptions.Singleline);
            if (!match.Success || !Categories.Contains(match.Groups[1].Value) || string.IsNullOrWhiteSpace(match.Groups[2].Value)) { errors.Add(location + ": invalid swallow marker (known category and nonempty reason required)"); continue; }
            var owner = catches.Where(c => c.Start <= comment.OffsetStart && comment.OffsetStart < c.Following && SourceCheck.Line(source, comment.OffsetStart) == c.Row["line"]!.GetValue<int>() || c.Opening <= comment.OffsetStart && comment.OffsetStart < c.End && string.IsNullOrWhiteSpace(source[c.Opening..comment.OffsetStart])).MaxBy(c => c.Start);
            if (owner is null) { errors.Add(location + ": swallow marker must be on the catch line or first in its block"); continue; }
            if (owner.Category is not null) errors.Add(location + ": multiple swallow markers on one catch");
            owner.Category = match.Groups[1].Value;
            owner.Row["category"] = owner.Category;
        }
        return (catches.Select(c => c.Row).ToList(), errors);
    }
    internal static List<JsonObject> BaselineRows(IEnumerable<JsonObject> rows) => rows.Where(r => r["kind"]!.GetValue<string>() != "handled" && r["category"] is null)
        .Select(r => new JsonObject { ["fingerprint"] = r["fingerprint"]!.DeepClone(), ["kind"] = r["kind"]!.DeepClone(), ["path"] = r["path"]!.DeepClone(), ["line"] = r["line"]!.DeepClone() }).ToList();
}
