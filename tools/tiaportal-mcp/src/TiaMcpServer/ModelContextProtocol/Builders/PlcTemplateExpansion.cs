using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class PlcTemplateExpansion
    {
        public sealed class Document { public string Name = ""; public string Xml = ""; }
        private static readonly Regex Token = new Regex(@"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant);
        public static List<Document> Expand(string templatePath, string rowsJson)
        {
            if (!Path.IsPathRooted(templatePath) || !File.Exists(templatePath)) throw new ArgumentException("Existing absolute XML template path required.");
            XDocument template;
            using (var reader = XmlReader.Create(templatePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 })) template = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var rows = JsonNode.Parse(rowsJson) as JsonArray ?? throw new ArgumentException("rowsJson must be an array of {fileName,values:{token:value}}.");
            if (rows.Count < 1 || rows.Count > 100) throw new ArgumentException("Use 1..100 rows.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<Document>();
            foreach (var row in rows.OfType<JsonObject>())
            {
                string name = row["fileName"]?.GetValue<string>() ?? "";
                if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !names.Add(name)) throw new ArgumentException("Unique simple .xml fileName required for every row.");
                var values = row["values"] as JsonObject ?? throw new ArgumentException("values must be an object of strings.");
                foreach (var value in values) if (!(value.Value is JsonValue v) || !v.TryGetValue<string>(out _)) throw new ArgumentException("Replacement values must be strings.");
                var used = new HashSet<string>(StringComparer.Ordinal);
                string Replace(string text) => Token.Replace(text, match =>
                {
                    string key = match.Groups[1].Value;
                    if (!values.TryGetPropertyValue(key, out var value)) throw new ArgumentException("Missing template token: " + key);
                    used.Add(key); return value!.GetValue<string>();
                });
                var doc = new XDocument(template);
                foreach (var attribute in doc.Descendants().Attributes()) attribute.Value = Replace(attribute.Value);
                foreach (var text in doc.DescendantNodes().OfType<XText>()) text.Value = Replace(text.Value);
                if (values.Any(k => !used.Contains(k.Key))) throw new ArgumentException("Unused replacement keys: " + string.Join(",", values.Select(k => k.Key).Where(k => !used.Contains(k))));
                string xml = doc.ToString(SaveOptions.DisableFormatting);
                if (Token.IsMatch(xml)) throw new ArgumentException("Unresolved or recursively introduced template token.");
                result.Add(new Document { Name = name, Xml = xml });
            }
            if (result.Count != rows.Count) throw new ArgumentException("Every row must be an object.");
            return result;
        }
    }
}
