using System.Text;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using Xunit;

public sealed class DataBlockMemberPolicyTests
{
    [Theory]
    [InlineData("db-review-v15.1.xml")]
    [InlineData("db-review-v21.xml")]
    public void References_bulk_and_nested_udt_members_have_explicit_refusal_reasons(string fixture)
    {
        var template = DataBlockStartValueExchange.Parse(File.ReadAllText(TableFileTests.Fixture(fixture)));
        var table = DataBlockStartValueExchange.ToTable(template); var reasons = DataBlockStartValueExchange.NotEditableReasons(template);
        foreach (var pair in new[] { ("ConstantLiteral", "ConstantName"), ("BulkValue", "IsBulkValue"), ("Typed/NestedValue", "UDT/FB"),
            ("HardwareExpression", "plain exported literal"), ("DefaultHardware", "allow-list") })
        {
            Assert.DoesNotContain(table.Rows, row => row[1] == pair.Item1);
            Assert.Contains(pair.Item2, (string)reasons.Single(r => (string?)r!["memberPath"] == pair.Item1)!["reason"]!);
            var member = template.Descendants().Single(e => e.Name.LocalName == "Member"
                && (string?)e.Attribute("Name") == pair.Item1.Split('/').Last());
            var attempted = new TableData(table.Columns, new[] { new[] { "Static", pair.Item1, (string)member.Attribute("Datatype")!, "1" } });
            Assert.Contains(pair.Item2, Assert.Throws<ArgumentException>(() => DataBlockStartValueExchange.FromTable(template, attempted)).Message);
        }
        Assert.Equal(reasons.Count, DataBlockStartValueExchange.NotEditable(template));
        Assert.True(XNode.DeepEquals(template, DataBlockStartValueExchange.FromTable(template, table).Document));
    }

    [Theory]
    [InlineData("db-review-v15.1.xml")]
    [InlineData("db-review-v21.xml")]
    public void Plain_existing_allow_list_scalars_remain_editable_and_new_value_follows_comments(string fixture)
    {
        var template = DataBlockStartValueExchange.Parse(File.ReadAllText(TableFileTests.Fixture(fixture)));
        var table = DataBlockStartValueExchange.ToTable(template);
        foreach (var pair in new[] { ("DefaultInt", "42"), ("Hardware", "65"), ("AnyDb", "43"), ("SymbolicText", "'beta'") })
            table.Rows.Single(row => row[1] == pair.Item1)[3] = pair.Item2;
        var edited = DataBlockStartValueExchange.FromTable(template, table);
        Assert.Equal(4, (int)edited.Summary["updated"]!);
        byte[][] StableParts(XDocument document) => document.Descendants().Where(e => e.Name.LocalName == "Comment"
            || e.Name.LocalName == "AttributeList" && e.Parent?.Name.LocalName == "Member"
            || e.Name.LocalName == "ObjectList" && e.Parent?.Name.LocalName == "SW.Blocks.GlobalDB")
            .Select(e => Encoding.UTF8.GetBytes(e.ToString(SaveOptions.DisableFormatting))).ToArray();
        var beforeParts = StableParts(template); var afterParts = StableParts(edited.Document);
        Assert.Equal(beforeParts.Length, afterParts.Length);
        for (int i = 0; i < beforeParts.Length; i++) Assert.Equal(beforeParts[i], afterParts[i]);
        var oldFlags = template.Descendants().Attributes("Remanence").Select(a => Encoding.UTF8.GetBytes(a.ToString())).ToArray();
        var newFlags = edited.Document.Descendants().Attributes("Remanence").Select(a => Encoding.UTF8.GetBytes(a.ToString())).ToArray();
        Assert.Equal(oldFlags.Length, newFlags.Length);
        for (int i = 0; i < oldFlags.Length; i++) Assert.Equal(oldFlags[i], newFlags[i]);
        var member = edited.Document.Descendants().Single(e => e.Name.LocalName == "Member" && (string?)e.Attribute("Name") == "DefaultInt");
        Assert.Equal(new[] { "AttributeList", "Comment", "StartValue" }, member.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(member.Name.Namespace + "StartValue", member.Elements().Last().Name);
    }
}
