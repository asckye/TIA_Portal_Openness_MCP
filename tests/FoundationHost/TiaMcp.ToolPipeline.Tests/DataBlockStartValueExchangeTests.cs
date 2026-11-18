using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class DataBlockStartValueExchangeTests
{
    public static IEnumerable<object[]> Fixtures() => new[] { "db-v15.1.xml", "db-v21.xml" }.Select(name => new object[] { name });
    private static XDocument Read(string name = "db-v21.xml") => DataBlockStartValueExchange.Parse(File.ReadAllText(TableFileTests.Fixture(name)));
    private static XElement Member(XDocument document, string name) => document.Descendants()
        .Single(e => e.Name.LocalName == "Member" && (string?)e.Attribute("Name") == name);

    [Theory, MemberData(nameof(Fixtures))]
    public void Scalar_rows_follow_inspection_order_and_roundtrip_without_changes(string name)
    {
        var template = Read(name); var table = DataBlockStartValueExchange.ToTable(template);
        Assert.Equal(new[] { "Section", "MemberPath", "DataType", "StartValue" }, table.Columns);
        Assert.Equal(new[] { "Enabled", "DefaultInt", "电机%2F运行%252F", "Motor/Config/Speed" }, table.Rows.Select(r => r[1]));
        Assert.Equal(new[] { "Static", "DefaultInt", "Int", "" }, table.Rows[1]);
        Assert.Equal(9, DataBlockStartValueExchange.NotEditable(template));
        var result = DataBlockStartValueExchange.FromTable(template, table);
        Assert.True(XNode.DeepEquals(template, result.Document));
        Assert.Equal(PlcDocumentEditing.Canonical(template), PlcDocumentEditing.Canonical(result.Document));
        Assert.Equal(4, (int)result.Summary["unchanged"]!); Assert.Equal(0, (int)result.Summary["updated"]!);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Edits_only_start_values_adds_missing_element_in_namespace_and_keeps_omitted_members(string name)
    {
        var template = Read(name); var table = DataBlockStartValueExchange.ToTable(template);
        table.Rows.RemoveAt(2); table.Rows[0][3] = "true"; table.Rows[1][3] = "42"; table.Rows[2][3] = "2.5";
        var result = DataBlockStartValueExchange.FromTable(template, table);
        Assert.Equal(3, (int)result.Summary["updated"]!); Assert.Equal(1, (int)result.Summary["keptFromTemplate"]!);
        Assert.Equal("42", PlcDocumentEditing.Value(Member(result.Document, "DefaultInt"), "StartValue"));
        Assert.Equal(Member(result.Document, "DefaultInt").Name.Namespace,
            Member(result.Document, "DefaultInt").Elements().Single().Name.Namespace);
        var expected = new XDocument(template);
        Member(expected, "DefaultInt").Add(new XElement(Member(expected, "DefaultInt").Name.Namespace + "StartValue", "42"));
        Member(expected, "Enabled").Elements().Single().Value = "true";
        Member(expected, "Speed").Elements().Single().Value = "2.5";
        Assert.True(XNode.DeepEquals(expected, result.Document));
        Assert.False(Member(template, "DefaultInt").HasElements);
        Assert.Equal("DefaultInt", (string?)result.Summary["changedRows"]![1]!["memberPath"]);
    }

    [Theory]
    [InlineData("type", "DataType must equal")]
    [InlineData("unknown", "Unknown or non-editable")]
    [InlineData("duplicate", "Duplicate member key")]
    [InlineData("aliasDuplicate", "Duplicate member key")]
    [InlineData("struct", "Unknown or non-editable")]
    [InlineData("array", "Unknown or non-editable")]
    [InlineData("locked", "Unknown or non-editable")]
    [InlineData("section", "Unknown or non-editable")]
    [InlineData("column", "Expected only Section")]
    public void Invalid_rows_refuse_without_mutating_template(string change, string message)
    {
        var template = Read(); var before = template.ToString(); var table = DataBlockStartValueExchange.ToTable(template);
        if (change == "type") table.Rows[0][2] = "Int";
        if (change == "unknown") table.Rows[0][1] = "Missing";
        if (change == "duplicate") table.Rows.Add(table.Rows[0].ToArray());
        if (change == "aliasDuplicate") { table.Rows.Add(table.Rows[0].ToArray()); table.Rows.Last()[1] = "%45nabled"; }
        if (change == "struct") table.Rows.Add(new[] { "Static", "Motor", "Struct", "1" });
        if (change == "array") table.Rows.Add(new[] { "Static", "Samples", "Array[0..1] of Int", "1" });
        if (change == "locked") table.Rows.Add(new[] { "Static", "Locked", "Int", "1" });
        if (change == "section") table.Rows[0][0] = "Input";
        if (change == "column") table = new TableData(new[] { "Section", "MemberPath", "DataType", "Other" }, table.Rows);
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.FromTable(template, table)).Message);
        Assert.Equal(before, template.ToString());
    }

    [Theory]
    [InlineData("SW.Blocks.InstanceDB")]
    [InlineData("SW.Blocks.FB")]
    [InlineData("SW.Types.PlcStruct")]
    public void Other_kinds_point_to_patch_workflow(string kind)
        => Assert.Contains("PatchPlcBlockDocument", Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.Parse(
            File.ReadAllText(TableFileTests.Fixture("db-v21.xml")).Replace("SW.Blocks.GlobalDB", kind))).Message);

    [Fact]
    public void Library_binding_and_unsafe_xml_refuse()
    {
        string xml = File.ReadAllText(TableFileTests.Fixture("db-v21.xml"));
        Assert.Contains("Library-connected", Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.Parse(
            xml.Replace("<Name>", "<LibraryTypeGuid>type</LibraryTypeGuid><Name>"))).Message);
        Assert.Throws<System.Xml.XmlException>(() => DataBlockStartValueExchange.Parse(
            "<!DOCTYPE Document [<!ENTITY test 'value'>]>" + xml.Substring(xml.IndexOf("<Document>", StringComparison.Ordinal))));
    }

    [Fact]
    public void More_than_100_edits_use_file_limits_and_preview_changes_are_bounded()
    {
        var template = Read(); var section = Member(template, "Enabled").Parent!; section.RemoveNodes();
        for (int i = 0; i < 120; i++) section.Add(new XElement(section.Name.Namespace + "Member",
            new XAttribute("Name", "Value" + i), new XAttribute("Datatype", "Int")));
        var table = DataBlockStartValueExchange.ToTable(template);
        foreach (var row in table.Rows) row[3] = "42";
        var result = DataBlockStartValueExchange.FromTable(template, table);
        Assert.Equal(120, (int)result.Summary["updated"]!); Assert.Equal(50, result.Summary["changedRows"]!.AsArray().Count);
        Assert.True((bool)result.Summary["changedRowsTruncated"]!);
        Assert.All(DataBlockStartValueExchange.ToTable(result.Document).Rows, row => Assert.Equal("42", row[3]));
        Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.FromTable(template,
            new TableData(table.Columns, Enumerable.Repeat(table.Rows[0], 10001))));
    }

    [Theory]
    [InlineData("Int", "1")]
    [InlineData("String[80]", "'1'")]
    [InlineData("WString[254]", "WSTRING#'1'")]
    [InlineData("LTime_Of_Day", "LTOD#00:00:00.000000001")]
    [InlineData("LDT", "LDT#2026-01-02-00:00:00.000000000")]
    public void Elementary_missing_values_are_editable_while_patch_still_requires_an_existing_value(string type, string value)
    {
        var template = Read(); Member(template, "DefaultInt").SetAttributeValue("Datatype", type);
        var table = DataBlockStartValueExchange.ToTable(template); table.Rows.Single(r => r[1] == "DefaultInt")[3] = value;
        Assert.Equal(value, PlcDocumentEditing.Value(Member(DataBlockStartValueExchange.FromTable(template, table).Document, "DefaultInt"), "StartValue"));
        var changes = new JsonArray(new JsonObject { ["action"] = "setMemberStartValue", ["section"] = "Static",
            ["memberPath"] = "DefaultInt", ["expectedValue"] = "", ["value"] = "1" });
        Assert.Throws<InvalidOperationException>(() => PlcDocumentEditing.Patch(template.ToString(), changes.ToJsonString(),
            PlcDocumentEditing.HashText(PlcDocumentEditing.Canonical(template))));
    }

    [Theory]
    [InlineData("Struct")]
    [InlineData("Array[0..1] of Int")]
    [InlineData("DTL")]
    [InlineData("\"MotorType\"")]
    public void Shared_edit_refuses_invented_struct_array_and_udt_values(string type)
    {
        var template = Read(); Member(template, "DefaultInt").SetAttributeValue("Datatype", type);
        Assert.Throws<ArgumentException>(() => PlcDocumentEditing.SetMemberStartValue(template, "Static", new[] { "DefaultInt" }, "", "1"));
        Assert.False(Member(template, "DefaultInt").HasElements);
    }

    [Theory]
    [InlineData("Array[0..1] of Struct")]
    [InlineData("\"MotorType\"")]
    public void Scalars_nested_under_arrays_or_typed_members_are_not_offered(string type)
    {
        var template = Read(); Member(template, "Motor").SetAttributeValue("Datatype", type);
        Assert.DoesNotContain(DataBlockStartValueExchange.ToTable(template).Rows, row => row[1].StartsWith("Motor/", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.FromTable(template,
            new TableData(new[] { "Section", "MemberPath", "DataType", "StartValue" }, new[] { new[] { "Static", "Motor/Config/Speed", "Real", "2" } })));
    }
}
