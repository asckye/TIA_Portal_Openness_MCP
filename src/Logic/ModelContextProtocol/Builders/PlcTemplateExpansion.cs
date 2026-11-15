using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class PlcTemplateExpansion
    {
        public sealed class Document { public string Name = ""; public string Xml = ""; }
        private static readonly Regex Token = new Regex(@"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant);
        public static List<Document> Expand(string templatePath, string rowsJson)
        {
            if (!Path.IsPathRooted(templatePath) || !File.Exists(templatePath))
                throw new AdapterPreconditionException("Supply an existing absolute XML template path.", "templatePath");
            XDocument template;
            try
            {
                using (var reader = XmlReader
                        .Create(templatePath, new XmlReaderSettings {
                            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
                    template = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException) { throw new AdapterPreconditionException("Supply a well-formed XML template without a DTD, within the 4 MiB character limit.", "templatePath"); }
            JsonArray rows;
            try { rows = JsonNode.Parse(rowsJson) as JsonArray
                ?? throw new AdapterPreconditionException("Supply rows as an array of objects containing fileName and values.", "rows"); }
            catch (Exception ex) when (ex is ArgumentException || ex is System.Text.Json.JsonException)
            { throw new AdapterPreconditionException("Supply rows as an array of objects containing fileName and values.", "rows"); }
            if (rows.Count < 1 || rows.Count > 100) throw new AdapterPreconditionException("Supply 1..100 rows.", "rows");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<Document>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i] as JsonObject ?? throw new AdapterPreconditionException("Supply an object for every row.", "/rows/" + i);
                string name = row["fileName"] is JsonValue fileName && fileName.TryGetValue<string>(out var textName) ? textName : "";
                if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name
                        .EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !names.Add(name))
                    throw new AdapterPreconditionException("Supply a unique simple .xml fileName for every row.", "/rows/" + i + "/fileName");
                var values = row["values"] as JsonObject ?? throw new AdapterPreconditionException("Supply values as an object of strings.", "/rows/" + i + "/values");
                foreach (var value in values) if (!(value.Value is JsonValue v) || !v.TryGetValue<string>(out _))
                    throw new AdapterPreconditionException("Supply strings for all replacement values.", "/rows/" + i + "/values");
                var used = new HashSet<string>(StringComparer.Ordinal);
                string Replace(string text) => Token.Replace(text, match =>
                {
                    string key = match.Groups[1].Value;
                    if (!values.TryGetPropertyValue(key, out var value))
                        throw new AdapterPreconditionException("Supply a replacement for missing template token '" + key + "'.", "/rows/" + i + "/values");
                    used.Add(key); return value!.GetValue<string>();
                });
                var doc = new XDocument(template);
                foreach (var attribute in doc.Descendants().Attributes()) attribute.Value = Replace(attribute.Value);
                foreach (var text in doc.DescendantNodes().OfType<XText>()) text.Value = Replace(text.Value);
                if (values.Any(k => !used.Contains(k.Key)))
                    throw new AdapterPreconditionException("A replacement key in rows[" + i + "].values is not used by the template; remove unused replacements.", "/rows/" + i + "/values");
                string xml = doc.ToString(SaveOptions.DisableFormatting);
                if (Token.IsMatch(xml)) throw new AdapterPreconditionException("Resolve all template tokens and remove tokens introduced by replacements.", "/rows/" + i + "/values");
                result.Add(new Document { Name = name, Xml = xml });
            }
            if (result.Count != rows.Count) throw new AdapterPreconditionException("Supply an object for every row.", "rows");
            return result;
        }
    }
}
