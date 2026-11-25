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
        Assert.Equal(523, matrix.Count);
        foreach (var (name, operation) in ToolUsageTests.ReviewedExamples)
        {
            if (name == "ManageCfcChartProtection")
            {
                Assert.DoesNotContain(matrix, row => (string?)row!["name"] == name);
                Assert.DoesNotContain(fixture.Tools, tool => tool.ProtocolTool.Name == name);
                var usage = await fixture.Call("GetToolUsage", new { toolName = name, operation });
                Assert.False((bool?)usage["ok"]);
                continue;
            }
            var declared = matrix.Single(row => (string?)row!["name"] == name)!["releases"]!.AsArray();
            if (!declared.Any(version => (string?)version == release)) continue;
            var result = await fixture.Call("GetToolUsage", new { toolName = name, operation });
            Assert.True((bool?)result["ok"], $"{release}/{name}/{operation}: {result["error"]}");
            Assert.True(ToolUsageTests.ReviewedExampleMatches(name, operation, result, result.ToJsonString().Length),
                $"{release}/{name}/{operation} returned a different example.");
        }
    }

    [Theory, MemberData(nameof(B3HostPortsTests.Releases), MemberType = typeof(B3HostPortsTests))]
    public async Task Address_example_selects_the_current_offset_and_supplies_a_new_one(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var address = (await fixture.Call("GetToolUsage", new { toolName = "SetDeviceAddress" }))["data"]!;
        var args = address["example"]!["request"]!["params"]!["arguments"]!;
        Assert.Equal("Input", (string?)args["ioType"]);
        Assert.Equal(1, (int?)args["startAddress"]);
        Assert.Equal(2, (int?)args["properties"]?["StartAddress"]);
        Assert.NotEqual((int?)args["startAddress"], (int?)args["properties"]?["StartAddress"]);
        Assert.Contains("current", (string?)address["inputSchema"]?["properties"]?["startAddress"]?["description"]);
    }

    [Theory, MemberData(nameof(B3HostPortsTests.Releases), MemberType = typeof(B3HostPortsTests))]
    public async Task Directory_import_example_includes_import_order(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var batch = (await fixture.Call("GetToolUsage", new { toolName = "ImportPlcBlocksFromDirectory" }))["data"]!;
        Assert.NotNull(batch["example"]?["request"]?["params"]?["arguments"]?["importOrder"]);
    }

    [Theory, InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Block_import_note_shows_the_required_namespace_element(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var block = (await fixture.Call("GetToolUsage", new { toolName = "ImportPlcBlock" }))["data"]!;
        Assert.Contains("<Namespace />", (string?)block["example"]?["note"]);
    }

    [Theory, InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Instance_db_example_uses_the_supported_fb_kind(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var instance = (await fixture.Call("GetToolUsage", new { toolName = "CreatePlcInstanceDb" }))["data"]!;
        string fb = (string)instance["example"]!["request"]!["params"]!["arguments"]!["fbPath"]!;
        Assert.Equal(release is "15.1" or "16", fb.Contains("ProDiag", StringComparison.Ordinal));
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
