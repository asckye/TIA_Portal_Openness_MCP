using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using Xunit;

public sealed class TableFileTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "TableExchange", name);
    private static readonly TableData Sample = new(new[] { "Name", "Value", "Comment" }, new[] {
        new[] { "电机/1", "0010", "引号\"，comma,\nline\r\nend" }, new[] { "=1+1", "-1", "1E3" } });

    [Fact]
    public void Csv_roundtrip_keeps_bom_quotes_newlines_and_chinese()
    {
        byte[] bytes = TableFile.WriteCsv(Sample);
        Assert.Equal(new byte[] { 239, 187, 191 }, bytes.Take(3));
        Assert.Equal("\"Name\",\"Value\",\"Comment\"\n", Encoding.UTF8.GetString(bytes.Skip(3).Take(25).ToArray()));
        Equal(Sample, TableFile.ReadCsv(bytes));
    }

    [Theory]
    [InlineData("A,B\r\nx,y\r\n")]
    [InlineData("A,B\rx,y\r")]
    [InlineData("A,B\nx,y\n")]
    public void Csv_accepts_no_bom_and_all_line_endings(string csv)
        => Assert.Equal(new[] { "x", "y" }, TableFile.ReadCsv(Encoding.UTF8.GetBytes(csv)).Rows.Single());

    [Theory]
    [InlineData("A\nx\0")]
    [InlineData("A\n\"x")]
    [InlineData("A\nx\"y\"")]
    [InlineData("A\n\"x\"y")]
    [InlineData("A,B\nx")]
    [InlineData("A,A\nx,y")]
    [InlineData(",B\nx,y")]
    [InlineData("")]
    public void Csv_refusals_name_file_row_and_column(string csv)
    {
        var error = Assert.Throws<ArgumentException>(() => TableFile.ReadCsv(Encoding.UTF8.GetBytes(csv), "bad.csv"));
        Assert.Contains("bad.csv: row ", error.Message); Assert.Contains(", column ", error.Message);
    }

    [Fact]
    public void Xlsx_roundtrip_writes_only_inline_text_cells()
    {
        byte[] bytes = TableFile.WriteXlsx(Sample, "Rows");
        Equal(Sample, TableFile.ReadXlsx(bytes, "Rows"));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var document = XDocument.Load(stream);
        Assert.All(document.Descendants().Where(e => e.Name.LocalName == "c"), c => Assert.Equal("inlineStr", (string?)c.Attribute("t")));
    }

    [Fact]
    public void Binary_fixtures_read_shared_strings_numbers_booleans_and_cached_formulas()
    {
        var table = TableFile.ReadXlsx(File.ReadAllBytes(Fixture("shared-strings.xlsx")), "Rows");
        Assert.Equal(new[] { "Name", "Value", "Enabled", "Formula" }, table.Columns);
        Assert.Equal(new[] { "电机/1", "0010", "true", "42" }, table.Rows.Single());
        var written = TableFile.ReadXlsx(File.ReadAllBytes(Fixture("inline-text.xlsx")), "Rows");
        Assert.Equal(new[] { "0010", "%M0.0", "1E3", "=1+1" }, written.Rows.Single());
    }

    [Theory]
    [InlineData("<c r=\"A2\"><f>1+1</f></c>", "Formula has no cached value")]
    [InlineData("<c r=\"A2\"><f>1+1</f><v/></c>", "Formula has no cached value")]
    [InlineData("<c r=\"A2\" t=\"e\"><v>#REF!</v></c>", "error cells")]
    [InlineData("<c r=\"A2\" t=\"s\"><v>99</v></c>", "shared-string index")]
    public void Xlsx_refuses_invalid_cells(string cell, string message)
    {
        byte[] bytes = ModifiedSheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A</t></is></c></row><row r=\"2\">" + cell + "</row>");
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => TableFile.ReadXlsx(bytes, "Rows", "bad.xlsx")).Message);
    }

    [Theory]
    [InlineData("bad.xlsm")]
    [InlineData("bad.xls")]
    public void Xlsx_refuses_other_workbook_extensions(string name)
        => Assert.Throws<ArgumentException>(() => TableFile.ReadXlsx(TableFile.WriteXlsx(Sample, "Rows"), "Rows", name));

    [Fact]
    public void Xlsx_refuses_oversized_uncompressed_parts()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var stream = zip.CreateEntry("oversize.xml").Open()) stream.Write(new byte[TableFile.MaximumBytes + 1]);
        Assert.Contains("16 MiB", Assert.Throws<ArgumentException>(() => TableFile.ReadXlsx(bytes.ToArray(), "Rows")).Message);
    }

    [Fact]
    public void Writers_refuse_oversized_expanded_tables_before_materializing_the_whole_output()
    {
        string[] columns = Enumerable.Range(0, 1024).Select(i => "C" + i).ToArray();
        string[] row = columns.Select(_ => "").ToArray();
        var table = new TableData(columns, Enumerable.Repeat(row, 10000));
        Assert.Contains("16 MiB", Assert.Throws<ArgumentException>(() => TableFile.WriteCsv(table)).Message);
        Assert.Contains("16 MiB", Assert.Throws<ArgumentException>(() => TableFile.WriteXlsx(table, "Rows")).Message);
    }

    [Fact]
    public void Codecs_refuse_10001_data_rows_and_cell_limits()
    {
        var table = new TableData(new[] { "A" }, Enumerable.Range(0, 10001).Select(_ => new[] { "x" }));
        Assert.Throws<ArgumentException>(() => TableFile.WriteCsv(table));
        Assert.Throws<ArgumentException>(() => TableFile.WriteXlsx(table, "Rows"));
        Assert.Throws<ArgumentException>(() => TableFile.ReadCsv(Encoding.UTF8.GetBytes("A\n" + string.Concat(Enumerable.Repeat("x\n", 10001)))));
        byte[] tooMany = ModifiedSheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A</t></is></c></row>"
            + string.Concat(Enumerable.Range(2, 10001).Select(r => "<row r=\"" + r + "\"><c r=\"A" + r + "\"><v>1</v></c></row>")));
        Assert.Throws<ArgumentException>(() => TableFile.ReadXlsx(tooMany, "Rows"));
        Assert.Throws<ArgumentException>(() => TableFile.WriteXlsx(new TableData(new[] { "A" }, new[] { new[] { new string('x', 32768) } }), "Rows"));
    }

    [Fact]
    public void Xlsx_reads_only_relationship_parts_and_refuses_ambiguous_sheets()
    {
        byte[] bytes = TableFile.WriteXlsx(Sample, "Other");
        Equal(Sample, TableFile.ReadXlsx(bytes, "Rows"));
        using var buffer = new MemoryStream(); buffer.Write(bytes); buffer.Position = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("xl/workbook.xml")!;
            XDocument workbook; using (var stream = entry.Open()) workbook = XDocument.Load(stream);
            var sheet = workbook.Descendants().Single(e => e.Name.LocalName == "sheet");
            var copy = new XElement(sheet); copy.SetAttributeValue("name", "Another"); sheet.AddAfterSelf(copy);
            entry.Delete(); using var output = zip.CreateEntry("xl/workbook.xml").Open(); workbook.Save(output);
        }
        Assert.Contains("ambiguous", Assert.Throws<ArgumentException>(() => TableFile.ReadXlsx(buffer.ToArray(), "Rows")).Message);
    }

    [Fact]
    public void Excel_saved_escape_fixture_decodes_once_and_roundtrips_literal_escapes()
    {
        var table = TableFile.ReadXlsx(File.ReadAllBytes(Fixture("excel-escaped-strings.xlsx")), "Rows");
        Assert.Equal(new[] { "line\rbreak", "_x0041_", "_x005F_", "inline\rtext" }, table.Rows.Select(row => row[1]));
        byte[] bytes = TableFile.WriteXlsx(table, "Rows");
        Equal(table, TableFile.ReadXlsx(bytes, "Rows"));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("_x005F_x0041_", xml); Assert.Contains("_x005F_x005F_", xml);
    }

    [Fact]
    public void Xlsx_exports_have_fixed_timestamps_order_and_sha256()
    {
        byte[] first = TableFile.WriteXlsx(Sample, "Rows"), second = TableFile.WriteXlsx(Sample, "Rows");
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(first), System.Security.Cryptography.SHA256.HashData(second));
        using var zip = new ZipArchive(new MemoryStream(first));
        Assert.Equal(new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml" },
            zip.Entries.Select(entry => entry.FullName));
        Assert.All(zip.Entries, entry => Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime));
    }

    [Fact]
    public void Xlsx_counts_decompressed_bytes_even_when_declared_sizes_lie()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open())) writer.Write(new string(' ', TableFile.MaximumBytes + 1));
        byte[] bytes = buffer.ToArray();
        for (int i = 0; i < bytes.Length - 28; i++)
        {
            uint signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x02014b50) BitConverter.GetBytes(1U).CopyTo(bytes, i + 24);
            if (signature == 0x04034b50) BitConverter.GetBytes(1U).CopyTo(bytes, i + 22);
        }
        var error = Assert.Throws<ArgumentException>(() => TableFile.ReadXlsx(bytes, "Rows", "lying.xlsx"));
        Assert.Contains("lying.xlsx: row 1, column 1:", error.Message);
        Assert.Contains("Invalid, encrypted or unsupported XLSX", error.Message);
    }

    [Fact]
    public void Decompression_stream_limit_counts_actual_bytes_and_stops_at_the_first_excess_byte()
    {
        using var compressed = new MemoryStream();
        using (var writer = new DeflateStream(compressed, CompressionLevel.Optimal, true)) writer.Write(new byte[TableFile.MaximumBytes + 1]);
        compressed.Position = 0;
        using var stream = new DeflateStream(compressed, CompressionMode.Decompress);
        long actual = 0;
        var error = Assert.Throws<ArgumentException>(() => TableFile.ReadXlsxPart(stream, "lying.xlsx", "xl/workbook.xml", ref actual));
        Assert.Contains("lying.xlsx: row 1, column 1:", error.Message);
        Assert.Contains("16 MiB while reading", error.Message);
        Assert.Equal(TableFile.MaximumBytes + 1L, actual);
    }

    [Fact]
    public void Readers_ignore_empty_rows_and_empty_styled_trailing_cells()
    {
        var csv = TableFile.ReadCsv(Encoding.UTF8.GetBytes("A,B\n\n,,\nx,y\n,\n"));
        Assert.Equal(new[] { "x", "y" }, csv.Rows.Single());
        var xlsx = TableFile.ReadXlsx(ModifiedSheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A</t></is></c>"
            + "<c r=\"XFD1\" s=\"1\"/></row><row r=\"2\"><c r=\"XFD2\" s=\"1\"/></row>"
            + "<row r=\"5\"><c r=\"A5\" t=\"inlineStr\"><is><t>x</t></is></c><c r=\"XFD5\" s=\"1\"/></row><row r=\"10002\"/>"), "Rows");
        Assert.Equal(new[] { "A" }, xlsx.Columns); Assert.Equal(new[] { "x" }, xlsx.Rows.Single());
        var leading = TableFile.ReadCsv(Encoding.UTF8.GetBytes("\n,,\nA,B\nx,y\n"));
        Assert.Equal(new[] { "A", "B" }, leading.Columns); Assert.Equal(new[] { "x", "y" }, leading.Rows.Single());
        var styled = TableFile.ReadXlsx(ModifiedSheet("<row r=\"1\"><c r=\"XFD1\" s=\"1\"/></row>"
            + "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>A</t></is></c></row>"
            + "<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>x</t></is></c></row>"), "Rows");
        Assert.Equal(new[] { "A" }, styled.Columns); Assert.Equal(new[] { "x" }, styled.Rows.Single());
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\ttext")]
    [InlineData("\rtext")]
    public void Formula_prefix_is_present_in_both_files_and_removed_exactly_once(string value)
    {
        var table = new TableData(new[] { "Value" }, new[] { new[] { value } });
        byte[] csv = TableFile.WriteCsv(table), xlsx = TableFile.WriteXlsx(table, "Rows");
        Assert.Contains("\"'" + value + "\"", Encoding.UTF8.GetString(csv));
        Equal(table, TableFile.ReadCsv(csv)); Equal(table, TableFile.ReadXlsx(xlsx, "Rows"));
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        Assert.Equal("'" + value, XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "t").Last().Value);
        Assert.Equal("''" + value, TableFile.ReadCsv(Encoding.UTF8.GetBytes("A\n\"''" + value + "\"\n")).Rows.Single()[0]);
    }

    public static IEnumerable<object[]> InvalidXmlCharacters() => Enumerable.Range(1, 8).Concat(new[] { 11, 12 })
        .Concat(Enumerable.Range(14, 18)).Concat(new[] { 0xd800, 0xdc00 }).Select(value => new object[] { value });

    [Theory, MemberData(nameof(InvalidXmlCharacters))]
    public void Validate_refuses_xml_invalid_characters_before_either_writer(int character)
    {
        var table = new TableData(new[] { "A" }, new[] { new[] { new string((char)character, 1) } });
        Assert.Equal("bad.csv: row 2, column 1: XML-invalid character.",
            Assert.Throws<ArgumentException>(() => TableFile.WriteCsv(table, "bad.csv")).Message);
        Assert.Equal("bad.xlsx: row 2, column 1: XML-invalid character.",
            Assert.Throws<ArgumentException>(() => TableFile.WriteXlsx(table, "Rows", "bad.xlsx")).Message);
    }

    [Fact]
    public void Formula_prefix_keeps_the_existing_logical_cell_limits()
    {
        var csv = new TableData(new[] { "A" }, new[] { new[] { "=" + new string('x', 65535) } });
        Equal(csv, TableFile.ReadCsv(TableFile.WriteCsv(csv)));
        var xlsx = new TableData(new[] { "A" }, new[] { new[] { "=" + new string('x', 32766) } });
        Equal(xlsx, TableFile.ReadXlsx(TableFile.WriteXlsx(xlsx, "Rows"), "Rows"));
    }

    internal static byte[] ModifiedSheet(string rows)
    {
        using var buffer = new MemoryStream(); buffer.Write(TableFile.WriteXlsx(new TableData(new[] { "A" }, Array.Empty<string[]>()), "Rows"));
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, true))
        {
            zip.GetEntry("xl/worksheets/sheet1.xml")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
            writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + rows + "</sheetData></worksheet>");
        }
        return buffer.ToArray();
    }
    private static void Equal(TableData expected, TableData actual)
    {
        Assert.Equal(expected.Columns, actual.Columns); Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++) Assert.Equal(expected.Rows[i], actual.Rows[i]);
    }
}
