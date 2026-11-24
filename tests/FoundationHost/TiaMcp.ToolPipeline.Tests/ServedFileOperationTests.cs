extern alias toolpipeline;

using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.Logic.Generation;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class ServedFileOperationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("served-file-operations-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Published_artifact_cleanup_previews_only_the_named_staging_directory(string release)
    {
        string artifact = Directory.CreateDirectory(Path.Combine(root, "artifacts", "staging", "gen-" + new string('a', 64))).FullName;
        var store = new GenerationArtifacts(root);
        var requests = new ServedToolOperations(release).For("ManageGenerationArtifacts").ToArray();
        Assert.NotEmpty(requests);
        foreach (var (_, usage) in requests)
        {
            var args = usage["example"]!["request"]!["params"]!["arguments"]!;
            Assert.True((bool?)args["dryRun"]);
            var result = store.Manage((string)args["action"]!, (string?)args["artifactId"] ?? "", true, (string?)args["expectedPlanHash"] ?? "");
            Assert.False((bool?)result["executed"]);
            Assert.True(Directory.Exists(artifact));
        }
    }

    [Theory, InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Published_cwc_inspection_and_build_previews_validate_a_control_package(string release)
    {
        Directory.CreateDirectory(Path.Combine(root, "control"));
        File.WriteAllText(Path.Combine(root, "control", "index.html"), "<html><body>Control</body></html>");
        File.WriteAllText(Path.Combine(root, "manifest.json"), """
            {"mver":"1.2.0","control":{"identity":{"name":"Test","displayname":"Test","version":"1.0","type":"guid://551BF148-2F0D-4293-8E10-C9C3A1A6A073"}}}
            """);
        var requests = new ServedToolOperations(release).For("ManageUnifiedCwcPackage").ToArray();
        Assert.NotEmpty(requests);
        foreach (var (_, usage) in requests)
        {
            var args = usage["example"]!["request"]!["params"]!["arguments"]!;
            Assert.True(Path.IsPathFullyQualified((string)args["sourceDirectory"]!));
            Assert.True((bool?)args["dryRun"]);
            var result = UnifiedCwcPackage.Run(root, (string)args["action"]!, (string?)args["exportFile"] ?? "", true,
                (string?)args["expectedPackageHash"] ?? "");
            Assert.False((bool?)result["written"]);
        }
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Published_table_conversions_accept_the_corresponding_xml_and_workbooks(string release)
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TableExchange");
        File.Copy(Path.Combine(fixtures, "tags-v21.xml"), Path.Combine(root, "Tags.xml"));
        File.Copy(Path.Combine(fixtures, "db-v21.xml"), Path.Combine(root, "DB_HMI_Interface.xml"));
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.PlcOfflineTools();
        var requests = new ServedToolOperations(release).For("ConvertEngineeringTable").ToArray();
        Assert.NotEmpty(requests);
        string Input(string path)
        {
            if (path == "") return "";
            Assert.True(Path.IsPathFullyQualified(path));
            return Path.Combine(root, Path.GetFileName(path));
        }
        // Materialize the three matching workbook fixtures through the same writer before checking any previews.
        foreach (var (_, usage) in requests.Where(row => row.Operation is "toTable" or "dbToTable" or "rowsToTable"))
            Convert(usage, fixture: true);
        foreach (var (_, usage) in requests) Convert(usage, fixture: false);

        void Convert(JsonObject usage, bool fixture)
        {
            var args = usage["example"]!["request"]!["params"]!["arguments"]!;
            string Text(string field) => (string?)args[field] ?? "";
            Assert.True((bool?)args["dryRun"]);
            var output = Input(Text("exportFile"));
            if (!fixture && output != "") output = Path.Combine(root, "preview-" + Path.GetFileName(output));
            var result = tools.ConvertEngineeringTable(Text("kind"), Text("direction"), Input(Text("inputPath")), Input(Text("templatePath")),
                args["rows"]?.Deserialize<JsonElement[]>(), args["columns"]?.Deserialize<string[]>(), (string?)args["format"] ?? "xlsx",
                output, (int?)args["offset"] ?? 0, (int?)args["limit"] ?? 50, dryRun: !fixture);
            Assert.True((bool?)result.Meta!["operationSuccess"], result.Message);
        }
    }
}
