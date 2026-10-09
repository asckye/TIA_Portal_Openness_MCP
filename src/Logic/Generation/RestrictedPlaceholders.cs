using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    internal static class RestrictedPlaceholders
    {
        internal static void Validate(string template, string path, Action<string, string, string> error, Func<string, bool>? namingExists = null)
        {
            var position = 0;
            while (position < template.Length)
            {
                var open = template.IndexOf("{{", position, StringComparison.Ordinal);
                var stray = template.IndexOf("}}", position, StringComparison.Ordinal);
                if (open < 0)
                {
                    if (stray >= 0) error(path, "placeholder", "Unmatched placeholder delimiter.");
                    return;
                }
                if (stray >= 0 && stray < open) { error(path, "placeholder", "Unmatched placeholder delimiter."); return; }
                var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { error(path, "placeholder", "Unmatched placeholder delimiter."); return; }
                var expression = template.Substring(open + 2, close - open - 2);
                var parser = new Parser(expression, namingExists);
                if (!parser.Read()) error(path, "placeholder", "Only field paths and approved built-in functions are allowed.");
                position = close + 2;
            }
        }

        private sealed class Parser
        {
            private readonly string text;
            private readonly Func<string, bool>? namingExists;
            private int index;
            internal Parser(string text, Func<string, bool>? namingExists) { this.text = text; this.namingExists = namingExists; }
            internal bool Read() { var valid = Value(0); Space(); return valid && index == text.Length; }
            private void Space() { while (index < text.Length && char.IsWhiteSpace(text[index])) index++; }
            private bool Value(int depth)
            {
                Space();
                if (depth > 8 || index >= text.Length) return false;
                if (text[index] == '\'' || text[index] == '"')
                {
                    var quote = text[index++];
                    while (index < text.Length && text[index] != quote)
                    { if (text[index] < 32 || text[index] == '\\' || text[index] == '{' || text[index] == '}') return false; index++; }
                    return index < text.Length && text[index++] == quote;
                }
                if (char.IsDigit(text[index]))
                {
                    var start = index;
                    while (index < text.Length && char.IsDigit(text[index])) index++;
                    return int.TryParse(text.Substring(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out _);
                }
                var match = Regex.Match(text.Substring(index), @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.CultureInvariant);
                if (!match.Success) return false;
                var name = match.Value;
                index += match.Length;
                Space();
                if (index >= text.Length || text[index] != '(') return true;
                var arity = name switch
                {
                    "tag" or "alloc.io" or "alloc.ip" or "upper" or "lower" or "text" => 1,
                    "pad" => 2,
                    "seq" => -3,
                    _ => name.StartsWith("naming.", StringComparison.Ordinal) && name.Split('.').Length == 2
                        && (namingExists == null || namingExists(name.Substring(7))) ? -1 : -2
                };
                if (arity == -2) return false;
                index++;
                Space();
                var count = 0;
                if (index < text.Length && text[index] != ')')
                {
                    do
                    {
                        if (!Value(depth + 1)) return false;
                        count++;
                        Space();
                        if (index >= text.Length || text[index] != ',') break;
                        index++;
                    } while (count < 8);
                }
                if (index >= text.Length || text[index++] != ')') return false;
                return arity == -1 ? count >= 1 && count <= 8 : arity == -3 ? count <= 1 : count == arity;
            }
        }
    }
}
