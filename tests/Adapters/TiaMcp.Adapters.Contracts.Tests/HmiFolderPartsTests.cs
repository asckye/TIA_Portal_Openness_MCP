using Xunit;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.Tests.Shared;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HmiFolderPartsTests
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("/", new string[0])]
    [InlineData("/F", new[] { "F" })]
    [InlineData("/A/B%2FC", new[] { "A", "B/C" })]
    [InlineData("A/B", new[] { "A", "B" })]
    [InlineData("A/B%2FC", new[] { "A", "B%2FC" })]
    [InlineData("A\\B", new[] { "A", "B" })]
    public void Folder_paths_keep_relative_literals_and_decode_absolute_segments(string? path, string[] expected)
    {
        Assert.Equal(expected, EngineeringInputValidation.HmiFolderParts(path));
    }

    [Theory]
    [InlineData("/A//B")]
    [InlineData("/A/../B")]
    [InlineData("a/../b")]
    [InlineData("/A/%2E%2E/B")]
    public void Empty_and_parent_segments_are_refused(string path)
    {
        Assert.Throws<ArgumentException>(() => EngineeringInputValidation.HmiFolderParts(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void Folder_paths_allow_at_most_64_segments(string prefix)
    {
        string path = prefix + string.Join("/", Enumerable.Repeat("F", 64));
        Assert.Equal(64, EngineeringInputValidation.HmiFolderParts(path).Length);
        Assert.Throws<ArgumentException>(() => EngineeringInputValidation.HmiFolderParts(path + "/F"));
    }

    [Theory]
    [InlineData("/A%2FB/C", "A/B")]
    [InlineData("/A%5CB/C", "A\\B")]
    public void Classic_folder_delete_preserves_the_decoded_parent_name(string path, string parentName)
    {
        string[] parts = EngineeringInputValidation.HmiFolderParts(path);
        Assert.Equal(new[] { parentName, "C" }, parts);
        Assert.Equal(new[] { parentName }, parts.Take(parts.Length - 1));
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string source = SourceFamily.Read(Path.Combine(root, "src/Adapters/Native/Hmi/ClassicHmiFoldersAdapter.cs"));
        foreach (string kind in new[] { "Screen", "Popup", "Template", "Tag" })
            Assert.Contains("Exact" + kind + "Folder(HmiTarget hmi, IEnumerable<string> parts)", source, StringComparison.Ordinal);
        Assert.Contains("object Resolve(string[] p)", source, StringComparison.Ordinal);
        Assert.Contains("var parent2 = Resolve(parts.Take(parts.Length - 1).ToArray());", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Resolve(parentPath)", source, StringComparison.Ordinal);
    }
}
