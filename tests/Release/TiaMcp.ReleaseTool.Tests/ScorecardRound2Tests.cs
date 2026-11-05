using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ScorecardRound2Tests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(StaticCheckVectorTests.Root,
        "tests/Release/TiaMcp.ReleaseTool.Tests/Vectors/Scorecard", name));

    private static ScorecardRun TwoRuns(StaticTree tree)
    {
        tree.Write("manifest/contracts/v4/baseline/21.json", Fixture("roster-21.json"));
        tree.Write("bin-build/vm-test/beta2/run.json", Fixture("beta2-run.json"));
        tree.Write("bin-build/vm-test/beta2/results.jsonl", Fixture("beta2-results.jsonl"));
        tree.Write("bin-build/vm-test/beta3/run.json", Fixture("beta3-run.json"));
        tree.Write("bin-build/vm-test/beta3/lead-tools.md", Fixture("beta3-lead-tools.md"));
        return new(Path.Combine(tree.Root, "bin-build/vm-test/beta2"), "21", "7e9c4c65", "4.0.0-beta.2", new(2026, 10, 31));
    }

    [Fact]
    public void ErrorQualityUsesNewestJsonlEvenWhenNewestRunOnlyHasTables()
    {
        using var tree = new StaticTree(); TwoRuns(tree);
        var context = new Scorecard(tree.Root, tree.Root, "sdk");
        ScorecardRuns.Collect(context);
        Assert.Equal(128, Row(context, "failed-rows").Raw);
        Assert.Equal(118d / 128, Row(context, "typed-error-share").Raw);
        Assert.Equal(51d / 128, Row(context, "native-message-share").Raw);
        Assert.Equal(13d / 128, Row(context, "outcome-unknown-share").Raw);
        Assert.Equal(3d / 128, Row(context, "server-log-share").Raw);
        Assert.Equal(27, Row(context, "recovery-rows").Raw);
        Assert.Contains("beta2", Row(context, "failed-rows").Source);
        Assert.Contains("beta3", Row(context, "tools-run-share").Source);
    }

    [Fact]
    public void ErrorQualityAcceptsNumericCodeFromTheRealRefusedRowWhenItFails()
    {
        using var tree = new StaticTree(); TwoRuns(tree);
        var record = Fixture("beta2-results.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).First(row => row["tool"]!.GetValue<string>() == "ManageUnifiedEvent" &&
                row["error"]?["code"] is JsonValue value && value.TryGetValue<int>(out int code) && code == -32602);
        Assert.Equal(-32602, record["error"]!["code"]!.GetValue<int>());
        record["outcome"] = "failed";
        tree.Write("bin-build/vm-test/beta2/results.jsonl", record.ToJsonString() + "\n");
        var context = new Scorecard(tree.Root, tree.Root, "sdk");
        ScorecardRuns.Collect(context);
        Assert.Equal(1, Row(context, "failed-rows").Raw);
        Assert.Equal(1, Row(context, "typed-error-share").Raw);
        Assert.Equal("measured", Row(context, "typed-error-share").State);
    }

    [Fact]
    public void FixtureMetadataUsesRepositoryRelativePaths()
    {
        Assert.DoesNotMatch(@"(?i)(?:file:///|[a-z]:[\\/])", Fixture("naming.sarif"));
        Assert.DoesNotMatch(@"(?i)(?:file:///|[a-z]:[\\/])", Fixture("origins.json"));
    }

    [Fact]
    public void VerifiedExamplesCountsToolsObjectAndAbsentFileAddsNoMissingMeasure()
    {
        using var tree = new StaticTree(); TwoRuns(tree);
        string path = tree.Write("bin-build/vm-test/beta3/verified-examples-v21.json", Fixture("verified-examples-v21.json"));
        var context = new Scorecard(tree.Root, tree.Root, "sdk"); ScorecardRuns.Collect(context);
        Assert.Equal(194, Row(context, "verified-examples").Raw);
        Assert.Equal("measured", Row(context, "verified-examples").State);
        File.Delete(path);
        context = new(tree.Root, tree.Root, "sdk"); ScorecardRuns.Collect(context);
        Assert.DoesNotContain(context.Report.Rows, row => row.Measure == "verified-examples");
    }

    [Fact]
    public void SampleCollectorExcludesDeletedRealSource()
    {
        using var tree = new StaticTree(); tree.Write("Version.props", Fixture("Version.props"));
        var paths = JsonNode.Parse(Fixture("sample-diff.json"))!;
        string[]? arguments = null;
        var context = new Scorecard(tree.Root, tree.Root, "sdk")
        {
            ReadGit = args =>
            {
                arguments = args;
                return string.Join('\0', paths[args.Contains("--diff-filter=d") ? "retained" : "paths"]!.AsArray()
                    .Select(node => node!.GetValue<string>()));
            }
        };
        string sample = Scorecard.WriteSample(context, Options.Parse(["-Base", "v4.0.0-beta.2"], Program.BooleanOptions));
        Assert.Contains("--diff-filter=d", arguments!);
        Assert.DoesNotContain("src/Engine/Siemens/Hmi/HmiSnapshot.cs", sample);
        Assert.Contains(paths["retained"]![0]!.GetValue<string>(), sample);
    }

    [Fact]
    public void ReviewsCollectorIncludesLowercaseSuffixesFromRealMasterSubjects()
    {
        using var tree = new StaticTree();
        string subjects = Fixture("review-subjects.txt");
        string[] tasks = subjects.Split('\n').Select(line => Regex.Match(line, @"\(([A-Z][A-Za-z0-9-]*?)(?: round \d+)?\)$"))
            .Where(match => match.Success).Select(match => Regex.Replace(match.Groups[1].Value, "-PORT$", "")).Distinct().ToArray();
        foreach (string task in tasks) tree.Write($"bin-build/refactor/{task}.20261104.result.md", "");
        var context = new Scorecard(tree.Root, tree.Root, "sdk") { ReadGit = _ => subjects };
        ScorecardSource.Reviews(context);
        Assert.Contains(context.Report.Rows, row => row.Measure == "handovers:P8-50d");
        Assert.Contains(context.Report.Rows, row => row.Measure == "handovers:UI-2a");
        Assert.Equal(tasks.Length, context.Report.Rows.Count(row => row.Measure.StartsWith("handovers:", StringComparison.Ordinal)));
        Assert.Equal(1, Row(context, "median-handovers", "").Raw);
    }

    [Theory]
    [InlineData(false, "partial")]
    [InlineData(true, "measured")]
    public void NamingCollectorReportsNarrowCoverageAsPartial(bool wide, string state)
    {
        using var tree = new StaticTree(); tree.Write("analyzers/naming.sarif", Fixture("naming.sarif"));
        var context = new Scorecard(tree.Root, tree.Root, "sdk") { RunDirectory = tree.Root };
        context.Timings.Add(new(DateTime.UnixEpoch, "c626c546", "refactor/SCORE-PORT", wide, wide, false,
            600, 0, [], new() { ["build"] = 500 }, tree.Root));
        ScorecardSource.Naming(context);
        Assert.Equal(18, Row(context, "naming-diagnostics", "").Raw);
        Assert.Equal(state, Row(context, "naming-diagnostics", "").State);
        Assert.Equal(state, Assert.Single(context.Report.Dimensions).State);
    }

    [Fact]
    public void EfficiencyCollectorExcludesDocsOnlyRunsFromCodeMedianAndMinimumSamples()
    {
        using var tree = new StaticTree(); string path = Path.Combine(tree.Root, "timings.jsonl");
        for (int index = 0; index < 5; index++)
        {
            MergeCheckRuns.Append(path, new(DateTime.UnixEpoch.AddMinutes(index), "c626c546", "code", false, false, false,
                600 + 50 * index, 0, [], new() { ["build"] = 500 }, tree.Root));
            MergeCheckRuns.Append(path, new(DateTime.UnixEpoch.AddMinutes(10 + index), "c626c546", "docs", false, false, true,
                1, 0, [], new() { ["check-repository"] = 1 }, tree.Root));
        }
        var context = new Scorecard(tree.Root, tree.Root, "sdk") { Timings = MergeCheckRuns.Read(path) };
        ScorecardEvidence.Efficiency(context);
        Assert.Equal(700, Row(context, "narrow-seconds", "").Raw);
        context = new(tree.Root, tree.Root, "sdk") { Timings = context.Timings.Skip(1).ToList() };
        ScorecardEvidence.Efficiency(context);
        Assert.Equal("not measured", Row(context, "narrow-seconds", "").State);
    }

    [Fact]
    public void SourceCollectorDoesNotTreatRealFlatElseIfChainAsDeepNesting()
    {
        using var tree = new StaticTree();
        tree.Write("src/RuntimeToolContract.cs", Fixture("RuntimeToolContract.Map.cs.txt"));
        tree.Write("fixture.csproj", Fixture("contracts.csproj.txt"));
        var context = new Scorecard(tree.Root, tree.Root, "sdk")
        {
            ReadGit = args => args[0] == "log" ? "src/RuntimeToolContract.cs\n"
                : args.Contains("*.csproj") ? "fixture.csproj\0" : "src/RuntimeToolContract.cs\0"
        };
        ScorecardSource.Collect(context, 4.25);
        Assert.Equal(0, Row(context, "deep-methods", "").Raw);
        Assert.Equal(1, Row(context, "long-methods", "").Raw);
        Assert.Equal(1, Row(context, "complex-methods", "").Raw);
        Assert.Equal(4.25, Row(context, "reviewed-sample", "").Raw);
        Assert.Equal(0, Row(context, "commented-code", "").Raw);
        Assert.Equal(0, Row(context, "duplicate-blocks", "").Raw);
        Assert.Single(context.Report.TypeIndices);
    }

    [Fact]
    public void DocumentationCollectorIncludesDocsRootReadme()
    {
        using var tree = new StaticTree(); var context = DocumentationContext(tree);
        ScorecardEvidence.Documentation(context, () => (2, []), () => 0);
        Assert.Contains("2 total", Row(context, "outdated-docs", "").Detail);
        Assert.Equal(0, Row(context, "repository-errors", "").Raw);
    }

    [Fact]
    public void FailedDeadReferenceResultCannotReceiveAPassingScore()
    {
        using var tree = new StaticTree(); var context = DocumentationContext(tree);
        ScorecardEvidence.Documentation(context, () => (2, []), () => 1);
        Assert.Equal(1, Row(context, "dead-references", "").Raw);
        Assert.Equal(0, Row(context, "dead-references", "").Score);
        Assert.Equal(0, Assert.Single(context.Report.Dimensions).Score);
    }

    private static Scorecard DocumentationContext(StaticTree tree)
    {
        tree.Write("docs/README.md", Fixture("docs-readme.md.txt"));
        tree.Write("docs/standards/README.md", Fixture("standards-readme.md.txt"));
        return new(tree.Root, tree.Root, "sdk")
        {
            ReadGit = args => args.Contains(":(glob)docs/**/*.md")
                ? "docs/README.md\0docs/standards/README.md\0" : "docs/standards/README.md\0"
        };
    }

    private static ScorecardRow Row(Scorecard context, string measure, string release = "21") =>
        Assert.Single(context.Report.Rows, row => row.Measure == measure && row.Release == release);
}
