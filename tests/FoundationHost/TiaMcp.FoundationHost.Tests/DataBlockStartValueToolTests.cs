extern alias toolpipeline;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Xml.Linq;
using TiaMcp.Logic.Exchange;
using Xunit;

public sealed class DataBlockStartValueToolTests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();
    public static IEnumerable<object[]> InvalidLiterals() => Releases().SelectMany(release => new[] {
        new object[] { release[0], "Int", "", "Resetting to the default" },
        new object[] { release[0], "Int", "0", "type default" },
        new object[] { release[0], "Real", "0.10000000000000001", "suggested value 0.1" } });
    private const string Xml = "<Document><Engineering version='V21'/><DocumentInfo/><SW.Blocks.GlobalDB ID='0'><AttributeList>"
        + "<Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Static'>"
        + "<Member Name='电机/运行' Datatype='Int'/><Member Name='Locked' Datatype='Int' Informative='true'/>"
        + "</Section></Sections></Interface><Name>DB/电机</Name><Number>200</Number><MemoryLayout>Standard</MemoryLayout>"
        + "<ProgrammingLanguage>DB</ProgrammingLanguage></AttributeList></SW.Blocks.GlobalDB></Document>";

    [Theory, MemberData(nameof(Releases))]
    public void Schema_and_usage_publish_the_db_kind_and_examples_on_every_release(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        var catalog = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.AllToolDescriptors();
        var descriptor = catalog["ConvertEngineeringTable"];
        var schema = JsonNode.Parse(descriptor.Tool.InputSchema.GetRawText())!.AsObject();
        Assert.Contains("dataBlockStartValues", schema["properties"]!["kind"]!.ToJsonString());
        Assert.Contains("dataBlockStartValues", descriptor.Parameters.Single(p => p.Name == "kind").AllowedValues);
        var usage = TiaOpenness.Shared.ToolUsageCatalog.Describe("ConvertEngineeringTable", release, "full-engine",
            descriptor.Tool.Description!, schema, operation: "fromTable", roster: catalog.Keys);
        Assert.Contains("dataBlockStartValues", usage["resultReading"]!.ToJsonString());
        Assert.Contains("db-start-values-excel", usage.ToJsonString());
        var examples = TiaOpenness.Shared.ToolUsageCatalog.Examples(release, "plc-foundation", catalog.Keys,
            exampleId: "sequence/db-start-values-excel", schemaFor: name => catalog.TryGetValue(name, out var tool)
                ? JsonNode.Parse(tool.Tool.InputSchema.GetRawText())!.AsObject() : null);
        var sequence = Assert.Single(examples["examples"]!.AsArray());
        Assert.True((bool)sequence!["available"]!, sequence.ToJsonString());
        Assert.Equal(4, sequence["steps"]!.AsArray().Count(s => (string?)s!["tool"] == "ConvertEngineeringTable"));
        var steps = sequence["steps"]!.AsArray();
        int exportIndex = Enumerable.Range(0, steps.Count).Single(i => (string?)steps[i]!["tool"] == "ExportPlcBlock"
            && (bool?)steps[i]!["arguments"]!["dryRun"] == false);
        var export = steps[exportIndex]!["arguments"]!;
        Assert.True((bool)export["confirm"]!); Assert.False(string.IsNullOrWhiteSpace((string?)export["expectedProjectFile"]));
        Assert.True((bool)steps[exportIndex - 1]!["arguments"]!["dryRun"]!);
        Assert.Equal((string?)export["exportDirectory"] + "/" + (string?)export["blockPath"] + ".xml",
            (string?)steps[exportIndex + 1]!["arguments"]!["inputPath"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Global_db_conversion_uses_default_output_previews_all_edits_and_returns_verified_import_route(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        string source = Path.Combine(fixture.Root, "db.xml"), csv = Path.Combine(fixture.Root, "edit.csv");
        File.WriteAllText(source, Xml);
        var toArgs = new JsonObject { ["kind"] = "dataBlockStartValues", ["direction"] = "toTable", ["inputPath"] = source, ["format"] = "csv" };
        var preview = Body(await fixture.Call("ConvertEngineeringTable", toArgs));
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.Equal(1, (int)preview["data"]!["notEditable"]!); Assert.Equal(1, (int)preview["data"]!["rowCount"]!);
        string output = (string)preview["data"]!["exportFile"]!;
        Assert.Equal("ConvertEngineeringTable." + TiaMcp.Adapters.Contracts.ObjectFileNameEncoding.Encode("DB/电机") + ".csv", Path.GetFileName(output));
        Assert.False(File.Exists(output));
        toArgs["dryRun"] = false;
        var write = Body(await fixture.Call("ConvertEngineeringTable", toArgs));
        Assert.True((bool?)write["ok"], write.ToJsonString());
        Assert.Equal(output, (string?)write["data"]!["output"]!["path"]);
        var table = TableFile.ReadCsv(File.ReadAllBytes(output)); table.Rows[0][3] = "42";
        File.WriteAllBytes(csv, TableFile.WriteCsv(table));
        var fromArgs = new JsonObject { ["kind"] = "dataBlockStartValues", ["direction"] = "fromTable", ["inputPath"] = csv,
            ["templatePath"] = source };
        var merge = Body(await fixture.Call("ConvertEngineeringTable", fromArgs));
        Assert.True((bool?)merge["ok"], merge.ToJsonString());
        string xml = (string)merge["data"]!["exportFile"]!; Assert.False(File.Exists(xml));
        Assert.Equal(1, (int)merge["data"]!["changes"]!["updated"]!);
        var next = merge["data"]!["nextStep"]!;
        Assert.Equal("ImportPlcBlockVerified", (string?)next["tool"]);
        Assert.Equal(xml, (string?)next["arguments"]!["importPath"]);
        Assert.True((bool)next["arguments"]!["dryRun"]!);
        Assert.Contains("evidenceDirectory", next["arguments"]!.AsObject().Select(p => p.Key));
        Assert.Contains("previewToken", (string)next["execution"]!);
        fromArgs["dryRun"] = false;
        var applied = Body(await fixture.Call("ConvertEngineeringTable", fromArgs));
        Assert.True((bool?)applied["ok"], applied.ToJsonString());
        Assert.Equal("42", DataBlockStartValueExchange.ToTable(DataBlockStartValueExchange.Parse(File.ReadAllText(xml))).Rows[0][3]);
        Assert.Equal(Xml, File.ReadAllText(source));
        Assert.False((bool)Body(await fixture.Call("ConvertEngineeringTable", fromArgs))["ok"]!);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Xlsx_roundtrip_and_invalid_member_report_file_row_column(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        string source = Path.Combine(fixture.Root, "db.xml"), xlsx = Path.Combine(fixture.Root, "edit.xlsx"), output = Path.Combine(fixture.Root, "out.xml");
        File.WriteAllText(source, Xml);
        var exported = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "dataBlockStartValues", direction = "toTable",
            inputPath = source, exportFile = xlsx, dryRun = false }));
        Assert.True((bool?)exported["ok"], exported.ToJsonString());
        var table = TableFile.ReadXlsx(File.ReadAllBytes(xlsx), "DataBlockStartValues");
        Assert.Equal("电机%2F运行", table.Rows[0][1]);
        var args = new { kind = "dataBlockStartValues", direction = "fromTable", inputPath = xlsx, templatePath = source, exportFile = output };
        Assert.True((bool?)Body(await fixture.Call("ConvertEngineeringTable", args))["ok"]);
        table.Rows[0][1] = "Missing"; File.WriteAllBytes(xlsx, TableFile.WriteXlsx(table, "DataBlockStartValues"));
        var refused = Body(await fixture.Call("ConvertEngineeringTable", args));
        Assert.False((bool)refused["ok"]!); Assert.False(File.Exists(output));
        Assert.Contains("row 2, column 2", refused.ToJsonString());
    }

    private static JsonObject Body(CallToolResult result) => result.StructuredContent!.AsObject();

    [Theory, MemberData(nameof(InvalidLiterals))]
    public async Task Invalid_literals_refuse_before_planning_with_file_row_column(string release, string type, string value, string message)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        string source = Path.Combine(fixture.Root, "db.xml"), csv = Path.Combine(fixture.Root, "edit.csv"), output = Path.Combine(fixture.Root, "out.xml");
        var document = XDocument.Parse(Xml);
        var member = document.Descendants().Single(e => e.Name.LocalName == "Member" && (string?)e.Attribute("Name") == "电机/运行");
        member.SetAttributeValue("Datatype", type); member.Add(new XElement(member.Name.Namespace + "StartValue", type == "Real" ? "1.0" : "42"));
        File.WriteAllText(source, document.ToString());
        var table = DataBlockStartValueExchange.ToTable(document); table.Rows[0][3] = value;
        string text = Encoding.UTF8.GetString(TableFile.WriteCsv(table)); int end = text.IndexOf('\n');
        File.WriteAllText(csv, text.Insert(end + 1, "\n\n"));
        var refused = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "dataBlockStartValues", direction = "fromTable",
            inputPath = csv, templatePath = source, exportFile = output, dryRun = false }));
        Assert.False((bool)refused["ok"]!); Assert.Equal("INVALID_ARGUMENT", (string?)refused["error"]!["code"]);
        Assert.Contains(message, refused.ToJsonString()); Assert.Contains("row 4, column 4", refused.ToJsonString());
        Assert.Contains("edit.csv", refused.ToJsonString()); Assert.False(File.Exists(output));
        Assert.Equal(document.ToString(), File.ReadAllText(source));
    }
}
