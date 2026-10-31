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
        jobs.AddRange(ReleaseCommands.CheckAllPlcScriptJobs().Concat(ReleaseCommands.CheckAllPlcHarnessJobs()).Select(Job));
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var family in new[] { "contracts", "responses", "transport-usage", "usage", "adapter-coverage" }) jobs.Add(Job(family + "-v" + key));
        foreach (var name in catalog.Select(pair => pair.Key))
            jobs.AddRange(new[] { name, name + "-gate", name + "-shard-0", name + "-coverage-proof" }.Select(Job));
        Assert.Empty(CheckAllJobInputs.Undeclared(jobs, catalog));
        Assert.Equal(0, CheckAllJobInputs.Validate(jobs, catalog).ExitCode);
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
    [InlineData("build-tools/release/CheckAll.cs")]
    [InlineData("build-tools/release/CheckAllJobInputs.cs")]
    [InlineData("build-tools/release/CheckAllProfiles.cs")]
    [InlineData("tests/test-suites.json")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/Directory.Build.targets")]
    [InlineData("global.json")]
    [InlineData("NuGet.Config")]
    [InlineData("sdk/TIA_V20_PublicAPI/Siemens.Engineering.dll")]
    public void WideInputsFallBackToTheFullMerge(string path)
    {
        var selection = new CheckAllAffected([path], [], null, false);
        selection.Map([Job("usage-v20-plc")], Catalog);
        Assert.NotNull(selection.Fallback);
        Assert.True(selection.Selected(Job("usage-v21-plc"), CheckAllJobInputs.Find(Job("usage-v21-plc"), Catalog)));
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

    [Fact]
    public void DocsOnlyStillRunsFastChecksAndQuickNeverRunsStressOrUnchangedGui()
    {
        var jobs = new[] { Job("repository"), Job("usage-v21-plc"), Job("stability-v21-c8-stdio-full-plc"),
            Job("studio-gui-false-shard-0"), Job("configuration-page") };
        var selection = new CheckAllAffected(["docs/development/validation.md"], [], null, true); selection.Map(jobs, Catalog);
        Assert.Null(selection.Fallback);
        Assert.Equal(new[] { "repository" }, selection.QuickPlan(jobs, Catalog).Select(job => job.Name));
        var studio = new CheckAllAffected(["src/Studio/Gui/View.cs"],
            [new("gui", (string)Catalog["studio-gui-false"]!["project"]!, ["src/Studio/Gui/View.cs"])], null, true);
        studio.Map(jobs, Catalog);
        Assert.Contains(studio.QuickPlan(jobs, Catalog), job => job.Name == "studio-gui-false-shard-0");
        Assert.True(CheckAllJobInputs.NightlyOnly("stability-v20-c16-http-lite-plc"));
        Assert.False(CheckAllJobInputs.NightlyOnly("stability-v20-c8-http-lite-plc"));
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
        internal PassedNightly()
        {
            Output = Path.Combine(fixture.DirectoryPath, "passed-nightly"); Directory.CreateDirectory(Path.Combine(Output, "logs"));
            File.WriteAllText(Path.Combine(Output, "logs/" + Name + ".log"), "passed");
            Directory.CreateDirectory(Path.Combine(Output, "suites"));
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(Output, "suites/example.trx"), ["Passed", "Passed"]);
            var owner = new CheckAllNightlyCoverage(Output + Path.DirectorySeparatorChar, "machine", null);
            owner.Record(Name, Identity, ["suites/example.trx"]); owner.Save();
            Report = new JsonObject { ["profile"] = "nightly", ["failed"] = 0, ["underThirtyMinutes"] = true,
                ["checks"] = new JsonArray(new JsonObject { ["Name"] = Name, ["Status"] = "passed", ["ExitCode"] = 0, ["IdentityHash"] = Identity },
                    new JsonObject { ["Name"] = "tracked-tree-drift", ["Status"] = "passed", ["ExitCode"] = 0 }) };
            Evidence = new JsonObject
            {
                ["runId"] = "passed-nightly",
                ["mode"] = new JsonObject { ["profile"] = "nightly", ["clean"] = true, ["buildCacheDisabled"] = true, ["resultCacheEnabled"] = false },
                ["source"] = new JsonObject { ["cleanAtStart"] = true },
                ["result"] = new JsonObject { ["exitCode"] = 0, ["failed"] = 0, ["reused"] = 0, ["underThirtyMinutes"] = true },
                ["planned"] = new JsonArray(Name), ["completedUtc"] = DateTime.UtcNow.ToString("o"),
                ["jobs"] = new JsonArray(new JsonObject { ["name"] = Name, ["status"] = "passed", ["exitCode"] = 0, ["reused"] = false },
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
        foreach (var path in new[] { "data/input.json", "outputs/product.dll", "sdk/api.dll", "offline.config" })
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
            new(id, revision, "report", status, seconds, seconds < 1800, now.AddMinutes(offset));
        bool Ancestor(string left, string right) => left == right || left is "previous" or "ancestor" && right is "ancestor" or "release";
        var failed = Run("failed", "ancestor", "failed", 1);
        Assert.Contains("No override", NightlyRuns.Block([failed], "release", "previous", Ancestor));
        Assert.NotNull(NightlyRuns.Block([Run("timeout", "release", "passed", 2, 1800)], "release", "previous", Ancestor));
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
    public void NightlyHistoryKeepsLatestFailureAndLatestPassedPointerSeparately()
    {
        using var fixture = new OfflineFixtures(); string directory = Path.Combine(fixture.DirectoryPath, "history");
        var now = DateTime.UtcNow;
        NightlyRuns.Save(directory, new("pass", "commit", "report", "passed", 100, true, now));
        NightlyRuns.Save(directory, new("failure", "commit", "report", "failed", 101, true, now.AddMinutes(1)));
        Assert.Equal("failed", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "latest.json")))!["status"]!);
        Assert.Equal("pass", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "latest-passed.json")))!["runId"]!);
        Assert.Equal(2, JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!.Length);
        NightlyRuns.Save(directory, new("pass", "commit", "report", "timed-out", 1801, false, now.AddMinutes(2)));
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
