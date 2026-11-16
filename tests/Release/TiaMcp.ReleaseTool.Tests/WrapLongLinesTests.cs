using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class WrapLongLinesTests
{
    private static string LongLine(string name = "Call") => name + "(" + new string('a', 161) + ");";

    [Theory]
    [InlineData(" // TODO")]
    [InlineData(", \"中文\"")]
    public void WrappableLinesWithReportedCategoriesStillFail(string suffix)
    {
        using var tree = new GitTree();
        tree.BaseFile("Changed.cs", "Short();\n");
        tree.Commit();
        string arguments = string.Join(", ", Enumerable.Repeat("argumentWithLongName", 15));
        string line = suffix.StartsWith(" //", StringComparison.Ordinal) ? "Call(" + arguments + ");" + suffix
            : "Call(" + arguments + suffix + ");";
        Assert.True(line.Length > 300);
        Assert.All(WrapLongLines.Format(line, "Changed.cs").Split('\n'), fragment => Assert.True(fragment.Length <= 160));
        tree.Write("Changed.cs", line + "\n");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains("Changed.cs:1:", result.Output);
    }

    [Theory]
    [InlineData("Walk", 0)]
    [InlineData("Go", 0)]
    [InlineData("LongCall", 1)]
    public void ExistingLongLinesFailOnlyWhenTheyGetLonger(string replacement, int expected)
    {
        using var tree = new GitTree();
        tree.BaseFile("Changed.cs", LongLine() + "\n");
        tree.Commit();
        tree.Write("Changed.cs", LongLine(replacement) + "\n");
        Assert.Equal(expected, tree.Check().Code);
    }

    [Fact]
    public void EqualSizeHunksPairOldAndNewLinesByPosition()
    {
        using var tree = new GitTree();
        tree.BaseFile("Changed.cs", LongLine("Original") + "\nShort();\n");
        tree.Commit();
        tree.Write("Changed.cs", LongLine("Changed") + "\n" + LongLine() + "\n");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain("Changed.cs:1:", result.Output);
        Assert.Contains("Changed.cs:2:", result.Output);
    }

    [Fact]
    public void UnequalSizeHunksCompareEachNewLineWithLongestRemovedLine()
    {
        using var tree = new GitTree();
        tree.BaseFile("Changed.cs", LongLine("Original") + "\nShort();\n");
        tree.Commit();
        tree.Write("Changed.cs", LongLine("Changed") + "\n" + LongLine() + "\n" + LongLine("Lengthened") + "\n");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain("Changed.cs:1:", result.Output);
        Assert.DoesNotContain("Changed.cs:2:", result.Output);
        Assert.Contains("Changed.cs:3:", result.Output);
    }

    [Fact]
    public void AddedLongLinesFailBesideExistingLongLines()
    {
        using var tree = new GitTree();
        tree.BaseFile("Changed.cs", LongLine("Original") + "\n");
        tree.Commit();
        tree.Write("Changed.cs", LongLine("Original") + "\n" + LongLine() + "\n");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains("Changed.cs:2:", result.Output);
    }

    [Fact]
    public void AlwaysColoredGitDiffStillReportsLongLines()
    {
        using var tree = new GitTree();
        tree.BaseFile("Changed.cs", "Short();\n");
        tree.Commit();
        tree.Git("config", "color.ui", "always");
        tree.Write("Changed.cs", LongLine() + "\n");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains("Changed.cs:1:", result.Output);
    }

    [Fact]
    public void FormatterExceptionsKeepOriginalLineNumbersAfterEarlierWrapping()
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", "Short();\n");
        tree.Commit();
        string call = "Call(" + string.Join(", ", Enumerable.Repeat("longArgumentName", 15)) + ");";
        tree.Write("New.cs", call + "\nvar text = \"\"\"\n" + new string('a', 170) + "\n\"\"\";\n");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains("New.cs:1:", result.Output);
        Assert.Contains("New.cs:3 (verbatim-or-raw)", result.Output);
    }

    [Fact]
    public void OldLongLinesPassAndChecksPreserveWorkingBytesAndIndex()
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", LongLine() + "\nShort();\n");
        tree.Commit();
        tree.Write("Old.cs", LongLine() + "\nChanged();\n");
        var contents = File.ReadAllBytes(Path.Combine(tree.Root, "Old.cs"));
        var index = File.ReadAllBytes(Path.Combine(tree.Root, ".git/index"));
        var result = tree.Check();
        Assert.Equal(0, result.Code);
        Assert.DoesNotContain("Old.cs:1:", result.Output);
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(tree.Root, "Old.cs")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(tree.Root, ".git/index")));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("added")]
    [InlineData("untracked")]
    [InlineData("committed")]
    public void ReportsEveryNewLongLineWithPathAndCurrentLineNumber(string kind)
    {
        using var tree = new GitTree();
        const string path = "说明 with spaces.cs";
        tree.BaseFile(path, "First();\nShort();\n");
        tree.Commit();
        string source = "First();\n" + LongLine() + "\n" + LongLine("Other") + "\n";
        if (kind == "untracked") tree.Write("New.cs", source);
        else
        {
            tree.Write(path, kind == "added" ? "First();\nShort();\n" + LongLine() + "\n" : source);
            if (kind == "committed") { tree.Track(path); tree.Commit(branch: true); }
        }
        var before = File.ReadAllBytes(Path.Combine(tree.Root, kind == "untracked" ? "New.cs" : path));
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains((kind == "untracked" ? "New.cs:2:" : path + (kind == "added" ? ":3:" : ":2:")), result.Output);
        if (kind != "added") Assert.Contains((kind == "untracked" ? "New.cs" : path) + ":3:", result.Output);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(tree.Root, kind == "untracked" ? "New.cs" : path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PureRenameDoesNotRecheckOldLongLines(bool staged)
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", LongLine() + "\n");
        tree.Commit();
        File.Move(Path.Combine(tree.Root, "Old.cs"), Path.Combine(tree.Root, "Moved.cs"));
        if (staged) { tree.Git("update-index", "--remove", "Old.cs"); tree.Track("Moved.cs"); }
        var result = tree.Check();
        Assert.Equal(0, result.Code);
        Assert.DoesNotContain("Moved.cs:1:", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamedFileChecksOnlyItsChangedLines(bool staged)
    {
        using var tree = new GitTree();
        string source = LongLine() + "\n" + string.Concat(Enumerable.Repeat("Short();\n", 30));
        tree.BaseFile("Old.cs", source);
        tree.Commit();
        File.Move(Path.Combine(tree.Root, "Old.cs"), Path.Combine(tree.Root, "Moved.cs"));
        tree.Write("Moved.cs", source + LongLine("NewCall") + "\n");
        if (staged) { tree.Git("update-index", "--remove", "Old.cs"); tree.Track("Moved.cs"); }
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain("Moved.cs:1:", result.Output);
        Assert.Contains("Moved.cs:32:", result.Output);
    }

    [Fact]
    public void MergeBaseIgnoresLaterChangesOnTheBaseBranch()
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", LongLine() + "\nShort();\n");
        tree.Commit();
        tree.Write("Old.cs", LongLine() + "\nBranchChange();\n");
        tree.Track("Old.cs");
        tree.Commit(branch: true);
        string branchSource = File.ReadAllText(Path.Combine(tree.Root, "Old.cs"));
        tree.Write("Old.cs", LongLine("Upstream") + "\nShort();\n");
        tree.Track("Old.cs");
        string upstream = tree.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit-tree",
            tree.Git("write-tree"), "-p", tree.Git("rev-parse", "master"), "-m", "upstream fixture");
        tree.Git("update-ref", "refs/heads/master", upstream);
        tree.Git("read-tree", "HEAD");
        tree.Write("Old.cs", branchSource);
        Assert.Equal(0, tree.Check().Code);
    }

    [Fact]
    public void StagedNewFileReportsLongLines()
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", "Short();\n");
        tree.Commit();
        tree.Write("New.cs", LongLine() + "\n");
        tree.Track("New.cs");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains("New.cs:1:", result.Output);
    }

    [Fact]
    public void ChangedHunksDoNotIncludeOldLinesFromConfiguredInterHunkContext()
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", "First();\n" + LongLine() + "\nLast();\n");
        tree.Commit();
        tree.Git("config", "diff.interHunkContext", "5");
        tree.Write("Old.cs", "ChangedFirst();\n" + LongLine() + "\nChangedLast();\n");
        Assert.Equal(0, tree.Check().Code);
    }

    [Fact]
    public void RenameAndReplacementAtOldPathHaveIndependentLineNumbers()
    {
        using var tree = new GitTree();
        string source = LongLine() + "\n" + string.Concat(Enumerable.Repeat("Short();\n", 30));
        tree.BaseFile("Old.cs", source);
        tree.Commit();
        tree.Write("Moved.cs", source + LongLine("NewCall") + "\n");
        tree.Track("Moved.cs");
        tree.Write("Old.cs", "Replacement();\n" + LongLine("Replacement") + "\n");
        tree.Track("Old.cs");
        var result = tree.Check();
        Assert.Equal(1, result.Code);
        Assert.Contains("Old.cs:2:", result.Output);
        Assert.Contains("Moved.cs:32:", result.Output);
        Assert.DoesNotContain("Moved.cs:1:", result.Output);
    }

    [Theory]
    [InlineData("obj/Generated.cs", "")]
    [InlineData("bin/Generated.cs", "")]
    [InlineData("Nested/Generated.g.cs", "")]
    [InlineData("Generated.cs", "// <auto-generated>\n")]
    [InlineData("src/Shared/BundleLayout.cs", "")]
    public void GeneratedFilesAndSourceTablesAreExcludedInBothModes(string path, string prefix)
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", "Short();\n");
        tree.Commit();
        string source = prefix + LongLine() + "\n";
        tree.Write(path, source);
        Assert.Equal(0, tree.Check().Code);
        if (!path.StartsWith("obj/", StringComparison.Ordinal) && !path.StartsWith("bin/", StringComparison.Ordinal))
            Assert.Equal(source, WrapLongLines.Format(source, path));
    }

    [Fact]
    public void ReportedExceptionsAreListedAndMultilineContentIsExcluded()
    {
        using var tree = new GitTree();
        tree.BaseFile("Old.cs", "Short();\n");
        tree.Commit();
        tree.Write("New.cs", "var text = \"\"\"\n" + new string('a', 170) + "\n\"\"\";\n// " + new string('b', 170) + "\n");
        var result = tree.Check();
        Assert.Equal(0, result.Code);
        Assert.Contains("Excluded files:", result.Output);
        Assert.Contains("Excluded lines:", result.Output);
        Assert.Contains("New.cs:2 (verbatim-or-raw)", result.Output);
        Assert.Contains("New.cs:4 (comment)", result.Output);
    }

    [Theory]
    [InlineData("var values = new[] { 1, 2 }; var result = Build([.. properties, otherLongPropertyName]);")]
    [InlineData("var result = Build([\n.. propertiesWithVeryLongName, otherLongPropertyName]);")]
    [InlineData("var result = veryLongCollectionName[a..b];")]
    [InlineData("var result = veryLongCollectionName[a>>>b];")]
    public void FormatterPreservesContextualMulticharacterTokens(string source)
    {
        string formatted = WrapLongLines.Format(source, "Example.cs", 35);
        Assert.True(WrapLongLines.Equivalent(source, formatted));
        Assert.DoesNotContain(".\n", formatted);
        Assert.Equal(formatted, WrapLongLines.Format(formatted, "Example.cs", 35));
    }

    [Theory]
    [InlineData("var result = Build([.. properties]);", "var result = Build([.\n. properties]);")]
    [InlineData("var result = values[a..b];", "var result = values[a.\n.b];")]
    [InlineData("var result = a >> b;", "var result = a >\n> b;")]
    [InlineData("var result = a >>> b;", "var result = a >\n>> b;")]
    [InlineData("var result = a ?? b;", "var result = a ?\n? b;")]
    public void EquivalenceRejectsSplitsThatChangeTokenisation(string before, string after) =>
        Assert.False(WrapLongLines.Equivalent(before, after));

    private sealed class GitTree : IDisposable
    {
        private readonly StaticTree tree = new();
        internal string Root => tree.Root;
        internal GitTree()
        {
            Git("init", "--initial-branch=master");
            Git("config", "core.autocrlf", "false");
        }
        internal void Write(string path, string source) => tree.Write(path, source);
        internal string Git(params string[] args)
        {
            var result = ReviewChangeSet.Git("git", args, Root);
            ProcessRunner.RequireSuccess(result, "Long-line Git fixture");
            return result.StandardOutput.Trim();
        }
        internal void BaseFile(string path, string source) { Write(path, source); Track(path); }
        internal void Track(string path) => Git("update-index", "--add", "--cacheinfo", "100644", Git("hash-object", "-w", path), path);
        internal void Commit(bool branch = false)
        {
            if (branch) Git("symbolic-ref", "HEAD", "refs/heads/topic");
            var args = new List<string> { "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit-tree", Git("write-tree") };
            if (branch) { args.Add("-p"); args.Add(Git("rev-parse", "master")); }
            args.AddRange(["-m", "fixture"]);
            Git("update-ref", "HEAD", Git(args.ToArray()));
        }
        internal (int Code, string Output) Check()
        {
            using var output = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(output);
                int code = WrapLongLines.Run(Root, Options.Parse(["-Check", "-Base", "master"], Program.BooleanOptions));
                return (code, output.ToString());
            }
            finally { Console.SetOut(previous); }
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, ".git"), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            tree.Dispose();
        }
    }
}
