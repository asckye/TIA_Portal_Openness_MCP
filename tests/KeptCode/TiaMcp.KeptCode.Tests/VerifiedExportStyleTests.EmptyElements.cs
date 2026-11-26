using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed partial class VerifiedExportStyleTests
{
    [Theory]
    [InlineData("DB6_unexp", "MCP_RC_UDT3")]
    [InlineData("DB6_selfclosed", "MCP_RC_UDT3")]
    [InlineData("DB7_verb", "MCP_RC_UDT")]
    [InlineData("MCP_RC_DB4_plain", "MCP_RC_UDT2")]
    public void Recorded_candidates_match_after_full_and_None_readback(string file, string type)
    {
        string candidate = Staged("14sp1-" + file);
        var none = DbNone(candidate);
        foreach (var member in none.Descendants().Where(m => (string?)m.Attribute("Datatype") == "\"" + type + "\""))
            member.RemoveNodes();
        var full = new XDocument(none);
        var instances = full.Descendants().Where(m => (string?)m.Attribute("Datatype") == "\"" + type + "\"").ToArray();
        var ns = instances.First().Name.Namespace;
        const string basic = "<Member Name='Run' Datatype='Bool'/><Member Name='Speed' Datatype='Int'/>";
        var declaration = Section(ns, type switch {
            "MCP_RC_UDT3" => "<Member Name='Lim' Datatype='Int'><StartValue>25</StartValue></Member>" +
                "<Member Name='Gain' Datatype='Real'><StartValue>2.5</StartValue></Member>" +
                "<Member Name='On' Datatype='Bool'><StartValue>true</StartValue></Member>",
            "MCP_RC_UDT2" => "<Member Name='M' Datatype='&quot;MCP_RC_UDT&quot;'/><Member Name='N' Datatype='Int'/>",
            _ => basic
        });
        foreach (var instance in instances)
        {
            var expansion = new XElement(declaration);
            expansion.Descendants(ns + "StartValue").Remove();
            if (type == "MCP_RC_UDT2") Member(expansion, "M").Add(Section(ns, basic));
            instance.Add(expansion);
        }
        var before = WithComputed(full, false);
        if (type == "MCP_RC_UDT3") Member(before, "Count").Element(ns + "StartValue")!.Value = "1";
        if (file == "DB7_verb") Member(before, "W").Element(ns + "StartValue")!.Value = "2";
        if (file == "MCP_RC_DB4_plain") Member(Member(before, "A"), "Speed").Add(new XElement(ns + "StartValue", 9));
        var meta = Apply(candidate, before.ToString(), WithComputed(full, true).ToString(), none.ToString(), true,
            declaration: Declaration(type, declaration), declarationName: type);
        Assert.True((bool?)meta["verificationSuccess"]);
        Assert.Null(meta["comparison"]!["noneDifference"]);
    }

    [Fact]
    public void Recorded_DB6_pair_differs_only_in_the_spelling_of_D()
    {
        var open = PlcDocumentEditing.Parse(Staged("14sp1-DB6_unexp"));
        var closed = PlcDocumentEditing.Parse(Staged("14sp1-DB6_selfclosed"));
        Assert.False(Member(open, "D").IsEmpty);
        Assert.True(Member(closed, "D").IsEmpty);
        Assert.Equal(PlcDocumentEditing.Canonical(open), PlcDocumentEditing.Canonical(closed));
        Member(closed, "D").Remove();
        Assert.NotEqual(PlcDocumentEditing.Canonical(open), PlcDocumentEditing.Canonical(closed));
    }

    [Theory]
    [InlineData("<Value/>", true)]
    [InlineData("<Value> </Value>", false)]
    [InlineData("<Value Unit='ms'/>", false)]
    public void Restored_member_attribute_checks_its_nested_content(string value, bool matches)
    {
        string candidate = Staged("17-T1");
        var none = DbNone(candidate);
        var before = WithComputed(none, false);
        var after = WithComputed(none, true);
        var speed = Member(after, "Speed");
        speed.AddBeforeSelf(new XElement(speed.Name, new XAttribute("Name", "Run"), new XAttribute("Datatype", "Bool")));
        void AddAttribute(XDocument doc, string content) => Member(doc, "Speed").Element(speed.Name.Namespace + "AttributeList")!.Add(
            XElement.Parse($"<Attribute xmlns='{speed.Name.Namespace}' Name='Extension'>{content}</Attribute>", LoadOptions.PreserveWhitespace));
        AddAttribute(before, "<Value></Value>");
        AddAttribute(after, value);
        var meta = Apply(candidate, before.ToString(), after.ToString(), none.ToString(), true);
        Assert.Equal(matches, (bool?)meta["verificationSuccess"]);
        if (!matches) Assert.Contains("Restored attribute differs in after.xml", (string?)meta["comparison"]!["reason"]);
    }

    [Theory]
    [InlineData("14sp1-MCP_RC_FCmin")]
    [InlineData("15.1-N4")]
    [InlineData("16-T3")]
    [InlineData("17-MCP_RC_FC5")]
    [InlineData("18-T3")]
    [InlineData("19-T3")]
    [InlineData("20-T3")]
    [InlineData("21-T3")]
    public void Full_readback_and_preview_agree_across_empty_spellings(string file)
    {
        var doc = XDocument.Parse(Staged(file));
        var member = doc.Descendants().First(PlcDocumentEditing.IsInterfaceMember);
        member.Add(new XElement(member.Name.Namespace + "StartValue"));
        AddExportText(doc);
        foreach (var element in doc.Descendants("Text")) element.RemoveNodes();
        string closed = doc.ToString();
        foreach (var element in doc.Descendants().Where(e => e.IsEmpty)) element.Value = "";
        string open = doc.ToString();
        string before = WithComputed(XDocument.Parse(closed), false).ToString();
        string after = WithComputed(XDocument.Parse(closed), true).ToString();
        var first = Apply(closed, before, after, "", true, extra: false);
        var second = Apply(open, before, after, "", true, extra: false);
        Assert.True((bool?)second["verificationSuccess"]);
        Assert.Equal((string?)first["token"], (string?)second["token"]);
        Assert.Equal((string?)first["plannedFingerprint"], (string?)second["actualFingerprint"]);
        Assert.Contains("<NetworkSource></NetworkSource>", PlcDocumentEditing.PrepareImport(before, open, new JsonArray()));
    }

    [Theory]
    [InlineData("<Text/>", true)]
    [InlineData("<Text> </Text>", false)]
    [InlineData("<Text>a</Text>", false)]
    [InlineData("<Text><![CDATA[]]></Text>", false)]
    [InlineData("<Text/><![CDATA[ ]]>", false)]
    [InlineData("<Text><!--keep--></Text>", false)]
    [InlineData("<Text xml:space='preserve'/>", false)]
    [InlineData("", false)]
    public void Only_an_empty_tag_spelling_can_match_in_either_direction(string changed, bool matches)
    {
        string document = Staged("14sp1-MCP_RC_FCmin");
        string WithText(string text) => document.Replace("<NetworkSource />", "<NetworkSource>" + text + "</NetworkSource>");
        string open = WithText("<Text></Text>");
        string actual = WithText(changed);
        string Canon(string xml) => PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(xml));
        Assert.Equal(matches, Canon(open) == Canon(actual));
        Assert.Equal(matches, XNode.DeepEquals(PlcDocumentEditing.RawBlock(actual), PlcDocumentEditing.RawBlock(open)));
    }

    [Fact]
    public void Restored_leaf_checks_its_XML_attributes_on_full_readback()
    {
        string candidate = Staged("17-T1");
        var none = DbNone(candidate);
        var before = WithComputed(none, false);
        var after = WithComputed(none, true);
        Attributes(before).Add(new XElement("HeaderAuthor", new XAttribute("Lang", "en"), "Owner"));
        Attributes(after).Add(new XElement("HeaderAuthor", new XAttribute("Lang", "de"), "Owner"));
        var speed = Member(after, "Speed");
        speed.AddBeforeSelf(new XElement(speed.Name, new XAttribute("Name", "Run"), new XAttribute("Datatype", "Bool")));
        var meta = Apply(candidate, before.ToString(), after.ToString(), none.ToString(), true);
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Equal("Restored attribute differs in after.xml: HeaderAuthor", (string?)meta["comparison"]!["reason"]);
    }

    [Fact]
    public void An_empty_StartValue_differs_from_an_absent_StartValue()
    {
        string candidate = Staged("17-T1");
        var stated = XDocument.Parse(candidate);
        var speed = Member(stated, "Speed");
        speed.Element(speed.Name.Namespace + "StartValue")!.RemoveNodes();
        var absent = new XDocument(stated);
        Member(absent, "Speed").Element(speed.Name.Namespace + "StartValue")!.Remove();
        var none = DbNone(absent.ToString());
        var before = WithComputed(DbNone(stated.ToString()), false);
        var meta = Apply(stated.ToString(), before.ToString(), WithComputed(none, true).ToString(), none.ToString(), true,
            expectedDifference: "Static/Grp/Inner/Speed: startValue");
        Assert.False((bool?)meta["verificationSuccess"]);
        Assert.Equal("Static/Grp/Inner/Speed: startValue", (string?)meta["comparison"]!["noneDifference"]);
    }

    [Fact]
    public void Declaration_fingerprint_and_difference_ignore_only_empty_spelling()
    {
        const string first = "<Member Name='First' Datatype='Int'></Member>";
        const string rest = "<Member Name='Next' Datatype='Int'><StartValue>2</StartValue></Member>";
        string section = $"<Section xmlns='{Interface}' Name='Static'>{first}{rest}</Section>";
        string closed = section.Replace("Datatype='Int'></Member>", "Datatype='Int'/>");
        Assert.Equal(PlcDocumentEditing.DeclarationFingerprint(XElement.Parse(section)),
            PlcDocumentEditing.DeclarationFingerprint(XElement.Parse(closed)));
        Assert.Equal("Static/Next: startValue", PlcDocumentEditing.ReadbackDifference(section, closed.Replace(">2<", ">3<")));
    }
}
