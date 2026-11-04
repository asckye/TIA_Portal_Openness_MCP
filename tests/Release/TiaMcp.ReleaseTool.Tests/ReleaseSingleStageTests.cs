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
    public void ParentBuildsCampaignFileAppInTheExportedCacheBeforeSchedulingReaders()
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

    }

    [Fact]
    public void FoundationUsesOneExclusiveShardedJobAndNoIndependentSubsetProcesses()
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
        var results = ReleaseSuiteStage.Run(tree.Root, catalog, Path.Combine(tree.Root, "results"), tree.Root, "dotnet", null, 1, Process,
            foundationRun: () =>
            {
                Assert.Equal(new[] { "ordinary" }, events);
                events.Add("sharded-foundation-api");
                return ReleaseFoundationStage.Names.ToDictionary(name => name, _ => new CommandResult(0, "passed", ""));
            });
        Assert.Equal(new[] { "ordinary", "sharded-foundation-api", "studio-gui" }, events);
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
                Assert.True(options.Has("NoBuild"));
                Assert.True(DerivedSuiteGates.FilterContains(selected["foundation-api"]!.AsObject(), selected["foundation"]!.AsObject()));
                var directory = options.Get("ResultsDirectory")!; Directory.CreateDirectory(directory);
                var trx = Path.Combine(directory, "fixture.trx");
                WriteFoundationTrx(trx, failure);
                var merged = SuiteSharding.Merge("foundation-api", selected["foundation-api"]!.AsObject(), [trx], directory);
                return failure == "source" ? merged with { ExitCode = 1 } : merged;
            }, process: (_, _, _, _) => new(0, "built", ""));
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
}
