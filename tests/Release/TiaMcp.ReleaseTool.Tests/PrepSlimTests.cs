using System.Text.Json.Nodes;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PrepSlimTests
{
    [Theory]
    [InlineData("foundation-host", "runtime/v14sp1/TiaMcp.FoundationHost.exe")]
    [InlineData("studio", "runtime/studio/TiaOpenness.exe")]
    public void BundledRuntimeJobsWorkWithAFreshLogsDirectory(string name, string executable)
    {
        using var tree = new StaticTree();
        var context = new PrepareContext(tree.Root, "4.0.0-beta.3", "sdk", null, "python", "companion",
            Path.Combine(tree.Root, "prepared"), new(2026, 11, 4), 1);
        Directory.CreateDirectory(context.Logs);
        var output = Path.Combine(context.Logs, "tests");
        Assert.False(Directory.Exists(output));
        var bundle = Path.Combine(tree.Root, "bundle");
        int calls = 0;
        var plan = ReleaseCommands.PrepareCheckJobs(context, bundle,
            new Dictionary<string, string?> { ["TEMP"] = Path.Combine(tree.Root, "temp") },
            (file, _, cwd, environment, _) =>
            {
                calls++;
                Assert.Equal(Path.Combine(bundle, executable), file);
                Assert.Equal(bundle, cwd);
                Assert.Equal("1", environment["COREHOST_TRACE"]);
                File.WriteAllText(environment["COREHOST_TRACEFILE"]!, "Resolved fxr ["
                    + Path.GetFullPath(Path.Combine(bundle, "runtime/dotnet/host/fxr"))
                    + Path.DirectorySeparatorChar + "10.0.0/hostfxr.dll]");
                return new(0, "", "");
            });

        var job = Assert.Single(plan.Jobs, job => job.Name == "bundled-runtime-" + name);
        Assert.Equal(0, job.Run().ExitCode);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(output, "corehost-" + name + ".txt")));
    }

    [Fact]
    public void PrepareHasExactlyThePackageChecks()
    {
        using var tree = new StaticTree();
        var context = new PrepareContext(tree.Root, "4.0.0-beta.3", "sdk", null, "python", "companion",
            Path.Combine(tree.Root, "prepared"), new(2026, 11, 4), 1);
        var plan = ReleaseCommands.PrepareCheckJobs(context, Path.Combine(tree.Root, "bundle"),
            new Dictionary<string, string?> { ["TEMP"] = Path.Combine(tree.Root, "temp") });
        var expected = new[] { "product-smoke", "relocated-bundle", "bundled-runtime-foundation-host", "bundled-runtime-studio",
            "bundle-write-guard", "foundation-approval", "approval-v20-bundle", "approval-v21-bundle",
            "native-mcp-session-self-test", "nativelifecycle-self-test" }
            .Concat(PlcWorkerFixtures.Keys.SelectMany(key => new[] { "weave-v" + key + "-bundle", "contracts-v" + key }));
        Assert.Equal(expected.Order(), plan.Jobs.Select(job => job.Name).Order());
    }

    [Theory]
    [InlineData("src/WorkerChannel/Channel.cs", false, true)]
    [InlineData("scripts/checks/Test-FoundationTransport.py", false, true)]
    [InlineData("tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/Program.cs", false, true)]
    [InlineData("docs/manuals/release.md", false, false)]
    [InlineData("src/Studio/Gui/View.cs", false, false)]
    [InlineData("Directory.Build.props", false, true)]
    [InlineData("", true, true)]
    public void TransportSelectionIncludesItsBuilds(string path, bool all, bool selected)
    {
        var closures = new Dictionary<string, HashSet<string>> { ["worker-channel"] = new() { path } };
        var options = Options.Parse(all ? ["-All"] : [], Program.BooleanOptions);
        var plan = MergeCheck.Select(path.Length == 0 ? [] : [path], options.Has("All"), new JsonObject(), closures,
            (_, _) => false, [], "dotnet", "python", "sdk", "output");
        foreach (var name in new[] { "foundation-transport", "foundation-transport-plc" })
            Assert.Equal(selected, plan.Checks.Any(job => job.Name == name));
        if (!selected) return;
        Assert.True(plan.Table);
        Assert.Contains("src/FoundationHost/TiaMcp.FoundationHost.csproj", plan.Builds.Keys);
        Assert.Contains("tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/TiaMcp.WorkerChannel.TransportFixture.csproj", plan.Builds.Keys);
        if (all) Assert.Contains(plan.Checks, job => job.Name == "configuration-page");
        Assert.All(plan.Checks.Where(job => job.Name.StartsWith("foundation-transport", StringComparison.Ordinal)),
            job =>
            {
                Assert.Contains("--host-exe", job.Arguments);
                Assert.True(job.PostSuite);
                Assert.False(job.Serial);
                Assert.True(job.Isolated);
            });
    }

    [Theory]
    [InlineData(false, 1, "1")]
    [InlineData(false, 16, "8")]
    [InlineData(true, 1, "1")]
    [InlineData(true, 16, "8")]
    public void TransportChecksRespectParallelismAndKeepFullProfile(bool all, int parallelism, string expected)
    {
        const string path = "src/WorkerChannel/Channel.cs";
        var plan = MergeCheck.Select([path], all, new JsonObject(),
            new Dictionary<string, HashSet<string>> { ["worker-channel"] = new() { path } },
            (_, _) => false, [], "dotnet", "python", "sdk", "output", parallelism);
        foreach (var name in new[] { "foundation-transport", "foundation-transport-plc" })
        {
            var job = Assert.Single(plan.Checks, job => job.Name == name);
            Assert.DoesNotContain("--check-profile", job.Arguments);
            var index = Array.IndexOf(job.Arguments, "--max-parallel");
            Assert.InRange(index, 0, job.Arguments.Length - 2);
            Assert.Equal(expected, job.Arguments[index + 1]);
        }
    }

    [Theory]
    [InlineData(0, 16, 4)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 3, 3)]
    [InlineData(1, 16, 4)]
    public void SocketExhaustionFailsAndPersistsTheFourReleaseFallback(int exitCode, int parallelism, int expected)
    {
        using var tree = new StaticTree();
        var output = Path.Combine(tree.Root, "bin-build/merge-check");
        var fallback = MergeCheck.TransportFallbackPath(tree.Root, _ => new(0, Path.Combine(tree.Root, ".git"), ""));
        Directory.CreateDirectory(output);
        Assert.Equal(Math.Min(8, parallelism), MergeCheck.TransportParallelism(fallback, parallelism));
        var failure = new CommandResult(exitCode, "", "urllib.error.URLError: <urlopen error [WinError 10055] No buffer space>");
        var result = MergeCheck.CheckResult("foundation-transport-plc", failure, output, fallback);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(failure.StandardError, result.StandardError);
        Assert.Contains("Subsequent merge checks use at most 4", result.StandardError);
        Assert.True(File.Exists(fallback));
        Directory.Delete(output, true);
        var plan = MergeCheck.Select([], true, new JsonObject(), new Dictionary<string, HashSet<string>>(),
            (_, _) => false, [], "dotnet", "python", "sdk", output, parallelism, fallback);
        foreach (var job in plan.Checks.Where(job => job.Name.StartsWith("foundation-transport", StringComparison.Ordinal)))
            Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), job.Arguments[Array.IndexOf(job.Arguments, "--max-parallel") + 1]);
    }

    [Fact]
    public void OtherTransportFailuresKeepTheApprovedLimit()
    {
        using var tree = new StaticTree();
        var output = Path.Combine(tree.Root, "merge-check");
        var fallback = MergeCheck.TransportFallbackPath(tree.Root, _ => new(0, Path.Combine(tree.Root, ".git"), ""));
        var failure = new CommandResult(1, "", "[WinError 10048] Address already in use");
        Assert.Equal(failure, MergeCheck.CheckResult("foundation-transport", failure, output, fallback));
        Assert.Equal(8, MergeCheck.TransportParallelism(fallback, 16));
        Assert.False(File.Exists(fallback));
    }

    [Fact]
    public void SocketExhaustionFallbackSurvivesTheOriginatingWorktree()
    {
        using var tree = new StaticTree();
        var first = Path.Combine(tree.Root, "worktree-one");
        var second = Path.Combine(tree.Root, "worktree-two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        CommandResult Git(string[] args)
        {
            Assert.Equal(new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, args);
            return new(0, Path.Combine(tree.Root, "main", ".git") + "\n", "");
        }
        var fallback = MergeCheck.TransportFallbackPath(first, Git);
        Assert.Equal(Path.Combine(tree.Root, "main", "bin-build", "transport-parallelism-4.txt"), fallback);
        var failure = new CommandResult(1, "", "[WinError 10055] No buffer space");
        Assert.Equal(1, MergeCheck.CheckResult("foundation-transport", failure, Path.Combine(first, "bin-build/merge-check"), fallback).ExitCode);
        Assert.StartsWith(tree.Root + Path.DirectorySeparatorChar, first, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(first, true);
        var shared = MergeCheck.TransportFallbackPath(second, Git);
        Assert.Equal(fallback, shared);
        Assert.True(File.Exists(shared));
        var plan = MergeCheck.Select([], true, new JsonObject(), new Dictionary<string, HashSet<string>>(),
            (_, _) => false, [], "dotnet", "python", "sdk", Path.Combine(second, "bin-build/merge-check"), 16, shared);
        var transports = plan.Checks.Where(job => job.Name.StartsWith("foundation-transport", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, transports.Length);
        Assert.All(transports, job => Assert.Equal("4", job.Arguments[Array.IndexOf(job.Arguments, "--max-parallel") + 1]));
    }

    [Theory]
    [InlineData("src/WorkerChannel/channel.cs", true)]
    [InlineData("src/Adapters/Native/tool.cs", true)]
    [InlineData("reference/tool-catalog/tools.json", true)]
    [InlineData("build-tools/release/ToolUsageGenerator.cs", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("src/Studio/Gui/View.cs", false)]
    public void WorkerChecksKeepNativeSafetyUsageAndCampaignInputs(string path, bool selected)
    {
        var plan = MergeCheck.Select([path], false, new JsonObject(),
            new Dictionary<string, HashSet<string>> { ["worker-channel"] = new() { path } },
            (_, _) => false, [], "dotnet", "python", "sdk", "output");
        Assert.Equal(selected, plan.Builds.ContainsKey(MergeCheck.ApiCompileProject));
        Assert.Equal(selected, plan.Builds.ContainsKey(MergeCheck.AdapterDiagnosticsProject));
        foreach (var key in new[] { "20", "21" })
        {
            Assert.Equal(selected, plan.Builds.ContainsKey($"tests/FoundationHost/TiaMcp.Engine.Native.Tests/V{key}/TiaMcp.Engine.Native.Tests.V{key}.csproj"));
            foreach (var name in new[] { "native-safety", "usage", "campaign-inputs" })
                Assert.Equal(selected, plan.Checks.Any(job => job.Name == name + "-v" + key + "-plc"));
        }
    }
}
