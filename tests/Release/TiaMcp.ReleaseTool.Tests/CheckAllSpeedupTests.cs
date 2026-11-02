using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllSpeedupTests
{
    private static string Root => Repository.FindRoot();
    private static CheckJob Job(string name, params string[] dependencies) => new(name, () => new(0, "", ""), dependencies);

    [Fact]
    public void RealFormatSelectedAdapterProjectUsesTheBuildPropertiesAndSelectsOnlyItsClosure()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), text);
        }
        Write("src/PlcWorker/TiaMcp.PlcWorker.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="SelectedAdapterProject;SelectedAdapterDirectory">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <SelectedAdapterDirectory Condition="'$(TiaReleaseKey)' == '20' or '$(TiaReleaseKey)' == '21'">V$(TiaReleaseKey)</SelectedAdapterDirectory>
                <SelectedAdapterProject Condition="'$(SelectedAdapterDirectory)' != ''">../Adapters/$(SelectedAdapterDirectory)/Adapter.$(TiaReleaseKey).csproj</SelectedAdapterProject>
              </PropertyGroup>
              <ItemGroup><Compile Include="Worker.cs"/><ProjectReference Include="$(SelectedAdapterProject)" Condition="'$(SelectedAdapterProject)' != ''"/></ItemGroup>
            </Project>
            """);
        Write("src/PlcWorker/Worker.cs", "class Worker {}");
        foreach (var key in new[] { "20", "21" })
        {
            Write($"src/Adapters/V{key}/Adapter.{key}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write($"src/Adapters/V{key}/Adapter.cs", "class Adapter {}");
        }
        var builds = new[] { "20", "21" }.Select(key => new CheckBuild("src/PlcWorker/TiaMcp.PlcWorker.csproj", key, ["-p:TiaReleaseKey=" + key]));
        var affected = CheckAllAffected.DiscoverChanges(root, ["src/Adapters/V20/Adapter.cs"], builds, "dotnet", root);
        Assert.Null(affected.Fallback);
        Assert.Contains("src/Adapters/V20/Adapter.cs", affected.Units.Single(unit => unit.Id.Contains("TiaReleaseKey=20", StringComparison.Ordinal)).Inputs);
        Assert.DoesNotContain("src/Adapters/V20/Adapter.cs", affected.Units.Single(unit => unit.Id.Contains("TiaReleaseKey=21", StringComparison.Ordinal)).Inputs);
        var jobs = new[] { Job("usage-v20-plc"), Job("usage-v21-plc") };
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        affected.Map(jobs, catalog);
        Assert.Null(affected.Fallback);
        Assert.True(affected.Selected(jobs[0], CheckAllJobInputs.Find(jobs[0], catalog)));
        Assert.False(affected.Selected(jobs[1], CheckAllJobInputs.Find(jobs[1], catalog)));
        Write("src/PlcWorker/TiaMcp.PlcWorker.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"$(UnknownAdapterProject)/missing.csproj\"/></ItemGroup></Project>");
        var failed = CheckAllAffected.DiscoverChanges(root, [], builds, "dotnet", root);
        Assert.Contains("Cannot derive build input closure", failed.Fallback);
        Assert.All(jobs, job => Assert.True(failed.Selected(job, CheckAllJobInputs.Find(job, catalog))));
    }

    [Fact]
    public void ActualWorkerProjectsResolveAllEightAdapterSelectionsWithoutExecutingBuildTargets()
    {
        var api = Path.Combine(Root, "sdk");
        var builds = CatalogJson.Keys.Select(key => new CheckBuild("src/PlcWorker/TiaMcp.PlcWorker.csproj", key,
            ["-p:TiaReleaseKey=" + key, "-p:SiemensEngineeringDirectory=" + Path.Combine(api, ReleaseCommands.PlcWorkerApi(key).Directory)]));
        var discovery = CheckAllAffected.DiscoverChanges(Root, ["src/Adapters/V21/Adapter.cs"], builds, "dotnet", api);
        Assert.Null(discovery.Fallback); Assert.Equal(8, discovery.Units.Length);
        foreach (var unit in discovery.Units)
        {
            var key = unit.Id.Split('|')[1];
            var adapter = key == "14sp1" ? "V14Sp1" : key == "15.1" ? "V15_1" : "V" + key;
            Assert.Contains($"src/Adapters/{adapter}/Adapter.{key}.csproj", unit.Inputs);
            Assert.DoesNotContain(unit.Inputs, path => path.Contains("$(SelectedAdapterProject)", StringComparison.Ordinal));
        }
    }

    public static IEnumerable<object[]> TierCases => new[]
    {
        ("repository-self-tests", "src/Logic/Logic.cs", "build-tools/release/PythonScriptCalls.cs")
    }.Select(row => new object[] { row.Item1, row.Item2, row.Item3 }).Concat(
        new[] { "tools-list-self-tests", "repository-self-tests", "suite-runner-self-tests", "bundle-self-tests", "contract-self-tests",
            "check-all-speedup-self-tests", "campaign-runner-self-tests", "campaign-validator-self-tests", "plc-script-self-tests",
            "plc-harness-self-tests", "approval-self-tests", "relocation-self-tests", "native-lifecycle", "native-mcp-session" }
            .Select(name => new object[] { name, "src/Logic/Logic.cs", CheckAllMergePolicy.SelfTestInputs(name)![0] }));

    [Theory]
    [MemberData(nameof(TierCases))]
    public void TierMovesProveTheUnchangedAndAffectedPathsAndAlwaysKeepNightly(string name, string unrelated, string changed)
    {
        Assert.False(new CheckAllMergePolicy([unrelated], null).Includes(name, "merge"));
        Assert.True(new CheckAllMergePolicy([changed], null).Includes(name, "merge"));
        Assert.True(new CheckAllMergePolicy([], null).Includes(name, "nightly"));
        Assert.True(new CheckAllMergePolicy([], "unknown change set").Includes(name, "merge"));
    }

    [Theory]
    [InlineData("scripts/checks/Snapshot-ToolContracts.py")]
    [InlineData("scripts/checks/Test-DomainTools.py")]
    public void SourceContractInputsIncludeTheExtractedLexer(string script) =>
        Assert.Contains("scripts/checks/csharp_lexer.py", CheckAllScriptInputs.Closure(script));

    [Fact]
    public void TierSelectionPrunesEarlyDependenciesAndCannotCarryMergeExclusionsIntoNightly()
    {
        var policy = new CheckAllMergePolicy(["docs/README.md"], null);
        Assert.False(policy.Includes("contract-self-tests", "merge"));
        var jobs = new[] { Job("contract-self-tests"), Job("preflight", "contract-self-tests") };
        var merge = policy.Apply(jobs, "merge");
        Assert.Equal("preflight", Assert.Single(merge).Name); Assert.Empty(merge[0].Dependencies);
        var nightly = policy.Apply(jobs, "nightly");
        Assert.Equal(2, nightly.Length); Assert.Equal(jobs[1].Dependencies, nightly[1].Dependencies);
    }

    [Theory]
    [InlineData("build-tools/release/CheckAll.cs")]
    [InlineData("build-tools/release/CheckAllJobInputs.cs")]
    [InlineData("build-tools/release/CheckAllProfiles.cs")]
    [InlineData("tests/test-suites.json")]
    public void ReviewedBuildControlPathsUsePerJobMappingInsteadOfFullFallback(string path)
    {
        var selection = new CheckAllAffected([path], [], null, false);
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        selection.Map([Job("release-tool-shard-0"), Job("usage-v20-plc")], catalog);
        Assert.Null(selection.Fallback);
        Assert.False(selection.Selected(Job("usage-v20-plc"), CheckAllJobInputs.Find(Job("usage-v20-plc"), catalog)));
    }

    [Fact]
    public void TimingSeedsNeverReplaceLocalObservationsAndOnlyCopyHistory()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var main = Path.Combine(root, "main"); var work = Path.Combine(root, "work");
        var source = Path.Combine(main, "bin-build/check-timings"); var target = Path.Combine(work, "bin-build/check-timings");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "foundation-api.json"), "{\"T:Case\":10}");
        File.WriteAllText(Path.Combine(source, "interrupted.json"), "{incomplete");
        File.WriteAllText(Path.Combine(source, "check-costs.json"), "{\"job\":192}");
        File.WriteAllText(Path.Combine(target, "check-costs.json"), "local observation");
        File.WriteAllText(Path.Combine(main, "bin-build/nightly-identities.json"), "not timing history");
        ReleaseCommands.SeedCheckAllHistory(work, main);
        Assert.Equal("local observation", File.ReadAllText(Path.Combine(target, "check-costs.json")));
        Assert.Equal("{\"T:Case\":10}", File.ReadAllText(Path.Combine(target, "foundation-api.json")));
        Assert.False(File.Exists(Path.Combine(work, "bin-build/nightly-identities.json")));
        Assert.False(File.Exists(Path.Combine(target, "interrupted.json")));
    }

    [Fact]
    public void FreshWorktreesReadMainNightlyEvidenceWithoutReplacingLocalOrExplicitPointers()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var main = Path.Combine(root, "main"); var work = Path.Combine(root, "work");
        var pointer = Path.GetFullPath(Path.Combine(main, "bin-build/nightly/latest-passed.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!); Directory.CreateDirectory(work); File.WriteAllText(pointer, "{}");
        Assert.Equal(pointer, NightlyRuns.LatestPassedFor(work, main));
        var local = Path.GetFullPath(Path.Combine(work, "bin-build/nightly/latest-passed.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!); File.WriteAllText(local, "invalid local evidence stays fail-closed");
        Assert.Equal(local, NightlyRuns.LatestPassedFor(work, main));
        var configured = Path.Combine(work, "configured");
        Assert.Equal(Path.GetFullPath(Path.Combine(configured, "latest-passed.json")), NightlyRuns.LatestPassedFor(work, main, configured));
        Assert.Equal("{}", File.ReadAllText(pointer));
    }

    [Fact]
    public void UpdaterRetainsGlobalExclusionAndRunsAfterIndependentWorkWithoutDependencyCycles()
    {
        using var fixture = new OfflineFixtures();
        var order = new List<string>();
        CheckJob Run(string name, string[] deps) => new(name, () => { order.Add(name); return new(0, "", ""); }, deps);
        var jobs = CheckAllMergePolicy.UpdaterTail([Run("general", []), Run("updater-shard-0", []) with { GlobalExclusive = true },
            Run("updater-gate", ["updater-shard-0"]), Run("preflight", ["updater-gate", "general"])]);
        Assert.True(jobs.Single(job => job.Name == "updater-shard-0").GlobalExclusive);
        Assert.All(CheckScheduler.Run(jobs, 1, fixture.DirectoryPath), row => Assert.True(CheckTiming.Passed(row)));
        Assert.Equal(new[] { "general", "updater-shard-0", "updater-gate", "preflight" }, order);
    }

    [Fact]
    public void FailureLedgerAppendsWithoutOverwritingMaintainerClassifications()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        CheckAllFailureLedger.Append(root, "run-1", [new("failed", "failed", 1, 0, "fixture"), new("passed", "passed", 0, 0, null)]);
        var path = Path.Combine(root, "bin-build/check-failures.jsonl");
        Assert.Equal(root, CheckAllFailureLedger.RootFor(Path.Combine(root, "bin-build/c/snapshot"), root));
        Assert.Equal(root, CheckAllFailureLedger.RootFor(root, Path.Combine(root, "another-worktree")));
        File.WriteAllText(path, File.ReadAllText(path).Replace("unclassified", "environment", StringComparison.Ordinal));
        CheckAllFailureLedger.Append(root, "run-2", [new("blocked", "blocked", 1, 0, "dependency")]);
        var rows = File.ReadLines(path).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(2, rows.Length); Assert.Equal("environment", (string)rows[0]["classification"]!);
        Assert.Equal("unclassified", (string)rows[1]["classification"]!);
    }

    [Fact]
    public void ScriptImportGraphIncludesTransitiveAndImportlibHelpersWithoutWholeSourceTrees()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var directory = Path.Combine(root, "scripts/checks"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "job.py"), "from helper import value\nimport os\nloader('dynamic.py')\nload('stem')\n");
        File.WriteAllText(Path.Combine(directory, "helper.py"), "import nested\n");
        File.WriteAllText(Path.Combine(directory, "nested.py"), "import helper\n");
        File.WriteAllText(Path.Combine(directory, "dynamic.py"), "pass\n");
        File.WriteAllText(Path.Combine(directory, "stem.py"), "pass\n");
        var closure = CheckAllScriptInputs.Closure("scripts/checks/job.py", root);
        foreach (var path in new[] { "scripts/checks/dynamic.py", "scripts/checks/helper.py", "scripts/checks/job.py", "scripts/checks/nested.py", "scripts/checks/stem.py" })
            Assert.Contains(path, closure);
        Assert.Equal(closure.Length, closure.Distinct().Count());
        File.Delete(Path.Combine(directory, "helper.py"));
        Assert.Contains("scripts/checks/helper.py", CheckAllScriptInputs.Closure("scripts/checks/job.py", root));
        File.WriteAllText(Path.Combine(directory, "unresolved.py"), "import importlib\nimportlib.import_module(selected_by_input)\n");
        Assert.Contains("scripts/checks", CheckAllScriptInputs.Closure("scripts/checks/unresolved.py", root));
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        var inputs = CheckAllJobInputs.Find(Job("transport-usage-v20-plc"), catalog)!;
        Assert.DoesNotContain("scripts", inputs.Paths); Assert.DoesNotContain("src", inputs.Paths); Assert.DoesNotContain("tests", inputs.Paths);
        Assert.Contains("scripts/checks/call_latency.py", inputs.Paths);
    }

    [Fact]
    public void AnalyzerResultReferencesRejectChangedSourcesReportsAndMissingEvidence()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var sources = Path.Combine(root, "src"); Directory.CreateDirectory(sources);
        var reports = Path.Combine(root, "reports"); Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(sources, "Input.cs"), "class Input {}");
        File.WriteAllText(Path.Combine(reports, "compile.sarif"), "fixture report");
        var receipt = Path.Combine(root, "result.json"); CheckAllAnalyzerResult.Write(root, reports, receipt);
        Assert.True(CheckAllAnalyzerResult.Valid(root, receipt));
        File.AppendAllText(Path.Combine(sources, "Input.cs"), "// changed");
        Assert.False(CheckAllAnalyzerResult.Valid(root, receipt));
        CheckAllAnalyzerResult.Write(root, reports, receipt);
        File.AppendAllText(Path.Combine(reports, "compile.sarif"), "changed");
        Assert.False(CheckAllAnalyzerResult.Valid(root, receipt));
        Assert.False(CheckAllAnalyzerResult.Valid(root, receipt + ".missing"));
    }

    [Fact]
    public void DryPlansMeasureNarrowDocsAndWideSelectionWithHistoricalCosts()
    {
        ReleaseCommands.SeedCheckAllHistory(Root);
        var output = Path.Combine(Root, "bin-build/refactor/dry-plans"); Directory.CreateDirectory(output);
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        var derivations = CheckAllProfiles.Plan(catalog, "fixture-api", "fixture-nuget", null, "dotnet");
        var jobs = new List<CheckJob>();
        foreach (var (name, node) in catalog)
            if (CheckAllProfiles.RunsSeparately(name, "merge", derivations))
                FastSuites.AddJobs(jobs, Root, name, node!.AsObject(), Path.Combine(output, "suites"), "dotnet",
                    (int)node["minimumPassed"]! >= 100 ? 8 : 1, null, []);
        CheckAllProfiles.AddStudio(jobs, Root, catalog, Path.Combine(output, "suites"), "dotnet", "merge", null);
        CheckAllProfiles.AddDerived(jobs, catalog, Path.Combine(output, "suites"), derivations);
        foreach (var name in ReleaseCommands.CheckAllPlcScriptJobs().Concat(ReleaseCommands.CheckAllPlcHarnessJobs())
            .Concat(CheckAllJobInputs.ToolJobs).Concat(new[] { "contract-verify", "response-verify" }).Distinct())
            if (!jobs.Any(job => job.Name == name)) jobs.Add(Job(name));
        const string wide = "src/Logic/V4/ResponseEnvelope.cs";
        var units = catalog.Select(pair => new AffectedUnit(pair.Key, (string)pair.Value!["project"]!, [wide]))
            .Concat(new[] { "src/FoundationHost/TiaMcp.FoundationHost.csproj", "src/PlcWorker/TiaMcp.PlcWorker.csproj" }
                .Select(project => new AffectedUnit(project, project, [wide]))).ToArray();
        var historyPath = Path.Combine(Root, "bin-build/check-timings/check-costs.json");
        var history = File.Exists(historyPath) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(historyPath))! : [];
        var summaries = new List<object>(); var counts = new List<int>();
        foreach (var (scenario, path) in new[] { ("narrow", "src/Adapters/V21/PortedAdapter.cs"), ("docs", "docs/README.md"), ("wide", wide) })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var policy = new CheckAllMergePolicy([path], null);
            var tier = policy.Apply(jobs.Where(job => !CheckAllJobInputs.NightlyOnly(job.Name)), "merge");
            var affected = new CheckAllAffected([path], units, null, false); affected.Map(tier, catalog);
            Assert.Null(affected.Fallback);
            // Dry-plan assumption only: every unselected nonaggregate job has byte-identical passing nightly evidence.
            var selected = tier.Where(job => affected.Selected(job, CheckAllJobInputs.Find(job, catalog))).ToArray();
            counts.Add(selected.Length);
            double Cost(CheckJob job)
            {
                // Case history estimates ordering even when it is incomplete for the stricter deadline gate.
                if (job.ResultInputs is { Family: "suite", Plan: not null } spec)
                {
                    var planPath = Path.ChangeExtension(spec.Plan, ".json");
                    var index = JsonNode.Parse(spec.Definition)?["index"]?.GetValue<int>();
                    if (File.Exists(planPath) && index != null && JsonNode.Parse(File.ReadAllText(planPath))?["costs"] is JsonArray costs)
                        return costs[index.Value]!.GetValue<double>() + 5;
                }
                return history.TryGetValue(job.Name, out var observed) ? observed : ReleaseCommands.CheckAllJobCost(job, history);
            }
            var execution = selected.Select(job => job with { Dependencies = job.Dependencies
                .Concat(job.Name != "build-once" && !ReleaseCommands.CheckAllSourceOnly(job.Name) && job.Dependencies.Length == 0 ? new[] { "build-once" } : [])
                .Where(dep => selected.Any(other => other.Name == dep)).Distinct().ToArray(), EstimatedSeconds = Cost(job) }).ToArray();
            execution = CheckAllMergePolicy.UpdaterTail(execution);
            var allocated = execution.Select(job => job with { Lane = job.Name.StartsWith("stability-", StringComparison.Ordinal) ? "stability" : job.Lane,
                SensitiveGroup = job.ResultInputs?.Definition.Contains(SuiteWorkerCopies.FoundationProject, StringComparison.Ordinal) == true
                    ? SuiteWorkerCopies.FoundationProject + "#isolated-" + job.Name : job.SensitiveGroup }).ToList();
            var capacities = CheckAffinity.AssignLanes(allocated, new(0x3FUL, 0x3C0UL, 0x3FFFFC00UL, CheckAffinity.FullIsolation, null),
                CheckAffinity.DefaultCheckAllSensitiveWorkers, Enumerable.Range(0, 15).Select(core => 3UL << (core * 2)).ToArray())
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            capacities["general"] = 20; capacities["stability"] = 4;
            var pending = allocated.OrderByDescending(job => job.EstimatedSeconds).ToList();
            var active = new List<(CheckJob Job, double Until)>(); var complete = new HashSet<string>(StringComparer.Ordinal);
            double simulated = 0;
            while (pending.Count != 0 || active.Count != 0)
            {
                foreach (var job in pending.ToArray())
                {
                    if (!job.Dependencies.All(complete.Contains) || active.Any(item => item.Job.GlobalExclusive) || job.GlobalExclusive && active.Count != 0) continue;
                    if (active.Count(item => item.Job.Lane == job.Lane) >= capacities[job.Lane]) continue;
                    active.Add((job, simulated + Math.Max(.001, job.EstimatedSeconds))); pending.Remove(job);
                }
                Assert.NotEmpty(active); // A cycle or missing dependency invalidates the plan.
                simulated = active.Min(item => item.Until);
                foreach (var item in active.Where(item => item.Until <= simulated).ToArray()) { complete.Add(item.Job.Name); active.Remove(item); }
            }
            double work = execution.Sum(job => job.EstimatedSeconds);
            summaries.Add(new { scenario, changedPath = path, planningMilliseconds = watch.Elapsed.TotalMilliseconds,
                selectedJobs = selected.Select(job => job.Name), reusedIfByteIdentical = tier.Except(selected).Select(job => job.Name), tierExcluded = policy.Excluded,
                jobCosts = execution.Select(job => new { job.Name, job.EstimatedSeconds, job.Exclusive }),
                historicalJobSeconds = work, simulatedMergeGateSeconds = simulated,
                assumption = "fixture closure: shared Logic affects every suite/runtime; historical costs including cold build maximum, 20 general slots / 4 stability / three sensitive workers using the production lane allocator on a synthetic 30-CPU topology, LPT with dependencies and globally exclusive updater tail; latest passing nightly identities equal for unselected jobs; no product execution" });
        }
        Assert.True(counts[1] < counts[0]); Assert.True(counts[0] < counts[2]);
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
