using System.Text;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using Xunit;

public sealed class DataBlockLiteralTests
{
    private static XDocument Template(string type, string? before = null)
    {
        var member = new XElement("Member", new XAttribute("Name", "Value"), new XAttribute("Datatype", type),
            before == null ? null : new XElement("StartValue", before));
        var sections = new XElement("Sections", new XElement("Section", new XAttribute("Name", "Static"), member));
        return DataBlockStartValueExchange.Parse(new XDocument(new XElement("Document", new XElement("SW.Blocks.GlobalDB",
            new XElement("AttributeList", new XElement("Name", "DB_Literals"), new XElement("Interface", sections))))).ToString());
    }
    private static DataBlockStartValueEdit Edit(XDocument template, string value)
    {
        var table = DataBlockStartValueExchange.ToTable(template); Assert.Single(table.Rows)[3] = value;
        return DataBlockStartValueExchange.FromTable(template, table);
    }
    private static string Start(XDocument document) => document.Descendants("StartValue").Single().Value;

    [Theory]
    [InlineData("Bool", "TRUE", "true")]
    [InlineData("SInt", "-128", "-128")]
    [InlineData("Int", "+0002", "2")]
    [InlineData("DInt", "2147483647", "2147483647")]
    [InlineData("LInt", "-9223372036854775808", "-9223372036854775808")]
    [InlineData("USInt", "255", "255")]
    [InlineData("UInt", "65535", "65535")]
    [InlineData("UDInt", "4294967295", "4294967295")]
    [InlineData("ULInt", "18446744073709551615", "18446744073709551615")]
    [InlineData("Byte", "255", "255")]
    [InlineData("Word", "65535", "65535")]
    [InlineData("DWord", "4294967295", "4294967295")]
    [InlineData("LWord", "18446744073709551615", "18446744073709551615")]
    [InlineData("Real", "2", "2.0")]
    [InlineData("Real", "0.1", "0.1")]
    [InlineData("Real", "2e2", "200.0")]
    [InlineData("LReal", "2", "2.0")]
    [InlineData("LReal", "1.2345678901234567", "1.2345678901234567")]
    [InlineData("Time", "TIME#1500ms", "T#1s_500ms")]
    [InlineData("Time", "T#-2s", "T#-2s")]
    [InlineData("LTime", "LTIME#1001ns", "LT#1us_1ns")]
    [InlineData("S5Time", "S5TIME#100ms", "S5T#100ms")]
    [InlineData("Date", "DATE#2026-1-2", "D#2026-01-02")]
    [InlineData("Time_Of_Day", "TIME_OF_DAY#1:02:03.4", "TOD#01:02:03.400")]
    [InlineData("LTime_Of_Day", "LTOD#01:02:03.000000001", "LTOD#01:02:03.000000001")]
    [InlineData("Date_And_Time", "DT#2026-01-02-01:02:03", "DT#2026-01-02-01:02:03.000")]
    [InlineData("LDT", "LDT#2026-01-02-01:02:03", "LDT#2026-01-02-01:02:03.000000000")]
    [InlineData("Char", "CHAR#'A'", "'A'")]
    [InlineData("WChar", "'字'", "WCHAR#'字'")]
    [InlineData("String", "'Motor$'s'", "'Motor$'s'")]
    [InlineData("String[10]", "'00123'", "'00123'")]
    [InlineData("WString[10]", "'电机'", "WSTRING#'电机'")]
    public void Excel_like_inputs_normalize_before_the_change_is_planned(string type, string input, string expected)
    {
        var result = Edit(Template(type), input);
        Assert.Equal(expected, Start(result.Document));
        Assert.Equal(expected, (string?)result.Summary["changedRows"]![0]!["new"]);
    }

    [Theory]
    [InlineData("Bool", "1", "true or false")]
    [InlineData("SInt", "128", "within")]
    [InlineData("Int", "-32769", "within")]
    [InlineData("DInt", "2147483648", "within")]
    [InlineData("LInt", "9223372036854775808", "within")]
    [InlineData("USInt", "-1", "within")]
    [InlineData("UInt", "65536", "within")]
    [InlineData("UDInt", "4294967296", "within")]
    [InlineData("ULInt", "18446744073709551616", "within")]
    [InlineData("Byte", "256", "within")]
    [InlineData("Word", "65536", "within")]
    [InlineData("DWord", "4294967296", "within")]
    [InlineData("LWord", "18446744073709551616", "within")]
    [InlineData("Int", "1.5", "Decimal integer")]
    [InlineData("Int", "1E3", "Decimal integer")]
    [InlineData("Int", "1,000", "Decimal integer")]
    [InlineData("Int", "16#10", "radix")]
    [InlineData("UInt", "2#10", "radix")]
    [InlineData("Byte", "8#10", "radix")]
    [InlineData("Real", "0.10000000000000001", "suggested value 0.1")]
    [InlineData("LReal", "0.10000000000000001", "suggested value 0.1")]
    [InlineData("Real", "16777217", "suggested value 16777216.0")]
    [InlineData("Real", "1,5", "invariant")]
    [InlineData("Real", "1E39", "range")]
    [InlineData("LReal", "NaN", "finite")]
    [InlineData("Time", "2", "prefix")]
    [InlineData("Time", "T#2147483648ms", "range")]
    [InlineData("Time", "T#1us", "resolution")]
    [InlineData("Time", "T#_1s", "whole typed units")]
    [InlineData("LTime", "LT#9223372036854775808ns", "range")]
    [InlineData("S5Time", "S5T#1ms", "exact")]
    [InlineData("Date", "1/2/2026", "D#")]
    [InlineData("Date", "D#2026-02-30", "D#")]
    [InlineData("Time_Of_Day", "TOD#24:00:00", "range")]
    [InlineData("Time_Of_Day", "TOD#12:00:00.0001", "resolution")]
    [InlineData("Date_And_Time", "DT#2090-01-01-00:00:00", "range")]
    [InlineData("LDT", "LDT#2262-04-11-23:59:59", "range")]
    [InlineData("Char", "'AB'", "capacity")]
    [InlineData("Char", "'字'", "character")]
    [InlineData("String[2]", "'abc'", "capacity")]
    [InlineData("String", "12345", "single-quoted")]
    [InlineData("String", "'line\nline'", "dollar escapes")]
    public void Lossy_invalid_and_untyped_inputs_refuse_with_the_value_cell_location(string type, string input, string message)
    {
        var template = Template(type); string original = template.ToString();
        var error = Assert.Throws<ArgumentException>(() => Edit(template, input));
        Assert.Contains(message, error.Message); Assert.Equal(2, error.Data["tableRow"]); Assert.Equal(4, error.Data["tableColumn"]);
        Assert.Equal(original, template.ToString());
    }

    [Theory]
    [InlineData("Bool", "false")]
    [InlineData("Int", "0")]
    [InlineData("Real", "0")]
    [InlineData("LReal", "0.0")]
    [InlineData("Time", "T#0ms")]
    [InlineData("LTime", "LT#0ms")]
    [InlineData("S5Time", "S5T#0ms")]
    [InlineData("Date", "D#1990-01-01")]
    [InlineData("Time_Of_Day", "TOD#00:00:00.000")]
    [InlineData("LTime_Of_Day", "LTOD#00:00:00.000000000")]
    [InlineData("Date_And_Time", "DT#1990-01-01-00:00:00.000")]
    [InlineData("LDT", "LDT#1970-01-01-00:00:00.000000000")]
    [InlineData("Char", "'$00'")]
    [InlineData("WChar", "WCHAR#'$0000'")]
    [InlineData("String", "''")]
    [InlineData("WString", "WSTRING#''")]
    public void New_defaults_refuse_but_existing_explicit_defaults_remain_unchanged(string type, string value)
    {
        Assert.Contains("type default", Assert.Throws<ArgumentException>(() => Edit(Template(type), value)).Message);
        var explicitDefault = Template(type, value); var result = Edit(explicitDefault, value);
        Assert.True(XNode.DeepEquals(explicitDefault, result.Document)); Assert.Equal(0, (int)result.Summary["updated"]!);
    }

    [Theory]
    [InlineData("16#0001", "2", "16#0002")]
    [InlineData("16#0001", "16#00ff", "16#00FF")]
    [InlineData("2#0001", "2#10", "2#0010")]
    [InlineData("8#0001", "8#10", "8#0010")]
    public void Radix_is_retained_only_when_evidenced_by_the_member_export(string before, string input, string expected)
        => Assert.Equal(expected, Start(Edit(Template("Word", before), input).Document));

    [Fact]
    public void Emptying_an_existing_value_refuses_with_original_csv_row_and_column()
    {
        var template = Template("Int", "42");
        var table = TableFile.ReadCsv(Encoding.UTF8.GetBytes("\nSection,MemberPath,DataType,StartValue\n\nStatic,Value,Int,\n"));
        var error = Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.FromTable(template, table));
        Assert.Contains("Resetting to the default is not supported", error.Message);
        Assert.Equal(4, error.Data["tableRow"]); Assert.Equal(4, error.Data["tableColumn"]);
        Assert.Equal("42", Start(template));
    }
}
