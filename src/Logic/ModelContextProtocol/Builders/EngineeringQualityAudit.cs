using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>Independent rule implementation; no tia-linter/TiaUtilities GPL source used.</summary>
    public static class EngineeringQualityAudit
    {
        public static JsonObject Audit(string directory, string rulesJson, string blockNamePattern, int maxNetworks)
        {
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory)) throw new ArgumentException("An existing absolute export directory is required.");
            if (maxNetworks < 1 || maxNetworks > 10000) throw new ArgumentException("maxNetworks must be 1..10000.");
            Regex? names = string.IsNullOrEmpty(blockNamePattern) ? null : Pattern(blockNamePattern);
            var rules = JsonNode.Parse(rulesJson) as JsonArray ?? throw new ArgumentException("rulesJson must be an array.");
            if (rules.Count > 50) throw new ArgumentException("At most 50 XML rules.");
            var ruleIds = new HashSet<string>(StringComparer.Ordinal); var evaluated = new Dictionary<string, int>();
            foreach (var n in rules)
            {
                var r = n as JsonObject ?? throw new ArgumentException("Each rule is an object.");
                if (r.Any(k => !new[] { "id", "files", "xpath", "minCount", "maxCount", "valuePattern", "severity" }.Contains(k.Key))) throw new ArgumentException("Unknown rule property.");
                string id = r["id"]?.GetValue<string>() ?? "";
                if (id.Length == 0 || !ruleIds.Add(id)) throw new ArgumentException("Rule IDs must be non-empty and unique.");
                string xpath = r["xpath"]?.GetValue<string>() ?? "";
                if (xpath.Length == 0 || xpath.Length > 1024) throw new ArgumentException("Each rule needs an XPath selecting elements (max 1024 chars).");
                XPathExpression.Compile(xpath); Pattern(r["files"]?.GetValue<string>() ?? ".*");
                if (r["valuePattern"] != null) Pattern(r["valuePattern"]!.GetValue<string>());
                int min = r["minCount"]?.GetValue<int>() ?? 0, max = r["maxCount"]?.GetValue<int>() ?? int.MaxValue;
                if (min < 0 || max < min) throw new ArgumentException("Rule counts require 0 <= minCount <= maxCount.");
                if (!new[] { "info", "warning", "error" }.Contains(r["severity"]?.GetValue<string>() ?? "warning")) throw new ArgumentException("severity must be info/warning/error.");
                evaluated[id] = 0;
            }
            var files = OfflineAnalysisLogic.EnumerateDocuments(directory, true, new[] { ".xml", ".scl", ".s7dcl", ".aml" });
            var findings = new JsonArray(); var failures = new JsonArray(); var blocks = new Dictionary<string, string>(StringComparer.Ordinal); int totalFindings = 0;
            void Finding(string file, string rule, string severity, string detail)
            { totalFindings++; if (findings.Count < 2000) findings.Add(new JsonObject { ["file"] = file, ["rule"] = rule, ["severity"] = severity, ["message"] = detail }); }
            foreach (string file in files.Take(1000))
            {
                try
                {
                    if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Export exceeds 16 MiB audit limit.");
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    XDocument? xml = null;
                    if (extension == ".xml" || extension == ".aml")
                    {
                        using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 })) xml = XDocument.Load(reader);
                        foreach (var rule in rules.OfType<JsonObject>())
                        {
                            if (!Pattern(rule["files"]?.GetValue<string>() ?? ".*").IsMatch(file)) continue;
                            string id = rule["id"]!.GetValue<string>(); evaluated[id]++;
                            var selected = xml.XPathSelectElements(rule["xpath"]!.GetValue<string>()).ToList();
                            if (selected.Count < (rule["minCount"]?.GetValue<int>() ?? 0) || selected.Count > (rule["maxCount"]?.GetValue<int>() ?? int.MaxValue)) Finding(file, id, rule["severity"]?.GetValue<string>() ?? "warning", "XPath match count: " + selected.Count);
                            if (rule["valuePattern"] != null)
                            {
                                var pattern = Pattern(rule["valuePattern"]!.GetValue<string>());
                                foreach (var element in selected.Where(e => !pattern.IsMatch(e.Value))) Finding(file, id, rule["severity"]?.GetValue<string>() ?? "warning", "Value does not match policy at " + element.Name.LocalName + ": " + element.Value.Substring(0, Math.Min(200, element.Value.Length)));
                            }
                        }
                        // Hardware, library and metadata exports use the explicit XML rules above.
                        if (!xml.Descendants().Any(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) && e.Name.LocalName != "SW.Blocks.CompileUnit")) continue;
                    }
                    var doc = OfflineAnalysisLogic.ParseFile(file);
                    foreach (var warning in doc.Warnings) Finding(file, "parser-scope", "info", warning);
                    if (doc.BlockName.Length == 0) { Finding(file, "block-name-missing", "warning", "No block declaration/name found."); continue; }
                    if (names != null && !names.IsMatch(doc.BlockName)) Finding(file, "block-name-policy", "warning", "Name does not match configured policy: " + doc.BlockName);
                    if (blocks.TryGetValue(doc.BlockName, out var prior)) Finding(file, "duplicate-block-name", "warning", "Also present in " + prior + "; inspect PLC/unit namespaces before treating as a collision.");
                    else blocks[doc.BlockName] = file;
                    if (doc.Comment.Length == 0 && doc.Title.Length == 0 && doc.CommentLines == 0) Finding(file, "block-comment", "warning", "No block title/comment or source comment found.");
                    if (doc.Networks.Count > maxNetworks) Finding(file, "block-size", "warning", "Network count " + doc.Networks.Count + " exceeds configured " + maxNetworks + ".");
                    foreach (var network in doc.Networks.Where(n => n.Title.Length == 0 && n.Comment.Length == 0)) Finding(file, "network-comment", "info", "Network " + network.Index + " has no title/comment.");
                    foreach (var metadata in new[] { "Author", "Family", "Version" })
                        if (!doc.Attributes.TryGetValue(metadata, out var value) || string.IsNullOrWhiteSpace(value)) Finding(file, "metadata-" + metadata, "info", "Export does not contain " + metadata + ".");
                }
                catch (Exception ex) { failures.Add(new JsonObject { ["file"] = file, ["error"] = ex.Message }); }
            }
            var rulesReport = new JsonArray(evaluated.Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["filesEvaluated"] = p.Value, ["status"] = p.Value == 0 ? "notEvaluated" : "evaluated" }).ToArray());
            bool complete = files.Count > 0 && (blocks.Count > 0 || evaluated.Values.Any(v => v > 0)) && failures.Count == 0 && files.Count <= 1000 && totalFindings <= findings.Count && evaluated.Values.All(v => v > 0);
            return new JsonObject { ["filesFound"] = files.Count, ["filesInspected"] = Math.Min(files.Count, 1000), ["blocksInspected"] = blocks.Count, ["findings"] = findings, ["totalFindings"] = totalFindings,
                ["failures"] = failures, ["rules"] = rulesReport, ["dataComplete"] = complete, ["qualityPassed"] = complete && !findings.OfType<JsonObject>().Any(f => f["severity"]?.GetValue<string>() == "error" || f["severity"]?.GetValue<string>() == "warning"),
                ["scope"] = "Only supplied export documents and selected policies. Not a Siemens style-guide certification, native compilation, hardware safety check or complete project/reference analysis. Unmatched custom rules are not evaluated, not passed." };
        }
        private static Regex Pattern(string value) => new Regex(value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        public static string Html(JsonObject report) => "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Engineering quality report</title><style>body{font:14px system-ui;margin:32px}pre{white-space:pre-wrap;overflow-wrap:anywhere}@media print{body{margin:0}}</style><h1>Engineering quality report</h1><pre>" + WebUtility.HtmlEncode(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })) + "</pre></html>";
    }
}
