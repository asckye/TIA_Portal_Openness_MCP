using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.BuildCommon;

public sealed record Token(string Kind, string Value, int Start, int End, IReadOnlyList<Token> Expressions)
{
    // Public offsets follow Python's Unicode code-point indexing. Source extraction uses UTF-16 offsets.
    public int OffsetStart { get; init; }
    public int OffsetEnd { get; init; }
}

public sealed class CSharpLexer
{
    public static IReadOnlySet<string> SkipDirs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "bin-v20", "obj-v20", "generated", "__pycache__" };
    public static IReadOnlyList<string> GeneratedSuffixes { get; } = [".g.cs", ".g.i.cs", ".generated.cs", ".designer.cs"];
    private static readonly Regex LiteralStart = new(@"\G(\$+@?|@\$?)?(""+)");
    private static readonly Regex Number = new(@"\G(?:0[xX][\da-fA-F_]+|0[bB][01_]+|(?:\d[\d_]*(?:\.\d[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?[\d_]+)?)[uUlLfFdDmM]*");
    private static readonly Regex Space = new(@"\G[\s\u001c-\u001f]+");
    private static readonly Regex Operators = new(@"\G(?:>>>=|>>>|<<=|>>=|\?\?=|=>|\?\.|\?\?|::|\+\+|--|&&|\|\||==|!=|<=|>=|<<|>>|\+=|-=|\*=|/=|%=|&=|\|=|\^=|\.\.)");
    private readonly string source;
    private readonly int[] offsets;
    private int pos;
    public List<Token> AllComments { get; } = [];

    public CSharpLexer(string source)
    {
        this.source = source;
        offsets = new int[source.Length + 1];
        for (var i = 0; i < source.Length; i++)
            offsets[i + 1] = offsets[i] + (char.IsLowSurrogate(source[i]) && i > 0 && char.IsHighSurrogate(source[i - 1]) ? 0 : 1);
    }

    private Token Make(string kind, string value, int start, int end, IReadOnlyList<Token>? expressions = null) =>
        new(kind, value, offsets[start], offsets[end], expressions ?? []) { OffsetStart = start, OffsetEnd = end };
    private bool Starts(string value, int offset) => source.AsSpan(offset).StartsWith(value, StringComparison.Ordinal);
    private void Error(string message) => throw new ArgumentException($"line {source.AsSpan(0, Math.Min(pos, source.Length)).Count('\n') + 1}: {message}");
    private static string Strip(string value) => Regex.Replace(value, @"^[\s\u001c-\u001f]+|[\s\u001c-\u001f]+$", "");

    private Token? Literal()
    {
        var start = pos;
        var match = LiteralStart.Match(source, start);
        string prefix;
        int quotes;
        char quote;
        if (source[start] == '\'') { prefix = ""; quotes = 1; quote = '\''; pos++; }
        else if (match.Success)
        {
            prefix = match.Groups[1].Value;
            quote = '"';
            quotes = match.Groups[2].Length >= 3 && !prefix.Contains('@') ? match.Groups[2].Length : 1;
            pos += prefix.Length + quotes;
        }
        else return null;
        var raw = quotes >= 3;
        var verbatim = prefix.Contains('@');
        var dollars = prefix.Count(c => c == '$');
        var pieces = new List<object?>();
        var expressions = new List<Token>();
        var segment = pos;
        while (pos < source.Length)
        {
            if (Starts(new string(quote, quotes), pos))
            {
                if (verbatim && Starts("\"\"", pos)) { pos += 2; continue; }
                pieces.Add(source[segment..pos]);
                pos += quotes;
                if (Starts("u8", pos) && quote == '"') pos += 2;
                var value = source[start..pos];
                if (dollars != 0) value = PythonJson.Dumps(new object?[] { prefix, quotes, pieces }, ensureAscii: false, compactSeparators: true);
                return Make("literal", value, start, pos, expressions);
            }
            if (!raw && !verbatim && source[pos] == '\\') { pos += 2; continue; }
            if (dollars != 0 && source[pos] == '{')
            {
                if (!raw && Starts("{{", pos)) { pos += 2; continue; }
                var width = raw ? dollars : 1;
                var end = pos;
                while (end < source.Length && source[end] == '{') end++;
                if (end - pos >= width)
                {
                    // Extra opening braces in a raw interpolation are literal text.
                    pos = end - width;
                    pieces.Add(source[segment..pos]);
                    pos = end;
                    var (inner, _) = Scan(width);
                    pieces.Add(inner.Select(t => new[] { t.Kind, t.Value }).ToArray());
                    expressions.AddRange(inner);
                    segment = pos;
                    continue;
                }
                pos = end;
                continue;
            }
            if (!raw && !verbatim && source[pos] is '\r' or '\n') Error("newline in regular string/char literal");
            pos++;
        }
        Error("unterminated literal");
        return null;
    }

    // Python's \w includes all Unicode letters/numbers and underscore, but excludes combining marks.
    private int IdentifierEnd(int start)
    {
        var offset = source[start] == '@' ? start + 1 : start;
        var first = true;
        while (offset < source.Length)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(source, offset);
            var letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
            if (source[offset] != '_' && !letter && (first || category != UnicodeCategory.DecimalDigitNumber)) break;
            offset += char.IsHighSurrogate(source[offset]) && offset + 1 < source.Length && char.IsLowSurrogate(source[offset + 1]) ? 2 : 1;
            first = false;
        }
        return first ? start : offset;
    }

    public (IReadOnlyList<Token> Tokens, IReadOnlyList<Token> Comments) Scan(int interpolation = 0)
    {
        var tokens = new List<Token>();
        var comments = new List<Token>();
        var stack = new List<string>();
        var pairs = new Dictionary<string, string> { ["}"] = "{", [")"] = "(", ["]"] = "[" };
        while (pos < source.Length)
        {
            var start = pos;
            var whitespace = Space.Match(source, start);
            if (whitespace.Success) { pos = whitespace.Index + whitespace.Length; continue; }
            if (Starts("//", start))
            {
                var end = source.IndexOf('\n', start);
                pos = end < 0 ? source.Length : end;
                var comment = Make("comment", source[start..pos], start, pos);
                comments.Add(comment);
                AllComments.Add(comment);
                continue;
            }
            if (Starts("/*", start))
            {
                var end = source.IndexOf("*/", start + 2, StringComparison.Ordinal);
                if (end < 0) Error("unterminated comment");
                pos = end + 2;
                var comment = Make("comment", source[start..pos], start, pos);
                comments.Add(comment);
                AllComments.Add(comment);
                continue;
            }
            var lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
            if (source[start] == '#' && Strip(source[lineStart..start]).Length == 0)
            {
                var end = source.IndexOf('\n', start);
                pos = end < 0 ? source.Length : end;
                var value = Strip(Regex.Replace(source[start..pos].Split("//", 2, StringSplitOptions.None)[0], @"[\s\u001c-\u001f]+", " "));
                tokens.Add(Make("directive", value, start, pos));
                continue;
            }
            if (interpolation != 0 && stack.Count == 0 && Starts(new string('}', interpolation), start))
            { pos += interpolation; return (tokens, comments); }
            if (interpolation != 0 && stack.Count == 0 && source[start] == ':' && !Starts("::", start))
            {
                // Format text is not C# code (e.g. {value:catch "quoted"}).
                var end = source.IndexOf(new string('}', interpolation), start + 1, StringComparison.Ordinal);
                if (end < 0) Error("unterminated interpolation format");
                tokens.Add(Make("literal", source[start..end], start, end));
                pos = end + interpolation;
                return (tokens, comments);
            }
            if ("'\"@$".Contains(source[start]))
            {
                var literal = Literal();
                if (literal is not null) { tokens.Add(literal); continue; }
            }
            var identifier = IdentifierEnd(start);
            var number = char.IsDigit(source[start]) || source[start] == '.' ? Number.Match(source, start) : Match.Empty;
            var op = Operators.Match(source, start);
            string kind;
            if (identifier > start) { kind = "identifier"; pos = identifier; }
            else if (number.Success) { kind = "number"; pos = number.Index + number.Length; }
            else if (op.Success) { kind = "symbol"; pos = op.Index + op.Length; }
            else { kind = "symbol"; pos = start + (char.IsHighSurrogate(source[start]) && start + 1 < source.Length && char.IsLowSurrogate(source[start + 1]) ? 2 : 1); }
            var token = Make(kind, source[start..pos], start, pos);
            tokens.Add(token);
            if (interpolation != 0)
            {
                if (token.Value is "{" or "(" or "[") stack.Add(token.Value);
                else if (pairs.TryGetValue(token.Value, out var opening))
                {
                    if (stack.Count == 0 || stack[^1] != opening) Error("unbalanced interpolation");
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }
        if (interpolation != 0) Error("unterminated interpolation");
        return (tokens, comments);
    }
}

public static class MatchingPairs
{
    private sealed class Branch(List<int[]> initial)
    {
        internal List<int[]> Initial { get; } = initial.ToList();
        internal List<List<int[]>> Ends { get; } = [];
        internal bool HasElse { get; set; }
    }

    public static IReadOnlyDictionary<int, int> Find(IReadOnlyList<Token> tokens)
    {
        var stack = new List<int[]>();
        var matches = new Dictionary<int, int>();
        var branches = new List<Branch>();
        var pairs = new Dictionary<string, string> { ["}"] = "{", [")"] = "(", ["]"] = "[" };
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == "directive")
            {
                var directive = token.Value.Split(' ')[0];
                if (directive == "#if") branches.Add(new Branch(stack));
                else if (directive is "#else" or "#elif")
                {
                    if (branches.Count == 0) throw new ArgumentException("conditional directive without #if");
                    var branch = branches[^1];
                    branch.Ends.Add(stack);
                    branch.HasElse |= directive == "#else";
                    stack = branch.Initial.ToList();
                }
                else if (directive == "#endif")
                {
                    if (branches.Count == 0) throw new ArgumentException("#endif without #if");
                    var branch = branches[^1];
                    branches.RemoveAt(branches.Count - 1);
                    var ends = branch.Ends.Append(stack).ToList();
                    if (!branch.HasElse) ends.Add(branch.Initial);
                    var shape = string.Join(" ", ends[0].Select(group => tokens[group[0]].Value));
                    if (ends.Any(end => string.Join(" ", end.Select(group => tokens[group[0]].Value)) != shape))
                        throw new ArgumentException("conditional branches leave incompatible delimiters");
                    stack = Enumerable.Range(0, stack.Count).Select(level => ends.SelectMany(end => end[level]).Distinct().Order().ToArray()).ToList();
                }
                continue;
            }
            if (token.Kind != "symbol") continue;
            if (token.Value is "{" or "(" or "[") stack.Add([index]);
            else if (pairs.TryGetValue(token.Value, out var opening))
            {
                if (stack.Count == 0 || tokens[stack[^1][0]].Value != opening)
                    throw new ArgumentException($"unbalanced {token.Value} at offset {token.Start}");
                foreach (var item in stack[^1]) matches[item] = index;
                stack.RemoveAt(stack.Count - 1);
            }
        }
        if (branches.Count != 0) throw new ArgumentException("unclosed #if");
        if (stack.Count != 0)
        {
            var token = tokens[stack[^1][0]];
            throw new ArgumentException($"unclosed {token.Value} at offset {token.Start}");
        }
        return matches;
    }
}
