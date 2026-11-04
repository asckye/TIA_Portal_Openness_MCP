using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class OfflineAnalysisLogic
    {

        private static BlockDocument ParseTextSource(string text, Dictionary<string, string> resources, string format)
        {
            var doc = new BlockDocument { Format = format };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var inBlockComment = false;
            var declared = false;
            string? section = null;
            var structPrefix = new List<string>();
            NetworkInfo? current = null;
            var implicitBody = false;
            var pendingAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var attributeBuffer = new StringBuilder();
            var inAttributeBlock = false;
            var networkIndex = 0;
            var codeLines = new List<string>();
            var canonical = new List<string>();

            string Resolve(string value)
            {
                var v = value.Trim().Trim('"', '\'');
                return MlcRegex.IsMatch(v) ? MlcRegex.Replace(v, m => resources.TryGetValue(m.Value, out var r) ? r : "<mlc>") : v;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd();
                var code = StripComments(raw, ref inBlockComment, out var comment);
                var codeTrim = code.Trim();
                var hasComment = comment.Trim().Length > 0;

                var canonicalLine = MlcRegex.Replace(raw.Trim(), m => resources.TryGetValue(m.Value, out var r) ? "\"" + r + "\"" : "<mlc>");
                if (canonicalLine.Length > 0) canonical.Add(Mask(canonicalLine));

                if (current != null && (hasComment || codeTrim.Length > 0))
                {
                    if (!codeTrim.StartsWith("END_NETWORK", StringComparison.OrdinalIgnoreCase)
                        && !(implicitBody
                                && (codeTrim.StartsWith("END_FUNCTION", StringComparison.OrdinalIgnoreCase) || codeTrim
                                    .StartsWith("END_ORGANIZATION", StringComparison.OrdinalIgnoreCase) || codeTrim
                                    .StartsWith("END_DATA_BLOCK", StringComparison.OrdinalIgnoreCase))))
                    {
                        current.SourceLines.Add(raw.Trim());
                        doc.SourceLines++;
                        if (hasComment) doc.CommentLines++;
                        if (codeTrim.Length > 0 && !codeTrim.StartsWith("{", StringComparison.Ordinal)) codeLines.Add(codeTrim);
                    }
                }

                if (inAttributeBlock)
                {
                    attributeBuffer.Append(' ').Append(code);
                    if (code.IndexOf('}') >= 0)
                    {
                        inAttributeBlock = false;
                        ApplyAttributeBlock(attributeBuffer.ToString(), doc, pendingAttributes, declared, current != null, Resolve);
                        attributeBuffer.Clear();
                    }
                    continue;
                }
                if (codeTrim.StartsWith("{", StringComparison.Ordinal))
                {
                    if (codeTrim.IndexOf('}') >= 0) ApplyAttributeBlock(codeTrim, doc, pendingAttributes, declared, current != null, Resolve);
                    else { inAttributeBlock = true; attributeBuffer.Append(codeTrim); }
                    continue;
                }
                if (codeTrim.Length == 0) continue;

                if (!declared)
                {
                    var decl = DeclarationRegex.Match(codeTrim);
                    if (decl.Success)
                    {
                        declared = true;
                        doc.BlockName = decl.Groups[2].Value;
                        doc.BlockType = decl.Groups[1].Value.ToUpperInvariant() switch
                        {
                            "FUNCTION_BLOCK" => "FB", "FUNCTION" => "FC", "ORGANIZATION_BLOCK" => "OB", "DATA_BLOCK" => "GlobalDB", "TYPE" => "PlcStruct", _ =>
                                decl.Groups[1].Value
                        };
                        if (doc.BlockType == "FC" && decl.Groups[4].Success)
                            doc.Members.Add(new InterfaceMember { Section = "Return", Path = "Ret_Val", Datatype = decl.Groups[4].Value.Trim() });
                    }
                    continue;
                }

                if (codeTrim.StartsWith("VERSION", StringComparison.OrdinalIgnoreCase) && codeTrim.IndexOf(':') > 0 && section == null && current == null)
                {
                    doc.Attributes["S7_Version"] = codeTrim.Substring(codeTrim.IndexOf(':') + 1).Trim().TrimEnd(';').Trim();
                    continue;
                }

                var upper = codeTrim.ToUpperInvariant();
                if (current == null)
                {
                    var newSection = SectionFor(upper, doc.BlockType);
                    if (newSection != null) { section = newSection; structPrefix.Clear(); continue; }
                    if (upper.StartsWith("END_VAR", StringComparison.Ordinal)
                            || (upper.StartsWith("END_STRUCT", StringComparison.Ordinal) && structPrefix.Count == 0))
                        { section = null; continue; }
                    if (upper.StartsWith("END_STRUCT", StringComparison.Ordinal) && structPrefix.Count > 0) {
                        structPrefix.RemoveAt(structPrefix.Count - 1); continue; }
                    if (section != null)
                    {
                        var m = MemberRegex.Match(codeTrim);
                        if (m.Success)
                        {
                            var name = m.Groups[1].Value.Trim('"');
                            var typeText = m.Groups[3].Value;
                            var assign = typeText.IndexOf(":=", StringComparison.Ordinal);
                            if (assign >= 0) typeText = typeText.Substring(0, assign);
                            typeText = typeText.Trim().TrimEnd(';').Trim();
                            var path = structPrefix.Count == 0 ? name : string.Join(".", structPrefix) + "." + name;
                            doc.Members.Add(new InterfaceMember { Section = section, Path = path, Datatype = typeText });
                            if (typeText.Equals("Struct", StringComparison.OrdinalIgnoreCase) || typeText
                                    .EndsWith(" of Struct", StringComparison.OrdinalIgnoreCase))
                                structPrefix.Add(name);
                        }
                        continue;
                    }
                    if (upper == "BEGIN")
                    {
                        // .scl bodies without NETWORK keywords are treated as one implicit network.
                        implicitBody = true;
                        current = new NetworkInfo { Index = ++networkIndex, Language = "SCL" };
                        doc.Networks.Add(current);
                        continue;
                    }
                }

                if (upper == "NETWORK" || upper.StartsWith("NETWORK ", StringComparison.Ordinal))
                {
                    if (implicitBody && current != null && current.SourceLines.Count == 0) {
                        doc.Networks.Remove(current); networkIndex--; implicitBody = false; }
                    current = new NetworkInfo { Index = ++networkIndex };
                    pendingAttributes.TryGetValue("S7_NetworkTitle", out var title);
                    pendingAttributes.TryGetValue("S7_NetworkComment", out var netComment);
                    pendingAttributes.TryGetValue("S7_Language", out var language);
                    current.Title = title ?? ""; current.Comment = netComment ?? ""; current.Language = language ?? "";
                    pendingAttributes.Clear();
                    doc.Networks.Add(current);
                    continue;
                }
                if (upper.StartsWith("END_NETWORK", StringComparison.Ordinal)) { current = null; continue; }
                if (implicitBody
                        && (upper.StartsWith("END_FUNCTION", StringComparison.Ordinal) || upper.StartsWith("END_ORGANIZATION", StringComparison.Ordinal)
                            || upper.StartsWith("END_DATA_BLOCK", StringComparison.Ordinal)))
                    { current = null; implicitBody = false; }
            }

            if (!declared) doc.Warnings.Add("No FUNCTION/FUNCTION_BLOCK/ORGANIZATION_BLOCK/DATA_BLOCK/TYPE declaration found.");
            if (doc.Attributes.TryGetValue("S7_BlockTitle", out var blockTitle)) { doc.Title = blockTitle; doc.Attributes.Remove("S7_BlockTitle"); }
            if (doc.Attributes.TryGetValue("S7_BlockComment", out var blockComment)) { doc.Comment = blockComment; doc.Attributes.Remove("S7_BlockComment"); }
            if (doc.Attributes.TryGetValue("S7_PreferredLanguage", out var preferred)) doc.Language = preferred;
            else if (format == "SCL") doc.Language = "SCL";
            else
            {
                var languages = doc.Networks.Select(n => n.Language).Where(l => l.Length > 0).Distinct().ToList();
                doc.Language = languages.Count == 1 ? languages[0] : languages.Count > 1 ? "Mixed" : "";
            }
            foreach (var n in doc.Networks.Where(n => n.Language.Length == 0)) n.Language = doc.Language;

            var sclCode = doc.Format == "SCL" || doc.Networks.Any(n => n.Language.Equals("SCL", StringComparison.OrdinalIgnoreCase));
            if (sclCode) doc.MaxNesting = MaxNesting(codeLines);
            CollectTextCalls(codeLines, doc);
            doc.Canonical = canonical;
            return doc;
        }

        private static string? SectionFor(string upper, string blockType)
        {
            if (upper.StartsWith("VAR_INPUT", StringComparison.Ordinal)) return "Input";
            if (upper.StartsWith("VAR_OUTPUT", StringComparison.Ordinal)) return "Output";
            if (upper.StartsWith("VAR_IN_OUT", StringComparison.Ordinal)) return "InOut";
            if (upper.StartsWith("VAR_TEMP", StringComparison.Ordinal)) return "Temp";
            if (upper.StartsWith("VAR CONSTANT", StringComparison.Ordinal) || upper.StartsWith("VAR_CONSTANT", StringComparison.Ordinal)) return "Constant";
            if (upper == "VAR" || upper.StartsWith("VAR ", StringComparison.Ordinal)) return "Static";
            if (upper == "STRUCT") return blockType == "PlcStruct" ? "None" : "Static";
            return null;
        }

        private static void ApplyAttributeBlock(string block, BlockDocument doc, Dictionary<string, string> pending, bool declared, bool insideNetwork,
                Func<string, string> resolve)
        {
            if (insideNetwork) return; // S7_Templates etc. inside rungs are part of the source, not metadata
            foreach (Match m in AttributePairRegex.Matches(block))
            {
                var key = m.Groups[1].Value;
                var value = resolve(m.Groups[2].Value);
                if (key == "S7_Language" || key == "S7_NetworkTitle" || key == "S7_NetworkComment") pending[key] = value;
                else if (key == "S7_Optimized_Access") doc.Attributes["S7_Optimized"] = value;
                else doc.Attributes[key] = value;
            }
        }

        // Splits a source line into code and comment parts; handles // and multi-line (* *) and skips string literals.
        public static string StripComments(string line, ref bool inBlockComment, out string comment)
        {
            var code = new StringBuilder();
            var commentText = new StringBuilder();
            var i = 0;
            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*)", i, StringComparison.Ordinal);
                    if (end < 0) { commentText.Append(line, i, line.Length - i); i = line.Length; }
                    else { commentText.Append(line, i, end - i).Append(' '); i = end + 2; inBlockComment = false; }
                    continue;
                }
                var c = line[i];
                if (c == '\'' )
                {
                    var close = line.IndexOf('\'', i + 1);
                    if (close < 0) close = line.Length - 1;
                    code.Append(line, i, close - i + 1); i = close + 1; continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { commentText.Append(line, i + 2, line.Length - i - 2); break; }
                if (c == '(' && i + 1 < line.Length && line[i + 1] == '*') { inBlockComment = true; i += 2; continue; }
                code.Append(c); i++;
            }
            comment = commentText.ToString();
            return code.ToString();
        }

        private static string StripCommentsSimple(string line)
        {
            var dummy = false;
            return StripComments(line, ref dummy, out _);
        }

        public static int MaxNesting(IEnumerable<string> codeLines)
        {
            var depth = 0; var max = 0;
            foreach (var line in codeLines)
            {
                depth += NestOpenRegex.Matches(line).Count;
                if (depth > max) max = depth;
                depth -= NestCloseRegex.Matches(line).Count;
                if (depth < 0) depth = 0;
            }
            return max;
        }

        private static void CollectTextCalls(IEnumerable<string> codeLines, BlockDocument doc)
        {
            foreach (var line in codeLines)
            {
                foreach (Match m in QuotedCallRegex.Matches(line)) doc.BlockCalls.Add("Block:" + m.Groups[1].Value);
                foreach (Match m in InstanceCallRegex.Matches(line))
                    doc.BlockCalls.Add("Instance:" + m.Groups[1].Value + (m.Groups[2].Success ? "." + m.Groups[2].Value : ""));
                foreach (Match m in BareCallRegex.Matches(line))
                {
                    var name = m.Groups[1].Value;
                    if (!SclKeywords.Contains(name)) doc.Instructions.Add(name);
                }
            }
        }

        private static void BuildCanonical(BlockDocument doc)
        {
            var lines = new List<string>
            {
                "[Block] Name=" + doc.BlockName + " Type=" + doc.BlockType + " Language=" + doc.Language
            };
            if (doc.Title.Length > 0) lines.Add("[Title] " + doc.Title);
            if (doc.Comment.Length > 0) lines.Add("[Comment] " + doc.Comment);
            foreach (var kv in doc.Attributes) lines.Add("[Attribute] " + kv.Key + "=" + kv.Value);
            foreach (var m in doc.Members) lines.Add("[Interface] " + m.Section + " " + m.Path + " : " + m.Datatype);
            foreach (var n in doc.Networks)
            {
                lines.Add("[Network " + n.Index + "] Language=" + n.Language);
                if (n.Title.Length > 0) lines.Add("[Network " + n.Index + "] Title=" + n.Title);
                if (n.Comment.Length > 0) lines.Add("[Network " + n.Index + "] Comment=" + n.Comment);
                foreach (var s in n.SourceLines) { var t = s.Trim(); if (t.Length > 0) lines.Add(t); }
            }
            doc.Canonical = MaskLines(lines);
        }

        public static string Mask(string line)
        {
            var masked = GuidRegex.Replace(line, "<guid>");
            masked = TimestampRegex.Replace(masked, "<timestamp>");
            return masked.Length > MaxLineLength ? masked.Substring(0, MaxLineLength) + "…" : masked;
        }

        private static List<string> MaskLines(IEnumerable<string> lines) => lines.Select(Mask).ToList();
    }
}
