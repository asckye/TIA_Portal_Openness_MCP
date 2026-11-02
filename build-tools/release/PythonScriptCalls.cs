using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

// Only literal outbound names are checked; comments and string payloads stay opaque.
internal static class PythonScriptCalls
{
    private static readonly Regex Tokens = new(""""(?<comment>\#[^\r\n]*)|(?<string>(?<prefix>(?i:br|rb|fr|rf|r|u|b|f))?(?<quote>'''|"""|['"])(?<text>(?:\\[\s\S]|(?!\k<quote>)[^\\])*?)\k<quote>)|(?<identifier>[^\W\d]\w*)|(?<symbol>\S)"""",
        RegexOptions.IgnorePatternWhitespace);

    internal static List<(int Line, string Name)> Calls(string source, Func<string, bool> helper, Func<string, bool> toolLiteral)
    {
        var tokens = Tokens.Matches(source).Where(match => !match.Groups["comment"].Success).Select(match =>
        {
            var prefix = match.Groups["prefix"].Value;
            var literal = match.Groups["string"].Success && !prefix.Contains("f", StringComparison.OrdinalIgnoreCase) && !prefix.Contains("b", StringComparison.OrdinalIgnoreCase);
            var text = literal ? match.Groups["text"].Value : match.Value;
            if (literal && !prefix.Contains("r", StringComparison.OrdinalIgnoreCase)) text = Unescape(text);
            return new Token(literal ? "literal" : match.Groups["identifier"].Success ? "identifier" : "symbol", text, match.Index, match.Index + match.Length, [])
                { OffsetStart = match.Index, OffsetEnd = match.Index + match.Length };
        }).ToArray();
        var pairs = MatchingPairs.Find(tokens);
        var calls = new List<(int, string)>();
        void Add(Token token) => calls.Add((SourceCheck.Line(source, token.OffsetStart), token.Value));
        var tables = new Dictionary<string, Token[]>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < tokens.Length; i++)
            if (tokens[i].Kind == "identifier" && Indent(source, tokens[i]) == 0 && tokens[i + 1].Value == "=" && tokens[i + 2].Kind == "symbol" && tokens[i + 2].Value is "[" or "(" or "{")
                tables[tokens[i].Value] = Elements(tokens, pairs, i + 2).Where(element => element.Length == 1 && element[0].Kind == "literal").Select(element => element[0]).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (i + 2 < tokens.Length && token.Kind == "identifier" && helper(token.Value) && tokens[i + 1].Value == "(")
            {
                var argument = Elements(tokens, pairs, i + 1).FirstOrDefault();
                if (argument is { Length: > 0 } && argument.All(part => part.Kind == "literal"))
                {
                    var name = string.Concat(argument.Select(part => part.Value));
                    if (toolLiteral(name)) Add(argument[0] with { Value = name });
                }
            }
            if (token.Kind == "identifier" && token.Value == "for") AddDispatchedTable(source, tokens, i, tables, helper, Add);
            if (token.Kind == "symbol" && token.Value == "{")
            {
                var fields = Elements(tokens, pairs, i).Where(element => element.Length >= 3 && element[0].Kind == "literal" && element[1].Value == ":").ToArray();
                foreach (var (key, args) in new[] { ("tool", "args"), ("name", "arguments") })
                    if (fields.Any(field => field[0].Value == args))
                        foreach (var field in fields.Where(field => field[0].Value == key && field.Length == 3 && field[2].Kind == "literal")) Add(field[2]);
                if (i >= 2 && tokens[i - 1].Value == "=" && tokens[i - 2].Value == "calls")
                    foreach (var field in fields.Where(field => field[0].Value != "profiles" && field[2].Kind == "symbol" && field[2].Value == "{")) Add(field[0]);
            }
            // A call's argument list and a subscript are not Python case tuples/lists.
            if (token.Kind != "symbol" || token.Value is not ("(" or "[") || i > 0 && (tokens[i - 1].Kind == "identifier" && tokens[i - 1].Value is not ("in" or "return") || tokens[i - 1].Value is ")" or "]")) continue;
            var elements = Elements(tokens, pairs, i);
            if (elements.Length < 3 || !elements.Skip(2).Any(element => element[0].Kind == "symbol" && element[0].Value == "{" || element.Length > 1 && element[0].Kind == "identifier" && element[1].Value == "(")) continue;
            foreach (var element in elements.Take(2).Where(element => element.Length == 1 && element[0].Kind == "literal"))
                if (Regex.IsMatch(element[0].Value, @"^[A-Za-z_][A-Za-z0-9_]*$") && (char.IsUpper(element[0].Value[0]) || element[0].Value.StartsWith("__", StringComparison.Ordinal))) Add(element[0]);
        }
        return calls;
    }

    private static string Unescape(string text) => Regex.Replace(text, @"\\(?:x([0-9a-fA-F]{2})|u([0-9a-fA-F]{4})|U([0-9a-fA-F]{8})|([0-7]{1,3})|(\r?\n)|(.))", match =>
    {
        var hex = match.Groups.Cast<Group>().Skip(1).Take(3).FirstOrDefault(group => group.Success);
        if (hex != null)
        {
            var code = Convert.ToInt32(hex.Value, 16);
            return code <= 0xffff ? ((char)code).ToString() : char.ConvertFromUtf32(code);
        }
        if (match.Groups[4].Success) return ((char)Convert.ToInt32(match.Groups[4].Value, 8)).ToString();
        if (match.Groups[5].Success) return "";
        return match.Groups[6].Value switch { "a" => "\a", "b" => "\b", "f" => "\f", "n" => "\n", "r" => "\r", "t" => "\t", "v" => "\v",
            "\\" => "\\", "'" => "'", "\"" => "\"", _ => match.Value };
    });

    private static Token[][] Elements(Token[] tokens, IReadOnlyDictionary<int, int> pairs, int opening)
    {
        var elements = new List<Token[]>();
        var start = opening + 1;
        var end = pairs[opening];
        for (var index = start; index <= end; index++)
        {
            if (index == end || tokens[index].Kind == "symbol" && tokens[index].Value == ",")
            {
                if (index > start) elements.Add(tokens[start..index]);
                start = index + 1;
            }
            else if (pairs.TryGetValue(index, out var closing)) index = closing;
        }
        return elements.ToArray();
    }

    private static int Indent(string source, Token token)
    {
        var start = source.LastIndexOf('\n', token.OffsetStart) + 1;
        var end = start;
        while (end < source.Length && source[end] is ' ' or '\t') end++;
        return end - start;
    }

    private static void AddDispatchedTable(string source, Token[] tokens, int index, Dictionary<string, Token[]> tables, Func<string, bool> helper, Action<Token> add)
    {
        if (index + 4 >= tokens.Length || tokens[index + 1].Kind != "identifier" || tokens[index + 2].Value != "in" || tokens[index + 4].Value != ":" || !tables.TryGetValue(tokens[index + 3].Value, out var table)) return;
        var variable = tokens[index + 1].Value;
        var line = SourceCheck.Line(source, tokens[index].OffsetStart);
        var indent = Indent(source, tokens[index]);
        for (var next = index + 5; next + 2 < tokens.Length; next++)
        {
            if (SourceCheck.Line(source, tokens[next].OffsetStart) > line && Indent(source, tokens[next]) <= indent) break;
            if (!helper(tokens[next].Value) || tokens[next + 1].Value != "(" || tokens[next + 2].Value != variable) continue;
            foreach (var item in table) add(item);
            break;
        }
    }
}
