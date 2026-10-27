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

    [Theory]
    [InlineData("sensitive-0", true)]
    [InlineData("stability", false)]
    public void IdleReservedCpusAreLentThenDrainedBeforeTheirOwnerStarts(string reservedLane, bool exclusive)
    {
        using var fixture = new OfflineFixtures();
        using var borrowed = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int borrowing = 0; bool ownerRan = false;
        var jobs = new List<CheckJob> {
            new CheckJob("borrower", () => {
                Assert.Equal(3UL, CheckAffinity.Current); Interlocked.Exchange(ref borrowing, 1); borrowed.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10))); Interlocked.Exchange(ref borrowing, 0); return Passed();
            }, []) { Priority = 2 },
            new CheckJob("trigger", () => { Assert.True(borrowed.Wait(TimeSpan.FromSeconds(10))); return Passed(); }, []) { Priority = 1 },
            new CheckJob("owner", () => { Assert.Equal(0, Volatile.Read(ref borrowing)); Assert.Equal(2UL, CheckAffinity.Current); ownerRan = true; return Passed(); }, ["trigger"], Exclusive: exclusive)
                { Lane = reservedLane, Affinity = 2 },
            new("new-general", () => { Assert.Equal(1UL, CheckAffinity.Current); Assert.False(ownerRan); release.Set(); return Passed(); }, ["trigger"]),
            new("after", () => { Assert.Equal(3UL, CheckAffinity.Current); return Passed(); }, ["owner", "borrower", "new-general"]) };
        try
        {
            var rows = CheckScheduler.Run(jobs, 2, fixture.DirectoryPath, new Dictionary<string, int> { ["general"] = 2, [reservedLane] = 1 },
                packing: new(1, .5, 256UL << 20, 4UL << 30, 2));
            Assert.All(rows, row => Assert.True(CheckTiming.Passed(row), row.Error));
            Assert.Equal(jobs.Count, rows.Count); Assert.True(ownerRan);
            Assert.Equal("3", rows.Single(row => row.Name == "borrower").AffinityMask);
            Assert.Equal("1", rows.Single(row => row.Name == "new-general").AffinityMask);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void PackingUsesCpuSecondsPerWallSecondAndRespectsMemoryAndTheCap()
    {
        CheckResourceUsage[] history = [new(10, 100, 1UL << 30, "measured"), new(30, 100, 1UL << 30, "measured")];
        var plan = CheckPacking.Plan(3, history, 64UL << 30, 32);
        Assert.Equal(.2, plan.CpuPerJob, 8);
        Assert.Equal(10, plan.Capacity(3)); Assert.Equal(32, plan.Capacity(0xffff));
        Assert.Equal(2, CheckPacking.Plan(3, history, 4UL << 30, 32).Capacity(0xffff));
        Assert.Equal(1, CheckPacking.Plan(3, history, 4UL << 30, 32).Capacity(0xffff, 1));
        var jobs = new[] { new CheckJob("one", Passed, [], Exclusive: true) { SensitiveGroup = "a" },
            new CheckJob("two", Passed, [], Exclusive: true) { SensitiveGroup = "b" } };
        Assert.Equal(4, CheckPacking.ReservedCores(jobs, new Dictionary<string, CheckResourceUsage> { ["one"] = history[0], ["two"] = history[1] }, 2));
        Assert.Equal(2, CheckPacking.ReservedCores(jobs.Take(1), new Dictionary<string, CheckResourceUsage>(), 2));
    }

    [Fact]
    public void LegacyRunnerCpuIsApportionedAndNeverMislabelledAsOwnedJobCpu()
    {
        using var fixture = new OfflineFixtures();
        var start = DateTime.UtcNow;
        var rows = new[] { "one", "two" }.Select(name => new CheckTiming(name, "passed", 0, 10, null) {
            StartedAtUtc = start, CpuLoad = new(2, 0, [], 0) }).ToArray();
        var report = Path.Combine(fixture.DirectoryPath, "report.json"); File.WriteAllText(report, JsonSerializer.Serialize(new { checks = rows }));
        var history = CheckPacking.Read(fixture.DirectoryPath, report);
        Assert.Equal(2, history.Count);
        Assert.All(history.Values, row => { Assert.Equal(10, row.CpuSeconds); Assert.Equal(1, row.CoreDemand); Assert.Contains("apportioned", row.Source); });
    }

    [Fact]
    public void IndependentParityAndAllRuntimeChecksCanStartAfterTheBuild()
    {
        using var fixture = new OfflineFixtures(); using var barrier = new Barrier(2);
        CommandResult Both() { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return Passed(); }
        var plan = ReleaseCommands.CheckAllRuntimeDependencies([
            new("suite-output-parity", Both, ["build-once"]), new("foundation-api-shard-1", Both, []),
            new("repository", Passed, [])], independentParity: true);
        Assert.Equal(new[] { "build-once" }, plan[1].Dependencies);
        Assert.Equal(new[] { "build-once", "analyzer-warnings" }, plan[2].Dependencies);
        var rows = CheckScheduler.Run(plan, 2, fixture.DirectoryPath, prerequisites: [new("build-once", "passed", 0, 1, null), new("analyzer-warnings", "passed", 0, 1, null)]);
        Assert.All(rows, row => Assert.True(CheckTiming.Passed(row), row.Error));
        foreach (var name in new[] { "bundle", "python-repository" })
        {
            Assert.False(ReleaseCommands.CheckAllSourceOnly(name));
            var job = ReleaseCommands.CheckAllEarlyDependencies(new(name, Passed, []));
            Assert.Equal(new[] { "analyzer-warnings" }, job.Dependencies);
        }
    }

    [Fact]
    public void OrdinaryBaselineSourcesHaveIdenticalBytesAndIndependentWriters()
    {
        using var fixture = new OfflineFixtures();
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "input.cs"), "source bytes\n");
        var baseline = CheckAllLayout.CopyBaselineSources(fixture.DirectoryPath, ["input.cs"]);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, "input.cs")), File.ReadAllBytes(Path.Combine(baseline, "input.cs")));
        File.WriteAllText(Path.Combine(baseline, "input.cs"), "ordinary writer");
        Assert.Equal("source bytes\n", File.ReadAllText(Path.Combine(fixture.DirectoryPath, "input.cs")));
        Assert.Throws<ReleaseException>(() => CheckAllLayout.CopyBaselineSources(fixture.DirectoryPath, ["../outside.cs"]));
    }

    [Fact]
    public void OrdinaryBaselineBuildDoesNotTouchTheRuntimeOutputBeingRead()
    {
        using var fixture = new OfflineFixtures();
        var root = fixture.DirectoryPath;
        var project = "tests/unit/unit.csproj";
        Directory.CreateDirectory(Path.Combine(root, "tests/unit/bin/Release/net10.0"));
        File.WriteAllText(Path.Combine(root, project), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "tests/unit/Input.cs"), "public class Input {}\n");
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        var ordinary = Path.Combine(root, "tests/unit/bin/Release/net10.0/unit.dll");
        File.WriteAllText(ordinary, "runtime reader");
        using var locked = new FileStream(ordinary, FileMode.Open, FileAccess.Read, FileShare.Read);
        var baseline = CheckAllLayout.CopyBaselineSources(root, [project, "tests/unit/Input.cs", "offline.config"]);
        var result = CheckAllLayout.BuildInventories(baseline, [new(project, "", [])], "dotnet", Path.Combine(baseline, "offline.config"), root, Path.Combine(root, "receipts"), 1);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.True(File.Exists(Path.Combine(baseline, "tests/unit/bin/Release/net10.0/unit.dll")));
        Assert.Equal("runtime reader", File.ReadAllText(ordinary));
    }

    [Fact]
    public void CommandResourcesComeFromItsOwnedJobIncludingExitedChildren()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows command job accounting verification");
        using var fixture = new OfflineFixtures(); using var process = System.Diagnostics.Process.GetCurrentProcess();
        var mask = unchecked((ulong)process.ProcessorAffinity.ToInt64());
        var rows = CheckScheduler.Run([new CheckJob("command", () => ProcessRunner.Run("dotnet", ["--info"], fixture.DirectoryPath), []) { Affinity = mask }], 1, fixture.DirectoryPath);
        var row = Assert.Single(rows);
        Assert.True(CheckTiming.Passed(row), row.Error);
        Assert.NotNull(row.Resources); Assert.True(row.Resources.CpuSeconds > 0); Assert.True(row.Resources.PeakMemoryBytes > 0);
        Assert.Contains("owned command jobs", row.Resources.Source);
    }

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
