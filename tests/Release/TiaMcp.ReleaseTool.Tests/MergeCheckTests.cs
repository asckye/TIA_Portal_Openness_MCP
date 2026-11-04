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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComposedToolArgumentsParseWithTheRealOptionTable(bool docsOnly)
    {
        var plan = Select(docsOnly ? ["README.md"] : ["Directory.Build.props"]);
        var jobs = plan.Checks.Where(job => job.Arguments[0] == typeof(Program).Assembly.Location).ToArray();
        Assert.NotEmpty(jobs);
        foreach (var job in jobs)
        {
            Assert.Contains(job.Arguments[1], CommandLine.Commands);
            Options.Parse(job.Arguments[2..], Program.BooleanOptions);
        }
    }

    [Theory]
    [InlineData("adapter-inputs", "input-results.json")]
    [InlineData("worker-isolation", "worker-isolation-results.json")]
    public void SuccessfulEvidenceJobRequiresItsResultFile(string name, string file)
    {
        using var tree = new StaticTree();
        var result = new CommandResult(0, "passed", "");
        Assert.Contains(file, Assert.Throws<ReleaseException>(() => MergeCheck.CheckResult(name, result, tree.Root)).Message);
        tree.Write(name + "/" + file, "{}");
        Assert.Equal(result, MergeCheck.CheckResult(name, result, tree.Root));
        Assert.Equal(7, MergeCheck.CheckResult(name, result with { ExitCode = 7 }, tree.Root).ExitCode);
    }

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
    [InlineData("docs/manuals/example.md")]
    [InlineData("")]
    public void DocsPlanHasExactlyThreeChecksAndNoBuildsOrSuites(string path)
    {
        var plan = Select(path.Length == 0 ? [] : [path]);
        Assert.True(plan.DocsOnly);
        Assert.Empty(plan.Builds);
        Assert.Empty(plan.Suites);
        Assert.Equal(new[] { "check-repository", "check-dead-tool-references", "generate-tool-counts" }, plan.Checks.Select(job => job.Name));
        Assert.DoesNotContain("-NoAnalyzer", plan.Checks[0].Arguments);
    }

    [Theory]
    [InlineData("reference/x.md")]
    [InlineData("reference/tool-examples/metadata.json")]
    [InlineData("manifest/contracts/v4/responses/example.json")]
    [InlineData("manifest/package-manifest.json")]
    [InlineData("hooks/hooks.json")]
    [InlineData("schemas/input.json")]
    [InlineData("plugin/entry.json")]
    [InlineData(".claude-plugin/plugin.json")]
    [InlineData("third_party/library/source.cs")]
    [InlineData("scripts/ecosystem/tool.py")]
    [InlineData("scripts/operations/tool.py")]
    [InlineData("templates/standards/tiamcp.basic/README.md")]
    [InlineData("docs/reference/real-machine-ledger.md")]
    [InlineData("docs/reference/version-tool-catalog.md")]
    [InlineData("docs/reference/tool-matrix.md")]
    [InlineData("docs/releases/v4.0.0-tool-migration.md")]
    public void UnmappedAndGeneratedDocumentsSelectEverythingAndNameTheirReason(string path)
    {
        var plan = Select([path], closures: new() { ["release-tool"] = new() { path } }, mentions: (_, _) => true);
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
    public void RealUnmappedPropsFileStaysWideDespiteTextMentions()
    {
        const string path = "src/Studio/Directory.Build.props";
        Assert.True(File.Exists(Path.Combine(StaticCheckVectorTests.Root, path)));
        var plan = Select([path], mentions: (_, _) => true);
        Assert.True(plan.Wide);
        Assert.Equal(Catalog().Count, plan.Suites.Count);
        Assert.Contains(path, plan.Builds["eight-release-table"]);
    }

    [Theory]
    [InlineData("src/DeletedGlobbedSource.cs")]
    [InlineData("src/Studio/Gui/RenamedAway.xaml")]
    [InlineData("new-top-level/file.cs")]
    public void TextMentionsCannotMapAnOtherwiseUnknownPath(string path)
    {
        var plan = Select([path], mentions: (_, _) => true);
        Assert.True(plan.Wide);
        Assert.Equal(Catalog().Count, plan.Suites.Count);
        Assert.Contains(path, plan.Builds["eight-release-table"]);
    }

    [Fact]
    public void TextMentionsAddSuitesWhenAClosureAlreadyMapsThePath()
    {
        var catalog = new JsonObject { ["one"] = Suite("tests/one/a.csproj"), ["two"] = Suite("tests/two/b.csproj"), ["three"] = Suite("tests/three/c.csproj") };
        var plan = Select(["src/shared.cs"], catalog, new() { ["one"] = new() { "src/shared.cs" } },
            mentions: (name, directory) => name == "shared.cs" && directory == "tests/two");
        Assert.False(plan.Wide);
        Assert.Equal(new[] { "one", "two" }, plan.Suites.Keys.Order());
    }

    [Fact]
    public void EvaluationReadsEveryItemKindIncludingWpfInputs()
    {
        using var tree = new StaticTree();
        var items = new JsonObject();
        string[] kinds = ["Compile", "EmbeddedResource", "None", "Content", "Page", "ApplicationDefinition", "Resource", "ProjectReference"];
        foreach (var kind in kinds)
            items[kind] = new JsonArray(new JsonObject { ["FullPath"] = Path.Combine(tree.Root, kind), ["DefiningProjectFullPath"] = Path.Combine(tree.Root, "test.csproj") });
        var result = MergeCheck.Evaluate(tree.Root, "test.csproj", [], (_, args, _, _) =>
        {
            Assert.Contains("-getItem:Compile,EmbeddedResource,None,Content,Page,ApplicationDefinition,Resource,ProjectReference", args);
            return new(0, new JsonObject { ["Items"] = items, ["Properties"] = new JsonObject { ["MSBuildAllProjects"] = "" } }.ToJsonString(), "");
        }, "dotnet");
        Assert.Equal(new[] { "ProjectReference" }, result.Projects);
        Assert.Equal(kinds.Where(kind => kind != "ProjectReference").Append("test.csproj").Order(StringComparer.Ordinal), result.Inputs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Page", "src/Studio/Gui/Views/Pane.xaml")]
    [InlineData("ApplicationDefinition", "src/Studio/Gui/App.xaml")]
    [InlineData("Resource", "src/Studio/Gui/Resources/Icon.png")]
    public void WpfItemsSelectTheirOwningSuiteWithoutGoingWide(string kind, string path)
    {
        using var tree = new StaticTree();
        var catalog = new JsonObject { ["gui"] = Suite("tests/gui.csproj"), ["other"] = Suite("tests/other.csproj") };
        var closures = MergeCheck.Closures(catalog, [], "sdk", (project, properties) => MergeCheck.Evaluate(tree.Root, project, properties,
            (_, _, _, _) => new(0, new JsonObject
            {
                ["Items"] = new JsonObject { [kind] = project == "tests/gui.csproj" ? new JsonArray(new JsonObject { ["FullPath"] = Path.Combine(tree.Root, path) }) : new JsonArray() },
                ["Properties"] = new JsonObject { ["MSBuildAllProjects"] = "" }
            }.ToJsonString(), ""), "dotnet"));
        var plan = Select([path], catalog, closures);
        Assert.False(plan.Wide);
        Assert.Equal(new[] { "gui" }, plan.Suites.Keys);
    }

    [Theory]
    [InlineData("scripts/checks/Test-CampaignInputs.py")]
    [InlineData("scripts/checks/Test-CampaignInputs.cs")]
    public void CampaignSelfTestsAlwaysBuildTheValidator(string path)
    {
        var plan = Select([path]);
        Assert.False(plan.Wide);
        Assert.Contains(path, plan.Builds["file:scripts/checks/Test-CampaignInputs.cs"]);
        Assert.Contains(plan.Checks, job => job.Name == "campaign-self-test");
        Assert.Contains(plan.Checks, job => job.Name == "campaign-validator-self-test");
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
        Assert.True(plan.Wide);
        Assert.Equal(Catalog().Count, plan.Suites.Count);
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
        catalog["fixture"]!["minimumPassed"] = 100;
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

    private static string WriteSdk(StaticTree tree, string directory = "sdk")
    {
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            var relative = $"src/Adapters/{PrepareBuild.AdapterFolder(key)}/Release.props";
            var source = File.ReadAllText(Path.Combine(StaticCheckVectorTests.Root, relative));
            tree.Write(relative, source);
            foreach (var api in XDocument.Parse(source).Descendants("AdapterApi"))
            {
                var name = ((string)api.Attribute("Include")!).Replace('\\', '/').Split('/')[^1];
                tree.Write(Path.Combine(directory, ReleaseCommands.PlcWorkerApi(key).Directory, name), "existence-only SDK fixture");
            }
        }
        return Path.Combine(tree.Root, directory);
    }

    [Fact]
    public void WorktreeSdkFallsBackToTheCommonCheckoutAndExplicitPathsWin()
    {
        using var tree = new StaticTree();
        var sdk = WriteSdk(tree, "checkout/sdk");
        var python = tree.Write("python.exe", "fixture");
        var common = Path.Combine(tree.Root, "checkout/.git");
        CommandResult Git(string[] args)
        {
            Assert.Equal(new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, args);
            return new(0, common, "");
        }
        var found = MergeCheck.RequirePrerequisites(tree.Root, Options.Parse(["-Python", python], new HashSet<string>()), Git, _ => null);
        Assert.Equal(Path.GetFullPath(sdk), found.Api);
        Assert.Equal(python, found.Python);
        var local = WriteSdk(tree);
        var localFound = MergeCheck.RequirePrerequisites(tree.Root, Options.Parse(["-Python", python], new HashSet<string>()), _ => throw new InvalidOperationException("Local SDK needs no fallback"), _ => null);
        Assert.Equal(Path.GetFullPath(local), localFound.Api);
        var explicitFound = MergeCheck.RequirePrerequisites(tree.Root, Options.Parse(["-PublicApiRoot", sdk, "-Python", python], new HashSet<string>()),
            _ => throw new InvalidOperationException("Explicit SDK needs no fallback"), name => name == "TIA_MCP_TEST_PUBLIC_API_ROOT" ? local : null);
        Assert.Equal(Path.GetFullPath(sdk), explicitFound.Api);
    }

    [Fact]
    public void MissingPrerequisitesFailBeforeEvaluationOrBuild()
    {
        using var tree = new StaticTree();
        var sdk = WriteSdk(tree);
        var python = tree.Write("python.exe", "fixture");
        var badSdk = Assert.Throws<ReleaseException>(() => MergeCheck.RequirePrerequisites(tree.Root,
            Options.Parse(["-PublicApiRoot", Path.Combine(tree.Root, "missing"), "-Python", python], new HashSet<string>()), _ => throw new InvalidOperationException(), _ => null));
        Assert.Contains("PublicAPI SDK", badSdk.Message);
        var missingPython = Assert.Throws<ReleaseException>(() => MergeCheck.RequirePrerequisites(tree.Root,
            Options.Parse(["-PublicApiRoot", sdk], new HashSet<string>()), _ => throw new InvalidOperationException(), _ => null));
        Assert.Contains("-Python", missingPython.Message);
        var envFound = MergeCheck.RequirePrerequisites(tree.Root, Options.Parse([], new HashSet<string>()), _ => throw new InvalidOperationException(),
            name => name == "TIA_MCP_TEST_PUBLIC_API_ROOT" ? sdk : name == ReleaseCommands.DecoderPythonVariable ? python : null);
        Assert.Equal(python, envFound.Python);
        tree.Write("tests/test-suites.json", new JsonObject { ["fixture"] = Suite("tests/one.csproj") }.ToJsonString());
        tree.Write("tests/one.csproj", "<Project />");
        Assert.Throws<ReleaseException>(() => MergeCheck.Run(tree.Root,
            Options.Parse(["-PublicApiRoot", sdk, "-Python", "python"], new HashSet<string>()),
            (_, _, _, _) => throw new InvalidOperationException("No process should run before validation"),
            args => new(0, args[0] == "merge-base" ? "abc" : args[0] == "diff" ? "source.cs\0" : "", ""), (_, _) => 0));
        Assert.False(Directory.Exists(Path.Combine(tree.Root, "bin-build/merge-check")));
    }

    [Fact]
    public void DocsOnlyRunNeedsNeitherSdkNorPython()
    {
        using var tree = new StaticTree();
        tree.Write("tests/test-suites.json", new JsonObject { ["fixture"] = Suite("tests/one.csproj") }.ToJsonString());
        tree.Write("tests/one.csproj", "<Project />");
        var commands = new ConcurrentQueue<string>();
        var result = MergeCheck.Run(tree.Root, Options.Parse([], new HashSet<string>()), (_, arguments, _, _) =>
        {
            commands.Enqueue(arguments.ElementAt(1));
            return new(0, "", "");
        }, args => new(0, args[0] == "merge-base" ? "abc" : args[0] == "diff" ? "README.md\0" : "", ""),
            (_, _) => throw new InvalidOperationException("No decoder preparation for docs"));
        Assert.Equal(0, result);
        Assert.Equal(new[] { "check-dead-tool-references", "check-repository", "generate-tool-counts" }, commands.Order());
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    [InlineData("build", false, false)]
    [InlineData("suite", false, false)]
    [InlineData("suite", true, false)]
    [InlineData("check", false, false)]
    [InlineData("decoder", false, false)]
    [InlineData(null, false, true)]
    [InlineData("suite", false, true)]
    public void EndToEndAccountsForEachFailureAndCanSucceed(string? failure, bool variant, bool foundation)
    {
        using var tree = new StaticTree();
        foreach (var folder in new[] { "scripts", "src", "build-tools" }) Directory.CreateDirectory(Path.Combine(tree.Root, folder));
        var sdk = WriteSdk(tree);
        var python = tree.Write("python.exe", "fixture");
        tree.Write("source.cs", "public class Source {}\n");
        var catalog = new JsonObject
        {
            [foundation ? "foundation-api" : "fixture"] = Suite("tests/one.csproj", variant ? ["-p:FixtureRoot=" + tree.Root + new string('x', 240)] : []),
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
                return new(0, new JsonObject
                {
                    ["Items"] = new JsonObject { ["Compile"] = args[1].EndsWith("one.csproj", StringComparison.Ordinal) || args[1].EndsWith("two.csproj", StringComparison.Ordinal)
                        ? new JsonArray(new JsonObject { ["FullPath"] = Path.Combine(root, "source.cs") }) : new JsonArray() },
                    ["Properties"] = new JsonObject { ["MSBuildAllProjects"] = "" }
                }.ToJsonString(), "");
            if (args[0] == "test")
            {
                var directory = args[Array.IndexOf(args, "--results-directory") + 1];
                var name = args.Single(arg => arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal))[16..];
                WriteTrx(Path.Combine(directory, name), ["Fixture"]);
                return new(failure == "suite" && args[1].EndsWith("one.csproj", StringComparison.Ordinal) ? 1 : 0, "", "");
            }
            return new(failure == "build" && args.Contains("-t:Build") || failure == "check" && args.Contains("check-tools-list") ? 1 : 0, "", "");
        }
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "abc" : args[0] == "diff" && args[^1] == "HEAD" ? "source.cs\0" : "", "");
        int decoderCalls = 0;
        var result = MergeCheck.Run(tree.Root, Options.Parse(["-PublicApiRoot", sdk, "-Python", python], new HashSet<string>()), Process, Git,
            (options, root) =>
            {
                decoderCalls++;
                Assert.True(options.Has("Reuse")); Assert.Equal(python, options.Get("Python")); Assert.Equal(tree.Root, root);
                return failure == "decoder" ? 1 : 0;
            });
        Assert.Equal(failure == null ? 0 : 1, result);
        Assert.Equal(1, decoderCalls);
        var snapshot = calls.ToArray();
        Assert.True(Array.FindIndex(snapshot, args => args.Contains("-t:Restore")) < Array.FindIndex(snapshot, args => args.Contains("-t:Build")));
        Assert.Equal(2, snapshot.Count(args => args[0] == "test"));
        if (foundation) Assert.DoesNotContain(snapshot, args => args[0] == "build" && args[1].EndsWith("one.csproj", StringComparison.Ordinal));
        Assert.Contains(snapshot, args => args.Contains("check-tools-list"));
        Assert.Contains(snapshot, args => args.Contains("check-tia-features"));
        Assert.All(environments.Where(row => row.Kind == "build"), row => Assert.Equal(Path.GetFullPath(Path.Combine(tree.Root, "bin-build/merge-check/analyzers")), Path.GetFullPath(row.Reports!)));
        Assert.All(environments.Where(row => row.Kind != "build"), row => Assert.Null(row.Reports));
    }

    [Fact]
    public void WorkerChecksBuildFixturesAfterTheTableAndRunTransportAfterSuites()
    {
        using var tree = new StaticTree();
        foreach (var folder in new[] { "scripts", "src", "build-tools" }) Directory.CreateDirectory(Path.Combine(tree.Root, folder));
        var sdk = WriteSdk(tree);
        var python = tree.Write("python.exe", "fixture");
        const string source = "src/WorkerChannel/Channel.cs";
        tree.Write("tests/one.csproj", "<Project />");
        tree.Write("tests/test-suites.json", new JsonObject { ["fixture"] = Suite("tests/one.csproj") }.ToJsonString());
        var calls = new ConcurrentQueue<string[]>();
        CommandResult Process(string exe, IEnumerable<string> arguments, string root, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray(); calls.Enqueue(args);
            if (args.Any(arg => arg.StartsWith("-getItem:", StringComparison.Ordinal)))
                return new(0, new JsonObject
                {
                    ["Items"] = new JsonObject { ["Compile"] = args[1].EndsWith("one.csproj", StringComparison.Ordinal)
                        ? new JsonArray(new JsonObject { ["FullPath"] = Path.Combine(root, source) }) : new JsonArray() },
                    ["Properties"] = new JsonObject { ["MSBuildAllProjects"] = "" }
                }.ToJsonString(), "");
            if (args[0] == "test")
            {
                var directory = args[Array.IndexOf(args, "--results-directory") + 1];
                WriteTrx(Path.Combine(directory, "fixture.trx"), ["Fixture"]);
            }
            if (args[0].EndsWith("Test-FoundationTransport.py", StringComparison.Ordinal))
            {
                Assert.Contains("--host-exe", args);
                Assert.Equal("src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe", args[Array.IndexOf(args, "--host-exe") + 1]);
                Assert.StartsWith(Path.GetFullPath(Path.Combine(tree.Root, "bin-build/merge-check/checks/foundation-transport")), env!["TIA_MCP_DATA_DIRECTORY"]);
                Assert.True(File.Exists(Path.Combine(env["TIA_MCP_DATA_DIRECTORY"]!, "config/approval.settings")));
                Assert.Equal(env["TEMP"], env["TMP"]);
                Assert.Null(env["TIA_MCP_BUNDLE_ROOT"]);
            }
            return new(0, args.SequenceEqual(new[] { "--self-test" })
                ? "COMPLETE: 1 native harness safety checks passed; live TIA tests NOT RUN" : "", "");
        }
        CommandResult Git(string[] args) => new(0, args[0] == "merge-base" ? "abc" : args[0] == "diff" && args[^1] == "HEAD" ? source + "\0" : "", "");
        var result = MergeCheck.Run(tree.Root, Options.Parse(["-PublicApiRoot", sdk, "-Python", python], Program.BooleanOptions), Process, Git, (_, _) => 0);
        Assert.True(result == 0, string.Join("\n", Directory.EnumerateFiles(Path.Combine(tree.Root, "bin-build/merge-check"), "*.log")
            .Select(File.ReadAllText).Where(text => text.Contains("Exit code: 1", StringComparison.Ordinal))));
        var snapshot = calls.ToArray();
        int table = Array.FindIndex(snapshot, args => args.Any(arg => arg.EndsWith("table-Worker.proj", StringComparison.Ordinal)));
        var fixtures = snapshot.Select((args, index) => (args, index)).Where(row => row.args.Contains("-p:BuildProjectReferences=false")).ToArray();
        Assert.Equal(2, fixtures.Length);
        Assert.All(fixtures, row => Assert.True(row.index > table));
        Assert.Equal(new[] { "20", "21" }, fixtures.Select(row => row.args.Single(arg => arg.StartsWith("-p:TiaReleaseKey=", StringComparison.Ordinal))[17..]));
        int suite = Array.FindLastIndex(snapshot, args => args[0] == "test");
        var transports = snapshot.Select((args, index) => (args, index))
            .Where(row => row.args[0].EndsWith("Test-FoundationTransport.py", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, transports.Length);
        Assert.All(transports, row => Assert.True(row.index > suite));
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
