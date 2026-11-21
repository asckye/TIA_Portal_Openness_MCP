extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class TechnologyImportOverwriteTests
{
    public static IEnumerable<object[]> Releases() => new[] { "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> FailedBatches() => from release in Releases()
        from imported in new[] { false, true } select new object[] { release[0], imported };

    [Theory, MemberData(nameof(Releases))]
    public void Directory_import_does_not_overwrite_by_default(string release)
    {
        using var fixture = new ImportFixture(release);
        var properties = Host.McpServer.CatalogView.Find("ImportTechnologyObjectsFromDirectory")!.Tool.InputSchema.GetProperty("properties");
        Assert.False(properties.GetProperty("overwrite").GetProperty("default").GetBoolean());
        var preview = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory).StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.Empty(fixture.Requests);
        var body = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory, dryRun: false,
            expectedPlanHash: (string)preview["data"]!["planHash"]!, expectedProjectFile: ImportFixture.Project).StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("ImportTechnologyObjectsFromDirectory", request.Operation);
        Assert.False(request.Overwrite);
        Assert.False(request.DryRun);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Directory_import_overwrites_only_when_asked(string release)
    {
        using var fixture = new ImportFixture(release);
        var preview = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory, overwrite: true).StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.Empty(fixture.Requests);
        var body = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory, overwrite: true, dryRun: false,
            expectedPlanHash: (string)preview["data"]!["planHash"]!, expectedProjectFile: ImportFixture.Project).StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.True(Assert.Single(fixture.Requests).Overwrite);
    }

    [Theory, MemberData(nameof(FailedBatches))]
    public void Stopped_import_batch_retains_its_existing_unknown_outcome(string release, bool imported)
    {
        using var fixture = new ImportFixture(release);
        fixture.Batch = new JsonObject {
            ["Imported"] = imported ? new JsonArray("EarlierObject") : new JsonArray(),
            ["Failed"] = new JsonArray(new JsonObject { ["Path"] = Path.Combine(fixture.Directory, "Object.xml"),
                ["Error"] = "Technology object already exists. Batch stopped; later files were not attempted." }) };
        var preview = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory).StructuredContent!;
        var body = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory, dryRun: false,
            expectedPlanHash: (string)preview["data"]!["planHash"]!, expectedProjectFile: ImportFixture.Project).StructuredContent!;
        Assert.False((bool?)body["ok"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
        Assert.Single(body["data"]!["failed"]!.AsArray());
        Assert.Equal(imported ? 1 : 0, body["data"]!["imported"]!.AsArray().Count);
        Assert.False(Assert.Single(fixture.Requests).Overwrite);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Single_import_checks_the_target_before_applying(string release)
    {
        using var fixture = new ImportFixture(release);
        var properties = Host.McpServer.CatalogView.Find("ImportTechnologyObject")!.Tool.InputSchema.GetProperty("properties");
        Assert.False(properties.GetProperty("overwrite").GetProperty("default").GetBoolean());
        string file = Path.Combine(fixture.Directory, "Object.xml");
        var preview = fixture.Tools.ImportTechnologyObjectV4("PLC_1", "", file).StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.True(Assert.Single(fixture.Requests).DryRun);
        fixture.Existing = true;
        var apply = fixture.Tools.ImportTechnologyObjectV4("PLC_1", "", file, dryRun: false,
            expectedPlanHash: (string)preview["data"]!["planHash"]!, expectedProjectFile: ImportFixture.Project).StructuredContent!;
        Assert.False((bool?)apply["ok"]);
        Assert.False(fixture.Requests.Last().Overwrite);
        var refused = fixture.Tools.ImportTechnologyObjectV4("PLC_1", "", file).StructuredContent!;
        Assert.False((bool?)refused["ok"]);
        Assert.Equal("PRECONDITION_FAILED", (string?)refused["error"]?["code"]);
        Assert.Contains("already exists", (string?)refused["error"]?["message"], StringComparison.Ordinal);
        Assert.Equal("PRECONDITION_FAILED", (string?)apply["error"]?["code"]);
        var replace = fixture.Tools.ImportTechnologyObjectV4("PLC_1", "", file, overwrite: true).StructuredContent!;
        Assert.True((bool?)replace["ok"], replace.ToJsonString());
        var replaced = fixture.Tools.ImportTechnologyObjectV4("PLC_1", "", file, overwrite: true, dryRun: false,
            expectedPlanHash: (string)replace["data"]!["planHash"]!, expectedProjectFile: ImportFixture.Project).StructuredContent!;
        Assert.True((bool?)replaced["ok"], replaced.ToJsonString());
        Assert.True(fixture.Requests.Last().Overwrite);
    }

    [Fact]
    public void Directory_preview_refuses_a_selected_tag_table()
    {
        using var fixture = new ImportFixture("20");
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        File.Copy(Path.Combine(root, "templates", "mcp-full-e2e-verify", "import", "MCP_E2E_Tags.xml"), Path.Combine(fixture.Directory, "Tags.xml"));
        var result = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory).StructuredContent!;
        Assert.False((bool?)result["ok"]);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Directory_import_reports_an_invalid_regex_before_reading_files(bool dryRun)
    {
        using var fixture = new ImportFixture("20");
        string missing = Path.Combine(fixture.Directory, "missing");
        var body = fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", missing, "[", dryRun: dryRun,
            expectedPlanHash: new string('a', 64), expectedProjectFile: ImportFixture.Project).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/regexName", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public void Directory_preview_validates_only_the_selected_top_level_xml()
    {
        using var fixture = new ImportFixture("20");
        string file = Path.Combine(fixture.Directory, "Object.xml");
        string xml = File.ReadAllText(file);
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Directory, "Nested"));
        File.WriteAllText(Path.Combine(nested.FullName, "Object.xml"), "<Document/>");
        File.WriteAllText(Path.Combine(fixture.Directory, "Other.xml"), "<Document/>");
        JsonNode Preview(string filter) => fixture.Tools.ImportTechnologyObjectsFromDirectoryV4("PLC_1", "", fixture.Directory, filter).StructuredContent!;
        Assert.True((bool?)Preview("^Object$")["ok"]);
        Assert.False((bool?)Preview("^Absent$")["ok"]);
        File.WriteAllText(file, xml.Replace("<Name>Fixture</Name>", "<Name/>"));
        Assert.False((bool?)Preview("^Object$")["ok"]);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public void Export_preview_reports_the_number_of_planned_items()
    {
        using var fixture = new ImportFixture("18");
        fixture.Batch["Imported"] = new JsonArray();
        fixture.Batch["Meta"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["objectPath"] = "First" },
            new JsonObject { ["objectPath"] = "Second" }) };
        var result = fixture.Tools.ExportTechnologyObjectsToDirectory("PLC_1", fixture.Directory);
        Assert.Contains("Previewed 2 technology object(s)", result.Message, StringComparison.Ordinal);
    }

    private sealed class ImportFixture : IDisposable
    {
        internal const string Project = "C:/Projects/Fixture.ap21";
        private readonly PreviewHostFixture host;
        private readonly IDisposable session;
        internal readonly string Directory;
        internal readonly List<PlcSoftwareRequest> Requests = new();
        internal bool Existing;
        internal readonly Host.PlcTechnologyTools Tools;
        internal JsonObject Batch = new() { ["Imported"] = new JsonArray("Object"), ["Failed"] = new JsonArray() };

        internal ImportFixture(string release)
        {
            host = new PreviewHostFixture(release);
            session = host.Pipeline.EnterSession();
            string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            Directory = Path.Combine(root, "bin-build", "technology-import-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "Object.xml"), """
                <Document><Engineering version="V17"/><SW.TechnologicalObjects.TechnologicalInstanceDB ID="0">
                <AttributeList><Name>Fixture</Name></AttributeList></SW.TechnologicalObjects.TechnologicalInstanceDB></Document>
                """);
            var service = new Services.PlcOrganisationPortService((_, arguments) => {
                Requests.Add(JsonSerializer.Deserialize<PlcSoftwareRequest>(arguments["request"]!.ToJsonString())!);
                if (Existing && !Requests.Last().Overwrite)
                    return new JsonObject { ["MayHaveChanged"] = false, ["Failure"] = new JsonObject {
                        ["Code"] = nameof(AdapterPreconditionException), ["Message"] = "Technology object already exists.",
                        ["Evidence"] = new JsonObject { ["status"] = "PreconditionFailed", ["mayHaveChanged"] = false, ["parameter"] = "overwrite" } } };
                return new JsonObject { ["Batch"] = Batch.DeepClone() };
            }, () => true, () => Project);
            Tools = new Host.PlcTechnologyTools(service);
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, true);
            session.Dispose(); host.Dispose();
        }
    }
}
