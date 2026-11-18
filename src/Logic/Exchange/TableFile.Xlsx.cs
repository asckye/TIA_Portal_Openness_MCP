using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Logic.Exchange
{
    public static partial class TableFile
    {
        private static readonly XNamespace SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static readonly Regex SpreadsheetEscape = new Regex(@"_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant);
        private static readonly Regex SpreadsheetLiteral = new Regex(@"_(?=x[0-9A-Fa-f]{4}_)", RegexOptions.CultureInvariant);
        public const int MaximumWorkbookCharacters = 64 * 1024 * 1024;

        private static string EncodeSpreadsheetText(string text) => SpreadsheetLiteral.Replace(text, "_x005F_");
        private static string DecodeSpreadsheetText(string text) => SpreadsheetEscape.Replace(text,
            match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

        public static byte[] WriteXlsx(TableData table, string sheetName, string file = "XLSX")
        {
            Validate(table, file, 32767);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                long total = 0;
                void Put(string name, MemoryStream buffer)
                {
                    total += buffer.Length;
                    if (total > MaximumBytes) throw Error(file, 1, 1, "Uncompressed parts exceed 16 MiB.");
                    var entry = zip.CreateEntry(name);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open(); buffer.Position = 0; buffer.CopyTo(stream);
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
                            writer.WriteAttributeString("xml", "space", XNamespace.Xml.NamespaceName, "preserve");
                            writer.WriteString(EncodeSpreadsheetText(cells[c]));
                            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.Flush();
                            if (total + sheetBuffer.Length > MaximumBytes) throw Error(file, r, c + 1, "Uncompressed parts exceed 16 MiB.");
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); writer.WriteEndElement();
                }
                Put("xl/worksheets/sheet1.xml", sheetBuffer);
            }
            return output.ToArray();
        }

        public static TableData ReadXlsx(byte[] bytes, string sheetName, string file = "table.xlsx", EmptyTableRows emptyRows = EmptyTableRows.Skip)
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
                long actualBytes = 0;
                XDocument Load(string name)
                {
                    if (!entries.TryGetValue(name, out var entry)) throw Error(file, 1, 1, "Missing workbook part: " + name);
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream(ReadXlsxPart(stream, file, name, ref actualBytes));
                    using var reader = XmlReader.Create(buffer, new XmlReaderSettings {
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
                var rows = new List<string[]>(); int previousRow = 0, width = 0, pendingEmptyRows = 0;
                var sourceRows = new List<int>();
                long logicalCharacters = 0;
                foreach (var row in sheetData.Elements(SheetNs + "row"))
                {
                    int r;
                    if (row.Attribute("r") is { } rowNumber)
                    {
                        if (!int.TryParse(rowNumber.Value, NumberStyles.None, CultureInfo.InvariantCulture, out r))
                            throw Error(file, previousRow + 1, 1, "Invalid row index: " + rowNumber.Value);
                    }
                    else r = previousRow + 1;
                    if (r <= previousRow) throw Error(file, r, 1, "Invalid row index.");
                    var cells = new SortedDictionary<int, string>(); int previousColumn = 0;
                    foreach (var cell in row.Elements(SheetNs + "c"))
                    {
                        string reference = (string?)cell.Attribute("r") ?? ColumnName(previousColumn) + r;
                        int c = ColumnIndex(reference, r, file);
                        if (c <= previousColumn) throw Error(file, r, c, "Invalid or duplicate column index.");
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
                        value ??= "";
                        logicalCharacters += value.Length;
                        if (logicalCharacters > MaximumWorkbookCharacters) throw Error(file, r, c, "Workbook exceeds 64 Mi logical characters.");
                        cells.Add(c, value);
                    }
                    if (rows.Count == 0 && cells.Values.Any(value => value.Length != 0))
                    {
                        width = cells.Where(cell => cell.Value.Length != 0).Select(cell => cell.Key).DefaultIfEmpty(0).Max();
                        if (width > MaximumColumns) throw Error(file, r, width, "Too many columns.");
                    }
                    bool nonempty = cells.Values.Any(value => value.Length != 0);
                    if (rows.Count != 0 && emptyRows != EmptyTableRows.Skip) pendingEmptyRows += r - previousRow - 1;
                    if (nonempty || rows.Count != 0 && emptyRows == EmptyTableRows.Keep)
                    {
                        var extra = cells.FirstOrDefault(cell => cell.Key > width && cell.Value.Length != 0);
                        if (extra.Key != 0) throw Error(file, r, extra.Key, "Row width exceeds header.");
                        if ((long)rows.Count + pendingEmptyRows > MaximumRows) throw Error(file, r, 1, "More than 10000 data rows.");
                        while (pendingEmptyRows > 0)
                        {
                            sourceRows.Add(r - pendingEmptyRows); rows.Add(Enumerable.Repeat("", width).ToArray()); pendingEmptyRows--;
                        }
                        rows.Add(Enumerable.Range(1, width).Select(c => cells.TryGetValue(c, out var value) ? value : "").ToArray());
                        sourceRows.Add(r);
                        if (rows.Count > MaximumRows + 1) throw Error(file, r, 1, "More than 10000 data rows.");
                    }
                    else if (rows.Count != 0 && emptyRows == EmptyTableRows.TrimTrailing) pendingEmptyRows++;
                    previousRow = r;
                }
                var table = new TableData(rows.FirstOrDefault() ?? Array.Empty<string>(), rows.Skip(1), sourceRows.Skip(1), sourceRows.FirstOrDefault());
                Validate(table, file, 32767); return table;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is XmlException || ex is InvalidOperationException
                || ex is FormatException || ex is OverflowException)
            { throw Error(file, 1, 1, "Invalid, encrypted or unsupported XLSX: " + ex.Message); }
        }

        private static string StringText(XElement element) => DecodeSpreadsheetText(string.Concat(element.Descendants(SheetNs + "t")
            .Where(t => t.Parent?.Name != SheetNs + "rPh").Select(t => t.Value)));
        internal static byte[] ReadXlsxPart(Stream stream, string file, string name, ref long actualBytes)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192]; int count;
            while ((count = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaximumBytes - actualBytes + 1))) != 0)
            {
                actualBytes += count;
                if (actualBytes > MaximumBytes) throw Error(file, 1, 1, "Uncompressed parts exceed 16 MiB while reading " + name + ".");
                buffer.Write(chunk, 0, count);
            }
            return buffer.ToArray();
        }
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
                if (c > 16384) throw Error(file, row, c, "Invalid Excel column index.");
            }
            if (i == 0 || !int.TryParse(reference.Substring(i), out int r) || r != row) throw Error(file, row, c, "Invalid cell reference.");
            return c;
        }
    }
}
