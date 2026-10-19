using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;
using Xunit.Abstractions;

namespace TiaMcp.ReleaseTool.Tests;

// check-all on machines too small for the reserved whole-core lanes (P8-72b):
// a recorded degraded layout instead of a refusal, unless a lane size is explicit.
public sealed class CheckAllCpuLayoutTests(ITestOutputHelper output)
{
    // Synthetic topologies: two SMT siblings per physical core, or one CPU per core.
    private static ulong[] Smt(int physical) => Enumerable.Range(0, physical).Select(core => 3UL << (2 * core)).ToArray();
    private static ulong[] SingleThreaded(int physical) => Enumerable.Range(0, physical).Select(core => 1UL << core).ToArray();

    private static ulong[] Topology(string name) => name switch
    {
        "smt-1" => Smt(1), "single-2" => SingleThreaded(2), "smt-2" => Smt(2), "single-4" => SingleThreaded(4), "smt-3" => Smt(3),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static int DefaultStability(ulong available) => Math.Min(8, Math.Max(1, BitOperations.PopCount(available) / 3));

    [Theory]
    // 2 logical CPUs: one SMT core shares everything; two single-thread cores keep a stability core.
    [InlineData(0x3UL, "smt-1", null, null, 0x0UL, 0x3UL, 0x3UL, "no reserved lanes")]
    [InlineData(0x3UL, "single-2", null, null, 0x0UL, 0x2UL, 0x1UL, "no reserved sensitive lane")]
    // 4 logical CPUs on two SMT cores (GitHub's Windows runner): no reserved sensitive lane,
    // with the default or the explicit whole-core stability size of the CI evidence.
    [InlineData(0xFUL, "smt-2", null, null, 0x0UL, 0xCUL, 0x3UL, "no reserved sensitive lane")]
    [InlineData(0xFUL, "smt-2", null, 2, 0x0UL, 0xCUL, 0x3UL, "no reserved sensitive lane")]
    // 6 logical CPUs on three SMT cores: resized to whole cores and still disjoint, or an
    // explicit lane kept exactly while the defaulted lane gives way.
    [InlineData(0x3FUL, "smt-3", null, null, 0x3UL, 0xCUL, 0x30UL, "lanes resized to whole physical cores and still disjoint: sensitive 2 CPUs, stability 2 CPUs")]
    [InlineData(0x3FUL, "smt-3", 4, null, 0xFUL, 0x30UL, 0x30UL, "the stability lane shares the general lane's 2 CPUs; the sensitive lane keeps 4 CPUs")]
    [InlineData(0x3FUL, "smt-3", null, 4, 0x0UL, 0x3CUL, 0x3UL, "no reserved sensitive lane")]
    public void SmallMachinesRunADegradedLayoutInsteadOfRefusing(ulong available, string topology, int? sensitiveCores, int? stabilityCores,
        ulong sensitive, ulong stability, ulong general, string reason)
    {
        var physical = Topology(topology);
        int requestedSensitive = sensitiveCores ?? CheckAffinity.DefaultSensitiveCores(available), requestedStability = stabilityCores ?? DefaultStability(available);
        // The strict layout refuses this machine; that refusal is what the degraded layout replaces.
        Assert.Equal(64, Assert.Throws<ReleaseException>(() => CheckAffinity.SplitPreferred(
            CheckAffinity.SplitPreferred(available, requestedSensitive, physical).General, requestedStability, physical)).ExitCode);

        var layout = CheckAffinity.PlanCheckAll(available, requestedSensitive, sensitiveCores != null, requestedStability, stabilityCores != null, physical);

        Assert.Equal((sensitive, stability, general), (layout.Sensitive, layout.Stability, layout.General));
        Assert.Equal(CheckAffinity.DegradedIsolation, layout.Isolation);
        Assert.Contains("cannot hold the " + (sensitiveCores == null ? "default" : "requested") + " " + requestedSensitive + "-CPU sensitive lane", layout.Reason);
        Assert.Contains(reason, layout.Reason);
        AssertUsable(available, physical, layout);
        // An explicit lane keeps its exact size on whole cores and shares no CPU.
        if (sensitiveCores is { } sensitiveSize)
        {
            Assert.Equal(sensitiveSize, BitOperations.PopCount(layout.Sensitive));
            Assert.Equal(0UL, layout.Sensitive & (layout.Stability | layout.General));
        }
        if (stabilityCores is { } stabilitySize)
        {
            Assert.Equal(stabilitySize, BitOperations.PopCount(layout.Stability));
            Assert.Equal(0UL, layout.Stability & (layout.Sensitive | layout.General));
        }
    }

    [Fact]
    public void MachinesThatHoldTheRequestedLanesKeepTheFullLayout()
    {
        // The 24-core host's defaults, and explicit sizes that fit whole SMT cores.
        var host = CheckAffinity.PlanCheckAll(0xFFFFFF, 8, false, 8, false, SingleThreaded(24));
        Assert.Equal(new CheckAffinity.CheckAllCpuLayout(0xFF, 0xFF00, 0xFF0000, CheckAffinity.FullIsolation, null), host);
        var explicitSmt = CheckAffinity.PlanCheckAll(0x3F, 2, true, 2, true, Smt(3));
        Assert.Equal(new CheckAffinity.CheckAllCpuLayout(0x3, 0xC, 0x30, CheckAffinity.FullIsolation, null), explicitSmt);
        // Four single-thread CPUs hold the default 2/1/1 split.
        Assert.Equal(new CheckAffinity.CheckAllCpuLayout(0x3, 0x4, 0x8, CheckAffinity.FullIsolation, null),
            CheckAffinity.PlanCheckAll(0xF, CheckAffinity.DefaultSensitiveCores(0xF), false, DefaultStability(0xF), false, SingleThreaded(4)));
    }

    [Theory]
    // Both sizes explicit: today's strict refusal and message, even where a degraded layout exists.
    [InlineData(0xFUL, "smt-2", 2, 2, "-StabilityCores must fit whole available physical cores and leave CPUs for other checks")]
    [InlineData(0x3FUL, "smt-3", 4, 2, "-StabilityCores must fit whole available physical cores and leave CPUs for other checks")]
    [InlineData(0x3UL, "single-2", 1, 1, "-StabilityCores must fit whole available physical cores and leave CPUs for other checks")]
    // One size explicit: kept exactly or refused; it is never resized or shared.
    [InlineData(0xFUL, "smt-2", null, 1, "-StabilityCores 1 must fit whole available physical cores")]
    [InlineData(0xFUL, "smt-2", null, 4, "-StabilityCores 4 must fit whole available physical cores")]
    [InlineData(0x3FUL, "smt-3", 3, null, "-SensitiveCores 3 must fit whole available physical cores")]
    [InlineData(0x3UL, "smt-1", 2, null, "-SensitiveCores 2 must fit whole available physical cores")]
    [InlineData(0x3UL, "smt-1", null, 2, "-StabilityCores 2 must fit whole available physical cores")]
    public void ExplicitCoreOptionsKeepTheStrictRefusal(ulong available, string topology, int? sensitiveCores, int? stabilityCores, string message)
    {
        var error = Assert.Throws<ReleaseException>(() => CheckAffinity.PlanCheckAll(available, sensitiveCores ?? CheckAffinity.DefaultSensitiveCores(available), sensitiveCores != null,
            stabilityCores ?? DefaultStability(available), stabilityCores != null, Topology(topology)));
        Assert.Equal(64, error.ExitCode);
        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x3UL, "smt-1")]
    [InlineData(0x3UL, "single-2")]
    [InlineData(0xFUL, "smt-2")]
    [InlineData(0x3FUL, "smt-3")]
    public void DegradedLanesPassSchedulerValidationAndRunEveryCheck(ulong available, string topology)
    {
        var physical = Topology(topology);
        var layout = CheckAffinity.PlanCheckAll(available, CheckAffinity.DefaultSensitiveCores(available), false, DefaultStability(available), false, physical);
        Assert.Equal(CheckAffinity.DegradedIsolation, layout.Isolation);
        using var fixture = new OfflineFixtures();
        var ran = new System.Collections.Concurrent.ConcurrentBag<string>();
        CommandResult Run(string name) { ran.Add(name); return new(0, "", ""); }
        var jobs = new List<CheckJob>
        {
            new("suite-shard-0", () => Run("suite-shard-0"), []),
            new("suite-shard-1", () => Run("suite-shard-1"), []),
            new CheckJob("sensitive-one", () => Run("sensitive-one"), [], Exclusive: true) { SensitiveGroup = "one.dll", EstimatedSeconds = 20 },
            new CheckJob("sensitive-two", () => Run("sensitive-two"), [], Exclusive: true) { SensitiveGroup = "two.dll", EstimatedSeconds = 10 },
            new CheckJob("stability-one", () => Run("stability-one"), []) { Lane = "stability", Affinity = layout.Stability },
            new("gate", () => Run("gate"), ["suite-shard-0", "suite-shard-1", "sensitive-one", "sensitive-two", "stability-one"])
        };
        var sensitiveLanes = CheckAffinity.AssignLanes(jobs, layout, 2, physical);
        int generalCapacity = BitOperations.PopCount(layout.General);
        var lanes = new Dictionary<string, int>(sensitiveLanes) { ["general"] = generalCapacity,
            ["stability"] = Math.Min(4, BitOperations.PopCount(layout.Stability)) };
        foreach (var job in jobs.Where(job => job.Exclusive))
            if (layout.Sensitive == 0) { Assert.Equal("general", job.Lane); Assert.Equal(layout.General, job.Affinity); }
            else { Assert.StartsWith("sensitive-", job.Lane, StringComparison.Ordinal); Assert.Equal(job.Affinity!.Value, job.Affinity!.Value & layout.Sensitive); }
        Assert.All(jobs.Where(job => !job.Exclusive && job.Lane == "general"), job => Assert.Equal(layout.General, job.Affinity));
        Assert.All(jobs, job => Assert.NotEqual(0UL, job.Affinity!.Value));
        var rows = CheckScheduler.Run(jobs, generalCapacity, fixture.DirectoryPath, lanes);
        Assert.All(rows, row => Assert.Equal("passed", row.Status));
        Assert.Equal(jobs.Select(job => job.Name).Order(StringComparer.Ordinal), ran.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TwoCpuCheckAllRecordsTheDegradedLayoutAndKeepsEveryCheck()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Program.cs"), "using System.IO; if (args[0] == \"msbuild\") return 1; if (args.Length > 1 && args[1].StartsWith(\"generate-\")) return 0; File.WriteAllText(\"unexpected-check\", string.Join(\"|\", args)); return 2;");
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        ProcessRunner.RequireSuccess(ProcessRunner.Run(dotnet, ["build", Path.Combine(root, "fake.csproj"), "-c", "Release", "-m:1", "-nodeReuse:false",
            "-p:RestoreConfigFile=" + Path.Combine(root, "offline.config"), "-p:UseSharedCompilation=false"], root), "fake compiler fixture");
        using var current = Process.GetCurrentProcess();
        ulong available = OperatingSystem.IsWindows() ? unchecked((ulong)current.ProcessorAffinity.ToInt64())
            : throw new PlatformNotSupportedException("Windows check-all fixture");
        // Any one or two CPUs are too few for whole-core sensitive and stability lanes plus a general CPU.
        ulong mask = 0;
        for (int bit = 0; bit < 64 && BitOperations.PopCount(mask) < 2; bit++) mask |= available & (1UL << bit);
        var runDirectory = Path.Combine(root, "degraded-run");
        CommandResult result;
        using (CheckAffinity.Enter(mask))
            result = ProcessRunner.Run(dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", "-PublicApiRoot", root,
                "-NuGetConfig", Path.Combine(root, "offline.config"), "-Dotnet", Path.Combine(root, "bin/Release/net10.0/fake.exe"),
                "-CompanionPython", Path.Combine(root, "bin/Release/net10.0/fake.exe"), "-BuildParallelism", "1", "-MaxParallelism", "16", "-OutputDirectory", runDirectory], Repository.FindRoot());
        output.WriteLine("Degraded fake build: available mask={0:X}, check-all mask={1:X}", available, mask);
        output.WriteLine(result.StandardOutput + result.StandardError);
        // The failed fake build, not a CPU refusal (exit 64), ends the run.
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("check-all: cpuIsolation degraded: ", result.StandardOutput, StringComparison.Ordinal);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(runDirectory, "report.json")))!;
        Assert.Equal(CheckAffinity.DegradedIsolation, (string?)report["cpuIsolation"]);
        var reason = (string?)report["cpuIsolationReason"];
        Assert.StartsWith(BitOperations.PopCount(mask) + " available CPU", reason, StringComparison.Ordinal);
        var checks = report["checks"]!.AsArray();
        Assert.Equal(new[] { "generate-catalogs", "generate-tool-usage", "build-once", "tracked-tree-drift" }, checks.Select(row => (string)row!["Name"]!));
        Assert.Equal("failed", (string)checks[2]!["Status"]!);
        var comparison = File.ReadAllLines(Path.Combine(runDirectory, "comparison.md"));
        Assert.Equal(new[] { "cpuIsolation: degraded", "cpuIsolationReason: " + reason }, comparison.Take(2));
        Assert.False(File.Exists(Path.Combine(Repository.FindRoot(), "unexpected-check")));
    }

    [Fact]
    public void CheckAllRefusesExplicitCoreOptionsThatDoNotFitBeforeAnyCheck()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        var companion = Path.Combine(root, "python.exe"); File.WriteAllText(companion, "fixture");
        using var current = Process.GetCurrentProcess();
        ulong available = OperatingSystem.IsWindows() ? unchecked((ulong)current.ProcessorAffinity.ToInt64())
            : throw new PlatformNotSupportedException("Windows check-all fixture");
        string cpus = BitOperations.PopCount(available).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        foreach (var (name, options, message) in new[] {
            ("both", new[] { "-SensitiveCores", cpus, "-StabilityCores", "1" }, "-StabilityCores must fit whole available physical cores and leave CPUs for other checks"),
            ("stability", new[] { "-StabilityCores", cpus }, "-StabilityCores " + cpus + " must fit whole available physical cores") })
        {
            var runDirectory = Path.Combine(root, name);
            var result = ProcessRunner.Run(dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", "-PublicApiRoot", root, "-NuGetConfig", Path.Combine(root, "offline.config"),
                "-Dotnet", Path.Combine(root, "missing-dotnet.exe"), "-CompanionPython", companion, .. options, "-OutputDirectory", runDirectory], Repository.FindRoot());
            output.WriteLine(name + ": " + result.StandardOutput + result.StandardError);
            Assert.Equal(64, result.ExitCode);
            Assert.Contains(message, result.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(runDirectory, "report.json")));
            Assert.False(Directory.Exists(Path.Combine(runDirectory, "logs")));
        }
    }

    private static void AssertUsable(ulong available, ulong[] physical, CheckAffinity.CheckAllCpuLayout layout)
    {
        // Every lane has CPUs, uses only available CPUs and together they use all of them.
        Assert.NotEqual(0UL, layout.General); Assert.NotEqual(0UL, layout.Stability);
        Assert.Equal(0UL, (layout.Sensitive | layout.Stability | layout.General) & ~available);
        Assert.Equal(available, layout.Sensitive | layout.Stability | layout.General);
        // A reserved lane never splits a physical core, and reserved lanes never overlap.
        foreach (var lane in new[] { layout.Sensitive, layout.Stability == layout.General ? 0 : layout.Stability })
            Assert.All(physical, core => Assert.True((lane & core & available) == 0 || (lane & core & available) == (core & available)));
        Assert.Equal(0UL, layout.Sensitive & layout.Stability);
        Assert.Equal(0UL, layout.Sensitive & layout.General);
    }
}
