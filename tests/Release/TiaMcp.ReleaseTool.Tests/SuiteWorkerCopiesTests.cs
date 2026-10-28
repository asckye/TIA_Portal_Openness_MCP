using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class SuiteWorkerCopiesTests
{
    private static (CheckBuild Unit, string Manifest) Inputs(string root)
    {
        var unit = new CheckBuild(SuiteWorkerCopies.FoundationProject, "", []);
        var rows = new List<string>();
        foreach (var input in CheckAllBuild.WithTestFixtures([unit]))
        {
            string directory = Path.Combine(root, "built", Path.GetFileNameWithoutExtension(input.Project));
            Directory.CreateDirectory(Path.Combine(directory, "fixtures"));
            string assembly = Path.Combine(directory, Path.GetFileNameWithoutExtension(input.Project) + ".dll");
            File.WriteAllBytes(assembly, [0, 1, 2, 255]);
            File.WriteAllText(Path.ChangeExtension(assembly, ".exe"), "apphost");
            File.WriteAllText(Path.Combine(directory, "fixtures/input.json"), "{\"immutable\":true}");
            rows.Add(string.Join('\t', Path.Combine(root, input.Project), input.Variant, directory, directory, assembly));
        }
        string manifest = Path.Combine(root, "runtime.tsv"); File.WriteAllLines(manifest, rows);
        return (unit, manifest);
    }

    [Fact]
    public void WorkerCopiesMatchEveryBuiltByteAndKeepAllWritableTreesSeparate()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        var (unit, manifest) = Inputs(root); var copies = new SuiteWorkerCopies(root);
        var jobs = new List<CheckJob>();
        foreach (string name in new[] { "foundation-a", "foundation-api-b", "operator-c", "general-a", "general-b" })
        {
            var group = copies.Register(name, unit, Path.Combine(root, "results", name), unit.Project);
            jobs.Add(new(name, () => new(0, "", ""), [], Exclusive: !name.StartsWith("general", StringComparison.Ordinal)) {
                Lane = name is "foundation-a" or "operator-c" ? "sensitive-0" : name == "foundation-api-b" ? "sensitive-1" : "general", SensitiveGroup = group });
        }
        copies.Prepare(root, jobs, manifest);
        var first = copies.ForJob("foundation-a")!; var second = copies.ForJob("foundation-api-b")!;
        Assert.Equal(first, copies.ForJob("operator-c"));
        var runtimes = new[] { first, second, copies.ForJob("general-a")!, copies.ForJob("general-b")! };
        Assert.Equal(4, runtimes.Select(runtime => runtime.Assembly).Distinct().Count());
        Assert.Equal(4, runtimes.Select(runtime => runtime.Fixtures).Distinct().Count());
        var evidence = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "suite-worker-copies.json")))!;
        Assert.Equal(5, evidence["assignments"]!.AsArray().Count);
        Assert.Equal(5, evidence["assignments"]!.AsArray().Select(row => (string)row!["results"]!).Distinct().Count());
        Assert.Equal(48, evidence["files"]!.AsArray().Count);
        foreach (var row in evidence["files"]!.AsArray())
        {
            string source = (string)row!["source"]!, destination = (string)row["destination"]!;
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
            Assert.Equal(ReleaseRecords.HashFile(source), (string)row["sha256"]!);
            Assert.Equal(ReleaseRecords.HashFile(destination), (string)row["sha256"]!);
        }
        // Mutating an assembly-side identity, data directory or fixture in one
        // worker must not alter the source or any other worker's test inputs.
        foreach (var runtime in runtimes)
        {
            var paths = new[] { Path.Combine(Path.GetDirectoryName(runtime.Assembly)!, "TiaMcp.Adapter.21.dll"),
                Path.Combine(Path.GetDirectoryName(runtime.Assembly)!, "fixture-data/audit.json"),
                Path.Combine(runtime.Fixtures, "TiaMcp.PlcWorker.FaultFixture/TiaMcp.Adapter.21.dll") };
            foreach (string path in paths)
            {
                Assert.False(File.Exists(path)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, runtime.Assembly);
            }
        }
        Assert.All(evidence["files"]!.AsArray(), row => Assert.Equal((string)row!["sha256"]!, ReleaseRecords.HashFile((string)row["source"]!)));
        File.WriteAllBytes(first.Assembly, [9]);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(second.Assembly));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(first.RegisteredOutput));
    }

    [Fact]
    public void SplitFoundationJobsUseAllExclusiveWorkersWhileUnauditedGroupsStaySerial()
    {
        using var fixture = new OfflineFixtures(); var copies = new SuiteWorkerCopies(fixture.DirectoryPath);
        var unit = new CheckBuild(SuiteWorkerCopies.FoundationProject, "", []);
        var other = new CheckBuild("tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj", "", []);
        var jobs = new List<CheckJob>();
        for (int i = 0; i < 9; i++)
        {
            string name = "foundation-" + i;
            jobs.Add(new(name, () => new(0, "", ""), [], Exclusive: true) {
                SensitiveGroup = copies.Register(name, unit, Path.Combine(fixture.DirectoryPath, name), unit.Project), MeasuredSeconds = 100 - i });
        }
        for (int i = 0; i < 2; i++)
        {
            string name = "gui-" + i;
            jobs.Add(new(name, () => new(0, "", ""), [], Exclusive: true) {
                SensitiveGroup = copies.Register(name, other, Path.Combine(fixture.DirectoryPath, name), other.Project), MeasuredSeconds = 10 });
        }
        var topology = Enumerable.Range(0, 24).Select(i => 1UL << i).ToArray();
        var workers = CheckAffinity.PlanCheckAllWorkers(0xFFFFFF, 6, false, 3, false, topology, 3, false, 4);
        var lanes = CheckAffinity.AssignLanes(jobs, workers.Cpu, 3, topology);
        Assert.Equal(3, jobs.Where(job => job.Name.StartsWith("foundation", StringComparison.Ordinal)).Select(job => job.Lane).Distinct().Count());
        Assert.Single(jobs.Where(job => job.Name.StartsWith("gui", StringComparison.Ordinal)).Select(job => job.Lane).Distinct());
        Assert.All(lanes.Values, capacity => Assert.Equal(1, capacity));
        var masks = jobs.GroupBy(job => job.Lane).Select(group => group.First().Affinity!.Value).ToArray();
        Assert.Equal(3, masks.Distinct().Count());
        for (int i = 0; i < masks.Length; i++)
        {
            Assert.True(System.Numerics.BitOperations.PopCount(masks[i]) >= 2);
            Assert.Equal(0UL, masks[i] & (workers.Cpu.General | workers.Cpu.Stability));
            for (int j = 0; j < i; j++) Assert.Equal(0UL, masks[i] & masks[j]);
        }
    }

    [Fact]
    public void RegisteredFoundationShardsRunCopiedAssembliesWithDistinctResultsAndUnchangedFilters()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        var (_, manifest) = Inputs(root); var copies = new SuiteWorkerCopies(root); var jobs = new List<CheckJob>();
        var seen = new List<(string Assembly, string Results, string Temp, string Pipe)>();
        var suite = new JsonObject { ["project"] = SuiteWorkerCopies.FoundationProject, ["minimumPassed"] = 1, ["maximumSkipped"] = 0,
            ["arguments"] = new JsonArray("--filter", "FullyQualifiedName!~Operator") };
        CommandResult Run(string exe, IEnumerable<string> arguments, string working, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray(); Assert.Equal("vstest", args[0]);
            Assert.Contains("--TestCaseFilter:FullyQualifiedName!~Operator", args); Assert.Contains(SuiteHang.VstestArgument, args);
            Assert.Contains(args, arg => arg.EndsWith(";TraceLevel=Info", StringComparison.Ordinal));
            string directory = args.Single(arg => arg.StartsWith("--ResultsDirectory:", StringComparison.Ordinal))[19..];
            Assert.NotNull(environment); string fixtures = environment!["TIA_MCP_TEST_WORKER_FIXTURE_ROOT"]!;
            Assert.True(File.Exists(Path.Combine(fixtures, "TiaMcp.PlcWorker.FaultFixture/TiaMcp.PlcWorker.FaultFixture.exe")));
            Assert.DoesNotContain(args, arg => arg.StartsWith("-p:", StringComparison.Ordinal));
            seen.Add((args[1], directory, environment["TEMP"]!, environment["TIA_MCP_TEST_PIPE_SCOPE"]!));
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(directory, "foundation.trx"), ["Passed"]);
            return new(0, "", "");
        }
        FastSuites.AddJobs(jobs, root, "foundation", suite, Path.Combine(root, "results"), "dotnet", 2, null, [],
            manifest: manifest, process: Run, diagnostics: true, workerCopies: copies);
        var shards = jobs.Where(job => !job.Name.EndsWith("-gate", StringComparison.Ordinal)).ToArray();
        Assert.All(shards, job => Assert.Contains("#isolated-" + job.Name, job.SensitiveGroup!));
        for (int i = 0; i < jobs.Count; i++) if (jobs[i].Exclusive) jobs[i] = jobs[i] with { Lane = "sensitive-" + i % 3 };
        copies.Prepare(root, jobs, manifest);
        foreach (var job in jobs.Where(job => !job.Name.EndsWith("-gate", StringComparison.Ordinal))) Assert.Equal(0, job.Run().ExitCode);
        Assert.Equal(shards.Length, seen.Count);
        Assert.Equal(shards.Length, seen.Select(item => item.Results).Distinct().Count());
        Assert.Equal(shards.Length, seen.Select(item => item.Temp).Distinct().Count());
        Assert.Equal(shards.Length, seen.Select(item => item.Pipe).Distinct().Count());
        Assert.All(seen, item => Assert.Contains(Path.Combine(root, "bin-build/w"), item.Assembly));
        var gate = Assert.Single(jobs, job => job.Name.EndsWith("-gate", StringComparison.Ordinal));
        Assert.True(shards.Select(job => job.Name).ToHashSet().SetEquals(gate.Dependencies));
        Assert.True(gate.AllowFailedDependencies);
    }

    [Fact]
    public void FixtureAndRegisteredOutputLocatorsRespectOnlyTheirIsolatedProcessOverrides()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        string? previousFixtures = Environment.GetEnvironmentVariable("TIA_MCP_TEST_WORKER_FIXTURE_ROOT");
        string? previousOutput = Environment.GetEnvironmentVariable("TIA_MCP_TEST_REGISTERED_OUTPUT");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_WORKER_FIXTURE_ROOT", root);
            Assert.Equal(Path.Combine(root, "Plc", "fixture.exe"), TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("Plc", "fixture.exe", "net48"));
            string bin = Path.Combine(root, "tests/FoundationHost/TiaMcp.FoundationHost.Tests/bin");
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_REGISTERED_OUTPUT", Path.Combine(bin, "check-all/TiaMcp.FoundationHost.Tests/default/Release/net10.0"));
            Assert.Equal(Path.GetFullPath(Path.Combine(bin, "Release/net10.0")), TiaMcp.Engine.Tests.Shared.TestPaths.RegisteredOutput(AppContext.BaseDirectory));
            Assert.Equal(root, TiaMcp.Engine.Tests.Shared.TestPaths.RegisteredOutput(root));
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_WORKER_FIXTURE_ROOT", null);
            string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            Assert.Equal(Path.Combine(repository, "tests", "WorkerChannel", "Plc", "bin", "Release", "net48", "fixture.exe"),
                TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("Plc", "fixture.exe", "net48"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_WORKER_FIXTURE_ROOT", previousFixtures);
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_REGISTERED_OUTPUT", previousOutput);
        }
    }

    [Fact]
    public void DuplicateResultDirectoriesAreRefusedBeforeAnyTestsStart()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        var (unit, manifest) = Inputs(root); var copies = new SuiteWorkerCopies(root);
        foreach (string name in new[] { "foundation", "foundation-api" })
            copies.Register(name, unit, Path.Combine(root, "results"), unit.Project);
        var jobs = new[] { new CheckJob("foundation", () => throw new InvalidOperationException("must not start"), []),
            new CheckJob("foundation-api", () => throw new InvalidOperationException("must not start"), []) };
        Assert.Contains("Colliding Foundation results", Assert.Throws<ReleaseException>(() => copies.Prepare(root, jobs, manifest)).Message);
    }

    [Fact]
    public void MissingBuiltFixtureOrUnpreparedCopyFailsInsteadOfUsingSharedOutput()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        var (unit, manifest) = Inputs(root); var copies = new SuiteWorkerCopies(root);
        string group = copies.Register("foundation", unit, Path.Combine(root, "results"), unit.Project);
        Assert.Throws<ReleaseException>(() => copies.ForJob("foundation"));
        Assert.Null(copies.ForJob("other-assembly"));
        File.WriteAllLines(manifest, File.ReadAllLines(manifest).Where(line => !line.Contains("PlcWorker.FaultFixture", StringComparison.Ordinal)).ToArray());
        Assert.Throws<ReleaseException>(() => copies.Prepare(root, [new("foundation", () => new(0, "", ""), []) { SensitiveGroup = group }], manifest));
    }
}
