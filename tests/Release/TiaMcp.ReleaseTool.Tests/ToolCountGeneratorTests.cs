using System.Text;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class ToolCountGeneratorTests
{
    private static string Root => Repository.FindRoot();

    [Fact]
    public void EveryTargetHasOneGeneratedBlockAndCatalogCounts()
    {
        Assert.Contains("generate-tool-counts", CommandLine.Commands);
        var outputs = ToolCountGenerator.Generate(Root);
        Assert.Equal(ToolCountGenerator.Targets.Order(), outputs.Keys.Order());
        Assert.Equal(0, CatalogJson.Write(Root, outputs, true));
        var data = new CatalogData(Root);
        foreach (var (path, output) in outputs)
        {
            Assert.Contains("`tools/list`", output);
            foreach (var key in CatalogJson.Keys)
                Assert.Contains("| " + data.Rosters[key].Count + " |", output);
            Assert.Equal(output, ToolCountGenerator.Replace(output,
                output.Split(ToolCountGenerator.Begin + "\n")[1].Split("\n" + ToolCountGenerator.End)[0], path));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    public void MissingDuplicateAndReversedBlocksFailClosed(string kind)
    {
        var block = ToolCountGenerator.Begin + "\nold\n" + ToolCountGenerator.End;
        var text = kind == "missing" ? "old" : kind == "duplicate" ? block + block
            : ToolCountGenerator.End + ToolCountGenerator.Begin;
        Assert.Throws<ReleaseException>(() => ToolCountGenerator.Replace(text, "new", "fixture.md"));
    }

    [Fact]
    public void GenerationRepairsDriftAndCheckDoesNotWrite()
    {
        var directory = Path.Combine(Root, "bin-build/tool-count-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = "before\n" + ToolCountGenerator.Begin + "\nold\n" + ToolCountGenerator.End + "\nafter\n";
            var expected = new Dictionary<string, string>
            {
                ["fixture.md"] = ToolCountGenerator.Replace(original, "| V21 | 1 |", "fixture.md")
            };
            var path = Path.Combine(directory, "fixture.md");
            File.WriteAllText(path, original, new UTF8Encoding(false));
            Assert.Equal(1, CatalogJson.Write(directory, expected, true));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(0, CatalogJson.Write(directory, expected, false));
            Assert.Equal(0, CatalogJson.Write(directory, expected, true));
            Assert.StartsWith("before\n", File.ReadAllText(path));
            Assert.EndsWith("\nafter\n", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
