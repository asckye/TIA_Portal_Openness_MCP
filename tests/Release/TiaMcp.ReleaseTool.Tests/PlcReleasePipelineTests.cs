using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlcReleasePipelineTests
{
    [Fact]
    public void TransportOnlyQuickPlanStillProducesUsageAuditInputsAndFoundationApprovalSelectsSevenChecks()
    {
        using var tree = new StaticTree();
        var jobs = Jobs(tree.Root);
        var transport = new ReleaseCheckPlan("quick", ["foundation-transport"], [], []);
        var approval = new ReleaseCheckPlan("quick", ["foundation-approval"], [], []);
        foreach (var key in new[] { "20", "21" })
        {
            Assert.Contains(jobs, job => job.Name == "usage-v" + key + "-plc" && ReleaseCommands.PlcReleaseSelected(transport, job));
            Assert.Contains(jobs, job => job.Name == "approval-v" + key + "-plc" && ReleaseCommands.PlcReleaseSelected(approval, job));
        }
    }

    private static IReadOnlyList<PlcReleaseJob> Jobs(string root) => ReleaseCommands.PlcReleaseJobs(root,
        Path.Combine(root, "sdk"), Path.Combine(root, "logs"), Path.Combine(root, "temp"), "dotnet", "python");

    [Fact]
    [Trait("Category", "Pipeline")]
    public void EveryHarnessSuccessorAndEveryReleaseGroupHasAConcreteCommand()
    {
        using var tree = new StaticTree();
        var jobs = Jobs(tree.Root);
        Assert.Equal(jobs.Count, jobs.Select(job => job.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var plc in ReleaseCommands.CheckAllPlcHarnessJobs())
        {
            var job = Assert.Single(jobs.Where(job => job.Name == plc));
            Assert.Contains("Test-PlcHarness.py", job.Arguments[0]);
            Assert.Contains("--worker", job.Arguments);
            Assert.DoesNotContain("--engine-worker", job.Arguments);
            Assert.Equal(plc.Split('-')[1][1..], job.Release);
        }
        foreach (var group in ReleaseCommands.PlcReleaseGroups)
            foreach (var key in new[] { "20", "21" })
                Assert.Contains(jobs, job => job.Check == "plc-" + group && job.Release == key);
        foreach (var key in new[] { "20", "21" })
        {
            var engineering = Assert.Single(jobs.Where(job => job.Name == "engineering-api-v" + key + "-plc"));
            Assert.Equal("test-suites", engineering.Executable);
            Assert.Contains("foundation-api", engineering.Arguments);
            Assert.Contains(jobs, job => job.Name == "native-safety-v" + key + "-plc" && job.Arguments.SequenceEqual(["--self-test"]));
            var transport = Assert.Single(jobs.Where(job => job.Name == "worker-protocol-v" + key + "-plc"));
            Assert.Contains("--plc-sdk-worker", transport.Arguments);
            Assert.DoesNotContain("--stdio-only", transport.Arguments);
        }
    }

    [Fact]
    public void SnapshotsCoverBothLayoutsAndTransportsWithExactReleaseFixtureOverrides()
    {
        using var tree = new StaticTree();
        var captures = Jobs(tree.Root).Where(job => job.Mode == "capture").ToArray();
        Assert.Equal(16, captures.Length);
        Assert.Equal(16, captures.Select(job => job.Evidence).Distinct(StringComparer.Ordinal).Count());
        foreach (var job in captures)
        {
            var args = job.Arguments;
            Assert.Equal("capture", args[1]);
            Assert.Contains("--engine-host", args);
            Assert.Equal(job.Release, args[Array.IndexOf(args, "--releases") + 1]);
            var worker = args[Array.IndexOf(args, "--plc-worker") + 1];
            Assert.StartsWith(job.Release + "=", worker);
            Assert.EndsWith("TiaMcp.PlcWorker." + job.Release + ".exe", worker);
            Assert.DoesNotContain("--engine-worker", args);
            Assert.DoesNotContain("--engine-catalog", args);
        }
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void StabilityRetainsFourProfilesAndTransportsAtBothConcurrencyLevels(string key)
    {
        using var tree = new StaticTree();
        var jobs = Jobs(tree.Root).Where(job => job.Release == key && job.Check is "plc-stability" or "plc-isolated-stability").ToArray();
        Assert.Equal(2, jobs.Length);
        foreach (var job in jobs)
        {
            Assert.Equal("50", job.Arguments[Array.IndexOf(job.Arguments, "--rounds") + 1]);
            Assert.Equal(job.Check == "plc-stability" ? "8" : "16", job.Arguments[Array.IndexOf(job.Arguments, "--concurrency") + 1]);
            Assert.DoesNotContain("--profiles", job.Arguments); // The script's complete full/lite default.
            Assert.DoesNotContain("--transports", job.Arguments); // The complete stdio/HTTP default.
        }
        Assert.Throws<ReleaseException>(() => ReleaseCommands.PlcReleaseJobs(tree.Root, tree.Root, tree.Root, tree.Root, "dotnet", "python", 9));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void ProductionInstallRemovesTheEnginePayloadAndRequiresTheMatchingAdapter(string key)
    {
        using var tree = new StaticTree();
        var source = $"src/PlcWorker/bin/{key}/Release/net48/";
        tree.Write(source + $"TiaMcp.PlcWorker.{key}.exe", "production-plc");
        tree.Write(source + $"TiaMcp.Adapter.{key}.dll", "adapter");
        tree.Write(source + "TiaMcp.Adapters.Contracts.dll", "contracts");
        tree.Write($"runtime/v{key}/worker/TiaMcp.Engine.V{key}.exe", "engine");
        ReleaseCommands.InstallReleasePlcWorker(tree.Root, key);
        var installed = ReleaseCommands.PlcReleaseInstallDirectory(tree.Root, key);
        Assert.Equal("production-plc", File.ReadAllText(Path.Combine(installed, $"TiaMcp.PlcWorker.{key}.exe")));
        Assert.False(File.Exists(Path.Combine(tree.Root, $"runtime/v{key}/worker/TiaMcp.Engine.V{key}.exe")));
        Assert.Equal("worker", Path.GetFileName(installed));
        tree.Write(source + "TiaMcp.Adapter.wrong.dll", "wrong adapter");
        Assert.Throws<ReleaseException>(() => ReleaseCommands.InstallReleasePlcWorker(tree.Root, key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotRunnerRequiresBothSemanticSuccessAndFrozenBytes(bool compareFails)
    {
        using var tree = new StaticTree();
        var job = Jobs(tree.Root).First(job => job.Mode == "capture");
        tree.Write("manifest/contracts/v4/baseline/20.json", "frozen\n");
        Directory.CreateDirectory(job.Evidence);
        File.WriteAllText(Path.Combine(job.Evidence, "20.json"), compareFails ? "frozen\n" : "changed\n");
        var compared = false;
        Assert.Throws<ReleaseException>(() => ReleaseCommands.RunPlcReleaseJobs([job], tree.Root, Path.Combine(tree.Root, "logs"),
            _ => new(0, "", ""), (_, args) =>
            {
                compared = true;
                Assert.Equal("compare", args[0]); Assert.Contains("20", args);
                if (compareFails) throw new ReleaseException("semantic difference");
            }));
        Assert.True(compared);
    }

    private static PlcReleaseJob[] ParallelFixtureJobs() =>
    [
        new("native-coverage-v20-plc", "native-coverage", "dotnet", ["verify"], Release: "20"),
        new("jit-adapter-coverage-v20-plc", "native-diagnostics", "dotnet", ["verify"], Release: "20"),
        new("campaign-inputs-v20-plc", "plc-functional", "python", ["a"], Release: "20"),
        new("campaign-inputs-v21-plc", "plc-functional", "python", ["b"], Release: "21"),
        new("worker-protocol-v20-plc", "plc-worker-isolation", "python", ["c"], Mode: "x", Release: "20"),
        new("engine-host-v20-plc", "http-concurrency", "test-suites", ["-Suite", "engine-host"], Release: "20"),
        new("worker-robustness-v20-plc", "plc-worker-isolation", "test-suites", ["-Suite", "worker-robustness"], Release: "20"),
        new("native-coverage-v21-plc", "native-coverage", "dotnet", ["verify"], Release: "21"),
    ];

    // P8-93 early (maintainer 2026-10-21): the release runs the independent PLC jobs in parallel, after the inventory producers
    // and before the exclusive jobs, which keep their table order.
    [Fact]
    public void ParallelReleaseRunKeepsInventoryFirstAndExclusiveJobsLast()
    {
        using var tree = new StaticTree();
        var jobs = ParallelFixtureJobs();
        var order = new List<string>();
        ReleaseCommands.RunPlcReleaseJobs(jobs, tree.Root, tree.Root, job => { lock (order) order.Add(job.Name); return new(0, "", ""); },
            (_, _) => throw new InvalidOperationException("no capture in this fixture"), parallelism: 4);
        Assert.Equal(jobs.Select(job => job.Name).Order(), order.Order());
        Assert.Equal(["native-coverage-v20-plc", "jit-adapter-coverage-v20-plc", "native-coverage-v21-plc"], order.Take(3));
        Assert.Equal(["engine-host-v20-plc", "worker-robustness-v20-plc"], order.Skip(order.Count - 2));
        Assert.True(ReleaseCommands.IsExclusivePlcJob(new("stability-c8-v20-plc", "plc-stability", "python", ["d"])));
        Assert.True(ReleaseCommands.IsExclusivePlcJob(new("stability-c16-v20-plc", "plc-isolated-stability", "python", ["d"])));
    }

    [Fact]
    public void ParallelReleaseRunReportsFailuresAndStartsNoExclusiveJob()
    {
        using var tree = new StaticTree();
        var ran = new List<string>();
        var error = Assert.Throws<ReleaseException>(() => ReleaseCommands.RunPlcReleaseJobs(ParallelFixtureJobs(), tree.Root, tree.Root, job =>
        {
            lock (ran) ran.Add(job.Name);
            return job.Name == "campaign-inputs-v21-plc" ? new(3, "", "failed PLC check") : new(0, "", "");
        }, (_, _) => throw new InvalidOperationException(), parallelism: 4));
        Assert.Contains("campaign-inputs-v21-plc", error.Message);
        Assert.DoesNotContain("engine-host-v20-plc", ran);
        Assert.DoesNotContain("worker-robustness-v20-plc", ran);
    }

    [Fact]
    public void FailedSuccessorStopsThePipelineAndCannotProducePassedEvidence()
    {
        using var tree = new StaticTree();
        var jobs = Jobs(tree.Root).Take(2).ToArray();
        var calls = 0;
        Assert.Throws<ReleaseException>(() => ReleaseCommands.RunPlcReleaseJobs(jobs, tree.Root, tree.Root,
            _ => { calls++; return new(8, "", "failed PLC check"); }, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothEngineeringApiSuccessorsShareOneShardedFoundationApiRunAndItsResult(int exitCode)
    {
        using var tree = new StaticTree();
        var logs = Path.Combine(tree.Root, "logs"); Directory.CreateDirectory(logs);
        var jobs = Jobs(tree.Root);
        var v20 = Assert.Single(jobs, job => job.Name == "engineering-api-v20-plc");
        var v21 = Assert.Single(jobs, job => job.Name == "engineering-api-v21-plc");
        Assert.Equal(v20.Arguments, v21.Arguments);
        Assert.Equal("8", v20.Arguments[Array.IndexOf(v20.Arguments, "-Shards") + 1]);
        Assert.Equal("foundation-api", v20.Arguments[Array.IndexOf(v20.Arguments, "-Suite") + 1]);
        Assert.Equal(Path.Combine(tree.Root, "logs", "dotnet-suites", "foundation-api"), v20.Arguments[Array.IndexOf(v20.Arguments, "-ResultsDirectory") + 1]);
        Assert.Equal(ReleaseCommands.FoundationApiSuiteArguments("dotnet", Path.Combine(tree.Root, "sdk"), logs), v20.Arguments);
        var calls = new List<string>(); var shared = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var run = ReleaseCommands.ShareIdenticalSuiteRuns(job => { calls.Add(job.Name); return new(exitCode, "suite " + job.Name, ""); }, shared, logs,
            new Dictionary<string, (string Job, CommandResult Result)>(StringComparer.Ordinal));
        var robustness = jobs.Where(job => job.Name.StartsWith("worker-robustness-", StringComparison.Ordinal)).ToArray();
        var first = run(v20); var second = run(v21);
        foreach (var job in robustness) run(job);
        Assert.Equal(["engineering-api-v20-plc", .. robustness.Select(job => job.Name)], calls);
        Assert.Same(first, second);
        Assert.Equal(exitCode, second.ExitCode);
        Assert.Equal("engineering-api-v20-plc", Assert.Single(shared, pair => pair.Key == "engineering-api-v21-plc").Value);
        Assert.Single(shared);
        Assert.Contains("Shared run: engineering-api-v20-plc", File.ReadAllText(Path.Combine(logs, "engineering-api-v21-plc.log")));
        if (exitCode != 0)
            Assert.Throws<ReleaseException>(() => ReleaseCommands.RunPlcReleaseJobs([v21], tree.Root, logs, run, (_, _) => { }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OfflineSuitesAndBothEngineeringApiSuccessorsShareTheOneShardedFoundationApiRun(int exitCode)
    {
        using var tree = new StaticTree();
        var logs = Path.Combine(tree.Root, "logs"); Directory.CreateDirectory(logs);
        var arguments = ReleaseCommands.FoundationApiSuiteArguments("dotnet", Path.Combine(tree.Root, "sdk"), logs);
        Assert.Equal("8", arguments[Array.IndexOf(arguments, "-Shards") + 1]);
        var results = arguments[Array.IndexOf(arguments, "-ResultsDirectory") + 1];
        tree.Write("logs/dotnet-suites/foundation-api/shard-9/stale.trx", "stale");
        var runs = new Dictionary<string, (string Job, CommandResult Result)>(StringComparer.Ordinal);
        var suiteCalls = 0;
        var offline = ReleaseCommands.RunSharedSuite("offline-suites foundation-api", arguments, logs, given =>
        {
            suiteCalls++;
            Assert.Equal(arguments, given);
            Assert.False(Directory.Exists(Path.Combine(results, "shard-9")), "a previous run's shards are removed first");
            return new(exitCode, "Suites: sharded foundation-api", "");
        }, runs);
        Assert.Same(offline, ReleaseCommands.RunSharedSuite("offline-suites foundation-api", arguments, logs, _ => throw new InvalidOperationException(), runs));
        var shared = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var run = ReleaseCommands.ShareIdenticalSuiteRuns(job => throw new InvalidOperationException("must reuse the offline-suites run: " + job.Name), shared, logs, runs);
        var jobs = Jobs(tree.Root);
        foreach (var name in new[] { "engineering-api-v20-plc", "engineering-api-v21-plc" })
        {
            var job = Assert.Single(jobs, job => job.Name == name);
            Assert.Same(offline, run(job));
            Assert.Contains("Shared run: offline-suites foundation-api", File.ReadAllText(Path.Combine(logs, name + ".log")));
            if (exitCode != 0) Assert.Throws<ReleaseException>(() => ReleaseCommands.RunPlcReleaseJobs([job], tree.Root, logs, run, (_, _) => { }));
        }
        Assert.Equal(1, suiteCalls);
        Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["engineering-api-v20-plc"] = "offline-suites foundation-api", ["engineering-api-v21-plc"] = "offline-suites foundation-api" }, shared);
    }

    [Fact]
    public void BranchGateRunsFiftyStabilityRoundsByDefaultAndRefusesFewer()
    {
        Options Parse(params string[] args) => Options.Parse(args, new HashSet<string>());
        Assert.Equal(50, ReleaseCommands.BranchGateMinimumStabilityRounds);
        Assert.Equal(50, ReleaseCommands.BranchGateStabilityRounds(Parse()));
        Assert.Equal(50, ReleaseCommands.BranchGateStabilityRounds(Parse("-LocalStabilityRounds", "50")));
        Assert.Equal(120, ReleaseCommands.BranchGateStabilityRounds(Parse("-LocalStabilityRounds", "120")));
        foreach (var value in new[] { "49", "10", "0", "ten" })
        {
            var refused = Assert.Throws<ReleaseException>(() => ReleaseCommands.BranchGateStabilityRounds(Parse("-LocalStabilityRounds", value)));
            Assert.Contains("at least 50 stability rounds", refused.Message);
            Assert.Contains("-LocalStabilityRounds " + value, refused.Message);
        }
    }

    [Fact]
    public void PreparedEvidenceListsEveryPlcFileOnceIncludingTheAlreadyBoundApprovalReceipts()
    {
        using var tree = new StaticTree();
        var logs = Path.Combine(tree.Root, "logs");
        var approval = tree.Write("logs/approval-v20-plc/result.json", "{}");
        var usage = tree.Write("logs/usage-v21-plc.json", "[]");
        var fixture = tree.Write("logs/plc-fixtures/sdk/obj/project.assets.json", "{}");
        var unrelated = tree.Write("logs/tools-20.json", "{}");
        // Bound earlier with a forward-slash path, as build-multi-version does.
        var evidence = new List<string> { Path.Combine(logs, "approval-v20-plc/result.json"), unrelated };
        ReleaseCommands.AddPlcReleaseEvidence(evidence, logs);
        var relative = evidence.Select(path => Path.GetRelativePath(tree.Root, Path.GetFullPath(path)).Replace('\\', '/')).ToArray();
        Assert.Equal(relative.Length, relative.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["logs/approval-v20-plc/result.json", "logs/plc-fixtures/sdk/obj/project.assets.json", "logs/tools-20.json", "logs/usage-v21-plc.json"],
            relative.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(approval) && File.Exists(usage) && File.Exists(fixture));
    }

    [Fact]
    public void ARepeatedRunRemovesOnlyEachJobsOwnPreviousOutputsBelowItsLogDirectory()
    {
        using var tree = new StaticTree();
        var logs = Path.Combine(tree.Root, "logs");
        var jobs = Jobs(tree.Root);
        var stale = new List<string>();
        foreach (var job in jobs.Where(job => job.Name is "stability-c8-v20-plc" or "campaign-inputs-v21-plc" or "usage-v20-plc" or "approval-v21-plc" or "harness-v20-http-plc" or "responses-source-http-v21-plc"))
        {
            foreach (var option in new[] { "--output", "--evidence", "--coverage-output" })
                if (Array.IndexOf(job.Arguments, option) is var index and >= 0)
                {
                    var path = job.Arguments[index + 1];
                    if (path.EndsWith(".json", StringComparison.Ordinal)) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "old"); }
                    else { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "result.json"), "old"); }
                    stale.Add(path);
                }
            ReleaseCommands.ResetPlcReleaseJobOutputs(job, logs);
        }
        Assert.Equal(7, stale.Count);
        Assert.All(stale, path => Assert.False(File.Exists(path) || Directory.Exists(path), path));
        var keep = tree.Write("logs/plc-fixtures/sdk/bin/20/Release/net48/TiaMcp.PlcWorker.20.exe", "fixture");
        var outside = tree.Write("elsewhere/result.json", "kept");
        ReleaseCommands.ResetPlcReleaseJobOutputs(new PlcReleaseJob("foreign", "plc-functional", "python",
            ["script.py", "--output", Path.GetDirectoryName(outside)!, "--worker", keep], outside), logs);
        Assert.True(File.Exists(keep)); Assert.True(File.Exists(outside));
    }

    [Fact]
    public void HarnessAndUsageSuccessorsKeepTheCheckAllHostAndWorkerIdentityGates()
    {
        using var tree = new StaticTree();
        var logs = Path.Combine(tree.Root, "logs");
        var worker = new PlcWorkerFixtureBuild("20", "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"));
        Directory.CreateDirectory(Path.GetDirectoryName(worker)!); File.WriteAllText(worker, "sdk-worker");
        tree.Write("runtime/v20/TiaMcp.FoundationHost.exe", "host");
        string workerHash = ReleaseRecords.HashFile(worker), hostHash = ReleaseRecords.HashFile(Path.Combine(tree.Root, "runtime/v20/TiaMcp.FoundationHost.exe"));
        var jobs = Jobs(tree.Root);
        var harness = Assert.Single(jobs, job => job.Name == "harness-v20-http-plc");
        var output = harness.Arguments[Array.IndexOf(harness.Arguments, "--output") + 1];
        var evidence = new JsonObject { ["mode"] = "http", ["release"] = "20", ["nativeTiaExecuted"] = false, ["workerModel"] = "foundation-host-plc-worker",
            ["runtimeSha256"] = workerHash, ["sdkWorkerSha256"] = workerHash, ["hostSha256"] = hostHash };
        Directory.CreateDirectory(output);
        void Harness() => ReleaseCommands.ValidatePlcReleaseJob(harness, new(0, "COMPLETE: 3 Foundation transport checks passed" + Environment.NewLine, ""), tree.Root, logs);
        File.WriteAllText(Path.Combine(output, "result.json"), evidence.ToJsonString());
        Harness();
        evidence["workerModel"] = "combined-host-supervised-worker";
        File.WriteAllText(Path.Combine(output, "result.json"), evidence.ToJsonString());
        Assert.Throws<ReleaseException>(Harness);
        File.Delete(Path.Combine(output, "result.json"));
        Assert.ThrowsAny<Exception>(Harness);

        var usage = Assert.Single(jobs, job => job.Name == "usage-v20-plc");
        var coverage = usage.Arguments[Array.IndexOf(usage.Arguments, "--coverage-output") + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(coverage)!);
        var receipt = new JsonObject { ["releaseKey"] = "20", ["workerModel"] = "foundation-host-plc-worker", ["runtimeSha256"] = workerHash, ["sdkWorkerSha256"] = workerHash, ["hostSha256"] = hostHash };
        void Usage() => ReleaseCommands.ValidatePlcReleaseJob(usage, new(0, "", ""), tree.Root, logs);
        File.WriteAllText(coverage, new JsonArray(receipt.DeepClone()).ToJsonString());
        Usage();
        foreach (var (field, value) in new[] { ("hostSha256", "other"), ("sdkWorkerSha256", "other"), ("workerModel", "combined-host-supervised-worker"), ("releaseKey", "21") })
        {
            var changed = receipt.DeepClone().AsObject(); changed[field] = value;
            File.WriteAllText(coverage, new JsonArray(changed).ToJsonString());
            Assert.Throws<ReleaseException>(Usage);
        }
        File.WriteAllText(coverage, new JsonArray(receipt.DeepClone(), receipt.DeepClone()).ToJsonString());
        Assert.ThrowsAny<Exception>(Usage);
    }

    [Theory]
    [InlineData("rounds")]
    [InlineData("runs")]
    [InlineData("workerHash")]
    [InlineData("hostHash")]
    [InlineData("native")]
    [InlineData("status")]
    [InlineData("isolated")]
    public void StabilityEvidenceRejectsIncompleteOrForeignWorkerResults(string mutation)
    {
        using var tree = new StaticTree();
        var job = Jobs(tree.Root).First(job => job.Check == "plc-isolated-stability");
        var logs = Path.Combine(tree.Root, "logs");
        var worker = new PlcWorkerFixtureBuild("20", "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"));
        Directory.CreateDirectory(Path.GetDirectoryName(worker)!); File.WriteAllText(worker, "sdk-worker");
        tree.Write("runtime/v20/TiaMcp.FoundationHost.exe", "host");
        Directory.CreateDirectory(Path.GetDirectoryName(job.Evidence)!);
        var evidence = new JsonObject { ["status"] = "passed", ["rounds"] = 50, ["runs"] = new JsonArray(1, 2, 3, 4),
            ["isolatedWorker"] = true, ["nativeTiaExecuted"] = false,
            ["runtimeSha256"] = ReleaseRecords.HashFile(worker), ["sdkWorkerSha256"] = ReleaseRecords.HashFile(worker),
            ["hostSha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, "runtime/v20/TiaMcp.FoundationHost.exe")) };
        File.WriteAllText(job.Evidence, evidence.ToJsonString());
        ReleaseCommands.ValidatePlcReleaseJob(job, new(0, "", ""), tree.Root, logs);
        switch (mutation)
        {
            case "rounds": evidence["rounds"] = 49; break;
            case "runs": evidence["runs"]!.AsArray().RemoveAt(0); break;
            case "workerHash": evidence["sdkWorkerSha256"] = "other"; break;
            case "hostHash": evidence["hostSha256"] = "other"; break;
            case "native": evidence["nativeTiaExecuted"] = true; break;
            case "status": evidence["status"] = "failed"; break;
            case "isolated": evidence["isolatedWorker"] = false; break;
        }
        File.WriteAllText(job.Evidence, evidence.ToJsonString());
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidatePlcReleaseJob(job, new(0, "", ""), tree.Root, logs));
    }
}
