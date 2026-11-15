extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;

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

    private sealed class ImportFixture : IDisposable
    {
        internal const string Project = "C:/Projects/Fixture.ap21";
        private readonly PreviewHostFixture host;
        private readonly IDisposable session;
        internal readonly string Directory;
        internal readonly List<PlcSoftwareRequest> Requests = new();
        internal readonly Host.PlcTechnologyTools Tools;
        internal JsonObject Batch = new() { ["Imported"] = new JsonArray("Object"), ["Failed"] = new JsonArray() };

        internal ImportFixture(string release)
        {
            host = new PreviewHostFixture(release);
            session = host.Pipeline.EnterSession();
            string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            Directory = Path.Combine(root, "bin-build", "technology-import-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(Path.Combine(root, "templates", "mcp-full-e2e-verify", "import", "MCP_E2E_Tags.xml"), Path.Combine(Directory, "Object.xml"));
            var service = new Services.PlcOrganisationPortService((_, arguments) => {
                Requests.Add(JsonSerializer.Deserialize<PlcSoftwareRequest>(arguments["request"]!.ToJsonString())!);
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
