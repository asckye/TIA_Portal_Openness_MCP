using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class PlcDocumentationLogic
    {

        public sealed class LintFinding
        {
            public string Rule = "";
            public string Severity = "";   // error | warning | info
            public int Line;
            public string Message = "";
            public string Text = "";
        }

        public sealed class LintOptions
        {
            public HashSet<string> Disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int MaxLineLength = 120;
            public int MaxNesting = 5;
            public string[] Markers = OfflineAnalysisLogic.DefaultMarkers.ToArray();
        }

        public static LintOptions ParseLintOptions(string? rulesJson)
        {
            var o = new LintOptions();
            if (string.IsNullOrWhiteSpace(rulesJson)) return o;
            JsonNode? node;
            try { node = JsonNode.Parse(rulesJson!); }
            catch (JsonException ex) { throw new ArgumentException("rulesJson is not valid JSON: " + ex.Message); }
            if (!(node is JsonObject obj))
                throw new ArgumentException("rulesJson must be a JSON object, e.g. {\"disable\":[\"SCL007\"],\"maxLineLength\":120,\"maxNesting\":5}.");
            if (obj["disable"] is JsonArray dis) foreach (var d in dis) if (d != null) o.Disabled.Add(d.ToString());
            if (obj["maxLineLength"] != null) o.MaxLineLength = obj["maxLineLength"]!.GetValue<int>();
            if (obj["maxNesting"] != null) o.MaxNesting = obj["maxNesting"]!.GetValue<int>();
            if (obj["markers"] is JsonArray mk) o.Markers = mk.Select(m => m?.ToString() ?? "").Where(m => m.Length > 0).ToArray();
            if (o.MaxLineLength < 40 || o.MaxLineLength > 1000) throw new ArgumentException("maxLineLength must be between 40 and 1000.");
            if (o.MaxNesting < 1 || o.MaxNesting > 50) throw new ArgumentException("maxNesting must be between 1 and 50.");
            return o;
        }

        public static List<LintFinding> LintScl(string source, LintOptions? options = null)
        {
            options ??= new LintOptions();
            var findings = new List<LintFinding>();
            var lines = (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            void Add(string rule, string severity, int line, string message, string text)
            {
                if (options.Disabled.Contains(rule)) return;
                findings
                    .Add(new LintFinding {
                        Rule = rule, Severity = severity, Line = line, Message = message, Text = text.Length > 200 ? text.Substring(0, 200) : text });
            }

            var stack = new Stack<(string keyword, int line)>();
            var inBlockComment = false;
            var paren = 0; var bracket = 0; var parenLine = 0;
            var nesting = 0; var reportedNesting = false;
            string? previousCode = null; var previousLine = 0;
            var whitespaceLines = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var lineNo = i + 1;
                if (raw.Length > options.MaxLineLength)
                    Add("SCL007", "info", lineNo, "Line longer than " + options.MaxLineLength + " characters (" + raw.Length + ").", raw);
                if (raw.Contains('\t') || raw.Length > 0 && char.IsWhiteSpace(raw[raw.Length - 1])) whitespaceLines++;

                var stripped = OfflineAnalysisLogic.StripComments(raw, ref inBlockComment, out var comment);
                if (comment.Length > 0)
                    foreach (var marker in options.Markers)
                        if (Regex.IsMatch(comment, @"\b" + Regex.Escape(marker) + @"\b")) {
                            Add("SCL010", "info", lineNo, "Annotation marker '" + marker + "' in comment.", comment.Trim()); break; }
                var code = StripStrings(stripped);
                if (code.Trim().Length == 0) continue;

                foreach (var ch in code)
                {
                    if (ch == '(') { if (paren == 0) parenLine = lineNo; paren++; }
                    else if (ch == ')') {
                        paren--; if (paren < 0) { Add("SCL002", "error", lineNo, "Closing ')' without opening parenthesis.", raw.Trim()); paren = 0; } }
                    else if (ch == '[') bracket++;
                    else if (ch == ']') {
                        bracket--; if (bracket < 0) { Add("SCL002", "error", lineNo, "Closing ']' without opening bracket.", raw.Trim()); bracket = 0; } }
                }
                if (code.Contains(";;")) Add("SCL009", "info", lineNo, "Empty statement ';;'.", raw.Trim());

                var words = WordRegex.Matches(code).Cast<Match>().Select(m => m.Value).ToList();
                var firstWord = words.Count > 0 ? words[0].ToUpperInvariant() : "";
                foreach (var w in words)
                {
                    var upper = w.ToUpperInvariant();
                    if (upper == "GOTO") Add("SCL005", "warning", lineNo, "GOTO makes control flow hard to review; prefer structured statements.", raw.Trim());
                    if (BlockOpeners.TryGetValue(upper, out var closer))
                    {
                        // "VAR CONSTANT" / "VAR RETAIN" is still one VAR block; "TYPE" only opens at statement start.
                        if (upper == "TYPE" && firstWord != "TYPE") continue;
                        stack.Push((upper, lineNo));
                        if (upper == "IF" || upper == "CASE" || upper == "FOR" || upper == "WHILE" || upper == "REPEAT")
                        {
                            nesting++;
                            if (nesting > options.MaxNesting && !reportedNesting) {
                                reportedNesting = true;
                                Add("SCL006", "info", lineNo, "Nesting depth " + nesting + " exceeds " + options.MaxNesting + ".", raw.Trim());
                            }
                        }
                        continue;
                    }
                    if (Closers.Contains(upper))
                    {
                        if (stack.Count == 0) { Add("SCL001", "error", lineNo, upper + " without a matching opener.", raw.Trim()); continue; }
                        var top = stack.Peek();
                        var expected = BlockOpeners[top.keyword];
                        if (!expected.Equals(upper, StringComparison.OrdinalIgnoreCase))
                        {
                            Add("SCL001", "error", lineNo, upper + " closes " + top.keyword + " opened at line " + top.line + " (expected " + expected + ").",
                                    raw.Trim());
                            // Recover: unwind to the nearest matching opener if any.
                            var unwound = stack.ToList();
                            var idx = unwound.FindIndex(s => BlockOpeners[s.keyword].Equals(upper, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0) for (var k = 0; k <= idx; k++) { var popped = stack.Pop(); if (IsNestingKeyword(popped.keyword)) nesting--; }
                            continue;
                        }
                        stack.Pop();
                        if (IsNestingKeyword(top.keyword)) nesting--;
                    }
                }

                if (firstWord == "WHILE" && Regex.IsMatch(code, @"\bWHILE\s+TRUE\b", RegexOptions.IgnoreCase))
                    Add("SCL011", "warning", lineNo, "WHILE TRUE loop; make sure an EXIT/RETURN condition exists (cycle time!).", raw.Trim());
                if (firstWord == "REGION" && words.Count == 1 && code.Trim().Equals("REGION", StringComparison.OrdinalIgnoreCase))
                    Add("SCL012", "info", lineNo, "REGION without a name.", raw.Trim());

                if (AssignmentSuspect.IsMatch(code) && !StatementKeywords.Contains(firstWord) && paren == 0 && !code.Contains(":=") && !Regex
                        .IsMatch(code, @"[<>]=|<>|=>"))
                    Add("SCL003", "warning", lineNo, "'=' at statement level; SCL assignment is ':=' (comparison '=' is only valid inside an expression).", raw
                            .Trim());

                var trimmed = code.Trim();
                var startsCloser = firstWord == "ELSE" || firstWord == "ELSIF" || firstWord == "UNTIL" || firstWord
                    .StartsWith("END_", StringComparison.Ordinal);
                if (previousCode != null && startsCloser && !previousCode.EndsWith(";", StringComparison.Ordinal))
                {
                    var prevWords = WordRegex.Matches(previousCode).Cast<Match>().Select(m => m.Value.ToUpperInvariant()).ToList();
                    var prevLast = prevWords.Count > 0 ? prevWords[prevWords.Count - 1] : "";
                    var prevFirst = prevWords.Count > 0 ? prevWords[0] : "";
                    var prevIsHeader = LineEndingKeywords.Contains(prevLast) || prevFirst.StartsWith("END_", StringComparison.Ordinal) || prevFirst == "ELSE"
                        || prevFirst == "REGION" || prevFirst == "CASE" || Regex.IsMatch(previousCode.Trim(), @"^[\w#""'\.\[\]\s,]+:$");
                    if (!prevIsHeader && !previousCode.Trim().EndsWith(":", StringComparison.Ordinal))
                        Add("SCL004", "warning", previousLine, "Statement before " + firstWord + " does not end with ';'.", lines[previousLine - 1].Trim());
                }
                previousCode = trimmed; previousLine = lineNo;
            }
            foreach (var open in stack.Reverse())
                Add("SCL001", "error", open.line, open.keyword + " opened here is never closed (missing " + BlockOpeners[open.keyword] + ").",
                        lines[open.line - 1].Trim());
            if (paren > 0)
                Add("SCL002", "error", parenLine, paren + " '(' never closed (first unclosed at line " + parenLine + ").", lines[Math.Max(0, parenLine - 1)]
                    .Trim());
            if (bracket > 0) Add("SCL002", "error", lines.Length, bracket + " '[' never closed.", "");
            if (whitespaceLines > 0) Add("SCL008", "info", 0, whitespaceLines + " line(s) with tab characters or trailing whitespace.", "");
            if (inBlockComment) Add("SCL013", "error", lines.Length, "Block comment '(*' is never closed.", "");
            return findings.OrderBy(f => f.Line).ThenBy(f => f.Rule, StringComparer.Ordinal).ToList();
        }

        private static bool IsNestingKeyword(string keyword) => keyword == "IF" || keyword == "CASE" || keyword == "FOR" || keyword == "WHILE"
            || keyword == "REPEAT";

        // Replaces the contents of '...' string literals (and "..." symbol names) so quotes/keywords inside do not count.
        public static string StripStrings(string code)
        {
            var sb = new StringBuilder(code.Length);
            char? quote = null;
            foreach (var ch in code)
            {
                if (quote == null)
                {
                    if (ch == '\'' || ch == '"') { quote = ch; sb.Append(ch); continue; }
                    sb.Append(ch);
                }
                else
                {
                    if (ch == quote) { quote = null; sb.Append(ch); continue; }
                    sb
                        .Append(ch == '"' ? 'x' : ch == '\'' ? 'x' : char.IsLetterOrDigit(ch) ? 'x' : ch == '(' || ch == ')' || ch == '[' || ch == ']'
                            || ch == ';' || ch == '=' ? 'x' : ch);
                }
            }
            return sb.ToString();
        }

        public static JsonObject FindingJson(LintFinding f) => new JsonObject
        {
            ["rule"] = f.Rule, ["severity"] = f.Severity, ["line"] = f.Line, ["message"] = f.Message, ["text"] = f.Text
        };

        public static JsonObject RuleCatalog() => new JsonObject
        {
            ["SCL001"] = "error: block keyword pairing (IF/END_IF, CASE, FOR, WHILE, REPEAT, REGION, FUNCTION..., VAR/END_VAR, STRUCT, TYPE)",
            ["SCL002"] = "error: unbalanced parentheses/brackets",
            ["SCL003"] = "warning: '=' at statement level where ':=' is expected",
            ["SCL004"] = "warning: statement before ELSE/ELSIF/UNTIL/END_* without ';'",
            ["SCL005"] = "warning: GOTO used",
            ["SCL006"] = "info: nesting depth above maxNesting",
            ["SCL007"] = "info: line longer than maxLineLength",
            ["SCL008"] = "info: tabs or trailing whitespace (aggregated)",
            ["SCL009"] = "info: empty statement ';;'",
            ["SCL010"] = "info: annotation marker (TODO/FIXME/...) in a comment",
            ["SCL011"] = "warning: WHILE TRUE loop",
            ["SCL012"] = "info: REGION without a name",
            ["SCL013"] = "error: unterminated block comment"
        };
    }
}
