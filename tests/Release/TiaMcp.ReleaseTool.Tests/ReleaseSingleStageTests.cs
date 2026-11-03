using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Xml.Linq;
using System.IO.Compression;
using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseSingleStageTests
{
    [Fact]
    public void CompanionSelectionIsAutomaticAndExplicitPathsTakePrecedence()
    {
        using var tree = new StaticTree();
        var local = Path.GetFullPath(tree.Write("TiaMcp_Output/ecosystem-python/Scripts/python.exe", "fixture"));
        var specified = Path.GetFullPath(tree.Write("explicit/python.exe", "fixture"));
        Options Parse(params string[] args) => Options.Parse(args, new HashSet<string>());
        Assert.Equal(local, ReleaseCommands.ReleaseCompanionPython(Parse(), tree.Root, _ => null));
        Assert.Equal(specified, ReleaseCommands.ReleaseCompanionPython(Parse(), tree.Root, _ => specified));
        Assert.Equal(local, ReleaseCommands.ReleaseCompanionPython(Parse("-CompanionPython", local), tree.Root, _ => specified));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ReleaseCompanionPython(Parse("-CompanionPython", Path.Combine(tree.Root, "missing.exe")), tree.Root, _ => specified));
    }

    [Fact]
    public void AnalyzerRefreshForcesFreshCompilationWithoutChangingCallerOptions()
    {
        var options = Options.Parse(["-Cold=false", "-MaxParallelism", "2", "-NuGetConfig", "offline.config"], new HashSet<string> { "Cold" });
        var refresh = ReleaseCommands.ReleaseAnalyzerOptions(options);
        Assert.True(refresh.Has("Cold"));
        Assert.False(options.Has("Cold"));
        Assert.Equal("2", refresh.Get("MaxParallelism"));
        Assert.Equal("offline.config", refresh.Get("NuGetConfig"));
    }

    [Fact]
    public void MissingCompanionDependenciesFailBeforeAnyBuild()
    {
        var invoked = false;
        var error = Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireEcosystemPython("python", "fixture/bridge.py", "python",
            (executable, arguments, root, env) =>
            {
                invoked = true;
                var args = arguments.ToArray();
                Assert.Contains("import reportlab", args[2]);
                Assert.Contains("load_groups", args[2]);
                return new(1, "", "ModuleNotFoundError: reportlab");
            }));
        Assert.True(invoked);
        Assert.Contains("dependencies unavailable", error.Message);
    }

    [Fact]
    public void MarketplaceVersionMismatchFailsBeforeMechanicalBump()
    {
        using var tree = new StaticTree();
        tree.Write(".claude-plugin/plugin.json", "{\"name\":\"fixture\"}");
        tree.Write(".claude-plugin/marketplace.json", "{\"plugins\":[{\"name\":\"fixture\",\"version\":\"4.0.0-beta.2\"}]}");
        Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireMarketplaceVersion(tree.Root, "4.0.0-beta.3"));
        ReleasePrerequisites.RequireMarketplaceVersion(tree.Root, "4.0.0-beta.2");
    }

    [Theory]
    [InlineData(2, 0, 1, 0)]
    [InlineData(1, 0, 1, 1)]
    public void ParallelStageEnforcesEachRegisteredCountGate(int minimum, int maximum, int passed, int skipped)
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["fixture"] = Suite("one.csproj", minimum, maximum) };
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray();
            if (args[0] == "test") WriteTrx(args, passed, skipped);
            return new(0, "fixture completed", "");
        }
        Assert.Throws<ReleaseException>(() => ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 2, Process));
        var row = JsonNode.Parse(File.ReadAllText(Path.Combine(tree.Root, "results/fixture/fixture.json")))!;
        Assert.Equal(minimum, (int)row["minimumPassed"]!);
        Assert.Equal(maximum, (int)row["maximumSkipped"]!);
        Assert.NotEmpty(row["errors"]!.AsArray());
    }

    [Fact]
    public void ParallelStageSerializesVariantsAndRunsGuiAloneWithPrivateEnvironments()
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject
        {
            ["first"] = Suite("shared.csproj", 1, 0), ["second"] = Suite("shared.csproj", 1, 0),
            ["independent"] = Suite("other.csproj", 1, 0), ["studio-gui"] = Suite("gui.csproj", 1, 0)
        };
        using var simultaneous = new Barrier(2);
        var order = new ConcurrentQueue<string>();
        var roots = new ConcurrentBag<string>();
        var active = 0;
        var poolStarted = 0;
        var compilationGate = new object();
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray();
            var name = Path.GetFileNameWithoutExtension(args[1]);
            if (args[0] == "build")
            {
                Assert.Equal(0, Volatile.Read(ref poolStarted));
                Assert.Equal(0, Volatile.Read(ref active));
                Assert.True(Monitor.IsEntered(compilationGate));
                order.Enqueue("build:" + name);
                return new(0, "built", "");
            }
            var directory = args[Array.IndexOf(args, "--results-directory") + 1];
            Interlocked.Exchange(ref poolStarted, 1);
            name = Path.GetFileName(directory);
            Assert.Null(env!["TIA_MCP_TEST_SHARD_COUNT"]);
            Assert.Null(env["TIA_MCP_BUILD_CACHE_DISABLED"]);
            Assert.Null(env["TIA_MCP_PRODUCTION_RELEASE"]);
            Assert.Equal("prepared", env["TIA_MCP_DECODER_RUNTIME"]);
            roots.Add(env["TIA_MCP_DATA_DIRECTORY"]!);
            var count = Interlocked.Increment(ref active);
            try
            {
                if (name is "first" or "independent") Assert.True(simultaneous.SignalAndWait(TimeSpan.FromSeconds(10)));
                if (name == "studio-gui") Assert.Equal(1, count);
                order.Enqueue("test:" + name);
                WriteTrx(args, 1, 0);
            }
            finally { Interlocked.Decrement(ref active); }
            return new(0, "passed", "");
        }
        var results = ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 2, Process,
            configure: environment =>
            {
                environment["TIA_MCP_DECODER_RUNTIME"] = "prepared";
                environment["TIA_MCP_BUILD_CACHE_DISABLED"] = "1";
                environment["TIA_MCP_PRODUCTION_RELEASE"] = "1";
            },
            additionalJobs: [("extra", () => { order.Enqueue("extra"); return new("extra", 0, "passed", ""); })], compilationGate: compilationGate);
        Assert.Equal(4, results.Count);
        Assert.Equal(4, roots.Distinct().Count());
        var events = order.ToArray();
        Assert.True(Array.IndexOf(events, "test:first") < Array.IndexOf(events, "test:second"));
        Assert.Equal("test:studio-gui", events[^1]);
        Assert.Contains("extra", events);
        Assert.Equal(1, events.Count(value => value == "build:shared"));
    }

    [Fact]
    public void UpdaterGuiAndSpecialHarnessJobsRunAloneAfterThePool()
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["updater"] = Suite("updater.csproj", 1, 0), ["studio-gui"] = Suite("gui.csproj", 1, 0) };
        using var ordinaryStarted = new ManualResetEventSlim();
        var active = 0;
        var order = new List<string>();
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray();
            if (args[0] == "build") return new(0, "built", "");
            Assert.True(ordinaryStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, Interlocked.Increment(ref active));
            try { order.Add(Path.GetFileName(args[Array.IndexOf(args, "--results-directory") + 1])); WriteTrx(args, 1, 0); }
            finally { Interlocked.Decrement(ref active); }
            return new(0, "passed", "");
        }
        ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 2, Process,
            additionalJobs: [("reader", () =>
            {
                Interlocked.Increment(ref active); ordinaryStarted.Set(); Thread.Sleep(100);
                order.Add("reader"); Interlocked.Decrement(ref active);
                return new("reader", 0, "passed", "");
            })], exclusiveJobs: [("harness", () =>
            {
                Assert.Equal(0, Volatile.Read(ref active)); order.Add("harness");
                return new("harness", 0, "passed", "");
            })]);
        Assert.Equal(new[] { "reader", "harness", "updater", "studio-gui" }, order);
        order.Clear();
        ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "solo-results"), tree.Root, "dotnet", null, 2, Process);
        Assert.Equal(new[] { "updater", "studio-gui" }, order);
    }

    [Fact]
    public void AllVariantsBuildBeforeReadersAndRetainTheirOwnBinaries()
    {
        using var tree = new StaticTree();
        tree.Write("shared.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var first = Suite("shared.csproj", 1, 0); first["arguments"] = new JsonArray("-p:Variant=first");
        var second = Suite("shared.csproj", 1, 0); second["arguments"] = new JsonArray("-p:Variant=second");
        var catalog = new JsonObject { ["first"] = first, ["second"] = second };
        var builds = new Dictionary<string, string>();
        var readersStarted = false;
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray();
            var variant = args.Single(arg => arg.StartsWith("-p:Variant=", StringComparison.Ordinal));
            var output = args[Array.IndexOf(args, "--output") + 1];
            if (args[0] == "build")
            {
                Assert.False(readersStarted); builds.Add(variant, output);
                Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "compiled.txt"), variant);
            }
            else
            {
                readersStarted = true; Assert.Equal(2, builds.Count);
                Assert.Equal(Path.Combine(tree.Root, "bin", "Release", "net10.0"), env!["TIA_MCP_TEST_REGISTERED_OUTPUT"]);
                Assert.Equal(builds[variant], output);
                Assert.Equal(variant, File.ReadAllText(Path.Combine(output, "compiled.txt")));
                WriteTrx(args, 1, 0);
            }
            return new(0, "passed", "");
        }
        ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 2, Process);
        Assert.Equal(2, builds.Values.Distinct().Count());
    }

    [Fact]
    public void ParentBuildsBothPlcFileAppsInTheExportedCacheBeforeSchedulingReaders()
    {
        using var tree = new StaticTree();
        var commands = new List<string>();
        var temp = Path.Combine(tree.Root, "parent-file-apps");
        var result = ReleaseCommands.PreparePlcReleaseFilePrograms(tree.Root, "dotnet", "offline.config", Path.Combine(tree.Root, "logs"), temp,
            (_, arguments, root, environment) =>
            {
                var args = arguments.ToArray(); commands.Add(args[1]);
                Assert.Equal("build", args[0]); Assert.Equal(tree.Root, root);
                Assert.Equal(Path.GetFullPath(temp), environment!["TEMP"]);
                Assert.Equal(environment["TEMP"], environment["TMP"]);
                Assert.DoesNotContain("-m:1", args); Assert.Contains("-p:RestoreConfigFile=" + Path.GetFullPath("offline.config"), args);
                return new(0, "built", "");
            });
        Assert.Equal(Path.GetFullPath(temp), result);
        Assert.Equal(ReleaseCommands.PlcReleaseFilePrograms.Select(program => Path.Combine(tree.Root, program)), commands);
        var source = File.ReadAllText(Path.Combine(TiaMcp.BuildCommon.Repository.FindRoot(), "build-tools/release/ReleaseTestStage.cs"));
        var stage = source[source.IndexOf("private static ReleaseTestResults RunReleaseTestStage", StringComparison.Ordinal)..];
        var scheduling = stage.IndexOf("var jobs =", StringComparison.Ordinal);
        Assert.InRange(stage.IndexOf("PreparePlcReleaseFilePrograms(", StringComparison.Ordinal), 0, scheduling - 1);
        Assert.InRange(stage.IndexOf("HostParity.Prepare(", StringComparison.Ordinal), 0, scheduling - 1);
        Assert.Contains("additionalEnvironment: stageEnvironment", stage);
        Assert.Contains("environment[PlcReleaseSystemTempVariable] = fileAppTemp", stage);
    }

    [Fact]
    public void OnlyTheTwoScratchAndDiagnosticsHarnessModesUseTheExclusiveLane()
    {
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var jobs = ReleaseCommands.PlcReleaseJobs(root, root, Path.Combine(root, "bin-build/fixture"), root, "dotnet", "python");
        var exclusive = jobs.Where(ReleaseCommands.ReleasePlcExclusive).ToArray();
        Assert.Equal(4, exclusive.Length);
        Assert.Equal(new[] { "native-diagnostics-only", "test-ecosystem-assembly" }, exclusive.Select(job => job.Mode).Distinct().Order());
        Assert.All(exclusive, job => Assert.StartsWith("harness-", job.Name));
    }

    [Theory]
    [InlineData("suite")]
    [InlineData("ordinary")]
    [InlineData("exclusive")]
    [InlineData("post")]
    public void TestStageOnlyRunsTheNamedJobWithoutWritingRecords(string lane)
    {
        using var tree = new StaticTree();
        var record = tree.Write("manifest/configurator-build.json", "{\"testsPassed\":0}");
        var before = File.ReadAllBytes(record);
        var options = Options.Parse(["-TestStageOnly", "-Only", "x"], new HashSet<string> { "TestStageOnly" });
        var selection = new ReleaseTestSelection(options, ["x", "unselected"]);
        var executed = new List<string>();
        var catalog = new JsonObject { [lane == "suite" ? "x" : "unselected"] = Suite("fixture.csproj", 1, 0) };
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray();
            Assert.Equal("suite", lane);
            if (args[0] == "test") { executed.Add("x"); WriteTrx(args, 1, 0); }
            return new(0, "passed", "");
        }
        (string Name, Func<PipelineResult> Run) Job(string candidate) =>
            (lane == candidate ? "x" : "unselected", () =>
            {
                Assert.Equal(lane, candidate); executed.Add("x"); return new("x", 0, "passed", "");
            });
        ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 1, Process,
            additionalJobs: [Job("ordinary")], exclusiveJobs: [Job("exclusive")], selection: selection, postSuiteJobs: [Job("post")]);
        Assert.Equal(new[] { "x" }, executed);
        Assert.Equal(before, File.ReadAllBytes(record));
        Assert.Single(Directory.GetFiles(Path.Combine(tree.Root, "manifest")));
        var source = File.ReadAllText(Path.Combine(TiaMcp.BuildCommon.Repository.FindRoot(), "build-tools/release/ReleaseTestStage.cs"));
        Assert.True(source.IndexOf("if (options.Has(\"TestStageOnly\")) return results;", StringComparison.Ordinal)
            < source.IndexOf("configuration[\"testsPassed\"] =", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData(",")]
    public void TestStageOnlyRejectsUnknownOrEmptySelections(string only)
    {
        var options = Options.Parse(["-TestStageOnly", "-Only", only], new HashSet<string> { "TestStageOnly" });
        Assert.Throws<ReleaseException>(() => new ReleaseTestSelection(options, ["x"]));
        Assert.Throws<ReleaseException>(() => new ReleaseTestSelection(Options.Parse(["-Only", "x"], new HashSet<string>()), ["x"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("foundation-api")]
    [InlineData("foundation")]
    public void FoundationUsesOneExclusiveShardedJobAndNoIndependentSubsetProcesses(string? only)
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["ordinary"] = Suite("ordinary.csproj", 1, 0), ["studio-gui"] = Suite("gui.csproj", 1, 0) };
        foreach (var name in ReleaseFoundationStage.Names) catalog[name] = Suite("Foundation.csproj", 1, 0);
        var events = new List<string>();
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray();
            Assert.DoesNotContain(Path.Combine(tree.Root, "Foundation.csproj"), args);
            if (args[0] == "test") { events.Add(Path.GetFileName(args[Array.IndexOf(args, "--results-directory") + 1])); WriteTrx(args, 1, 0); }
            return new(0, "passed", "");
        }
        var selection = only is null ? null : new ReleaseTestSelection(Options.Parse(["-TestStageOnly", "-Only", only], new HashSet<string> { "TestStageOnly" }), catalog.Select(pair => pair.Key));
        var results = ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 1, Process,
            selection: selection, foundationRun: () =>
            {
                if (only is null) Assert.Equal(new[] { "ordinary" }, events);
                else Assert.Empty(events);
                events.Add("sharded-foundation-api");
                return ReleaseFoundationStage.Names.ToDictionary(name => name, _ => new CommandResult(0, "passed", ""));
            });
        Assert.Equal(only is null ? new[] { "ordinary", "sharded-foundation-api", "studio-gui" } : new[] { "sharded-foundation-api" }, events);
        Assert.All(ReleaseFoundationStage.Names, name => Assert.Equal(0, results[name].ExitCode));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("minimum")]
    [InlineData("skip")]
    [InlineData("source")]
    public void ShardedFoundationDerivesAllFiveGatesAndStampsEveryExitCode(string failure)
    {
        using var tree = new StaticTree();
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root);
        var output = Path.Combine(tree.Root, "results");
        var invoked = 0;
        var results = ReleaseFoundationStage.Run(tree.Root, catalog, output, "sdk", "dotnet", "offline.config", 6, _ => { },
            (options, selected, process) =>
            {
                invoked++;
                Assert.Equal(new[] { "foundation-api" }, options.All("Suite"));
                Assert.Equal("8", options.Get("Shards")); Assert.Equal("6", options.Get("MaxParallelism"));
                Assert.Equal("sdk", options.Get("PublicApiRoot")); Assert.Equal("offline.config", options.Get("NuGetConfig"));
                Assert.Equal(Path.Combine(output, "foundation-api"), options.Get("ResultsDirectory"));
                Assert.False(options.Has("NoBuild")); Assert.False(options.Has("RuntimeManifest"));
                Assert.True(DerivedSuiteGates.FilterContains(selected["foundation-api"]!.AsObject(), selected["foundation"]!.AsObject()));
                var directory = options.Get("ResultsDirectory")!; Directory.CreateDirectory(directory);
                var trx = Path.Combine(directory, "fixture.trx");
                WriteFoundationTrx(trx, failure);
                var merged = SuiteSharding.Merge("foundation-api", selected["foundation-api"]!.AsObject(), [trx], directory);
                return failure == "source" ? merged with { ExitCode = 1 } : merged;
            });
        Assert.Equal(1, invoked);
        Assert.Equal(6, results.Count);
        foreach (var name in ReleaseFoundationStage.Names)
        {
            var row = JsonNode.Parse(File.ReadAllText(Path.Combine(output, name, name + ".json")))!;
            Assert.Equal(results[name].ExitCode, (int)row["exitCode"]!);
            Assert.Equal((int)catalog[name]!["minimumPassed"]!, (int)row["minimumPassed"]!);
            Assert.Equal((int)catalog[name]!["maximumSkipped"]!, (int)row["maximumSkipped"]!);
            if (name != "foundation-api") Assert.Equal("foundation-api", (string?)row["derivedFrom"]);
        }
        Assert.Equal(failure == "source" ? 1 : 0, results["foundation-api"].ExitCode);
        Assert.Equal(failure is "minimum" or "skip" ? 1 : 0, results["shared-pipeline"].ExitCode);
        if (failure == "none")
        {
            Assert.All(results.Values, result => Assert.Equal(0, result.ExitCode));
            var derived = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "foundation/foundation.json")))!;
            Assert.Equal(32129, (int)derived["passed"]!);
        }
    }

    private static void WriteFoundationTrx(string path, string failure)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var rows = new List<(string Class, string Method, string Display, string Outcome)>();
        void Add(string cls, int count)
        {
            for (int index = 0; index < count; index++) rows.Add((cls, "Check", cls + ".Check(" + index + ")", "Passed"));
        }
        Add("FoundationChecks", 31409); Add("WorkerRobustnessTests", 570); Add("WorkbenchControlHostTests", 98);
        Add("SharedPipelineTests", failure == "minimum" ? 5 : 6); Add("PlcSharedLifecycleTests", 46); Add("ApiMetadataChecks", 2459);
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var kind in new[] { "UDT", "GlobalDB" })
            rows.Add(("FoundationChecks", "Check", "FoundationChecks.Check(row: OfflineXmlBuilderTests #1: " + key + " " + kind + " official interface XSD: )", "Passed"));
        if (failure == "skip")
        {
            var index = rows.FindIndex(row => row.Class == "SharedPipelineTests");
            var row = rows[index]; rows[index] = (row.Class, row.Method, row.Display, "NotExecuted");
        }
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "Results", rows.Select((row, index) => new XElement(ns + "UnitTestResult", new XAttribute("testId", "test" + index),
                new XAttribute("executionId", "run" + index), new XAttribute("testName", row.Display), new XAttribute("outcome", row.Outcome)))),
            new XElement(ns + "TestDefinitions", rows.Select((row, index) => new XElement(ns + "UnitTest", new XAttribute("id", "test" + index),
                new XElement(ns + "TestMethod", new XAttribute("className", row.Class), new XAttribute("name", row.Method))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(ns + "Counters",
                new XAttribute("total", rows.Count), new XAttribute("executed", rows.Count(row => row.Outcome == "Passed")),
                new XAttribute("passed", rows.Count(row => row.Outcome == "Passed")), new XAttribute("failed", 0),
                new XAttribute("notExecuted", rows.Count(row => row.Outcome == "NotExecuted")))))).Save(path);
    }

    [Fact]
    public void FailedJobsDoNotHideLaterSuitesExclusiveJobsOrPostSuiteJobs()
    {
        using var tree = new StaticTree();
        var events = new List<string>();
        var catalog = new JsonObject { ["first"] = Suite("shared.csproj", 1, 0), ["second"] = Suite("shared.csproj", 1, 0),
            ["updater"] = Suite("updater.csproj", 1, 0), ["studio-gui"] = Suite("gui.csproj", 1, 0) };
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray();
            if (args[0] == "build") return new(0, "built", "");
            var name = Path.GetFileName(args[Array.IndexOf(args, "--results-directory") + 1]);
            events.Add(name); WriteTrx(args, 1, 0); return new(name == "first" ? 1 : 0, "completed", "");
        }
        (string, Func<PipelineResult>) Job(string name, bool fail) => (name, () =>
        {
            events.Add(name);
            if (fail) throw new ReleaseException("fixture failure " + name);
            return new(name, 0, "passed", "");
        });
        var error = Assert.Throws<ReleaseException>(() => ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 1, Process,
            additionalJobs: [Job("pool-failed", true), Job("pool-later", false)],
            exclusiveJobs: [Job("exclusive-failed", true), Job("exclusive-later", false)],
            postSuiteJobs: [Job("post-failed", true), Job("post-later", false)]));
        Assert.Equal(new[] { "first", "second", "pool-failed", "pool-later", "exclusive-failed", "exclusive-later", "updater", "studio-gui", "post-failed", "post-later" }, events);
        foreach (var name in new[] { "first", "pool-failed", "exclusive-failed", "post-failed" }) Assert.Contains(name, error.Message);
    }

    [Fact]
    public void FailedCompilationStillReportsItsDependentsAndRunsOtherProjects()
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["first"] = Suite("broken.csproj", 1, 0), ["same-build"] = Suite("broken.csproj", 1, 0), ["later"] = Suite("later.csproj", 1, 0) };
        var executed = new List<string>();
        CommandResult Process(string _, IEnumerable<string> arguments, string root, IDictionary<string, string?>? environment)
        {
            var args = arguments.ToArray();
            if (args[0] == "build") return new(args[1].EndsWith("broken.csproj", StringComparison.Ordinal) ? 1 : 0, "build completed", "");
            executed.Add(Path.GetFileName(args[Array.IndexOf(args, "--results-directory") + 1])); WriteTrx(args, 1, 0); return new(0, "passed", "");
        }
        var error = Assert.Throws<ReleaseException>(() => ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 1, Process));
        Assert.Equal(new[] { "later" }, executed);
        Assert.Contains("first", error.Message); Assert.Contains("same-build", error.Message);
    }

    [Fact]
    public void TestStageOnlyUsesTheSameExternalTempRootAsProduction()
    {
        var source = File.ReadAllText(Path.Combine(TiaMcp.BuildCommon.Repository.FindRoot(), "build-tools/release/ReleaseProduction.cs"));
        var start = source.IndexOf("if (options.Has(\"TestStageOnly\"))", StringComparison.Ordinal);
        var end = source.IndexOf("if (options.Has(\"EarlyGatesOnly\"))", start, StringComparison.Ordinal);
        Assert.Contains("RunReleaseTestStage(options, publicApiRoot, testLogs, CreateReleaseTempRoot(), python, nugetConfig)", source[start..end]);
    }

    private static JsonObject Suite(string project, int minimum, int maximum) => new()
    {
        ["project"] = project, ["minimumPassed"] = minimum, ["maximumSkipped"] = maximum, ["arguments"] = new JsonArray()
    };

    private static void WriteTrx(string[] arguments, int passed, int skipped)
    {
        var directory = arguments[Array.IndexOf(arguments, "--results-directory") + 1];
        var file = arguments[Array.IndexOf(arguments, "--logger") + 1].Split("LogFileName=", StringSplitOptions.None)[1];
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var cases = Enumerable.Range(0, passed + skipped).ToArray();
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "Results", cases.Select(index => new XElement(ns + "UnitTestResult",
                new XAttribute("testId", "test" + index), new XAttribute("executionId", "run" + index), new XAttribute("outcome", index < passed ? "Passed" : "NotExecuted")))),
            new XElement(ns + "TestDefinitions", cases.Select(index => new XElement(ns + "UnitTest", new XAttribute("id", "test" + index),
                new XElement(ns + "TestMethod", new XAttribute("className", "Fixture"), new XAttribute("name", "Test" + index))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(ns + "Counters",
                new XAttribute("total", passed + skipped), new XAttribute("executed", passed), new XAttribute("passed", passed), new XAttribute("failed", 0), new XAttribute("notExecuted", skipped)))))
            .Save(Path.Combine(directory, file));
    }

    [Theory]
    [InlineData("2026-11-0X")]
    [InlineData("2026-02-30")]
    [InlineData("")]
    public void ReleaseRefusesPlaceholderOrImpossibleChangelogDates(string date) =>
        Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireChangelog("## [Unreleased]\n\n## [4.0.0-beta.3] - " + date + "\n", "4.0.0-beta.3"));

    [Fact]
    public void ChangelogSkipsUnreleasedAndRequiresRequestedVersion()
    {
        const string changelog = "## [Unreleased]\n\n## [4.0.0-beta.3] - 2026-11-03\n";
        Assert.True(ReleasePrerequisites.RequireChangelog(changelog, "4.0.0-beta.3").Success);
        Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireChangelog(changelog, "4.0.0-beta.2"));
    }

    [Fact]
    public void FullReleaseRetainsEveryGateWithOnlyApprovedFoundationDerivations()
    {
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(TiaMcp.BuildCommon.Repository.FindRoot(), "tests/test-suites.json"), TiaMcp.BuildCommon.Repository.FindRoot());
        var groups = ReleaseSuiteStage.Groups(catalog);
        Assert.Equal(38, groups.SelectMany(group => group).Count());
        Assert.Equal(new[] { "foundation", "foundation-api", "shared-lifecycle-plc", "shared-pipeline", "workbench-control-host", "worker-robustness" }, ReleaseFoundationStage.Names.Order());
        Assert.Equal(catalog.Select(pair => pair.Key).Order(), groups.SelectMany(group => group).Order());
        foreach (var names in new[] { new[] { "plc-exchange-worker-v20", "plc-exchange-worker-v21" },
            new[] { "foundation", "foundation-api", "operator-host", "worker-robustness", "shared-lifecycle-plc" } })
            Assert.Contains(groups, group => names.All(group.Contains));
    }

    [Fact]
    public void ProductionPolicyRetiresOnlyApprovedChecksAndKeepsEveryOtherFullGate()
    {
        var policy = ReleaseCheckPolicy.Load(TiaMcp.BuildCommon.Repository.FindRoot());
        var production = policy.ForProductionRelease();
        Assert.Equal(new[] { "plc-isolated-stability", "plc-stability" }, policy.Checks.Except(production.Checks).Order());
        var plan = production.Select("full", null) with { ProductionRelease = true };
        policy.ValidatePlan(plan);
        Assert.Throws<ReleaseException>(() => policy.ValidatePlan(plan with
        {
            SelectedChecks = plan.SelectedChecks.Where(check => check != "plc-approval").ToArray(),
            SkippedChecks = ["plc-approval"]
        }));
        Assert.Contains("plc-stability", policy.Select("full", null).SelectedChecks);
    }

    [Fact]
    public void PendingProducerKeepsBinaryProofAndCannotSatisfyTheCompletedTestGate()
    {
        using var tree = new StaticTree();
        var repository = TiaMcp.BuildCommon.Repository.FindRoot();
        tree.Write("build-tools/release/release-checks.json", File.ReadAllText(Path.Combine(repository, "build-tools/release/release-checks.json")));
        tree.Write("Version.props", "<Project />");
        var binary = tree.Write("runtime/tools/fixture.dll", "producer bytes");
        var policy = ReleaseCheckPolicy.Load(tree.Root);
        var plan = policy.ForProductionRelease().Select("full", null) with { ProductionRelease = true };
        var validation = new JsonObject();
        var record = new JsonObject
        {
            ["tier"] = "full", ["checkPlan"] = JsonSerializer.SerializeToNode(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            ["release"] = "4.0.0", ["fileVersion"] = "4.0.0.0", ["validation"] = validation,
            ["runtimeFiles"] = new JsonArray(new JsonObject { ["path"] = "runtime/tools/fixture.dll", ["sha256"] = ReleaseRecords.HashFile(binary) }),
            ["validationArtifacts"] = new JsonArray(),
            ["sourceFiles"] = JsonSerializer.SerializeToNode(ReleaseRecords.GetSources(tree.Root, "engine").Select(row => new { path = row.Path, sha256 = row.Sha256 })),
            ["validationInputs"] = JsonSerializer.SerializeToNode(ReleaseRecords.GetValidationInputs(tree.Root, "engine").Select(row => new { path = row.Path, sha256 = row.Sha256 }))
        };
        string Reason(bool pending)
        {
            using var parsed = JsonDocument.Parse(record.ToJsonString());
            return pending ? ReleaseRecords.EngineProducerReason(tree.Root, "4.0.0", parsed.RootElement, ReleaseRecords.GetSources(tree.Root, "engine"))
                : ReleaseRecords.EngineReuseReason(tree.Root, "4.0.0", parsed.RootElement, ReleaseRecords.GetSources(tree.Root, "engine"));
        }
        Assert.Equal("", Reason(pending: true));
        Assert.Equal("offline validation missing", Reason(pending: false));
        File.WriteAllText(binary, "changed");
        Assert.Contains("binary changed", Reason(pending: true));
        File.WriteAllText(binary, "producer bytes");
        foreach (var (name, count) in new[] { ("versionPolicySdkPassed", 10), ("keptCodePassed", 5155),
            ("writeGuardPassed", 8), ("crashEvidencePassed", 7), ("updaterPassed", 33), ("nativeMcpSafetyPassed", 8) }) validation[name] = count;
        Assert.Equal("", Reason(pending: false));
        record["checkPlan"]!["productionRelease"] = false;
        Assert.Contains("full production release plan", Reason(pending: true));
    }

    [Fact]
    public void PendingProducerPackageCannotPassTheProductionGate()
    {
        using var tree = new StaticTree();
        var policy = ReleaseCheckPolicy.Load(TiaMcp.BuildCommon.Repository.FindRoot());
        var checks = policy.ForProductionRelease().Checks;
        var package = Path.Combine(tree.Root, "candidate.zip");
        void Write(string status, IEnumerable<string> ran)
        {
            using var archive = new ZipArchive(File.Create(package), ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("fixture/manifest/package-manifest.json").Open());
            writer.Write(new JsonObject { ["tier"] = "full", ["productionRelease"] = true, ["checkStatus"] = status,
                ["checksRan"] = new JsonArray(ran.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()), ["checksSkipped"] = new JsonArray() }.ToJsonString());
        }
        Write("pending", []);
        Assert.Throws<ReleaseException>(() => policy.RequireFullPackage(package));
        Write("passed", checks);
        policy.RequireFullPackage(package);
        Write("passed", checks.Where(name => name != "plc-approval"));
        Assert.Throws<ReleaseException>(() => policy.RequireFullPackage(package));
    }

    [Fact]
    public void ReleasePathHasNoNightlyOrEvidenceAdoptionAndDryRunReturnsBeforePublication()
    {
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var source = File.ReadAllText(Path.Combine(root, "build-tools/release/ReleaseProduction.cs"));
        var production = source[..source.IndexOf("// The rehearsal ends", StringComparison.Ordinal)];
        Assert.DoesNotContain("NightlyRuns.RequireRelease", production);
        Assert.DoesNotContain("AdoptReleaseEvidence", production);
        Assert.DoesNotContain("\"--local\"", production);
        Assert.True(production.IndexOf("if (dryRun)", StringComparison.Ordinal) < production.IndexOf("CommitReleaseChanges", StringComparison.Ordinal));
        var policy = ReleaseCheckPolicy.Load(root).ForProductionRelease();
        Assert.DoesNotContain("plc-stability", policy.Checks);
        Assert.DoesNotContain("plc-isolated-stability", policy.Checks);
        Assert.Contains("http-concurrency", policy.Checks);
    }
}
