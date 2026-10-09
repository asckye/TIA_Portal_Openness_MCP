using System;
using System.Collections.Generic;
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

        internal static Expression Parse(string expression)
        {
            var parser = new Parser(expression, null);
            if (!parser.Read()) throw CanonicalJson.Failure("", "placeholder", "Only field paths and approved built-in functions are allowed.");
            return parser.Result!;
        }

        internal sealed class Expression
        {
            internal string Name = "";
            internal object? Literal;
            internal List<Expression>? Arguments;
        }

        private sealed class Parser
        {
            private readonly string text;
            private readonly Func<string, bool>? namingExists;
            private int index;
            internal Expression? Result;
            internal Parser(string text, Func<string, bool>? namingExists) { this.text = text; this.namingExists = namingExists; }
            internal bool Read() { var valid = Value(0, out Result); Space(); return valid && index == text.Length; }
            private void Space() { while (index < text.Length && char.IsWhiteSpace(text[index])) index++; }
            private bool Value(int depth, out Expression? result)
            {
                result = null;
                Space();
                if (depth > 8 || index >= text.Length) return false;
                if (text[index] == '\'' || text[index] == '"')
                {
                    var quote = text[index++];
                    var start = index;
                    while (index < text.Length && text[index] != quote)
                    { if (text[index] < 32 || text[index] == '\\' || text[index] == '{' || text[index] == '}') return false; index++; }
                    if (index >= text.Length) return false;
                    result = new Expression { Literal = text.Substring(start, index - start) };
                    return text[index++] == quote;
                }
                if (char.IsDigit(text[index]))
                {
                    var start = index;
                    while (index < text.Length && char.IsDigit(text[index])) index++;
                    if (!int.TryParse(text.Substring(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
                    result = new Expression { Literal = number };
                    return true;
                }
                var match = Regex.Match(text.Substring(index), @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.CultureInvariant);
                if (!match.Success) return false;
                var name = match.Value;
                result = new Expression { Name = name };
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
                result.Arguments = new List<Expression>();
                Space();
                var count = 0;
                if (index < text.Length && text[index] != ')')
                {
                    do
                    {
                        if (!Value(depth + 1, out var argument)) return false;
                        result.Arguments.Add(argument!);
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
