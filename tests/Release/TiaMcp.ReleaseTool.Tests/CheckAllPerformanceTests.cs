using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllPerformanceTests
{
    private static CommandResult Passed() => new(0, "passed\n", "");

    [Fact]
    public void CheckAllCapacityComesFromTheCpuPlanAndKeepsExplicitOverrides()
    {
        Options Parse(params string[] args) => Options.Parse(args, new HashSet<string>());
        Assert.Equal(12, FastSuites.CheckAllParallelism(Parse(), 12));
        Assert.Equal(1, FastSuites.CheckAllParallelism(Parse(), 1));
        Assert.Equal(20, FastSuites.CheckAllParallelism(Parse("-MaxParallelism", "20"), 6));
        Assert.Equal(8, FastSuites.CheckAllParallelism(Parse("-CpuParallelism", "8", "-MaxParallelism", "20"), 6));
        Assert.Throws<ReleaseException>(() => FastSuites.CheckAllParallelism(Parse("-MaxParallelism", "0"), 6));
    }

    [Fact]
    public void ReadyGatesAndStaticChecksPrecedeTheBacklogDeterministically()
    {
        using var fixture = new OfflineFixtures();
        var expected = new[] { "shard", "gate", "static", "a", "z" };
        foreach (bool reverse in new[] { false, true })
        {
            var ran = new List<string>();
            CommandResult Run(string name) { ran.Add(name); return Passed(); }
            CheckJob[] jobs = [
                new("z", () => Run("z"), []), new("a", () => Run("a"), []),
                new CheckJob("static", () => Run("static"), []) { Priority = 1 },
                new CheckJob("gate", () => Run("gate"), ["shard"]) { Priority = 1 },
                new CheckJob("shard", () => Run("shard"), []) { EstimatedSeconds = 100, Priority = 2 }];
            // Gates take the newly freed slot immediately, ahead of long work.
            jobs[2] = jobs[2] with { EstimatedSeconds = 1 };
            jobs[3] = jobs[3] with { EstimatedSeconds = 1 };
            var rows = CheckScheduler.Run(reverse ? jobs.Reverse().ToArray() : jobs, 1, fixture.DirectoryPath);
            Assert.Equal(expected, ran);
            Assert.Equal(jobs.Select(job => job.Name).Order(), rows.Select(row => row.Name).Order());
            Assert.All(rows, row => Assert.True(CheckTiming.Passed(row)));
        }
    }

    [Fact]
    public void RuntimeInputsKeepEveryJobAndEveryShardGateDependency()
    {
        using var fixture = new OfflineFixtures();
        var ran = new List<string>();
        CommandResult Run(string name) { ran.Add(name); return name == "two" ? new(1, "", "failure") : Passed(); }
        var source = new List<CheckJob> { new("suite-output-parity", () => Run("suite-output-parity"), []) };
        foreach (var name in new[] { "one", "two", "three" }) source.Add(new(name, () => Run(name), []));
        source.Add(new CheckJob("gate", () => { Assert.Equal(4, ran.Count); return Run("gate"); }, ["one", "two", "three"])
            { AllowFailedDependencies = true, Priority = 1 });
        var plan = ReleaseCommands.CheckAllRuntimeDependencies(source);
        Assert.Equal(source.Select(job => job.Name), plan.Select(job => job.Name));
        Assert.Equal(new[] { "one", "two", "three" }, plan[^1].Dependencies);
        Assert.All(plan.Skip(1).Take(3), job => Assert.Equal(new[] { "suite-output-parity" }, job.Dependencies));
        CheckScheduler.Run(plan, 1, fixture.DirectoryPath);
        Assert.Equal(source.Count, ran.Count);
        Assert.Equal(source.Select(job => job.Name).Order(), ran.Order());
    }

    [Fact]
    public void CompletedInputsStillBlockTheirOriginalDependentsWithoutRunningTwice()
    {
        using var fixture = new OfflineFixtures();
        CheckTiming[] completed = [new("build", "passed", 0, 1, null), new("source", "failed", 1, 1, "source failure")];
        var ran = new List<string>();
        var rows = CheckScheduler.Run([
            new("preflight", () => throw new Exception("failed source prerequisite must block"), ["source"]),
            new("runtime", () => { ran.Add("runtime"); return Passed(); }, ["build"])], 1, fixture.DirectoryPath, prerequisites: completed);
        Assert.Equal(new[] { "blocked", "passed" }, rows.Select(row => row.Status));
        Assert.Equal(new[] { "runtime" }, ran);
        Assert.Equal(2, rows.Count);
        Assert.Throws<ReleaseException>(() => CheckScheduler.Run([new("source", Passed, [])], 1, fixture.DirectoryPath, prerequisites: completed));
    }

    [Fact]
    public void FailedParityStillRunsEveryIndependentRuntimeCheckAfterProjection()
    {
        using var fixture = new OfflineFixtures();
        bool projected = false;
        var ran = new List<string>();
        var plan = ReleaseCommands.CheckAllRuntimeDependencies([
            new("suite-output-parity", () => { projected = true; return new(1, "", "missing output"); }, []),
            new("runtime", () => { Assert.True(projected); ran.Add("runtime"); return Passed(); }, [])]);
        var rows = CheckScheduler.Run(plan, 1, fixture.DirectoryPath);
        Assert.Equal(new[] { "failed", "passed" }, rows.Select(row => row.Status));
        Assert.Equal(new[] { "runtime" }, ran);
    }

    [Fact]
    public void MeasuredSensitiveCasesAreBalancedAndStayOnTheirOriginalExclusiveWorkers()
    {
        using var fixture = new OfflineFixtures();
        var history = Path.Combine(fixture.DirectoryPath, "history"); Directory.CreateDirectory(history);
        var costs = Enumerable.Range(0, 245).ToDictionary(index =>
            "T:EngineHttpSessionTests.Shutdown_drains_an_open_http_request_and_stops_its_session_worker(release: \"" + index + "\")",
            index => index < 4 ? 34.0 : 1.6, StringComparer.Ordinal);
        const string producer = "M:FoundationChecks.CheckLaterToolUsage";
        const string portal = "T:PortedPortalAdmissionTests.Port_dispatch_takes_the_shared_worker_lane_before_journaling(row: 0)";
        costs.Add(producer, 38); costs.Add(portal, 57);
        foreach (int index in Enumerable.Range(0, 200)) costs.Add("T:Ordinary.Case(" + index + ")", 13);
        File.WriteAllText(Path.Combine(history, "foundation.json"), JsonSerializer.Serialize(costs));
        var first = SuiteCosts.Prepare("foundation", Path.Combine(fixture.DirectoryPath, "first"), history, null, 8, true, 2);
        var second = SuiteCosts.Prepare("foundation", Path.Combine(fixture.DirectoryPath, "second"), history, null, 8, true, 2);
        Assert.Equal(File.ReadAllBytes(first.Path), File.ReadAllBytes(second.Path));
        Assert.Equal(first.Costs, second.Costs);
        var assignments = File.ReadLines(first.Path).Select(line => line.Split('\t')).ToDictionary(
            fields => Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])), fields => int.Parse(fields[1]), StringComparer.Ordinal);
        int sensitive = first.SensitivePartitions!.Length, ordinary = first.Costs.Length - sensitive;
        Assert.True(sensitive > 2);
        Assert.True(first.Costs.Skip(ordinary).Max() < 2 * first.Costs.Take(ordinary).Max());
        Assert.Equal(costs.Values.Sum(), first.Costs.Sum(), 6);
        var actual = new Dictionary<string, int>();
        foreach (var pair in costs)
        {
            string cls = pair.Key == producer ? "FoundationChecks" : pair.Key == portal ? "PortedPortalAdmissionTests"
                : pair.Key.Contains("EngineHttpSessionTests", StringComparison.Ordinal) ? "EngineHttpSessionTests" : "Ordinary";
            int shard = TestShard.Assign(cls, pair.Key, pair.Key, first.Costs.Length, assignments, true, sensitive);
            actual.Add(pair.Key, shard);
            Assert.Equal(pair.Key.StartsWith("T:Ordinary", StringComparison.Ordinal), shard < ordinary);
        }
        Assert.Equal(costs.Count, actual.Count);
        Assert.Equal(costs.Keys.Order(), actual.Keys.Order());
        Assert.Equal(1, first.SensitivePartitions[actual[portal] - ordinary]);
        Assert.Equal(0, first.SensitivePartitions[actual[producer] - ordinary]);
        Assert.Equal(38, costs[producer]); // The legacy producer is one indivisible scheduling unit.
        var suite = new JsonObject { ["project"] = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj",
            ["minimumPassed"] = 100, ["maximumSkipped"] = 0, ["arguments"] = new JsonArray() };
        var jobs = new List<CheckJob>();
        FastSuites.AddJobs(jobs, fixture.DirectoryPath, "foundation", suite, Path.Combine(fixture.DirectoryPath, "jobs"), "dotnet", 8, null, [], history: history);
        var originalNames = jobs.Select(job => job.Name).ToArray();
        var layout = new CheckAffinity.CheckAllCpuLayout(0xff, 0x300, 0xc00, CheckAffinity.FullIsolation, null);
        var cores = Enumerable.Range(0, 12).Select(bit => 1UL << bit).ToArray();
        var lanes = CheckAffinity.AssignLanes(jobs, layout, 2, cores);
        Assert.Equal(originalNames, jobs.Select(job => job.Name));
        Assert.Equal(jobs.Take(jobs.Count - 1).Select(job => job.Name), jobs[^1].Dependencies);
        Assert.All(lanes.Values, capacity => Assert.Equal(1, capacity));
        var exclusive = jobs.Where(job => job.Exclusive).ToArray();
        Assert.Equal(sensitive, exclusive.Length);
        Assert.All(exclusive, job => Assert.Equal(0UL, job.Affinity!.Value & (layout.General | layout.Stability)));
        Assert.All(exclusive.GroupBy(job => job.SensitiveGroup), group => Assert.Single(group.Select(job => job.Lane).Distinct()));
        Assert.Equal(2, exclusive.Select(job => job.Lane).Distinct().Count());
        Assert.Equal(0UL, exclusive.First().Affinity!.Value & exclusive.Last().Affinity!.Value);
    }

    [Fact]
    public void PerformanceReportsMeasuredDependencyPathConcurrencyAndIdleTime()
    {
        var start = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
        CheckJob[] jobs = [new("build", Passed, []), new("left", Passed, ["build"]),
            new CheckJob("right", Passed, ["build"]) { Lane = "sensitive-0" }, new("gate", Passed, ["left", "right"])];
        CheckTiming Row(string name, int offset, int seconds) => new(name, "passed", 0, seconds, null) { StartedAtUtc = start.AddSeconds(offset) };
        CheckTiming[] rows = [Row("build", 0, 10), Row("left", 10, 20), Row("right", 10, 30), Row("gate", 40, 1)];
        var result = CheckScheduler.Performance(jobs, rows, start, 50);
        Assert.Equal(new[] { "build", "right", "gate" }, result.CriticalPath);
        Assert.Equal(41, result.CriticalPathSeconds);
        Assert.Equal(61, result.JobSeconds);
        Assert.Equal(1.22, result.AverageConcurrency, 8);
        Assert.Equal(2, result.PeakConcurrency);
        Assert.Equal(1.22, Assert.Single(result.Windows).AverageConcurrency, 8);
        Assert.Equal(30, result.Lanes.Single(lane => lane.Lane == "sensitive-0").JobSeconds);
    }

    [Fact]
    public void FileAppBuildGroupsCoverAllProgramsAndSerializeProjectReferenceWriters()
    {
        using var fixture = new OfflineFixtures();
        foreach (var name in new[] { "a", "b", "c", "d" }) File.WriteAllText(Path.Combine(fixture.DirectoryPath, name + ".cs"),
            name is "b" or "d" ? "#:project shared.csproj\nreturn 0;\n" : "return 0;\n");
        var programs = new[] { "d.cs", "c.cs", "b.cs", "a.cs" };
        var groups = FileBasedPrograms.BuildGroups(fixture.DirectoryPath, programs);
        Assert.Equal(programs.Order(), groups.SelectMany(group => group).Order());
        Assert.Equal(new[] { "b.cs", "d.cs" }, groups[0]);
        Assert.Equal(new[] { "a.cs", "c.cs" }, groups.Skip(1).Select(group => Assert.Single(group)));
        Assert.Equal(JsonSerializer.Serialize(groups), JsonSerializer.Serialize(FileBasedPrograms.BuildGroups(fixture.DirectoryPath, programs.Reverse())));
    }
}
