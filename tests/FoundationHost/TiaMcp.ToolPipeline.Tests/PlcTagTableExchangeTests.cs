using System.Globalization;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using Xunit;

public sealed class PlcTagTableExchangeTests
{
    public static IEnumerable<object[]> Fixtures() => new[] { "tags-v14sp1.xml", "tags-v21.xml" }.Select(name => new object[] { name });
    private static XDocument Read(string name) => PlcTagTableExchange.Parse(File.ReadAllText(TableFileTests.Fixture(name)));

    [Theory, MemberData(nameof(Fixtures))]
    public void Columns_values_and_unchanged_xml_roundtrip(string name)
    {
        var template = Read(name); var table = PlcTagTableExchange.ToTable(template);
        Assert.Equal(new[] { "Kind", "Name", "DataType", "Address", "Value", "ExternalAccessible", "ExternalVisible", "ExternalWritable",
            "Comment[en-US]", "Comment[zh-CN]" }, table.Columns);
        Assert.Equal(new[] { "Tag", "电机/运行", "Bool", "%M0.0", "", "false", "true", "false", "Motor run", "电机运行" }, table.Rows[0]);
        Assert.Equal(new[] { "Constant", "Limit", "Int", "", "10", "", "", "", "", "" }, table.Rows[2]);
        var edited = PlcTagTableExchange.FromTable(template, table);
        Assert.True(XNode.DeepEquals(template, edited.Document));
        Assert.Equal(4, (int)edited.Summary["unchanged"]!); Assert.Equal(0, (int)edited.Summary["updated"]!);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Edits_additions_and_omissions_preserve_unrelated_xml_and_ids(string name)
    {
        var template = Read(name); var table = PlcTagTableExchange.ToTable(template);
        table.Rows[0][3] = "%M2.0"; table.Rows[0][5] = "true"; table.Rows[0][9] = "运行反馈";
        table.Rows[0][8] = "";
        var omitted = table.Rows[1]; table.Rows.RemoveAt(1);
        table.Rows.Add(new[] { "Tag", "New/Tag", "Bool", "%M3.0", "", "false", "true", "true", "New", "新增" });
        table.Rows.Add(new[] { "Constant", "NewLimit", "Int", "", "20", "", "", "", "", "" });
        var result = PlcTagTableExchange.FromTable(template, table);
        Assert.Equal(1, (int)result.Summary["updated"]!); Assert.Equal(2, (int)result.Summary["added"]!);
        Assert.Equal(1, (int)result.Summary["keptFromTemplate"]!);
        var actual = PlcTagTableExchange.ToTable(result.Document);
        Assert.Equal(table.Rows[0], actual.Rows[0]); Assert.Contains(actual.Rows, row => row.SequenceEqual(omitted));
        foreach (string element in new[] { "Engineering", "DocumentInfo" })
            Assert.True(XNode.DeepEquals(template.Root!.Element(element), result.Document.Root!.Element(element)));
        Assert.True(XNode.DeepEquals(template.Descendants("Unrelated").Single(), result.Document.Descendants("Unrelated").Single()));
        var ids = result.Document.Descendants().Attributes("ID").Select(a => ulong.Parse(a.Value, NumberStyles.HexNumber)).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var newTag = result.Document.Descendants("SW.Tags.PlcTag").Single(e => e.Element("AttributeList")!.Element("Name")!.Value == "New/Tag");
        Assert.Equal(new[] { "DataTypeName", "ExternalAccessible", "ExternalVisible", "ExternalWritable", "LogicalAddress", "Name" },
            newTag.Element("AttributeList")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(3, result.Summary["changedRows"]!.AsArray().Count);
    }

    [Fact]
    public void Empty_cells_remove_attributes_and_comments_and_can_add_a_culture_item()
    {
        var template = Read("tags-v21.xml"); var table = PlcTagTableExchange.ToTable(template);
        table.Rows[0][5] = ""; table.Rows[0][9] = ""; table.Rows[1][8] = "Added comment";
        var document = PlcTagTableExchange.FromTable(template, table).Document;
        var first = document.Descendants("SW.Tags.PlcTag").First();
        Assert.Null(first.Element("AttributeList")!.Element("ExternalAccessible"));
        Assert.DoesNotContain(first.Descendants("Culture"), c => c.Value == "zh-CN");
        Assert.Contains(document.Descendants("Text"), t => t.Value == "Added comment");
    }

    [Theory]
    [InlineData("duplicate", "table: row 6, column 2: Duplicate row key.")]
    [InlineData("unknown", "Unknown template column: Unknown")]
    [InlineData("rename", "Renamed row key refused; keep the original Name and add new rows separately.")]
    [InlineData("system", "SystemConstant rows are read-only.")]
    [InlineData("case", "Name collision (case-insensitive): limit.")]
    [InlineData("kind", "Kind change refused for existing name: Limit.")]
    public void Invalid_edits_refuse(string edit, string expected)
    {
        var template = Read("tags-v21.xml"); var table = PlcTagTableExchange.ToTable(template);
        if (edit == "duplicate") table.Rows.Add(table.Rows[0].ToArray());
        if (edit == "unknown") table = new TableData(table.Columns.Concat(new[] { "Unknown" }).ToArray(),
            table.Rows.Select(row => row.Concat(new[] { "value" }).ToArray()));
        if (edit == "rename") table.Rows[0][1] = "Renamed";
        if (edit == "system") table.Rows[3][4] = "99";
        if (edit == "case") table.Rows[2][1] = "limit";
        if (edit == "kind") table.Rows[2][0] = "Tag";
        Assert.Equal(expected, Assert.Throws<ArgumentException>(() => PlcTagTableExchange.FromTable(template, table)).Message);
    }

    [Fact]
    public void Names_collide_across_case_and_kinds_and_constants_never_emit_external_accessible()
    {
        var template = Read("tags-v21.xml"); var table = PlcTagTableExchange.ToTable(template);
        table.Rows.Add(new[] { "Constant", "NewLimit", "Int", "", "20", "false", "", "", "", "" });
        var result = PlcTagTableExchange.FromTable(template, table);
        Assert.All(result.Document.Descendants("SW.Tags.PlcUserConstant"), constant =>
            Assert.Null(constant.Element("AttributeList")!.Element("ExternalAccessible")));
        table.Rows.Add(new[] { "Tag", "newlimit", "Bool", "%M3.0", "", "", "", "", "", "" });
        Assert.Equal("Name collision (case-insensitive): newlimit.",
            Assert.Throws<ArgumentException>(() => PlcTagTableExchange.FromTable(template, table)).Message);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Editing_one_row_preserves_the_other_rows_xml_bytes(string name)
    {
        string xml = File.ReadAllText(TableFileTests.Fixture(name));
        var template = PlcTagTableExchange.Parse(xml); var table = PlcTagTableExchange.ToTable(template);
        table.Rows[0][3] = "%M10.0";
        var result = PlcTagTableExchange.FromTable(template, table);
        using var buffer = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(buffer, new System.Xml.XmlWriterSettings {
            Encoding = new System.Text.UTF8Encoding(false), NewLineHandling = System.Xml.NewLineHandling.Entitize })) result.Document.Save(writer);
        string exported = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        foreach (var original in template.Descendants().Where(e => e.Name.LocalName.StartsWith("SW.Tags.Plc", StringComparison.Ordinal)
            && e.Name.LocalName != "SW.Tags.PlcTagTable").Skip(1))
        {
            string before = original.ToString(SaveOptions.DisableFormatting);
            Assert.Contains(before, xml);
            Assert.Contains(before, exported);
            var after = result.Document.Descendants(original.Name).Single(e => (string?)e.Attribute("ID") == (string?)original.Attribute("ID"));
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(before), System.Text.Encoding.UTF8.GetBytes(after.ToString(SaveOptions.DisableFormatting)));
        }
    }

    [Fact]
    public void Empty_template_keeps_fixed_columns_and_new_tags_use_builder_order()
    {
        var template = Read("tags-v21.xml"); template.Descendants("SW.Tags.PlcTagTable").Single().Element("ObjectList")!.RemoveNodes();
        var table = PlcTagTableExchange.ToTable(template);
        Assert.Equal(new[] { "Kind", "Name", "DataType", "Address", "Value" }, table.Columns);
        table.Rows.Add(new[] { "Tag", "First", "Bool", "%M0.0", "" });
        var result = PlcTagTableExchange.FromTable(template, table);
        Assert.Equal(new[] { "DataTypeName", "LogicalAddress", "Name" },
            result.Document.Descendants("SW.Tags.PlcTag").Single().Element("AttributeList")!.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Xml_dtd_is_prohibited()
        => Assert.Throws<System.Xml.XmlException>(() => PlcTagTableExchange.Parse("<!DOCTYPE Document [<!ENTITY x 'bad'>]><Document>&x;</Document>"));
}
