using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class PlcVisualComparisonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tia-visual-comparison-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    private static string Block(string release, string members, string kind = "OB") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <Engineering version="V{{release}}" />
          <SW.Blocks.{{kind}} ID="0">
            <AttributeList>
              <Interface>
                <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v{{(release == "14sp1" ? "2" : "5")}}">
                  <Section Name="{{(kind == "OB" ? "Input" : "Static")}}">{{members}}</Section>
                </Sections>
              </Interface>
              <Name>Example</Name>
              <ProgrammingLanguage>{{(kind == "OB" ? "LAD" : "DB")}}</ProgrammingLanguage>
            </AttributeList>
            <ObjectList />
          </SW.Blocks.{{kind}}>
        </Document>
        """;

    private static string ObMembers(string attributes = "", string comment = "First call") => $$"""
        <Member Name="Initial_Call" Datatype="Bool" Informative="true" {{attributes}}>
          <Comment><MultiLanguageText Lang="en-US">{{comment}}</MultiLanguageText></Comment>
        </Member>
        <Member Name="Remanence" Datatype="Bool" Informative="true">
          <Comment><MultiLanguageText Lang="en-US">Retained data</MultiLanguageText></Comment>
        </Member>
        """;

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private JsonObject Compare(string left, string right) => PlcVisualComparison.Compare(
        Write("left.xml", left), Write("right.xml", right), out _);

    [Theory]
    [MemberData(nameof(Releases))]
    public void Omitted_member_defaults_compare_equal(string release)
    {
        var result = Compare(Block(release, ObMembers("Remanence=\"NonRetain\" Accessibility=\"Public\"")),
            Block(release, ObMembers()));
        Assert.Equal("Equal", (string?)result["state"]);
        Assert.All(result["comparison"]!["Interface"]!["Members"]!.AsArray(), row => Assert.Equal(0, (int?)row!["State"]));
        Assert.Empty(result["interfaceDifferences"]!.AsArray());
        Assert.True((bool?)result["documentDiff"]!["identicalAfterNormalization"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Real_member_change_stays_changed(string release)
    {
        var result = Compare(Block(release, ObMembers("Remanence=\"Retain\"")), Block(release, ObMembers()));
        Assert.Equal("Changed", (string?)result["state"]);
        var difference = Assert.Single(result["interfaceDifferences"]!.AsArray())!;
        Assert.Equal("Retain", (string?)difference["field"]);
        Assert.Equal("Input", (string?)difference["section"]);
        Assert.Equal("Initial_Call", (string?)difference["member"]);
        Assert.Equal("True", (string?)difference["left"]);
        Assert.Equal("Unspecified", (string?)difference["right"]);
        Assert.False((bool?)result["documentDiff"]!["identicalAfterNormalization"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Explicit_nondefault_remanence_is_not_an_omitted_default(string release)
    {
        foreach (string value in new[] { "SetInIDB", "Classic" })
        foreach (bool reverse in new[] { false, true })
        {
            string changed = Block(release, ObMembers("Remanence=\"" + value + "\""));
            string defaults = Block(release, ObMembers("Remanence=\"NonRetain\""));
            var result = reverse ? Compare(defaults, changed) : Compare(changed, defaults);
            Assert.Equal("Changed", (string?)result["state"]);
            Assert.Contains(result["interfaceDifferences"]!.AsArray(), row => (string?)row!["field"] == "Retain");
            Assert.False((bool?)result["documentDiff"]!["identicalAfterNormalization"]);
        }
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Comment_change_is_reported(string release)
    {
        var result = Compare(Block(release, ObMembers(comment: "Changed comment")), Block(release, ObMembers()));
        Assert.Equal("Changed", (string?)result["state"]);
        var difference = Assert.Single(result["interfaceDifferences"]!.AsArray())!;
        Assert.Equal("Comment", (string?)difference["field"]);
        Assert.Equal("en-US", (string?)difference["key"]);
        Assert.Equal("Changed comment", (string?)difference["left"]);
        Assert.Equal("First call", (string?)difference["right"]);
        Assert.False((bool?)result["documentDiff"]!["identicalAfterNormalization"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Start_value_difference_is_not_identical(string release)
    {
        string Member(string value) => "<Member Name=\"Count\" Datatype=\"Int\"><StartValue>" + value + "</StartValue></Member>";
        var result = OfflineAnalysisLogic.CompareFiles(Write("left.xml", Block(release, Member("0"), "GlobalDB")),
            Write("right.xml", Block(release, Member("5"), "GlobalDB")), 0, 80);
        Assert.False((bool?)result["identicalAfterNormalization"]);
        Assert.Contains("StartValue=0", result["hunks"]!.ToJsonString());
        Assert.Contains("StartValue=5", result["hunks"]!.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Xml_and_s7dcl_of_the_same_db_keep_equal_interface_lines(string release)
    {
        var result = OfflineAnalysisLogic.CompareFiles(Write("left.xml", Block(release,
            "<Member Name=\"Count\" Datatype=\"Int\"><StartValue>0</StartValue></Member>", "GlobalDB")),
            Write("right.s7dcl", "DATA_BLOCK \"Example\"\nVAR\nCount : Int := 0;\nEND_VAR\nEND_DATA_BLOCK"), 0, 80, contextLines: 0);
        Assert.DoesNotContain(result["hunks"]!.AsArray().SelectMany(hunk => hunk!["lines"]!.AsArray()),
            line => ((string)line!).Contains("[Interface]", StringComparison.Ordinal));
        Assert.True((bool?)result["identicalAfterNormalization"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Mixed_format_comparisons_include_start_values_from_both_text_forms(string release)
    {
        string xml = Block(release, "<Member Name=\"Count\" Datatype=\"Int\"><StartValue>20</StartValue></Member>", "GlobalDB");
        foreach (bool postamble in new[] { false, true })
        foreach (string value in new[] { "20", "21" })
        {
            string text = postamble ? "DATA_BLOCK Example\nVAR\n\"Count\" : Int;\nEND_VAR\n\"Count\" := " + value + ";\nEND_DATA_BLOCK"
                : "DATA_BLOCK \"Example\"\nVAR\nCount : Int := " + value + ";\nEND_VAR\nEND_DATA_BLOCK";
            string left = Write("left.xml", xml), right = Write("right.s7dcl", text);
            Assert.Equal(value == "20", (bool?)OfflineAnalysisLogic.CompareFiles(left, right, 0, 80)["identicalAfterNormalization"]);
            Assert.Equal(value == "20", (bool?)OfflineAnalysisLogic.CompareFiles(right, left, 0, 80)["identicalAfterNormalization"]);
        }
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Nested_default_only_changes_leave_parent_equal(string release)
    {
        string Members(string attributes) => "<Member Name=\"Container\" Datatype=\"Struct\"><Member Name=\"Flag\" Datatype=\"Bool\" "
            + attributes + "/></Member>";
        var result = Compare(Block(release, Members("Informative=\"false\"")), Block(release, Members("")));
        Assert.Equal("Equal", (string?)result["state"]);
        Assert.All(result["comparison"]!["Interface"]!["Members"]!.AsArray(), row => Assert.Equal(0, (int?)row!["State"]));
        Assert.Empty(result["interfaceDifferences"]!.AsArray());
        Assert.True((bool?)result["documentDiff"]!["identicalAfterNormalization"]);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Member_traits_preserve_nondefaults_in_fixed_order(string release)
    {
        const string member = """
            <Member Name="Values" Datatype="Array[0..1] of Int" Informative="true" Accessibility="Private" Remanence="Retain">
              <StartValue> 0 </StartValue>
              <Comment>
                <MultiLanguageText Lang="fr-FR"> Valeurs </MultiLanguageText>
                <MultiLanguageText Lang="en-US"> Values </MultiLanguageText>
              </Comment>
              <Subelement Path="[1]"><StartValue> 5 </StartValue></Subelement>
              <Subelement Path="[0]"><StartValue> 2 </StartValue></Subelement>
            </Member>
            """;
        var document = OfflineAnalysisLogic.ParseText(Block(release, member, "GlobalDB"), "Example.xml");
        Assert.Equal(" Remanence=Retain Accessibility=Private Informative=true StartValue=0"
            + " Comment[en-US]=Values Comment[fr-FR]=Valeurs Subelement[[0]]=2 Subelement[[1]]=5", document.Members[0].Traits);
        Assert.Contains(document.Canonical, line => line.EndsWith(document.Members[0].Traits, StringComparison.Ordinal));
        var defaults = OfflineAnalysisLogic.ParseText(Block(release,
            "<Member Name=\"Count\" Datatype=\"Int\" Remanence=\"NonRetain\" Accessibility=\"Public\" Informative=\"false\" />"), "Example.xml");
        Assert.Empty(defaults.Members[0].Traits);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
