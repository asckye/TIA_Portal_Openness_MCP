using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class AffectedNightlyTests
{
    private static string Root => Repository.FindRoot();
    private static JsonObject Catalog => DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
    private static CheckJob Job(string name) => new(name, () => new(0, "passed", ""), []);

    private static CheckJob[] AdoptionEvidenceJobs()
    {
        using var fixture = new OfflineFixtures();
        var manifest = Path.Combine(fixture.DirectoryPath, "runtime-paths.tsv");
        var api = Path.Combine(fixture.DirectoryPath, "api");
        File.WriteAllLines(manifest, new[] { "20", "21" }.Select(key =>
        {
            var unit = new CheckBuild("src/PlcWorker/TiaMcp.PlcWorker.csproj", "", ["-p:TiaReleaseKey=" + key,
                "-p:SiemensEngineeringDirectory=" + Path.GetFullPath(Path.Combine(api, ReleaseCommands.PlcWorkerApi(key).Directory))]);
            var directory = Path.Combine(fixture.DirectoryPath, key);
            return string.Join('\t', Path.Combine(Root, unit.Project), unit.Variant, directory, directory,
                Path.Combine(directory, "TiaMcp.PlcWorker." + key + ".exe"));
        }));
        var jobs = new List<CheckJob>();
        Func<string, string, string[], bool, CommandResult> isolated = (_, _, _, _) => new(0, "", "");
        typeof(ReleaseCommands).GetMethod("AddAdoptionEvidenceJobs", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [jobs, fixture.DirectoryPath, api, "nuget", "dotnet", manifest, isolated]);
        return jobs.ToArray();
    }

    [Fact]
    public void EngineHostReferenceBindingIsDeclaredWithTheEngineHostSuiteInputs()
    {
        var inputs = CheckAllJobInputs.Find(Job("engine-host-reference"), Catalog);
        Assert.NotNull(inputs);
        Assert.Equal(CheckAllJobInputs.Find(Job("engine-host"), Catalog)!.Projects, inputs!.Projects);
    }

    [Fact]
    public void ManagedNightlyBuildsItsCleanSnapshotAtTheOrdinaryRepositoryDepth()
    {
        var repository = Path.GetFullPath(@"D:\repo");
        var nested = Path.Combine(repository, @"bin-build\nightly\20261031-054357-3bb4fa06\worktree");
        Assert.Equal(repository, ReleaseCommands.CheckAllSnapshotBase(nested, "1", repository));
        Assert.Equal(nested, ReleaseCommands.CheckAllSnapshotBase(nested, null, repository));
        Assert.Equal(nested, ReleaseCommands.CheckAllSnapshotBase(nested, "1", null));
        var snapshot = ReleaseCommands.CheckAllSnapshotPath(ReleaseCommands.CheckAllSnapshotBase(nested, "1", repository), "84fe8194");
        Assert.True(snapshot.Length <= repository.Length + ReleaseCommands.CheckAllCleanRootMargin, snapshot);
    }

    [Fact]
    public void OnlyTheManagedNightlyLayoutCountsAsAFreshCheckAllOutput()
    {
        using var temp = new OfflineFixtures(); var output = temp.DirectoryPath;
        File.WriteAllText(Path.Combine(output, "nightly-owner.json"), "{}"); File.WriteAllText(Path.Combine(output, "nightly.log"), "");
        Directory.CreateDirectory(Path.Combine(output, "worktree"));
        Assert.True(NightlyRuns.ManagedFreshOutput(output, "1"));
        Assert.False(NightlyRuns.ManagedFreshOutput(output, null));
        File.WriteAllText(Path.Combine(output, "report.json"), "{}");
        Assert.False(NightlyRuns.ManagedFreshOutput(output, "1"));
        File.Delete(Path.Combine(output, "report.json")); File.Delete(Path.Combine(output, "nightly-owner.json"));
        Assert.False(NightlyRuns.ManagedFreshOutput(output, "1"));
    }

    [Fact]
    public void ChangeSetIncludesMergeBaseDiffStagedUnstagedDeletedAndUntrackedPaths()
    {
        var calls = new List<string[]>();
        var (changes, fallback) = CheckAllAffected.ChangeSet("base", args =>
        {
            calls.Add(args);
            return new(0, args[0] == "merge-base" ? "common\n" : args[0] == "ls-files" ? "new file.cs\0" :
                args[^1] == "HEAD" ? "staged.cs\0unstaged.cs\0deleted.cs\0" : "committed.cs\0deleted.cs\0", "");
        });
        Assert.Null(fallback);
        Assert.Equal(new[] { "committed.cs", "deleted.cs", "new file.cs", "staged.cs", "unstaged.cs" }, changes);
        Assert.Equal(new[] { "merge-base", "base", "HEAD" }, calls[0]);
        Assert.Contains(calls, args => args[^1] == "common..HEAD");
        Assert.NotNull(CheckAllAffected.ChangeSet("missing", _ => new(1, "", "bad ref")).Fallback);
    }

    [Fact]
    public void EveryStaticRegisteredAndVersionedJobHasAnInputDeclaration()
    {
        var catalog = Catalog; var jobs = new List<CheckJob>();
        Action<string, Func<CommandResult>, string[]?, bool> add = (name, run, dependencies, exclusive) => jobs.Add(new(name, run, dependencies ?? [], Exclusive: exclusive));
        Func<string, string[], CommandResult> tool = (_, _) => new(0, "", "");
        Func<string, string, string[], CommandResult> python = (_, _, _) => new(0, "", "");
        Func<string, string, string[], bool, CommandResult> isolated = (_, _, _, _) => new(0, "", "");
        typeof(ReleaseCommands).GetMethod("AddCheckAllStaticJobs", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [add, tool, python, isolated, "output", "api", "nuget", 1, 8, "dotnet"]);
        jobs.AddRange(CheckAllJobInputs.ToolJobs.Select(Job));
        jobs.AddRange(AdoptionEvidenceJobs());
        jobs.AddRange(ReleaseCommands.CheckAllPlcScriptJobs().Concat(ReleaseCommands.CheckAllPlcHarnessJobs()).Select(Job));
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var family in key is "20" or "21" ? new[] { "contracts", "responses", "transport-usage", "usage", "adapter-coverage" }
            : new[] { "contracts", "usage", "adapter-coverage" }) jobs.Add(Job(family + "-v" + key));
        foreach (var name in catalog.Select(pair => pair.Key))
            jobs.AddRange(new[] { name, name + "-gate", name + "-shard-0", name + "-coverage-proof" }.Select(Job));
        Assert.DoesNotContain(jobs, job => job.Name is "suite-output-parity" or "check-latency" || job.Name.StartsWith("latency-", StringComparison.Ordinal)
            || job.Name.StartsWith("stability-", StringComparison.Ordinal) && job.Name.Contains("-http-", StringComparison.Ordinal));
        Assert.DoesNotContain(jobs, job => new[] { "14sp1", "15.1", "16", "17", "18", "19" }.Any(key =>
            job.Name.StartsWith("responses-v" + key, StringComparison.Ordinal) || job.Name.StartsWith("transport-usage-v" + key, StringComparison.Ordinal)));
        Assert.Equal(new[] { "studio-core", "studio-gui" }, CheckAllProfiles.StudioSuites);
        foreach (var removed in new[] { "offline-v20", "kept-code-v20", "studio-core-true", "studio-core-false", "studio-gui-true", "studio-gui-false" })
            Assert.False(catalog.ContainsKey(removed));
        Assert.DoesNotContain("check-latency", CommandLine.Commands);
        Assert.Null(typeof(CheckJob).GetProperty("Quiet"));
        Assert.DoesNotContain(ReleaseEvidence.RequiredJobs(Root), row => row.Item1.StartsWith("latency-", StringComparison.Ordinal)
            || row.Item1 == "suite-output-parity");
        var selfTest = new CheckJob("affected-inputs-self-test", () => CheckAllJobInputs.Validate(jobs, catalog), []);
        var validation = selfTest.Run();
        Console.Write(validation.StandardOutput);
        Console.Error.Write(validation.StandardError);
        Assert.Empty(CheckAllJobInputs.Undeclared(jobs, catalog));
        Assert.Equal(0, validation.ExitCode);
        Assert.True(CheckAllJobInputs.Find(Job("contract-self-tests"), catalog)!.SourceOnly);
        Assert.False(CheckAllJobInputs.Find(Job("campaign-validator-self-tests"), catalog)!.SourceOnly);
        Assert.Equal("20", CheckAllJobInputs.Find(Job("plc-exchange-worker-v20-shard-0"), catalog)!.Release);
        Assert.Equal("21", CheckAllJobInputs.Find(Job("plc-exchange-worker-v21-shard-0"), catalog)!.Release);
        Assert.Contains("tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/TiaMcp.PlcWorker.FaultFixture.csproj",
            CheckAllJobInputs.Find(Job("harness-v20-native-diagnostics-only-plc"), catalog)!.Projects);
        Assert.Contains("scripts/checks/Test-CampaignInputs.cs", CheckAllJobInputs.Find(Job("harness-v21-check-modes-self-test-plc"), catalog)!.FilePrograms!);
        var unknown = Job("new-unreviewed-job");
        Assert.Equal(1, CheckAllJobInputs.Validate([unknown], catalog).ExitCode);
        Assert.True(new CheckAllAffected([], [], null, false).Selected(unknown, null));
    }

    [Theory]
    [InlineData("host-behavior-parity-v20", "20", "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj", "tests/FoundationHost/TiaMcp.FoundationHost.Tests/BehaviorParityTests.cs")]
    [InlineData("host-behavior-parity-v21", "21", "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj", "tests/FoundationHost/TiaMcp.FoundationHost.Tests/BehaviorParityTests.cs")]
    [InlineData("host-behavior-parity-v20", "20", "tests/Fixtures/TiaMcp.Offline.Fixtures/TiaMcp.Offline.Fixtures.csproj", "tests/Fixtures/TiaMcp.Offline.Fixtures/BehaviorParityCases.cs")]
    [InlineData("host-behavior-parity-v21", "21", "tests/Fixtures/TiaMcp.Offline.Fixtures/TiaMcp.Offline.Fixtures.csproj", "tests/Fixtures/TiaMcp.Offline.Fixtures/BehaviorParityCases.cs")]
    [InlineData("native-safety-v20-plc", "20", "tests/Engine/TiaMcp.Engine.Native.Tests/V20/TiaMcp.Engine.Native.Tests.V20.csproj", "tests/Engine/TiaMcp.Engine.Native.Tests/Program.cs")]
    [InlineData("native-safety-v21-plc", "21", "tests/Engine/TiaMcp.Engine.Native.Tests/V21/TiaMcp.Engine.Native.Tests.V21.csproj", "tests/Engine/TiaMcp.Engine.Native.Tests/Program.cs")]
    [InlineData("native-coverage-v20-plc", "20", "build-tools/native-call-weaver/NativeCallWeaver.csproj", "build-tools/native-call-weaver/Weaver.cs")]
    [InlineData("native-coverage-v21-plc", "21", "build-tools/native-call-weaver/NativeCallWeaver.csproj", "build-tools/native-call-weaver/Weaver.cs")]
    public void AdoptionEvidenceJobsDeclareTheirRealBuildInputs(string name, string release, string project, string source)
    {
        var jobs = AdoptionEvidenceJobs();
        var job = Assert.Single(jobs, job => job.Name == name);
        var declaration = CheckAllJobInputs.Find(job, Catalog);
        Assert.NotNull(declaration);
        Assert.Equal(release, declaration.Release);
        Assert.Contains(project, declaration.Projects);
        Assert.All(declaration.Projects, project => Assert.True(File.Exists(Path.Combine(Root, project)), project));
        Assert.False(declaration.Aggregate);
        Assert.False(CheckAllJobInputs.AlwaysRun(name));
        var selection = new CheckAllAffected([source], [new("input", project, [source])], null, true);
        selection.Map(jobs, Catalog);
        Assert.Null(selection.Fallback);
        Assert.True(selection.Selected(job, declaration));
        Assert.Contains(selection.QuickPlan(jobs, Catalog), selected => selected.Name == name);
        var unrelated = new CheckAllAffected(["docs/development/validation.md"], [], null, false);
        unrelated.Map(jobs, Catalog);
        Assert.Null(unrelated.Fallback);
        Assert.False(unrelated.Selected(job, declaration));
        if (name.StartsWith("host-behavior-parity-", StringComparison.Ordinal))
            Assert.Contains("tests/Fixtures/TiaMcp.Offline.Fixtures/TiaMcp.Offline.Fixtures.csproj", declaration.Projects);
        if (name.StartsWith("native-coverage-", StringComparison.Ordinal))
            Assert.Contains("src/PlcWorker/TiaMcp.PlcWorker.csproj", declaration.Projects);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData("src/Directory.Build.targets")]
    [InlineData("global.json")]
    [InlineData("NuGet.Config")]
    [InlineData("sdk/TIA_V20_PublicAPI/Siemens.Engineering.dll")]
    public void WideInputsFallBackToTheFullMerge(string path)
    {
        var selection = new CheckAllAffected([path], [], null, false);
        selection.Map([Job("usage-v20-plc")], Catalog);
        Assert.NotNull(selection.Fallback);
        Assert.Contains("Wide input", selection.Fallback);
        Assert.True(selection.Selected(Job("usage-v21-plc"), CheckAllJobInputs.Find(Job("usage-v21-plc"), Catalog)));
    }

    [Theory]
    [InlineData("build-tools/release/CheckAll.cs")]
    [InlineData("build-tools/release/CheckAllJobInputs.cs")]
    [InlineData("build-tools/release/CheckAllProfiles.cs")]
    [InlineData("tests/test-suites.json")]
    public void PipelineInputsSelectTheirDeclaredChecksWithoutWideFallback(string path)
    {
        var job = Job("tools-list");
        var selection = new CheckAllAffected([path], [], null, true);
        selection.Map([job], Catalog);
        Assert.Null(selection.Fallback);
        Assert.True(selection.Selected(job, CheckAllJobInputs.Find(job, Catalog)));
        Assert.Equal(job.Name, Assert.Single(selection.QuickPlan([job], Catalog)).Name);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("20")]
    [InlineData("21")]
    public void AdapterAndStudioVersionInputsArePartitionedByRelease(string key)
    {
        foreach (var path in new[] { $"src/Adapters/V{key}/Adapter.cs", $"src/Studio/Openness/V{key}/StudioRelease.cs" })
        {
            Assert.Equal(key, CheckAllAffected.ReleaseKey(path));
            var selection = new CheckAllAffected([path], [], null, false);
            var same = Job("usage-v" + key + (key is "20" or "21" ? "-plc" : "")); var other = Job(key == "21" ? "usage-v20-plc" : "usage-v21-plc");
            selection.Map([same, other], Catalog);
            Assert.Null(selection.Fallback);
            Assert.True(selection.Selected(same, CheckAllJobInputs.Find(same, Catalog)));
            Assert.False(selection.Selected(other, CheckAllJobInputs.Find(other, Catalog)));
        }
        Assert.Equal("14sp1", CheckAllAffected.ReleaseKey("src/Adapters/V14Sp1/Release.props"));
        Assert.Equal("15.1", CheckAllAffected.ReleaseKey("src/Studio/Openness/V15_1/StudioRelease.cs"));
    }

    [Theory]
    [InlineData("src/Adapters/Native/Shared.cs")]
    [InlineData("src/Adapters/build/Adapter.props")]
    [InlineData("src/PlcWorker/Worker.cs")]
    public void SharedWorkerAndNativeInputsAffectAllReleases(string path)
    {
        var jobs = new[] { "usage-v14sp1", "usage-v20-plc", "usage-v21-plc" }.Select(Job).ToArray();
        var selection = new CheckAllAffected([path], [], null, false); selection.Map(jobs, Catalog);
        Assert.Null(selection.Fallback);
        Assert.All(jobs, job => Assert.True(selection.Selected(job, CheckAllJobInputs.Find(job, Catalog))));
    }

    [Fact]
    public void CompileClosureWalksDependentsAndDataClosureSelectsJobs()
    {
        const string logic = "src/Logic/Changed.cs";
        var job = Job("usage-v21-plc"); var unrelated = Job("write-guard-shard-0") with { ResultInputs = new("suite", "{}") };
        var selection = new CheckAllAffected([logic], [new("host-unit", "src/FoundationHost/TiaMcp.FoundationHost.csproj", [logic])], null, false);
        selection.Map([job, unrelated], Catalog);
        Assert.Null(selection.Fallback);
        Assert.True(selection.Selected(job, CheckAllJobInputs.Find(job, Catalog)));
        Assert.False(selection.Selected(unrelated, CheckAllJobInputs.Find(unrelated, Catalog)));
        var data = new CheckAllAffected(["reference/tool-examples/input.json"], [], null, false); data.Map([job], Catalog);
        Assert.True(data.Selected(job, CheckAllJobInputs.Find(job, Catalog)));
        var unknown = new CheckAllAffected(["new-root/unmapped.file"], [], null, false); unknown.Map([job], Catalog);
        Assert.Contains("Unmapped", unknown.Fallback);
    }

    [Theory]
    [InlineData("contracts")]
    [InlineData("responses")]
    [InlineData("usage")]
    [InlineData("transport-usage")]
    public void ReleaseScriptDataDeclaresTheVersionCatalog(string family)
        => Assert.Contains("src/Logic/Siemens/TiaVersionCatalog.cs", CheckAllScriptInputs.Data(family));

    [Theory]
    [InlineData("contracts-v14sp1")]
    [InlineData("contracts-v20-plc")]
    [InlineData("responses-v21-plc")]
    [InlineData("usage-v20-plc")]
    [InlineData("transport-usage-v21-plc")]
    [InlineData("usage-audit-plc")]
    [InlineData("contract-self-tests")]
    [InlineData("check-all-speedup-self-tests")]
    [InlineData("plc-script-self-tests")]
    [InlineData("plc-harness-self-tests")]
    [InlineData("relocation-self-tests")]
    [InlineData("domain-source")]
    [InlineData("harness-v20-plc")]
    public void VersionCatalogOnlyChangeSelectsReleaseScriptConsumers(string name)
    {
        const string path = "src/Logic/Siemens/TiaVersionCatalog.cs";
        var catalog = Catalog;
        var job = Job(name);
        var declaration = CheckAllJobInputs.Find(job, catalog)!;
        // This existing broad source check maps the path even before the fix,
        // so an unmapped-path fallback cannot hide a missing consumer input.
        var selection = new CheckAllAffected([path], [], null, false);
        selection.Map([job, Job("contract-verify")], catalog);
        Assert.Null(selection.Fallback);
        Assert.True(selection.Selected(job, declaration));
        Assert.Contains(path, declaration.Paths);
        Assert.Contains(selection.QuickPlan([job, Job("contract-verify")], catalog), selected => selected.Name == name);
        Assert.True(new CheckAllMergePolicy([path], null).Includes(name, "merge"));
    }

    [Fact]
    public void DocsOnlyStillRunsFastChecksAndQuickNeverRunsStressOrUnchangedGui()
    {
        var jobs = new[] { Job("repository"), Job("usage-v21-plc"), Job("stability-v21-c8-stdio-full-plc"),
            Job("studio-gui-shard-0"), Job("configuration-page") };
        var selection = new CheckAllAffected(["docs/development/validation.md"], [], null, true); selection.Map(jobs, Catalog);
        Assert.Null(selection.Fallback);
        Assert.Equal(new[] { "repository" }, selection.QuickPlan(jobs, Catalog).Select(job => job.Name));
        var studio = new CheckAllAffected(["src/Studio/Gui/View.cs"],
            [new("gui", (string)Catalog["studio-gui"]!["project"]!, ["src/Studio/Gui/View.cs"])], null, true);
        studio.Map(jobs, Catalog);
        Assert.Contains(studio.QuickPlan(jobs, Catalog), job => job.Name == "studio-gui-shard-0");
        Assert.True(CheckAllJobInputs.NightlyOnly("stability-v20-c16-stdio-lite-plc"));
        Assert.False(CheckAllJobInputs.NightlyOnly("stability-v20-c8-stdio-lite-plc"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void QuickDerivedGuiGatesFollowTheirEligibleSourceSuite(bool studioChanged, bool wideInput)
    {
        var source = Job("studio-gui-shard-0");
        var sourceGate = Job("studio-gui-gate") with { Dependencies = [source.Name] };
        string[] derived = ["workbench-vci-prefill-gate", "mcp-panel-gate", "workbench-control-gate", "crash-evidence-gate"];
        var jobs = new[] { source, sourceGate, Job("repository") }
            .Concat(derived.Select(name => Job(name) with { Dependencies = [sourceGate.Name] }))
            .Append(Job("derived-tail") with { Dependencies = [derived[0]] }).Reverse().ToArray();
        var path = studioChanged ? "src/Studio/Gui/View.cs" : wideInput ? "Directory.Build.props" : "docs/development/validation.md";
        var selection = new CheckAllAffected([path],
            [new("gui", (string)Catalog["studio-gui"]!["project"]!, ["src/Studio/Gui/View.cs"])], null, true);
        selection.Map(jobs, Catalog);
        if (wideInput) Assert.Contains("Wide input", selection.Fallback);
        else Assert.Null(selection.Fallback);
        var plan = selection.QuickPlan(jobs, Catalog);
        foreach (string name in derived.Append("derived-tail").Append(source.Name).Append(sourceGate.Name))
            Assert.Equal(studioChanged, plan.Any(job => job.Name == name));
        Assert.Contains(plan, job => job.Name == "repository");
        if (studioChanged)
        {
            Assert.All(plan.Where(job => derived.Contains(job.Name)), job => Assert.Equal(new[] { sourceGate.Name }, job.Dependencies));
            Assert.Equal(new[] { source.Name }, plan.Single(job => job.Name == sourceGate.Name).Dependencies);
        }
        else
        {
            using var fixture = new OfflineFixtures();
            selection.Write(fixture.DirectoryPath, [], [], null);
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "affected.json")))!;
            foreach (string name in derived.Append("derived-tail").Append(sourceGate.Name))
                Assert.Equal("quick: source suite excluded", (string?)report["skippedBecauseUnaffected"]![name]);
        }
        var merge = new CheckAllAffected([path], [], null, false);
        Assert.Equal(jobs, merge.QuickPlan(jobs, Catalog));
    }

    private sealed class PassedNightly : IDisposable
    {
        private readonly OfflineFixtures fixture = new();
        internal string Output { get; }
        internal string Pointer { get; }
        internal string Current => Path.Combine(fixture.DirectoryPath, "current");
        internal string Identity = new('a', 64);
        internal const string Name = "sensitive-job";
        internal JsonObject Report { get; }
        internal JsonObject Evidence { get; }
        internal PassedNightly(string name = Name, string? identity = null)
        {
            Identity = identity ?? Identity;
            Output = Path.Combine(fixture.DirectoryPath, "passed-nightly"); Directory.CreateDirectory(Path.Combine(Output, "logs"));
            File.WriteAllText(Path.Combine(Output, "logs/" + name + ".log"), "passed");
            Directory.CreateDirectory(Path.Combine(Output, "suites"));
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(Output, "suites/example.trx"), ["Passed", "Passed"]);
            var owner = new CheckAllNightlyCoverage(Output + Path.DirectorySeparatorChar, "machine", null);
            owner.Record(name, Identity, ["suites/example.trx"]); owner.Save();
            Report = new JsonObject { ["profile"] = "nightly", ["failed"] = 0, ["underThirtyMinutes"] = true,
                ["checks"] = new JsonArray(new JsonObject { ["Name"] = name, ["Status"] = "passed", ["ExitCode"] = 0, ["IdentityHash"] = Identity },
                    new JsonObject { ["Name"] = "tracked-tree-drift", ["Status"] = "passed", ["ExitCode"] = 0 }) };
            Evidence = new JsonObject
            {
                ["runId"] = "passed-nightly",
                ["mode"] = new JsonObject { ["profile"] = "nightly", ["clean"] = true, ["buildCacheDisabled"] = true, ["resultCacheEnabled"] = false },
                ["source"] = new JsonObject { ["cleanAtStart"] = true },
                ["result"] = new JsonObject { ["exitCode"] = 0, ["failed"] = 0, ["reused"] = 0, ["underThirtyMinutes"] = true },
                ["planned"] = new JsonArray(name), ["completedUtc"] = DateTime.UtcNow.ToString("o"),
                ["jobs"] = new JsonArray(new JsonObject { ["name"] = name, ["status"] = "passed", ["exitCode"] = 0, ["reused"] = false },
                    new JsonObject { ["name"] = "tracked-tree-drift", ["status"] = "passed", ["exitCode"] = 0, ["reused"] = false })
            };
            Pointer = Path.Combine(fixture.DirectoryPath, "latest-passed.json"); Save();
        }
        internal void Save()
        {
            File.WriteAllText(Path.Combine(Output, "report.json"), Report.ToJsonString());
            File.WriteAllText(Path.Combine(Output, ReleaseEvidence.FileName), Evidence.ToJsonString());
            NightlyRuns.Save(fixture.DirectoryPath, new("passed-nightly", "commit", Path.Combine(Output, "report.json"), "passed", 100, true,
                DateTime.UtcNow, CheckAllResultCache.FileHash(Path.Combine(Output, "report.json")), CheckAllResultCache.FileHash(Path.Combine(Output, ReleaseEvidence.FileName)),
                CheckAllResultCache.FileHash(Path.Combine(Output, "nightly-identities.json"))));
        }
        internal CheckAllNightlyBinding Binding(string machine = "machine", bool reuse = true) =>
            new CheckAllNightlyCoverage(Current, machine, Pointer).Bind(Job(Name), () => Identity, reuse, ["suites/example.trx"]);
        public void Dispose() => fixture.Dispose();
    }

    [Theory]
    [InlineData("responses-v20-plc", false)]
    [InlineData("responses-v20-plc", true)]
    [InlineData("transport-usage-v20-plc", false)]
    [InlineData("transport-usage-v20-plc", true)]
    public void UnchangedCaptureInputsStillCheckNightlyIdentityAndRunIfBinaryDiffers(string name, bool binaryChanged)
    {
        using var binary = new OfflineFixtures();
        string host = Path.Combine(binary.DirectoryPath, "host.dll"); File.WriteAllText(host, "nightly runtime bytes");
        using var fixture = new PassedNightly(name, CheckAllResultCache.FileHash(host));
        if (binaryChanged) File.WriteAllText(host, "different runtime bytes");
        int identityChecks = 0, captures = 0;
        var capture = new CheckJob(name, () => { captures++; return new(0, "captured", ""); }, []);
        var tail = Job("response-verify") with { Dependencies = [name] };
        var selection = new CheckAllAffected(["docs/development/validation.md"], [], null, false);
        selection.Map([capture, tail], Catalog);
        Assert.Null(selection.Fallback);
        Assert.False(selection.Selected(capture, CheckAllJobInputs.Find(capture, Catalog)));
        var plan = selection.QuickPlan([capture, tail], Catalog);
        Assert.Equal(capture, Assert.Single(plan, job => job.Name == name));
        Assert.Equal(new[] { name }, Assert.Single(plan, job => job.Name == tail.Name).Dependencies);
        var nightly = new CheckAllNightlyCoverage(fixture.Current, "machine", fixture.Pointer, recordPasses: false);
        Assert.Equal("passed-nightly", nightly.NightlyId);
        var binding = nightly.Bind(capture, () => { identityChecks++; return CheckAllResultCache.FileHash(host); },
            !selection.Selected(capture, CheckAllJobInputs.Find(capture, Catalog)), ["suites/example.trx"]);
        var rows = CheckScheduler.Run([capture with { NightlyCoverage = binding }], 1, fixture.Current);
        var row = Assert.Single(rows); Assert.True(CheckTiming.Passed(row));
        Assert.True(identityChecks > 0);
        Assert.Equal(binaryChanged ? 1 : 0, captures);
        Assert.Equal(binaryChanged ? null : "passed-nightly", row.ReusedRunId);
        selection.Write(fixture.Current, rows, [], nightly.NightlyId);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Current, "affected.json")))!;
        Assert.Null(report["skippedBecauseUnaffected"]![name]);
    }


    [Fact]
    public void SensitiveJobCanReuseOnlyAnIdenticalPassedNightlyIdentity()
    {
        using var fixture = new PassedNightly(); var reused = fixture.Binding().TryReuse();
        Assert.NotNull(reused); Assert.True(CheckTiming.Passed(reused)); Assert.Equal("passed-nightly", reused.ReusedRunId);
        var suite = new JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 };
        string trx = Path.Combine(fixture.Current, "suites/example.trx"), merged = Path.Combine(fixture.Current, "merged");
        Assert.Equal(0, SuiteSharding.Merge("example", suite, [trx], merged).ExitCode);
        suite["minimumPassed"] = 3;
        Assert.Equal(1, SuiteSharding.Merge("example", suite, [trx], merged).ExitCode);
        fixture.Identity = new('b', 64);
        Assert.Null(fixture.Binding().TryReuse());
        fixture.Identity = new('a', 64);
        Assert.Null(fixture.Binding("other-machine").TryReuse());
        Assert.Null(fixture.Binding(reuse: false).TryReuse());
        File.WriteAllText(Path.Combine(fixture.Output, "logs/" + PassedNightly.Name + ".log"), "tampered");
        Assert.Null(fixture.Binding().TryReuse());
    }

    [Fact]
    public void NightlyJobIdentityBindsLoadedBinaryBytesDataDefinitionAndMachineState()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath;
        foreach (var path in new[] { "data/input.json", "outputs/product.dll", "sdk/api.dll", "offline.config", "runtime/decoder-python/stdlib.zip" })
        {
            string file = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "initial");
        }
        string manifest = Path.Combine(root, "runtime.tsv");
        File.WriteAllText(manifest, string.Join('\t', Path.Combine(root, "product/project.csproj"), "variant", Path.Combine(root, "outputs"),
            Path.Combine(root, "outputs"), Path.Combine(root, "outputs/product.dll")) + "\n");
        string machineState = "first";
        var cache = new CheckAllResultCache(root, root, manifest, Path.Combine(root, "sdk"), Path.Combine(root, "offline.config"), "dotnet", "python", null,
            Options.Parse([], new HashSet<string>()), "resources", machineStateProbe: () => machineState);
        var spec = new CheckAllResultSpec("job", "definition");
        var declaration = new CheckJobInputs("fixture", ["data"], ["product/project.csproj"], "arguments", []);
        string Identity() => cache.JobIdentity(Job("example"), spec, declaration);
        string initial = Identity(); Assert.Equal(initial, Identity());
        string binary = Path.Combine(root, "outputs/product.dll"); var timestamp = File.GetLastWriteTimeUtc(binary);
        File.WriteAllText(binary, "changed"); File.SetLastWriteTimeUtc(binary, timestamp); Assert.NotEqual(initial, Identity());
        File.WriteAllText(binary, "initial"); Assert.Equal(initial, Identity());
        string decoder = Path.Combine(root, "runtime/decoder-python/stdlib.zip");
        timestamp = File.GetLastWriteTimeUtc(decoder);
        File.WriteAllText(decoder, "changed"); File.SetLastWriteTimeUtc(decoder, timestamp); Assert.NotEqual(initial, Identity());
        File.WriteAllText(decoder, "initial"); Assert.Equal(initial, Identity());
        File.WriteAllText(Path.Combine(root, "data/input.json"), "changed"); Assert.NotEqual(initial, Identity());
        File.WriteAllText(Path.Combine(root, "data/input.json"), "initial");
        Assert.NotEqual(initial, cache.JobIdentity(Job("example"), spec with { Definition = "different" }, declaration));
        machineState = "second"; Assert.NotEqual(initial, Identity());
        File.Delete(manifest);
        Assert.NotEmpty(cache.JobIdentity(Job("source-only"), spec, declaration with { SourceOnly = true }));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("merge")]
    [InlineData("quick")]
    [InlineData("incomplete")]
    [InlineData("warm")]
    [InlineData("cached")]
    [InlineData("timeout")]
    public void FailedIncompleteMergeQuickWarmOrCachedNightliesAreNeverReused(string defect)
    {
        using var fixture = new PassedNightly();
        switch (defect)
        {
            case "failed": fixture.Report["failed"] = 1; break;
            case "merge": fixture.Report["profile"] = "merge"; break;
            case "quick": fixture.Report["quick"] = true; break;
            case "incomplete": fixture.Evidence["planned"]!.AsArray().Add("missing-job"); break;
            case "warm": fixture.Evidence["mode"]!["clean"] = false; break;
            case "cached": fixture.Evidence["mode"]!["resultCacheEnabled"] = true; break;
            case "timeout": fixture.Report["underThirtyMinutes"] = false; break;
        }
        fixture.Save(); Assert.Null(fixture.Binding().TryReuse());
    }

    [Fact]
    public void NightlyFailureBlocksUntilALaterRelevantPassAndHasNoOverride()
    {
        var now = DateTime.UtcNow;
        NightlyRun Run(string id, string revision, string status, int offset, double seconds = 100) =>
            new(id, revision, "report", status, seconds, seconds < 2700, now.AddMinutes(offset));
        bool Ancestor(string left, string right) => left == right || left is "previous" or "ancestor" && right is "ancestor" or "release";
        var failed = Run("failed", "ancestor", "failed", 1);
        Assert.Contains("No override", NightlyRuns.Block([failed], "release", "previous", Ancestor));
        Assert.NotNull(NightlyRuns.Block([Run("timeout", "release", "passed", 2, 2700)], "release", "previous", Ancestor));
        Assert.Null(NightlyRuns.Block([failed, Run("pass", "release", "passed", 2)], "release", "previous", Ancestor));
        Assert.NotNull(NightlyRuns.Block([failed, Run("other", "other-branch", "passed", 2)], "release", "previous", Ancestor));
        Assert.Null(NightlyRuns.Block([Run("old", "previous", "failed", 0)], "release", "previous", Ancestor));
        Assert.Null(NightlyRuns.Block([Run("other", "other-branch", "failed", 2)], "release", "previous", Ancestor));
        Assert.NotNull(NightlyRuns.Block([Run("current-tag", "release", "failed", 2)], "release", "release", Ancestor));
    }

    [Fact]
    public void WarmAndPartialNightlyProfilesAreDiagnosticsRatherThanCommitNightlyHistory()
    {
        var switches = new HashSet<string> { "Clean", "Cold", "NoBuildCache", "BuildOnly", "Affected", "Quick" };
        Assert.True(NightlyRuns.EvidenceRun(Options.Parse(["-Profile", "nightly", "-Clean", "-NoBuildCache"], switches)));
        Assert.True(NightlyRuns.EvidenceRun(Options.Parse(["-Profile", "nightly", "-Cold", "-NoBuildCache"], switches)));
        foreach (var args in new[] { new[] { "-Profile", "nightly" }, new[] { "-Profile", "nightly", "-Clean" },
            new[] { "-Profile", "nightly", "-Clean", "-NoBuildCache", "-BuildOnly" }, new[] { "-Profile", "merge", "-Clean", "-NoBuildCache" } })
            Assert.False(NightlyRuns.EvidenceRun(Options.Parse(args, switches)));
        using var fixture = new PassedNightly();
        fixture.Evidence["mode"]!["clean"] = false; fixture.Evidence["mode"]!["cold"] = true;
        Assert.True(CheckAllNightlyCoverage.PassedNightly(fixture.Report, fixture.Evidence));
    }

    [Fact]
    public void NightlyCompletesAtFortyMinutesAndTimesOutAtFortySixWhileMergeKeepsThirty()
    {
        using var fixture = new PassedNightly();
        var prior = Environment.GetEnvironmentVariable("TIA_MCP_NIGHTLY_ROOT");
        var directory = Path.GetDirectoryName(fixture.Pointer)!;
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_NIGHTLY_ROOT", directory);
            foreach (var (seconds, passed) in new[] { (2400, true), (2760, false) })
            {
                Assert.Equal(passed, NightlyRuns.WithinTimeGate("nightly", seconds));
                Assert.False(NightlyRuns.WithinTimeGate("merge", seconds));
                fixture.Report["underThirtyMinutes"] = passed;
                fixture.Evidence["result"]!["underThirtyMinutes"] = passed;
                fixture.Save();
                NightlyRuns.Complete(directory, fixture.Output, "commit", passed ? 0 : 1, seconds);
                var run = Assert.Single(JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!);
                Assert.Equal(passed ? "passed" : "timed-out", run.Status);
                Assert.Equal(passed, run.UnderThirtyMinutes);
                Assert.Equal(!passed, NightlyRuns.Block([run], "commit", null, (_, _) => true) != null);
                Assert.Equal(passed, fixture.Binding().TryReuse() != null);
            }
            Assert.True(NightlyRuns.WithinTimeGate("merge", 1799));
            Assert.False(NightlyRuns.WithinTimeGate("merge", 1800));
            Assert.False(NightlyRuns.WithinTimeGate("nightly", 2700));
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_NIGHTLY_ROOT", prior); }
    }

    [Fact]
    public void AbandonedNightlyRetainsItsFortyFiveMinuteDeadline()
    {
        using var fixture = new OfflineFixtures();
        string root = fixture.DirectoryPath, directory = Path.Combine(root, "nightly");
        var began = DateTime.UtcNow;
        NightlyRuns.Save(directory, new("running", "commit", Path.Combine(directory, "running/report.json"), "running", 0, false, began));
        NightlyRuns.Prune(root, directory, 1, began.AddMinutes(40));
        Assert.Equal("running", JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))![0].Status);
        NightlyRuns.Prune(root, directory, 1, began.AddMinutes(46));
        var run = Assert.Single(JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!);
        Assert.Equal("timed-out", run.Status);
        Assert.Equal(2700, run.WallSeconds);
        Assert.Equal(began.AddMinutes(45), run.CompletedUtc);
        Assert.False(run.UnderThirtyMinutes);
    }

    [Fact]
    public void NightlyHistoryKeepsLatestFailureAndLatestPassedPointerSeparately()
    {
        using var fixture = new OfflineFixtures(); string directory = Path.Combine(fixture.DirectoryPath, "history");
        var now = DateTime.UtcNow;
        NightlyRuns.Save(directory, new("pass", "commit", "report", "passed", 100, true, now));
        NightlyRuns.Save(directory, new("failure", "commit", "report", "failed", 101, true, now.AddMinutes(1)));
        Assert.Equal("failed", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "latest.json")))!["status"]!);
        Assert.Equal("pass", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "latest-passed.json")))!["runId"]!);
        Assert.Equal(2, JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!.Length);
        NightlyRuns.Save(directory, new("pass", "commit", "report", "timed-out", 2701, false, now.AddMinutes(2)));
        Assert.False(File.Exists(Path.Combine(directory, "latest-passed.json")));
        var options = Options.Parse(["-PublicApiRoot", "C:\\SDK root", "-CompanionPython", "C:\\Python\\python.exe", "-NuGetConfig", "C:\\offline.config"], new HashSet<string>());
        string schedule = ReleaseCommands.NightlySchedule("C:\\Work tree", options);
        Assert.Contains("/ST 03:00", schedule); Assert.Contains("-Worktree", schedule); Assert.Contains("-NuGetConfig", schedule);
        Assert.Contains("-CompanionPython", schedule); Assert.Contains("nightly", CommandLine.Commands);
    }

    [Fact]
    public void PruningKeepsRecentFailuresLastPassedArtifactsAndTheReleaseBlockHistory()
    {
        using var fixture = new OfflineFixtures(); string root = fixture.DirectoryPath, directory = Path.Combine(root, "nightly");
        var now = DateTime.UtcNow;
        foreach (var (id, status, days) in new[] { ("old-pass", "passed", 30), ("last-pass", "passed", 20),
            ("old-failure", "failed", 15), ("abandoned", "running", 16), ("recent-failure", "failed", 7), ("latest", "failed", 1) })
        {
            string output = Path.Combine(directory, id); Directory.CreateDirectory(output);
            NightlyRuns.Write(Path.Combine(output, "nightly-owner.json"), new { root });
            File.WriteAllText(Path.Combine(output, "evidence.log"), "retain according to policy");
            NightlyRuns.Save(directory, new(id, "commit", Path.Combine(output, "report.json"), status, 100, true, now.AddDays(-days)));
        }
        NightlyRuns.Prune(root, directory, 1, now);
        Assert.False(Directory.Exists(Path.Combine(directory, "old-pass")));
        Assert.False(Directory.Exists(Path.Combine(directory, "old-failure")));
        Assert.False(Directory.Exists(Path.Combine(directory, "abandoned")));
        foreach (var id in new[] { "last-pass", "recent-failure", "latest" }) Assert.True(Directory.Exists(Path.Combine(directory, id)));
        var history = JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!;
        Assert.Equal(6, history.Length);
        Assert.Equal("timed-out", history.Single(row => row.RunId == "abandoned").Status);
        Assert.Throws<IOException>(() => CheckAllNightlyCoverage.Safe(root, "../outside"));
    }
}
