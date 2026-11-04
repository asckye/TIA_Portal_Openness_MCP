using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class GitWorkingBytesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanAutocrlfCheckoutIsRefusedBeforeAndAfterTheProbe(bool beforeProbe)
    {
        using var tree = new WorkingTree();
        Git(tree, ["init", "--initial-branch=master"]);
        Git(tree, ["config", "core.autocrlf", "true"]);
        Track(tree, "README.md", "fixture\n");
        if (!beforeProbe) Assert.Empty(GitWorkingBytes.NonNormalizedWorkingFiles(tree.Root, "git"));
        tree.Write("README.md", "fixture\r\n");
        Assert.Equal(["README.md"], GitWorkingBytes.NonNormalizedWorkingFiles(tree.Root, "git"));
        var error = Assert.Throws<ReleaseException>(() => GitWorkingBytes.RequireNormalizedWorkingBytes(tree.Root, "git"));
        Assert.Contains("Git index/HEAD normalization", error.Message);
        Assert.Contains("README.md", error.Message);
        Assert.Contains("Preserve any edits", error.Message);
        Assert.Contains("core.autocrlf=false", error.Message);
    }

    [Fact]
    public void WorkingByteProbePreservesBinaryBytesEditsAndTheIndex()
    {
        using var tree = new WorkingTree();
        Git(tree, ["init", "--initial-branch=master"]);
        tree.Write(".gitattributes", "*.md text eol=lf\n*.dat -text\n");
        Track(tree, "README.md", "fixture\n");
        const string name = "说明 with spaces.md";
        Track(tree, name, "one\ntwo\n");
        var binary = tree.Write("binary.dat", "");
        File.WriteAllBytes(binary, [0, 13, 10, 255, 128]);
        var hash = Git(tree, ["hash-object", "-w", "--no-filters", binary]);
        Git(tree, ["update-index", "--add", "--cacheinfo", "100644", hash, "binary.dat"]);
        tree.Write(name, "one\r\ntwo\r\n");
        File.AppendAllText(Path.Combine(tree.Root, "README.md"), "a real LF edit\n");
        var index = File.ReadAllBytes(Path.Combine(tree.Root, ".git/index"));
        var contents = Prepare.Inventory(tree.Root);
        Assert.NotEqual(Git(tree, ["hash-object", "--no-filters", "--", name]), Git(tree, ["hash-object", "--", name]));
        Assert.Equal([name], GitWorkingBytes.NonNormalizedWorkingFiles(tree.Root, "git"));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(tree.Root, ".git/index")));
        Assert.Equal(contents, Prepare.Inventory(tree.Root));
        Assert.Equal(new byte[] { 0, 13, 10, 255, 128 }, File.ReadAllBytes(binary));
        tree.Write(name, "one\ntwo\n");
        GitWorkingBytes.RequireNormalizedWorkingBytes(tree.Root, "git");
    }

    private static void Track(WorkingTree tree, string name, string content)
    {
        tree.Write(name, content);
        var hash = Git(tree, ["hash-object", "-w", "--stdin"], content);
        Git(tree, ["update-index", "--add", "--cacheinfo", "100644", hash, name]);
    }

    private static string Git(WorkingTree tree, string[] arguments, string? input = null)
    {
        var result = ProcessRunner.Run("git", arguments, tree.Root, standardInput: input);
        ProcessRunner.RequireSuccess(result, "Working-byte fixture");
        return result.StandardOutput.Trim();
    }

    private sealed class WorkingTree : IDisposable
    {
        private readonly StaticTree tree = new();
        internal string Root => tree.Root;
        internal string Write(string name, string content) => tree.Write(name, content);
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            tree.Dispose();
        }
    }
}
