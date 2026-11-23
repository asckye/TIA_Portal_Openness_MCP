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
    [InlineData("torn-rerun")]
    [InlineData("blank-rerun")]
    [InlineData("null-rerun")]
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
        if (problem == "torn-rerun") File.AppendAllText(path, "{\"commit\":\"unfinished");
        if (problem == "blank-rerun") File.AppendAllText(path, "\n");
        if (problem == "null-rerun") File.AppendAllText(path, "null\n");
        if (problem == "invalid-json") File.WriteAllText(path, "{");
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
        Assert.Null(Require("-BuildOnly"));
        Assert.Throws<ReleaseException>(() => Require());
        Assert.Throws<ReleaseException>(() => Require("-BuildOnly=false"));
        Assert.Contains("-BuildOnly", Assert.Throws<ReleaseException>(() => Require("-SkipMergeCheck", "-NoDraftUpload")).Message);
        Assert.Null(Require("-BuildOnly", "-SkipMergeCheck"));
    }

    [Fact]
    public void Removed_merge_check_switch_cannot_bypass_release_evidence()
    {
        using var checkout = new StaticTree();
        MergeCheckRun? Require(params string[] flags) => MergeCheckRuns.RequirePassing(checkout.Root,
            Options.Parse(flags, Program.BooleanOptions), args => Git(checkout.Root, args));
        Assert.Equal(64, Assert.Throws<ReleaseException>(() => Require("-SkipMergeCheck", "-NoDraftUpload")).ExitCode);
        Assert.Equal(64, Assert.Throws<ReleaseException>(() => Require("-BuildOnly", "-SkipMergeCheck")).ExitCode);
        Assert.Contains("merge-check -All", Assert.Throws<ReleaseException>(() => Require("-SkipMergeCheck=true")).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DamagedLinesPreserveTheLatestIntactFullRun(int exit)
    {
        using var checkout = new StaticTree();
        var path = Path.Combine(checkout.Root, MergeCheckRuns.RelativePath);
        var run = new MergeCheckRun(DateTime.UnixEpoch, Commit, "master", true, true, false, 2.5,
            0, [], new() { ["suite-stage"] = 2 }, checkout.Root, Tree);
        MergeCheckRuns.Append(path, run);
        File.AppendAllText(path, "{\"commit\":\"unfinished"); // Simulate a writer interrupted before its newline.
        var latest = run with { Utc = run.Utc.AddMinutes(1), Exit = exit, Failed = exit == 0 ? [] : ["suite-stage"] };
        MergeCheckRuns.Append(path, latest);
        var options = Options.Parse([], Program.BooleanOptions);
        MergeCheckRun? Require() => MergeCheckRuns.RequirePassing(checkout.Root, options, args => Git(checkout.Root, args));
        if (exit == 0) Assert.Equal(latest.Utc, Require()!.Utc);
        else Assert.Contains("merge-check -All", Assert.Throws<ReleaseException>(() => Require()).Message);
        File.AppendAllText(path, "{not-json}\n{\"exit\":0}\n{");

        var rows = MergeCheckRuns.Read(path);
        Assert.Equal(new[] { run.Utc, latest.Utc }, rows.Select(row => row.Utc));
        var damaged = Assert.Throws<ReleaseException>(() => Require());
        Assert.Contains("Unreadable merge-check record", damaged.Message);
        Assert.Contains("merge-check -All", damaged.Message);
    }

    [Fact]
    public void AnAppendRetriesTheExclusiveLockWithoutLosingOrDuplicatingRecords()
    {
        using var checkout = new StaticTree();
        var path = Path.Combine(checkout.Root, "runs.jsonl");
        var run = new MergeCheckRun(DateTime.UnixEpoch, Commit, "first", true, true, false, 1,
            0, [], new() { [new string('j', 8192)] = 1 }, checkout.Root, Tree);
        using var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        int retries = 0;
        MergeCheckRuns.Append(path, run with { Branch = "second" }, () =>
        {
            retries++;
            held.Dispose();
            MergeCheckRuns.Append(path, run);
        });
        Assert.Equal(1, retries);
        Assert.Equal(new[] { "first", "second" }, MergeCheckRuns.Read(path).Select(row => row.Branch));
        Assert.Equal(2, File.ReadAllLines(path).Length);
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
