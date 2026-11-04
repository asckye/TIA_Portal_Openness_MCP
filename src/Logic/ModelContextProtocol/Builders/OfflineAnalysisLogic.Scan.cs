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

        // ------------------------------------------------------------------ annotations

        public static string[] ParseMarkers(string markersJson)
        {
            if (string.IsNullOrWhiteSpace(markersJson)) return DefaultMarkers.ToArray();
            JsonNode? node;
            try { node = JsonNode.Parse(markersJson); } catch (JsonException ex) {
                throw new ArgumentException("markersJson must be a JSON string array: " + ex.Message); }
            if (node is not JsonArray array || array.Count == 0)
                throw new ArgumentException("markersJson must be a non-empty JSON string array, e.g. [\"TODO\",\"FIXME\"].");
            var markers = new List<string>();
            foreach (var item in array)
            {
                var value = item?.GetValue<string>()?.Trim() ?? "";
                if (value.Length == 0 || !Regex.IsMatch(value, @"^[A-Za-z0-9_]+$"))
                    throw new ArgumentException("Marker '" + value + "' must be a plain word (letters, digits, underscore).");
                if (!markers.Contains(value, StringComparer.Ordinal)) markers.Add(value);
            }
            return markers.ToArray();
        }

        public static string[] ParseExtensions(string extensionsJson)
        {
            if (string.IsNullOrWhiteSpace(extensionsJson)) return DefaultExtensions.ToArray();
            JsonNode? node;
            try { node = JsonNode.Parse(extensionsJson); } catch (JsonException ex) {
                throw new ArgumentException("extensionsJson must be a JSON string array: " + ex.Message); }
            if (node is not JsonArray array || array.Count == 0)
                throw new ArgumentException("extensionsJson must be a non-empty JSON string array, e.g. [\".xml\",\".s7dcl\"].");
            var list = new List<string>();
            foreach (var item in array)
            {
                var value = item?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
                if (value.Length == 0) throw new ArgumentException("Empty extension in extensionsJson.");
                if (!value.StartsWith(".", StringComparison.Ordinal)) value = "." + value;
                if (!list.Contains(value)) list.Add(value);
            }
            return list.ToArray();
        }

        public static List<string> EnumerateDocuments(string directory, bool recursive, string[] extensions)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new ArgumentException("Absolute directory path required: '" + directory + "'.");
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Directory not found: " + directory);
            return Directory.EnumerateFiles(directory, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<AnnotationRow> ScanFile(string path, string[] markers)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            string? s7res = null;
            var resPath = Path.ChangeExtension(path, ".s7res");
            if (!string.Equals(resPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(resPath)) s7res = File.ReadAllText(resPath, Encoding.UTF8);
            return ScanText(path, text, markers, s7res);
        }

        public static List<AnnotationRow> ScanText(string filePath, string text, string[] markers, string? s7resText = null)
        {
            if (markers == null || markers.Length == 0) throw new ArgumentException("At least one marker is required.");
            var regex = new Regex(@"\b(" + string.Join("|", markers.Select(Regex.Escape)) + @")\b");
            var body = (text ?? "").TrimStart('﻿');
            return LooksLikeXml(body) ? ScanXml(filePath, body, regex) : ScanSource(filePath, body, regex, ReadS7Res(s7resText));
        }

        private static List<AnnotationRow> ScanXml(string filePath, string xml, Regex regex)
        {
            var rows = new List<AnnotationRow>();
            var xdoc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
            var root = xdoc.Root;
            if (root == null) return rows;
            var block = root.Elements().FirstOrDefault(e => Local(e).StartsWith("SW.", StringComparison.Ordinal))
                ?? (Local(root).StartsWith("SW.", StringComparison.Ordinal) ? root : null);
            var blockName = Child(Child(block, "AttributeList"), "Name")?.Value.Trim() ?? "";
            var units = new Dictionary<XElement, int>();
            if (block != null) foreach (var unit in block.Descendants().Where(e => Local(e) == "SW.Blocks.CompileUnit")) units[unit] = units.Count + 1;

            foreach (var textNode in xdoc.DescendantNodes().OfType<XText>())
            {
                var value = textNode.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var parent = textNode.Parent;
                if (parent != null && Local(parent) == "Name") continue;
                foreach (Match m in regex.Matches(value))
                {
                    int? network = null;
                    var kind = "xmlText";
                    for (var e = parent; e != null; e = e.Parent)
                    {
                        var local = Local(e);
                        if (kind == "xmlText")
                        {
                            if (local == "LineComment" || local == "Comment" && e.Parent != null && Local(e.Parent) == "StructuredText") kind = "sclComment";
                            else if (local == "MultilingualText")
                            {
                                var composition = (string?)e.Attribute("CompositionName") ?? "";
                                kind = composition == "Title" ? "title" : composition == "Comment" ? "comment" : "multilingualText";
                            }
                            else if (local == "Comment" && e.Elements().Any(c => Local(c) == "MultiLanguageText")) kind = "memberComment";
                        }
                        if (units.TryGetValue(e, out var idx)) { network = idx; break; }
                    }
                    if (kind == "title" || kind == "comment") kind = (network.HasValue ? "network" : "block") + (kind == "title" ? "Title" : "Comment");
                    rows.Add(new AnnotationRow
                    {
                        File = filePath, BlockName = blockName, Network = network,
                        Line = ((IXmlLineInfo)textNode).HasLineInfo() ? ((IXmlLineInfo)textNode).LineNumber : 0,
                        Marker = m.Groups[1].Value, Text = Clip(CollapseWhitespace(value)), Kind = kind,
                    });
                }
            }
            return rows;
        }

        private static List<AnnotationRow> ScanSource(string filePath, string text, Regex regex, Dictionary<string, string> resources)
        {
            var rows = new List<AnnotationRow>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var inBlockComment = false;
            var blockName = "";
            var networkIndex = 0;
            var inNetwork = false;
            var implicitBody = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var code = StripComments(raw, ref inBlockComment, out var comment);
                var codeTrim = code.Trim();
                var upper = codeTrim.ToUpperInvariant();
                if (blockName.Length == 0) { var decl = DeclarationRegex.Match(codeTrim); if (decl.Success) blockName = decl.Groups[2].Value; }
                if (upper == "NETWORK" || upper.StartsWith("NETWORK ", StringComparison.Ordinal))
                {
                    if (implicitBody && inNetwork) { networkIndex--; implicitBody = false; }
                    networkIndex++; inNetwork = true;
                }
                else if (upper.StartsWith("END_NETWORK", StringComparison.Ordinal)) inNetwork = false;
                else if (upper == "BEGIN" && networkIndex == 0) { networkIndex = 1; inNetwork = true; implicitBody = true; }
                else
                    if (implicitBody
                        && (upper.StartsWith("END_FUNCTION", StringComparison.Ordinal) || upper.StartsWith("END_ORGANIZATION", StringComparison.Ordinal)
                            || upper.StartsWith("END_DATA_BLOCK", StringComparison.Ordinal)))
                    { inNetwork = false; implicitBody = false; }

                int? network = inNetwork ? networkIndex : (int?)null;
                foreach (Match m in regex.Matches(comment))
                    rows
                        .Add(new AnnotationRow {
                            File = filePath, BlockName = blockName, Network = network, Line = i + 1, Marker = m.Groups[1].Value, Text =
                                Clip(CollapseWhitespace(comment)), Kind = "sourceComment"
                        });

                foreach (Match mlc in MlcRegex.Matches(code))
                {
                    if (!resources.TryGetValue(mlc.Value, out var resolved)) continue;
                    var kind = code.IndexOf("S7_NetworkTitle", StringComparison.Ordinal) >= 0 ? "networkTitle"
                        : code.IndexOf("S7_NetworkComment", StringComparison.Ordinal) >= 0 ? "networkComment"
                        : code.IndexOf("S7_BlockTitle", StringComparison.Ordinal) >= 0 ? "blockTitle"
                        : code.IndexOf("S7_BlockComment", StringComparison.Ordinal) >= 0 ? "blockComment" : "resourceText";
                    var pendingNetwork = kind.StartsWith("network", StringComparison.Ordinal) && !inNetwork ? networkIndex + 1 : network;
                    foreach (Match m in regex.Matches(resolved))
                        rows
                            .Add(new AnnotationRow {
                                File = filePath, BlockName = blockName, Network = pendingNetwork, Line = i + 1, Marker = m.Groups[1].Value, Text =
                                    Clip(CollapseWhitespace(resolved)), Kind = kind
                            });
                }
            }
            return rows;
        }

        private static string Clip(string text) => text.Length > MaxAnnotationText ? text.Substring(0, MaxAnnotationText) + "…" : text;

        public static JsonObject AnnotationJson(AnnotationRow r) => new JsonObject
        {
            ["file"] = r.File, ["blockName"] = r.BlockName, ["network"] = r.Network.HasValue ? r.Network.Value : (JsonNode?)null,
            ["line"] = r.Line, ["marker"] = r.Marker, ["text"] = r.Text, ["kind"] = r.Kind,
        };

        public static string ToCsv(IEnumerable<AnnotationRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("file,blockName,network,line,marker,kind,text");
            foreach (var r in rows)
                sb
                    .AppendLine(string
                        .Join(",", new[] {
                            r.File, r.BlockName, r.Network?.ToString(CultureInfo.InvariantCulture) ?? "", r.Line.ToString(CultureInfo.InvariantCulture), r
                                .Marker, r.Kind, r.Text
                        }.Select(CsvField)));
            return sb.ToString();
        }

        private static string CsvField(string value)
        {
            var v = value ?? "";
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // ------------------------------------------------------------------ metrics

        public static JsonObject Metrics(BlockDocument doc)
        {
            var sections = new JsonObject();
            var total = 0;
            foreach (var g in doc.Members.GroupBy(m => m.Section).OrderBy(g => g.Key, StringComparer.Ordinal)) {
                sections[g.Key] = g.Count(); total += g.Count(); }
            sections["total"] = total;

            var calls = new JsonArray();
            foreach (var g in doc.BlockCalls.GroupBy(c => c, StringComparer.Ordinal).OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var colon = g.Key.IndexOf(':');
                calls
                    .Add(new JsonObject {
                        ["kind"] = colon > 0 ? g.Key.Substring(0, colon) : "", ["name"] = colon > 0 ? g.Key.Substring(colon + 1) : g.Key, ["count"] = g.Count()
                    });
            }
            var instructions = new JsonArray();
            var instructionGroups = doc.Instructions.GroupBy(c => c, StringComparer.Ordinal).OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            foreach (var g in instructionGroups.Take(25)) instructions.Add(new JsonObject { ["name"] = g.Key, ["count"] = g.Count() });

            var languages = new JsonObject();
            foreach (var g in doc.Networks.GroupBy(n => n.Language.Length == 0 ? "(unknown)" : n.Language).OrderBy(g => g.Key, StringComparer.Ordinal))
                languages[g.Key] = g.Count();

            var sclCode = doc.Format == "SCL" || doc.Networks.Any(n => n.Language.Equals("SCL", StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["file"] = doc.SourcePath, ["format"] = doc.Format, ["blockName"] = doc.BlockName, ["blockType"] = doc.BlockType, ["language"] = doc.Language,
                ["networkCount"] = doc.Networks.Count,
                ["networksWithTitle"] = doc.Networks.Count(n => n.Title.Length > 0),
                ["networksWithComment"] = doc.Networks.Count(n => n.Comment.Length > 0),
                ["networkLanguages"] = languages,
                ["interfaceMembers"] = sections,
                ["blockCalls"] = calls,
                ["blockCallCount"] = doc.BlockCalls.Count,
                ["instructions"] = instructions,
                ["instructionCount"] = doc.Instructions.Count,
                ["distinctInstructions"] = instructionGroups.Count,
                ["instructionsTruncated"] = instructionGroups.Count > 25,
                ["sourceLines"] = doc.SourceLines,
                ["commentLines"] = doc.CommentLines,
                ["commentRatio"] = doc.SourceLines > 0 ? Math.Round((double)doc.CommentLines / doc.SourceLines, 3) : (JsonNode?)null,
                ["maxNesting"] = doc.MaxNesting.HasValue ? doc.MaxNesting.Value : (JsonNode?)null,
                ["nestingDerivable"] = sclCode && doc.MaxNesting.HasValue,
                ["canonicalLines"] = doc.Canonical.Count,
                ["warnings"] = new JsonArray(doc.Warnings.Select(w => (JsonNode)w).ToArray()),
            };
        }
    }
}
