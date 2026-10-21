using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllTests
{
    private static CommandResult Passed() => new(0, "passed\n", "");

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void EngineJobsStageExactBytesPrivatelyWithoutReplacingDeliveredPlcWorkers(string key)
    {
        using var tree = new StaticTree();
        var source = tree.Write($"engine-source/TiaMcp.Engine.V{key}.exe", "engine bytes");
        tree.Write("engine-source/tool-catalog.json", "catalog bytes");
        tree.Write("engine-source/nested/dependency.dll", "dependency bytes");
        var delivered = tree.Write($"runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe", "delivered PLC bytes");
        var deliveredHost = tree.Write($"runtime/v{key}/TiaMcp.FoundationHost.exe", "delivered host");
        foreach (var variant in new[] { "engine", "sdk" })
        {
            var worker = ReleaseCommands.StageCheckAllEngine(tree.Root, "run", key, variant, source);
            var directory = Path.GetFullPath(Path.Combine(tree.Root, "bin-build/engine-compat/check-all/run", "v" + key, variant));
            Assert.Equal(Path.Combine(directory, $"TiaMcp.Engine.V{key}.exe"), Path.GetFullPath(worker));
            Assert.Equal(ReleaseRecords.HashFile(source), ReleaseRecords.HashFile(worker));
            Assert.Equal("catalog bytes", File.ReadAllText(Path.Combine(directory, "tool-catalog.json")));
            Assert.Equal("dependency bytes", File.ReadAllText(Path.Combine(directory, "nested/dependency.dll")));
            // The jobs keep the source host; nothing host-shaped is placed beside the private worker.
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "TiaMcp.FoundationHost.exe")));
        }
        Assert.Equal("delivered PLC bytes", File.ReadAllText(delivered));
        Assert.Equal("delivered host", File.ReadAllText(deliveredHost));
        Assert.False(File.Exists(Path.Combine(tree.Root, $"runtime/v{key}/worker/TiaMcp.Engine.V{key}.exe")));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.StageCheckAllEngine(tree.Root, "run", key, "missing", Path.Combine(tree.Root, "missing/worker.exe")));
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void EngineCompatibilityHostIsACopyOfTheInstalledHostOutsideRuntime(string key)
    {
        using var tree = new StaticTree();
        tree.Write($"runtime/v{key}/TiaMcp.FoundationHost.exe", "installed host");
        tree.Write($"runtime/v{key}/release-key.txt", key);
        var delivered = tree.Write($"runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe", "delivered PLC bytes");
        var host = ReleaseCommands.StageEngineCompatibilityHost(tree.Root, key);
        Assert.Equal(Path.GetFullPath(Path.Combine(tree.Root, "bin-build/engine-compat", "v" + key, "TiaMcp.FoundationHost.exe")), Path.GetFullPath(host));
        Assert.Equal(Path.GetDirectoryName(ReleaseCommands.EngineCompatibilityDirectory(tree.Root, key)), Path.GetDirectoryName(host));
        Assert.Equal("installed host", File.ReadAllText(host));
        Assert.Equal(key, File.ReadAllText(Path.Combine(Path.GetDirectoryName(host)!, "release-key.txt")));
        // Only the host's own files are copied; the delivered PLC worker stays in runtime/.
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(host)!, "worker", "TiaMcp.PlcWorker." + key + ".exe")));
        Assert.Equal("delivered PLC bytes", File.ReadAllText(delivered));
        var environment = ReleaseCommands.WithEngineCrossCheck(new Dictionary<string, string?>());
        Assert.Equal(Path.GetFullPath(Path.Combine(Repository.FindRoot(), "bin-build/engine-compat")), environment[ReleaseCommands.EngineCrossCheckVariable]);
    }

    [Fact]
    public void ScriptSuccessorsCoverEveryEngineJobExactlyOnce()
    {
        var expected = new List<string> { "usage-audit" };
        foreach (var key in new[] { "20", "21" })
        {
            foreach (var c in new[] { 8, 16 })
            foreach (var transport in new[] { "stdio", "http" })
            foreach (var profile in new[] { "full", "lite" }) expected.Add($"stability-v{key}-c{c}-{transport}-{profile}");
            foreach (var family in new[] { "contracts", "responses", "transport-usage", "usage", "campaign-inputs", "approval", "resources", "ecosystem" })
                expected.Add(family + "-v" + key);
        }
        var mapping = ReleaseCommands.CheckAllScriptSuccessors();
        Assert.Equal(33, mapping.Count);
        Assert.Equal(expected.Order(), mapping.Keys.Order());
        Assert.Equal(33, mapping.Values.Distinct().Count());
        foreach (var name in expected) Assert.Equal(name + "-plc", ReleaseCommands.CheckAllPlcSuccessor(name));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.CheckAllPlcSuccessor("harness-v21-http"));
        var jobs = mapping.SelectMany(pair => new[] { pair.Key, pair.Value }).Select(name => new CheckJob(name, Passed, [])).ToArray();
        ReleaseCommands.ValidateCheckAllScriptSuccessors(jobs);
        foreach (var name in jobs.Select(job => job.Name))
        {
            Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllScriptSuccessors(jobs.Where(job => job.Name != name).ToArray()));
            Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllScriptSuccessors([.. jobs, new(name, Passed, [])]));
        }
    }

    [Fact]
    public void TrackedBytesChecksEveryUniqueFileAndDetectsChangesWithUnmergedStagePaths()
    {
        using var fixture = new OfflineFixtures();
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "README.md"), "original\n");
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "New.cs"), "new source\n");
        const string inventory = "README.md\0README.md\0README.md\0New.cs\0";
        var before = ReleaseCommands.CheckAllTrackedBytes(fixture.DirectoryPath, inventory);
        Assert.Equal(new[] { "New.cs", "README.md" }, before.Keys.Order());
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, "README.md"))), before["README.md"]);
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "New.cs"), "changed source\n");
        var after = ReleaseCommands.CheckAllTrackedBytes(fixture.DirectoryPath, inventory);
        Assert.Equal(before["README.md"], after["README.md"]);
        Assert.NotEqual(before["New.cs"], after["New.cs"]);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
    }

    [Fact]
    public void SchedulerRunsEveryIndependentCheckAfterFailure()
    {
        using var fixture = new OfflineFixtures(); var ran = new List<string>();
        var rows = CheckScheduler.Run([
            new("failure", () => new(1, "", "failure"), []),
            new("dependent", () => throw new Exception("must not execute"), ["failure"]),
            new("independent", () => { ran.Add("independent"); return Passed(); }, [])], 1, fixture.DirectoryPath);
        Assert.Equal(new[] { "failed", "blocked", "passed" }, rows.Select(row => row.Status));
        Assert.Equal(new[] { "independent" }, ran);
        Assert.Equal(3, Directory.GetFiles(fixture.DirectoryPath, "*.log").Length);
    }

    [Fact]
    public void SchedulerHonorsDependenciesResourcesAndExclusiveChecks()
    {
        using var fixture = new OfflineFixtures();
        int active = 0, peak = 0, finished = 0;
        CommandResult Run(bool exclusive)
        {
            int current = Interlocked.Increment(ref active);
            if (exclusive) Assert.Equal(1, current);
            lock (this) peak = Math.Max(peak, current);
            Thread.Sleep(30); Interlocked.Decrement(ref active); Interlocked.Increment(ref finished); return Passed();
        }
        var rows = CheckScheduler.Run([
            new("one", () => Run(false), [], Slots: 2), new("two", () => Run(false), [], Slots: 2),
            new("exclusive", () => Run(true), [], Exclusive: true),
            new("after", () => { Assert.Equal(3, finished); return Passed(); }, ["one", "two", "exclusive"])], 4, fixture.DirectoryPath);
        Assert.All(rows, row => Assert.Equal("passed", row.Status)); Assert.Equal(2, peak);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("slots")]
    public void SchedulerRejectsInvalidPlans(string defect)
    {
        CheckJob[] jobs = defect switch
        {
            "cycle" => [new("a", Passed, ["b"]), new("b", Passed, ["a"])],
            "missing" => [new("a", Passed, ["b"])],
            "duplicate" => [new("a", Passed, []), new("a", Passed, [])],
            _ => [new("a", Passed, [], Slots: 3)]
        };
        Assert.Throws<ReleaseException>(() => CheckScheduler.Validate(jobs, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public void DeterministicShardsAreADisjointCompletePartition(int count)
    {
        var keys = Enumerable.Range(0, 17666).Select(i => "TestClass:theory(" + i + ")").ToArray();
        var shards = Enumerable.Range(0, count).Select(index => keys.Where(key => TestShard.Bucket(key, count) == index).ToArray()).ToArray();
        Assert.Equal(keys.Order(), shards.SelectMany(shard => shard).Order());
        Assert.Equal(keys.Length, shards.Sum(shard => shard.Length));
        Assert.All(keys, key => Assert.InRange(TestShard.Bucket(key, count), 0, count - 1));
        Assert.Equal(0, TestShard.Bucket("PortedPortalAdmissionTests:case-42", 8));
        var costs = keys.ToDictionary(key => key, key => (double)(TestShard.Bucket(key, 13) + 1));
        var (plan, totals) = SuiteCosts.Balance(costs, count);
        Assert.Equal(keys.Order(), plan.Keys.Order()); Assert.Equal(costs.Values.Sum(), totals.Sum());
        Assert.True(totals.Max() - totals.Min() <= 13); Assert.Equal(count, totals.Length);
        Assert.Equal(plan, SuiteCosts.Balance(costs.Reverse().ToDictionary(p => p.Key, p => p.Value), count).Item1);
    }

    [Fact]
    public void LargeSerializableTheoriesSplitWithoutSplittingLegacyProducers()
    {
        Assert.True(TestShard.SplitCases("PortedPortalAdmissionTests")); Assert.True(TestShard.SplitCases("BehaviorParityTests"));
        Assert.True(TestShard.SplitCases("BehaviorCapabilityTests")); Assert.False(TestShard.SplitCases("ApiMetadataChecks"));
        Assert.True(TestShard.SplitCases("TiaMcp.ReleaseTool.Tests.BuildOutputCacheTests"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestShard.Bucket("case", 0));
        Assert.Equal(TestShard.CostKey("Legacy", "Checks", "row one", true), TestShard.CostKey("Legacy", "Checks", "row two", true));
        Assert.NotEqual(TestShard.CostKey("Theory", "Check", "case one", false), TestShard.CostKey("Theory", "Check", "case two", false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void SensitiveClassesRemainExclusiveEvenWithoutHistoryOrWithOldCaseAssignments(int ordinaryCount)
    {
        var cases = TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.Methods.SelectMany(pair => pair.Value.SelectMany(method =>
            Enumerable.Range(0, 7).Select(row => (Class: pair.Key, Key: "T:" + pair.Key + "." + method + "(" + row + ")"))))
            .Concat(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.Classes.Keys.Select(cls => (Class: cls, Key: "T:" + cls + ".Unmeasured()")))
            .Concat(new[] { "Unmeasured", "UnmeasuredTheory" }.SelectMany(cls =>
                Enumerable.Range(0, 7).Select(row => (Class: cls, Key: "T:" + cls + ".Case(" + row + ")")))).ToArray();
        var oldPlan = cases.Take(100).ToDictionary(test => test.Key, _ => 0);
        int count = ordinaryCount + 1;
        var shards = Enumerable.Range(0, count).Select(index => cases.Where(test =>
            TestShard.Assign(test.Class, test.Key, test.Key, count, oldPlan, true) == index).ToArray()).ToArray();
        Assert.Equal(cases.OrderBy(test => test.Key), shards.SelectMany(shard => shard).OrderBy(test => test.Key));
        Assert.Equal(cases.Length, shards.Sum(shard => shard.Length));
        Assert.All(shards[^1], test => Assert.True(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.ContainsCost(test.Key)));
        Assert.All(shards.Take(ordinaryCount).SelectMany(shard => shard), test => Assert.False(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.ContainsCost(test.Key)));
        Assert.Equal(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.Methods.Values.Sum(methods => methods.Length) * 7, shards[^1].Length);
        Assert.Throws<InvalidOperationException>(() => TestShard.Assign("Unmeasured", "T:unknown", "fallback", count,
            new Dictionary<string, int> { ["C:Unmeasured"] = ordinaryCount }, true));
    }

    [Fact]
    public void SensitivePartitionIsScheduledAndMergedUnderTheOriginalSuiteGate()
    {
        using var fixture = new OfflineFixtures();
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Repository.FindRoot(), "tests/test-suites.json"), Repository.FindRoot());
        foreach (string name in new[] { "foundation", "foundation-api", "worker-robustness", "worker-channel", "release-tool" })
        {
            var suite = catalog[name]!.AsObject(); string original = suite.ToJsonString();
            var jobs = new List<CheckJob>();
            FastSuites.AddJobs(jobs, Repository.FindRoot(), name, suite, fixture.DirectoryPath, "dotnet", 4, null, [],
                history: Path.Combine(fixture.DirectoryPath, "history", name));
            var shards = jobs.Where(job => job.Name.Contains("-shard-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(5, shards.Length); Assert.True(shards[^1].Exclusive);
            Assert.Equal(shards.Select(job => job.Name), jobs[^1].Dependencies);
            Assert.True(jobs[^1].AllowFailedDependencies); Assert.Equal(original, suite.ToJsonString());
            var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, name, "shard-plan.json")))!;
            Assert.Equal(4, (int)plan["exclusiveShard"]!);
            Assert.Contains("EngineHttpSessionTests", plan["timeSensitiveClasses"]!.AsObject().Select(pair => pair.Key));
        }
    }

    [Fact]
    public void VariantsHaveIndependentBuildPathsAndKeepEveryExistingFilter()
    {
        var suite = JsonNode.Parse("{\"project\":\"test.csproj\",\"arguments\":[\"--filter\",\"FullyQualifiedName!~Operator\",\"-p:EngineRelease=20\"]}")!.AsObject();
        var unit = CheckAllBuild.Suite(suite);
        Assert.Equal(new[] { "-p:EngineRelease=20" }, unit.Properties);
        Assert.NotEmpty(unit.Variant); Assert.Equal(unit, CheckAllBuild.Suite(suite) with { Properties = unit.Properties });
        Assert.Contains("FullyQualifiedName!~Operator", suite["arguments"]!.AsArray().Select(a => (string)a!));
        suite["arguments"]![2] = "-p:EngineRelease=21";
        Assert.NotEqual(unit.Variant, CheckAllBuild.Suite(suite).Variant);
        foreach (var property in new[] { "-p:DefineConstants=TIA_V20", "-p:TiaSharedAdapterPaths=true", "-p:TiaReleaseKey=20" })
            Assert.NotEmpty(new CheckBuild("variant.csproj", "", [property]).Variant);
        Assert.Equal(new CheckBuild("variant.csproj", "", ["-p:EngineRelease=20", "-p:UseAppHost=false"]).Variant,
            new CheckBuild("variant.csproj", "", ["-p:UseAppHost=false", "-p:EngineRelease=20"]).Variant);
        using var fixture = new OfflineFixtures();
        foreach (var mode in new[] { "false", "true" })
        {
            var studio = new CheckBuild("tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj", "", ["-p:TiaSharedAdapterPaths=" + mode]);
            var source = Path.Combine(fixture.DirectoryPath, "tests/Studio/TiaOpenness.Core.Tests/bin/check-all/TiaOpenness.Core.Tests", studio.Variant, "Release/net10.0-windows");
            var bridge = Path.Combine(fixture.DirectoryPath, "src/Studio/Bridge/bin/check-all/TiaOpenness.Bridge", studio.Variant, "Release/net48");
            var destination = Path.Combine(fixture.DirectoryPath, "tests/Studio/TiaOpenness.Core.Tests/bin/Release", mode == "true" ? "shared-adapter" : "", "net10.0-windows");
            Directory.CreateDirectory(Path.Combine(source, "bridge")); Directory.CreateDirectory(bridge);
            Directory.CreateDirectory(Path.Combine(destination, "bridge"));
            File.WriteAllText(Path.Combine(source, "TiaOpenness.Core.Tests.dll"), "compiled-" + mode);
            File.WriteAllText(Path.Combine(source, "bridge/TiaOpenness.Bridge.exe"), "stale");
            File.WriteAllText(Path.Combine(bridge, "TiaOpenness.Bridge.exe"), "correct-" + mode);
            File.WriteAllText(Path.Combine(destination, "bridge/Newtonsoft.Json.dll"), "stale");
            ReleaseCommands.PrepareCheckAllRuntime(fixture.DirectoryPath, [studio]);
            Assert.Equal("compiled-" + mode, File.ReadAllText(Path.Combine(destination, "TiaOpenness.Core.Tests.dll")));
            Assert.Equal("correct-" + mode, File.ReadAllText(Path.Combine(destination, "bridge/TiaOpenness.Bridge.exe")));
            Assert.False(File.Exists(Path.Combine(destination, "bridge/Newtonsoft.Json.dll")));
            var modified = File.GetLastWriteTimeUtc(Path.Combine(destination, "TiaOpenness.Core.Tests.dll"));
            var bridgeModified = File.GetLastWriteTimeUtc(Path.Combine(destination, "bridge/TiaOpenness.Bridge.exe"));
            ReleaseCommands.PrepareCheckAllRuntime(fixture.DirectoryPath, [studio]);
            Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(destination, "TiaOpenness.Core.Tests.dll")));
            Assert.Equal(bridgeModified, File.GetLastWriteTimeUtc(Path.Combine(destination, "bridge/TiaOpenness.Bridge.exe")));
        }
    }

    [Fact]
    public void ProcessIsolationClearsSdkAndShardState()
    {
        using var fixture = new OfflineFixtures();
        var one = FastSuites.EnvironmentFor(Path.Combine(fixture.DirectoryPath, "one"));
        var two = FastSuites.EnvironmentFor(Path.Combine(fixture.DirectoryPath, "two"), "sdk");
        Assert.Null(one["TIA_MCP_TEST_PUBLIC_API_ROOT"]); Assert.Equal("sdk", two["TIA_MCP_TEST_PUBLIC_API_ROOT"]);
        foreach (var key in new[] { "TEMP", "TMP", "TIA_MCP_DATA_DIRECTORY", "TIA_MCP_TEST_PIPE_SCOPE" }) Assert.NotEqual(one[key], two[key]);
        Assert.Null(one["TIA_MCP_TEST_PRODUCT_ROOT"]); Assert.Null(one["TIA_MCP_TEST_SHARD_COUNT"]);
        Assert.StartsWith(one["TEMP"]!, one["TIA_MCP_DATA_DIRECTORY"]!, StringComparison.OrdinalIgnoreCase);
        for (var parent = new DirectoryInfo(one["TEMP"]!); parent != null; parent = parent.Parent)
            Assert.False(File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")));
    }

    [Theory]
    [InlineData(4, 1, true)]
    [InlineData(5, 1, false)]
    [InlineData(4, 0, false)]
    public void MinimumAndSkipLimitApplyToShardTotals(int minimum, int maximum, bool passes)
    {
        using var fixture = new OfflineFixtures();
        var paths = MakeShards(fixture.DirectoryPath, new[] { "Passed", "Passed" }, new[] { "Passed", "Passed", "NotExecuted" });
        for (int i = 0; i < paths.Length; i++)
        {
            var doc = XDocument.Load(paths[i]);
            foreach (var row in doc.Descendants(doc.Root!.Name.Namespace + "UnitTestResult")) row.SetAttributeValue("testName", i + ":" + row.Attribute("testName")!.Value);
            doc.Save(paths[i]);
        }
        var result = SuiteSharding.Merge("suite", new JsonObject { ["minimumPassed"] = minimum, ["maximumSkipped"] = maximum }, paths, fixture.DirectoryPath);
        Assert.Equal(passes ? 0 : 1, result.ExitCode);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "suite.json")))!;
        Assert.Equal(4, (int)summary["passed"]!); Assert.Equal(1, (int)summary["skipped"]!);
    }

    [Fact]
    public void EmptyFirstShardDoesNotLoseLaterResults()
    {
        using var fixture = new OfflineFixtures();
        var paths = MakeShards(fixture.DirectoryPath, Array.Empty<string>(), new[] { "Passed", "Passed" });
        var empty = XDocument.Load(paths[0]); var ns = empty.Root!.Name.Namespace;
        empty.Root.Element(ns + "Results")!.Remove(); empty.Root.Element(ns + "TestDefinitions")!.Remove(); empty.Save(paths[0]);
        var result = SuiteSharding.Merge("suite", new JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 }, paths, fixture.DirectoryPath);
        Assert.Equal(0, result.ExitCode); Assert.Equal(2, SuiteSharding.Identities([Path.Combine(fixture.DirectoryPath, "suite.trx")]).Values.Sum());
    }

    [Fact]
    public void MergeCannotHideFailedOrMissingShards()
    {
        using var fixture = new OfflineFixtures(); var paths = MakeShards(fixture.DirectoryPath, new[] { "Passed" }, new[] { "Failed" });
        var suite = new JsonObject { ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };
        Assert.Equal(1, SuiteSharding.Merge("suite", suite, paths, fixture.DirectoryPath).ExitCode);
        Assert.Throws<ReleaseException>(() => { SuiteSharding.Merge("suite", suite, [paths[0], paths[1] + ".missing"], fixture.DirectoryPath); });
    }

    [Fact]
    public void IdentityProofComparesMultisetsAndOutcomes()
    {
        using var fixture = new OfflineFixtures(); var paths = MakeShards(fixture.DirectoryPath, new[] { "Passed" }, new[] { "Passed" });
        SuiteSharding.Compare([paths[0]], [paths[1]]);
        Assert.Throws<ReleaseException>(() => SuiteSharding.Compare([paths[0]], paths));
        var doc = XDocument.Load(paths[1]); doc.Descendants(doc.Root!.Name.Namespace + "UnitTestResult").Single().SetAttributeValue("outcome", "Failed"); doc.Save(paths[1]);
        SuiteSharding.Compare([paths[0]], [paths[1]]);
        Assert.Throws<ReleaseException>(() => SuiteSharding.Compare([paths[0]], [paths[1]], outcomes: true));
        paths = MakeShards(fixture.DirectoryPath, new[] { "Failed" }, new[] { "Failed" });
        Assert.Equal(1, SuiteSharding.Merge("failed", new JsonObject { ["minimumPassed"] = 1, ["maximumSkipped"] = 0 }, [paths[1]], fixture.DirectoryPath, paths[0]).ExitCode);
        var proof = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "failed.json")))!;
        Assert.Equal("equal", (string)proof["equivalence"]!); Assert.True((bool)proof["identitiesEqual"]!); Assert.True((bool)proof["outcomesEqual"]!);
    }

    [Fact]
    public void WarmCachePreservesUnchangedOutputTimeAndRepairsCorruption()
    {
        using var fixture = new OfflineFixtures();
        var output = Path.Combine(fixture.DirectoryPath, "output"); Directory.CreateDirectory(output);
        var path = Path.Combine(output, "library.dll"); File.WriteAllText(path, "original");
        var cache = new BuildOutputCache(Path.Combine(fixture.DirectoryPath, "cache"), "check-all");
        var key = BuildOutputCache.Key([], []); cache.Populate(key, output);
        var timestamp = DateTime.UtcNow.AddHours(-1); File.SetLastWriteTimeUtc(path, timestamp); timestamp = File.GetLastWriteTimeUtc(path);
        Assert.True(cache.Restore(key, output)); Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        File.WriteAllText(path, "corrupted"); Assert.True(cache.Restore(key, output)); Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void HarnessInventoryPreservesBothReleasesAndV21OnlyModes()
    {
        var v20 = ReleaseCommands.CheckAllHarnessModes("20"); var v21 = ReleaseCommands.CheckAllHarnessModes("21");
        Assert.Equal(16, v20.Length); Assert.Equal(18, v21.Length);
        Assert.Equal(v20, v21.Take(16)); Assert.Contains("test-download-route", v21); Assert.Contains("test-match-plc-name", v21);
        Assert.Contains("descriptor-catalog-only", v20); Assert.Contains("check-modes-self-test", v20);
    }

    [Fact]
    public void HarnessSuccessorsAndD5PartitionEveryOriginalJobWithoutRetirement()
    {
        var expected = new[] { "20", "21" }.SelectMany(key => ReleaseCommands.CheckAllHarnessModes(key)
            .Select(mode => "harness-v" + key + "-" + (mode.Length == 0 ? "http" : mode))).ToArray();
        var mapping = ReleaseCommands.CheckAllHarnessSuccessors(); var d5 = ReleaseCommands.CheckAllHarnessD5();
        Assert.Equal(34, expected.Length); Assert.Equal(32, mapping.Count); Assert.Equal(2, d5.Count);
        Assert.Equal(expected.Order(), mapping.Keys.Concat(d5.Keys).Order());
        Assert.Empty(mapping.Keys.Intersect(d5.Keys)); Assert.Equal(32, mapping.Values.Distinct().Count());
        Assert.All(mapping, pair => Assert.Equal(pair.Key + "-plc", pair.Value));
        Assert.All(d5, pair => { Assert.EndsWith("-engineering-api-only", pair.Key); Assert.Contains("D5", pair.Value); });
        var assertions = ReleaseCommands.CheckAllHarnessD5Assertions();
        Assert.Equal(8, assertions.Count);
        Assert.All(assertions, pair => { Assert.Contains(pair.Key, mapping.Keys); Assert.StartsWith("D5:", pair.Value); });
        var jobs = expected.SelectMany(name => mapping.TryGetValue(name, out var plc) ? new[] { name, plc } : new[] { name })
            .Select(name => new CheckJob(name, Passed, [])).ToArray();
        ReleaseCommands.ValidateCheckAllHarnessSuccessors(jobs);
        foreach (var name in jobs.Select(job => job.Name))
        {
            Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllHarnessSuccessors(jobs.Where(job => job.Name != name).ToArray()));
            Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllHarnessSuccessors([.. jobs, new(name, Passed, [])]));
        }
        var swapped = jobs.ToArray(); (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllHarnessSuccessors(swapped));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllHarnessSuccessors([.. jobs, new("harness-v21-engineering-api-only-plc", Passed, [])]));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllHarnessSuccessors([.. jobs, new("harness-v21-invented", Passed, [])]));
    }

    [Fact]
    public void PlcHarnessSuccessorsProveTheirHostWorkerAndDiagnosticsGates()
    {
        using var fixture = new OfflineFixtures();
        string host = Path.Combine(fixture.DirectoryPath, "TiaMcp.FoundationHost.exe"), worker = Path.Combine(fixture.DirectoryPath, "TiaMcp.PlcWorker.21.exe");
        File.WriteAllText(host, "host"); File.WriteAllText(worker, "worker");
        string inventory = Path.Combine(fixture.DirectoryPath, "adapter-coverage-v21.json"); File.WriteAllText(inventory, "{\"count\":1200}");
        string Evidence(string mode, Action<JsonObject>? change = null)
        {
            var directory = Path.Combine(fixture.DirectoryPath, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var value = new JsonObject { ["mode"] = mode, ["release"] = "21", ["nativeTiaExecuted"] = false, ["workerModel"] = "foundation-host-plc-worker",
                ["runtimeSha256"] = ReleaseRecords.HashFile(worker), ["sdkWorkerSha256"] = ReleaseRecords.HashFile(worker), ["hostSha256"] = ReleaseRecords.HashFile(host) };
            change?.Invoke(value);
            File.WriteAllText(Path.Combine(directory, "result.json"), value.ToJsonString());
            return directory;
        }
        ReleaseCommands.ValidateCheckAllPlcHarness("", Evidence("http"), "", "21", host, worker, inventory);
        ReleaseCommands.ValidateCheckAllPlcHarness("", Evidence("hmi-only"), "hmi-only", "21", host, worker, inventory);
        foreach (var change in new Action<JsonObject>[] { v => v["mode"] = "hmi-only", v => v["release"] = "20", v => v["nativeTiaExecuted"] = true, v => v.Remove("nativeTiaExecuted"),
            v => v["workerModel"] = "combined-host-supervised-worker", v => v["runtimeSha256"] = "0", v => v["sdkWorkerSha256"] = "0", v => v["hostSha256"] = "0" })
            Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllPlcHarness("", Evidence("http", change), "", "21", host, worker, inventory));
        const string diagnostics = "COMPLETE: 1150 adapter native diagnostic wrappers JIT prepared; 50 open generic wrappers\nCOMPLETE: 9 adapter integration diagnostic checks passed\n"
            + "COMPLETE: 3 native journal reader checks passed; no native call executed\nCOMPLETE: 4 direct SDK guard checks passed; no native call executed\n";
        ReleaseCommands.ValidateCheckAllPlcHarness(diagnostics, Evidence("native-diagnostics-only"), "native-diagnostics-only", "21", host, worker, inventory);
        foreach (var broken in new[] { diagnostics.Replace("50 open", "49 open"), diagnostics.Replace("9 adapter integration", "7 adapter integration"),
            diagnostics.Replace("3 native journal", "2 native journal"), diagnostics.Replace("4 direct SDK", "3 direct SDK"), diagnostics.Replace("COMPLETE: 4 direct SDK guard checks passed", "") })
            Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateCheckAllPlcHarness(broken, Evidence("native-diagnostics-only"), "native-diagnostics-only", "21", host, worker, inventory));
    }

    [Fact]
    public void CandidateHarnessBuildsSelectOneFamilyAndCannotOverwriteProduction()
    {
        var modes = ReleaseCommands.CheckAllCandidateModes(); Assert.Equal(8, modes.Length);
        foreach (var release in new[] { "20", "21" })
        foreach (var mode in modes)
        {
            var unit = ReleaseCommands.CheckAllCandidateBuild(release, mode);
            Assert.Equal($"src/Engine/TiaMcp.Engine.V{release}.csproj", unit.Project);
            Assert.StartsWith("candidate-", unit.Variant);
            var policy = Assert.Single(unit.Properties);
            Assert.DoesNotContain(",", policy);
            Assert.Equal("-p:TiaMcpTestPolicy=P6-" + (mode == "save-close" ? "CLOSE" : mode.ToUpperInvariant()) + ":safe-v4", policy);
        }
    }

    private static string[] MakeShards(string directory, params string[][] outcomes)
    {
        return outcomes.Select((rows, index) =>
        {
            var path = Path.Combine(directory, "shard-" + index + ".trx"); DotnetSuiteSelfTests.SyntheticTrx(path, rows);
            var doc = XDocument.Load(path); var ns = doc.Root!.Name.Namespace;
            foreach (var result in doc.Descendants(ns + "UnitTestResult")) result.SetAttributeValue("executionId", index + ":" + result.Attribute("executionId")!.Value);
            doc.Save(path); return path;
        }).ToArray();
    }
}
