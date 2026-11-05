using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed partial class ScorecardTests
{
    public static IEnumerable<object[]> Bands() => ScorecardBands.All.Select(band => new object[] { band.Measure });

    [Theory]
    [MemberData(nameof(Bands))]
    public void EveryBandHasInclusiveEdgesAndSixScores(string measure)
    {
        var band = ScorecardBands.Find(measure)!;
        Assert.Equal(5, band.Edges.Length);
        for (int index = 0; index < band.Edges.Length; index++)
        {
            double edge = band.Edges[index];
            Assert.Equal(band.HigherIsBetter ? index + 1 : 5 - index, band.Score(edge));
            Assert.Equal(band.HigherIsBetter ? index : 5 - index, band.Score(edge - .000001));
            Assert.Equal(band.HigherIsBetter ? index + 1 : 4 - index, band.Score(edge + .000001));
        }
    }

    [Theory]
    [InlineData("Readability", "long-methods")]
    [InlineData("Comments", "missing-public-docs")]
    [InlineData("Maintainability", "maintainability-index")]
    [InlineData("Tests", "suite-coverage")]
    [InlineData("Verification efficiency", "narrow-seconds")]
    [InlineData("Delivery quality", "delivery-pass-share")]
    [InlineData("Review", "median-handovers")]
    [InlineData("Security", "high-critical")]
    [InlineData("Documentation", "repository-errors")]
    [InlineData("Write safety", "dry-run-share")]
    [InlineData("Official API coverage", "official-api-share")]
    [InlineData("Real-machine coverage", "tools-run-share")]
    [InlineData("Defect escape and regression", "regressions")]
    [InlineData("Error quality", "typed-error-share")]
    public void DimensionsUseLowestScoreAndKeepMissingMeasuresPartial(string dimension, string measure)
    {
        var report = new ScorecardReport();
        var band = ScorecardBands.Find(measure)!;
        report.Add(dimension, measure, "21", band.Edges[2], "fixture");
        report.Add(dimension, measure, "21", band.Edges[4], "fixture");
        report.Missing(dimension, "absent", "21", "missing.json", "File missing");
        var row = Assert.Single(report.Dimensions);
        Assert.Equal("partial", row.State);
        Assert.Equal(Math.Min(band.Score(band.Edges[2]), band.Score(band.Edges[4])), row.Score);
        Assert.Contains("proposed", report.Render());
        Assert.Contains("missing.json", report.Render());
    }

    [Fact]
    public void DeltaMatchesDimensionMeasureAndReleaseAndRetainsNewRows()
    {
        var old = new ScorecardReport();
        old.Add("Delivery quality", "delivery-pass-share", "21", .7, "before", unit: "share");
        old.Add("Performance trend", "http", "21", 20, "before", unit: "ms");
        var current = new ScorecardReport();
        current.Add("Delivery quality", "delivery-pass-share", "21", .9, "after", unit: "share");
        current.Add("Delivery quality", "delivery-pass-share", "20", .7, "after", unit: "share");
        current.Add("Performance trend", "http", "21", 15, "after", unit: "ms");
        current.Compare(old);
        Assert.Equal("+0.2; score +2", current.Rows[0].Change);
        Assert.Equal("new", current.Rows[1].Change);
        Assert.Equal("-5; faster", current.Rows[2].Change);
        Assert.Null(current.Rows[2].Score);
        var copy = JsonSerializer.Deserialize<ScorecardReport>(JsonSerializer.Serialize(current, ScorecardReport.Json), ScorecardReport.Json)!;
        Assert.Equal(current.Rows, copy.Rows);
    }

    [Fact]
    public void RawCountsDoNotImplyThatAnUnavailableDimensionWasMeasured()
    {
        var report = new ScorecardReport();
        report.Add("Error quality", "failed-rows", "21", 0, "results.jsonl");
        report.Missing("Error quality", "typed-error-share", "21", "results.jsonl", "No failures to assess");
        var dimension = Assert.Single(report.Dimensions);
        Assert.Equal("not measured", dimension.State); Assert.Null(dimension.Score);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("CallTool", "Wrapped")]
    [InlineData("CallTool:Wrapped", "Wrapped")]
    public void JsonlResolvesDirectAndWrapperToolNames(string tool, string expected)
    {
        var row = new JsonObject { ["tool"] = tool, ["arguments"] = new JsonObject { ["name"] = "Wrapped" } };
        Assert.Equal(expected, ScorecardRuns.Tool(row));
    }

    [Theory]
    [InlineData("", "not-run")]
    [InlineData("passed", "pass")]
    [InlineData("failed", "defect")]
    [InlineData("refused-as-designed", "refused-by-design")]
    [InlineData("passed,failed", "uncertain")]
    [InlineData("failed,passed", "pass")]
    [InlineData("passed,failed,refused-as-designed", "uncertain")]
    [InlineData("failed,refused-as-designed", "defect")]
    public void JsonlClassifiesExecutedHistory(string history, string expected)
    {
        Assert.Equal(expected, ScorecardRuns.Classify(history.Split(',', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void JsonlDropsUnknownToolsAndCountsErrorEnvelopesAndRecovery()
    {
        string Row(string tool, string outcome, JsonObject? error = null) => new JsonObject
        {
            ["tool"] = tool, ["outcome"] = outcome, ["error"] = error
        }.ToJsonString();
        var result = ScorecardRuns.Envelopes([
            Row("Known", "failed", new() { ["code"] = "OUTCOME_UNKNOWN", ["message"] = "See server log",
                ["details"] = new JsonObject { ["nativeMessage"] = "Native diagnostic" } }),
            Row("Known", "failed", new() { ["code"] = "INTERNAL_ERROR" }),
            Row("Known", "failed", new() { ["code"] = null }),
            Row("Known", "passed"), Row("Known", "skipped"),
            Row("Unknown", "failed", new() { ["code"] = "OUTCOME_UNKNOWN" }), Row("(recovery)", "passed")
        ], new HashSet<string> { "Known", "Untested" });
        Assert.Equal("pass", result.Tools["Known"]);
        Assert.Equal("not-run", result.Tools["Untested"]);
        Assert.Equal(3, result.Failed); Assert.Equal(1, result.Typed); Assert.Equal(1, result.Native);
        Assert.Equal(1, result.Unknown); Assert.Equal(1, result.ServerLog); Assert.Equal(1, result.Recovery);
        Assert.True(result.Envelopes);
    }

    [Fact]
    public void SerializedEnvelopesCountNativeFieldPresenceEvenWhenNull()
    {
        var envelope = new JsonObject
        {
            ["schemaVersion"] = 4, ["ok"] = false, ["data"] = null,
            ["error"] = new JsonObject { ["code"] = "NATIVE_ERROR", ["message"] = "See server log",
                ["details"] = new JsonObject { ["nativeCode"] = null, ["nativeMessage"] = null } },
            ["meta"] = new JsonObject { ["releaseKey"] = "21" }
        };
        var row = new JsonObject
        {
            ["utc"] = "2026-11-01T00:00:00Z", ["release"] = "21", ["tool"] = "Known", ["outcome"] = "failed",
            ["error"] = new JsonObject { ["code"] = "OUTCOME_UNKNOWN", ["message"] = "Flattened message" },
            ["envelope"] = envelope.ToJsonString()
        };
        var result = ScorecardRuns.Envelopes([row.ToJsonString()], new HashSet<string> { "Known" });
        Assert.Equal(1, result.Typed); Assert.Equal(1, result.Unknown); Assert.Equal(1, result.Native);
        Assert.Equal(1, result.ServerLog); Assert.Null(result.ErrorProblem);
        row["error"] = null; row["envelope"] = envelope;
        result = ScorecardRuns.Envelopes([row.ToJsonString()], new HashSet<string> { "Known" });
        Assert.Equal(0, result.Typed); Assert.Equal(0, result.Unknown); Assert.Equal(1, result.Native);
        row["envelope"] = "invalid json";
        result = ScorecardRuns.Envelopes([row.ToJsonString()], new HashSet<string> { "Known" });
        Assert.Equal("defect", result.Tools["Known"]); Assert.NotNull(result.ErrorProblem);
    }

    [Fact]
    public void RecoveryFailuresAndRetainedJsonPrefixesContributeToErrorEvidence()
    {
        string prefix = "{\"ok\":false,\"error\":{\"code\":\"NATIVE_ERROR\",\"details\":{\"nativeMessage\":null},\"message\":\"cut";
        var row = new JsonObject { ["tool"] = "Known", ["outcome"] = "failed", ["envelope"] = prefix,
            ["error"] = new JsonObject { ["code"] = "NATIVE_ERROR" } };
        var recovery = new JsonObject { ["tool"] = "(recovery)", ["outcome"] = "failed" };
        var result = ScorecardRuns.Envelopes([row.ToJsonString(), recovery.ToJsonString()], new HashSet<string> { "Known" });
        Assert.Equal(2, result.Failed); Assert.Equal(1, result.Typed); Assert.Equal(1, result.Native);
        Assert.Equal(1, result.Recovery); Assert.NotNull(result.ErrorProblem);
        Assert.Single(result.Tools); Assert.Equal("defect", result.Tools["Known"]);
    }

    [Theory]
    [InlineData("PASS native", "pass")]
    [InlineData("BUG-42", "defect")]
    [InlineData("EXPECTED-REFUSAL", "refused-by-design")]
    [InlineData("EXPECTED REFUSAL reason", "refused-by-design")]
    [InlineData("INCONCLUSIVE", "uncertain")]
    [InlineData("NOT RUN", "not-run")]
    [InlineData("DEFERRED", "not-run")]
    [InlineData("SKIP", "not-run")]
    [InlineData("LEAD", "not-run")]
    [InlineData("Other", "not-run")]
    public void TablesHonorEveryStatusPrefix(string status, string expected)
    {
        var result = ScorecardRuns.Tables([$"| `Known` (note) | {status} |"], new HashSet<string> { "Known" });
        Assert.Equal(expected, result["Known"]);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("example")]
    [InlineData("bugs")]
    [InlineData("session reset")]
    [InlineData("side effect")]
    [InlineData("general")]
    [InlineData("cross-cutting")]
    [InlineData("not run")]
    [InlineData("deferred")]
    [InlineData("untested")]
    [InlineData("counts")]
    [InlineData("status")]
    public void TablesSkipSectionsIncludingSubheadingsAndResumeAtSibling(string heading)
    {
        string text = $"## {heading.ToUpperInvariant()}\n| Known | BUG |\n### Nested\n| Known | BUG |\n## Tools\n| Known | PASS |";
        Assert.Equal("pass", ScorecardRuns.Tables([text], new HashSet<string> { "Known" })["Known"]);
    }

    [Fact]
    public void TablesKeepDefectsAndLastExecutedRowAcrossFiles()
    {
        var result = ScorecardRuns.Tables([
            "| **Alpha**, `Beta` (note) | PASS |\n| Gamma | BUG |\n| Outside | BUG |",
            "| Alpha | NOT RUN |\n| Beta | INCONCLUSIVE |\n| Gamma | PASS |"
        ], new HashSet<string> { "Alpha", "Beta", "Gamma" });
        Assert.Equal("pass", result["Alpha"]);
        Assert.Equal("uncertain", result["Beta"]);
        Assert.Equal("defect", result["Gamma"]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void RunDiscoveryIgnoresMissingMetadataAndSortsByRunDate()
    {
        using var tree = new StaticTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "ignored"));
        tree.Write("first/run.json", RunMetadata("21", "2026-10-01"));
        tree.Write("second/run.json", RunMetadata("21", "2026-11-01"));
        var notes = new List<string>();
        var runs = ScorecardRuns.Discover(tree.Root, notes);
        Assert.Equal(2, runs.Count); Assert.EndsWith("second", runs[0].Directory);
        Assert.Contains(notes, note => note.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void V21OnlyEvidenceNeverBecomesAProjectScore()
    {
        using var tree = new StaticTree();
        WriteRosters(tree);
        tree.Write("bin-build/vm-test/new/run.json", RunMetadata("21", "2026-11-02"));
        tree.Write("bin-build/vm-test/new/results.jsonl", "{\"tool\":\"Known\",\"outcome\":\"passed\"}\n");
        var context = new Scorecard(tree.Root, tree.Root, "missing-sdk");
        ScorecardRuns.Collect(context);
        var dimensions = context.Report.Dimensions.Where(row => row.Dimension == "Delivery quality").ToArray();
        Assert.Equal(8, dimensions.Length);
        Assert.Single(dimensions, row => row.Score != null);
        Assert.Equal(7, dimensions.Count(row => row.State == "no evidence"));
        Assert.DoesNotContain(dimensions, row => row.Release.Length == 0);
        Assert.DoesNotContain("\"total\"", JsonSerializer.Serialize(context.Report, ScorecardReport.Json));
    }

    [Fact]
    public void TwoRunsCountTransitionsAndPreferJsonlOverTables()
    {
        using var tree = new StaticTree(); WriteRosters(tree, "Known", "Other");
        tree.Write("bin-build/vm-test/old/run.json", RunMetadata("21", "2026-10-01"));
        tree.Write("bin-build/vm-test/new/run.json", RunMetadata("21", "2026-11-01"));
        tree.Write("bin-build/vm-test/old/results.jsonl",
            "{\"tool\":\"Known\",\"outcome\":\"passed\"}\n{\"tool\":\"Other\",\"outcome\":\"failed\"}\n");
        tree.Write("bin-build/vm-test/new/results.jsonl",
            "{\"tool\":\"Known\",\"outcome\":\"failed\"}\n{\"tool\":\"Other\",\"outcome\":\"passed\"}\n");
        tree.Write("bin-build/vm-test/new/lead-tools.md", "| Known | PASS |");
        var context = new Scorecard(tree.Root, tree.Root, "missing-sdk"); ScorecardRuns.Collect(context);
        Assert.Equal(1, Row(context, "regressions", "21").Raw);
        Assert.Equal(1, Row(context, "fixed", "21").Raw);
        Assert.Equal(1, Row(context, "delivery-defects", "21").Raw);
    }

    [Fact]
    public void TableOnlyRunsDoNotInventErrorEnvelopes()
    {
        using var tree = new StaticTree(); WriteRosters(tree);
        tree.Write("bin-build/vm-test/run/run.json", RunMetadata("21", "2026-11-01"));
        tree.Write("bin-build/vm-test/run/slice-1.md", "| Known | PASS |");
        tree.Write("bin-build/vm-test/run/lead-probe-results.md", "| Known | INCONCLUSIVE |");
        tree.Write("bin-build/vm-test/run/lead-tools.md", "| Known | BUG |");
        var context = new Scorecard(tree.Root, tree.Root, "missing-sdk"); ScorecardRuns.Collect(context);
        Assert.Equal("not measured", Row(context, "envelopes", "21").State);
        Assert.Equal(1, Row(context, "delivery-defects", "21").Raw);
    }

    [Fact]
    public void OlderRowsAreStaleAndUnscoredWithCountsAndDates()
    {
        using var tree = new StaticTree();
        tree.Write("bin-build/vm-acceptance/4.0/old.json", """
            {"release":"20","date":"2026-10-06","rows":[{"tool":"Known","seconds":1},{"tool":"Known","seconds":2},
              {"tool":"Other","seconds":3,"utc":"2026-10-10"}]}
            """);
        var context = new Scorecard(tree.Root, tree.Root, "missing-sdk"); ScorecardRuns.Collect(context);
        var row = Row(context, "run-evidence", "20", "Real-machine coverage");
        Assert.Equal("stale", row.State); Assert.Null(row.Score);
        Assert.Contains("3 rows, 2 tools", row.Detail); Assert.Contains("2026-10-06–2026-10-10", row.Detail);
        Assert.Contains("4.0 dev builds", row.Detail);
    }

    [Fact]
    public void OlderArrayAndSummaryShapesUseReleaseFromFilenameOrSummary()
    {
        using var tree = new StaticTree();
        string path = tree.Write("extra-15.1.json", "[{\"tool\":\"Known\",\"seconds\":1},{\"tool\":\"Other\",\"seconds\":2}]");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        tree.Write("engine-sweep.json", """
            {"summary":{"release":"20","total":1},"rows":[{"tool":"Known","seconds":1,"utc":"2026-10-09"}]}
            """);
        tree.Write("v14-final.json", "[{\"tool\":\"Known\",\"seconds\":1}]");
        var stale = ScorecardRuns.Stale(tree.Root);
        Assert.Equal(2, stale["15.1"].Rows); Assert.Equal(2, stale["15.1"].Tools);
        Assert.Equal(new DateTime(2026, 10, 6), stale["15.1"].First.Date);
        Assert.Equal(1, stale["20"].Rows);
        Assert.Equal(1, stale["14sp1"].Rows);
    }

    [Fact]
    public void WriteSafetyJoinsRosterToOperationsAndUsesPerReleaseApproval()
    {
        using var tree = new StaticTree();
        tree.Write("manifest/tools-list.json", """
            {"tools":[{"name":"Write","operation":"WRITE"},{"name":"Online","operation":"ONLINE-WRITE"},
              {"name":"Read","operation":"READ"}]}
            """);
        foreach (string key in CatalogJson.Keys) tree.Write($"manifest/contracts/v4/baseline/{key}.json", """
            {"tools":[{"name":"Write","inputSchema":{"properties":{"dryRun":{"default":true},"expectedProjectFile":{}}}},
              {"name":"Online","inputSchema":{"properties":{"dryRun":{"default":false}}}},
              {"name":"Read","inputSchema":{"properties":{"dryRun":{"default":true}}}}]}
            """);
        var releases = new JsonObject();
        foreach (string key in CatalogJson.Keys.Take(6)) releases[key] = new JsonObject { ["checksPassed"] = 5 };
        tree.Write("bin-build/prepared/package/logs/foundation-approval/result.json", new JsonObject
        {
            ["status"] = "passed", ["checksExpected"] = 30, ["releases"] = releases
        }.ToJsonString());
        foreach (string key in new[] { "20", "21" }) tree.Write($"bin-build/prepared/package/logs/approval-v{key}-bundle/result.json",
            "{\"status\":\"passed\",\"checksPassed\":7,\"checksExpected\":7}");
        var context = new Scorecard(tree.Root, tree.Root, "sdk"); ScorecardEvidence.WriteSafety(context);
        Assert.Equal(.5, Row(context, "dry-run-share", "21").Raw);
        Assert.Equal(.5, Row(context, "project-identity-share", "21").Raw);
        Assert.Equal(1, Row(context, "approval-checks", "14sp1").Raw);
        Assert.Equal(1, Row(context, "approval-checks", "21").Raw);
        Assert.All(context.Report.Rows.Where(row => row.Measure == "rollback"), row => Assert.Null(row.Score));
    }

    [Fact]
    public void SecurityRequiresInitializationAndCompletedScan()
    {
        using var tree = new StaticTree();
        tree.Write(AnalyzerWarnings.BaselinePath, "{\"entries\":[{\"rule\":\"CA2100\"}]}");
        tree.Write("bin-build/prepared/package/logs/vulnerable-packages.json", "{\"status\":\"not run\",\"findings\":[]}");
        var context = new Scorecard(tree.Root, tree.Root, "sdk"); ScorecardEvidence.Security(context);
        Assert.All(context.Report.Rows, row => Assert.Equal("not measured", row.State));
        tree.Write(AnalyzerWarnings.BaselinePath,
            "{\"initializedRules\":[\"CA2100\"],\"entries\":[{\"rule\":\"CA2100\"},{\"rule\":\"CA1506\"}]}");
        tree.Write("bin-build/prepared/package/logs/vulnerable-packages.json", """
            {"status":"failed","findings":[{"kind":"vulnerability-high"},{"kind":"vulnerability-critical"},
             {"kind":"vulnerability-low"},{"kind":"vulnerability-moderate"}]}
            """);
        context = new(tree.Root, tree.Root, "sdk"); ScorecardEvidence.Security(context);
        Assert.Equal(1, Row(context, "security-warnings").Raw);
        Assert.Equal(2, Row(context, "high-critical").Raw); Assert.Equal(2, Row(context, "low-moderate").Raw);
    }

    [Theory]
    [InlineData("CA2100", true)]
    [InlineData("CA2300", true)]
    [InlineData("CA2330", true)]
    [InlineData("CA3001", true)]
    [InlineData("CA3012", true)]
    [InlineData("CA3075", true)]
    [InlineData("CA3077", true)]
    [InlineData("CA5350", true)]
    [InlineData("CA5403", true)]
    [InlineData("CA2331", false)]
    [InlineData("CA1506", false)]
    public void SecurityRuleRangeIsExplicit(string rule, bool expected) => Assert.Equal(expected, ScorecardEvidence.SecurityRule(rule));

    [Fact]
    public void TimingRecordAppendsFakeRunsAndUnwritableDestinationCannotChangeExit()
    {
        using var tree = new StaticTree();
        var run = new MergeCheckRun(new DateTime(2026, 11, 1), "abc", "feature", true, true, false,
            15.5, 1, ["failed-job"], new() { ["build"] = 3.5 }, tree.Root);
        string path = Path.Combine(tree.Root, "timings.jsonl");
        MergeCheckRuns.Append(path, run); MergeCheckRuns.Append(path, run with { Exit = 0 });
        var records = MergeCheckRuns.Read(path);
        Assert.Equal(2, records.Count); Assert.Equal(1, records[0].Exit); Assert.Equal(3.5, records[0].Jobs["build"]);
        string blocker = tree.Write("blocker", "file, not directory");
        var error = Record.Exception(() => MergeCheckRuns.Append(Path.Combine(blocker, "run.jsonl"), run));
        Assert.Null(error); Assert.Equal(1, run.Exit); Assert.Equal("file, not directory", File.ReadAllText(blocker));
    }

    [Fact]
    public void MergeTimingAppendUsesInjectedGitAndCapturesAllJobResults()
    {
        using var tree = new StaticTree();
        CommandResult Git(string[] args) => new(0, args.Contains("--git-common-dir") ? Path.Combine(tree.Root, ".git")
            : args[0] == "branch" ? "feature" : "abc", "");
        var plan = new MergeCheck.Plan { Wide = true, DocsOnly = false };
        MergeCheckRuns.Append(tree.Root, Options.Parse(["-All"], Program.BooleanOptions), plan,
            [new("pass", 1.25, new(0, "", "")), new("fail", 2, new(7, "", ""))], 3.5, tree.Root, Git);
        var row = Assert.Single(MergeCheckRuns.Read(Path.Combine(tree.Root, "bin-build/scorecard/merge-check-runs.jsonl")));
        Assert.True(row.All); Assert.True(row.Wide); Assert.False(row.DocsOnly);
        Assert.Equal("abc", row.Commit); Assert.Equal("feature", row.Branch); Assert.Equal(1, row.Exit);
        Assert.Equal(new[] { "fail" }, row.Failed); Assert.Equal(3.5, row.Seconds); Assert.Equal(2, row.Jobs["fail"]);
    }

    [Fact]
    public void EfficiencySeparatesWideAndNarrowAndDoesNotScoreFourRuns()
    {
        using var tree = new StaticTree(); var context = new Scorecard(tree.Root, tree.Root, "sdk");
        for (int index = 0; index < 10; index++) context.Timings.Add(new(new DateTime(2026, 11, 1).AddMinutes(index),
            "sha", "branch-" + index / 2, false, index >= 5, false, index >= 5 ? 900 : 600, index % 2,
            [], [], tree.Root));
        ScorecardEvidence.Efficiency(context);
        Assert.Equal(600, Row(context, "narrow-seconds").Raw); Assert.Equal(900, Row(context, "wide-seconds").Raw);
        Assert.Equal(1, Row(context, "first-pass-rate").Raw);
        context = new(tree.Root, tree.Root, "sdk") { Timings = context.Timings.Take(4).ToList() };
        ScorecardEvidence.Efficiency(context); Assert.All(context.Report.Rows, row => Assert.Null(row.Score));
    }

    [Fact]
    public void PerformanceReadsRealLatencyShapeAndStaysUnscored()
    {
        using var tree = new StaticTree();
        tree.Write("transport-plc/call-latency.json", """
            {"schemaVersion":1,"producer":"transport","profile":"full","gated":false,"errors":[],
              "samples":{"transport/21/http/full":{"calls":42,"medianMilliseconds":12.5}}}
            """);
        var context = new Scorecard(tree.Root, tree.Root, "sdk") { RunDirectory = tree.Root };
        ScorecardEvidence.Performance(context);
        Assert.Equal(16, context.Report.Rows.Count); Assert.Equal(12.5, Row(context, "http", "21").Raw);
        Assert.All(context.Report.Rows, row => Assert.Null(row.Score));
    }

    [Fact]
    public void TrxTotalsAvoidDerivedSubsetsAndStreamReleaseNames()
    {
        using var tree = new StaticTree();
        tree.Write("tests/fixture.csproj", "<Project/>");
        JsonObject Suite(int minimum) => new()
        {
            ["project"] = "tests/fixture.csproj", ["arguments"] = new JsonArray(),
            ["minimumPassed"] = minimum, ["maximumSkipped"] = 0
        };
        tree.Write("tests/test-suites.json", new JsonObject
        {
            ["foundation-api"] = Suite(1), ["foundation"] = Suite(1), ["independent"] = Suite(1)
        }.ToJsonString());
        string path = Path.Combine(tree.Root, "suites/foundation-api/foundation-api.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DotnetSuiteSelfTests.SyntheticTrx(path, ["Passed", "Failed"]);
        var document = XDocument.Load(path);
        foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            element.SetAttributeValue("testName", "Check(release: \"21\")");
        document.Save(path);
        string subset = Path.Combine(tree.Root, "suites/foundation/foundation.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(subset)!);
        DotnetSuiteSelfTests.SyntheticTrx(subset, ["Passed"]);
        var context = new Scorecard(tree.Root, tree.Root, "sdk") { RunDirectory = tree.Root };
        ScorecardEvidence.Tests(context);
        Assert.Equal(2d / 3, Row(context, "suite-coverage").Raw);
        Assert.Equal(1, Row(context, "passed-tests").Raw); Assert.Equal(1, Row(context, "failed-tests").Raw);
        Assert.Equal(2, Row(context, "release-results", "21").Raw);
        Assert.Equal(0, Row(context, "minimum-margin").Raw);
        Assert.Equal("partial", Assert.Single(context.Report.Dimensions, row => row.Release == "").State);
    }

    [Fact]
    public void AdapterSearchPrefersWorktreeThenPrimaryThenNewestPrepared()
    {
        using var tree = new StaticTree();
        string prepared = tree.Write("primary/bin-build/prepared/candidate/TIA_MCP_Delivery_test/runtime/v21/worker/TiaMcp.Adapter.21.dll", "");
        string primary = tree.Write("primary/src/PlcWorker/bin/21/Release/net48/TiaMcp.Adapter.21.dll", "");
        string current = tree.Write("worktree/src/PlcWorker/bin/21/Release/net48/TiaMcp.Adapter.21.dll", "");
        string root = Path.Combine(tree.Root, "worktree"), main = Path.Combine(tree.Root, "primary");
        Assert.Equal(Path.GetFullPath(current), Path.GetFullPath(ScorecardCoverage.Adapter(root, main, "21")!));
        File.Delete(current); Assert.Equal(Path.GetFullPath(primary), Path.GetFullPath(ScorecardCoverage.Adapter(root, main, "21")!));
        File.Delete(primary); Assert.Equal(Path.GetFullPath(prepared), Path.GetFullPath(ScorecardCoverage.Adapter(root, main, "21")!));
    }

    [Fact]
    public void MissingSourcesNeverTurnIntoZeroScores()
    {
        using var tree = new StaticTree(); var context = new Scorecard(tree.Root, tree.Root, "missing-sdk");
        ScorecardCoverage.Collect(context); ScorecardRuns.Collect(context); ScorecardEvidence.WriteSafety(context);
        ScorecardEvidence.Tests(context); ScorecardEvidence.Security(context); ScorecardEvidence.Documentation(context);
        ScorecardEvidence.Performance(context); ScorecardEvidence.Efficiency(context);
        Assert.NotEmpty(context.Report.Rows); Assert.All(context.Report.Rows.Where(row => row.State == "not measured"), row => Assert.Null(row.Score));
        Assert.Equal(8, context.Report.Rows.Count(row => row.Dimension == "Official API coverage"));
        Assert.Equal(32, context.Report.Rows.Count(row => row.Measure == "run-evidence"));
        Assert.Contains(context.Report.Rows, row => row.Dimension == "Documentation" && row.State == "not measured");
    }

    private static string RunMetadata(string release, string date) => new JsonObject
    {
        ["release"] = release, ["date"] = date, ["commit"] = "fixture-commit", ["package"] = "fixture-package"
    }.ToJsonString();

    private static void WriteRosters(StaticTree tree, params string[] names)
    {
        if (names.Length == 0) names = ["Known"];
        foreach (string key in CatalogJson.Keys) tree.Write($"manifest/contracts/v4/baseline/{key}.json", new JsonObject
        {
            ["tools"] = new JsonArray(names.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray())
        }.ToJsonString());
    }

    private static ScorecardRow Row(Scorecard context, string measure, string release = "", string? dimension = null) =>
        Assert.Single(context.Report.Rows, row => row.Measure == measure && row.Release == release &&
            (dimension == null || row.Dimension == dimension));
}
