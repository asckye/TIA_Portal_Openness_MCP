using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

// test-suites -RunnerIndex/-RunnerCount run disjoint shard subsets on several machines; -GateOnly applies every
// registered suite gate to their union and refuses missing, duplicated or differently planned shards.
public sealed class SuiteRunnerTests
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "NoBuild", "GateOnly", "ProveEquivalence" };

    private sealed class Workspace : IDisposable
    {
        private readonly OfflineFixtures fixture = new("suite-runners-");
        internal string Root => fixture.DirectoryPath;
        internal string Catalog => Path.Combine(Root, "catalog.json");
        internal string Runners => Path.Combine(Root, "runners");
        internal readonly List<string> Started = new();
        internal int RowsPerShard = 40;
        internal int SkippedPerShard;

        internal Workspace(int maximumSkipped = 1)
        {
            Directory.CreateDirectory(Path.Combine(Root, "demo"));
            File.WriteAllText(Path.Combine(Root, "demo/Demo.Tests.csproj"), "<Project />");
            File.WriteAllText(Catalog, new JsonObject {
                ["demo"] = new JsonObject { ["project"] = "demo/Demo.Tests.csproj", ["minimumPassed"] = 100, ["maximumSkipped"] = maximumSkipped, ["arguments"] = new JsonArray() },
                ["single"] = new JsonObject { ["project"] = "demo/Demo.Tests.csproj", ["minimumPassed"] = 2, ["maximumSkipped"] = 0, ["arguments"] = new JsonArray() }
            }.ToJsonString());
        }

        // Stands in for dotnet test: writes one TRX with rows that are unique to the suite and shard.
        internal CommandResult Process(string executable, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToList();
            string directory = args[args.IndexOf("--results-directory") + 1];
            string logger = args.Single(arg => arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal));
            string suite = Path.GetFileNameWithoutExtension(logger["trx;LogFileName=".Length..]);
            string shard = environment!["TIA_MCP_TEST_SHARD_INDEX"]!;
            lock (Started) Started.Add(suite + "-shard-" + shard);
            int rows = suite == "single" ? 2 : RowsPerShard, skipped = suite == "single" ? 0 : SkippedPerShard;
            var path = Path.Combine(directory, suite + ".trx");
            DotnetSuiteSelfTests.SyntheticTrx(path, Enumerable.Repeat("Passed", rows).Concat(Enumerable.Repeat("NotExecuted", skipped)).ToArray());
            var doc = XDocument.Load(path); var ns = doc.Root!.Name.Namespace;
            foreach (var row in doc.Descendants(ns + "UnitTestResult"))
            {
                row.SetAttributeValue("executionId", suite + shard + ":" + row.Attribute("executionId")!.Value);
                row.SetAttributeValue("testName", suite + shard + ":" + row.Attribute("testName")!.Value);
            }
            doc.Save(path);
            return new(0, "", "");
        }

        internal CommandResult Run(params string[] extra)
        {
            var args = new List<string> { "-Catalog", Catalog, "-Suite", "demo", "-Suite", "single", "-Shards", "3", "-MaxParallelism", "2" };
            args.AddRange(extra);
            return DotnetSuites.Run(Root, Options.Parse(args.ToArray(), Flags), null, Process);
        }

        internal CommandResult Runner(int index, int count = 2, string shards = "3") =>
            Run("-NoBuild", "-Shards", shards, "-RunnerIndex", index.ToString(), "-RunnerCount", count.ToString(), "-ResultsDirectory", Path.Combine(Runners, "runner-" + index));

        internal CommandResult Gate(string name = "gate") =>
            Run("-GateOnly", "-RunnerResults", Runners, "-ResultsDirectory", Path.Combine(Root, name));

        internal JsonNode Report(string name = "gate") => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, name, "report.json")))!;

        public void Dispose() => fixture.Dispose();
    }

    private static CheckJob Job(string name, double seconds) => new(name, () => new(0, "", ""), []) { EstimatedSeconds = seconds };

    [Fact]
    public void AssignmentIsRoundRobinWithoutHistoryAndLongestFirstWithIt()
    {
        var cold = new[] { Job("a-shard-0", 1200), Job("a-shard-1", 1200), Job("a-shard-2", 1200), Job("a-gate", 0), Job("b-shard-0", 1200), Job("b-gate", 0) };
        var assigned = SuiteRunners.Assign(cold, 2);
        Assert.Equal(new[] { 0, 1, 0, 1 }, new[] { "a-shard-0", "a-shard-1", "a-shard-2", "b-shard-0" }.Select(name => assigned[name]));
        Assert.DoesNotContain("a-gate", assigned.Keys);
        var measured = SuiteRunners.Assign(new[] { Job("x", 10), Job("y", 100), Job("z", 50), Job("w", 45) }, 2);
        // y (100) on runner 0; z (50), w (45) and x (10) then fill runner 1 while it stays below 100.
        Assert.Equal(0, measured["y"]); Assert.Equal(1, measured["z"]); Assert.Equal(1, measured["w"]); Assert.Equal(1, measured["x"]);
        Assert.Equal(SuiteRunners.Assign(cold, 3), SuiteRunners.Assign(cold, 3));
    }

    [Theory]
    [InlineData("-RunnerIndex 0")]
    [InlineData("-RunnerCount 2")]
    [InlineData("-RunnerIndex 2 -RunnerCount 2")]
    [InlineData("-RunnerIndex -1 -RunnerCount 2")]
    [InlineData("-RunnerIndex 0 -RunnerCount 2 -GateOnly")]
    [InlineData("-RunnerIndex 0 -RunnerCount 2 -ProveEquivalence")]
    [InlineData("-GateOnly")]
    [InlineData("-GateOnly -RunnerResults runners -ProveEquivalence")]
    public void InvalidRunnerOptionsAreRefusedBeforeAnyWork(string options)
    {
        using var workspace = new Workspace();
        var error = Assert.Throws<ReleaseException>(() => workspace.Run(["-NoBuild", .. options.Split(' ')]));
        Assert.Equal(64, error.ExitCode);
        Assert.Empty(workspace.Started);
    }

    [Fact]
    public void RunnersShareTheShardsAndTheGateKeepsEverySuiteGate()
    {
        using var workspace = new Workspace();
        Assert.Equal(0, workspace.Runner(0).ExitCode);
        Assert.Equal(new[] { "demo-shard-0", "demo-shard-2" }, workspace.Started.Order());
        workspace.Started.Clear();
        Assert.Equal(0, workspace.Runner(1).ExitCode);
        Assert.Equal(new[] { "demo-shard-1", "single-shard-0" }, workspace.Started.Order());
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.Runners, "runner-1", SuiteRunners.ManifestName)))!;
        Assert.Equal(3, (int)manifest["shardCounts"]!["demo"]!); Assert.Equal(1, (int)manifest["shardCounts"]!["single"]!);
        Assert.False(File.Exists(Path.Combine(workspace.Runners, "runner-1", "demo.trx")));
        workspace.Started.Clear();
        var gate = workspace.Gate();
        Assert.True(gate.ExitCode == 0, gate.StandardOutput + gate.StandardError);
        Assert.Empty(workspace.Started);
        var demo = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.Root, "gate", "demo.json")))!;
        Assert.Equal(120, (int)demo["passed"]!); Assert.Equal(100, (int)demo["minimumPassed"]!); Assert.Empty(demo["errors"]!.AsArray());
        Assert.Equal(3, SuiteSharding.Identities(Enumerable.Range(0, 3).Select(i => Path.Combine(workspace.Root, "gate", "demo", "shard-" + i, "demo.trx"))).Count / 40);
        Assert.Empty(workspace.Report()["coverageErrors"]!.AsArray());
        Assert.Equal(2, (int)workspace.Report()["passed"]!);
    }

    [Fact]
    public void GateRefusesAMissingRunnerAndItsShards()
    {
        using var workspace = new Workspace();
        workspace.Runner(0);
        var gate = workspace.Gate();
        Assert.Equal(1, gate.ExitCode);
        var errors = workspace.Report()["coverageErrors"]!.AsArray().Select(e => (string)e!).ToArray();
        Assert.Contains("runner 1 of 2 has no runner.json", errors);
        Assert.Contains("demo-shard-1 was not run by any runner", errors);
        Assert.Contains("single-shard-0 was not run by any runner", errors);
        Assert.Contains("missing shard TRX", gate.StandardError);
    }

    [Fact]
    public void GateRefusesAShardThatRanTwiceOrUnderAnotherPlan()
    {
        using var workspace = new Workspace();
        workspace.Runner(0); workspace.Runner(1);
        CopyDirectory(Path.Combine(workspace.Runners, "runner-0"), Path.Combine(workspace.Runners, "runner-0-again"));
        Assert.Equal(1, workspace.Gate().ExitCode);
        var errors = workspace.Report()["coverageErrors"]!.AsArray().Select(e => (string)e!).ToArray();
        Assert.Contains(errors, e => e.EndsWith("runner 0 reported twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("demo-shard-0 ran on two runners", StringComparison.Ordinal));
        Directory.Delete(Path.Combine(workspace.Runners, "runner-0-again"), true);
        Directory.Delete(Path.Combine(workspace.Runners, "runner-1"), true);
        workspace.Runner(1, shards: "2");
        Assert.Equal(1, workspace.Gate("gate-2").ExitCode);
        errors = workspace.Report("gate-2")["coverageErrors"]!.AsArray().Select(e => (string)e!).ToArray();
        Assert.Contains(errors, e => e.EndsWith("ran -Shards 2, the gate expects 3", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.EndsWith("demo shard plan differs from the gate's plan", StringComparison.Ordinal) || e.Contains("demo shard count differs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(30, 0, "passed 90 below minimum 100")]
    [InlineData(40, 1, "skipped 3 above maximum 1")]
    public void UnionGateKeepsTheMinimumAndSkipCap(int rows, int skipped, string expected)
    {
        using var workspace = new Workspace();
        workspace.RowsPerShard = rows; workspace.SkippedPerShard = skipped;
        workspace.Runner(0); workspace.Runner(1);
        var gate = workspace.Gate();
        Assert.Equal(1, gate.ExitCode);
        Assert.Empty(workspace.Report()["coverageErrors"]!.AsArray());
        Assert.Contains(expected, gate.StandardError);
    }

    [Fact]
    public void FailedShardOnOneRunnerFailsTheUnionGate()
    {
        using var workspace = new Workspace();
        workspace.Runner(0); workspace.Runner(1);
        var trx = Path.Combine(workspace.Runners, "runner-1", "demo", "shard-1", "demo.trx");
        var doc = XDocument.Load(trx); var ns = doc.Root!.Name.Namespace;
        doc.Descendants(ns + "UnitTestResult").First().SetAttributeValue("outcome", "Failed");
        var counters = doc.Descendants(ns + "Counters").Single();
        counters.SetAttributeValue("passed", (int)counters.Attribute("passed")! - 1); counters.SetAttributeValue("failed", 1);
        doc.Save(trx);
        var gate = workspace.Gate();
        Assert.Equal(1, gate.ExitCode);
        Assert.Contains("1 failed checks", gate.StandardError);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
