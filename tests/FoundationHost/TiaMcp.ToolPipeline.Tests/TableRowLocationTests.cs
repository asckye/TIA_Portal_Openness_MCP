using System.Text;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using Xunit;

public sealed class TableRowLocationTests
{
    [Theory]
    [InlineData("csv", "db")]
    [InlineData("xlsx", "db")]
    [InlineData("csv", "tags")]
    [InlineData("xlsx", "tags")]
    public void Merge_errors_report_original_rows_after_empty_rows_and_header_offsets(string format, string kind)
    {
        string source = File.ReadAllText(TableFileTests.Fixture(kind == "db" ? "db-v21.xml" : "tags-v21.xml"));
        var template = kind == "db" ? DataBlockStartValueExchange.Parse(source) : PlcTagTableExchange.Parse(source);
        var original = kind == "db" ? DataBlockStartValueExchange.ToTable(template) : PlcTagTableExchange.ToTable(template);
        var one = new TableData(original.Columns, new[] { original.Rows[0] });
        TableData table;
        if (format == "csv")
        {
            var lines = Encoding.UTF8.GetString(TableFile.WriteCsv(one)).TrimStart('\ufeff').Split('\n');
            table = TableFile.ReadCsv(Encoding.UTF8.GetBytes("\n" + lines[0] + "\n\n" + lines[1] + "\n\n" + lines[1] + "\n"));
        }
        else
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XElement Row(int r, string[] cells) => new XElement(ns + "row", new XAttribute("r", r), cells.Select((value, i) =>
                new XElement(ns + "c", new XAttribute("r", ((char)('A' + i)).ToString() + r), new XAttribute("t", "inlineStr"),
                    new XElement(ns + "is", new XElement(ns + "t", value)))));
            table = TableFile.ReadXlsx(TableFileTests.ModifiedSheet(Row(2, one.Columns).ToString(SaveOptions.DisableFormatting)
                + Row(4, one.Rows[0]).ToString(SaveOptions.DisableFormatting) + Row(6, one.Rows[0]).ToString(SaveOptions.DisableFormatting)), "Rows");
        }
        Assert.Equal(2, table.HeaderRowNumber); Assert.Equal(4, table.RowNumber(0)); Assert.Equal(6, table.RowNumber(1));
        var error = Assert.Throws<ArgumentException>(() => {
            if (kind == "db") DataBlockStartValueExchange.FromTable(template, table);
            else PlcTagTableExchange.FromTable(template, table);
        });
        Assert.Equal(6, error.Data["tableRow"]); Assert.Contains("Duplicate", error.Message);
    }

    [Fact]
    public void Csv_multiline_cells_and_dropped_blank_rows_preserve_physical_line_numbers()
    {
        var table = TableFile.ReadCsv(Encoding.UTF8.GetBytes("A,B\r\n\"line\r\nline\",one\r\n\r\ntwo,three\r\n"));
        Assert.Equal(2, table.RowNumber(0)); Assert.Equal(5, table.RowNumber(1));
    }

    [Theory]
    [InlineData("db")]
    [InlineData("tags")]
    public void Unknown_columns_report_the_original_header_row(string kind)
    {
        string xml = File.ReadAllText(TableFileTests.Fixture(kind == "db" ? "db-v21.xml" : "tags-v21.xml"));
        var template = kind == "db" ? DataBlockStartValueExchange.Parse(xml) : PlcTagTableExchange.Parse(xml);
        string header = kind == "db" ? "Section,MemberPath,DataType,Other" : "Kind,Name,DataType,Address,Value,Other";
        var table = TableFile.ReadCsv(Encoding.UTF8.GetBytes("\n\n" + header + "\n"));
        var error = Assert.Throws<ArgumentException>(() => {
            if (kind == "db") DataBlockStartValueExchange.FromTable(template, table);
            else PlcTagTableExchange.FromTable(template, table);
        });
        Assert.Equal(3, error.Data["tableRow"]);
    }
}
