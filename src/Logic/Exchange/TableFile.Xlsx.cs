using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Logic.Exchange
{
    public static partial class TableFile
    {
        private static readonly XNamespace SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public static byte[] WriteXlsx(TableData table, string sheetName)
        {
            Validate(table, "XLSX", 32767);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                long total = 0;
                void Put(string name, MemoryStream buffer)
                {
                    total += buffer.Length;
                    if (total > MaximumBytes) throw Error("XLSX", 1, 1, "Uncompressed parts exceed 16 MiB.");
                    using var stream = zip.CreateEntry(name).Open(); buffer.Position = 0; buffer.CopyTo(stream);
                }
                void Part(string name, XDocument xml)
                {
                    using var buffer = new MemoryStream();
                    using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings {
                        Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize })) xml.Save(writer);
                    Put(name, buffer);
                }
                XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
                Part("[Content_Types].xml", new XDocument(new XElement(ct + "Types",
                    new XElement(ct + "Default", new XAttribute("Extension", "rels"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                    new XElement(ct + "Override", new XAttribute("PartName", "/xl/workbook.xml"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                    new XElement(ct + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
                XElement Relationship(string id, string type, string target) => new XElement(RelNs + "Relationship",
                    new XAttribute("Id", id), new XAttribute("Type", OfficeRel.NamespaceName + "/" + type), new XAttribute("Target", target));
                Part("_rels/.rels", new XDocument(new XElement(RelNs + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml"))));
                Part("xl/workbook.xml", new XDocument(new XElement(SheetNs + "workbook",
                    new XAttribute(XNamespace.Xmlns + "r", OfficeRel), new XElement(SheetNs + "sheets",
                        new XElement(SheetNs + "sheet", new XAttribute("name", sheetName), new XAttribute("sheetId", "1"),
                            new XAttribute(OfficeRel + "id", "rId1"))))));
                Part("xl/_rels/workbook.xml.rels", new XDocument(new XElement(RelNs + "Relationships",
                    Relationship("rId1", "worksheet", "worksheets/sheet1.xml"))));
                using var sheetBuffer = new MemoryStream();
                using (var writer = XmlWriter.Create(sheetBuffer, new XmlWriterSettings {
                    Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize }))
                {
                    void Start(string name) => writer.WriteStartElement(name, SheetNs.NamespaceName);
                    Start("worksheet"); Start("sheetData"); int r = 0;
                    foreach (var cells in new[] { table.Columns }.Concat(table.Rows))
                    {
                        r++; Start("row"); writer.WriteAttributeString("r", r.ToString(CultureInfo.InvariantCulture));
                        for (int c = 0; c < cells.Length; c++)
                        {
                            Start("c"); writer.WriteAttributeString("r", ColumnName(c) + r.ToString(CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("t", "inlineStr"); Start("is"); Start("t");
                            writer.WriteAttributeString("xml", "space", XNamespace.Xml.NamespaceName, "preserve"); writer.WriteString(cells[c]);
                            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.Flush();
                            if (total + sheetBuffer.Length > MaximumBytes) throw Error("XLSX", r, c + 1, "Uncompressed parts exceed 16 MiB.");
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); writer.WriteEndElement();
                }
                Put("xl/worksheets/sheet1.xml", sheetBuffer);
            }
            return output.ToArray();
        }

        public static TableData ReadXlsx(byte[] bytes, string sheetName, string file = "table.xlsx")
        {
            if (!string.Equals(Path.GetExtension(file), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw Error(file, 1, 1, "Only .xlsx workbooks are supported; .xls/.xlsm/encrypted files are refused.");
            if (bytes.Length > MaximumBytes) throw Error(file, 1, 1, "File exceeds 16 MiB.");
            try
            {
                using var input = new MemoryStream(bytes);
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                if (zip.Entries.Sum(e => e.Length) > MaximumBytes) throw Error(file, 1, 1, "Uncompressed parts exceed 16 MiB.");
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                    if (entries.ContainsKey(entry.FullName)) throw Error(file, 1, 1, "Duplicate workbook part.");
                    else entries.Add(entry.FullName, entry);
                XDocument Load(string name)
                {
                    if (!entries.TryGetValue(name, out var entry)) throw Error(file, 1, 1, "Missing workbook part: " + name);
                    using var stream = entry.Open();
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings {
                        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
                    return XDocument.Load(reader);
                }
                var workbook = Load("xl/workbook.xml");
                var relationships = Load("xl/_rels/workbook.xml.rels").Root!.Elements(RelNs + "Relationship").ToArray();
                string Target(XElement relationship)
                {
                    if ((string?)relationship.Attribute("TargetMode") == "External") throw Error(file, 1, 1, "External parts are refused.");
                    string target = (string?)relationship.Attribute("Target") ?? "";
                    if (target.Contains(":") || target.Contains("\\")) throw Error(file, 1, 1, "Invalid part target.");
                    var parts = new List<string>();
                    foreach (string part in (target.StartsWith("/", StringComparison.Ordinal) ? target.Substring(1) : "xl/" + target).Split('/'))
                    {
                        if (part == "..") { if (parts.Count == 0) throw Error(file, 1, 1, "Invalid part target."); parts.RemoveAt(parts.Count - 1); }
                        else if (part != "." && part.Length != 0) parts.Add(part);
                    }
                    return string.Join("/", parts);
                }
                var sheets = (workbook.Root?.Element(SheetNs + "sheets") ?? throw Error(file, 1, 1, "Missing workbook sheets."))
                    .Elements(SheetNs + "sheet").ToArray();
                var matches = sheets.Where(s => (string?)s.Attribute("name") == sheetName).ToArray();
                var sheet = matches.Length == 1 ? matches[0] : sheets.Length == 1 ? sheets[0]
                    : throw Error(file, 1, 1, "Workbook sheet is ambiguous; select the kind's named sheet.");
                var relation = relationships.Single(r => (string?)r.Attribute("Id") == (string?)sheet.Attribute(OfficeRel + "id")
                    && (string?)r.Attribute("Type") == OfficeRel.NamespaceName + "/worksheet");
                var sharedRelation = relationships.SingleOrDefault(r => (string?)r.Attribute("Type") == OfficeRel.NamespaceName + "/sharedStrings");
                var shared = sharedRelation == null ? Array.Empty<string>()
                    : Load(Target(sharedRelation)).Root!.Elements(SheetNs + "si").Select(StringText).ToArray();
                var sheetData = Load(Target(relation)).Root!.Element(SheetNs + "sheetData")
                    ?? throw Error(file, 1, 1, "Missing worksheet data.");
                var rows = new List<string[]>(); int previousRow = 0, width = 0;
                foreach (var row in sheetData.Elements(SheetNs + "row"))
                {
                    int r = (int?)row.Attribute("r") ?? previousRow + 1;
                    if (r <= previousRow || r > MaximumRows + 1) throw Error(file, r, 1, "Invalid row index or more than 10000 data rows.");
                    if (previousRow == 0 && r != 1) throw Error(file, r, 1, "Headers must be on row 1.");
                    while (previousRow + 1 < r) { rows.Add(new string[width].Select(_ => "").ToArray()); previousRow++; }
                    var cells = new SortedDictionary<int, string>(); int previousColumn = 0;
                    foreach (var cell in row.Elements(SheetNs + "c"))
                    {
                        string reference = (string?)cell.Attribute("r") ?? ColumnName(previousColumn) + r;
                        int c = ColumnIndex(reference, r, file);
                        if (c <= previousColumn || c > MaximumColumns) throw Error(file, r, c, "Invalid or duplicate column index.");
                        previousColumn = c;
                        string? value = cell.Element(SheetNs + "v")?.Value;
                        string type = (string?)cell.Attribute("t") ?? "n";
                        if (cell.Element(SheetNs + "f") != null && (value == null || value.Length == 0 && type != "str"))
                            throw Error(file, r, c, "Formula has no cached value.");
                        if (type == "e") throw Error(file, r, c, "Excel error cells are refused.");
                        if (type == "inlineStr") value = StringText(cell.Element(SheetNs + "is") ?? new XElement(SheetNs + "is"));
                        else if (type == "s")
                        {
                            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                                || index < 0 || index >= shared.Length) throw Error(file, r, c, "Invalid shared-string index.");
                            value = shared[index];
                        }
                        else if (type == "b") value = value == "1" ? "true" : value == "0" ? "false"
                            : throw Error(file, r, c, "Invalid boolean cell.");
                        else if (type != "n" && type != "str" && type != "d") throw Error(file, r, c, "Unsupported cell type.");
                        cells.Add(c, value ?? "");
                    }
                    if (r == 1) width = previousColumn;
                    if (previousColumn > width) throw Error(file, r, previousColumn, "Row width exceeds header.");
                    rows.Add(Enumerable.Range(1, width).Select(c => cells.TryGetValue(c, out var value) ? value : "").ToArray());
                    previousRow = r;
                }
                var table = new TableData(rows.FirstOrDefault() ?? Array.Empty<string>(), rows.Skip(1));
                Validate(table, file, 32767); return table;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is XmlException || ex is InvalidOperationException || ex is FormatException)
            { throw Error(file, 1, 1, "Invalid, encrypted or unsupported XLSX: " + ex.Message); }
        }

        private static string StringText(XElement element) => string.Concat(element.Descendants(SheetNs + "t")
            .Where(t => t.Parent?.Name != SheetNs + "rPh").Select(t => t.Value));
        private static string ColumnName(int zeroBased)
        {
            string name = "";
            for (int n = zeroBased + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
            return name;
        }
        private static int ColumnIndex(string reference, int row, string file)
        {
            int c = 0, i = 0;
            while (i < reference.Length && reference[i] >= 'A' && reference[i] <= 'Z')
            {
                c = c * 26 + reference[i++] - 'A' + 1;
                if (c > MaximumColumns) throw Error(file, row, c, "Too many columns.");
            }
            if (i == 0 || !int.TryParse(reference.Substring(i), out int r) || r != row) throw Error(file, row, c, "Invalid cell reference.");
            return c;
        }
    }
}
