using System.Text.Json.Nodes;
using System.Text.Json;
using System.Xml.Linq;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class SuiteShardingTests
{
    [Fact]
    public void RecordDropsKeysOfACompleteRun()
    {
        using var tree = new StaticTree();
        var history = Path.Combine(tree.Root, "history");
        tree.Write("history/sample.json", "{\"T:Removed.Test\":100}");
        var trx = tree.Write("results/shard-0/sample.trx", """
            <TestRun><TestDefinitions><UnitTest id="one"><TestMethod className="Current" name="Test" /></UnitTest></TestDefinitions>
            <Results><UnitTestResult testId="one" testName="Current.Test" duration="00:00:02" /></Results></TestRun>
            """);
        tree.Write("results/shard-5/sample.trx", File.ReadAllText(trx).Replace("Current", "Stale", StringComparison.Ordinal));
        var directory = Path.GetDirectoryName(Path.GetDirectoryName(trx))!;
        SuiteCosts.Record("sample", directory, history, expectedShards: 2);
        var partial = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(history, "sample.json")))!;
        Assert.Equal(100, partial["T:Removed.Test"]);
        SuiteCosts.Record("sample", directory, history, expectedShards: 1);
        var complete = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(history, "sample.json")))!;
        Assert.Equal(new[] { "T:Current.Test" }, complete.Keys);
        Assert.Equal(2, complete["T:Current.Test"]);
    }

    [Fact]
    public void PlanFromSharedHistorySplitsAnExpensiveClass()
    {
        using var tree = new StaticTree();
        tree.Write("history/sample.json", "{\"T:Expensive.First\":60,\"T:Expensive.Second\":60}");
        tree.Write("history/sample.classes.json", JsonSerializer.Serialize(new Dictionary<string, SuiteCosts.ClassCost>
        {
            ["Expensive"] = new(120, 120, 2, DateTime.UtcNow)
        }));
        var plan = SuiteCosts.Prepare("sample", Path.Combine(tree.Root, "new-worktree/results"), Path.Combine(tree.Root, "history"), null, 8);
        var assignments = File.ReadAllLines(plan.Path).Select(line => line.Split('\t')).ToArray();
        Assert.Equal(2, assignments.Length);
        Assert.Equal(2, assignments.Select(row => row[1]).Distinct().Count());
        Assert.Equal(120, plan.Costs.Sum());
    }

    [Fact]
    public async Task TimingHistoryOptionIsReadAndWrittenAtomically()
    {
        using var tree = new StaticTree();
        var history = Path.Combine(tree.Root, "history");
        tree.Write("history/sample.json", "{\"T:Current.Test\":2}");
        tree.Write("history/sample.classes.json", "{}");
        var suite = new JsonObject { ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        var options = Options.Parse(["-Suite=sample", "-TimingHistory=" + history, "-ResultsDirectory=" + Path.Combine(tree.Root, "results")], Program.BooleanOptions);
        var result = PlainShards.Run(tree.Root, options, new JsonObject { ["sample"] = suite }, (_, _, _, environment) =>
        {
            var directory = Path.GetDirectoryName(environment!["TIA_MCP_TEST_CASE_TIMINGS"])!;
            File.WriteAllText(Path.Combine(directory, "sample.trx"), """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                <TestDefinitions><UnitTest id="one"><TestMethod className="Current" name="Test" /></UnitTest></TestDefinitions>
                <Results><UnitTestResult testId="one" executionId="run" testName="Current.Test" outcome="Passed" duration="00:00:03" /></Results>
                <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
                """);
            return new(0, "", "");
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(history, "sample.json")))!["T:Current.Test"]);
        Assert.False(Directory.Exists(Path.Combine(tree.Root, "bin-build/check-timings")));
        var output = Path.Combine(tree.Root, "results/sample");
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                using var costs = SuiteCosts.ReadHistory<JsonDocument>(Path.Combine(history, "sample.json"));
                using var classes = SuiteCosts.ReadHistory<JsonDocument>(Path.Combine(history, "sample.classes.json"));
                Assert.Equal(JsonValueKind.Object, costs.RootElement.ValueKind);
                Assert.Equal(JsonValueKind.Object, classes.RootElement.ValueKind);
            }
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(120)));
        try { for (int index = 0; index < 20; index++) SuiteCosts.Record("sample", output, history, expectedShards: 1); }
        finally { stop.Cancel(); await reader; }
        Assert.Empty(Directory.EnumerateFiles(history, "*.tmp"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public void DeterministicShardsAreADisjointCompletePartition(int count)
    {
        var keys = Enumerable.Range(0, 17666).Select(i => "TestClass:theory(" + i + ")").ToArray();
        var shards = Enumerable.Range(0, count).Select(index => keys.Where(key => TestShard.Bucket(key, count) == index).ToArray()).ToArray();
        Assert.Equal(keys.Order(), shards.SelectMany(shard => shard).Order());
        Assert.Equal(keys.Length, shards.Sum(shard => shard.Length));
        Assert.All(keys, key => Assert.InRange(TestShard.Bucket(key, count), 0, count - 1));
        Assert.Equal(0, TestShard.Bucket("PortedPortalAdmissionTests:case-42", 8));
        var costs = keys.ToDictionary(key => key, key => (double)(TestShard.Bucket(key, 13) + 1));
        var (plan, totals) = SuiteCosts.Balance(costs, count);
        Assert.Equal(keys.Order(), plan.Keys.Order()); Assert.Equal(costs.Values.Sum(), totals.Sum());
        Assert.True(totals.Max() - totals.Min() <= 13); Assert.Equal(count, totals.Length);
        Assert.Equal(plan, SuiteCosts.Balance(costs.Reverse().ToDictionary(p => p.Key, p => p.Value), count).Item1);
    }

    [Fact]
    public void LargeSerializableTheoriesSplitWithoutSplittingLegacyProducers()
    {
        Assert.True(TestShard.SplitCases("PortedPortalAdmissionTests")); Assert.True(TestShard.SplitCases("BehaviorParityTests"));
        Assert.True(TestShard.SplitCases("BehaviorCapabilityTests")); Assert.False(TestShard.SplitCases("ApiMetadataChecks"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestShard.Bucket("case", 0));
        Assert.Equal(TestShard.CostKey("Legacy", "Checks", "row one", true), TestShard.CostKey("Legacy", "Checks", "row two", true));
        Assert.NotEqual(TestShard.CostKey("Theory", "Check", "case one", false), TestShard.CostKey("Theory", "Check", "case two", false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void SensitiveClassesRemainExclusiveEvenWithoutHistoryOrWithOldCaseAssignments(int ordinaryCount)
    {
        var cases = TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.Methods.SelectMany(pair => pair.Value.SelectMany(method =>
            Enumerable.Range(0, 7).Select(row => (Class: pair.Key, Key: "T:" + pair.Key + "." + method + "(" + row + ")"))))
            .Concat(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.Classes.Keys.Select(cls => (Class: cls, Key: "T:" + cls + ".Unmeasured()")))
            .Concat(new[] { "Unmeasured", "UnmeasuredTheory" }.SelectMany(cls =>
                Enumerable.Range(0, 7).Select(row => (Class: cls, Key: "T:" + cls + ".Case(" + row + ")")))).ToArray();
        var oldPlan = cases.Take(100).ToDictionary(test => test.Key, _ => 0);
        int count = ordinaryCount + 1;
        var shards = Enumerable.Range(0, count).Select(index => cases.Where(test =>
            TestShard.Assign(test.Class, test.Key, test.Key, count, oldPlan, true) == index).ToArray()).ToArray();
        Assert.Equal(cases.OrderBy(test => test.Key), shards.SelectMany(shard => shard).OrderBy(test => test.Key));
        Assert.Equal(cases.Length, shards.Sum(shard => shard.Length));
        Assert.All(shards[^1], test => Assert.True(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.ContainsCost(test.Key)));
        Assert.All(shards.Take(ordinaryCount).SelectMany(shard => shard), test => Assert.False(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.ContainsCost(test.Key)));
        Assert.Equal(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.Methods.Values.Sum(methods => methods.Length) * 7, shards[^1].Length);
        Assert.Throws<InvalidOperationException>(() => TestShard.Assign("Unmeasured", "T:unknown", "fallback", count,
            new Dictionary<string, int> { ["C:Unmeasured"] = ordinaryCount }, true));
    }

    [Fact]
    public void ProcessIsolationClearsSdkAndShardState()
    {
        using var fixture = new OfflineFixtures();
        var one = FastSuites.EnvironmentFor(Path.Combine(fixture.DirectoryPath, "one"));
        var two = FastSuites.EnvironmentFor(Path.Combine(fixture.DirectoryPath, "two"), "sdk");
        Assert.Null(one["TIA_MCP_TEST_PUBLIC_API_ROOT"]); Assert.Equal("sdk", two["TIA_MCP_TEST_PUBLIC_API_ROOT"]);
        foreach (var key in new[] { "TEMP", "TMP", "TIA_MCP_DATA_DIRECTORY", "TIA_MCP_TEST_PIPE_SCOPE" }) Assert.NotEqual(one[key], two[key]);
        Assert.Null(one["TIA_MCP_TEST_PRODUCT_ROOT"]); Assert.Null(one["TIA_MCP_TEST_SHARD_COUNT"]);
        Assert.StartsWith(one["TEMP"]!, one["TIA_MCP_DATA_DIRECTORY"]!, StringComparison.OrdinalIgnoreCase);
        for (var parent = new DirectoryInfo(one["TEMP"]!); parent != null; parent = parent.Parent)
            Assert.False(File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")));
    }

    [Theory]
    [InlineData(4, 1, true)]
    [InlineData(5, 1, false)]
    [InlineData(4, 0, false)]
    public void MinimumAndSkipLimitApplyToShardTotals(int minimum, int maximum, bool passes)
    {
        using var fixture = new OfflineFixtures();
        var paths = MakeShards(fixture.DirectoryPath, new[] { "Passed", "Passed" }, new[] { "Passed", "Passed", "NotExecuted" });
        for (int i = 0; i < paths.Length; i++)
        {
            var doc = XDocument.Load(paths[i]);
            foreach (var row in doc.Descendants(doc.Root!.Name.Namespace + "UnitTestResult")) row.SetAttributeValue("testName", i + ":" + row.Attribute("testName")!.Value);
            doc.Save(paths[i]);
        }
        var result = SuiteSharding.Merge("suite", new JsonObject { ["minimumPassed"] = minimum, ["maximumSkipped"] = maximum }, paths, fixture.DirectoryPath);
        Assert.Equal(passes ? 0 : 1, result.ExitCode);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "suite.json")))!;
        Assert.Equal(4, (int)summary["passed"]!); Assert.Equal(1, (int)summary["skipped"]!);
    }

    [Fact]
    public void EmptyFirstShardDoesNotLoseLaterResults()
    {
        using var fixture = new OfflineFixtures();
        var paths = MakeShards(fixture.DirectoryPath, Array.Empty<string>(), new[] { "Passed", "Passed" });
        var empty = XDocument.Load(paths[0]); var ns = empty.Root!.Name.Namespace;
        empty.Root.Element(ns + "Results")!.Remove(); empty.Root.Element(ns + "TestDefinitions")!.Remove(); empty.Save(paths[0]);
        var result = SuiteSharding.Merge("suite", new JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 }, paths, fixture.DirectoryPath);
        Assert.Equal(0, result.ExitCode); Assert.Equal(2, SuiteSharding.Identities([Path.Combine(fixture.DirectoryPath, "suite.trx")]).Values.Sum());
    }

    [Fact]
    public void MergeCannotHideFailedOrMissingShards()
    {
        using var fixture = new OfflineFixtures(); var paths = MakeShards(fixture.DirectoryPath, new[] { "Passed" }, new[] { "Failed" });
        var suite = new JsonObject { ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        Assert.Equal(1, SuiteSharding.Merge("suite", suite, paths, fixture.DirectoryPath).ExitCode);
        Assert.Throws<ReleaseException>(() => { SuiteSharding.Merge("suite", suite, [paths[0], paths[1] + ".missing"], fixture.DirectoryPath); });
    }

    private static string[] MakeShards(string directory, params string[][] outcomes)
    {
        return outcomes.Select((rows, index) =>
        {
            var path = Path.Combine(directory, "shard-" + index + ".trx"); DotnetSuiteSelfTests.SyntheticTrx(path, rows);
            var doc = XDocument.Load(path); var ns = doc.Root!.Name.Namespace;
            foreach (var result in doc.Descendants(ns + "UnitTestResult")) result.SetAttributeValue("executionId", index + ":" + result.Attribute("executionId")!.Value);
            doc.Save(path); return path;
        }).ToArray();
    }

    [Fact]
    public void MergeRejectsRepeatedIdentityEvenWithDifferentExecutionIdsAndPassingTotals()
    {
        using var fixture = new OfflineFixtures();
        var one = Path.Combine(fixture.DirectoryPath, "one.trx"); var two = Path.Combine(fixture.DirectoryPath, "two.trx");
        DotnetSuiteSelfTests.SyntheticTrx(one, ["Passed"]); DotnetSuiteSelfTests.SyntheticTrx(two, ["Passed"]);
        var doc = System.Xml.Linq.XDocument.Load(two);
        doc.Descendants(doc.Root!.Name.Namespace + "UnitTestResult").Single().SetAttributeValue("executionId", Guid.NewGuid()); doc.Save(two);
        var result = SuiteSharding.Merge("duplicate", new System.Text.Json.Nodes.JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 }, [one, two], fixture.DirectoryPath);
        Assert.Equal(1, result.ExitCode); Assert.Contains("duplicate test identities", result.StandardError);
        Assert.Equal(2, (int)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "duplicate.json")))!["passed"]!);
    }

    [Fact]
    public void DistinctTheoryIdsWithTheSameTruncatedDisplayNameKeepEveryAssertion()
    {
        using var fixture = new OfflineFixtures();
        var one = Path.Combine(fixture.DirectoryPath, "one.trx"); var two = Path.Combine(fixture.DirectoryPath, "two.trx");
        DotnetSuiteSelfTests.SyntheticTrx(one, ["Passed"]); DotnetSuiteSelfTests.SyntheticTrx(two, ["Passed"]);
        var doc = System.Xml.Linq.XDocument.Load(two); var ns = doc.Root!.Name.Namespace;
        var id = Guid.NewGuid().ToString(); doc.Descendants(ns + "UnitTest").Single().SetAttributeValue("id", id);
        var row = doc.Descendants(ns + "UnitTestResult").Single(); row.SetAttributeValue("testId", id); row.SetAttributeValue("executionId", Guid.NewGuid()); doc.Save(two);
        var suite = new System.Text.Json.Nodes.JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 };
        Assert.Equal(0, SuiteSharding.Merge("distinct", suite, [one, two], fixture.DirectoryPath).ExitCode);
        Assert.Equal(2, SuiteSharding.Identities([one, two]).Count);
    }

    [Fact]
    public void SensitiveMethodsLeaveUntimedPortalCasesInNormalShards()
    {
        using var fixture = new OfflineFixtures();
        const string cls = "PortedPortalAdmissionTests";
        var costs = new Dictionary<string, double> { ["T:" + cls + ".Port_dispatch_takes_the_shared_worker_lane_before_journaling(cancel: false)"] = 2,
            ["T:" + cls + ".Missing_portal_never_turns_an_unstarted_port_into_an_unknown_write(release: 21)"] = 120 };
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "portal.json"), JsonSerializer.Serialize(costs));
        var plan = SuiteCosts.Prepare("portal", Path.Combine(fixture.DirectoryPath, "results"), fixture.DirectoryPath, null, 8, true);
        Assert.Equal(122, plan.Costs.Sum()); Assert.Equal(2, plan.Costs[^1]);
        var assignments = File.ReadAllLines(plan.Path).ToDictionary(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0])), line => int.Parse(line.Split('\t')[1]));
        foreach (var key in costs.Keys)
            Assert.Equal(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.ContainsCost(key) ? plan.Costs.Length - 1 : assignments[key],
                TiaMcp.Engine.Tests.Shared.TestShard.Assign(cls, key, key, plan.Costs.Length, assignments, true));
    }

    [Fact]
    public void PortalLaneMethodHasItsOwnSensitivePartitionWithoutLosingOrDuplicatingCosts()
    {
        using var fixture = new OfflineFixtures();
        const string portal = "T:PortedPortalAdmissionTests.Port_dispatch_takes_the_shared_worker_lane_before_journaling(name: x, cancel: false)";
        const string process = "T:FoundationProcessLeaseTests.Job_object_killed_idle_worker_allows_takeover_with_client_evidence";
        const string ordinary = "T:PortedPortalAdmissionTests.Missing_portal_never_turns_an_unstarted_port_into_an_unknown_write(release: 21)";
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "sample.json"), JsonSerializer.Serialize(new Dictionary<string, double> {
            [portal] = 182, [process] = 3, [ordinary] = 120
        }));
        var plan = SuiteCosts.Prepare("sample", Path.Combine(fixture.DirectoryPath, "plan"), fixture.DirectoryPath, null, 8, true, 2);
        var original = SuiteCosts.Prepare("sample", Path.Combine(fixture.DirectoryPath, "original-plan"), fixture.DirectoryPath, null, 8, true);
        Assert.Equal(original.Costs.Length, plan.Costs.Length);
        Assert.Equal(305, plan.Costs.Sum()); Assert.Equal(3, plan.Costs[^2]); Assert.Equal(182, plan.Costs[^1]);
        var assignments = File.ReadAllLines(plan.Path).ToDictionary(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0])), line => int.Parse(line.Split('\t')[1]));
        Assert.Equal(plan.Costs.Length - 1, TiaMcp.Engine.Tests.Shared.TestShard.Assign("PortedPortalAdmissionTests", portal, portal, plan.Costs.Length, assignments, true, 2));
        Assert.Equal(plan.Costs.Length - 2, TiaMcp.Engine.Tests.Shared.TestShard.Assign("FoundationProcessLeaseTests", process, process, plan.Costs.Length, assignments, true, 2));
        Assert.InRange(TiaMcp.Engine.Tests.Shared.TestShard.Assign("PortedPortalAdmissionTests", ordinary, ordinary, plan.Costs.Length, assignments, true, 2), 0, plan.Costs.Length - 3);
    }

    [Fact]
    public void ProducerTelemetryPreservesEveryRowIncludingFailureAndSkip()
    {
        using var fixture = new OfflineFixtures();
        var path = Path.Combine(fixture.DirectoryPath, "producer.tsv");
        var previous = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PRODUCER_TIMINGS");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_PRODUCER_TIMINGS", path);
            var rows = TiaMcp.Engine.Tests.Shared.CheckSuite.Run("producer", (check, skip) => {
                check(true, "passed"); check(false, "failed"); skip("skipped", "reason");
            }).Select(row => (TiaMcp.Engine.Tests.Shared.CheckRow)row[0]).ToArray();
            Assert.Equal(new[] { "passed", "failed", "skipped" }, rows.Select(row => row.Message));
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => row.Number));
            Assert.True(rows[0].Passed); Assert.False(rows[1].Passed); Assert.Equal("reason", rows[2].SkipReason);
            var fields = Assert.Single(File.ReadAllLines(path)).Split('\t');
            Assert.Equal("producer", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])));
            Assert.True(double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture) >= 0); Assert.Equal("3", fields[2]);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_TEST_PRODUCER_TIMINGS", previous); }
    }

    [Fact]
    public void TrxClassHistorySplitsNewExpensiveClassesAndRetainsLateProducerCost()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var trx = Path.Combine(root, "suite.trx");
        File.WriteAllText(trx, """
            <TestRun><TestDefinitions>
            <UnitTest id="large"><TestMethod className="NewLargeClass" name="Check" /></UnitTest>
            <UnitTest id="small"><TestMethod className="SmallClass" name="Check" /></UnitTest>
            <UnitTest id="late"><TestMethod className="Legacy" name="Rows" /></UnitTest>
            </TestDefinitions><Results>
            <UnitTestResult testId="large" testName="NewLargeClass.Check(case: 1)" duration="00:00:40" />
            <UnitTestResult testId="large" testName="NewLargeClass.Check(case: 2)" duration="00:00:40" />
            <UnitTestResult testId="small" testName="SmallClass.Check" duration="00:00:05" />
            <UnitTestResult testId="late" testName="Legacy.Rows(row: one)" duration="00:00:00.000001" />
            </Results></TestRun>
            """);
        var costs = new Dictionary<string, double> { ["M:Legacy.Rows"] = 70 };
        SuiteCosts.ImportTrx([trx], costs, Path.Combine(root, "sample.classes.json"));
        File.WriteAllText(Path.Combine(root, "sample.json"), JsonSerializer.Serialize(costs));
        var plan = SuiteCosts.Prepare("sample", Path.Combine(root, "plan"), root, null, 8);
        var keys = File.ReadLines(plan.Path).Select(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0]))).ToArray();
        Assert.Contains("C:SmallClass", keys);
        Assert.Contains("T:NewLargeClass.Check(case: 1)", keys); Assert.Contains("T:NewLargeClass.Check(case: 2)", keys);
        Assert.Contains("M:Legacy.Rows", keys); Assert.Equal(155, plan.Costs.Sum());
        var classes = JsonSerializer.Deserialize<Dictionary<string, SuiteCosts.ClassCost>>(File.ReadAllText(Path.Combine(root, "sample.classes.json")))!;
        Assert.Equal(80, classes["NewLargeClass"].TrxSeconds); Assert.Equal(2, classes["NewLargeClass"].Cases);
        Assert.Equal(70, classes["Legacy"].Seconds);
    }
}
