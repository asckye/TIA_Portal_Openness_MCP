using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class ReviewAutomationTests
{
    private static CheckChange Change(string before, string after, string path = "tests/ExampleTests.cs", string? oldPath = null) =>
        new(path, oldPath ?? path, before, after);

    [Theory]
    [InlineData("Fact")]
    [InlineData("Theory")]
    [InlineData("TestMethod")]
    public void RemovedTestMethodsAreReportedAndUnchangedRenamesStayClean(string attribute)
    {
        string before = $"class Tests {{ [{attribute}] void Original() {{ Assert.True(true); }} }}";
        var removed = CheckLoosening.Scan([Change(before, "class Tests {}")]);
        Assert.Contains(removed, row => row.Kind == "test-removed" && row.Identifier.Contains("Original", StringComparison.Ordinal));
        Assert.Empty(CheckLoosening.Scan([Change(before, before.Replace("Original", "Renamed", StringComparison.Ordinal))]));
    }

    [Fact]
    public void FileRenamesAndFormattingKeepTestsAndAssertions()
    {
        const string before = "class Tests { [Fact] void Original() { Assert.True(true); } }";
        const string after = "class Tests\n{\n [Fact] void Original()\n {\n Assert.True ( true );\n }\n}";
        Assert.Empty(CheckLoosening.Scan([Change(before, after, "tests/NewTests.cs", "tests/OldTests.cs")]));
        Assert.Empty(CheckLoosening.Scan(ReviewChangeSet.PairRenames([
            new("tests/OldTests.cs", "tests/OldTests.cs", before, null), new("tests/NewTests.cs", "tests/NewTests.cs", null, after)])));
    }

    [Theory]
    [InlineData("[Fact]", "[Fact(Skip = \"reason\")]")]
    [InlineData("[Theory]", "[Theory(Skip = \"reason\")]")]
    public void AddedCSharpSkipsAreReported(string before, string after)
    {
        var rows = CheckLoosening.Scan([Change($"class Tests {{ {before} void Test() {{ }} }}", $"class Tests {{ {after} void Test() {{ }} }}")]);
        Assert.Contains(rows, row => row.Kind == "test-skipped");
    }

    [Fact]
    public void AddedSkippedTestIsAlsoReported()
    {
        Assert.Contains(CheckLoosening.Scan([Change("", "class Tests { [Fact(Skip = \"later\")] void NewTest() {} }")]), row => row.Kind == "test-skipped");
    }

    [Fact]
    public void DuplicateAssertionsRetainMultiplicityAndCommentsAreIgnored()
    {
        const string before = "class Tests { [Fact] void Test() { Assert.True(true); Assert.True(true); } }";
        const string after = "class Tests { [Fact] void Test() { Assert.True(true); /* Assert.True(true); */ } }";
        Assert.Single(CheckLoosening.Scan([Change(before, after)]), row => row.Kind == "assertion-removed");
    }

    [Fact]
    public void AssertionsAreCheckedInHelpersAndConditionalBranches()
    {
        const string before = "class Tests { void Helper() { Assert.True(true); }\n#if OTHER\n[Fact] void Test() {}\n#endif\n}";
        Assert.Contains(CheckLoosening.Scan([Change(before, "class Tests {}")]), row => row.Kind == "test-removed");
        Assert
            .Contains(CheckLoosening.Scan([Change(before, before.Replace("Assert.True(true);", "", StringComparison.Ordinal))]), row => row
                .Kind == "assertion-removed");
    }

    [Fact]
    public void RemovingOneConditionalTestDefinitionCannotHideBehindTheOther()
    {
        const string before = "class Tests {\n#if FIRST\n[Fact] void Test() {}\n#else\n[Fact] void Test() {}\n#endif\n}";
        const string after = "class Tests { [Fact] void Test() {} }";
        Assert.Single(CheckLoosening.Scan([Change(before, after)]), row => row.Kind == "test-removed");
    }

    [Fact]
    public void PythonRemovalSkipAndAssertionAreReported()
    {
        const string before =
            "def test_removed():\n    assert True\n\nclass TestSuite:\n    def test_kept(self):\n        assert 1 == 1\n        assert True\n";
        const string after = "class TestSuite:\n    @pytest.mark.skip(reason='later')\n    def test_kept(self):\n        assert True\n";
        var rows = CheckLoosening.Scan([Change(before, after, "tests/test_example.py")]);
        Assert.Contains(rows, row => row.Kind == "test-removed");
        Assert.Contains(rows, row => row.Kind == "test-skipped");
        Assert.Contains(rows, row => row.Kind == "assertion-removed");
    }

    [Theory]
    [InlineData("@pytest.mark.skip(reason='later')\nclass TestSuite:\n    def test_kept(self):\n        assert True\n")]
    [InlineData("pytestmark = pytest.mark.skip(reason='later')\nclass TestSuite:\n    def test_kept(self):\n        assert True\n")]
    public void PythonClassAndModuleSkipsAreReported(string after)
    {
        const string before = "class TestSuite:\n    def test_kept(self):\n        assert True\n";
        Assert.Single(CheckLoosening.Scan([Change(before, after, "tests/test_example.py")]), row => row.Kind == "test-skipped");
    }

    [Fact]
    public void SuiteGatesCoverPlatformOverridesAndRemovedSuites()
    {
        const string before =
            "{\"one\":{\"minimumPassed\":10,\"maximumSkipped\":0,\"platforms\":{\"linux\":{\"minimumPassed\":8,\"maximumSkipped\":2}}}," +
            "\"two\":{\"minimumPassed\":1}}";
        const string after = "{\"one\":{\"minimumPassed\":9,\"maximumSkipped\":1,\"platforms\":{\"linux\":{\"minimumPassed\":7,\"maximumSkipped\":3}}}}";
        var rows = CheckLoosening.Scan([Change(before, after, "tests/test-suites.json")]);
        Assert.Equal(2, rows.Count(row => row.Kind == "minimum-passed-lowered"));
        Assert.Equal(2, rows.Count(row => row.Kind == "maximum-skipped-raised"));
        Assert.Single(rows, row => row.Kind == "suite-removed");
    }

    [Theory]
    [InlineData("build-tools/release/baselines/response-envelope-baseline.json", "{\"counts\":{\"x\":2}}", "{\"counts\":{\"x\":3}}", "baseline-grown")]
    [InlineData("build-tools/release/baselines/analyzer-warnings-baseline.json", "{\"entries\":[{\"fingerprint\":\"one\"}]}",
                "{\"entries\":[{\"fingerprint\":\"one\"},{\"fingerprint\":\"two\"}]}", "baseline-grown")]
    [InlineData("tests/test-suites.json", "{\"one\":{\"timeoutSeconds\":30}}", "{\"one\":{\"timeoutSeconds\":60}}", "limit-raised")]
    public void KnownLimitsAndRatchetsCannotGrow(string path, string before, string after, string kind)
    {
        Assert.Contains(CheckLoosening.Scan([Change(before, after, path)]), row => row.Kind == kind);
        Assert.Empty(CheckLoosening.Scan([Change(after, before, path)]));
    }

    [Theory]
    [InlineData("SuiteHang.cs", "const string Timeout = \"5m\";", "const string Timeout = \"6m\";")]
    [InlineData("PublishPrepared.cs", "int RemainingMilliseconds() => 300000 - elapsed;", "int RemainingMilliseconds() => 400000 - elapsed;")]
    [InlineData("MergeCheck.cs", "bool CheckResult() => passed >= 157;", "bool CheckResult() => passed >= 156;")]
    [InlineData("PlcReleaseChecks.cs", "static int BuildReleaseCountRules = 8;", "static int BuildReleaseCountRules = 7;")]
    [InlineData("PrepareChecks.cs", "bool RequirePreparedFoundationApproval() => checks != 30;",
        "bool RequirePreparedFoundationApproval() => checks != 29;")]
    public void SourceTimeoutsAndBudgetMultipliersCannotGrow(string file, string before, string after)
    {
        string path = "build-tools/release/" + file;
        Assert.NotEmpty(CheckLoosening.Scan([Change("class C { " + before + " }", "class C { " + after + " }", path)]));
        Assert.Empty(CheckLoosening.Scan([Change("class C { " + after + " }", "class C { " + before + " }", path)]));
    }

    [Theory]
    [InlineData("timeout = 30", "timeout = 60")]
    [InlineData("future.result(timeout=30)", "future.result(timeout=60)")]
    [InlineData("parser.add_argument('--max-handle-growth', type=int, default=128)", "parser.add_argument('--max-handle-growth', type=int, default=256)")]
    public void PythonTimeoutAndGrowthDefaultsAreReviewed(string before, string after)
    {
        // Named timeout and growth limits in check scripts are reviewed.
        Assert.NotEmpty(CheckLoosening.Scan([Change(before, after, "scripts/checks/Test-LocalStability.py")]));
    }

    [Fact]
    public void DeletedCheckFileRequiresItsOwnApproval()
    {
        var rows = CheckLoosening.Scan([new("scripts/checks/Retired.py", "scripts/checks/Retired.py", "print('check')", null)]);
        Assert.Contains(rows, row => row.Kind == "check-file-deleted");
    }

    [Fact]
    public void RemovedGateAndLoweredSourceMinimumAreReported()
    {
        Assert.Contains(CheckLoosening.Scan([Change("{\"one\":{\"minimumPassed\":3}}", "{\"one\":{}}", "tests/test-suites.json")]),
            row => row.Kind == "gate-removed");
        Assert.Empty(CheckLoosening.Scan([Change("class C { const int MinimumPassed = 3; }", "class C { const int MinimumPassed = 2; }", "src/Business.cs")]));
    }

    [Fact]
    public void ApprovalSilencesOnlyOneExactFindingAndRequiresReviewMetadata()
    {
        var first = new ReviewFinding("tests/One.cs", "assertion-removed", "Tests/Test:abc:1", "removed", 3);
        var second = first with { Identifier = "Tests/Test:abc:2" };
        var approval = new CheckChangeApproval(first.Path, first.Kind, first.Identifier, "2026-11-02", "REVIEW-1 test approval");
        Assert.Equal([second], CheckChangeApprovals.Unapproved([first, second], [approval]));
        using var tree = new StaticTree();
        tree
            .Write(CheckChangeApprovals.RelativePath, JsonSerializer
                .Serialize(new { format = 1, entries = new[] { approval with { Reason = "" } } }, CheckChangeApprovals.Json));
        Assert.Throws<ReleaseException>(() => CheckChangeApprovals.Load(tree.Root));
    }

    [Fact]
    public void RemovalApprovalCannotBeReusedForChangedTestContents()
    {
        const string before = "class Tests { [Fact] void Test() { Assert.Equal(1, count); } }";
        var first = Assert.Single(CheckLoosening.Scan([Change(before, "class Tests {}")]), row => row.Kind == "test-removed");
        var changed = Assert
            .Single(CheckLoosening.Scan([Change(before.Replace("Equal(1,", "Equal(2,", StringComparison.Ordinal), "class Tests {}")]), row => row
                .Kind == "test-removed");
        var approval = new CheckChangeApproval(first.Path, first.Kind, first.Identifier, "2026-11-02", "Approved only the original test");
        Assert.Single(CheckChangeApprovals.Unapproved([changed], [approval]));
    }

    [Fact]
    public void VulnerabilityReportIncludesTransitivePackagesAndOnlyBlocksHighCritical()
    {
        const string report = """
            {"version":1,"sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"app.csproj","frameworks":[{"framework":"net10.0",
            "topLevelPackages":[{"id":"Direct","resolvedVersion":"1.0","vulnerabilities":[{"severity":"Moderate",
            "advisoryurl":"https://example.test/moderate"}]}],
            "transitivePackages":[{"id":"Indirect","resolvedVersion":"2.0","vulnerabilities":[{"severity":"High",
            "advisoryurl":"https://example.test/high"},{"severity":"Critical","advisoryurl":"https://example.test/critical"}]}]}]}]}
            """;
        var rows = VulnerablePackages.Parse(report, "repo");
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, VulnerablePackages.Blocking(rows).Count);
        Assert.All(VulnerablePackages.Blocking(rows), row => Assert.Contains("Indirect@2.0", row.Identifier));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"projects\":[]}")]
    [InlineData("{\"version\":1,\"projects\":[{\"path\":\"x\",\"frameworks\":[],\"errors\":[\"offline\"]}]}")]
    public void IncompleteVulnerabilityReportsCannotPass(string report)
    {
        Assert.Throws<ReleaseException>(() => VulnerablePackages.Parse(report, "repo"));
    }

    [Fact]
    public void CleanProjectWithoutFrameworksIsACompleteReport()
    {
        const string report = """
            {"version":1,"sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"check-build.csproj"}]}
            """;
        Assert.Empty(VulnerablePackages.Parse(report, "repo"));
    }

    [Fact]
    public void ComplexityReportOrdersChangedFilesFirstAndCountsBranches()
    {
        string source = "class C { void Complex() { " + string.Concat(Enumerable.Repeat("if (true) {} ", 16)) + "} }";
        var rows = ComplexityReport.Measure("src/C.cs", source, true);
        Assert.Contains(rows, row => row.Complexity == 17 && row.Changed);
        Assert.Contains(ComplexityReport.Measure("src/Long.cs", string.Join('\n', Enumerable.Repeat("// text", 601)), false), row => row.Kind == "file");
        string report = ComplexityReport.Render(rows.Concat(ComplexityReport.Measure("src/Other.cs", source, false)));
        Assert.True(report.IndexOf("src/C.cs", StringComparison.Ordinal) < report.IndexOf("src/Other.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void ComplexityCollectionFailureStillWritesANonBlockingReport()
    {
        using var tree = new StaticTree();
        string path = Path.Combine(tree.Root, "complexity-report.md");
        var options = Options.Parse(["-Base", "missing-review-ref-0123456789", "-Output", path], new HashSet<string>());
        Assert.Equal(0, ComplexityReport.Run(tree.Root, options));
        Assert.Contains("Report unavailable", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewJobsUseRealOptionsAndRequestedBase(bool docsOnly)
    {
        var plan = new MergeCheck.Plan { DocsOnly = docsOnly };
        CheckLoosening.AddJobs(plan, "review-base", "dotnet", "output");
        Assert.Equal(docsOnly ? 0 : 2, plan.Checks.Count);
        foreach (var job in plan.Checks)
        {
            Assert.Equal("dotnet", job.Executable);
            var options = Options.Parse(job.Arguments.Skip(2).ToArray(), Program.BooleanOptions);
            Assert.Equal("review-base", options.Get("Base"));
        }
        if (!docsOnly)
        {
            Assert.Equal(new[] { "check-loosening", "complexity-report" }, plan.Checks.Select(job => job.Name));
            Assert.Contains(Path.Combine("output", "complexity-report.md"), plan.Checks[1].Arguments);
        }
    }

    [Fact]
    public void RuleInitializationRejectsRebaseliningAnAlreadyEnabledRule()
    {
        const string enabled = "dotnet_diagnostic.CA5392.severity = warning\n";
        AnalyzerRuleInitialization.Validate(new HashSet<string> { "CA5392" }, "", enabled, [], []);
        Assert.Throws<ReleaseException>(() => AnalyzerRuleInitialization.Validate(new HashSet<string> { "CA5392" }, enabled, enabled, [], []));
        Assert.Throws<ReleaseException>(() => AnalyzerRuleInitialization.Validate(new HashSet<string> { "CA5392" }, "", "", [], []));
    }



    [Fact]
    public void CompletedScanHonorsExactAdvisoryApprovalsAndNormalSources()
    {
        using var tree = new StaticTree();
        const string report = """
            {"version":1,"sources":["https://api.nuget.org/v3/index.json"],
            "projects":[{"path":"tests/FoundationHost/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj","frameworks":[{"framework":"net48",
            "transitivePackages":[{"id":"Microsoft.Bcl.Memory","resolvedVersion":"9.0.5","vulnerabilities":[{"severity":"High",
            "advisoryurl":"https://github.com/advisories/GHSA-73j8-2gch-69rq"}]}]}]}]}
            """;
        tree.Write(CheckChangeApprovals.RelativePath, "{\"format\":2,\"tasks\":[]}");
        int calls = 0;
        CommandResult Run(string[] args, string root, IDictionary<string, string?> environment)
        {
            calls++;
            Assert.Contains("--include-transitive", args);
            Assert.Equal(tree.Root, root);
            Assert.Null(environment["RestoreConfigFile"]);
            Assert.Null(environment["TIA_MCP_OFFLINE_NUGET_CONFIG"]);
            Assert.Null(environment["NuGetAudit"]);
            Assert.Contains("--source", args);
            Assert.Contains(VulnerablePackages.Source, args);
            return new(0, report, "");
        }
        Assert.Equal(1, VulnerablePackages.Check(tree.Root, Run).ExitCode);
        Assert.Equal(1, calls);
        var finding = Assert.Single(VulnerablePackages.Parse(report, tree.Root));
        var approved = new CheckChangeApproval(finding.Path, finding.Kind, finding.Identifier, "2026-11-02", "REVIEW-1 test exception");
        CheckChangeApprovals.Record(tree.Root, "REVIEW-1", approved.MaintainerDate, approved.Reason, [finding], null);
        Assert.Equal(0, VulnerablePackages.Check(tree.Root, Run).ExitCode);
        Assert.Equal(2, VulnerablePackages.Check(tree.Root, (_, _, _) => new(1, "error: Unable to load the service index", "")).ExitCode);
        Assert
            .Contains("not run: Scan timed out", VulnerablePackages.Check(tree.Root,
            (_, _, _) => throw new ReleaseException("Scan timed out")).StandardOutput);
    }

    [Fact]
    public void ApprovedHistoricalFixtureAndPreviewChangesHaveExactFindingKeys()
    {
        const string before = "class Tests { [Fact] void FixtureReferences() { Assert.Equal(3, references.Length); } }";
        const string previewBefore =
            "class Tests { [Fact] void PreviewCoverage() { var writes = methods.Where(m => m.IsWrite); Assert.All(writes, Check); } }";
        string previewAfter = previewBefore.Replace("m.IsWrite", "m.IsWrite && m.Name != \"DeleteExportHandle\"", StringComparison.Ordinal);
        var rows = CheckLoosening.Scan([
            Change(before, before.Replace("Equal(3,", "Equal(2,", StringComparison.Ordinal)),
            Change(previewBefore, previewAfter, "tests/WritePreviewCoverageTests.cs")]);
        var finding = Assert.Single(rows);
        Assert.Equal("assertion-removed", finding.Kind);
        Assert.Equal("Tests/FixtureReferences( ):" + CheckLoosening.Hash("Assert . Equal ( 3 , references . Length )") + ":1", finding.Identifier);
        var approvals = rows.Select(row => new CheckChangeApproval(row.Path, row.Kind, row.Identifier, "2026-11-02", "Q3 / DES-3 decision"));
        Assert.Empty(CheckChangeApprovals.Unapproved(rows, approvals));
    }

}
