using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class TrendNoticeTests
{
    [Fact]
    public void FailedJobsDoNotContaminateTrendHistory()
    {
        using var tree = new StaticTree();
        string output = Path.Combine(tree.Root, "bin-build/merge-check");
        Directory.CreateDirectory(output);
        CommandResult Git(string[] args) => new(0,
            args.Contains("HEAD") ? "revision" : Path.Combine(tree.Root, ".git"), "");
        for (int run = 0; run < 5; run++)
            Assert.Equal(0, MergeCheck.MergeTrends(tree.Root, output, Git,
                [new("build", 200, new(0, "", ""))]).ExitCode);
        for (int run = 0; run < 6; run++)
            Assert.Equal(0, MergeCheck.MergeTrends(tree.Root, output, Git,
                [new("build", 3, new(1, "", "failed"))]).ExitCode);

        var result = MergeCheck.MergeTrends(tree.Root, output, Git,
            [new("build", 200, new(0, "", ""))]);
        Assert.DoesNotContain("Slower than usual", result.StandardOutput);
        var history = File.ReadAllLines(Path.Combine(tree.Root, "bin-build/trend.jsonl"));
        Assert.Equal(6, history.Length);
        Assert.All(history, row => Assert.Equal(200, (double)JsonNode.Parse(row)!["value"]!));
    }

    [Fact]
    public void NoticeRecordsRevisionAndUtcTime()
    {
        using var tree = new StaticTree();
        string output = Path.Combine(tree.Root, "bin-build/merge-check");
        Directory.CreateDirectory(output);
        const string head = "0123456789012345678901234567890123456789";
        var before = DateTimeOffset.UtcNow;
        var result = MergeCheck.MergeTrends(tree.Root, output,
            args => new(0, args.Contains("HEAD") ? head : Path.Combine(tree.Root, ".git"), ""), []);
        Assert.Equal(0, result.ExitCode);
        string header = File.ReadLines(Path.Combine(output, "trend-notice.txt")).First();
        string prefix = "Trend notice from commit " + head + " at ";
        Assert.StartsWith(prefix, header);
        Assert.True(DateTimeOffset.TryParse(header[prefix.Length..], out var utc));
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.InRange(utc, before, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrepareIdentifiesTheNoticeCommitAgainstCurrentHead(bool current)
    {
        using var tree = new StaticTree();
        var git = ProcessRunner.Run("git", ["rev-parse", "HEAD"], tree.Root);
        Assert.Equal(0, git.ExitCode);
        string head = git.StandardOutput.Trim();
        string source = current ? head : new string('0', 40);
        string header = "Trend notice from commit " + source + " at 2026-11-04T12:00:00.0000000+00:00";
        tree.Write("bin-build/merge-check/trend-notice.txt", header + "\nSlower than usual\n");
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(7, Prepare.RunWithTimings(() => 7, tree.Root));
        }
        finally { Console.SetOut(previous); }
        Assert.Contains(header, output.ToString());
        Assert.Contains(current ? "(current HEAD)" : "(differs from current HEAD " + head + ")", output.ToString());
        Assert.Contains("Slower than usual", output.ToString());
    }

    private static string[] Rows(StaticTree tree, string metric, string unit, params double[] values)
    {
        string directory = Path.Combine(tree.Root, Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR", directory);
            foreach (double value in values) TestDeadlines.Report(metric, value, unit);
            return Directory.GetFiles(directory, "trend-*.jsonl").SelectMany(File.ReadAllLines).ToArray();
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR", previous); }
    }

    [Theory]
    [InlineData(4, 210, false)]
    [InlineData(5, 190, false)]
    [InlineData(5, 200, false)]
    [InlineData(5, 210, true)]
    public void UsesFiveEarlierRowsAndStrictlyMoreThanTwiceTheMedian(int count, double value, bool slower)
    {
        using var tree = new StaticTree();
        var current = Rows(tree, "read-ms", "ms", value);
        var history = Rows(tree, "read-ms", "ms", Enumerable.Repeat(100d, count).ToArray());
        string notice = MergeCheck.TrendNotice(current, history, out var samples);
        Assert.Single(samples);
        if (slower)
        {
            Assert.Contains("Slower than usual", notice);
            Assert.Contains("read-ms: 210 ms, median 100 ms, ratio 2.1x", notice);
        }
        else Assert.Empty(notice);
    }

    [Fact]
    public void UsesTheLatestTwentyRowsForEachMetricAndUnit()
    {
        using var tree = new StaticTree();
        var older = Rows(tree, "read", "ms", Enumerable.Repeat(10_000d, 21).ToArray());
        var recent = Rows(tree, "read", "ms", Enumerable.Repeat(100d, 20).ToArray());
        var unrelated = Rows(tree, "write", "ms", Enumerable.Repeat(10_000d, 21).ToArray());
        var otherUnits = Rows(tree, "read", "s", Enumerable.Repeat(10_000d, 21).ToArray());
        string notice = MergeCheck.TrendNotice(Rows(tree, "read", "ms", 210),
            older.Concat(recent).Concat(unrelated).Concat(otherUnits), out _);
        Assert.Contains("read: 210 ms, median 100 ms, ratio 2.1x", notice);
        Assert.DoesNotContain("write:", notice);
    }

    [Fact]
    public void UsesTheMiddlePairForAnEvenSampleCount()
    {
        using var tree = new StaticTree();
        var history = Rows(tree, "read", "ms", 100, 40, 300, 60, 200, 20);
        string notice = MergeCheck.TrendNotice(Rows(tree, "read", "ms", 168), history, out _);
        Assert.Contains("median 80 ms, ratio 2.1x", notice);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"metric\":\"read\",\"value\":\"100\",\"unit\":\"ms\"}")]
    [InlineData("{\"metric\":\"read\",\"value\":100}")]
    [InlineData("{\"metric\":\"read\",\"metric\":\"write\",\"value\":100,\"unit\":\"ms\"}")]
    public void SkipsMalformedRowsAndKeepsValidHistory(string malformed)
    {
        using var tree = new StaticTree();
        var current = Rows(tree, "read", "ms", 210);
        var history = Rows(tree, "read", "ms", 100, 100, 100, 100, 100);
        string notice = MergeCheck.TrendNotice(current.Append(malformed), history.Prepend(malformed), out var samples);
        Assert.Single(samples);
        Assert.Contains("Skipped malformed trend sample row 2.", notice);
        Assert.Contains("Skipped malformed trend history row 1.", notice);
        Assert.Contains("read: 210 ms, median 100 ms, ratio 2.1x", notice);
    }

    [Fact]
    public void RecordsAndComparesJobsWithoutTestSamples()
    {
        using var tree = new StaticTree();
        string output = Path.Combine(tree.Root, "bin-build/merge-check");
        Directory.CreateDirectory(output);
        CommandResult Git(string[] args) => new(0,
            args.Contains("HEAD") ? "revision" : Path.Combine(tree.Root, ".git"), "");
        for (int run = 0; run < 5; run++)
            Assert.Equal(0, MergeCheck.MergeTrends(tree.Root, output, Git,
                [new("suite-fixture", 100, new(0, "", ""))]).ExitCode);
        var result = MergeCheck.MergeTrends(tree.Root, output, Git,
            [new("suite-fixture", 210, new(0, "", ""))]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("timing:suite-fixture: 210 s, median 100 s, ratio 2.1x", result.StandardOutput);
        string notice = File.ReadAllText(Path.Combine(output, "trend-notice.txt"));
        Assert.Equal(result.StandardOutput, notice[(notice.IndexOf('\n') + 1)..]);
        Assert.Contains("Slower than usual", MergeCheck.Summary([new("test-trends", 0, result)], 0));
        var history = File.ReadAllLines(Path.Combine(tree.Root, "bin-build/trend.jsonl"));
        Assert.Equal(6, history.Length);
        Assert.All(history, row =>
        {
            var sample = JsonNode.Parse(row)!;
            Assert.Equal("revision", (string?)sample["head"]);
            Assert.True(DateTimeOffset.TryParse((string?)sample["utc"], out var utc));
            Assert.Equal(TimeSpan.Zero, utc.Offset);
        });
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("malformed", false)]
    [InlineData("malformed", true)]
    [InlineData("unreadable", false)]
    [InlineData("unreadable", true)]
    [InlineData("unwritable", false)]
    [InlineData("unwritable", true)]
    [InlineData("merged", false)]
    [InlineData("merged", true)]
    [InlineData("notice", false)]
    [InlineData("notice", true)]
    public void TrendStorageProblemsDoNotChangeTheGateExitCode(string problem, bool failedCheck)
    {
        using var tree = new StaticTree();
        string output = Path.Combine(tree.Root, "bin-build/merge-check");
        Directory.CreateDirectory(output);
        tree.Write("tests/test-suites.json", new JsonObject
        {
            ["fixture"] = new JsonObject
            {
                ["project"] = "tests/one.csproj", ["arguments"] = new JsonArray(),
                ["minimumPassed"] = 1, ["maximumSkipped"] = 0
            }
        }.ToJsonString());
        tree.Write("tests/one.csproj", "<Project />");
        if (problem == "malformed") tree.Write("main/bin-build/trend.jsonl", "not JSON\n");
        if (problem == "unreadable") Directory.CreateDirectory(Path.Combine(tree.Root, "main/bin-build/trend.jsonl"));
        if (problem == "unwritable") tree.Write("main/bin-build", "blocking file");
        CommandResult Process(string exe, IEnumerable<string> args, string root, IDictionary<string, string?>? env)
        {
            if (problem is "merged" or "notice")
                Directory.CreateDirectory(Path.Combine(output, problem == "merged" ? "trend.jsonl" : "trend-notice.txt"));
            return new(failedCheck && args.Contains("check-repository") ? 7 : 0, "", "");
        }
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "base"
            : args[0] == "diff" ? "README.md\0"
            : args.Contains("--git-common-dir") ? Path.Combine(tree.Root, "main/.git")
            : args.Contains("HEAD") ? "revision" : "", "");
        int exit = MergeCheck.Run(tree.Root, Options.Parse([], new HashSet<string>()), Process, Git);
        Assert.Equal(failedCheck ? 1 : 0, exit);
        string log = File.ReadAllText(Path.Combine(output, "test-trends.log"));
        Assert.Contains("Skipped", log);
    }

    [Fact]
    public void UnreadableSampleFilesLeaveOtherSamplesAvailable()
    {
        using var tree = new StaticTree();
        string output = Path.Combine(tree.Root, "bin-build/merge-check");
        string path = tree.Write("bin-build/merge-check/trend/trend-locked.jsonl", "{}");
        var rows = Rows(tree, "read", "ms", 210);
        tree.Write("bin-build/merge-check/trend/trend-readable.jsonl", rows.Single());
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = MergeCheck.MergeTrends(tree.Root, output,
            args => new(0, args.Contains("HEAD") ? "revision" : Path.Combine(tree.Root, ".git"), ""), []);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Skipped trend sample file:", result.StandardOutput);
        Assert.Equal(rows, File.ReadAllLines(Path.Combine(output, "trend.jsonl")));
    }

    [Fact]
    public void ReadOnlyHistoryStillProducesTheNotice()
    {
        using var tree = new StaticTree();
        string output = Path.Combine(tree.Root, "bin-build/merge-check");
        Directory.CreateDirectory(output);
        string history = tree.Write("bin-build/trend.jsonl", string.Join('\n',
            Rows(tree, "read", "ms", 100, 100, 100, 100, 100)) + "\n");
        tree.Write("bin-build/merge-check/trend/trend-current.jsonl", Rows(tree, "read", "ms", 210).Single());
        using var locked = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = MergeCheck.MergeTrends(tree.Root, output,
            args => new(0, args.Contains("HEAD") ? "revision" : Path.Combine(tree.Root, ".git"), ""), []);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Skipped trend history write:", result.StandardOutput);
        Assert.Contains("read: 210 ms, median 100 ms, ratio 2.1x", result.StandardOutput);
        Assert.Equal(5, File.ReadAllLines(history).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareKeepsItsResultWhenTheNoticeIsAbsentOrUnreadable(bool unreadable)
    {
        using var tree = new StaticTree();
        using var locked = unreadable
            ? new FileStream(tree.Write("bin-build/merge-check/trend-notice.txt", "notice"),
                FileMode.Open, FileAccess.Read, FileShare.None)
            : null;
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(7, Prepare.RunWithTimings(() => 7, tree.Root));
        }
        finally { Console.SetOut(previous); }
        if (unreadable) Assert.Contains("Skipped trend notice read:", output.ToString());
        else Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void PreparePrintsTheLatestNoticeAndKeepsItsResult(int exitCode)
    {
        using var tree = new StaticTree();
        const string notice = "Slower than usual\nread: 210 ms, median 100 ms, ratio 2.1x\n";
        tree.Write("bin-build/merge-check/trend-notice.txt", notice);
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(exitCode, Prepare.RunWithTimings(() => exitCode, tree.Root));
        }
        finally { Console.SetOut(previous); }
        Assert.Contains(notice, output.ToString());
        Assert.Contains("Trend notice provenance unavailable (legacy file).", output.ToString());
    }
}
