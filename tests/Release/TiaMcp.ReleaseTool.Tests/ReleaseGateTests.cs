using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseGateTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Tree = "fedcba9876543210fedcba9876543210fedcba98";

    [Theory]
    [InlineData("merge-check", "1")]
    [InlineData("prepare", "0")]
    [InlineData("test-suites", "")]
    public void GatesRefuseAnySetGoldenUpdateValue(string command, string value)
    {
        Assert.Contains("TIA_MCP_UPDATE_GOLDEN",
            Assert.Throws<ReleaseException>(() => Program.RequireGoldenVerification(command, value)).Message);
        Program.RequireGoldenVerification(command, null);
        Program.RequireGoldenVerification("check-repository", value);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("missing-exit")]
    [InlineData("changed-commit")]
    [InlineData("changed-tree")]
    [InlineData("legacy-record")]
    [InlineData("dirty")]
    [InlineData("narrow")]
    [InlineData("docs-only")]
    [InlineData("failed-job")]
    [InlineData("failed-rerun")]
    public void PrepareRequiresPassingFullRunOnCandidateRevision(string problem)
    {
        using var checkout = new StaticTree();
        string path = Path.Combine(checkout.Root, MergeCheckRuns.RelativePath);
        var run = new MergeCheckRun(DateTime.UnixEpoch, Commit, "master", true, true, false, 2.5,
            0, [], new() { ["suite-stage"] = 2 }, checkout.Root, Tree);
        if (problem == "failed-rerun") MergeCheckRuns.Append(path, run);
        if (problem != "missing") MergeCheckRuns.Append(path, problem switch
        {
            "changed-commit" => run with { Commit = new string('b', 40) },
            "changed-tree" => run with { Tree = new string('c', 40) },
            "legacy-record" => run with { Tree = null },
            "narrow" => run with { All = false },
            "docs-only" => run with { DocsOnly = true },
            "failed-job" => run with { Failed = ["build"] },
            "failed-rerun" => run with { Exit = 1 },
            _ => run
        });
        if (problem == "invalid-json") File.AppendAllText(path, "{");
        if (problem == "missing-exit") File.WriteAllText(path, File.ReadAllText(path).Replace("\"exit\":0,", "", StringComparison.Ordinal));
        var error = Assert.Throws<ReleaseException>(() => MergeCheckRuns.RequirePassing(checkout.Root,
            Options.Parse(["-NoDraftUpload"], Program.BooleanOptions), args => Git(checkout.Root, args, problem == "dirty")));
        Assert.Contains("merge-check -All", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OnlyAConsistentCleanFullRunProvidesReleaseEvidence(bool changedHead, bool finishedDirty)
    {
        using var checkout = new StaticTree();
        var options = Options.Parse(["-All"], Program.BooleanOptions);
        var start = MergeCheckRuns.CleanRevision(args => Git(checkout.Root, args));
        var end = changedHead ? new string('e', 40) : Commit;
        MergeCheckRuns.Append(checkout.Root, options, new() { Wide = true },
            [new("suite-stage", 1, new(0, "", ""))], 1, checkout.Root,
            args => Git(checkout.Root, args, finishedDirty, end), start);
        if (changedHead || finishedDirty)
            Assert.Throws<ReleaseException>(() => MergeCheckRuns.RequirePassing(checkout.Root, options, args => Git(checkout.Root, args, commit: end)));
        else
            Assert.Equal(Tree, MergeCheckRuns.RequirePassing(checkout.Root, options, args => Git(checkout.Root, args))!.Tree);
    }

    [Fact]
    public void OnlyAnExplicitBuildOnlyExceptionCanPrepareWithoutEvidence()
    {
        using var checkout = new StaticTree();
        MergeCheckRun? Require(params string[] flags) => MergeCheckRuns.RequirePassing(checkout.Root,
            Options.Parse(flags, Program.BooleanOptions), args => Git(checkout.Root, args));
        Assert.Throws<ReleaseException>(() => Require("-BuildOnly"));
        Assert.Contains("-BuildOnly", Assert.Throws<ReleaseException>(() => Require("-SkipMergeCheck", "-NoDraftUpload")).Message);
        Assert.Null(Require("-BuildOnly", "-SkipMergeCheck"));
    }

    private static CommandResult Git(string root, string[] args, bool dirty = false, string commit = Commit) => new(0, args switch
    {
        ["status", "--porcelain=v1", "--untracked-files=all"] => dirty ? " M file" : "",
        ["rev-parse", "HEAD", "HEAD^{tree}"] => commit + "\n" + Tree + "\n",
        ["rev-parse", "HEAD"] => commit,
        ["rev-parse", "--path-format=absolute", "--git-common-dir"] => Path.Combine(root, ".git"),
        ["branch", "--show-current"] => "master",
        _ => throw new InvalidOperationException(string.Join(' ', args))
    }, "");
}
