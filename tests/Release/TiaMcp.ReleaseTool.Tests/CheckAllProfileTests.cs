using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllProfileTests
{
    private static string Root => Repository.FindRoot();
    private static JsonObject Catalog => DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root, "windows");
    public static IEnumerable<object[]> DerivedSuites => CheckAllProfiles.Derived.Keys.Select(name => new object[] { name });
    private static string TestClass(string name) => name switch
    {
        "foundation" => "FoundationChecks", "worker-robustness" => "WorkerRobustnessTests",
        "workbench-control-host" => "WorkbenchControlHostTests", "shared-pipeline" => "SharedPipelineTests",
        "shared-lifecycle-plc" => "PlcSharedLifecycleTests", "shared-lifecycle" => "SharedLifecycleTests",
        "workbench-control" => "TiaOpenness.Gui.Tests.WorkbenchControlTests", "mcp-panel" => "TiaOpenness.Gui.Tests.McpPanelTests",
        "workbench-vci-prefill" => "TiaOpenness.Gui.Tests.VciPanelTests", "crash-evidence" => "TiaOpenness.Gui.Tests.CrashEvidencePortTests",
        "vci-read-only-binding" => "TiaOpenness.Core.Tests.VciReadOnlyBindingTests",
        _ => throw new InvalidOperationException(name)
    };

    private static DerivationPlan Plan(JsonObject? catalog = null) => CheckAllProfiles.Plan(catalog ?? Catalog, "api-root", "offline.config", "companion.exe", "dotnet");

    [Theory]
    [MemberData(nameof(DerivedSuites))]
    public void SameRunProofRejectsMissingRepeatedOrFailedSubsetCasesEvenAboveTheMinimum(string name)
    {
        using var temp = new OfflineFixtures();
        var suite = Catalog[name]!.DeepClone().AsObject(); suite["minimumPassed"] = 1; suite["maximumSkipped"] = 0;
        string cls = TestClass(name);
        var rows = new[] { (cls, "Check", "first", "Passed"), (cls, "Check", "second", "Passed") };
        string original = Trx(temp.DirectoryPath, "original", rows), source = Trx(temp.DirectoryPath, "source", rows);
        string output = Path.Combine(temp.DirectoryPath, "proof");
        Assert.Equal(0, DerivedSuiteGates.CompareSameRun(name, suite, original, source, output, "superset").ExitCode);
        var missing = Trx(temp.DirectoryPath, "missing", [rows[0]]);
        // Merge applies only the same-run registered gate. Nightly detects the missing identity independently.
        Assert.Equal(0, DerivedSuiteGates.Evaluate(name, suite, missing, output, "superset").ExitCode);
        var lost = DerivedSuiteGates.CompareSameRun(name, suite, original, missing, output, "superset");
        Assert.Equal(1, lost.ExitCode); Assert.Contains(cls, lost.StandardError);
        var duplicate = Trx(temp.DirectoryPath, "duplicate", [rows[0], (cls, "Other", "third", "Passed")]);
        Assert.Equal(1, DerivedSuiteGates.CompareSameRun(name, suite, original, duplicate, output, "superset").ExitCode);
        var actual = DerivedSuiteGates.Read(original);
        Assert.Equal(new[] { cls }, DerivedSuiteGates.DifferenceClasses(actual, [.. actual, actual[0]]));
        var failed = Trx(temp.DirectoryPath, "failed", [rows[0], (cls, "Check", "second", "Failed")]);
        var failure = DerivedSuiteGates.CompareSameRun(name, suite, original, failed, output, "superset");
        Assert.Equal(1, failure.ExitCode); Assert.Contains("1 failed checks", failure.StandardError);
        Assert.Equal(1, DerivedSuiteGates.CompareSameRun(name, suite, failed, source, output, "superset").ExitCode);
    }

    [Fact]
    public void AddingOrRenamingCasesInBothExecutionsNeedsNoBaselineUpdate()
    {
        using var temp = new OfflineFixtures(); var suite = Catalog["shared-lifecycle"]!.DeepClone().AsObject(); suite["minimumPassed"] = 1;
        var rows = new[] { ("SharedLifecycleTests", "RenamedMethod", "new display", "Passed"), ("SharedLifecycleTests", "NewMethod", "added case", "Passed") };
        string original = Trx(temp.DirectoryPath, "original", rows), source = Trx(temp.DirectoryPath, "source", rows);
        Assert.Equal(0, DerivedSuiteGates.CompareSameRun("shared-lifecycle", suite, original, source, Path.Combine(temp.DirectoryPath, "proof"), "shared-session").ExitCode);
        Assert.DoesNotContain("generate-derived-suite-rosters", CommandLine.Commands);
        Assert.False(File.Exists(Path.Combine(Root, "tests/Release/derived-suite-rosters.json")));
    }

    [Fact]
    public void ProfilesKeepEveryOriginalProcessInNightlyAndFallBackWhenProofPreconditionsFail()
    {
        var catalog = Catalog; var plan = Plan(catalog);
        Assert.Equal("merge", CheckAllProfiles.Parse(Options.Parse([], new HashSet<string>())));
        Assert.Equal("nightly", CheckAllProfiles.Parse(Options.Parse(["-Profile", "nightly"], new HashSet<string>())));
        Assert.Throws<ReleaseException>(() => CheckAllProfiles.Parse(Options.Parse(["-Profile", "quick"], new HashSet<string>())));
        Assert.Equal(11, CheckAllProfiles.Derived.Count);
        Assert.Empty(plan.Fallbacks);
        Assert.Equal(11, plan.Mappings.Count);
        foreach (var name in catalog.Select(pair => pair.Key).Except(CheckAllProfiles.StudioSuites))
        {
            Assert.True(CheckAllProfiles.RunsSeparately(name, "nightly", plan));
            Assert.Equal(!plan.Mappings.ContainsKey(name), CheckAllProfiles.RunsSeparately(name, "merge", plan));
        }
        foreach (var name in new[] { "offline", "compile-candidate", "fallback-vci-modern",
            "fallback-vci-initial", "fallback-vci-classic", "plc-exchange-worker-v20", "plc-exchange-worker-v21" })
            Assert.True(CheckAllProfiles.RunsSeparately(name, "merge", plan));
        Assert.Equal(new[] { "nightly" }, CheckAllProfiles.Profiles("foundation-shard-0", plan));
        var jobs = new List<CheckJob>(); using var temp = new OfflineFixtures();
        CheckAllProfiles.AddDerived(jobs, catalog, temp.DirectoryPath, plan);
        Assert.Equal(11, jobs.Count);
        Assert.All(jobs, job => { Assert.Null(job.ResultInputs); Assert.True(job.AllowFailedDependencies); });
        CheckAllProfiles.AddNightlyProofs(jobs = [], catalog, temp.DirectoryPath);
        Assert.Equal(11, jobs.Count);
        foreach (var (name, source) in CheckAllProfiles.Derived)
        {
            Assert.Equal(new[] { name + "-gate", source + "-gate" }, jobs.Single(job => job.Name == name + "-coverage-proof").Dependencies);
            Assert.Contains((name + "-coverage-proof", true), ReleaseEvidence.RequiredJobs(Root));
        }
        CheckAllProfiles.WriteInventory(catalog, temp.DirectoryPath, "merge", plan);
        var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.DirectoryPath, "profile-inventory.json")))!;
        Assert.Contains(inventory["suites"]!.AsArray(), row => (string?)row!["name"] == "foundation" && row["fallbackReason"] == null && (string?)row["mergeDerivedFrom"] == "foundation-api");
    }

    [Theory]
    [InlineData("project")]
    [InlineData("variant")]
    [InlineData("unit")]
    [InlineData("filter")]
    [InlineData("unknown-filter")]
    [InlineData("environment")]
    [InlineData("base-environment")]
    [InlineData("sdk")]
    [InlineData("sdk-equal")]
    [InlineData("runner")]
    [InlineData("executable")]
    [InlineData("configuration")]
    public void UnsafeDerivationsAlwaysSelectAnIndependentProcess(string defect)
    {
        var catalog = Catalog; var plan = Plan(catalog);
        var executions = plan.Executions.ToDictionary(pair => pair.Key, pair => pair.Value);
        var target = executions["foundation"];
        switch (defect)
        {
            case "project": catalog["foundation"]!["project"] = Catalog["kept-code"]!["project"]!.DeepClone(); executions["foundation"] = target with { Unit = CheckAllBuild.Suite(catalog["foundation"]!.AsObject()) }; break;
            case "variant": catalog["foundation"]!["arguments"]!.AsArray().Add("-p:DefineConstants=OTHER"); executions["foundation"] = target with { Unit = CheckAllBuild.Suite(catalog["foundation"]!.AsObject()) }; break;
            case "unit": executions["foundation"] = target with { Unit = new(target.Unit.Project, "other", []) }; break;
            case "filter": catalog["foundation-api"]!["arguments"] = new JsonArray("--filter", "FullyQualifiedName~ApiMetadataChecks"); break;
            case "unknown-filter": catalog["foundation-api"]!["arguments"] = new JsonArray("--filter", "Name=one"); break;
            case "environment": executions["foundation"] = target with { Environment = new Dictionary<string, string?> { ["UNREVIEWED_INPUT"] = "1" } }; break;
            case "base-environment": executions["foundation"] = target with { BaseEnvironment = new Dictionary<string, string?>(target.BaseEnvironment!) { ["UNREVIEWED_INPUT"] = "1" } }; break;
            case "sdk": executions["foundation"] = target with { Environment = new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = "other-sdk" } }; break;
            case "sdk-equal": executions["foundation"] = target with { Environment = new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = "api-root" } }; break;
            case "runner": executions["foundation"] = target with { Runner = "another runner" }; break;
            case "executable": executions["foundation"] = target with { Executable = "another-dotnet" }; break;
            case "configuration": executions["foundation"] = target with { Approval = "enabled=false" }; break;
        }
        var checkedPlan = CheckAllProfiles.ValidatePlan(catalog, executions);
        Assert.Contains("foundation", checkedPlan.Fallbacks.Keys); Assert.DoesNotContain("foundation", checkedPlan.Mappings.Keys);
        Assert.True(CheckAllProfiles.RunsSeparately("foundation", "merge", checkedPlan));
    }

    [Fact]
    public void ContainmentIsProvedOverFiltersRatherThanTheRowsThatHappenedToBeReturned()
    {
        JsonObject Suite(string? filter) => new() { ["arguments"] = filter == null ? new JsonArray() : new JsonArray("--filter", filter) };
        Assert.True(DerivedSuiteGates.FilterContains(Suite(null), Suite("FullyQualifiedName~WorkerRobustnessTests")));
        Assert.True(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName!~Operator"), Suite("FullyQualifiedName!~ApiMetadataChecks&FullyQualifiedName!~Operator")));
        Assert.True(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName~Tests"), Suite("FullyQualifiedName~WorkerRobustnessTests")));
        Assert.False(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName!~Operator"), Suite("FullyQualifiedName~WorkerRobustnessTests")));
        Assert.False(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName~First"), Suite("FullyQualifiedName~First|FullyQualifiedName~Second")));
    }

    [Fact]
    public void NightlyExecutesTheLocalProcessesRatherThanReusingEarlierPassesOrCiSummaryReceipts()
    {
        var options = Options.Parse(["-Profile", "nightly"], new HashSet<string>());
        Assert.False(CheckAllResultCache.Enabled(options, _ => null));
        Assert.True(CheckAllResultCache.Enabled(Options.Parse(["-Profile", "merge"], new HashSet<string>()), _ => null));
        var error = Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("check-all", Options.Parse(["-Profile", "nightly", "-CiCovered", "receipt.json"], new HashSet<string>())));
        Assert.Equal(64, error.ExitCode); Assert.Contains("local same-run", error.Message);
    }

    [Fact]
    public void ExecutionUsesTheSameEnvironmentDeclarationThatThePreconditionChecks()
    {
        using var temp = new OfflineFixtures(); var plan = Plan();
        foreach (var name in new[] { "foundation", "foundation-api", "studio-gui", "workbench-control" })
        {
            var environment = FastSuites.EnvironmentFor(Path.Combine(temp.DirectoryPath, name));
            CheckAllProfiles.ApplyEnvironment(plan.Executions[name], environment, CheckAllProfiles.StudioSuites.Contains(name));
            foreach (var pair in plan.Executions[name].Environment)
                Assert.Equal(pair.Value == "$TEMP" ? environment["TEMP"] : pair.Value, environment[pair.Key]);
        }
        Assert.Equal(CheckAllBuild.Suite(Catalog["workbench-control"]!.AsObject()).Variant, CheckAllBuild.Suite(Catalog["studio-gui"]!.AsObject()).Variant);
    }

    [Fact]
    public void StudioSuitesRunTheRegisteredOutputAssemblyLikeTheOriginalIsolatedJobs()
    {
        using var temp = new OfflineFixtures();
        var unit = CheckAllBuild.Suite(Catalog["studio-core"]!.AsObject());
        var project = Path.Combine(temp.DirectoryPath, unit.Project);
        var registered = Path.Combine(temp.DirectoryPath, "registered");
        var manifest = Path.Combine(temp.DirectoryPath, "runtime-paths.tsv");
        File.WriteAllText(manifest, string.Join("\t", project, unit.Variant, Path.Combine(temp.DirectoryPath, "check-all"), registered,
            Path.Combine(temp.DirectoryPath, "check-all", "TiaOpenness.Core.Tests.dll")) + "\n");
        Assert.Equal(Path.Combine(registered, "TiaOpenness.Core.Tests.dll"), CheckAllBuild.RegisteredTestAssembly(temp.DirectoryPath, unit, manifest));
        File.WriteAllText(manifest, string.Join("\t", project, unit.Variant, Path.Combine(temp.DirectoryPath, "check-all"), "",
            Path.Combine(temp.DirectoryPath, "check-all", "TiaOpenness.Core.Tests.dll")) + "\n");
        Assert.Throws<ReleaseException>(() => CheckAllBuild.RegisteredTestAssembly(temp.DirectoryPath, unit, manifest));
    }

    [Fact]
    public void StudioSuitesClearTheShardBundleRootLikeTheOriginalIsolatedJobs()
    {
        using var temp = new OfflineFixtures(); var plan = Plan();
        foreach (var name in CheckAllProfiles.StudioSuites)
        {
            var environment = FastSuites.EnvironmentFor(Path.Combine(temp.DirectoryPath, name));
            environment["TIA_MCP_BUNDLE_ROOT"] = temp.DirectoryPath;
            CheckAllProfiles.ApplyEnvironment(plan.Executions[name], environment, studio: true);
            Assert.Null(environment["TIA_MCP_BUNDLE_ROOT"]);
        }
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("nightly")]
    public void StudioPlansRemainSequentialAndUnknownOrStaleTimingClassesNeverFailAGate(string profile)
    {
        using var temp = new OfflineFixtures(); var jobs = new List<CheckJob>();
        CheckAllProfiles.AddStudio(jobs, temp.DirectoryPath, Catalog, Path.Combine(temp.DirectoryPath, "results"), "dotnet", profile, null);
        foreach (var name in CheckAllProfiles.StudioSuites)
        {
            var shards = jobs.Where(job => job.Name.StartsWith(name + "-shard-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(name == "studio-gui" ? 3 : 1, shards.Length);
            Assert.All(shards, job => Assert.True(job.Exclusive));
            for (int index = 1; index < shards.Length; index++) Assert.Contains(shards[index - 1].Name, shards[index].Dependencies);
        }
        var (plan, costs) = CheckAllProfiles.GuiPlan(temp.DirectoryPath, "studio-gui", 3);
        Assert.NotEqual(plan["C:TiaOpenness.Gui.Tests.WorkbenchFontStyleTests"], plan["C:TiaOpenness.Gui.Tests.WorkbenchNarrowLayoutTests"]);
        var unknown = Enumerable.Range(0, 50).Select(i => "NewClass" + i).ToArray();
        foreach (var cls in unknown) Assert.InRange(TestShard.Assign(cls, "T:" + cls + ".NewCase", cls, 3, plan, false), 0, 2);
        Assert.Equal(143, costs.Sum());
        string history = Path.Combine(temp.DirectoryPath, "bin-build/check-timings"); Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "studio-gui.classes.json"), "{\"RemovedClass\":{\"Seconds\":200},\"InvalidClass\":null}");
        Assert.Contains("C:RemovedClass", CheckAllProfiles.GuiPlan(temp.DirectoryPath, "studio-gui", 3).Item1.Keys);
        File.WriteAllText(Path.Combine(history, "studio-gui.classes.json"), "obsolete format");
        Assert.Equal(143, CheckAllProfiles.GuiPlan(temp.DirectoryPath, "studio-gui", 3).Item2.Sum());
    }

    [Fact]
    public void NormalizationChangesOnlyTheFoundationProducerOffsetAndRecognizesExactlyTheSixteenSdkOnlyRows()
    {
        const string a = "FoundationChecks.Check(row: OfflineXmlBuilderTests #100: 19 GlobalDB DB identity and layout retained)";
        Assert.Equal(DerivedSuiteGates.Normalize("FoundationChecks", a), DerivedSuiteGates.Normalize("FoundationChecks", a.Replace("#100:", "#116:")));
        Assert.Equal(a, DerivedSuiteGates.Normalize("OtherChecks", a));
        Assert.NotEqual(DerivedSuiteGates.Normalize("FoundationChecks", a), DerivedSuiteGates.Normalize("FoundationChecks", a.Replace("19 GlobalDB", "20 GlobalDB")));
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var kind in new[] { "UDT", "GlobalDB" })
        {
            var row = new DerivedSuiteGates.Case("FoundationChecks", "Check", $"FoundationChecks.Check(row: OfflineXmlBuilderTests #100: {key} {kind} official interface XSD: )", new XElement("row"), new XElement("test"));
            Assert.True(DerivedSuiteGates.FoundationSdkOnly(row));
            Assert.False(DerivedSuiteGates.FoundationSdkOnly(row with { Display = row.Display.Replace("XSD: ", "XSD changed: ") }));
            Assert.False(DerivedSuiteGates.FoundationSdkOnly(row with { Method = "Different" }));
        }
    }

    [Fact]
    public void UnshardedBudgetUsesTheLargerOfTheFormerJobHistoryAndTheShardSum()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), CheckAllProfiles.UnshardedBudget("studio-gui", 3, null));
        Assert.Equal(TimeSpan.FromSeconds(785), CheckAllProfiles.UnshardedBudget("studio-gui", 3,
            new Dictionary<string, double> { ["studio-gui"] = 261.6666666666667 }));
        var shards = new Dictionary<string, double> { ["studio-gui"] = 0.01, ["studio-gui-shard-0"] = 123, ["studio-gui-shard-1"] = 157,
            ["studio-gui-shard-2"] = 44, ["studio-gui-shard-3"] = 1000 };
        // Only the job's own shards count; the aggregate alias's near-zero history never lowers the allowance.
        Assert.Equal(TimeSpan.FromSeconds(972), CheckAllProfiles.UnshardedBudget("studio-gui", 3, shards));
    }

    [Fact]
    public void GuiShardsShareTheOriginalUnshardedTimeoutInsteadOfMultiplyingItsAllowance()
    {
        var budget = CheckScheduler.JobTimeout(new("studio-gui", () => new(0, "", ""), []), new Dictionary<string, double>());
        Assert.Equal(TimeSpan.FromSeconds(300), budget);
        Assert.Null(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(299), budget));
        Assert.NotNull(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(300), budget));
        Assert.NotNull(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(301), budget));
        var measured = CheckScheduler.JobTimeout(new("studio-gui", () => new(0, "", ""), []), new Dictionary<string, double> { ["studio-gui"] = 297 });
        Assert.Equal(TimeSpan.FromSeconds(891), measured);
        Assert.NotNull(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(891), measured));
    }

    [Fact]
    public void ShippingNamesKeepTheOldMaximumBudgetsResourcesAndClassCosts()
    {
        var costs = new Dictionary<string, double> { ["studio-gui-false"] = 800, ["studio-gui"] = 50,
            ["studio-gui-false-shard-0"] = 123, ["unrelated"] = 17 };
        CheckAllProfiles.UseShippingHistory(costs, Math.Max);
        Assert.Equal(800, costs["studio-gui"]);
        Assert.Equal(123, costs["studio-gui-shard-0"]);
        Assert.Equal(17, costs["unrelated"]);
        Assert.DoesNotContain(costs.Keys, key => key.Contains("-false", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(2400), CheckAllProfiles.UnshardedBudget("studio-gui", 3, costs));
        var usage = new CheckResourceUsage(5, 3, 1UL << 30, "owned command peak");
        var resources = new Dictionary<string, CheckResourceUsage> { ["studio-core-false-shard-0"] = usage };
        CheckAllProfiles.UseShippingHistory(resources);
        Assert.Same(usage, resources["studio-core-shard-0"]);
        using var fixture = new OfflineFixtures();
        string directory = Path.Combine(fixture.DirectoryPath, "bin-build/check-timings"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "studio-gui-false.classes.json"), "{\"LegacyClass\":{\"Seconds\":12}}");
        var (plan, weights) = CheckAllProfiles.GuiPlan(fixture.DirectoryPath, "studio-gui", 3);
        Assert.Contains("C:LegacyClass", plan.Keys);
        Assert.Equal(155, weights.Sum());
    }

    [Fact]
    public void DeletedJobHistoryCannotSizePackingButLegacyShippingHistoryAndNoSafetyCoverageRemain()
    {
        var retained = new CheckResourceUsage(1, 10, 256UL << 20, "shipping");
        var retired = new CheckResourceUsage(32, 100, 32UL << 30, "deleted");
        var history = new Dictionary<string, CheckResourceUsage>
        {
            ["studio-core-false-shard-0"] = retained, ["compile-candidate-no-safety-shard-0"] = retained,
            ["responses-v20-plc"] = retained, ["transport-usage-v21-plc"] = retained
        };
        foreach (var name in new[] { "latency-v20", "check-latency", "suite-output-parity", "offline-v20-shard-0", "kept-code-v20-gate",
            "studio-core-true-shard-0", "studio-gui-false-control-load", "stability-v20-http-lite-c8", "responses-v19-plc", "transport-usage-v14sp1-plc" })
            history[name] = retired;
        CheckAllProfiles.UseShippingHistory(history);
        var input = history.Where(pair => !CheckAllProfiles.RetiredJob(pair.Key)).ToDictionary();
        Assert.Equal(new[] { "compile-candidate-no-safety-shard-0", "responses-v20-plc", "studio-core-shard-0", "transport-usage-v21-plc" }, input.Keys.Order());
        var expected = CheckPacking.Plan(15, [retained], 8UL << 30, 32);
        var actual = CheckPacking.Plan(15, input.Values, 8UL << 30, 32);
        Assert.Empty(actual.History);
        Assert.Equal(expected, actual with { History = expected.History });
    }

    [Fact]
    public void BuildInventoryUsesOnlyTheShippingConfigurationAndKeepsAllEightReleaseWorkers()
    {
        var builds = (CheckBuild[])typeof(ReleaseCommands).GetMethod("CheckAllBuildInventory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [Catalog, Path.Combine(Root, "sdk")])!;
        Assert.DoesNotContain(builds, unit => unit.Properties.Any(property => property is "-p:TiaSharedAdapterPaths=true"));
        foreach (var name in new[] { "studio-core", "studio-gui", "offline", "kept-code" })
        {
            var project = (string)Catalog[name]!["project"]!;
            Assert.Empty(Assert.Single(builds, unit => unit.Project == project).Properties);
        }
        var compileProject = (string)Catalog["compile-candidate"]!["project"]!;
        var compileBuilds = builds.Where(unit => unit.Project == compileProject).ToArray();
        Assert.Equal(2, compileBuilds.Length);
        Assert.Empty(Assert.Single(compileBuilds, unit => unit.Variant == "default").Properties);
        Assert.Equal(new[] { "-p:TestCompileSafety=false" }, Assert.Single(compileBuilds, unit => unit.Variant != "default").Properties);
        Assert.Equal(2, builds.Count(unit => unit.Project == SuiteWorkerCopies.FoundationProject));
        Assert.Single(builds, unit => unit.Project == SuiteWorkerCopies.FoundationProject && unit.Variant == "default");
        foreach (var key in CatalogJson.Keys)
            Assert.Contains(builds, unit => unit.Project == "src/PlcWorker/TiaMcp.PlcWorker.csproj" && unit.Properties.Contains("-p:TiaReleaseKey=" + key));
    }

    internal static string Trx(string directory, string name, (string Class, string Method, string Display, string Outcome)[] rows)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        string Id((string Class, string Method, string Display, string Outcome) row) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(row.Class + "\0" + row.Method + "\0" + row.Display)));
        string run = Guid.NewGuid().ToString("N");
        var definitions = rows.Select(row => new XElement(ns + "UnitTest", new XAttribute("id", Id(row)),
            new XElement(ns + "TestMethod", new XAttribute("className", row.Class), new XAttribute("name", row.Method))));
        var results = rows.Select((row, i) => new XElement(ns + "UnitTestResult", new XAttribute("testId", Id(row)),
            new XAttribute("executionId", run + "-" + i), new XAttribute("testName", row.Display), new XAttribute("outcome", row.Outcome), new XAttribute("duration", "00:00:00.001")));
        int failed = rows.Count(row => row.Outcome == "Failed"), passed = rows.Count(row => row.Outcome == "Passed");
        var doc = new XDocument(new XElement(ns + "TestRun", new XElement(ns + "TestDefinitions", definitions), new XElement(ns + "Results", results),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", failed > 0 ? "Failed" : "Completed"), new XElement(ns + "Counters",
                new XAttribute("total", rows.Length), new XAttribute("executed", passed + failed), new XAttribute("passed", passed), new XAttribute("failed", failed), new XAttribute("notExecuted", rows.Length - passed - failed)))));
        var path = Path.Combine(directory, name + ".trx"); doc.Save(path); return path;
    }
}
