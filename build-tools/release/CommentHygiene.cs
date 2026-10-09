using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class CommentHygiene
{
    internal static readonly string[] Kinds = ["product-version", "old-phase", "maintainer", "tombstone", "commented-code", "tool-count", "leftovers-file"];
    private static readonly Dictionary<string, Regex> Patterns = new()
    {
        ["product-version"] = new(@"(?<![\w.])[vV]?[23]\.(?:\d+|[xX])(?:\.(?:\d+|[xX]))*(?![\w.])"),
        ["old-phase"] = new(@"\bphase\s*\d+\b|\bsub[ -]?batch\b|子批次", RegexOptions.IgnoreCase),
        ["maintainer"] = new(@"\bmaintainer(?:s|\b)|\basckye\b|维护者", RegexOptions.IgnoreCase),
        ["tombstone"] = new(@"\b(?:moved|relocated|extracted)\s+to\b|已(?:移|迁)|移至|迁至", RegexOptions.IgnoreCase),
        ["tool-count"] = new(@"\b\d+\s+tools\b|\d+\s*(?:个|条)?工具|工具(?:数|数量|共|总计)[：:为是\s]*\d+", RegexOptions.IgnoreCase)
    };
    internal static bool CommentedStatement(string text)
    {
        if (!text.EndsWith(';')) return false;
        Token[] tokens;
        IReadOnlyDictionary<int, int> pairs;
        try { tokens = new CSharpLexer(text).Scan().Tokens.ToArray(); pairs = MatchingPairs.Find(tokens); }
        catch (ArgumentException) { return false; }
        var values = tokens.Select(t => t.Value).ToArray();
        if (values.Length == 0 || values[^1] != ";") return false;
        if (values[0] is "return" or "throw" or "break" or "continue" or "yield") return true;
        var i = tokens.Length > 2 && tokens.Take(2).All(t => t.Kind == "identifier") && values[2] == "=" ? 1 : 0;
        if (tokens[i].Kind != "identifier") return false;
        i++;
        while (i < tokens.Length)
        {
            if (values[i] is "." or "?." && i + 1 < tokens.Length && tokens[i + 1].Kind == "identifier") i += 2;
            else if (values[i] is "[" or "(" && pairs.TryGetValue(i, out var closing))
            {
                if (values[i] == "(" && closing == tokens.Length - 2) return true;
                i = closing + 1;
            }
            else break;
        }
        return i < tokens.Length - 1 && values[i] is "=" or "+=" or "-=" or "??=" or "++" or "--";
    }
    internal static bool ProductVersion(string text)
    {
        foreach (Match match in Patterns["product-version"].Matches(text))
        {
            var prefix = text[..match.Index];
            var native = match.Value.StartsWith('V') && Regex.IsMatch(text, @"\b(?:FW|firmware|S7TIA|SafetySystemVersion|OrderNumber|PN|expansions)\b");
            if (native || Regex.IsMatch(prefix, @"\b(?:FW|firmware|CAEX|manual|section|chapter)\s*(?:[><=]+\s*)?$", RegexOptions.IgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(prefix) && Regex.IsMatch(text[(match.Index + match.Length)..], @"\A\s+(?:nuget\b|Api\.)", RegexOptions.IgnoreCase)) continue;
            return true;
        }
        return false;
    }
    internal static List<JsonObject> ScanSource(string source, string path, string project)
    {
        var scanner = new CSharpLexer(source);
        var tokens = scanner.Scan().Tokens;
        var comments = scanner.AllComments.Select(t => (Start: t.OffsetStart, Value: t.Value)).ToList();
        foreach (var token in SwallowedExceptions.CodeTokens(tokens).Where(t => t.Kind == "directive"))
            foreach (Match match in Regex.Matches(source[token.OffsetStart..token.OffsetEnd], "\"(?:\\\\.|[^\"\\\\])*\"|//[^\\n]*|/\\*.*?\\*/"))
                if (match.Value.StartsWith('/')) comments.Add((token.OffsetStart + match.Index, match.Value));
        var lines = new Dictionary<int, List<string>>();
        foreach (var comment in comments.OrderBy(c => c.Start))
        {
            var offset = comment.Start;
            foreach (Match part in Regex.Matches(comment.Value, @"[^\r\n\v\f\x1c-\x1e\x85\u2028\u2029]*(?:\r\n|[\r\n\v\f\x1c-\x1e\x85\u2028\u2029]|\z)"))
            {
                if (part.Length == 0) continue;
                var line = SourceCheck.Line(source, offset);
                offset += part.Length;
                var text = Regex.Replace(part.Value.Trim(), @"\A(?:///?|/\*+|\*)\s?", "");
                text = Regex.Replace(text, @"\s*\*/$", "").Trim();
                if (!lines.TryGetValue(line, out var pieces)) lines[line] = pieces = [];
                pieces.Add(text);
            }
        }
        var rows = new List<JsonObject>();
        foreach (var (line, pieces) in lines)
        {
            var text = string.Join(" ", pieces);
            var kinds = Patterns.Where(p => p.Value.IsMatch(text)).Select(p => p.Key).ToList();
            if (kinds.Contains("product-version") && !ProductVersion(text)) kinds.Remove("product-version");
            if (kinds.Contains("tool-count") && Regex.IsMatch(text, @"\b(?:Windsurf|capped|limit|maximum)\b", RegexOptions.IgnoreCase)) kinds.Remove("tool-count");
            if (pieces.Any(CommentedStatement)) kinds.Add("commented-code");
            foreach (var kind in kinds) rows.Add(new JsonObject { ["kind"] = kind, ["fingerprint"] = SourceCheck.Fingerprint(kind, Regex.Replace(text, @"\s+", " ")), ["path"] = path, ["line"] = line, ["project"] = project });
        }
        if (Path.GetFileName(path).Contains("leftovers", StringComparison.OrdinalIgnoreCase)) rows.Add(new JsonObject { ["kind"] = "leftovers-file", ["fingerprint"] = SourceCheck.Fingerprint("leftovers-file", null), ["path"] = path, ["line"] = 1, ["project"] = project });
        return rows;
    }
    internal static JsonObject WithoutProject(JsonObject row)
    {
        var copy = (JsonObject)row.DeepClone();
        copy.Remove("project");
        return copy;
    }
}
