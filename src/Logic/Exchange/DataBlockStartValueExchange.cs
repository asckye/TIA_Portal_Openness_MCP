using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Logic.Exchange
{
    public sealed class DataBlockStartValueEdit
    {
        public XDocument Document { get; }
        public JsonObject Summary { get; }
        internal DataBlockStartValueEdit(XDocument document, JsonObject summary) { Document = document; Summary = summary; }
    }

    public static class DataBlockStartValueExchange
    {
        private static readonly string[] Columns = { "Section", "MemberPath", "DataType", "StartValue" };

        public static XDocument Parse(string xml)
        {
            if (Encoding.UTF8.GetByteCount(xml) > TableFile.MaximumBytes) throw new ArgumentException("XML exceeds 16 MiB.", nameof(xml));
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = TableFile.MaximumBytes });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            Validate(document);
            return document;
        }

        private static void Validate(XDocument document)
        {
            var objects = document.Root?.Elements().Where(e => e.Name.LocalName is not ("Engineering" or "DocumentInfo")).ToArray();
            if (document.Root?.Name.LocalName != "Document" || objects?.Length != 1 || objects[0].Name.LocalName != "SW.Blocks.GlobalDB")
                throw new ArgumentException("dataBlockStartValues requires a global DB; use PatchPlcBlockDocument for other block kinds.");
            if (string.IsNullOrWhiteSpace(PlcDocumentEditing.Name(document))) throw new ArgumentException("Block Name is missing.");
            if (PlcDocumentEditing.LibraryBound(document))
                throw new ArgumentException("Library-connected blocks require the library update workflow.");
        }

        private static XElement[] Members(XDocument document)
            => PlcDocumentEditing.Child(PlcDocumentEditing.Child(PlcDocumentEditing.Block(document), "AttributeList"), "Interface")
                .Descendants().Where(e => e.Name.LocalName == "Member").ToArray();

        private static string? NotEditableReason(XElement member)
        {
            var start = member.Elements().SingleOrDefault(e => e.Name.LocalName == "StartValue");
            if (start?.Attribute("ConstantName") != null) return "StartValue references ConstantName; constant references cannot be edited through the table.";
            if (start?.Attribute("IsBulkValue") != null) return "StartValue has IsBulkValue; bulk values cannot be edited through the table.";
            if (member.Elements().Any(e => e.Name.LocalName is "Member" or "Sections" or "Subelement")
                || member.Ancestors().Any(e => e.Name.LocalName == "Subelement" || e.Name.LocalName == "Member"
                    && !string.Equals((string?)e.Attribute("Datatype"), "Struct", StringComparison.OrdinalIgnoreCase)))
                return "Structured, array and nested UDT/FB members cannot be edited through the table.";
            if (member.AncestorsAndSelf().Concat(start == null ? Array.Empty<XElement>() : new[] { start })
                .Any(e => e.Attributes().Any(a => a.Name.LocalName is "ReadOnly" or "Informative" && (a.Value == "1"
                    || a.Value.Equals("true", StringComparison.OrdinalIgnoreCase))))) return "Read-only/informative member cannot be edited.";
            if (start?.HasElements == true) return "StartValue is not a plain scalar literal.";
            if (PlcDocumentEditing.ElementaryMember(member)) return null;
            string type = (string?)member.Attribute("Datatype") ?? "";
            return DataBlockStartValueLiterals.AdditionalScalar(type, start?.Value ?? "") ? null
                : "DataType is outside the elementary/existing-scalar allow-list or has no plain exported literal.";
        }

        private static string MemberPath(XElement member) => string.Join("/", member.AncestorsAndSelf().Reverse()
            .Where(e => e.Name.LocalName == "Member").Select(e => ObjectPathEncoding.EncodeSegment((string?)e.Attribute("Name") ?? "")));
        private static string Section(XElement member) => (string?)member.Ancestors().First(e => e.Name.LocalName == "Section").Attribute("Name") ?? "";

        public static JsonArray NotEditableReasons(XDocument document)
        {
            Validate(document);
            return new JsonArray(Members(document).Select(member => new { Member = member, Reason = NotEditableReason(member) })
                .Where(item => item.Reason != null).Select(item => (JsonNode)new JsonObject {
                    ["section"] = Section(item.Member), ["memberPath"] = MemberPath(item.Member),
                    ["dataType"] = (string?)item.Member.Attribute("Datatype"), ["reason"] = item.Reason }).ToArray());
        }
        public static int NotEditable(XDocument document) => NotEditableReasons(document).Count;

        public static TableData ToTable(XDocument document)
        {
            Validate(document);
            var table = new TableData(Columns, Members(document).Where(member => NotEditableReason(member) == null).Select(member => new[] {
                Section(member), MemberPath(member),
                (string?)member.Attribute("Datatype") ?? "", PlcDocumentEditing.Value(member, "StartValue") }));
            TableFile.Validate(table);
            return table;
        }

        private static string Key(string section, string path)
            => section + "\0" + string.Join("/", path.Split('/').Select(s => ObjectPathEncoding.EncodeSegment(ObjectPathEncoding.DecodeSegment(s))));

        public static DataBlockStartValueEdit FromTable(XDocument template, TableData table)
            => FromTable(template, table, PlcDocumentEditing.CreateMemberLookup);

        internal static DataBlockStartValueEdit FromTable(XDocument template, TableData table, Func<XDocument, PlcDocumentEditing.MemberLookup> buildLookup)
        {
            TableFile.Validate(table);
            var original = ToTable(template);
            if (table.Columns.Length != Columns.Length || Columns.Any(c => !table.Columns.Contains(c)))
                throw TableFile.Error("table", table.HeaderRowNumber, 1, "Expected only Section, MemberPath, DataType and StartValue columns.");
            int section = Array.IndexOf(table.Columns, "Section"), path = Array.IndexOf(table.Columns, "MemberPath"),
                type = Array.IndexOf(table.Columns, "DataType"), value = Array.IndexOf(table.Columns, "StartValue");
            var byKey = original.Rows.ToDictionary(row => Key(row[0], row[1]), StringComparer.Ordinal);
            var refused = NotEditableReasons(template).ToDictionary(row => Key((string)row!["section"]!, (string)row["memberPath"]!),
                row => (string)row!["reason"]!, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var document = new XDocument(template);
            var members = buildLookup(document);
            var changes = new JsonArray();
            int updated = 0, unchanged = 0;
            for (int index = 0; index < table.Rows.Count; index++)
            {
                var row = table.Rows[index]; string key = Key(row[section], row[path]);
                int sourceRow = table.RowNumber(index);
                if (!seen.Add(key)) throw TableFile.Error("table", sourceRow, path + 1, "Duplicate member key.");
                if (!byKey.TryGetValue(key, out var before))
                    throw TableFile.Error("table", sourceRow, path + 1, "Unknown or non-editable member; "
                        + (refused.TryGetValue(key, out var reason) ? reason : "interface changes are refused."));
                if (row[type] != before[2]) throw TableFile.Error("table", sourceRow, type + 1, "DataType must equal the template.");
                if (row[value] == before[3]) { unchanged++; continue; }
                string normalized;
                try
                {
                    if (row[value].Length == 0)
                        throw new ArgumentException("Resetting to the default is not supported through the table; do not empty StartValue cells.");
                    var literal = DataBlockStartValueLiterals.Normalize(before[2], row[value], before[3]);
                    normalized = literal.Text;
                    if (before[3].Length != 0 && normalized == DataBlockStartValueLiterals.Normalize(before[2], before[3], before[3]).Text)
                    { unchanged++; continue; }
                    if (literal.IsDefault)
                        throw new ArgumentException("The type default is omitted by TIA export and cannot be set through the table; "
                            + "verified import would mismatch. Keep an existing explicit default unchanged.");
                }
                catch (ArgumentException error) { throw TableFile.Error("table", sourceRow, value + 1, error.Message); }
                var member = members.FindMember(before[0], before[1].Split('/').Select(ObjectPathEncoding.DecodeSegment).ToArray());
                PlcDocumentEditing.SetMemberStartValue(member, before[3], normalized);
                updated++;
                if (changes.Count < 50) changes.Add(new JsonObject { ["section"] = before[0], ["memberPath"] = before[1],
                    ["old"] = before[3], ["new"] = normalized });
            }
            return new DataBlockStartValueEdit(document, new JsonObject { ["updated"] = updated, ["added"] = 0, ["unchanged"] = unchanged,
                ["keptFromTemplate"] = byKey.Count - seen.Count, ["notEditable"] = NotEditable(template),
                ["notEditableReasons"] = NotEditableReasons(template), ["changedRows"] = changes,
                ["changedRowsTruncated"] = updated > changes.Count });
        }
    }
}
