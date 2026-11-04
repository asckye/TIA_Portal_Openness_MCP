using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PlcReleasePipelineTests
{
    private static IReadOnlyList<PlcReleaseJob> Jobs(string root) => ReleaseCommands.PlcReleaseJobs(root,
        Path.Combine(root, "sdk"), Path.Combine(root, "logs"), Path.Combine(root, "temp"), "dotnet", "python");

    [Fact]
    [Trait("Category", "Pipeline")]
    public void EveryHarnessSuccessorAndEveryReleaseGroupHasAConcreteCommand()
    {
        using var tree = new StaticTree();
        var jobs = Jobs(tree.Root);
        Assert.Equal(jobs.Count, jobs.Select(job => job.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var plc in new[] { "20", "21" }.SelectMany(key => ReleaseCommands.PlcHarnessModes(key).Select(mode => ReleaseCommands.PlcHarnessJob(key, mode))))
        {
            var job = Assert.Single(jobs.Where(job => job.Name == plc));
            Assert.Contains("Test-PlcHarness.py", job.Arguments[0]);
            Assert.Contains("--worker", job.Arguments);
            Assert.DoesNotContain("--engine-worker", job.Arguments);
            Assert.Equal(plc.Split('-')[1][1..], job.Release);
        }
        foreach (var group in new[] { "functional", "worker-isolation", "approval", "ecosystem", "contracts" })
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
        Assert.Equal(8, captures.Length);
        Assert.Equal(8, captures.Select(job => job.Evidence).Distinct(StringComparer.Ordinal).Count());
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
            _ => { Directory.CreateDirectory(job.Evidence); File.WriteAllText(Path.Combine(job.Evidence, "20.json"), compareFails ? "frozen\n" : "changed\n"); return new(0, "", ""); }, (_, args) =>
            {
                compared = true;
                Assert.Equal("compare", args[0]); Assert.Contains("20", args);
                if (compareFails) throw new ReleaseException("semantic difference");
            }));
        Assert.True(compared);
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
        Assert.Equal(5, stale.Count);
        Assert.All(stale, path => Assert.False(File.Exists(path) || Directory.Exists(path), path));
        var keep = tree.Write("logs/plc-fixtures/sdk/bin/20/Release/net48/TiaMcp.PlcWorker.20.exe", "fixture");
        var outside = tree.Write("elsewhere/result.json", "kept");
        ReleaseCommands.ResetPlcReleaseJobOutputs(new PlcReleaseJob("foreign", "plc-functional", "python",
            ["script.py", "--output", Path.GetDirectoryName(outside)!, "--worker", keep], outside), logs);
        Assert.True(File.Exists(keep)); Assert.True(File.Exists(outside));
    }

    [Fact]
    public void HarnessSuccessorsKeepTheHostAndWorkerIdentityGates()
    {
        using var tree = new StaticTree();
        var logs = Path.Combine(tree.Root, "logs");
        var worker = new PlcWorkerFixtureBuild("20", "sdk").Worker(Path.Combine(logs, "plc-fixtures"));
        Directory.CreateDirectory(Path.GetDirectoryName(worker)!); File.WriteAllText(worker, "sdk-worker");
        tree.Write("runtime/v20/TiaMcp.FoundationHost.exe", "host");
        string workerHash = FileHash.Sha256(worker), hostHash = FileHash.Sha256(Path.Combine(tree.Root, "runtime/v20/TiaMcp.FoundationHost.exe"));
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

    }
}
