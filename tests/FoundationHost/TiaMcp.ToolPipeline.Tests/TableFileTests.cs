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
        byte[] tooMany = ModifiedSheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A</t></is></c></row><row r=\"10002\"/>");
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

    private static byte[] ModifiedSheet(string rows)
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
