using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class EnvelopeRewrite
{
    internal static readonly string[] Templates = ["Basic/explicit-clock", "Unstamped/verdict", "Unstamped/fields"];
    internal static readonly string[] Clocks = ["DateTime.Now", "DateTime.UtcNow", "ResponseClock.Now", "ResponseClock.UtcNow"];
    internal sealed record Rewrite(int Start, int End, string Template, string Replacement);
    private static Token[] Tokens(string source)
    {
        var (code, comments) = new CSharpLexer(source).Scan();
        return code.Concat(comments).OrderBy(t => t.OffsetStart).ToArray();
    }
    private static string[] Spellings(string source, IEnumerable<Token> tokens) => tokens.Select(t => source[t.OffsetStart..t.OffsetEnd]).ToArray();
    internal static bool Constant(string source)
    {
        var items = Tokens(source);
        if (items.Length == 2 && items[0].Value is "+" or "-" && items[1].Kind == "number") return true;
        return items.Length == 1 && (items[0].Kind == "number" || items[0].Value is "true" or "false" or "null" || items[0].Kind == "literal" && items[0].Expressions.Count == 0);
    }
    private static (string Clean, List<(int Start, int Length)> Removed) AnnotationFree(string source)
    {
        var clean = "";
        var previous = 0;
        var removed = new List<(int, int)>();
        foreach (var token in Tokens(source))
        {
            if (token.Kind != "comment" || !Regex.IsMatch(token.Value, @"\A// envelope: legacy-[a-z0-9]+(?:-[a-z0-9]+)*\z")) continue;
            var start = token.OffsetStart == 0 ? 0 : source.LastIndexOf('\n', token.OffsetStart - 1) + 1;
            var end = source.IndexOf('\n', token.OffsetEnd);
            end = end < 0 ? source.Length : end + 1;
            if (!string.IsNullOrWhiteSpace(source[start..token.OffsetStart]) || !string.IsNullOrWhiteSpace(source[token.OffsetEnd..end])) continue;
            clean += source[previous..start];
            removed.Add((clean.Length, end - start));
            previous = end;
        }
        return (clean + source[previous..], removed);
    }
    internal static List<Rewrite> Initializers(string source)
    {
        var items = Tokens(source);
        var raw = Spellings(source, items);
        var result = new List<Rewrite>();
        for (var i = 0; i < items.Length - 2; i++)
        {
            if (raw[i] != "new" || raw[i + 1] != "JsonObject") continue;
            var j = i + 2;
            if (j + 1 < raw.Length && raw[j] == "(" && raw[j + 1] == ")") j += 2;
            if (j >= raw.Length || raw[j] != "{") continue;
            j++;
            var fields = new List<(string Key, string Value)>();
            while (j + 4 < items.Length && raw[j] == "[" && raw[j + 2] == "]" && raw[j + 3] == "=")
            {
                var key = raw[j + 1];
                if (items[j + 1].Kind != "literal" || !Regex.IsMatch(key, "\\A\"[A-Za-z][A-Za-z0-9]*\"\\z")) break;
                var start = j + 4;
                j = start;
                var stack = new Stack<string>();
                while (j < items.Length)
                {
                    var value = raw[j];
                    if (stack.Count == 0 && value is "," or "}") break;
                    if (value is "(" or "[" or "{") stack.Push(value);
                    else if (value is ")" or "]" or "}") { if (stack.Count == 0) break; stack.Pop(); }
                    j++;
                }
                if (j == start || j == items.Length) break;
                fields.Add((key, source[items[start].OffsetStart..items[j - 1].OffsetEnd]));
                if (raw[j] == ",") j++; else break;
            }
            if (fields.Count == 0 || j >= items.Length || raw[j] != "}" || items.Skip(i).Take(j - i + 1).Any(t => t.Kind is "comment" or "directive")) continue;
            var keys = fields.Select(f => f.Key).ToArray();
            if (keys.Distinct().Count() != keys.Length) continue;
            string template, method;
            List<string> arguments;
            (string Key, string Value)[] tail;
            if (keys.Length >= 2 && keys[0] == "\"timestamp\"" && keys[1] == "\"success\"")
            {
                var clock = fields[0].Value;
                if (!Clocks.Contains(string.Concat(Spellings(clock, Tokens(clock))))) continue;
                template = Templates[0]; method = "Basic"; arguments = [clock, fields[1].Value]; tail = fields.Skip(2).ToArray();
            }
            else if (keys[0] == "\"success\"" && !keys.Contains("\"timestamp\""))
            { template = fields.Count > 1 ? Templates[2] : Templates[1]; method = "Unstamped"; arguments = [fields[0].Value]; tail = fields.Skip(1).ToArray(); }
            else continue;
            if (tail.Count(f => !Constant(f.Value)) > 1) continue;
            arguments.AddRange(tail.Select(f => $"({f.Key}, {f.Value})"));
            result.Add(new Rewrite(items[i].OffsetStart, items[j].OffsetEnd, template, $"ResponseMeta.{method}(" + string.Join(", ", arguments) + ")"));
        }
        return result;
    }
    internal static (Dictionary<string, int> Counts, int? Offset, string? Nearest) Compare(string old, string current)
    {
        (old, _) = AnnotationFree(old);
        var cleaned = AnnotationFree(current);
        var source = cleaned.Clean;
        var candidates = Initializers(old);
        var tokens = Tokens(source);
        var byStart = tokens.Select((t, i) => (t.OffsetStart, i)).ToDictionary(p => p.OffsetStart, p => p.i);
        var raw = Spellings(source, tokens);
        var counts = new Dictionary<string, int>();
        var before = 0; var after = 0;
        (Dictionary<string, int>, int?, string?) Failure(int position) => (counts, position + cleaned.Removed.Where(r => r.Start <= position).Sum(r => r.Length), candidates.MinBy(c => Math.Abs(c.Start - before))?.Template ?? "none (see " + string.Join(", ", Templates) + ")");
        static int Common(string first, string second)
        { var i = 0; while (i < first.Length && i < second.Length && first[i] == second[i]) i++; return i; }
        foreach (var candidate in candidates)
        {
            if (candidate.Start < before) continue;
            var prefix = old[before..candidate.Start];
            if (!source.AsSpan(after).StartsWith(prefix, StringComparison.Ordinal)) return Failure(after + Common(prefix, source[after..]));
            after += prefix.Length; before = candidate.Start;
            var original = old[candidate.Start..candidate.End];
            if (source.AsSpan(after).StartsWith(original, StringComparison.Ordinal)) after += original.Length;
            else
            {
                var expected = Spellings(candidate.Replacement, Tokens(candidate.Replacement));
                if (!byStart.TryGetValue(after, out var index) || !raw.Skip(index).Take(expected.Length).SequenceEqual(expected)) return Failure(after);
                after = tokens[index + expected.Length - 1].OffsetEnd;
                counts[candidate.Template] = counts.GetValueOrDefault(candidate.Template) + 1;
            }
            before = candidate.End;
        }
        var suffix = old[before..];
        return suffix != source[after..] ? Failure(after + Common(suffix, source[after..])) : (counts, null, null);
    }
    internal static int Run(string root, Options options)
    {
        var revision = options.Get("Base") ?? throw new ReleaseException("-Base is required", 64);
        string Git(params string[] args)
        { var result = ProcessRunner.Run("git", args, root); ProcessRunner.RequireSuccess(result, "Envelope rewrite git"); return result.StandardOutput; }
        var commit = Git("rev-parse", "--verify", "--end-of-options", revision + "^{commit}").Trim();
        var paths = options.All("Path");
        var names = (Git(["diff", "--name-only", "-z", commit, "--", .. paths]) + Git(["ls-files", "--others", "--exclude-standard", "-z", "--", .. paths])).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var errors = 0; var rewrites = 0;
        foreach (var name in names)
        {
            try
            {
                var old = Git("show", commit + ":" + name);
                var current = File.ReadAllText(Path.Combine(root, name), new System.Text.UTF8Encoding(false, true));
                var result = Compare(old, current);
                if (result.Offset is { } offset) { errors++; Console.WriteLine($"FAIL {name}:{SourceCheck.Line(current, offset)}: unknown rewrite; nearest template: {result.Nearest}"); }
                else { rewrites += result.Counts.Values.Sum(); Console.WriteLine($"PASS {name}: " + string.Join(", ", result.Counts.Select(p => $"{p.Key}={p.Value}"))); }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ReleaseException) { errors++; Console.WriteLine($"FAIL {name}:1: {ex.Message}; nearest template: none"); }
        }
        Console.WriteLine($"COMPLETE: {names.Length - errors} files passed; {errors} failed; {rewrites} template rewrites");
        return errors == 0 ? 0 : 1;
    }
}
