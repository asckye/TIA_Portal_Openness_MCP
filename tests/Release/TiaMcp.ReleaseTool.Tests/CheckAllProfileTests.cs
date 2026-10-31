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
        "vci-read-only-binding" => "TiaOpenness.Core.Tests.VciReadOnlyBindingTests", "kept-code-v20" => "KeptCodeTests",
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
        Assert.Equal(12, CheckAllProfiles.Derived.Count);
        Assert.Equal(new[] { "kept-code-v20", "shared-lifecycle-plc", "shared-pipeline", "workbench-control-host", "worker-robustness" }, plan.Fallbacks.Keys.Order());
        Assert.Equal(7, plan.Mappings.Count);
        foreach (var name in catalog.Select(pair => pair.Key).Except(CheckAllProfiles.StudioSuites))
        {
            Assert.True(CheckAllProfiles.RunsSeparately(name, "nightly", plan));
            Assert.Equal(!plan.Mappings.ContainsKey(name), CheckAllProfiles.RunsSeparately(name, "merge", plan));
        }
        foreach (var name in new[] { "offline", "offline-v20", "compile-candidate", "compile-candidate-no-safety", "fallback-vci-modern",
            "fallback-vci-initial", "fallback-vci-classic", "plc-exchange-worker-v20", "plc-exchange-worker-v21" })
            Assert.True(CheckAllProfiles.RunsSeparately(name, "merge", plan));
        Assert.Equal(new[] { "nightly" }, CheckAllProfiles.Profiles("foundation-shard-0", plan));
        Assert.Equal(new[] { "merge", "nightly" }, CheckAllProfiles.Profiles("kept-code-v20-shard-0", plan));
        var jobs = new List<CheckJob>(); using var temp = new OfflineFixtures();
        CheckAllProfiles.AddDerived(jobs, catalog, temp.DirectoryPath, plan);
        Assert.Equal(7, jobs.Count);
        Assert.All(jobs, job => { Assert.Null(job.ResultInputs); Assert.True(job.AllowFailedDependencies); });
        CheckAllProfiles.AddNightlyProofs(jobs = [], catalog, temp.DirectoryPath);
        Assert.Equal(12, jobs.Count);
        foreach (var (name, source) in CheckAllProfiles.Derived)
        {
            Assert.Equal(new[] { name + "-gate", source + "-gate" }, jobs.Single(job => job.Name == name + "-coverage-proof").Dependencies);
            Assert.Contains((name + "-coverage-proof", true), ReleaseEvidence.RequiredJobs(Root));
        }
        CheckAllProfiles.WriteInventory(catalog, temp.DirectoryPath, "merge", plan);
        var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.DirectoryPath, "profile-inventory.json")))!;
        Assert.Contains(inventory["suites"]!.AsArray(), row => (string?)row!["name"] == "kept-code-v20" && (string?)row["fallbackReason"] == "different project, variant or build unit");
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
        foreach (var name in new[] { "foundation", "foundation-api", "studio-gui-false", "workbench-control" })
        {
            var environment = FastSuites.EnvironmentFor(Path.Combine(temp.DirectoryPath, name));
            CheckAllProfiles.ApplyEnvironment(plan.Executions[name], environment, CheckAllProfiles.StudioSuites.Contains(name));
            foreach (var pair in plan.Executions[name].Environment)
                Assert.Equal(pair.Value == "$TEMP" ? environment["TEMP"] : pair.Value, environment[pair.Key]);
        }
        Assert.Equal(CheckAllBuild.Suite(Catalog["workbench-control"]!.AsObject()).Variant, CheckAllBuild.Suite(Catalog["studio-gui-false"]!.AsObject()).Variant);
    }

    [Fact]
    public void StudioSuitesRunTheRegisteredOutputAssemblyLikeTheOriginalIsolatedJobs()
    {
        using var temp = new OfflineFixtures();
        var unit = CheckAllBuild.Suite(Catalog["studio-core-true"]!.AsObject());
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
            Assert.Equal(name.StartsWith("studio-gui-", StringComparison.Ordinal) ? 3 : 1, shards.Length);
            Assert.All(shards, job => Assert.True(job.Exclusive));
            for (int index = 1; index < shards.Length; index++) Assert.Contains(shards[index - 1].Name, shards[index].Dependencies);
        }
        var (plan, costs) = CheckAllProfiles.GuiPlan(temp.DirectoryPath, "studio-gui-false", 3);
        Assert.NotEqual(plan["C:TiaOpenness.Gui.Tests.WorkbenchFontStyleTests"], plan["C:TiaOpenness.Gui.Tests.WorkbenchNarrowLayoutTests"]);
        var unknown = Enumerable.Range(0, 50).Select(i => "NewClass" + i).ToArray();
        foreach (var cls in unknown) Assert.InRange(TestShard.Assign(cls, "T:" + cls + ".NewCase", cls, 3, plan, false), 0, 2);
        Assert.Equal(143, costs.Sum());
        string history = Path.Combine(temp.DirectoryPath, "bin-build/check-timings"); Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "studio-gui-false.classes.json"), "{\"RemovedClass\":{\"Seconds\":200},\"InvalidClass\":null}");
        Assert.Contains("C:RemovedClass", CheckAllProfiles.GuiPlan(temp.DirectoryPath, "studio-gui-false", 3).Item1.Keys);
        File.WriteAllText(Path.Combine(history, "studio-gui-false.classes.json"), "obsolete format");
        Assert.Equal(143, CheckAllProfiles.GuiPlan(temp.DirectoryPath, "studio-gui-false", 3).Item2.Sum());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedClassesMustExistAndMergeRequiresPassesWithoutFailuresOrSkips(bool merge)
    {
        using var temp = new OfflineFixtures();
        var rows = CheckAllProfiles.SharedGuiClasses.Select(cls => ("TiaOpenness.Gui.Tests." + cls, "NewMethod", "new case", "Passed")).ToArray();
        var full = DerivedSuiteGates.Read(Trx(temp.DirectoryPath, "full", rows));
        Assert.Empty(CheckAllProfiles.SharedGuiErrors(full, merge));
        Assert.Contains(CheckAllProfiles.SharedGuiErrors(full.Skip(1), merge), error => error.Contains(CheckAllProfiles.SharedGuiClasses[0], StringComparison.Ordinal));
        foreach (var outcome in new[] { "Failed", "NotExecuted" })
        {
            var changed = rows.ToArray(); changed[0].Item4 = outcome;
            var actual = DerivedSuiteGates.Read(Trx(temp.DirectoryPath, outcome, changed));
            Assert.Equal(merge, CheckAllProfiles.SharedGuiErrors(actual, merge).Count > 0);
        }
        Assert.Empty(CheckAllProfiles.SharedGuiErrors([.. full, full[0]], merge));
    }

    [Theory]
    [InlineData("merge", "complete")]
    [InlineData("merge", "missing")]
    [InlineData("merge", "failed")]
    [InlineData("merge", "skipped")]
    [InlineData("nightly", "complete")]
    [InlineData("nightly", "missing")]
    [InlineData("nightly", "failed")]
    public void ActualStudioJobGateUsesPerClassPresenceAndTheRegisteredFullNightlyGate(string profile, string defect)
    {
        using var temp = new OfflineFixtures(); string root = temp.DirectoryPath;
        var catalog = new JsonObject();
        var cases = CheckAllProfiles.SharedGuiClasses.Select(cls => (Class: "TiaOpenness.Gui.Tests." + cls, Method: "Check", Display: cls, Outcome: "Passed")).ToArray();
        foreach (var name in CheckAllProfiles.StudioSuites)
            catalog[name] = new JsonObject { ["project"] = Catalog[name]!["project"]!.DeepClone(), ["arguments"] = Catalog[name]!["arguments"]!.DeepClone(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        var manifest = Path.Combine(root, "runtime-paths.tsv");
        File.WriteAllLines(manifest, catalog.Select(pair =>
        {
            var unit = CheckAllBuild.Suite(pair.Value!.AsObject()); string assembly = Path.Combine(root, pair.Key + ".dll"); File.WriteAllText(assembly, "fixture");
            // Real manifests carry the registered output directory in the fourth column.
            return string.Join('\t', Path.Combine(root, unit.Project), unit.Variant, assembly, root, assembly);
        }));
        CommandResult Process(string executable, IEnumerable<string> args, string cwd, IDictionary<string, string?>? environment)
        {
            var arguments = args.ToArray(); Assert.Equal("vstest", arguments[0]); Assert.Equal(Path.Combine(root, "studio-gui-true.dll"), arguments[1]);
            string directory = arguments.Single(arg => arg.StartsWith("--ResultsDirectory:", StringComparison.Ordinal))["--ResultsDirectory:".Length..];
            var plan = File.ReadLines(environment!["TIA_MCP_TEST_SHARD_PLAN"]!).Select(line => line.Split('\t'))
                .ToDictionary(fields => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])), fields => int.Parse(fields[1]));
            int count = int.Parse(environment["TIA_MCP_TEST_SHARD_COUNT"]!), index = int.Parse(environment["TIA_MCP_TEST_SHARD_INDEX"]!);
            var rows = cases.Where(row => TestShard.Assign(row.Class, "T:" + row.Display, row.Class, count, plan, false) == index)
                .Where(row => defect != "missing" || row.Class != cases[0].Class)
                .Select(row => row.Class == cases[0].Class && defect is "failed" or "skipped"
                    ? (row.Class, row.Method, row.Display, defect == "failed" ? "Failed" : "NotExecuted") : row).ToArray();
            Trx(directory, "studio-gui-true", rows); return new(rows.Any(row => row.Item4 == "Failed") ? 1 : 0, "synthetic VSTest\n", "");
        }
        var jobs = new List<CheckJob>(); var output = Path.Combine(root, "results");
        CheckAllProfiles.AddStudio(jobs, root, catalog, output, "dotnet", profile, manifest, Process);
        foreach (var job in jobs.Where(job => job.Name.StartsWith("studio-gui-true-shard-", StringComparison.Ordinal))) _ = job.Run();
        var gate = jobs.Single(job => job.Name == "studio-gui-true-gate").Run();
        Assert.Equal(defect == "complete" ? 0 : 1, gate.ExitCode);
        if (defect == "missing") Assert.Contains("Missing selected GUI class", gate.StandardError);
        if (defect == "failed") Assert.Contains("1 failed checks", gate.StandardError);
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
        Assert.Equal(TimeSpan.FromSeconds(300), CheckAllProfiles.UnshardedBudget("studio-gui-false", 3, null));
        Assert.Equal(TimeSpan.FromSeconds(785), CheckAllProfiles.UnshardedBudget("studio-gui-false", 3,
            new Dictionary<string, double> { ["studio-gui-false"] = 261.6666666666667 }));
        var shards = new Dictionary<string, double> { ["studio-gui-false"] = 0.01, ["studio-gui-false-shard-0"] = 123, ["studio-gui-false-shard-1"] = 157,
            ["studio-gui-false-shard-2"] = 44, ["studio-gui-false-shard-3"] = 1000 };
        // Only the job's own shards count; the aggregate alias's near-zero history never lowers the allowance.
        Assert.Equal(TimeSpan.FromSeconds(972), CheckAllProfiles.UnshardedBudget("studio-gui-false", 3, shards));
    }

    [Fact]
    public void GuiShardsShareTheOriginalUnshardedTimeoutInsteadOfMultiplyingItsAllowance()
    {
        var budget = CheckScheduler.JobTimeout(new("studio-gui-false", () => new(0, "", ""), []), new Dictionary<string, double>());
        Assert.Equal(TimeSpan.FromSeconds(300), budget);
        Assert.Null(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(299), budget));
        Assert.NotNull(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(300), budget));
        Assert.NotNull(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(301), budget));
        var measured = CheckScheduler.JobTimeout(new("studio-gui-false", () => new(0, "", ""), []), new Dictionary<string, double> { ["studio-gui-false"] = 297 });
        Assert.Equal(TimeSpan.FromSeconds(891), measured);
        Assert.NotNull(CheckAllProfiles.StudioRuntimeError(TimeSpan.FromSeconds(891), measured));
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
