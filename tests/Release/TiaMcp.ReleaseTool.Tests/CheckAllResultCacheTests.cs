using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllResultCacheTests
{
    private sealed class Inputs : IDisposable
    {
        private readonly OfflineFixtures fixture = new();
        internal string Root => fixture.DirectoryPath;
        internal string Results => Path.Combine(Root, "results");
        internal string Trx => Path.Combine(Results, "example.trx");
        internal string Log => Path.Combine(Root, "check.log");
        internal string Sdk = "10.0.402", Runtime = "10.0.12", Os = "Windows build 1", Machine = "machine-a", Definition = "registered definition", EnvironmentValue = "first", Settings = "shards=8;parallelism=12;isolation=default";
        internal readonly string[] Files = ["test/test.dll", "product/product.dll", "fixtures/input.json", "tests/test-suites.json", "fixture-output/helper.dll"];
        internal int Calls;
        internal Inputs()
        {
            foreach (var file in Files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, file))!); File.WriteAllText(Path.Combine(Root, file), "initial"); }
            Directory.CreateDirectory(Results); File.WriteAllText(Log, "clean pass\n");
        }
        internal string Identity() => CheckAllResultCache.Fingerprint(CheckAllResultCache.Enumerate(Root, ["fixtures/input.json", "tests/test-suites.json"],
            new Dictionary<string, string> { ["test"] = Path.Combine(Root, "test"), ["product"] = Path.Combine(Root, "product"), ["fixture"] = Path.Combine(Root, "fixture-output") }),
            new { Sdk, Runtime, Os, Machine, Definition, EnvironmentValue, Settings });
        internal CheckAllResultBinding Binding(bool enabled = true, string? directory = null, int maximum = 0) =>
            new(Root, Path.Combine(Root, "run-1"), "example-shard-0", new("suite", Definition, directory ?? Results, "example.trx", MaximumSkipped: maximum), Machine, Identity, enabled);
        internal CheckTiming Pass(CheckAllResultBinding? binding = null, string[]? outcomes = null)
        {
            binding ??= Binding(); Assert.Null(binding.TryReuse("example-shard-0"));
            DotnetSuiteSelfTests.SyntheticTrx(Trx, outcomes ?? ["Passed", "Passed"]);
            return binding.Record(new("example-shard-0", "passed", 0, 2, null) { StartedAtUtc = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc) }, Log);
        }
        public void Dispose() => fixture.Dispose();
    }

    [Fact]
    public void IdenticalInputsReplayVerifiedTrxAndKeepTheRegisteredGate()
    {
        using var input = new Inputs(); var pass = input.Pass();
        Assert.NotNull(pass.IdentityHash); Assert.True(File.Exists(pass.EvidencePath));
        var current = Path.Combine(input.Root, "run-2/shard-0");
        var reused = input.Binding(directory: current).TryReuse("example-shard-0");
        Assert.NotNull(reused); Assert.Equal("passed (reused)", reused.Status); Assert.True(CheckTiming.Passed(reused));
        Assert.Equal(pass.IdentityHash, reused.IdentityHash); Assert.Equal(pass.StartedAtUtc, reused.ReusedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(reused.ReusedRunId)); Assert.Equal(pass.EvidencePath, reused.EvidencePath);
        var trx = Path.Combine(current, "example.trx"); Assert.Equal(File.ReadAllBytes(input.Trx), File.ReadAllBytes(trx));
        var suite = new JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 };
        Assert.Equal(0, SuiteSharding.Merge("example", suite, [trx], Path.Combine(input.Root, "merged")).ExitCode);
        suite["minimumPassed"] = 3;
        Assert.Equal(1, SuiteSharding.Merge("example", suite, [trx], Path.Combine(input.Root, "higher-minimum")).ExitCode);
    }

    [Theory]
    [InlineData("test-binary")]
    [InlineData("product-binary")]
    [InlineData("data")]
    [InlineData("registry")]
    [InlineData("fixture-binary")]
    [InlineData("environment")]
    [InlineData("sdk")]
    [InlineData("runtime")]
    [InlineData("os")]
    [InlineData("definition")]
    [InlineData("shards")]
    [InlineData("parallelism")]
    [InlineData("isolation")]
    [InlineData("new-output")]
    [InlineData("removed-output")]
    public void AnyChangedInputForcesExecution(string change)
    {
        using var input = new Inputs(); input.Pass();
        int index = Array.IndexOf(new[] { "test-binary", "product-binary", "data", "registry", "fixture-binary" }, change);
        if (index >= 0)
        {
            var file = Path.Combine(input.Root, input.Files[index]); var timestamp = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, "changed"); File.SetLastWriteTimeUtc(file, timestamp); // equal length and time cannot hide changed bytes
        }
        else switch (change)
        {
            case "environment": input.EnvironmentValue = "second"; break;
            case "sdk": input.Sdk = "10.0.403"; break;
            case "runtime": input.Runtime = "10.0.13"; break;
            case "os": input.Os = "Windows build 2"; break;
            case "definition": input.Definition = "changed filter/minimum/skip maximum"; break;
            case "shards": input.Settings = "shards=1;parallelism=12;isolation=default"; break;
            case "parallelism": input.Settings = "shards=8;parallelism=1;isolation=default"; break;
            case "isolation": input.Settings = "shards=8;parallelism=12;isolation=changed"; break;
            case "new-output": File.WriteAllText(Path.Combine(input.Root, "product/new-dependency.dll"), "additional file"); break;
            case "removed-output": File.Delete(Path.Combine(input.Root, "fixture-output/helper.dll")); break;
        }
        Assert.Null(input.Binding().TryReuse("example-shard-0"));
        var rows = CheckScheduler.Run([new("example-shard-0", () => { input.Calls++; return new(0, "reran", ""); }, []) { ResultCache = input.Binding() }], 1, Path.Combine(input.Root, "logs"));
        Assert.Equal("passed", Assert.Single(rows).Status);
        Assert.Equal(1, input.Calls);
    }

    [Theory]
    [InlineData("suite")]
    [InlineData("static")]
    [InlineData("generator")]
    [InlineData("python")]
    public void EachFamilyHashesIgnoredDataBelowLayoutRoots(string family)
    {
        using var input = new Inputs();
        string Identity()
        {
            var inventory = CheckAllLayout.ResultSourceInventory(input.Root, ["fixtures/input.json"]);
            return CheckAllResultCache.Fingerprint(CheckAllResultCache.Enumerate(input.Root, inventory, new Dictionary<string, string>()), family);
        }
        var first = Identity(); File.WriteAllText(Path.Combine(input.Root, "fixtures/ignored-baseline.json"), "ignored by git");
        Assert.NotEqual(first, Identity()); var second = Identity();
        var binding = new CheckAllResultBinding(input.Root, "family-run", family, new(family, "definition"), "machine", Identity, true);
        Assert.Null(binding.TryReuse(family));
        binding.Record(new(family, "passed", 0, 1, null) { StartedAtUtc = DateTime.UtcNow }, input.Log);
        File.WriteAllText(Path.Combine(input.Root, "fixtures/ignored-baseline.json"), "changed data"); Assert.NotEqual(second, Identity());
        Assert.Null(binding.TryReuse(family));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("hung")]
    [InlineData("skipped")]
    [InlineData("malformed")]
    [InlineData("bad-summary")]
    public void NonCleanResultCannotBecomeReusable(string kind)
    {
        using var input = new Inputs(); var binding = input.Binding(); Assert.Null(binding.TryReuse("example-shard-0"));
        DotnetSuiteSelfTests.SyntheticTrx(input.Trx, kind == "failed" ? ["Failed"] : kind == "skipped" ? ["Passed", "NotExecuted"] : ["Passed"]);
        if (kind == "malformed") File.WriteAllText(input.Trx, "<broken");
        if (kind == "bad-summary") DotnetSuiteSelfTests.SyntheticTrx(input.Trx, ["Passed"], new() { ["passed"] = "2" });
        var row = new CheckTiming("example-shard-0", kind == "hung" ? "failed" : "passed", kind == "hung" ? 1 : 0, 1, kind == "hung" ? "timed out" : null);
        binding.Record(row, input.Log); Assert.False(File.Exists(binding.EntryPath)); Assert.Null(input.Binding().TryReuse("example-shard-0"));
    }

    [Fact]
    public void RegisteredSkipsAreEvidenceAndUnregisteredSkipsAreNot()
    {
        using var input = new Inputs(); var binding = input.Binding(maximum: 1);
        input.Pass(binding, ["Passed", "NotExecuted"]); Assert.NotNull(input.Binding(maximum: 1).TryReuse("example-shard-0"));
        Assert.Null(input.Binding(maximum: 0).TryReuse("example-shard-0"));
    }

    [Fact]
    public void EveryTimeSensitiveClassAndMethodRefusesReuse()
    {
        using var input = new Inputs();
        foreach (var pair in TimeSensitiveTests.Classes)
        {
            foreach (var method in TimeSensitiveTests.Methods.GetValueOrDefault(pair.Key) ?? new[] { "UnlistedMethod" })
            {
                DotnetSuiteSelfTests.SyntheticTrx(input.Trx, ["Passed"]);
                var doc = XDocument.Load(input.Trx); var ns = doc.Root!.Name.Namespace;
                var definition = doc.Descendants(ns + "TestMethod").Single(); definition.SetAttributeValue("className", pair.Key); definition.SetAttributeValue("name", method); doc.Save(input.Trx);
                Assert.False(CheckAllResultBinding.CleanTrx(input.Trx, 0), pair.Key + "." + method);
            }
        }
        Assert.All(new[] { "stability-v20", "soak", "timing-comparison", "cpu-budget", "network-fixture" }, name => Assert.True(CheckAllResultCache.AlwaysRun(name)));
        Assert.All(new[] { "DateTime.UtcNow", "Stopwatch.StartNew()", "Task.Delay(1)", "Environment.ProcessorCount", "Process.Start(\"fixture.exe\")" }, source => Assert.False(CheckAllResultCache.HasVolatileInput(source)));
        Assert.All(new[] { "new HttpClient()", "Registry.LocalMachine", "Process.GetProcessesByName(\"foreign\")" }, source => Assert.True(CheckAllResultCache.HasVolatileInput(source)));
        Assert.True(CheckAllResultCache.ReusableSuite("{\"suite\":\"foundation\"}"));
        Assert.True(CheckAllResultCache.ReusableSuite("{\"suite\":\"unknown-future-suite\"}"));
        Assert.False(CheckAllResultCache.ReusableStatic("suite-runner-self-tests"));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("payload")]
    [InlineData("receipt")]
    [InlineData("missing")]
    [InlineData("foreign-machine")]
    [InlineData("failed-entry")]
    public void CorruptOrForeignEvidenceIsIgnored(string kind)
    {
        using var input = new Inputs(); var binding = input.Binding(); var pass = input.Pass(binding);
        var evidence = Path.GetDirectoryName(pass.EvidencePath)!;
        switch (kind)
        {
            case "json": File.WriteAllText(binding.EntryPath, "{bad"); break;
            case "payload": File.AppendAllText(Path.Combine(evidence, "example.trx"), "changed"); break;
            case "receipt": File.WriteAllText(pass.EvidencePath!, "{bad"); break;
            case "missing": File.Delete(Path.Combine(evidence, "check.log")); break;
            case "foreign-machine": input.Machine = "machine-b"; break;
            case "failed-entry":
                var entry = JsonNode.Parse(File.ReadAllText(binding.EntryPath))!; entry["Status"] = "failed"; entry["ExitCode"] = 1;
                File.WriteAllText(binding.EntryPath, entry.ToJsonString()); File.WriteAllText(pass.EvidencePath!, entry.ToJsonString()); break;
        }
        Assert.Null(input.Binding().TryReuse("example-shard-0"));
    }

    [Fact]
    public void InputDriftAndDisabledRerunsInvalidatePreviousEvidence()
    {
        using var input = new Inputs(); var binding = input.Binding(); input.Pass(binding);
        Assert.Null(input.Binding(enabled: false).TryReuse("example-shard-0")); Assert.False(File.Exists(binding.EntryPath));
        var changing = input.Binding(); Assert.Null(changing.TryReuse("example-shard-0")); input.EnvironmentValue = "drifted while running";
        changing.Record(new("example-shard-0", "passed", 0, 1, null), input.Log); Assert.False(File.Exists(changing.EntryPath));
    }

    [Theory]
    [InlineData("Clean", null)]
    [InlineData("NoResultCache", null)]
    [InlineData("ProveEquivalence", null)]
    [InlineData("SequentialBaselineDirectory", null)]
    [InlineData(null, "GITHUB_ACTIONS")]
    [InlineData(null, "CI")]
    [InlineData(null, "TIA_MCP_RELEASE_CHECK_PLAN")]
    [InlineData(null, "TIA_MCP_BUILD_CACHE_DISABLED")]
    public void CleanCiAndCandidateContextsDisableReuse(string? option, string? environment)
    {
        var options = Options.Parse(option == null ? [] : ["-" + option + "=true"], new HashSet<string>());
        Assert.False(CheckAllResultCache.Enabled(options, name => name == environment ? "1" : null));
        Assert.True(CheckAllResultCache.Enabled(Options.Parse([], new HashSet<string>()), _ => null));
    }

    [Fact]
    public void SchedulerAcceptsVerifiedReuseAsADependencyAndReportsItsOrigin()
    {
        using var input = new Inputs(); input.Pass();
        var rows = CheckScheduler.Run([
            new("example-shard-0", () => throw new Exception("identical pass should be reused"), []) { ResultCache = input.Binding() },
            new("dependent", () => { input.Calls++; return new(0, "executed", ""); }, ["example-shard-0"])], 1, Path.Combine(input.Root, "scheduler"));
        Assert.Equal(new[] { "passed (reused)", "passed" }, rows.Select(row => row.Status)); Assert.Equal(1, input.Calls);
        Assert.True(ReleaseCommands.CheckAllPrerequisitesPassed(rows));
        Assert.False(CheckTiming.Passed(new("forged", "passed (reused)", 0, 0, null)));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(input.Root, "scheduler/timings.json")))!;
        Assert.Equal(rows[0].IdentityHash, (string?)json[0]!["IdentityHash"]); Assert.NotNull(json[0]!["EvidencePath"]);
    }

    [Fact]
    public void ActualLayoutIdentitySurvivesFreshReportPathsButRejectsChangedSettingsAndFiles()
    {
        using var input = new Inputs(); var root = input.Root;
        File.WriteAllText(Path.Combine(root, ".gitignore"), "bin-build/\n**/bin/\nsdk/\nresults/\n");
        ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["init", "--quiet"], root), "fixture inventory");
        var project = "tests/Example/Example.csproj";
        Directory.CreateDirectory(Path.Combine(root, "tests/Example"));
        File.WriteAllText(Path.Combine(root, project), "<Project><ItemGroup><Compile Include=\"*.cs\" /></ItemGroup></Project>"); File.WriteAllText(Path.Combine(root, "tests/Example/Check.cs"), "class Fixed { int Value => 1; }");
        var isolated = Path.Combine(root, "tests/Example/bin/isolated"); var ordinary = Path.Combine(root, "tests/Example/bin/ordinary");
        foreach (var path in new[] { isolated, ordinary, Path.Combine(root, "sdk") }) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "payload.dll"), "compiled bytes"); }
        var manifest = Path.Combine(root, "bin-build/runtime-paths.tsv"); Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, string.Join('\t', Path.Combine(root, project), "default", isolated, ordinary, Path.Combine(isolated, "payload.dll")) + "\n");
        var nuget = Path.Combine(root, "nuget.config"); File.WriteAllText(nuget, "<configuration />");
        var definition = new JsonObject { ["suite"] = "hardware-catalog", ["project"] = project }.ToJsonString();
        var job = new CheckJob("hardware-catalog-shard-0", () => new(0, "", ""), []);
        string Identity(string run, int shards = 8)
        {
            var output = Path.Combine(root, "bin-build/" + run); Directory.CreateDirectory(output);
            var plan = Path.Combine(output, "shard-plan.tsv"); File.WriteAllText(plan, "fixed-assignment\t0\n");
            var options = Options.Parse(["-OutputDirectory=" + output, "-Shards=" + shards], new HashSet<string>());
            var cache = new CheckAllResultCache(root, output, manifest, Path.Combine(root, "sdk"), nuget, "dotnet", "python", null, options, "same resources");
            return cache.Identity(job, new("suite", definition, output, "example.trx", plan));
        }
        var first = Identity("run-1"); Assert.Equal(first, Identity("run-2")); Assert.NotEqual(first, Identity("run-3", 1));
        File.WriteAllText(Path.Combine(isolated, "payload.dll"), "changed bytes"); Assert.NotEqual(first, Identity("run-4"));
        Assert.Throws<IOException>(() => IdentityWithMachineSource());
        void IdentityWithMachineSource()
        {
            File.WriteAllText(Path.Combine(root, "tests/Example/Check.cs"), "class NewTest { object Value => Microsoft.Win32.Registry.LocalMachine; }"); Identity("run-5");
        }
    }

    private sealed class SuiteInputs : IDisposable
    {
        private readonly OfflineFixtures fixture = new();
        internal string Root => fixture.DirectoryPath;
        internal const string Project = "tests/CacheSuite/CacheSuite.csproj";
        internal const string Source = "tests/CacheSuite/Checks.cs";
        internal string Output => Path.Combine(Root, "bin-build/current");
        internal string Manifest => Path.Combine(Root, "bin-build/runtime-paths.tsv");
        internal readonly Dictionary<string, string> Probe = new(StringComparer.Ordinal) {
            ["registry32"] = "TIA Portal/Openness/PLCSIM original", ["registry64"] = "TIA Portal/Openness/PLCSIM original",
            ["membership"] = "not a member", ["fixedDrives"] = "C:/", ["runtimes"] = "Microsoft.NETCore.App 10.0.12" };
        internal Func<string>? ProbeOverride;
        internal JsonObject Registration { get; } = new() { ["project"] = Project, ["minimumPassed"] = 1, ["maximumSkipped"] = 0, ["arguments"] = new JsonArray() };
        internal SuiteInputs()
        {
            Write(".gitignore", "bin-build/\n**/bin/\nsdk/\n");
            ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["init", "--quiet"], Root), "suite fixture inventory");
            Write(Project, "<Project><ItemGroup><Compile Include=\"**/*.cs\" Exclude=\"bin/**;obj/**\" /><ProjectReference Include=\"../Helper/Helper.csproj\" /></ItemGroup></Project>");
            Write(Source, """
                class Checks {
                    string Data(string root) => System.IO.Path.Combine(root, "reference", "cache-data", "input.json");
                    string Worker(string root) => System.IO.Path.Combine(root, "src", "Worker", "bin", "Release", "worker.dll");
                }
                """);
            Write("reference/cache-data/input.json", "data"); Write("docs/unrelated.md", "docs");
            Write("src/Studio/Gui/View.cs", "view"); Write("tests/test-suites.json", "unrelated registration");
            Write("tests/Helper/Helper.csproj", "<Project />");
            Write("tests/CacheSuite/bin/isolated/test.dll", "test"); Write("tests/CacheSuite/bin/ordinary/test.dll", "test");
            Write("tests/Helper/bin/isolated/helper.dll", "helper"); Write("tests/Helper/bin/ordinary/helper.dll", "helper");
            Write("src/Worker/Worker.csproj", "<Project />");
            Write("src/Worker/bin/isolated/worker.dll", "worker"); Write("src/Worker/bin/Release/worker.dll", "worker");
            Write("src/Worker/bin/isolated/worker.deps.json", "worker dependency");
            Write("src/Studio/Gui/Gui.csproj", "<Project />");
            Write("src/Studio/Gui/bin/isolated/gui.dll", "gui"); Write("src/Studio/Gui/bin/ordinary/gui.dll", "gui");
            Write("sdk/sdk.dll", "sdk"); Write("nuget.config", "<configuration />");
            Write("bin-build/runtime-paths.tsv", string.Join("\n", new[] {
                Row(Project, "tests/CacheSuite/bin/isolated", "tests/CacheSuite/bin/ordinary", "test.dll"),
                Row("tests/Helper/Helper.csproj", "tests/Helper/bin/isolated", "tests/Helper/bin/ordinary", "helper.dll"),
                Row("src/Worker/Worker.csproj", "src/Worker/bin/isolated", "src/Worker/bin/Release", "worker.dll"),
                Row("src/Studio/Gui/Gui.csproj", "src/Studio/Gui/bin/isolated", "src/Studio/Gui/bin/ordinary", "gui.dll") }) + "\n");
            Write("bin-build/check.log", "passed\n");
        }
        private string Row(string project, string source, string ordinary, string binary) =>
            string.Join('\t', Path.Combine(Root, project), "default", Path.Combine(Root, source), Path.Combine(Root, ordinary), Path.Combine(Root, source, binary));
        internal void Write(string path, string content)
        {
            var full = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, content);
        }
        internal CheckAllSuiteClosure Closure() => CheckAllInputClosure.Derive(Root, Project, Registration, CheckAllLayout.SourceInventory(Root));
        internal CheckAllResultSpec Spec(int index = 0) => new("suite", new JsonObject {
            ["suite"] = "cache-suite", ["project"] = Project, ["variant"] = "default", ["index"] = index, ["count"] = 2,
            ["registration"] = Registration.DeepClone() }.ToJsonString(), Output, "cache-suite.trx");
        internal CheckAllResultCache Cache() => new(Root, Output, Manifest, Path.Combine(Root, "sdk"), Path.Combine(Root, "nuget.config"),
            "dotnet", "python", null, Options.Parse([], new HashSet<string>()), "same resources", contextEnvironment: _ => null, machineStateProbe: () => ProbeOverride?.Invoke() ?? CheckAllResultCache.Fingerprint([], Probe));
        internal CheckJob Job(int index = 0, bool sensitive = false) => new("cache-suite-shard-" + index, () => new(0, "pass", ""), [], Exclusive: sensitive)
            { ResultInputs = Spec(index) };
        internal CheckAllResultBinding Binding(int index = 0, bool sensitive = false) => Cache().Bind(Job(index, sensitive)).ResultCache!;
        internal CheckTiming Pass()
        {
            var binding = Binding(); Assert.Null(binding.TryReuse("cache-suite-shard-0")); Directory.CreateDirectory(Output);
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(Output, "cache-suite.trx"), ["Passed"]);
            var row = binding.Record(new("cache-suite-shard-0", "passed", 0, 1, null) { StartedAtUtc = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc) }, Path.Combine(Root, "bin-build/check.log"));
            Assert.NotNull(row.EvidencePath); return row;
        }
        public void Dispose() => fixture.Dispose();
    }

    [Theory]
    [InlineData("docs/unrelated.md")]
    [InlineData("src/Studio/Gui/View.cs")]
    [InlineData("src/Studio/Gui/bin/isolated/gui.dll")]
    [InlineData("tests/test-suites.json")]
    public void OutsideSuiteClosureKeepsItsVerifiedHit(string path)
    {
        using var input = new SuiteInputs(); var pass = input.Pass(); input.Write(path, "unrelated change");
        var hit = input.Binding().TryReuse("cache-suite-shard-0"); Assert.NotNull(hit);
        Assert.Equal("passed (reused)", hit.Status); Assert.Equal(pass.IdentityHash, hit.IdentityHash);
    }

    [Theory]
    [InlineData("reference/cache-data/input.json")]
    [InlineData("tests/CacheSuite/bin/isolated/test.dll")]
    [InlineData("tests/Helper/bin/isolated/helper.dll")]
    [InlineData("src/Worker/bin/isolated/worker.dll")]
    [InlineData("src/Worker/bin/isolated/new-dependency.dll")]
    [InlineData("src/Worker/bin/Release/worker.deps.json")]
    public void SuiteDataTestFixtureAndLaunchedWorkerChangesMiss(string path)
    {
        using var input = new SuiteInputs(); input.Pass(); input.Write(path, "changed input");
        Assert.Null(input.Binding().TryReuse("cache-suite-shard-0"));
    }

    [Fact]
    public void PlantedUndeclaredRepositoryPathFailsTheClosureGuard()
    {
        using var input = new SuiteInputs(); var saved = input.Closure();
        File.AppendAllText(Path.Combine(input.Root, SuiteInputs.Source), "\nclass Planted { string Read(string root) => File.ReadAllText(Path.Combine(root, \"docs\", \"unrelated.md\")); }\n");
        var error = Assert.Throws<ReleaseException>(() => CheckAllInputClosure.Guard(input.Root, saved, input.Registration, CheckAllLayout.SourceInventory(input.Root)));
        Assert.Contains("docs/unrelated.md", error.Message); Assert.Contains(SuiteInputs.Source + ":", error.Message);
    }

    [Fact]
    public void EveryEligibleRegisteredSuiteHasACompleteRederivedClosure()
    {
        var root = Repository.FindRoot(); var layout = CheckAllLayout.SourceInventory(root);
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root);
        int eligible = 0;
        foreach (var (_, node) in catalog)
        {
            var registration = node!.AsObject();
            var closure = CheckAllInputClosure.Derive(root, (string)registration["project"]!, registration, layout);
            if (closure.LiveReasons.Length != 0) continue;
            eligible++;
            CheckAllInputClosure.Guard(root, closure, registration, layout);
            Assert.All(closure.References, reference => Assert.True(closure.Roots.Any(prefix => CheckAllInputClosure.Covers(prefix, reference.Path)), reference.File + ":" + reference.Line + ": " + reference.Path));
        }
        Assert.True(eligible > 0, "Eligibility and the closure guard must not be vacuous.");
    }

    [Fact]
    public void SensitiveMethodKeepsItsShardLiveWhileItsSiblingHits()
    {
        using var input = new SuiteInputs(); input.Pass();
        Assert.NotNull(input.Binding().TryReuse("cache-suite-shard-0"));
        var sensitive = input.Binding(1, sensitive: true); Assert.Null(sensitive.TryReuse("cache-suite-shard-1"));
        var pair = TimeSensitiveTests.Methods.First(); var trx = Path.Combine(input.Output, "cache-suite.trx");
        DotnetSuiteSelfTests.SyntheticTrx(trx, ["Passed"]); var doc = XDocument.Load(trx); var ns = doc.Root!.Name.Namespace;
        var method = doc.Descendants(ns + "TestMethod").Single(); method.SetAttributeValue("className", pair.Key); method.SetAttributeValue("name", pair.Value[0]); doc.Save(trx);
        Assert.False(CheckAllResultBinding.CleanTrx(trx, 0));
        sensitive.Record(new("cache-suite-shard-1", "passed", 0, 1, null) { StartedAtUtc = DateTime.UtcNow }, Path.Combine(input.Root, "bin-build/check.log"));
        Assert.False(File.Exists(sensitive.EntryPath)); Assert.NotNull(input.Binding().TryReuse("cache-suite-shard-0"));
    }

    [Theory]
    [InlineData("Microsoft.Win32.Registry.LocalMachine", "Registry")]
    [InlineData("new System.Management.ManagementObject()", "ManagementObject")]
    [InlineData("new System.ServiceProcess.ServiceController()", "ServiceController")]
    [InlineData("System.Diagnostics.Process.GetProcessesByName(\"foreign\")", "GetProcessesByName")]
    [InlineData("new System.IO.DriveInfo(\"C:\\\\\")", "DriveInfo")]
    [InlineData("new System.Net.Http.HttpClient().GetAsync(\"https://example.invalid/data\")", "network")]
    [InlineData("System.Net.Dns.GetHostAddresses(\"example.invalid\")", "DNS")]
    [InlineData("new System.IO.FileSystemWatcher(\"C:/external\")", "FileSystemWatcher")]
    public void PlantedMachineStateApiMakesTheSuiteLive(string expression, string api)
    {
        using var input = new SuiteInputs();
        File.AppendAllText(Path.Combine(input.Root, SuiteInputs.Source), "\nclass MachineProbe { object Read() => " + expression + "; }\n");
        var closure = input.Closure(); Assert.Contains(closure.LiveReasons, reason => reason.Detail.Contains(api, StringComparison.Ordinal));
        Assert.All(closure.LiveReasons, reason => { Assert.Equal(SuiteInputs.Source, reason.File); Assert.True(reason.Line > 1); });
        var job = input.Cache().Bind(input.Job()); Assert.Contains(api, job.ResultCachePolicy!);
        Assert.Null(job.ResultCache!.TryReuse(job.Name));
        var row = job.ResultCache.Record(new(job.Name, "passed", 0, 1, null), Path.Combine(input.Root, "bin-build/check.log"));
        Assert.Null(row.EvidencePath); Assert.False(File.Exists(job.ResultCache.EntryPath));
    }

    [Fact]
    public void OrdinaryAsyncProcessAndLoopbackFixturesRemainEligible()
    {
        using var input = new SuiteInputs();
        File.AppendAllText(Path.Combine(input.Root, SuiteInputs.Source), """

            class LoopbackFixture {
                async Task Run() {
                    Process.Start("fixture.exe"); var current = Process.GetCurrentProcess();
                    await Task.Delay(1); var elapsed = Stopwatch.StartNew();
                    var value = Environment.GetEnvironmentVariable("EXAMPLE");
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    using var client = new HttpClient(); await client.GetAsync("http://127.0.0.1:12345/fixture");
                    var time = DateTime.UtcNow; var timer = new Timer(_ => { });
                }
            }
            """);
        Assert.Empty(input.Closure().LiveReasons); input.Pass(); Assert.NotNull(input.Binding().TryReuse("cache-suite-shard-0"));
    }

    [Fact]
    public void RegistryPathsOnlyAddToTheAutomaticallyDerivedClosure()
    {
        using var input = new SuiteInputs(); var original = input.Closure();
        input.Registration["resultCacheInputs"] = new JsonArray("docs"); var extended = input.Closure();
        Assert.All(original.Roots, path => Assert.Contains(extended.Roots, prefix => CheckAllInputClosure.Covers(prefix, path)));
        Assert.Contains("docs", extended.Roots);
        input.Registration["resultCacheInputs"] = new JsonArray(); var unchanged = input.Closure();
        Assert.Equal(original.Roots, unchanged.Roots);
    }

    [Fact]
    public void ImportedLinkedAndGlobbedCompileItemsContributeTheirDeepestLiteralPrefixes()
    {
        using var input = new SuiteInputs();
        input.Write(SuiteInputs.Project, "<Project><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><Import Project=\"links.props\" /><ItemGroup><Compile Include=\"Checks.cs\" /></ItemGroup></Project>");
        input.Write("tests/CacheSuite/links.props", "<Project><ItemGroup><Compile Include=\"../SharedLinks/**/*.cs\" /></ItemGroup></Project>");
        input.Write("tests/SharedLinks/Linked.cs", "class Linked { string Read(string root, string file) => Path.Combine(root, \"catalog\", \"nested\", file); }");
        input.Write("catalog/nested/data.json", "data");
        var closure = input.Closure(); Assert.Empty(closure.LiveReasons); Assert.Contains("tests/SharedLinks/Linked.cs", closure.Sources);
        Assert.Contains("catalog/nested", closure.Roots); Assert.DoesNotContain("catalog", closure.Roots);
        CheckAllInputClosure.Guard(input.Root, closure, input.Registration, CheckAllLayout.SourceInventory(input.Root));
    }

    [Fact]
    public void DynamicSegmentsAndInterpolationsKeepTheDeepestProvablePrefix()
    {
        using var input = new SuiteInputs();
        File.AppendAllText(Path.Combine(input.Root, SuiteInputs.Source), """

            class Prefixes {
                string A(string root, string version) => Path.Combine(root, $"catalog/compiled/{version}");
                string B(string root, bool option) => Path.Combine(root, option ? "templates/a" : "docs/b");
                string C(string root, string item) => Path.Combine(root, "scripts", "checks", item);
            }
            """);
        var closure = input.Closure(); Assert.Empty(closure.LiveReasons);
        Assert.Contains("catalog/compiled", closure.Roots); Assert.Contains("templates/a", closure.Roots); Assert.Contains("docs/b", closure.Roots);
        Assert.Contains("scripts/checks", closure.Roots); Assert.DoesNotContain("catalog", closure.Roots); Assert.DoesNotContain("scripts", closure.Roots);
    }

    [Theory]
    [InlineData("registry32")]
    [InlineData("registry64")]
    [InlineData("membership")]
    [InlineData("fixedDrives")]
    [InlineData("runtimes")]
    public void ProductOnlyMachineAndNetworkApisStayEligibleAndEveryProbeChangeMisses(string component)
    {
        using var input = new SuiteInputs();
        input.Write("src/Product/Probe.cs", "class Product { object Read() => Microsoft.Win32.Registry.LocalMachine; object Fetch() => new HttpClient().GetAsync(\"https://example.invalid/data\"); }");
        var project = Path.Combine(input.Root, SuiteInputs.Project);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</ItemGroup>", "<Compile Include=\"../../src/Product/Probe.cs\" /></ItemGroup>", StringComparison.Ordinal));
        var closure = input.Closure(); Assert.Contains("src/Product/Probe.cs", closure.Sources); Assert.Empty(closure.LiveReasons);
        input.Pass(); var binding = input.Binding(); Assert.NotNull(binding.TryReuse("cache-suite-shard-0"));
        input.Probe[component] = "changed machine state";
        Assert.Null(binding.TryReuse("cache-suite-shard-0")); Assert.Null(input.Binding().TryReuse("cache-suite-shard-0"));
    }

    [Theory]
    [InlineData("Microsoft.Win32.Registry.LocalMachine")]
    [InlineData("new HttpClient().GetAsync(\"https://example.invalid/data\")")]
    public void LinkedTestMachineAndNetworkApisKeepTheSuiteLive(string expression)
    {
        using var input = new SuiteInputs(); input.Write("tests/SharedLinks/Probe.cs", "class Linked { object Read() => " + expression + "; }");
        var project = Path.Combine(input.Root, SuiteInputs.Project);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</ItemGroup>", "<Compile Include=\"../SharedLinks/Probe.cs\" /></ItemGroup>", StringComparison.Ordinal));
        Assert.Contains(input.Closure().LiveReasons, reason => reason.File == "tests/SharedLinks/Probe.cs");
        Assert.Null(input.Binding().TryReuse("cache-suite-shard-0"));
    }

    [Theory]
    [InlineData("Repository.FindRoot()")]
    [InlineData("EcosystemFiles.RepositoryRoot()")]
    [InlineData("BundleLayout.FindRoot(AppContext.BaseDirectory)")]
    public void RootHelperPathsResolveTheirConstantSegments(string helper)
    {
        using var input = new SuiteInputs();
        input.Write(SuiteInputs.Source, "class Paths { string Read() { var root = " + helper + "; return Path.Combine(root, \"catalog\", \"nested\", \"input.json\"); } }");
        input.Write("catalog/nested/input.json", "data"); var closure = input.Closure();
        Assert.Empty(closure.LiveReasons); Assert.False(closure.WholeInventory); Assert.Empty(closure.FallbackReasons);
        Assert.Contains("catalog/nested/input.json", closure.Roots); Assert.DoesNotContain("catalog", closure.Roots);
        CheckAllInputClosure.Guard(input.Root, closure, input.Registration, CheckAllLayout.SourceInventory(input.Root));
    }

    [Theory]
    [InlineData("Consume(Repository.FindRoot())", "docs/unrelated.md")]
    [InlineData("Consume(Repository.FindRoot())", "src/Studio/Gui/View.cs")]
    [InlineData("Consume(Repository.FindRoot())", "reference/cache-data/new.json")]
    [InlineData("Consume(Repository.FindRoot())", "extra-root/new-input.json")]
    [InlineData("Assembly.LoadFrom(arbitrary)", "docs/unrelated.md")]
    [InlineData("Assembly.LoadFrom(arbitrary)", "src/Studio/Gui/bin/isolated/gui.dll")]
    [InlineData("context.LoadFromAssemblyPath(arbitrary)", "extra-root/new-input.json")]
    public void UnfollowableRootsAndAssemblyPathsUseWholeInventoryAndChangesMiss(string expression, string changed)
    {
        using var input = new SuiteInputs(); input.Write(SuiteInputs.Source, "class Paths { void Read(string arbitrary) { " + expression + "; } }");
        var closure = input.Closure(); Assert.Empty(closure.LiveReasons); Assert.True(closure.WholeInventory); Assert.NotEmpty(closure.FallbackReasons);
        Assert.All(CheckAllLayout.SourceInventory(input.Root), path => Assert.Contains(path, CheckAllInputClosure.DataFiles(input.Root, closure)));
        CheckAllInputClosure.Guard(input.Root, closure, input.Registration, CheckAllLayout.SourceInventory(input.Root));
        input.Pass(); Assert.NotNull(input.Binding().TryReuse("cache-suite-shard-0")); input.Write(changed, "changed anywhere");
        Assert.Null(input.Binding().TryReuse("cache-suite-shard-0"));
    }

    [Theory]
    [InlineData("Path.Combine(AppContext.BaseDirectory, \"product.dll\")")]
    [InlineData("Path.Combine(Path.GetDirectoryName(typeof(Paths).Assembly.Location), \"product.dll\")")]
    [InlineData("Path.Combine(Repository.FindRoot(), \"src\", \"Worker\", \"bin\", \"Release\", \"worker.dll\")")]
    public void AssemblyPathsUnderOutputsOrRootConstantsStayBounded(string path)
    {
        using var input = new SuiteInputs(); input.Write(SuiteInputs.Source, "class Paths { void Read() { var path = " + path + "; Assembly.LoadFrom(path); } }");
        var closure = input.Closure(); Assert.Empty(closure.LiveReasons); Assert.False(closure.WholeInventory); Assert.Empty(closure.FallbackReasons);
    }

    [Fact]
    public void MsBuildEvaluatesCompileItemTransformsAndSeparatesArgumentSets()
    {
        using var input = new SuiteInputs();
        input.Write(SuiteInputs.Project, """
            <Project>
              <ItemGroup>
                <Compile Include="Checks.cs" />
                <Primitive Include="../SharedLinks/One.cs" Condition="'$(ProbeFlavor)' == 'one'" />
                <Primitive Include="../SharedLinks/Two.cs" Condition="'$(ProbeFlavor)' == 'two'" />
                <Compile Include="@(Primitive->'%(Identity)')" />
              </ItemGroup>
            </Project>
            """);
        input.Write("tests/SharedLinks/One.cs", "class One { string Read(string root) => Path.Combine(root, \"catalog\", \"one\"); }");
        input.Write("tests/SharedLinks/Two.cs", "class Two { string Read(string root) => Path.Combine(root, \"catalog\", \"two\"); }");
        input.Registration["arguments"] = new JsonArray("-p:ProbeFlavor=one"); var one = input.Closure();
        input.Registration["arguments"] = new JsonArray("-p:ProbeFlavor=two"); var two = input.Closure();
        Assert.Empty(one.LiveReasons); Assert.Empty(two.LiveReasons);
        Assert.Contains("tests/SharedLinks/One.cs", one.Sources); Assert.DoesNotContain("tests/SharedLinks/Two.cs", one.Sources);
        Assert.Contains("tests/SharedLinks/Two.cs", two.Sources); Assert.DoesNotContain("tests/SharedLinks/One.cs", two.Sources);
        Assert.Contains("catalog/one", one.Roots); Assert.Contains("catalog/two", two.Roots);
        input.Registration["arguments"] = new JsonArray("-p:ProbeFlavor=one"); Assert.Equal(one.SourceIdentity, input.Closure().SourceIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateLoopbackFixtureClientIsEligibleButAnExternalTargetIsLive(bool external)
    {
        string source = """
            class Tests {
                void Test() { var host = Fixture.Start(); var session = new Session(host.Url); }
                private sealed class Fixture {
                    static Fixture Start() { string url = "TARGET"; return new Fixture { Url = url }; }
                    public string Url;
                }
                private sealed class Session(string url) {
                    HttpClient client = new() { BaseAddress = new Uri(url) };
                }
            }
            """;
        source = source.Replace("TARGET", external ? "https://example.invalid" : "http://127.0.0.1:1234", StringComparison.Ordinal);
        var findings = CheckAllInputClosure.MachineInputs("tests/Example.cs", source);
        if (external) Assert.Contains(findings, finding => finding.Detail.Contains("network", StringComparison.Ordinal)); else Assert.Empty(findings);
    }

    [Fact]
    public void FailedMachineProbeCannotReuseOrRecordEvidence()
    {
        using var input = new SuiteInputs(); input.Pass(); input.ProbeOverride = () => throw new IOException("probe unavailable");
        var binding = input.Binding(); Assert.Null(binding.TryReuse("cache-suite-shard-0"));
        var row = binding.Record(new("cache-suite-shard-0", "passed", 0, 1, null), Path.Combine(input.Root, "bin-build/check.log"));
        Assert.Null(row.EvidencePath); Assert.False(File.Exists(binding.EntryPath));
    }

    [Fact]
    public void MachineProbeChangeDuringRunPreventsEvidencePublication()
    {
        using var input = new SuiteInputs(); var binding = input.Binding(); Assert.Null(binding.TryReuse("cache-suite-shard-0"));
        Directory.CreateDirectory(input.Output); DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(input.Output, "cache-suite.trx"), ["Passed"]);
        input.Probe["membership"] = "member";
        var row = binding.Record(new("cache-suite-shard-0", "passed", 0, 1, null), Path.Combine(input.Root, "bin-build/check.log"));
        Assert.Null(row.EvidencePath); Assert.False(File.Exists(binding.EntryPath));
    }
}
