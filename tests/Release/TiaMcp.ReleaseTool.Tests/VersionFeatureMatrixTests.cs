using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class VersionFeatureMatrixTests
{
    private static string Root => Repository.FindRoot();
    public static IEnumerable<object[]> Releases => Strings(Read(Root, "reference/version-feature-matrix.json")["releases"])
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void Technology_export_matches_advertised_release_tools_and_preserves_sdk_evidence(string release)
    {
        var matrix = Read(Root, "reference/version-feature-matrix.json");
        var feature = matrix["features"]!.AsArray().Single(row => Text(row!["id"]) == "technology-export")!;
        var roster = Strings(Read(Root, "manifest/version-tools.json")["releases"]![release]!["tools"]);
        Assert.Equal("implemented", Text(feature["implementation"]![release]));
        var tools = Strings(feature["tools"]);
        Assert.Equal(tools.Where(tool => roster.Contains(tool, StringComparer.Ordinal)), Strings(feature["advertisedTools"]![release]));
        Assert.Equal(tools, Strings(feature["advertisedTools"]![release]));
        foreach (string identifier in Strings(feature["sdkIdentifiers"]))
            Assert.Contains(identifier, Strings(feature["sdkEvidence"]![release]!["present"]));
        Assert.Empty(Strings(feature["sdkEvidence"]![release]!["absentFromInspectedXml"]));
        Assert.Contains("NOT RUN", Text(matrix["nativeAcceptance"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Technology_export_coverage_cells_match_the_reviewed_feature_matrix()
    {
        var matrix = Read(Root, "reference/version-feature-matrix.json");
        var feature = matrix["features"]!.AsArray().Single(row => Text(row!["id"]) == "technology-export")!;
        var row = File.ReadLines(Path.Combine(Root, "docs/reference/openness-coverage.md"))
            .Single(line => line.StartsWith("| Export technology-object XML |", StringComparison.Ordinal));
        var cells = row.Split('|', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(cell => cell.Trim()).ToArray();
        Assert.Equal(Strings(matrix["releases"]).Select(release => Text(feature["implementation"]![release])),
            cells.Select(cell => cell == "I" ? "implemented" : cell));
    }
}
