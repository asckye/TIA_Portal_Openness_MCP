using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class MergeCheckTests
{
    private static JsonObject Suite(string project, params string[] arguments) => new()
    {
        ["project"] = project, ["arguments"] = new JsonArray(arguments.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
        ["minimumPassed"] = 1, ["maximumSkipped"] = 0
    };

    private static JsonObject Catalog() => new()
    {
        ["foundation-api"] = Suite("tests/foundation.csproj"), ["foundation"] = Suite("tests/foundation.csproj", "--filter", "FullyQualifiedName~Foundation"),
        ["studio-gui"] = Suite("tests/gui.csproj"), ["workbench-control"] = Suite("tests/gui.csproj", "--filter", "FullyQualifiedName~Control"),
        ["studio-core"] = Suite("tests/core.csproj"), ["adapter-contracts"] = Suite("tests/adapter.csproj"),
        ["kept-code"] = Suite("tests/kept.csproj"), ["plc-exchange-worker-v20"] = Suite("tests/plc.csproj", "-p:TiaReleaseKey=20"),
        ["plc-exchange-worker-v21"] = Suite("tests/plc.csproj", "-p:TiaReleaseKey=21"),
        ["source-contracts"] = Suite("tests/source.csproj"), ["release-tool"] = Suite("tests/release.csproj")
    };

    private static MergeCheck.Plan Select(string[] paths, JsonObject? catalog = null,
        Dictionary<string, HashSet<string>>? closures = null, bool all = false, Func<string, string, bool>? mentions = null) =>
        MergeCheck.Select(paths, all, catalog ?? Catalog(), closures ?? new(), mentions ?? ((_, _) => false),
            ["scripts/checks/a.cs"], "dotnet", "python", "sdk", "output");

    [Fact]
    public void ChangeSetUnionsCommittedWorkingAndUntrackedPathsAndRequiresMergeBase()
    {
        var calls = new List<string[]>();
        CommandResult Git(string[] args)
        {
            calls.Add(args);
            return new(0, args[0] == "merge-base" ? "abc\n" : args[0] == "ls-files" ? "new.cs\0" : args[^1] == "HEAD" ? "work.cs\0same.cs\0" : "committed.cs\0same.cs\0", "");
        }
        Assert.Equal(new[] { "committed.cs", "new.cs", "same.cs", "work.cs" }, MergeCheck.ChangeSet("main", Git));
        Assert.Equal(new[] { "merge-base", "main", "HEAD" }, calls[0]);
        Assert.Equal(new[] { "diff", "--no-renames", "--name-only", "-z", "abc..HEAD" }, calls[1]);
        Assert.Throws<ReleaseException>(() => MergeCheck.ChangeSet("missing", _ => new(1, "", "unknown revision")));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/development/example.md")]
    [InlineData("")]
    public void DocsPlanHasExactlyThreeChecksAndNoBuildsOrSuites(string path)
    {
        var plan = Select(path.Length == 0 ? [] : [path]);
        Assert.True(plan.DocsOnly);
        Assert.Empty(plan.Builds);
        Assert.Empty(plan.Suites);
        Assert.Equal(new[] { "check-repository", "check-dead-tool-references", "generate-tool-counts" }, plan.Checks.Select(job => job.Name));
        Assert.Contains("-NoAnalyzer", plan.Checks[0].Arguments);
    }

    [Theory]
    [InlineData("reference/x.md")]
    [InlineData("templates/standards/tiamcp.basic/README.md")]
    [InlineData("docs/reference/real-machine-ledger.md")]
    [InlineData("docs/reference/version-tool-catalog.md")]
    [InlineData("docs/reference/tool-matrix.md")]
    [InlineData("docs/releases/v4.0.0-tool-migration.md")]
    [InlineData("new-top-level/file.cs")]
    public void UnmappedAndGeneratedDocumentsSelectEverythingAndNameTheirReason(string path)
    {
        var plan = Select([path]);
        Assert.False(plan.DocsOnly);
        Assert.True(plan.Wide);
        Assert.Equal(Catalog().Count, plan.Suites.Count);
        Assert.Contains(path, plan.Builds["eight-release-table"]);
        Assert.Contains(path, MergeCheck.Describe(plan));
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("global.json")]
    [InlineData("NuGet.Config")]
    [InlineData(".editorconfig")]
    [InlineData("Version.props")]
    [InlineData("TiaPortalOpenness.slnx")]
    [InlineData("tests/test-suites.json")]
    [InlineData("src/Shared/Adapter.Sources.props")]
    [InlineData("tests/Shared/new.cs")]
    [InlineData("build-tools/common/new.cs")]
    public void EveryWideRuleSelectsAllRegisteredSuitesAndProductBuilds(string path)
    {
        var plan = Select([path]);
        Assert.True(plan.Wide);
        Assert.Equal(Catalog().Count, plan.Suites.Count);
        Assert.True(plan.Table);
        Assert.Contains(MergeCheck.ConfigurationProject, plan.Builds.Keys);
        Assert.Contains(MergeCheck.DiagnosticsProject, plan.Builds.Keys);
        Assert.Contains("file:scripts/checks/a.cs", plan.Builds.Keys);
    }

    [Fact]
    public void EvaluatedClosureIncludesTransitiveAndGlobbedInputsOncePerPropertyPair()
    {
        var catalog = new JsonObject { ["one"] = Suite("tests/one.csproj"), ["same"] = Suite("tests/one.csproj"), ["two"] = Suite("tests/two.csproj") };
        var calls = new ConcurrentDictionary<string, int>();
        MergeCheck.Evaluation Evaluate(string project, string[] props)
        {
            calls.AddOrUpdate(project, 1, (_, count) => count + 1);
            return project switch
            {
                "tests/one.csproj" => new([], ["src/dependency.csproj"]),
                "src/dependency.csproj" => new(["src/globbed-new.cs"], []),
                _ => new([], [])
            };
        }
        var closures = MergeCheck.Closures(catalog, [], "sdk", Evaluate);
        Assert.Equal(1, calls["tests/one.csproj"]);
        Assert.Contains("src/globbed-new.cs", closures["same"]);
        var plan = Select(["README.md", "src/globbed-new.cs"], catalog, closures);
        Assert.False(plan.Wide);
        Assert.Equal(new[] { "one", "same" }, plan.Suites.Keys.Order());
        Assert.Equal(new[] { "two" }, Select(["tests/two.csproj"], catalog, closures).Suites.Keys);
    }

    [Fact]
    public void EvaluationReadsEveryInputKindAndProjectImports()
    {
        using var tree = new StaticTree();
        var items = new JsonObject();
        foreach (var kind in new[] { "Compile", "EmbeddedResource", "None", "Content", "ProjectReference" })
            items[kind] = new JsonArray(new JsonObject { ["FullPath"] = Path.Combine(tree.Root, kind), ["DefiningProjectFullPath"] = Path.Combine(tree.Root, "import.props") });
        var result = MergeCheck.Evaluate(tree.Root, "test.csproj", [], (_, args, _, _) =>
        {
            Assert.Contains("-getItem:Compile,EmbeddedResource,None,Content,ProjectReference", args);
            return new(0, new JsonObject { ["Items"] = items, ["Properties"] = new JsonObject { ["MSBuildAllProjects"] = Path.Combine(tree.Root, "all.props") } }.ToJsonString(), "");
        }, "dotnet");
        Assert.Equal(new[] { "ProjectReference" }, result.Projects);
        Assert.Equal(new[] { "Compile", "Content", "EmbeddedResource", "None", "all.props", "import.props", "test.csproj" }, result.Inputs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("src/Adapters/V17/x.cs")]
    [InlineData("src/Adapters/Native/x.cs")]
    [InlineData("src/PlcWorker/new.cs")]
    public void AdapterChangesBuildEightReleasesAndSelectSourceReaders(string path)
    {
        var plan = Select([path]);
        Assert.True(plan.Table);
        Assert.False(plan.Wide);
        foreach (var name in new[] { "foundation-api", "adapter-contracts", "kept-code", "plc-exchange-worker-v20", "plc-exchange-worker-v21" }) Assert.Contains(name, plan.Suites.Keys);
        Assert.Contains(MergeCheck.ConfigurationProject, Select([path], closures: new() { ["configuration-page"] = new() { path } }).Builds.Keys);
        Assert.Equal(path.StartsWith("src/Adapters/", StringComparison.Ordinal), plan.Builds.ContainsKey(MergeCheck.DiagnosticsProject));
    }

    [Fact]
    public void LogicFoundationAndSharedClosureReachTheWholeTable()
    {
        Assert.True(Select(["src/Logic/a.cs"], closures: new() { ["foundation-api"] = new() { "src/Logic/a.cs" } }).Table);
        Assert.True(Select(["src/Shared/BundleLayout.cs"], closures: new() { ["table:0"] = new() { "src/Shared/BundleLayout.cs" } }).Table);
        var plan = Select(["named-source.cs"], mentions: (name, _) => name == "named-source.cs");
        Assert.False(plan.Wide);
        Assert.Contains("kept-code", plan.Suites.Keys);
    }

    [Theory]
    [InlineData("src/Logic/a.cs", false)]
    [InlineData("src/FoundationHost/a.cs", false)]
    [InlineData("src/Studio/a.cs", true)]
    [InlineData("src/Shared/a.cs", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("reference/x", true)]
    public void D1OnlyDropsGuiForExclusivelyHostOrLogicReasons(string path, bool gui)
    {
        var closures = new Dictionary<string, HashSet<string>> { ["studio-gui"] = new() { "src/Logic/a.cs", path }, ["workbench-control"] = new() { "src/Logic/a.cs", path } };
        var plan = Select(["src/Logic/a.cs", path], closures: closures);
        Assert.Equal(gui, plan.Suites.ContainsKey("studio-gui"));
        Assert.Equal(gui, plan.Suites.ContainsKey("workbench-control"));
    }

    [Fact]
    public void CompanionWithoutSupersetKeepsRegisteredFilterAndGate()
    {
        var catalog = Catalog();
        catalog["release-tool"]!["companions"] = new JsonArray("workbench-control");
        var plan = Select(["build-tools/release/New.cs"], catalog);
        Assert.Contains("workbench-control", plan.Suites.Keys);
        Assert.DoesNotContain("studio-gui", plan.Suites.Keys);
        Assert.DoesNotContain("workbench-control", plan.Derived.Keys);
        Assert.Equal("FullyQualifiedName~Control", (string?)catalog["workbench-control"]!["arguments"]![1]);
    }

    [Fact]
    public void FoundationSubsetsUseTheirOwnMinimumOnSyntheticRows()
    {
        using var tree = new StaticTree();
        var catalog = Catalog();
        var names = new[] { "foundation", "worker-robustness", "workbench-control-host", "shared-pipeline", "shared-lifecycle-plc" };
        foreach (var name in names) catalog[name] = Suite("tests/foundation.csproj", "--filter", "FullyQualifiedName~" + name.Replace('-', '_'));
        var plan = Select(["src/Logic/a.cs"], catalog, new() { ["foundation-api"] = new() { "src/Logic/a.cs" } });
        Assert.Equal(names.Order(), plan.Derived.Keys.Order());
        var trx = Path.Combine(tree.Root, "source.trx");
        WriteTrx(trx, names.Select(name => name.Replace('-', '_')).ToArray());
        Assert.Empty(DotnetSuites.EvaluateTrx(trx, 5, 0).Errors);
        foreach (var name in names) Assert.Equal(0, DerivedSuiteGates.Evaluate(name, catalog[name]!.AsObject(), trx, Path.Combine(tree.Root, name), "foundation-api").ExitCode);
        catalog["shared-pipeline"]!["minimumPassed"] = 2;
        Assert.Equal(1, DerivedSuiteGates.Evaluate("shared-pipeline", catalog["shared-pipeline"]!.AsObject(), trx, Path.Combine(tree.Root, "failed"), "foundation-api").ExitCode);
    }

    [Fact]
    public void TraversalHasSeparateRestoreAndBuildTargets()
    {
        var document = MergeCheck.Traversal(["one.csproj", MergeCheck.ConfigurationProject, MergeCheck.DiagnosticsProject, "one.csproj"]);
        Assert.Equal(3, document.Descendants("MergeProject").Count());
        var tasks = document.Descendants("MSBuild").ToArray();
        Assert.Equal(new[] { "Restore", "Build" }, tasks.Select(task => (string?)task.Attribute("Targets")));
        Assert.Equal("false", (string?)tasks[0].Attribute("BuildInParallel"));
        Assert.Equal("true", (string?)tasks[1].Attribute("BuildInParallel"));
    }

    [Fact]
    public void ThreeLanesRespectCapacityAndContinueAfterFailure()
    {
        using var tree = new StaticTree();
        var active = new int[3]; var peak = new int[3]; var completed = 0;
        var gate = new object();
        (string, Func<PipelineResult>)[] Jobs(int lane) => Enumerable.Range(0, 6).Select(index => ($"lane{lane}-{index}", (Func<PipelineResult>)(() =>
        {
            lock (gate) { active[lane]++; peak[lane] = Math.Max(peak[lane], active[lane]); }
            Thread.Sleep(10);
            lock (gate) active[lane]--;
            Interlocked.Increment(ref completed);
            return new($"lane{lane}-{index}", index == 0 ? 1 : 0, "", "fixture");
        }))).ToArray();
        var result = PlainShards.RunLanes(tree.Root, 2, Jobs(0), Jobs(1), Jobs(2));
        Assert.Equal(18, completed);
        Assert.Equal(18, result.Count);
        Assert.InRange(peak[0], 1, 2); Assert.Equal(1, peak[1]); Assert.Equal(1, peak[2]);
    }

    [Fact]
    public void MissingShardTrxFailsPlainMergedGateAndAllShardsRun()
    {
        using var tree = new StaticTree();
        int calls = 0;
        var catalog = new JsonObject { ["fixture"] = Suite("tests/fixture.csproj") };
        var options = Options.Parse(["-Suite=fixture", "-Shards=3", "-MaxParallelism=2", "-ResultsDirectory=" + tree.Root], new HashSet<string>());
        var result = PlainShards.Run(tree.Root, options, catalog, (_, args, _, env) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("test", args.First()); Assert.Contains("--no-build", args);
            int index = int.Parse(env!["TIA_MCP_TEST_SHARD_INDEX"]!);
            if (index != 1) WriteTrx(Path.Combine(tree.Root, "fixture", "shard-" + index, "fixture.trx"), ["Class" + index]);
            return new(0, "", "");
        });
        Assert.Equal(3, calls);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing shard TRX", result.StandardError);
    }

    [Fact]
    public void SummarySortsSecondsAndNamesEveryFailure()
    {
        var summary = MergeCheck.Summary([new("fast", 1, new(1, "", "failed suite")), new("slow", 9, new(1, "", "failed build")), new("middle", 5, new(1, "", "failed check"))], 10);
        Assert.StartsWith("slow: 9.000 s\nmiddle: 5.000 s\nfast: 1.000 s", summary);
        foreach (var name in new[] { "slow", "middle", "fast" }) Assert.Contains("FAIL " + name, summary);
    }

    [Fact]
    public void StandaloneChecksEnforceCountsAndRejectNativeExecution()
    {
        using var tree = new StaticTree();
        var success = new CommandResult(0, "Passed: 157\n", "");
        Assert.Equal(0, MergeCheck.CheckResult("configuration-page", success, tree.Root).ExitCode);
        Assert.Equal(1, MergeCheck.CheckResult("configuration-page", success with { StandardOutput = "Passed: 156\n" }, tree.Root).ExitCode);
        var report = new JsonObject { ["behaviorChecks"] = 36, ["rejectionChecks"] = 5, ["nativeTiaExecuted"] = false };
        tree.Write("native-diagnostics/result.json", report.ToJsonString());
        Assert.Equal(0, MergeCheck.CheckResult("native-diagnostics", success, tree.Root).ExitCode);
        report["behaviorChecks"] = 35;
        tree.Write("native-diagnostics/result.json", report.ToJsonString());
        Assert.Equal(1, MergeCheck.CheckResult("native-diagnostics", success, tree.Root).ExitCode);
        report["behaviorChecks"] = 36; report["nativeTiaExecuted"] = true;
        tree.Write("native-diagnostics/result.json", report.ToJsonString());
        Assert.Equal(1, MergeCheck.CheckResult("native-diagnostics", success, tree.Root).ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndToEndContinuesAfterBuildSuiteAndCheckFailures(bool variant)
    {
        using var tree = new StaticTree();
        foreach (var folder in new[] { "scripts", "src", "build-tools" }) Directory.CreateDirectory(Path.Combine(tree.Root, folder));
        var catalog = new JsonObject
        {
            ["fixture"] = Suite("tests/one.csproj", variant ? ["-p:FixtureRoot=" + tree.Root + new string('x', 240)] : []),
            ["source-contracts"] = Suite("tests/two.csproj")
        };
        tree.Write("tests/test-suites.json", catalog.ToJsonString());
        tree.Write("tests/one.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        tree.Write("tests/two.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var calls = new ConcurrentQueue<string[]>();
        var environments = new ConcurrentQueue<(string Kind, string? Reports)>();
        CommandResult Process(string exe, IEnumerable<string> arguments, string root, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray(); calls.Enqueue(args);
            var kind = args[0] == "test" ? "test" : args[0] == "build" || args.Contains("-t:Build") || args.Contains("-t:Restore") ? "build" : "check";
            environments.Enqueue((kind, env is not null && env.TryGetValue("TIA_MCP_ANALYZER_REPORT_DIRECTORY", out var reports) ? reports : null));
            if (args.Any(arg => arg.StartsWith("-getItem:", StringComparison.Ordinal)))
                return new(0, new JsonObject { ["Items"] = new JsonObject { ["Compile"] = new JsonArray(new JsonObject { ["FullPath"] = Path.Combine(root, "source.cs") }) }, ["Properties"] = new JsonObject { ["MSBuildAllProjects"] = "" } }.ToJsonString(), "");
            if (args[0] == "test")
            {
                var directory = args[Array.IndexOf(args, "--results-directory") + 1];
                var name = args.Single(arg => arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal))[16..];
                WriteTrx(Path.Combine(directory, name), ["Fixture"]);
                return new(args[1].EndsWith("one.csproj", StringComparison.Ordinal) ? 1 : 0, "", "suite fixture");
            }
            return new(args.Contains("-t:Build") || args.Contains("check-tools-list") ? 1 : 0, "", "fixture");
        }
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "abc" : args[0] == "diff" && args[^1] == "HEAD" ? "source.cs\0" : "", "");
        Assert.Equal(1, MergeCheck.Run(tree.Root, Options.Parse([], new HashSet<string>()), Process, Git));
        var snapshot = calls.ToArray();
        Assert.True(Array.FindIndex(snapshot, args => args.Contains("-t:Restore")) < Array.FindIndex(snapshot, args => args.Contains("-t:Build")));
        Assert.Equal(2, snapshot.Count(args => args[0] == "test"));
        Assert.Contains(snapshot, args => args.Contains("check-tools-list"));
        Assert.Contains(snapshot, args => args.Contains("check-tia-features"));
        Assert.All(environments.Where(row => row.Kind == "build"), row => Assert.Equal(Path.GetFullPath(Path.Combine(tree.Root, "bin-build/merge-check/analyzers")), Path.GetFullPath(row.Reports!)));
        Assert.All(environments.Where(row => row.Kind != "build"), row => Assert.Null(row.Reports));
    }

    private static void WriteTrx(string path, string[] classes)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var ids = classes.Select(_ => Guid.NewGuid().ToString()).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "TestDefinitions", classes.Select((cls, i) => new XElement(ns + "UnitTest", new XAttribute("id", ids[i]),
                new XElement(ns + "TestMethod", new XAttribute("className", cls), new XAttribute("name", "Test"))))),
            new XElement(ns + "Results", classes.Select((cls, i) => new XElement(ns + "UnitTestResult", new XAttribute("testId", ids[i]),
                new XAttribute("executionId", Guid.NewGuid()), new XAttribute("testName", cls + ".Test"), new XAttribute("outcome", "Passed")))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(ns + "Counters",
                new XAttribute("total", classes.Length), new XAttribute("executed", classes.Length), new XAttribute("passed", classes.Length),
                new XAttribute("failed", 0), new XAttribute("notExecuted", 0))))).Save(path);
    }
}
