using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;
using TiaMcp.Logic.Exchange;

public sealed class EngineeringTableReviewToolTests
{
    private static string Fixture(string name) => Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
        "tests", "FoundationHost", "TiaMcp.ToolPipeline.Tests", "Fixtures", "TableExchange", name);

    [Theory]
    [InlineData("culture", 10)]
    [InlineData("attribute", 2)]
    [InlineData("attributeList", 1)]
    public async Task Malformed_xml_duplicates_are_typed_arguments_with_file_row_and_column(string duplicate, int column)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        string source = Path.Combine(fixture.Root, "duplicate.xml"), output = Path.Combine(fixture.Root, "out.csv");
        var document = XDocument.Load(Fixture("tags-v21.xml"));
        var tag = document.Descendants("SW.Tags.PlcTag").First();
        if (duplicate == "attribute") tag.Element("AttributeList")!.Add(new XElement("Name", "Other"));
        if (duplicate == "attributeList") tag.Add(new XElement(tag.Element("AttributeList")!));
        if (duplicate == "culture") tag.Descendants("MultilingualTextItem").First().AddAfterSelf(
            new XElement(tag.Descendants("MultilingualTextItem").First()));
        document.Save(source);
        var result = (await fixture.Call("ConvertEngineeringTable", new { kind = "plcTagTable", direction = "toTable", inputPath = source,
            format = "csv", exportFile = output, dryRun = false })).StructuredContent!;
        Invalid(result, source); Assert.False(File.Exists(output));
        Assert.Equal(2, (int?)result["data"]?["location"]?["row"]); Assert.Equal(column, (int?)result["data"]?["location"]?["column"]);
    }

    [Fact]
    public async Task Overflowing_worksheet_row_is_a_typed_argument_with_location()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        string source = Path.Combine(fixture.Root, "overflow.xlsx"); File.Copy(Fixture("shared-strings.xlsx"), source);
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            XDocument document; using (var input = entry.Open()) document = XDocument.Load(input);
            document.Descendants().Where(e => e.Name.LocalName == "row").Last().SetAttributeValue("r", "2147483648");
            entry.Delete(); using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); document.Save(output);
        }
        var result = (await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "fromTable", inputPath = source })).StructuredContent!;
        Invalid(result, source);
        Assert.Equal(2, (int?)result["data"]?["location"]?["row"]); Assert.Equal(1, (int?)result["data"]?["location"]?["column"]);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task Xml_invalid_cell_is_a_typed_argument_before_output_creation(string format)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        string output = Path.Combine(fixture.Root, "invalid." + format);
        var result = (await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "toTable", format,
            rows = new[] { new { Name = "\u0001" } }, exportFile = output, dryRun = false })).StructuredContent!;
        Invalid(result, output); Assert.False(File.Exists(output));
        Assert.Contains("row 2, column 1", (string?)result["error"]?["message"]);
    }

    [Theory]
    [InlineData("parse", false)]
    [InlineData("parse", true)]
    [InlineData("culture", false)]
    [InlineData("culture", true)]
    [InlineData("id", false)]
    [InlineData("id", true)]
    public async Task Template_failures_name_only_the_template_once(string failure, bool defaultOutput)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        using var session = fixture.EnterSession();
        string input = Path.Combine(fixture.Root, "edited.csv"), template = Path.Combine(fixture.Root, "bad-template.xml");
        var document = PlcTagTableExchange.Parse(File.ReadAllText(Fixture("tags-v21.xml")));
        File.WriteAllBytes(input, TableFile.WriteCsv(PlcTagTableExchange.ToTable(document)));
        if (failure == "parse") File.WriteAllText(template, "<Document><broken>");
        else
        {
            if (failure == "culture")
            {
                var comment = document.Descendants("MultilingualTextItem").First(); comment.AddAfterSelf(new XElement(comment));
            }
            else document.Descendants("SW.Tags.PlcTag").First().SetAttributeValue("ID", "invalid");
            document.Save(template);
        }
        var args = new JsonObject { ["kind"] = "plcTagTable", ["direction"] = "fromTable", ["inputPath"] = input, ["templatePath"] = template };
        if (!defaultOutput) args["exportFile"] = Path.Combine(fixture.Root, "output.xml");
        var result = (await fixture.Call("ConvertEngineeringTable", args)).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        string message = (string)result["error"]!["message"]!;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(message, "bad-template\\.xml").Count);
        Assert.DoesNotContain("edited.csv", message);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(message, "row [0-9]+, column [0-9]+:").Count);
        if (!defaultOutput || failure != "parse")
        {
            Invalid(result, template);
            Assert.Equal(failure == "culture" ? 2 : 1, (int?)result["data"]?["location"]?["row"]);
            Assert.Equal(failure == "culture" ? 10 : 1, (int?)result["data"]?["location"]?["column"]);
        }
    }

    [Fact]
    public async Task Repeated_shared_strings_refuse_as_typed_invalid_argument_at_the_logical_limit()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        string input = Path.Combine(fixture.Root, "large.xlsx"); File.Copy(Fixture("shared-logical-limit.xlsx"), input);
        var result = (await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "fromTable", inputPath = input })).StructuredContent!;
        Invalid(result, input); Assert.Contains("64 Mi logical characters", (string?)result["error"]?["message"]);
        Assert.Equal(2050, (int?)result["data"]?["location"]?["row"]);
    }

    private static void Invalid(JsonNode result, string file)
    {
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Contains(Path.GetFileName(file), (string?)result["error"]?["message"]);
        Assert.Equal(file, (string?)result["data"]?["location"]?["file"]);
        Assert.True((int?)result["data"]?["location"]?["row"] > 0); Assert.True((int?)result["data"]?["location"]?["column"] > 0);
        Assert.Contains("row ", (string?)result["error"]?["message"]); Assert.Contains("column ", (string?)result["error"]?["message"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }
}
