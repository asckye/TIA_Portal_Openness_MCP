using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class ToolCapabilityMatrixTests
{
    [Fact]
    public void CommandUsesExplicitPathsAndChecksThePublishedMatrixWithoutWriting()
    {
        using var tree = new StaticTree();
        tree.Copy("manifest/tools-list.json");
        var tools = Path.Combine(tree.Root, "manifest/tools-list.json");
        var output = Path.Combine(tree.Root, "matrix.md");
        var options = Options.Parse(["-ToolsList", tools, "-OutFile", output], Program.BooleanOptions);
        Assert.Contains("generate-tool-capability-matrix", CommandLine.Commands);
        Assert.Equal(0, ReleaseCommands.Run("generate-tool-capability-matrix", options));
        string Stable(string value) => Regex.Replace(value, @"(?m)^- 生成时间：.*$", "- generated timestamp");
        Assert.Equal(Stable(Repository.ReadSource(Path.Combine(Repository.FindRoot(), "docs/reference/tool-matrix.md"))),
            Stable(File.ReadAllText(output)));
        var original = File.ReadAllBytes(output);
        var check = Options.Parse(["--tools-list", tools, "--out-file", output, "--check"], Program.BooleanOptions);
        Assert.Equal(0, ReleaseCommands.Run("generate-tool-capability-matrix", check));
        Assert.Equal(original, File.ReadAllBytes(output));
        File.AppendAllText(output, "drift");
        var changed = File.ReadAllBytes(output);
        Assert.Throws<InvalidDataException>(() => ReleaseCommands.Run("generate-tool-capability-matrix", check));
        Assert.Equal(changed, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("categories")]
    [InlineData("uncategorized")]
    public void InvalidCatalogDoesNotReplaceOutput(string mutation)
    {
        using var tree = new StaticTree();
        tree.Copy("manifest/tools-list.json");
        var tools = Path.Combine(tree.Root, "manifest/tools-list.json");
        var output = tree.Write("matrix.md", "existing output");
        var data = JsonNode.Parse(File.ReadAllText(tools))!;
        if (mutation == "count") data["toolCount"] = 0;
        else if (mutation == "categories") data.AsObject().Remove("categories");
        else data["tools"]![0]!["category"] = "uncategorized";
        File.WriteAllText(tools, data.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ToolCapabilityMatrixGenerator.Generate(tools, output));
        Assert.Equal("existing output", File.ReadAllText(output));
    }
}
