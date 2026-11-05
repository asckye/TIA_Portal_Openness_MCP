using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReviewRoundTwoTests
{
    private static CheckChange Change(string before, string after, string path = "tests/Example.cs") => new(path, path, before, after);

    [Fact]
    public void ADeletedFileProducesOneFinding()
    {
        var rows = CheckLoosening
            .Scan([new("tests/Example.cs", "tests/Example.cs", "class C { [Fact] void A() { Assert.True(true); } [Fact] void B() {} }", null)]);
        Assert.Equal("check-file-deleted", Assert.Single(rows).Kind);
    }

    [Fact]
    public void TestsCanMoveAcrossFilesAndClasses()
    {
        const string method = "[Fact] void Test() { Assert.True(true); }";
        Assert.Empty(CheckLoosening.Scan([Change("namespace N; class A { " + method + " }", "namespace N; class A {}"),
            Change("namespace N; class B {}", "namespace N; class B { " + method + " }", "tests/Other.cs")]));
    }

    [Fact]
    public void FalsePreprocessorBranchIsReported()
    {
        const string before = "class C {\n[Fact] void Test() {}\n}";
        var rows = CheckLoosening
            .Scan([Change(before, before.Replace("[Fact] void Test() {}", "#if false\n[Fact] void Test() {}\n#endif", StringComparison.Ordinal))]);
        Assert.Contains(rows, row => row.Kind == "test-disabled");
    }

    [Theory]
    [InlineData("tests/X.csproj", "<Project><ItemGroup><Compile Remove=\"Example.cs\" /></ItemGroup></Project>")]
    [InlineData("tests/X.props", "<Project><ItemGroup><Compile Include=\"**/*.cs\" Exclude=\"Example.cs\" /></ItemGroup></Project>")]
    [InlineData("tests/X.runsettings",
                "<RunSettings><RunConfiguration><TestCaseFilter>FullyQualifiedName!~Slow</TestCaseFilter></RunConfiguration></RunSettings>")]
    public void ExcludedTestInputsAreReported(string path, string after)
    {
        Assert
            .Contains(CheckLoosening.Scan([Change(path.EndsWith(".runsettings", StringComparison.Ordinal) ? "<RunSettings />" : "<Project />", after, path)]),
                row => row.Kind == "test-excluded");
    }

    [Fact]
    public void SuiteFilterChangesAreReportedButOrdinaryLinqIsNot()
    {
        Assert
            .Contains(CheckLoosening
                .Scan([Change("{\"one\":{\"arguments\":[]}}", "{\"one\":{\"arguments\":[\"--filter\",\"FullyQualifiedName!~Slow\"]}}",
                            "tests/test-suites.json")]),
                row => row.Kind == "test-filter-changed");
        Assert
            .Empty(CheckLoosening.Scan([Change("class C { void M() { x.Where(y => y.A); } }",
            "class C { void M() { x.Where(y => y.B).Skip(2).Take(1); } }")]));
    }

    [Theory]
    [InlineData("self.assertEqual(1, value)")]
    [InlineData("with self.assertRaises(ValueError):\n        raise ValueError()")]
    public void PythonUnittestAssertionAndInlineSkipAreReported(string assertion)
    {
        var rows = CheckLoosening
            .Scan([Change("def test_x(self):\n    " + assertion + "\n", "def test_x(self):\n    pytest.skip('later')\n", "tests/test_example.py")]);
        Assert.Contains(rows, row => row.Kind == "assertion-removed");
        Assert.Contains(rows, row => row.Kind == "test-skipped");
    }

    [Fact]
    public void ArbitraryBusinessLimitsAreNotReviewGates()
    {
        Assert
            .Empty(CheckLoosening
                .Scan([Change("class C { const int TimeoutSeconds = 10; }", "class C { const int TimeoutSeconds = 20; }", "src/Business.cs")]));
        Assert.Empty(CheckLoosening.Scan([Change("class C { const int Threshold = 10; }",
            "class C { const int Threshold = 20; }", "tests/BusinessTests.cs")]));
        Assert
            .Contains(CheckLoosening.Scan([Change("class C { [Fact(Timeout=10)] void T() {} }", "class C { [Fact(Timeout=20)] void T() {} }")]), row => row
                .Kind == "limit-raised");
    }

    [Fact]
    public void GroupedApprovalRoundTripsAndRecordsOnlyUnapprovedRows()
    {
        using var tree = new StaticTree();
        tree.Write(CheckChangeApprovals.RelativePath, "{\"format\":2,\"tasks\":[]}");
        var row = new ReviewFinding("tests/A.cs", "test-removed", "A", "removed");
        CheckChangeApprovals.Record(tree.Root, "TASK-1", "2026-11-02", "Maintainer review", [row], null);
        var approval = Assert.Single(CheckChangeApprovals.Load(tree.Root));
        Assert.Equal("TASK-1", approval.Task);
        Assert.Empty(CheckChangeApprovals.Unapproved([row], [approval]));
        Assert.Single(JsonNode.Parse(File.ReadAllText(Path.Combine(tree.Root, CheckChangeApprovals.RelativePath)))!["tasks"]!.AsArray());
    }

    [Fact]
    public void ApprovalCommitMustBeSeparateAndHaveLeadPrefix()
    {
        Assert.True(CheckChangeApprovals.IsLeadCommit("Record maintainer approval for TASK-1", [CheckChangeApprovals.RelativePath]));
        Assert.False(CheckChangeApprovals.IsLeadCommit("Implement TASK-1", [CheckChangeApprovals.RelativePath]));
        Assert.False(CheckChangeApprovals.IsLeadCommit("Record maintainer approval for TASK-1", [CheckChangeApprovals.RelativePath, "src/C.cs"]));
    }





    [Fact]
    public void DataflowRulesBelongOnlyToReleaseProductionSources()
    {
        Assert.False(ReleaseRules.Emits("merge", "src/C.cs", "CA2100"));
        Assert.False(ReleaseRules.Emits("release", "tests/C.cs", "CA2100"));
        Assert.True(ReleaseRules.Emits("release", "src/C.cs", "CA2100"));
        Assert.True(ReleaseRules.Emits("merge", "tests/C.cs", "CA5392"));
    }

    [Theory]
    [InlineData("Implement feature", false, 1)]
    [InlineData("Record maintainer approval for TASK-1", false, 0)]
    [InlineData("Record maintainer approval for TASK-1", true, 1)]
    public void NewlyAddedApprovalsRequireAnApprovalOnlyLeadCommit(string subject, bool mixed, int rejected)
    {
        using var tree = new StaticTree();
        const string empty = "{\"format\":2,\"tasks\":[]}";
        tree.Write(CheckChangeApprovals.RelativePath, empty);
        var finding = new ReviewFinding("tests/A.cs", "test-removed", "A", "removed");
        CheckChangeApprovals.Record(tree.Root, "TASK-1", "2026-11-02", "Reviewed", [finding], null);
        string added = File.ReadAllText(Path.Combine(tree.Root, CheckChangeApprovals.RelativePath));
        CommandResult Git(string[] args)
        {
            string output = args[0] switch
            {
                "merge-base" => "base",
                "rev-list" => "approval",
                "diff-tree" => CheckChangeApprovals.RelativePath + (mixed ? "\nsrc/C.cs" : ""),
                "show" when args.Contains("--format=%s") => subject,
                "show" when args[1].StartsWith("approval:", StringComparison.Ordinal) || args[1].StartsWith("HEAD:", StringComparison.Ordinal) => added,
                _ => empty
            };
            return new(0, output, "");
        }
        var audit = CheckChangeApprovals.Audit(tree.Root, "base", [Change("old", "new")], Git);
        Assert.Single(audit.Added);
        Assert.Equal(rejected, audit.Unauthorized.Length);
    }

    [Fact]
    public void ProfileAwareRatchetRetainsReleaseOnlyBaselineDuringMerge()
    {
        using var tree = new StaticTree();
        const string source = "class C { void M() { Call(); } }";
        tree.Write("src/C.cs", source);
        var warning = AnalyzerWarnings.Describe(tree.Root, "src/C.cs", 1, 22, 1, 28, "CA2100", "existing", source)
            with { Reason = "previous review", Contexts = ["ordinary|C.csproj|net10.0|"] };
        var merge = new AnalyzerWarningCollection([], ["src/C.cs"],
            new() { ["src/C.cs"] = ["ordinary|C.csproj|net10.0|"] },
            new() { ["src/C.cs"] = ["merge"] });
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, merge, [warning], false));
        var release = merge with { Profiles = new() { ["src/C.cs"] = ["release"] } };
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, release, [warning], false), error => error.StartsWith("STALE ", StringComparison.Ordinal));
    }





    [Fact]
    public void UncommittedApprovalCannotApproveOnlyABaselineChange()
    {
        using var tree = new StaticTree();
        const string empty = "{\"format\":2,\"tasks\":[]}";
        tree.Write(CheckChangeApprovals.RelativePath, empty);
        CheckChangeApprovals.Record(tree.Root, "TASK-1", "2026-11-02", "Review pending",
            [new("build-tools/release/baselines/analyzer-warnings-baseline.json", "baseline-grown", "new", "added")], null);
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "base" : args[0] == "show" ? empty : "", "");
        var audit = CheckChangeApprovals.Audit(tree.Root, "base",
            [Change("{}", "{\"entries\":[{}]}", "build-tools/release/baselines/analyzer-warnings-baseline.json")], Git);
        Assert.Single(audit.Added);
        Assert.Single(audit.Unauthorized);
    }

    [Fact]
    public void PythonTestsCanMoveWithoutADeletion()
    {
        Assert.Empty(CheckLoosening.Scan([
            Change("def test_move():\n    assert True\n", "", "tests/test_before.py"),
            Change("", "def test_move():\n    assert True\n", "tests/test_after.py")]));
    }

    [Fact]
    public void FalseBranchCommentsDoNotHideItsEnd()
    {
        const string source = "class C {\n#if false // disabled helper\nvoid Helper() {}\n#endif // end\n[Fact] void Test() {}\n}";
        Assert.Empty(CheckLoosening.Scan([Change("class C { [Fact] void Test() {} }", source)]));
    }



    [Theory]
    [InlineData("merge")]
    [InlineData("release")]
    public void AnalyzerCollectionRetainsTheEmittingProfile(string profile)
    {
        using var tree = new StaticTree();
        string source = tree.Write("src/C.cs", "class C {}");
        tree.Write("reports/a.inputs", "context|" + Path.Combine(tree.Root, "C.csproj") + "|net10.0|NET|" + profile +
            "\n" + source + "|" + FileHash.Sha256(source) + "\n");
        tree.Write("reports/a.sarif", "{\"version\":\"2.1.0\",\"runs\":[{\"results\":[]}]}");
        var collected = AnalyzerWarnings.Collect(tree.Root, Path.Combine(tree.Root, "reports"));
        Assert.Equal(profile, Assert.Single(collected.Profiles!["src/C.cs"]));
    }

    [Fact]
    public void RemovingAnXunitTimeoutIsADeletedGate()
    {
        Assert.Contains(CheckLoosening.Scan([Change("class C { [Fact(Timeout=30)] void T() {} }",
            "class C { [Fact] void T() {} }")]), row => row.Kind == "gate-removed");
    }

    [Fact]
    public void ConditionalDefinitionRemovalApprovalsRemainDistinct()
    {
        var rows = CheckLoosening.Scan([Change("class C {\n#if FIRST\n[Fact] void T() {}\n#else\n[Fact] void T() {}\n#endif\n}", "class C {}")]);
        Assert.Equal(2, rows.Count(row => row.Kind == "test-removed"));
        Assert.Equal(2, rows.Select(row => row.Identifier).Distinct().Count());
    }

    [Fact]
    public void OrdinaryProductionCompileRemovalIsNotATestExclusion()
    {
        const string after = "<Project><ItemGroup><Compile Remove=\"Business.cs\" /></ItemGroup></Project>";
        Assert.Empty(CheckLoosening.Scan([Change("<Project />", after, "src/App.csproj")]));
        Assert.Contains(CheckLoosening.Scan([Change("<Project />", after, "tests/App.Tests.csproj")]), row => row.Kind == "test-excluded");
    }

    [Theory]
    [InlineData("FullyQualifiedName~A", "FullyQualifiedName~A|FullyQualifiedName~B")]
    [InlineData("FullyQualifiedName~A&Category=Fast", "FullyQualifiedName~A")]
    public void WideningRegisteredFiltersIsNotAWeakening(string before, string after)
    {
        var oldSuite = JsonSerializer.Serialize(new { one = new { arguments = new[] { "--filter", before } } });
        var newSuite = JsonSerializer.Serialize(new { one = new { arguments = new[] { "--filter", after } } });
        Assert.Empty(CheckLoosening.Scan([Change(oldSuite, newSuite, "tests/test-suites.json")]));
        Assert.Empty(CheckLoosening.Scan([Change("<RunSettings><RunConfiguration><TestCaseFilter>" + System.Security.SecurityElement.Escape(before) +
            "</TestCaseFilter></RunConfiguration></RunSettings>", "<RunSettings><RunConfiguration><TestCaseFilter>" +
            System.Security.SecurityElement.Escape(after) + "</TestCaseFilter></RunConfiguration></RunSettings>", "tests/settings.runsettings")]));
    }

    [Fact]
    public void LeadDraftReplacesUnauthorizedRowsInsteadOfTreatingThemAsApproved()
    {
        using var tree = new StaticTree();
        tree.Write(CheckChangeApprovals.RelativePath, "{\"format\":2,\"tasks\":[]}");
        var finding = new ReviewFinding("tests/A.cs", "test-removed", "A", "removed");
        CheckChangeApprovals.Record(tree.Root, "self", "2026-11-02", "untrusted", [finding], null);
        var unauthorized = CheckChangeApprovals.Load(tree.Root);
        CheckChangeApprovals.Record(tree.Root, "lead-review", "2026-11-03", "pending", [finding], null, unauthorized);
        Assert.Equal("lead-review", Assert.Single(CheckChangeApprovals.Load(tree.Root)).Task);
    }



    [Fact]
    public void ReleaseRuleInitializationAcceptsNewRuleAndRefusesExistingBaseline()
    {
        var rules = new HashSet<string> { "CA2100" };
        AnalyzerRuleInitialization.Validate(rules, "", "", [], []);
        Assert.Throws<ReleaseException>(() => AnalyzerRuleInitialization.Validate(rules,
            "dotnet_diagnostic.CA2100.severity = warning", "", [], []));
        using var tree = new StaticTree();
        const string source = "class C { void M() { Call(); } }";
        var warning = AnalyzerWarnings.Describe(tree.Root, "src/C.cs", 1, 22, 1, 28, "CA2100", "existing", source);
        Assert.Throws<ReleaseException>(() => AnalyzerRuleInitialization.Validate(rules, "", "", [warning], []));
    }

    [Fact]
    public void SecurityConfigurationContainsAllOneHundredRules()
    {
        var root = StaticCheckVectorTests.Root;
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/Shared/CodeAnalysis.props"));
        var release = props.Descendants("_TiaSecurityRule").SelectMany(item => item.Attribute("Include")!.Value.Split(';')).ToHashSet();
        Assert.Equal(38, release.Count);
        Assert.True(release.SetEquals(ReleaseRules.Rules));
        var config = File.ReadAllText(Path.Combine(root, ".editorconfig"));
        var merge = System.Text.RegularExpressions.Regex.Matches(config, @"dotnet_diagnostic\.(CA\d+)\.severity = warning")
            .Select(match => match.Groups[1].Value).ToHashSet();
        var expected = new[] { "CA2100", "CA3075", "CA3077" }
            .Concat(Enumerable.Range(2300, 31).Select(id => "CA" + id))
            .Concat(Enumerable.Range(3001, 12).Select(id => "CA" + id))
            .Concat(Enumerable.Range(5350, 54).Select(id => "CA" + id)).ToHashSet();
        Assert.Equal(100, expected.Count);
        Assert.True(expected.SetEquals(merge.Where(expected.Contains).Concat(release)));
        Assert.Empty(release.Intersect(merge));
        Assert.Contains("CustomAdditionalCompileInputs", File.ReadAllText(Path.Combine(root, "src/Shared/CodeAnalysis.props")));
    }

    [Theory]
    [InlineData("", "merge")]
    [InlineData("|release", "release")]
    public void FourFieldReceiptsDefaultToMerge(string suffix, string expected)
    {
        using var tree = new StaticTree();
        string source = tree.Write("src/C.cs", "class C {}");
        tree.Write("reports/a.inputs", "context|" + Path.Combine(tree.Root, "C.csproj") + "|net10.0|NET" + suffix +
            "\n" + source + "|" + FileHash.Sha256(source) + "\n");
        tree.Write("reports/a.sarif", """{"version":"2.1.0","runs":[{"results":[]}]}""");
        Assert.Equal(expected, Assert.Single(AnalyzerWarnings.Collect(tree.Root, Path.Combine(tree.Root, "reports")).Profiles!["src/C.cs"]));
    }

    [Theory]
    [InlineData(0, """{"version":1,"sources":[],"projects":[{"path":"app.csproj"}]}""", "Incomplete")]
    [InlineData(1, "error: Unable to load the service index for source", "Unable to load")]
    [InlineData(1, """{"problems":[{"level":"error","text":"restore failed"}]}""", "restore failed")]
    public void UnscannedReportsNeverPass(int exitCode, string output, string reason)
    {
        using var tree = new StaticTree();
        var result = VulnerablePackages.Check(tree.Root, (_, _, _) => new(exitCode, output, ""));
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not run:", result.StandardOutput);
        Assert.Contains(reason, result.StandardOutput);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PrepareRequiresScanUnlessBuildOnly(int exitCode)
    {
        using var tree = new StaticTree();
        tree.Write(CheckChangeApprovals.RelativePath, """{"format":2,"tasks":[]}""");
        const string report = """
            {"version":1,"sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"app.csproj","frameworks":[{"framework":"net48",
            "transitivePackages":[{"id":"Microsoft.Bcl.Memory","resolvedVersion":"9.0.5","vulnerabilities":[{"severity":"High",
            "advisoryurl":"https://github.com/advisories/GHSA-73j8-2gch-69rq"}]}]}]}]}
            """;
        int calls = 0;
        CommandResult Run(string[] args, string cwd, IDictionary<string, string?> environment)
        {
            calls++;
            return exitCode == 1 ? new(0, report, "") : new(1, "error: Unable to load the service index", "");
        }
        Prepare.RequireVulnerabilityScan(tree.Root, Options.Parse(["-BuildOnly"], Program.BooleanOptions), tree.Root, Run);
        Assert.Equal(0, calls);
        var error = Assert.Throws<ReleaseException>(() => Prepare.RequireVulnerabilityScan(tree.Root,
            Options.Parse([], Program.BooleanOptions), tree.Root, Run));
        Assert.Equal(exitCode, error.ExitCode);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(tree.Root, "vulnerable-packages.json")));
        if (exitCode == 2) Assert.Contains("Rerun prepare with network", error.Message);
    }

    [Fact]
    public void UnchangedChineseAssertionUsesUtf8GitBlobsAndPaths()
    {
        using var tree = new StaticTree();
        string Git(params string[] args)
        {
            var result = ReviewChangeSet.Git("git", args, tree.Root);
            ProcessRunner.RequireSuccess(result, "Synthetic git fixture");
            return result.StandardOutput.Trim();
        }
        try
        {
            Git("init");
            string path = "tests/中文Tests.cs";
            const string source = "class C { [Fact] void T() { Assert.Equal(\"中文断言\", text); } }";
            tree.Write(path, source);
            string blob = Git("hash-object", "-w", path);
            // Git octal quoting keeps the synthetic tree path independent of the host stdin code page.
            var folder = ProcessRunner
                .Run("git", ["mktree"], tree.Root, standardInput: "100644 blob " + blob + "\t\"\\344\\270\\255\\346\\226\\207Tests.cs\"\n");
            ProcessRunner.RequireSuccess(folder, "Synthetic tree");
            var top = ProcessRunner.Run("git", ["mktree"], tree.Root, standardInput: "040000 tree " + folder.StandardOutput.Trim() + "\ttests\n");
            ProcessRunner.RequireSuccess(top, "Synthetic root tree");
            string revision = Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit-tree", top.StandardOutput.Trim(), "-m", "fixture");
            Git("update-ref", "HEAD", revision);
            Git("read-tree", revision);
            tree.Write(path, "// changed comment\n" + source);
            var changes = ReviewChangeSet.Read(tree.Root, "HEAD");
            Assert.Equal(path, Assert.Single(changes).Path);
            Assert.Empty(CheckLoosening.Scan(changes));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(tree.Root, ".git"), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void SwitchingProfilesRecompilesAndKeepsPhysicalSourceSections()
    {
        using var tree = new StaticTree();
        foreach (var path in new[] { ".editorconfig", "src/Shared/CodeAnalysis.props", "Directory.Build.props",
            "src/Shared/DependencyVersions.props", "src/Shared/ReproducibleBuild.props", "Directory.Packages.props" }) tree.Copy(path);
        string config = tree.Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        tree.Write("src/C.cs", "public class C { }\n");
        string other = tree.Write("tests/Other.cs", "public class Other { }\n");
        string project = tree.Write("app/app.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><Compile Include="../src/C.cs" Link="C.cs"/><Compile Include="../tests/Other.cs" Link="Other.cs"/></ItemGroup></Project>
            """);
        var environment = new Dictionary<string, string?> { ["RestoreConfigFile"] = config, ["NuGetAudit"] = "false",
            ["UseSharedCompilation"] = "false", ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" };
        var times = new List<DateTime>();
        foreach (string profile in new[] { "merge", "release", "merge" })
        {
            ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", ["build", project, "-c", "Debug", "-m:1", "-nodeReuse:false",
                "-p:TiaSecurityAnalysisProfile=" + profile], tree.Root, environment), "Profile compilation fixture");
            string generated = File.ReadAllText(Path.Combine(tree.Root, "app/obj/Debug/net10.0/security-" + profile + ".globalconfig"));
            Assert.Contains("dotnet_diagnostic.CA2100.severity = none", generated);
            Assert.Equal(profile == "release", generated.Contains("[" + Path.Combine(tree.Root, "src/C.cs").Replace('\\', '/') + "]",
                StringComparison.Ordinal));
            Assert.DoesNotContain("[" + other.Replace('\\', '/') + "]", generated);
            times.Add(File.GetLastWriteTimeUtc(Path.Combine(tree.Root, "app/obj/Debug/net10.0/analyzer-warnings.sarif")));
        }
        Assert.True(times[1] > times[0]);
        Assert.True(times[2] > times[1]);
    }

    [Fact]
    public void InitializeReleaseRuleWritesOnlyReviewedNewRuleAndRefusesSecondInitialization()
    {
        using var tree = new StaticTree();
        try
        {
            string Git(params string[] args)
            {
                var result = ReviewChangeSet.Git("git", args, tree.Root);
                ProcessRunner.RequireSuccess(result, "Rule initialization git fixture");
                return result.StandardOutput.Trim();
            }
            Git("init");
            tree.Write(".editorconfig", "");
            string blob = Git("hash-object", "-w", ".editorconfig");
            var treeResult = ProcessRunner.Run("git", ["mktree"], tree.Root,
                standardInput: "100644 blob " + blob + "\t.editorconfig\n");
            ProcessRunner.RequireSuccess(treeResult, "Rule initialization tree");
            string revision = Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit-tree", treeResult.StandardOutput.Trim(), "-m", "fixture");
            Git("update-ref", "HEAD", revision);
            const string source = "class C { void M() { Call(); } }";
            string path = tree.Write("src/C.cs", source);
            var warning = AnalyzerWarnings.Describe(tree.Root, path, 1, 22, 1, 28, "CA2100", "fixture", source)
                with { Reason = "Reviewed fixture", Retirement = "When fixed", Contexts = ["ordinary|C.csproj|net10.0|NET"] };
            tree.Write("reports/a.inputs", "context|" + Path.Combine(tree.Root, "C.csproj") + "|net10.0|NET|release\n" +
                path + "|" + FileHash.Sha256(path) + "\n");
            tree.Write("reports/a.sarif", JsonSerializer.Serialize(new
            {
                version = "2.1.0", runs = new[] { new { results = new[] { new
                {
                    ruleId = "CA2100", level = "warning", message = new { text = "fixture" },
                    locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = path },
                        region = new { startLine = 1, startColumn = 22, endLine = 1, endColumn = 28 } } } }
                } } } }
            }));
            var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string baseline = tree.Write("baseline.json", JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, []), json));
            string review = tree.Write("review.json", JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, [warning]), json));
            var options = Options.Parse(["-InitializeRules", "CA2100", "-Base", "HEAD", "-ReviewData", review,
                "-Baseline", baseline, "-DiagnosticsDirectory", Path.Combine(tree.Root, "reports")], Program.BooleanOptions);
            Assert.Equal(0, AnalyzerWarnings.Run(tree.Root, options));
            Assert.Equal(warning.Fingerprint, Assert.Single(
                JsonSerializer.Deserialize<AnalyzerWarningBaseline>(File.ReadAllText(baseline), json)!.Entries).Fingerprint);
            Assert.Equal(64, Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Run(tree.Root, options)).ExitCode);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(tree.Root, ".git"), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
    }

}
