using System.Text.Json.Nodes;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class ServedToolUsageBudgetTests
{
    [Theory]
    [MemberData(nameof(B3HostPortsTests.Releases), MemberType = typeof(B3HostPortsTests))]
    public async Task Reviewed_examples_are_served_on_every_registered_release(string release)
    {
        var root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest", "version-tools.json")))!["tools"]!.AsArray();
        using var fixture = new DiscoveryFixture(release);
        foreach (var (name, operation) in ToolUsageTests.ReviewedExamples)
        {
            var declared = matrix.Single(row => (string?)row!["name"] == name)!["releases"]!.AsArray();
            if (!declared.Any(version => (string?)version == release)) continue;
            var result = await fixture.Call("GetToolUsage", new { toolName = name, operation });
            Assert.True((bool?)result["ok"], $"{release}/{name}/{operation}: {result["error"]}");
            Assert.True(ToolUsageTests.ReviewedExampleMatches(name, operation, result, result.ToJsonString().Length),
                $"{release}/{name}/{operation} returned a different example.");
        }
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task Public_usage_preserves_examples_and_bounds_the_two_large_responses(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var sample = await fixture.Call("GetToolUsage", new { toolName = "SamplePlcLiveValuesS7" });
        Assert.True((bool?)sample["ok"], sample.ToJsonString());
        Assert.Equal("DB1.DBX0.0:BOOL", (string?)sample["data"]?["example"]?["request"]?["params"]?["arguments"]?["items"]?[0]);
        foreach (var name in new[] { "ManagePlcExternalSources", "WritePlcSclSourceFile" })
        {
            var body = await fixture.Call("GetToolUsage", new { toolName = name });
            Assert.True((bool?)body["ok"], body.ToJsonString());
            int length = body.ToJsonString().Length;
            Assert.True(length < 40000, $"{release}/{name} returned {length} characters");
            Assert.NotNull(body["data"]?["inputSchema"]);
            var examples = body["data"]!["examples"]!.AsArray();
            var id = (string)examples.First()!["id"]!;
            var detail = await fixture.Call("GetToolUsage", new { toolName = name, exampleId = id });
            Assert.True((bool?)detail["ok"]);
            var example = Assert.Single(detail["data"]!["examples"]!.AsArray());
            Assert.Equal(id, (string?)example?["id"]);
            Assert.True(example!.AsObject().Count >= examples.First()!.AsObject().Count);
        }
    }
}
