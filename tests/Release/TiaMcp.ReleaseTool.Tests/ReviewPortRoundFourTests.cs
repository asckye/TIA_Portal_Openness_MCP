using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReviewPortRoundFourTests
{
    private static CheckChange Change(string before, string after) => new("tests/Example.cs", "tests/Example.cs", before, after);

    [Theory]
    [InlineData("Check")]
    [InlineData("check")]
    public void EveryNumericArgumentKeepsItsLowerBound(string name)
    {
        string before = "class C { void T(){ " + name + "(\"destructiveText\", \"pill\", 4.5, 7); } }";
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace("4.5", "3.0"))]), row => row.Kind == "assertion-removed");
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace(", 7)", ", 6)"))]), row => row.Kind == "assertion-removed");
        Assert.Empty(CheckLoosening.Scan([Change(before, before.Replace("4.5", "5.0"))]));
    }

    [Theory]
    [InlineData("check")]
    [InlineData("Check")]
    public void TopLevelConsoleChecksAreScanned(string name)
    {
        string before = name + "(true);\nvoid Helper() { " + name + "(true); }\n";
        var findings = CheckLoosening.Scan([Change(before, "")]);
        Assert.Equal(2, findings.Count(row => row.Kind == "assertion-removed"));
        Assert.Empty(CheckLoosening.Scan([new("src/Example.cs", "src/Example.cs", before, "")]));
    }

    [Theory]
    [InlineData("build-tools/release/baselines/tia-feature-expectations.json")]
    [InlineData("manifest/contracts/v4/baseline/21.json")]
    [InlineData("tests/Generation/Planning/Golden/example.json")]
    public void RegeneratedFilesAreReportedWithoutBlocking(string path)
    {
        using var tree = new StaticTree();
        try
        {
            Init(tree);
            tree.Write(path, "{}");
            Assert.Contains(ReviewChangeSet.Read(tree.Root, "HEAD"), row => row.Path == path);
            string output = Path.Combine(tree.Root, "report.json");
            Assert.Equal(0, CheckLoosening.Run(tree.Root, Options.Parse(["-Base", "HEAD", "-Output", output], Program.BooleanOptions)));
            string report = File.ReadAllText(output);
            Assert.Contains(path, report);
            Assert.Contains("regenerated: review the diff", report);
        }
        finally { Unlock(tree); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingRequiresAnExplicitNonemptyDecisionReason(bool empty)
    {
        using var tree = new StaticTree();
        try
        {
            Init(tree);
            tree.Write("Example.cs", "class C {}");
            tree.Write(CheckChangeApprovals.RelativePath, """{"format":2,"tasks":[]}""");
            string[] arguments = ["-Base", "HEAD", "-RecordApprovals", "-Task", "SPEC-4"];
            if (empty) arguments = [.. arguments, "-Reason", " "];
            var error = Assert.Throws<ReleaseException>(() => CheckLoosening.Run(tree.Root, Options.Parse(arguments, Program.BooleanOptions)));
            Assert.Equal(64, error.ExitCode);
            Assert.Empty(CheckChangeApprovals.Load(tree.Root));
        }
        finally { Unlock(tree); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingCannotBlessFindingsOutsideTheCurrentDiff(bool draft)
    {
        using var tree = new StaticTree();
        try
        {
            Init(tree);
            tree.Write("Example.cs", "class C {}");
            tree.Write(CheckChangeApprovals.RelativePath, """{"format":2,"tasks":[]}""");
            const string invented = """
                {"format":2,"tasks":[{"task":"SPEC-4","maintainerDate":"2026-11-04","reason":"SPEC-4",
                "items":[{"path":"tests/Other.cs","kind":"test-removed","identifier":"invented"}]}]}
                """;
            string path = draft ? tree.Write("draft.json", invented) : tree.Write(CheckChangeApprovals.RelativePath, invented);
            string[] arguments = ["-Base", "HEAD", "-RecordApprovals", "-Task", "SPEC-4", "-Reason", "SPEC-4 item 4"];
            if (draft) arguments = [.. arguments, "-ApprovalOutput", path];
            Assert.Throws<ReleaseException>(() => CheckLoosening.Run(tree.Root, Options.Parse(arguments, Program.BooleanOptions)));
            Assert.Contains("invented", File.ReadAllText(path));
        }
        finally { Unlock(tree); }
    }

    [Fact]
    public void ExplicitSpecReasonRecordsOnlyTheCurrentFinding()
    {
        using var tree = new StaticTree();
        try
        {
            Init(tree);
            tree.Write("Example.cs", "class C {}");
            tree.Write(CheckChangeApprovals.RelativePath, """{"format":2,"tasks":[]}""");
            const string reason = "REVIEW-PORT-round4.md, item 4";
            var options = Options.Parse(["-Base", "HEAD", "-RecordApprovals", "-Task", "SPEC-4", "-Reason", reason], Program.BooleanOptions);
            Assert.Equal(0, CheckLoosening.Run(tree.Root, options));
            var approval = Assert.Single(CheckChangeApprovals.Load(tree.Root));
            Assert.Equal(reason, approval.Reason);
            Assert.Equal("Example.cs", approval.Path);
        }
        finally { Unlock(tree); }
    }

    [Theory]
    [InlineData("tests/Golden/Tests.cs", "class C { [Fact] void T(){ Assert.True(true); } }", "class C {}")]
    [InlineData("scripts/checks/GoldenTests.py", "def test_x():\n    assert True\n", "")]
    public void GoldenNamedTestSourceStillRequiresApproval(string path, string before, string after)
    {
        Assert.Contains(CheckLoosening.Scan([new(path, path, before, after)]), row => row.Kind == "test-removed");
    }

    [Fact]
    public void InformationalFilesRetainEachChangedPath()
    {
        var changes = ReviewChangeSet.PairRenames([
            new("tests/Golden/old.json", "tests/Golden/old.json", "", null),
            new("tests/Golden/new.json", "tests/Golden/new.json", null, "")]);
        Assert.Equal(2, changes.Length);
    }

    [Fact]
    public void GoldenNamedTestSettingsRemainChecks()
    {
        var before = "<RunSettings />";
        var after = "<RunSettings><TestCaseFilter>FullyQualifiedName~One</TestCaseFilter></RunSettings>";
        Assert.Contains(CheckLoosening.Scan([new("tests/Golden/settings.runsettings", "tests/Golden/settings.runsettings", before, after)]),
            row => row.Kind == "test-excluded");
    }

    [Fact]
    public void MovingBetweenGoldenDataAndTestSourceDoesNotHideChecks()
    {
        const string test = "class C { [Fact] void T(){ Assert.True(true); } }";
        Assert.Contains(CheckLoosening.Scan([new("tests/Golden/reference.json", "tests/Example.cs", test, test)]),
            row => row.Kind == "check-file-deleted");
        Assert.Contains(CheckLoosening.Scan([new("tests/Example.cs", "tests/Golden/reference.json", "{}",
            "class C { [Fact(Skip=\"later\")] void T(){} }")]), row => row.Kind == "test-skipped");
    }

    private static void Init(StaticTree tree)
    {
        string Git(params string[] args)
        {
            var result = ReviewChangeSet.Git("git", args, tree.Root);
            ProcessRunner.RequireSuccess(result, "Round four fixture");
            return result.StandardOutput.Trim();
        }
        Git("init");
        tree.Write("Example.cs", "class C { [Fact] void T(){ Assert.True(true); } }");
        string blob = Git("hash-object", "-w", "Example.cs");
        var contents = ProcessRunner.Run("git", ["mktree"], tree.Root, standardInput: "100644 blob " + blob + "\tExample.cs\n");
        ProcessRunner.RequireSuccess(contents, "Fixture tree");
        string commit = Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit-tree", contents.StandardOutput.Trim(), "-m", "fixture");
        Git("update-ref", "HEAD", commit);
        Git("read-tree", commit);
    }

    private static void Unlock(StaticTree tree)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(tree.Root, ".git"), "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }
}
