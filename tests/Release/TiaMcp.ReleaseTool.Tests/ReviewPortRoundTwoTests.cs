using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReviewPortRoundTwoTests
{
    private static CheckChange Change(string before, string after, string path = "tests/Example.cs") => new(path, path, before, after);

    [Fact]
    public void NewPlatformOverridesCannotWeakenEffectiveLimits()
    {
        const string before = """{"one":{"minimumPassed":20,"maximumSkipped":0}}""";
        const string after = """{"one":{"minimumPassed":20,"maximumSkipped":0,"platforms":{"windows":{"minimumPassed":1,"maximumSkipped":9999}}}}""";
        var rows = CheckLoosening.Scan([Change(before, after, "tests/test-suites.json")]);
        Assert.Contains(rows, row => row.Kind == "minimum-passed-lowered");
        Assert.Contains(rows, row => row.Kind == "maximum-skipped-raised");
        Assert.Empty(CheckLoosening.Scan([Change(before, after.Replace(":1,", ":21,").Replace(":9999", ":0"), "tests/test-suites.json")]));
    }

    [Fact]
    public void ApprovalOnlyWorkingTreeChangesAreUnauthorized()
    {
        using var tree = new StaticTree();
        const string empty = """{"format":2,"tasks":[]}""";
        tree.Write(CheckChangeApprovals.RelativePath, empty);
        CheckChangeApprovals.Record(tree.Root, "TASK", "2026-11-04", "decision", [new("tests/A.cs", "test-removed", "A", "removed")], null);
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "base" : args[0] == "show" ? empty : "", "");
        var audit = CheckChangeApprovals.Audit(tree.Root, "base",
            [Change(empty, File.ReadAllText(Path.Combine(tree.Root, CheckChangeApprovals.RelativePath)), CheckChangeApprovals.RelativePath)], Git);
        Assert.Single(audit.Unauthorized);
    }

    [Theory]
    [InlineData("tests/FooTests.cs.txt")]
    [InlineData("tests/FooTests.py")]
    [InlineData("archive/FooTests.cs")]
    public void RenamingOutsideTestSourceRemovesTheCheck(string destination)
    {
        const string source = "class C { [Fact] void T() { Assert.True(true); } }";
        var rows = CheckLoosening.Scan([new(destination, "tests/FooTests.cs", source, source)]);
        Assert.Contains(rows, row => row.Kind == "check-file-deleted");
        Assert.Empty(CheckLoosening.Scan([new("tests/Other/FooTests.cs", "tests/FooTests.cs", source, source)]));
    }

    [Theory]
    [InlineData("InlineData")]
    [InlineData("MemberData")]
    [InlineData("TheoryData")]
    public void DataRowsAreAssertionsAndMayBeReordered(string attribute)
    {
        string before = $"class C {{ [Theory][{attribute}(1)][{attribute}(2)] void T() {{ }} }}";
        string removed = $"class C {{ [Theory][{attribute}(1)] void T() {{ }} }}";
        string reordered = $"class C {{ [Theory][{attribute}(2)][{attribute}(1)] void T() {{ }} }}";
        Assert.Contains(CheckLoosening.Scan([Change(before, removed)]), row => row.Kind == "assertion-removed");
        Assert.Empty(CheckLoosening.Scan([Change(before, reordered)]));
    }

    [Fact]
    public void TheoryDataInitializersCannotLoseRows()
    {
        const string before = "class C { TheoryData<int> Data = new TheoryData<int> { 1, 2 }; }";
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace("1, 2", "1"))]), row => row.Kind == "assertion-removed");
        Assert.Empty(CheckLoosening.Scan([Change(before, before.Replace("1, 2", "2, 1"))]));
    }

    [Fact]
    public void ScriptHelpersAndCSharpCheckCallsAreAssertions()
    {
        Assert.Contains(CheckLoosening.Scan([Change("def verify():\n    assert count >= 8\n", "def verify():\n    pass\n",
            "scripts/checks/Test-Example.py")]), row => row.Kind == "assertion-removed");
        Assert.Contains(CheckLoosening.Scan([Change("class C { void Verify() { Check(count >= 8, \"checks\"); } }",
            "class C { void Verify() { } }")]), row => row.Kind == "assertion-removed");
        Assert.Empty(CheckLoosening.Scan([Change("class C { [Fact] void T() { Assert.True(count >= 8); } }",
            "class C { [Fact] void T() { Assert.True(count >= 9); } }")]));
    }

    [Fact]
    public void PythonNumericTimeoutsCannotGrow()
    {
        const string before = "def verify():\n    result = run(timeout=30)\n";
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace("30", "60"), "scripts/checks/Test-Example.py")]),
            row => row.Kind == "limit-raised");
        Assert.Empty(CheckLoosening.Scan([Change(before, before.Replace("30", "20"), "scripts/checks/Test-Example.py")]));
    }

    [Fact]
    public void GateNumberChangesRemainVisibleWhenMessagesChange()
    {
        const string before = "class C { bool CheckResult() => count >= 157 || Fail(\"below 157\"); }";
        const string after = "class C { bool CheckResult() => count >= 150 || Fail(\"below 150\"); }";
        Assert.Contains(CheckLoosening.Scan([Change(before, after, "build-tools/release/MergeCheck.cs")]), row => row.Kind == "limit-lowered");
        Assert.Empty(CheckLoosening.Scan([Change(after, before, "build-tools/release/MergeCheck.cs")]));
        const string test = "class C { [Fact] void Before() { Assert.True(true); } }";
        Assert.Empty(CheckLoosening.Scan([Change(test, test.Replace("Before()", "After()"))]));
    }

    [Fact]
    public void ZeroRowRulesCanOnlyBeInitializedOnceAndNeedTheirProfile()
    {
        using var tree = new StaticTree();
        try
        {
            string Git(params string[] args)
            {
                var result = ReviewChangeSet.Git("git", args, tree.Root);
                ProcessRunner.RequireSuccess(result, "Fixture git");
                return result.StandardOutput.Trim();
            }
            Git("init");
            tree.Write(".editorconfig", "");
            string blob = Git("hash-object", "-w", ".editorconfig");
            var contents = ProcessRunner.Run("git", ["mktree"], tree.Root, standardInput: "100644 blob " + blob + "\t.editorconfig\n");
            ProcessRunner.RequireSuccess(contents, "Fixture tree");
            string revision = Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit-tree", contents.StandardOutput.Trim(), "-m", "fixture");
            Git("update-ref", "HEAD", revision);
            string source = tree.Write("src/C.cs", "class C {}");
            string receipt = "context|" + Path.Combine(tree.Root, "C.csproj") + "|net10.0|NET|";
            tree.Write("reports/a.inputs", receipt + "merge\n" + source + "|" + FileHash.Sha256(source) + "\n");
            tree.Write("reports/a.sarif", """{"version":"2.1.0","runs":[{"results":[]}]}""");
            string baseline = tree.Write("baseline.json", """{"format":1,"entries":[]}""");
            string review = tree.Write("review.json", """{"format":1,"entries":[]}""");
            var options = Options.Parse(["-InitializeRules", "CA2100", "-Base", "HEAD", "-ReviewData", review,
                "-Baseline", baseline, "-DiagnosticsDirectory", Path.Combine(tree.Root, "reports")], Program.BooleanOptions);
            Assert.NotEqual(0, AnalyzerWarnings.Run(tree.Root, options));
            tree.Write("reports/a.inputs", receipt + "release\n" + source + "|" + FileHash.Sha256(source) + "\n");
            Assert.Equal(0, AnalyzerWarnings.Run(tree.Root, options));
            using var recorded = JsonDocument.Parse(File.ReadAllText(baseline));
            Assert.Contains("CA2100", recorded.RootElement.GetProperty("initializedRules").EnumerateArray().Select(item => item.GetString()));
            var shrink = Options.Parse(["-UpdateBaseline", "-Complete", "-Baseline", baseline,
                "-DiagnosticsDirectory", Path.Combine(tree.Root, "reports")], Program.BooleanOptions);
            Assert.Equal(0, AnalyzerWarnings.Run(tree.Root, shrink));
            Assert.Equal(64, Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Run(tree.Root, options)).ExitCode);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(tree.Root, ".git"), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
    }
    [Theory]
    [InlineData("Implement change", false)]
    [InlineData("Record maintainer approval for TASK", true)]
    public void ApprovalOnlyCommitsStillRequireTheMaintainerSubject(string subject, bool accepted)
    {
        using var tree = new StaticTree();
        const string empty = """{"format":2,"tasks":[]}""";
        tree.Write(CheckChangeApprovals.RelativePath, empty);
        CheckChangeApprovals.Record(tree.Root, "TASK", "2026-11-04", "decision", [new("tests/A.cs", "test-removed", "A", "removed")], null);
        string added = File.ReadAllText(Path.Combine(tree.Root, CheckChangeApprovals.RelativePath));
        CommandResult Git(string[] args) => new(0, args[0] switch
        {
            "merge-base" => "base", "rev-list" => "commit", "diff-tree" => CheckChangeApprovals.RelativePath,
            "show" when args.Contains("--format=%s") => subject,
            "show" when args[1].StartsWith("commit:") || args[1].StartsWith("HEAD:") => added,
            _ => empty
        }, "");
        var result = CheckChangeApprovals.Audit(tree.Root, "base", [Change(empty, added, CheckChangeApprovals.RelativePath)], Git);
        Assert.Equal(accepted ? 0 : 1, result.Unauthorized.Length);
        Assert.Equal(accepted ? 0 : 1, result.Violations!.Length);
    }

    [Fact]
    public void EvenAnEmptyApprovalDocumentEditNeedsACommit()
    {
        using var tree = new StaticTree();
        const string empty = """{"format":2,"tasks":[]}""";
        tree.Write(CheckChangeApprovals.RelativePath, empty + "\n");
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "base" : args[0] == "show" ? empty : "", "");
        var result = CheckChangeApprovals.Audit(tree.Root, "base", [], Git);
        Assert.Single(result.Violations!);
    }

    [Fact]
    public void TestMovesUseProjectCompileDirectoriesAndExplicitLinks()
    {
        using var tree = new StaticTree();
        tree
            .Write("tests/test-suites.json",
                """{"one":{"project":"tests/One/One.csproj"},"two":{"project":"tests/Two/Two.csproj"},"linked":{"project":"tests/Linked/Linked.csproj"}}""");
        tree.Write("tests/One/One.csproj", "<Project />");
        tree.Write("tests/Two/Two.csproj", "<Project />");
        tree.Write("tests/Linked/Linked.csproj", """<Project><ItemGroup><Compile Include="../../shared/Linked.cs" /></ItemGroup></Project>""");
        const string source = "class C { [Fact] void T() { Assert.True(true); } }";
        var included = ReviewTestSources.Create(tree.Root);
        Assert.Contains(CheckLoosening.Scan([new("tests/Unused/T.cs", "tests/One/T.cs", source, source)], included),
            row => row.Kind == "check-file-deleted");
        Assert.Empty(CheckLoosening.Scan([new("tests/Two/T.cs", "tests/One/T.cs", source, source)], included));
        Assert.Empty(CheckLoosening.Scan([new("shared/Linked.cs", "tests/One/T.cs", source, source)], included));
    }

    [Fact]
    public void StricterAssertionsAndUnchangedMovesStayClean()
    {
        Assert.Empty(CheckLoosening.Scan([Change("def test_before():\n    assert True\n",
            "def test_after():\n    assert True\n", "scripts/checks/Test-Example.py")]));
        Assert.Empty(CheckLoosening.Scan([Change("def verify():\n    assert count >= 8\n",
            "def verify():\n    assert count >= 9\n", "scripts/checks/Test-Example.py")]));
        Assert.Empty(CheckLoosening.Scan([Change("class C { void M() { Check(size <= 10, \"before\"); } }",
            "class C { void M() { Check(size <= 9, \"after\"); } }")]));
        Assert.Contains(CheckLoosening.Scan([Change("def verify():\n    assert count >= 8\n",
            "def verify():\n    assert count >= 7\n", "scripts/checks/Test-Example.py")]), row => row.Kind == "assertion-removed");
    }

    [Theory]
    [InlineData("class C { static TheoryData<int> Data() => new() { 1, 2 }; }", "1, 2", "1", "2, 1")]
    [InlineData("class C { static void Data() { var data = new TheoryData<int>(); data.Add(1); data.Add(2); } }",
        "data.Add(1); data.Add(2);", "data.Add(1);", "data.Add(2); data.Add(1);")]
    [InlineData("class C { [Theory][MemberData(nameof(Data))] void T(int x){} " +
        "static object[][] Data => new[] { new object[] { 1 }, new object[] { 2 } }; }",
        "new object[] { 1 }, new object[] { 2 }", "new object[] { 1 }", "new object[] { 2 }, new object[] { 1 }")]
    public void DirectDataProvidersCannotLoseRowsButCanReorder(string before, string rows, string reduced, string reordered)
    {
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace(rows, reduced))]), row => row.Kind == "assertion-removed");
        Assert.Empty(CheckLoosening.Scan([Change(before, before.Replace(rows, reordered))]));
    }

    [Fact]
    public void ImportedSharedTestSourcesAndGlobsRemainCompiled()
    {
        using var tree = new StaticTree();
        tree.Write("tests/test-suites.json", """{"one":{"project":"tests/One/One.csproj"}}""");
        tree.Write("tests/One/One.csproj", """<Project><Import Project="../Common.props" /></Project>""");
        tree.Write("tests/Common.props", """
            <Project><ItemGroup><Compile Include="$(MSBuildThisFileDirectory)Shared/**/*.cs" /></ItemGroup></Project>
            """);
        Assert.True(ReviewTestSources.Create(tree.Root)("tests/Shared/Test.cs"));
    }

    [Fact]
    public void NegatingABoundDoesNotTurnWeakeningIntoStrengthening()
    {
        Assert.Contains(CheckLoosening.Scan([Change("class C { void M(){ Check(!(x >= 8)); } }",
            "class C { void M(){ Check(!(x >= 9)); } }")]), row => row.Kind == "assertion-removed");
    }

    [Fact]
    public void RemovingAWeakPlatformOverrideCanStrengthenTheGate()
    {
        const string before = """{"one":{"minimumPassed":20,"maximumSkipped":0,"platforms":{"windows":{"minimumPassed":1}}}}""";
        const string after = """{"one":{"minimumPassed":20,"maximumSkipped":0,"platforms":{"windows":{}}}}""";
        Assert.Empty(CheckLoosening.Scan([Change(before, after, "tests/test-suites.json")]));
        Assert.Contains(CheckLoosening.Scan([Change(after, before, "tests/test-suites.json")]), row => row.Kind == "minimum-passed-lowered");
    }

    [Fact]
    public void RemovedDuplicateDataRowsHaveDistinctApprovalKeys()
    {
        const string before = "class C { TheoryData<int> Data = new TheoryData<int> { 1, 1 }; }";
        var rows = CheckLoosening.Scan([Change(before, before.Replace("{ 1, 1 }", "{ }"))]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.Identifier).Distinct().Count());
    }

    [Fact]
    public void InitializationHistoryCannotBeErasedToAllowReinitialization()
    {
        const string before = """{"format":1,"entries":[],"initializedRules":["CA2100"]}""";
        const string removed = """{"format":1,"entries":[],"initializedRules":[]}""";
        const string missing = """{"format":1,"entries":[]}""";
        Assert.Contains(CheckLoosening.Scan([Change(before, removed, AnalyzerWarnings.BaselinePath)]),
            row => row.Kind == "initialization-history-removed");
        Assert.Contains(CheckLoosening.Scan([Change(before, missing, AnalyzerWarnings.BaselinePath)]),
            row => row.Kind == "initialization-history-removed");
        Assert.Empty(CheckLoosening.Scan([Change(removed, before, AnalyzerWarnings.BaselinePath)]));
    }

    [Fact]
    public void NamedPythonMinimumCannotBeLowered()
    {
        const string before = "MIN_CHECKS = 36\n";
        Assert.Contains(CheckLoosening.Scan([Change(before, "MIN_CHECKS = 35\n", "scripts/checks/Test-Example.py")]),
            row => row.Kind == "limit-lowered");
        Assert.Empty(CheckLoosening.Scan([Change(before, "MIN_CHECKS = 37\n", "scripts/checks/Test-Example.py")]));
    }

    [Fact]
    public void AllTimeoutArgumentsOnOneLineAreCompared()
    {
        const string before = "def verify():\n    run(rpc_timeout=15, startup_timeout=60)\n";
        Assert.Contains(CheckLoosening.Scan([Change(before, before.Replace("60", "90"), "scripts/checks/Test-Example.py")]),
            row => row.Kind == "limit-raised");
    }

}
