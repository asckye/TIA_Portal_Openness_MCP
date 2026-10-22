using System.Text.Json;
using TiaMcp.ReleaseTool;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CheckAllRound5Tests
{
    [Fact]
    public void PlcHarnessJobsUseTheSdkFixtureAndNoEngineHarnessJobRemains()
    {
        var root = Repository.FindRoot();
        var jobs = File.ReadAllText(Path.Combine(root, "build-tools/release/CheckAll.cs"));
        Assert.Contains("var plcName = CheckAllPlcHarnessJob(key, mode);", jobs);
        Assert.Contains("scripts/checks/Test-PlcHarness.py", jobs);
        Assert.Contains("\"--worker\", CheckAllPlcFixture(key)", jobs);
        Assert.Contains("\"--api\", targetApi", jobs);
        Assert.Contains("ValidateCheckAllPlcHarnessJobs(jobs)", jobs);
        // P8-04l: the engine harness jobs (a private engine copy under the harness) are retired.
        Assert.DoesNotContain("copiedEngine", jobs);
        Assert.DoesNotContain("CheckAllHarness(Isolated(", jobs);
        var harness = File.ReadAllText(Path.Combine(root, "tests/Engine/TiaMcp.Engine.Harness/Program.cs"));
        Assert.True(harness.IndexOf("RunPlcHarness(args)", StringComparison.Ordinal)
            < harness.IndexOf("EngineSurface.CheckHostRetirement", StringComparison.Ordinal));
        var subject = File.ReadAllText(Path.Combine(root, "tests/Engine/TiaMcp.Engine.Harness/PlcHarnessChecks.cs"));
        Assert.DoesNotContain("EngineSurface", subject); Assert.DoesNotContain("TiaMcp.Engine.V", subject);
        Assert.Contains("TiaMcp.Adapter.", subject); Assert.Contains("TiaReleaseKey", subject);
    }

    [Fact]
    public void PlcScriptFixtureBuildsHaveIndependentSdkReceiptsForBothReleases()
    {
        var root = Repository.FindRoot();
        var units = ReleaseCommands.CheckAllPlcFixtureBuilds(Path.Combine(root, "sdk"));
        Assert.Equal(2, units.Length);
        foreach (var key in new[] { "20", "21" })
        {
            var unit = Assert.Single(units.Where(unit => unit.Properties.Contains("-p:TiaReleaseKey=" + key)));
            Assert.Equal("src/PlcWorker/TiaMcp.PlcWorker.csproj", unit.Project);
            Assert.Equal("plc-sdk-" + key, unit.Variant);
            Assert.Contains("-p:TiaMcpPlcWorkerSdkFixture=true", unit.Properties);
            Assert.Contains(unit.Properties, property => property.StartsWith("-p:SiemensEngineeringDirectory=", StringComparison.Ordinal) && Path.IsPathFullyQualified(property.Split('=', 2)[1]));
            var fixtureRoot = Assert.Single(unit.Properties.Where(property => property.StartsWith("-p:TiaMcpPlcWorkerFixtureRoot=", StringComparison.Ordinal))).Split('=', 2)[1];
            Assert.StartsWith(root + Path.DirectorySeparatorChar, fixtureRoot, StringComparison.OrdinalIgnoreCase);
            var worker = ReleaseCommands.CheckAllPlcFixture(key);
            Assert.Equal(PlcWorkerFixtures.Candidate(key, "SOURCE", "current").Worker(Path.GetDirectoryName(fixtureRoot)!), worker);
            Assert.DoesNotContain("src/Engine", worker.Replace('\\', '/'));
            Assert.False(unit.Variant.StartsWith("sdk-", StringComparison.Ordinal)); // Engine runtime projection must not claim the PLC fixture.
        }
    }

    [Fact]
    public void MergeRejectsRepeatedIdentityEvenWithDifferentExecutionIdsAndPassingTotals()
    {
        using var fixture = new OfflineFixtures();
        var one = Path.Combine(fixture.DirectoryPath, "one.trx"); var two = Path.Combine(fixture.DirectoryPath, "two.trx");
        DotnetSuiteSelfTests.SyntheticTrx(one, ["Passed"]); DotnetSuiteSelfTests.SyntheticTrx(two, ["Passed"]);
        var doc = System.Xml.Linq.XDocument.Load(two);
        doc.Descendants(doc.Root!.Name.Namespace + "UnitTestResult").Single().SetAttributeValue("executionId", Guid.NewGuid()); doc.Save(two);
        var result = SuiteSharding.Merge("duplicate", new System.Text.Json.Nodes.JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 }, [one, two], fixture.DirectoryPath);
        Assert.Equal(1, result.ExitCode); Assert.Contains("duplicate test identities", result.StandardError);
        Assert.Equal(2, (int)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, "duplicate.json")))!["passed"]!);
    }

    [Fact]
    public void DistinctTheoryIdsWithTheSameTruncatedDisplayNameKeepEveryAssertion()
    {
        using var fixture = new OfflineFixtures();
        var one = Path.Combine(fixture.DirectoryPath, "one.trx"); var two = Path.Combine(fixture.DirectoryPath, "two.trx");
        DotnetSuiteSelfTests.SyntheticTrx(one, ["Passed"]); DotnetSuiteSelfTests.SyntheticTrx(two, ["Passed"]);
        var doc = System.Xml.Linq.XDocument.Load(two); var ns = doc.Root!.Name.Namespace;
        var id = Guid.NewGuid().ToString(); doc.Descendants(ns + "UnitTest").Single().SetAttributeValue("id", id);
        var row = doc.Descendants(ns + "UnitTestResult").Single(); row.SetAttributeValue("testId", id); row.SetAttributeValue("executionId", Guid.NewGuid()); doc.Save(two);
        var suite = new System.Text.Json.Nodes.JsonObject { ["minimumPassed"] = 2, ["maximumSkipped"] = 0 };
        Assert.Equal(0, SuiteSharding.Merge("distinct", suite, [one, two], fixture.DirectoryPath).ExitCode);
        Assert.Equal(2, SuiteSharding.Identities([one, two]).Count);
        Assert.Throws<ReleaseException>(() => SuiteSharding.Compare([one], [two]));
    }

    [Fact]
    public void CleanHistoryRoundTripPreservesCasesClassesAndJobMaximums()
    {
        using var fixture = new OfflineFixtures();
        var original = Path.Combine(fixture.DirectoryPath, "original"); var snapshot = Path.Combine(fixture.DirectoryPath, "snapshot");
        Directory.CreateDirectory(Path.Combine(original, "nested"));
        foreach (var name in new[] { "suite.json", "suite.classes.json", "check-costs.json", "nested/history.jsonl" })
            File.WriteAllText(Path.Combine(original, name), name + "\n");
        ReleaseCommands.CopyCheckAllHistory(original, snapshot);
        Assert.Equal(File.ReadAllText(Path.Combine(original, "nested/history.jsonl")), File.ReadAllText(Path.Combine(snapshot, "nested/history.jsonl")));
        File.WriteAllText(Path.Combine(snapshot, "check-costs.json"), "updated maximum");
        ReleaseCommands.CopyCheckAllHistory(snapshot, original);
        Assert.Equal("updated maximum", File.ReadAllText(Path.Combine(original, "check-costs.json")));
        Assert.Equal(4, Directory.GetFiles(original, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void SensitiveMethodsLeaveUntimedPortalCasesInNormalShards()
    {
        using var fixture = new OfflineFixtures();
        const string cls = "PortedPortalAdmissionTests";
        var costs = new Dictionary<string, double> { ["T:" + cls + ".Port_dispatch_takes_the_shared_worker_lane_before_journaling(cancel: false)"] = 2,
            ["T:" + cls + ".Missing_portal_never_turns_an_unstarted_port_into_an_unknown_write(release: 21)"] = 120 };
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "portal.json"), JsonSerializer.Serialize(costs));
        var plan = SuiteCosts.Prepare("portal", Path.Combine(fixture.DirectoryPath, "results"), fixture.DirectoryPath, null, 8, true);
        Assert.Equal(122, plan.Costs.Sum()); Assert.Equal(2, plan.Costs[^1]);
        var assignments = File.ReadAllLines(plan.Path).ToDictionary(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0])), line => int.Parse(line.Split('\t')[1]));
        foreach (var key in costs.Keys)
            Assert.Equal(TiaMcp.Engine.Tests.Shared.TimeSensitiveTests.ContainsCost(key) ? plan.Costs.Length - 1 : assignments[key],
                TiaMcp.Engine.Tests.Shared.TestShard.Assign(cls, key, key, plan.Costs.Length, assignments, true));
    }

    [Fact]
    public void PortalLaneMethodHasItsOwnSensitivePartitionWithoutLosingOrDuplicatingCosts()
    {
        using var fixture = new OfflineFixtures();
        const string portal = "T:PortedPortalAdmissionTests.Port_dispatch_takes_the_shared_worker_lane_before_journaling(name: x, cancel: false)";
        const string process = "T:FoundationProcessLeaseTests.Job_object_killed_idle_worker_allows_takeover_with_client_evidence";
        const string ordinary = "T:PortedPortalAdmissionTests.Missing_portal_never_turns_an_unstarted_port_into_an_unknown_write(release: 21)";
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "sample.json"), JsonSerializer.Serialize(new Dictionary<string, double> {
            [portal] = 182, [process] = 3, [ordinary] = 120
        }));
        var plan = SuiteCosts.Prepare("sample", Path.Combine(fixture.DirectoryPath, "plan"), fixture.DirectoryPath, null, 8, true, 2);
        var original = SuiteCosts.Prepare("sample", Path.Combine(fixture.DirectoryPath, "original-plan"), fixture.DirectoryPath, null, 8, true);
        Assert.Equal(original.Costs.Length, plan.Costs.Length);
        Assert.Equal(305, plan.Costs.Sum()); Assert.Equal(3, plan.Costs[^2]); Assert.Equal(182, plan.Costs[^1]);
        var assignments = File.ReadAllLines(plan.Path).ToDictionary(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0])), line => int.Parse(line.Split('\t')[1]));
        Assert.Equal(plan.Costs.Length - 1, TiaMcp.Engine.Tests.Shared.TestShard.Assign("PortedPortalAdmissionTests", portal, portal, plan.Costs.Length, assignments, true, 2));
        Assert.Equal(plan.Costs.Length - 2, TiaMcp.Engine.Tests.Shared.TestShard.Assign("FoundationProcessLeaseTests", process, process, plan.Costs.Length, assignments, true, 2));
        Assert.InRange(TiaMcp.Engine.Tests.Shared.TestShard.Assign("PortedPortalAdmissionTests", ordinary, ordinary, plan.Costs.Length, assignments, true, 2), 0, plan.Costs.Length - 3);
    }

    [Fact]
    public void SensitiveExclusiveLaneOverlapsBothGeneralAndStability()
    {
        using var fixture = new OfflineFixtures(); using var barrier = new Barrier(3);
        CommandResult Run() { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return new(0, "", ""); }
        var rows = CheckScheduler.Run([new("general", Run, []), new CheckJob("sensitive", Run, [], Exclusive: true) { Lane = "sensitive" },
            new CheckJob("stability", Run, []) { Lane = "stability" }], 1, fixture.DirectoryPath,
            new Dictionary<string, int> { ["general"] = 1, ["sensitive"] = 1, ["stability"] = 1 });
        Assert.All(rows, row => Assert.Equal("passed", row.Status));
    }

    [Fact]
    public void JobDeadlineFailsByNameAndStopsOwnedParentAndOrphan()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OfflineFixtures();
        var child = Path.Combine(fixture.DirectoryPath, "child.txt"); var ready = Path.Combine(fixture.DirectoryPath, "ready.txt");
        var parent = Path.Combine(fixture.DirectoryPath, "parent.txt"); var entered = Path.Combine(fixture.DirectoryPath, "entered.txt");
        File.WriteAllLines(child, ["child", ready]); File.WriteAllLines(parent, ["waiting-parent", child, ready, entered]);
        var rows = CheckScheduler.Run([new CheckJob("owned-timeout", () => ProcessRunner.Run("dotnet", [typeof(CheckAffinity).Assembly.Location, "--check-probe", parent],
            fixture.DirectoryPath, new Dictionary<string, string?> { ["TIA_MCP_CHECK_PROBE"] = "1" }), []) { Timeout = TimeSpan.FromSeconds(10) }], 1, fixture.DirectoryPath);
        var row = Assert.Single(rows); Assert.Equal("failed", row.Status); Assert.Contains("owned-timeout", row.Error);
        Assert.True(File.Exists(entered));
        int pid = int.Parse(File.ReadAllText(ready)); Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        AssertTimeoutMember(fixture.DirectoryPath, pid);
        Assert.Equal(TimeSpan.FromSeconds(300), CheckScheduler.JobTimeout(new("short", () => new(0, "", ""), []), new Dictionary<string, double> { ["short"] = 20 }));
        Assert.Equal(TimeSpan.FromSeconds(600), CheckScheduler.JobTimeout(new("long", () => new(0, "", ""), []), new Dictionary<string, double> { ["long"] = 200 }));
    }

    [Fact]
    public void EmptyJobAccountingStillWaitsForObservedProcessHandles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OfflineFixtures();
        var probe = Path.Combine(fixture.DirectoryPath, "child.txt"); var ready = Path.Combine(fixture.DirectoryPath, "ready.txt");
        File.WriteAllLines(probe, ["child", ready]);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = fixture.DirectoryPath, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(CheckAffinity).Assembly.Location, "--check-probe", probe }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_CHECK_PROBE"] = "1";
        using var process = System.Diagnostics.Process.Start(start)!;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        using var job = CheckAffinity.Assign(process, unchecked((ulong)current.ProcessorAffinity.ToInt64()));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(10)));
            Assert.True(CheckAffinity.IsMember(job, process));
            using var accountingRead = new ManualResetEventSlim();
            // Reproduce the accounting/handle ordering deterministically: zero
            // active PIDs is reported while an observed process is unsignalled.
            var wait = Task.Run(() => CheckAffinity.WaitForJobExit(() => { accountingRead.Set(); return []; }, [process], System.Diagnostics.Stopwatch.StartNew()));
            Assert.True(accountingRead.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(wait.Wait(TimeSpan.FromMilliseconds(200)));
            CheckAffinity.StopJobProcess(job, process);
            Assert.True(wait.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(process.HasExited);
        }
        finally
        {
            CheckAffinity.StopAndWait(job, fixture.DirectoryPath, grace: false);
            // Keep negative-control cleanup independent of the wait under test.
            Assert.True(process.WaitForExit(10000));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MemberExitingBetweenLookupAndObservationIsAlreadyGone(bool inspectApplication)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OfflineFixtures();
        var probe = Path.Combine(fixture.DirectoryPath, "exit.txt"); var ready = Path.Combine(fixture.DirectoryPath, "ready.txt");
        File.WriteAllLines(probe, ["exit-after-input", ready]);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        { WorkingDirectory = fixture.DirectoryPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in new[] { typeof(CheckAffinity).Assembly.Location, "--check-probe", probe }) start.ArgumentList.Add(argument);
        start.Environment["TIA_MCP_CHECK_PROBE"] = "1";
        using var process = System.Diagnostics.Process.Start(start)!;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        using var job = CheckAffinity.Assign(process, unchecked((ulong)current.ProcessorAffinity.ToInt64()));
        bool observedExit = false;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(10)));
            Assert.True(CheckAffinity.IsMember(job, process));
            var denied = new System.ComponentModel.Win32Exception(5);
            Assert.Same(denied, Assert.Throws<System.ComponentModel.Win32Exception>(() => CheckAffinity.StopAndWait(job,
                fixture.DirectoryPath, exclude: inspectApplication ? current.Id : null, findProcess: _ => throw denied)));
            var unexpected = new IOException("Unexpected observation failure");
            Assert.Same(unexpected, Assert.Throws<IOException>(() => CheckAffinity.StopAndWait(job,
                fixture.DirectoryPath, exclude: inspectApplication ? current.Id : null, findProcess: _ => throw unexpected)));
            // Preserve the lookup/handle-use gap, but make the child exit exactly
            // in that gap instead of relying on scheduler timing to hit the race.
            CheckAffinity.StopAndWait(job, fixture.DirectoryPath, exclude: inspectApplication ? current.Id : null,
                findProcess: pid =>
                {
                    // The probe's console host is a job member too; only the probe exits in the gap.
                    if (pid != process.Id || observedExit) return System.Diagnostics.Process.GetProcessById(pid);
                    var member = System.Diagnostics.Process.GetProcessById(pid);
                    try
                    {
                        process.StandardInput.WriteLine("exit"); process.StandardInput.Flush();
                        Assert.True(process.WaitForExit(10000));
                        Assert.Throws<InvalidOperationException>(() => { _ = member.Handle; });
                        observedExit = true;
                        return member;
                    }
                    catch { member.Dispose(); throw; }
                });
            Assert.True(observedExit);
            Assert.True(process.HasExited);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(CheckAffinity.Snapshot(job));
        }
        finally
        {
            // This probe exits on stdin EOF; even negative-control cleanup needs
            // no termination and remains independent of StopAndWait's observation.
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10000));
        }
    }

    [Fact]
    public void PythonCheckHasAnOuterJobDeadlineAndItsOwnedProcessExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OfflineFixtures();
        var script = Path.Combine(fixture.DirectoryPath, "waiting.py"); var ready = Path.Combine(fixture.DirectoryPath, "python-ready.txt");
        File.WriteAllText(script, "import os, pathlib, sys, threading\npathlib.Path(sys.argv[1]).write_text(str(os.getpid()))\nthreading.Event().wait()\n");
        var rows = CheckScheduler.Run([new CheckJob("python-timeout", () => ProcessRunner.Run(Environment.GetEnvironmentVariable("PYTHON") ?? "python", [script, ready], fixture.DirectoryPath), [])
            { Timeout = TimeSpan.FromSeconds(10) }], 1, fixture.DirectoryPath);
        var row = Assert.Single(rows); Assert.Equal("failed", row.Status); Assert.Contains("python-timeout", row.Error);
        int pid = int.Parse(File.ReadAllText(ready)); Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        AssertTimeoutMember(fixture.DirectoryPath, pid);
    }

    private static void AssertTimeoutMember(string directory, int pid)
    {
        var evidence = Assert.Single(Directory.GetFiles(directory, "*.timeout.json", SearchOption.AllDirectories));
        using var doc = JsonDocument.Parse(File.ReadAllText(evidence));
        Assert.Equal("timeout", doc.RootElement.GetProperty("reason").GetString());
        var member = Assert.Single(doc.RootElement.GetProperty("processes").EnumerateArray().Where(row => row.GetProperty("Pid").GetInt32() == pid));
        Assert.True(member.GetProperty("Member").GetBoolean());
        Assert.NotEqual(default, member.GetProperty("StartTimeUtc").GetDateTime());
        Assert.False(string.IsNullOrWhiteSpace(member.GetProperty("ImagePath").GetString()));
    }

    [Fact]
    public void SystemProcessWithoutAWorktreeArgumentIsOwnedByItsJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OfflineFixtures();
        var executable = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"));
        var rows = CheckScheduler.Run([new CheckJob("system-process-timeout", () => ProcessRunner.Run(executable,
            ["-NoProfile", "-NonInteractive", "-Command", "[Threading.Thread]::Sleep(-1)"], CheckAffinity.OwnershipRoot), [])
            { Timeout = TimeSpan.FromSeconds(10) }], 1, fixture.DirectoryPath);
        Assert.Equal("failed", Assert.Single(rows).Status);
        Assert.Contains("system-process-timeout", rows[0].Error);
        using var doc = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "*.timeout.json", SearchOption.AllDirectories))));
        var member = Assert.Single(doc.RootElement.GetProperty("processes").EnumerateArray().Where(row =>
            string.Equals(executable, row.GetProperty("ImagePath").GetString(), StringComparison.OrdinalIgnoreCase)));
        int pid = member.GetProperty("Pid").GetInt32();
        AssertTimeoutMember(fixture.DirectoryPath, pid);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public void SharedPreflightRetainsEveryGateWithoutDuplicateSuiteJobs()
    {
        string[] names = ["repository", "repository-self-tests", "dead-references", "catalog-drift", "tool-counts-drift", "usage-drift", "migration-drift", "contract-verify",
            "response-verify", "suite-runner-self-tests", "approval-self-tests", "relocation-self-tests", "bundle", "release-documentation", "release-tool-gate"];
        var jobs = names.Select(name => new CheckJob(name, () => new(0, "", ""), [])).ToList();
        ReleaseCommands.AddCheckAllPreflightJobs(jobs);
        Assert.Equal(names.Order(), jobs[^1].Dependencies.Order()); Assert.Equal(16, jobs.Count);
        Assert.DoesNotContain(jobs, job => job.Name.StartsWith("preflight-", StringComparison.Ordinal));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.AddCheckAllPreflightJobs([]));
    }

    [Fact]
    public void ParallelMsbuildNodesKeepWorktreeOwnershipAndDrainAfterTheirParentExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        foreach (var name in new[] { "one", "two" })
        {
            var probe = Path.Combine(root, name + "-probe.txt");
            var command = "dotnet &quot;" + typeof(CheckAffinity).Assembly.Location + "&quot; --check-probe &quot;" + probe + "&quot;";
            File.WriteAllText(Path.Combine(root, name + ".proj"), "<Project><Target Name=\"Build\"><WriteLinesToFile File=\"" + probe + "\" Lines=\"observe-job;" +
                Path.Combine(root, name + ".txt") + ";$([System.Environment]::ProcessId)\" Overwrite=\"true\" /><Exec Command=\"" + command + "\" /></Target></Project>");
        }
        var graph = Path.Combine(root, "parallel.proj");
        File.WriteAllText(graph, "<Project><Target Name=\"Build\"><MSBuild Projects=\"one.proj;two.proj\" BuildInParallel=\"true\" /></Target></Project>");
        var rows = CheckScheduler.Run([new CheckJob("parallel-msbuild", () => ProcessRunner.Run("dotnet", ["msbuild", graph, "-m:2", "-nodeReuse:false", "-nologo"], root,
            new Dictionary<string, string?> { ["MSBUILDENABLEALLPROPERTYFUNCTIONS"] = "1", ["TIA_MCP_CHECK_PROBE"] = "1" }), [])
            { Timeout = TimeSpan.FromSeconds(30) }], 1, Path.Combine(root, "logs"));
        var row = Assert.Single(rows); Assert.True(row.Status == "passed", $"{row.Status}/{row.ExitCode}: {row.Error}; {File.ReadAllText(Path.Combine(root, "logs/parallel-msbuild.log"))}");
        var evidence = new[] { "one", "two" }.Select(name => JsonSerializer.Deserialize<CheckAffinity.JobProcess>(File.ReadAllText(Path.Combine(root, name + ".txt")))!).ToArray();
        Assert.Equal(2, evidence.Select(member => member.Pid).Distinct().Count());
        Assert.All(evidence, member => {
            Assert.True(member.Member); Assert.NotNull(member.StartTimeUtc); Assert.NotNull(member.ImagePath);
            Assert.DoesNotContain("check-sdk", member.ImagePath, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(member.Pid));
        });
    }

    [Fact]
    public void JobOwnershipRejectsNonMembersAndDisallowsBreakaway()
    {
        if (!OperatingSystem.IsWindows()) return;
        CheckAffinity.EnsureRunnerJob();
        using var current = System.Diagnostics.Process.GetCurrentProcess(); using var job = CheckAffinity.Create();
        Assert.False(CheckAffinity.IsMember(job, current));
        Assert.Throws<ReleaseException>(() => CheckAffinity.StopJobProcess(job, current));
        Assert.False(current.HasExited);
        var flags = CheckAffinity.LimitFlags(job);
        Assert.Equal(0u, flags & (0x800u | 0x1000u));
        Assert.NotEqual(0u, flags & 0x2000u);
    }

    [Fact]
    public void SensitiveCpuCapPreservesTheRunnersIoWorkers()
    {
        if (!OperatingSystem.IsWindows()) return;
        CheckAffinity.EnsureRunnerJob();
        using var fixture = new OfflineFixtures(); using var process = System.Diagnostics.Process.GetCurrentProcess();
        ulong available = unchecked((ulong)process.ProcessorAffinity.ToInt64());
        var mask = available & (~available + 1);
        var output = Path.Combine(fixture.DirectoryPath, "runtime.json"); var probe = Path.Combine(fixture.DirectoryPath, "runtime.txt");
        File.WriteAllLines(probe, ["runtime-policy", output]);
        using var scope = CheckAffinity.Enter(mask);
        var result = ProcessRunner.Run("dotnet", [typeof(CheckAffinity).Assembly.Location, "--check-probe", probe], fixture.DirectoryPath,
            new Dictionary<string, string?> { ["TIA_MCP_CHECK_PROBE"] = "1" }, timeoutMilliseconds: 30000, waitForDescendants: true);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(mask, doc.RootElement.GetProperty("mask").GetUInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("processors").GetInt32());
        Assert.True(doc.RootElement.GetProperty("workers").GetInt32() >= CheckAffinity.BaselineWorkers);
    }

    [Fact]
    public void OrdinaryInventoryGraphBuildsEveryRootAndContentReceipt()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        foreach (var name in new[] { "one", "two" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Content Include=\"content.json\" CopyToOutputDirectory=\"PreserveNewest\" /><BridgePayload Include=\"payload.bin\" /><FixtureFiles Include=\"fixture.bin\" /></ItemGroup><Target Name=\"Scratch\" AfterTargets=\"Build\"><MakeDir Directories=\"$(TargetDir)bridge;$(TargetDir)fixtures\" /><Copy SourceFiles=\"@(BridgePayload)\" DestinationFolder=\"$(TargetDir)bridge\" /><Copy SourceFiles=\"@(FixtureFiles)\" DestinationFolder=\"$(TargetDir)fixtures\" /><WriteLinesToFile File=\"$(TargetDir)scratch.tmp\" Lines=\"transient test data\" Overwrite=\"true\" /></Target></Project>");
            File.WriteAllText(Path.Combine(root, name, "content.json"), "{}");
            File.WriteAllText(Path.Combine(root, name, "payload.bin"), "bridge fixture");
            File.WriteAllText(Path.Combine(root, name, "fixture.bin"), "test fixture");
        }
        var directory = Path.Combine(root, "inventory");
        var result = CheckAllLayout.BuildInventories(root, [new("one/one.csproj", "", []), new("two/two.csproj", "", [])], "dotnet", Path.Combine(root, "offline.config"), root, directory, 1);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var receipts = Directory.GetFiles(directory, "*.tsv"); Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Contains(File.ReadAllLines(receipt), line => line.EndsWith("content.json", StringComparison.Ordinal)));
        Assert.All(receipts, receipt => {
            var baseline = File.ReadAllLines(receipt)[1];
            Assert.True(File.Exists(Path.Combine(baseline, "content.json")));
            Assert.False(File.Exists(Path.Combine(baseline, "scratch.tmp")));
            Assert.Equal("bridge fixture", File.ReadAllText(Path.Combine(baseline, "bridge/payload.bin")));
            Assert.Equal("test fixture", File.ReadAllText(Path.Combine(baseline, "fixtures/fixture.bin")));
        });
        var graph = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "ordinary.proj"));
        Assert.All(graph.Descendants("MSBuild"), node => Assert.Equal("true", (string?)node.Attribute("BuildInParallel")));
    }

    [Fact]
    public void OrdinaryPropertyGroupsConsumeTheirOwnRestoredConditionalReferences()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        foreach (var name in new[] { "one", "two" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        }
        Directory.CreateDirectory(Path.Combine(root, "entry"));
        Directory.CreateDirectory(Path.Combine(root, "middle"));
        File.WriteAllText(Path.Combine(root, "middle/middle.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../one/one.csproj\" Condition=\"'$(Flavor)' == 'one'\" /><ProjectReference Include=\"../two/two.csproj\" Condition=\"'$(Flavor)' == 'two'\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(root, "entry/entry.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../middle/middle.csproj\" /></ItemGroup></Project>");
        // Two projects can share project.assets.json while retaining separate
        // NuGet no-op receipts. The inventory must restore the actual variant.
        File.WriteAllText(Path.Combine(root, "entry/alternate.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../two/two.csproj\" /></ItemGroup></Project>");
        foreach (var project in new[] { "entry.csproj", "alternate.csproj" })
        {
            var restore = ProcessRunner.Run("dotnet", ["msbuild", Path.Combine(root, "entry", project), "-t:Restore", "-m:1", "-nodeReuse:false", "-p:Flavor=one", "-p:RestoreConfigFile=" + Path.Combine(root, "offline.config")], root);
            Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        }
        var directory = Path.Combine(root, "inventory");
        var units = new[] { new CheckBuild("entry/entry.csproj", "one", ["-p:Flavor=one"]), new CheckBuild("entry/entry.csproj", "two", ["-p:Flavor=two"]) };
        var result = CheckAllLayout.BuildInventories(root, units, "dotnet", Path.Combine(root, "offline.config"), root, directory, 1);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var receipts = Directory.GetFiles(directory, "*.tsv"); Assert.Equal(2, receipts.Length);
        Assert.Contains(receipts, receipt => File.ReadAllText(receipt).Contains("one.dll", StringComparison.Ordinal));
        Assert.Contains(receipts, receipt => File.ReadAllText(receipt).Contains("two.dll", StringComparison.Ordinal));
        Assert.All(receipts, receipt => {
            var contents = File.ReadAllText(receipt);
            Assert.NotEqual(contents.Contains("one.dll", StringComparison.Ordinal), contents.Contains("two.dll", StringComparison.Ordinal));
        });
        var targets = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "ordinary.proj")).Descendants("MSBuild").Select(node => (string?)node.Attribute("Targets")).ToArray();
        Assert.Equal(new[] { "Clean", "Restore", "CheckOrdinaryInventory", "Clean", "Restore", "CheckOrdinaryInventory" }, targets);
    }

    [Fact]
    public void OrdinaryRootsWithDifferentReferencePropertiesCannotWriteTheSameObjConcurrently()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        File.WriteAllText(Path.Combine(root, "offline.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        foreach (var name in new[] { "one", "two", "shared", "middle" }) Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(root, "shared/shared.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name=\"ObserveWriter\" BeforeTargets=\"PrepareForBuild\"><Error Condition=\"'$(BuildInParallel)' != 'false'\" Text=\"Shared ordinary obj writers must be ordered\" /><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/observed.txt\" Lines=\"$(ReferenceFlavor)\" Overwrite=\"false\" /></Target></Project>");
        File.WriteAllText(Path.Combine(root, "middle/middle.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../shared/shared.csproj\" AdditionalProperties=\"ReferenceFlavor=nested\" /></ItemGroup></Project>");
        foreach (var name in new[] { "one", "two" })
            File.WriteAllText(Path.Combine(root, name, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../shared/shared.csproj\" AdditionalProperties=\"ReferenceFlavor=" + name + "\" /></ItemGroup></Project>");
        // A single root also reaches the same obj with different properties.
        var one = System.Xml.Linq.XDocument.Load(Path.Combine(root, "one/one.csproj"));
        one.Root!.Add(new System.Xml.Linq.XElement("ItemGroup",
            new System.Xml.Linq.XElement("ProjectReference", new System.Xml.Linq.XAttribute("Include", "../middle/middle.csproj")),
            new System.Xml.Linq.XElement("ProjectReference", new System.Xml.Linq.XAttribute("Include", "../two/two.csproj"))));
        one.Save(Path.Combine(root, "one/one.csproj"));
        var directory = Path.Combine(root, "inventory");
        var result = CheckAllLayout.BuildInventories(root, [new("one/one.csproj", "", []), new("two/two.csproj", "", [])], "dotnet", Path.Combine(root, "offline.config"), root, directory, 1);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var graph = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "ordinary.proj"));
        var items = graph.Root!.Elements("ItemGroup").SelectMany(group => group.Elements()).GroupBy(item => item.Name.LocalName)
            .ToDictionary(group => "@(" + group.Key + ")", group => group.Count());
        var builds = graph.Descendants("MSBuild").Where(task => (string?)task.Attribute("Targets") == "CheckOrdinaryInventory").ToArray();
        Assert.Equal(2, builds.Length);
        // A parallel MSBuild task is allowed one root only: both roots reach
        // shared/obj under different node identities. With two roots in one
        // task this must fail even on a single-node sandbox used for validation.
        Assert.All(builds, task => Assert.Equal(1, items[(string)task.Attribute("Projects")!]));
        // References can diverge inside one root too (the production Foundation
        // root's fault fixture is such an edge). Keep their writers ordered.
        Assert.All(graph.Descendants("MSBuild"), task => Assert.Contains("BuildInParallel=false", (string?)task.Attribute("Properties")));
        Assert.Equal(new[] { "Clean", "Clean", "Restore", "Restore", "CheckOrdinaryInventory", "CheckOrdinaryInventory" },
            graph.Descendants("MSBuild").Select(task => (string)task.Attribute("Targets")!).ToArray());
        Assert.Equal(2, Directory.GetFiles(directory, "*.tsv").Length);
        var referencedRoot = Assert.Single(Directory.GetFiles(directory, "*.tsv").Select(File.ReadAllLines)
            .Where(lines => Path.GetFullPath(lines[0]).StartsWith(Path.Combine(root, "two"), StringComparison.OrdinalIgnoreCase)));
        Assert.True(File.Exists(Path.Combine(referencedRoot[1], "two.dll")));
        var observations = File.ReadAllLines(Path.Combine(root, "shared/observed.txt"));
        Assert.Contains("one", observations); Assert.Contains("two", observations); Assert.Contains("nested", observations);
    }

    [Fact]
    public void SensitiveWorkersHaveDisjointCpusAndDoNotSerializeEachOther()
    {
        using var fixture = new OfflineFixtures(); using var barrier = new Barrier(2);
        CommandResult Run() { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); return new(0, "", ""); }
        const ulong mask = 0xff;
        var cores = Enumerable.Range(0, 8).Select(bit => 1UL << bit).ToArray();
        var jobs = new List<CheckJob> {
            new CheckJob("one", Run, [], Exclusive: true) { Lane = "sensitive", EstimatedSeconds = 100 },
            new CheckJob("two", Run, [], Exclusive: true) { Lane = "sensitive", EstimatedSeconds = 90 }
        };
        var lanes = CheckAffinity.AssignSensitiveLanes(jobs, mask, 2, cores);
        Assert.Equal(2, lanes.Count);
        Assert.Equal(0UL, jobs[0].Affinity!.Value & jobs[1].Affinity!.Value);
        Assert.Equal(mask, jobs[0].Affinity!.Value | jobs[1].Affinity!.Value);
        Assert.All(jobs, job => Assert.Equal(4, System.Numerics.BitOperations.PopCount(job.Affinity!.Value)));
        Assert.All(CheckScheduler.Run(jobs, 1, fixture.DirectoryPath, lanes), row => Assert.Equal("passed", row.Status));
    }

    [Fact]
    public void ExplicitGeneralProcessLimitSurvivesALargerCpuReservation()
    {
        Assert.Equal(12, FastSuites.GeneralCapacity(12, 8, true));
        Assert.Equal(8, FastSuites.GeneralCapacity(12, 8, false));
        Assert.Equal(4, FastSuites.GeneralCapacity(4, 8, true));
    }

    [Fact]
    public void StandaloneSuitesDoNotReserveCoresForAnUnusedSensitiveWorker()
    {
        CommandResult Run() => new(0, "", "");
        var jobs = new List<CheckJob> { new("general", Run, []) };
        Assert.Equal(0, FastSuites.SensitiveCoreBudget(0xffffff, jobs));
        jobs.Add(new CheckJob("one", Run, [], Exclusive: true) { SensitiveGroup = "one.dll" });
        jobs.Add(new CheckJob("one-filter", Run, [], Exclusive: true) { SensitiveGroup = "one.dll" });
        Assert.Equal(4, FastSuites.SensitiveCoreBudget(0xffffff, jobs));
        jobs.Add(new CheckJob("two", Run, [], Exclusive: true) { SensitiveGroup = "two.dll" });
        Assert.Equal(8, FastSuites.SensitiveCoreBudget(0xffffff, jobs));
    }

    [Fact]
    public void SensitiveSuitesSharingAnAssemblyStayOnOneWorker()
    {
        const ulong mask = 0xff;
        var cores = Enumerable.Range(0, 8).Select(bit => 1UL << bit).ToArray();
        var jobs = new List<CheckJob> {
            new CheckJob("foundation", () => new(0, "", ""), [], Exclusive: true) { Lane = "sensitive", SensitiveGroup = "foundation.dll", EstimatedSeconds = 100 },
            new CheckJob("foundation-api", () => new(0, "", ""), [], Exclusive: true) { Lane = "sensitive", SensitiveGroup = "foundation.dll", EstimatedSeconds = 90 },
            new CheckJob("studio", () => new(0, "", ""), [], Exclusive: true) { Lane = "sensitive", SensitiveGroup = "studio.dll", EstimatedSeconds = 80 }
        };
        CheckAffinity.AssignSensitiveLanes(jobs, mask, 2, cores);
        Assert.Equal(jobs[0].Lane, jobs[1].Lane);
        Assert.Equal(jobs[0].Affinity, jobs[1].Affinity);
        Assert.NotEqual(jobs[0].Lane, jobs[2].Lane);
        Assert.Equal(0UL, jobs[0].Affinity!.Value & jobs[2].Affinity!.Value);
    }

    [Fact]
    public void SequentialEquivalenceUsesTheWholeMeasuredSuiteCostForItsJobDeadline()
    {
        using var fixture = new OfflineFixtures();
        var history = Path.Combine(fixture.DirectoryPath, "bin-build/check-timings");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "foundation.json"), JsonSerializer.Serialize(new Dictionary<string, double> {
            ["T:FoundationProcessLeaseTests.Job_object_killed_idle_worker_allows_takeover_with_client_evidence"] = 120,
            ["T:OrdinaryTests.ReadOnly"] = 40
        }));
        var suite = new System.Text.Json.Nodes.JsonObject {
            ["project"] = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj",
            ["arguments"] = new System.Text.Json.Nodes.JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0
        };
        var jobs = new List<CheckJob>();
        FastSuites.AddJobs(jobs, fixture.DirectoryPath, "foundation", suite, Path.Combine(fixture.DirectoryPath, "results"), "dotnet", 8, null, [], prove: true);
        var sequential = Assert.Single(jobs.Where(job => job.Name == "foundation-sequential"));
        Assert.Equal(165, sequential.EstimatedSeconds);
        Assert.Equal(TimeSpan.FromSeconds(495), CheckScheduler.JobTimeout(sequential, new Dictionary<string, double>()));
        Assert.Equal(TimeSpan.FromSeconds(1500), CheckScheduler.JobTimeout(sequential, new Dictionary<string, double> { [sequential.Name] = 500 }));
    }

    [Fact]
    public void ShardsWithoutTimingHistoryGetTheColdAllowanceInsteadOfTheFloor()
    {
        using var fixture = new OfflineFixtures();
        var suite = new System.Text.Json.Nodes.JsonObject {
            ["project"] = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj",
            ["arguments"] = new System.Text.Json.Nodes.JsonArray(), ["minimumPassed"] = 1, ["maximumSkipped"] = 0
        };
        var jobs = new List<CheckJob>();
        FastSuites.AddJobs(jobs, fixture.DirectoryPath, "foundation", suite, Path.Combine(fixture.DirectoryPath, "results"), "dotnet", 4, null, [],
            history: Path.Combine(fixture.DirectoryPath, "bin-build/empty-timings"));
        var shards = jobs.Where(job => job.Name.StartsWith("foundation-shard-", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(shards);
        Assert.All(shards, shard => Assert.Equal(TimeSpan.FromSeconds(3 * FastSuites.ColdShardSeconds), CheckScheduler.JobTimeout(shard, new Dictionary<string, double>())));

        // A stopped run records only part of a suite: fewer measured cases than the registered minimum stays cold.
        var partial = Path.Combine(fixture.DirectoryPath, "bin-build/partial-timings");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "foundation.json"), JsonSerializer.Serialize(new Dictionary<string, double> { ["T:OrdinaryTests.ReadOnly"] = 40 }));
        suite["minimumPassed"] = 100;
        var partialJobs = new List<CheckJob>();
        FastSuites.AddJobs(partialJobs, fixture.DirectoryPath, "foundation", suite, Path.Combine(fixture.DirectoryPath, "partial-results"), "dotnet", 4, null, [], history: partial);
        var partialShards = partialJobs.Where(job => job.Name.StartsWith("foundation-shard-", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(partialShards);
        Assert.All(partialShards, shard => Assert.Equal(FastSuites.ColdShardSeconds, shard.EstimatedSeconds));
    }

    [Fact]
    public void FoundationFixturesHaveRuntimeReceiptsBeforeAnyOrdinaryBuild()
    {
        using var fixture = new OfflineFixtures();
        const string foundation = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj";
        var units = CheckAllBuild.WithTestFixtures([new(foundation, "", [])]).ToArray();
        var repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(repository, foundation));
        Assert.Equal("false", (string?)document.Descendants("ShouldUnsetParentConfigurationAndPlatform").Single());
        var references = document.Descendants("ProjectReference")
            .Where(reference => (string?)reference.Attribute("ReferenceOutputAssembly") == "false").ToArray();
        Assert.Equal(3, references.Length);
        var receipts = new List<string>();
        var expected = new List<(string Path, string Content)>();
        foreach (var reference in references)
        {
            var project = Path.GetRelativePath(repository, Path.GetFullPath(Path.Combine(repository, Path.GetDirectoryName(foundation)!, (string)reference.Attribute("Include")!))).Replace('\\', '/');
            var unit = Assert.Single(units.Where(unit => unit.Project == project));
            var properties = ((string?)reference.Attribute("AdditionalProperties") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(property => "-p:" + property);
            Assert.Equal(properties, unit.Properties);
            var source = Path.Combine(fixture.DirectoryPath, "isolated", Path.GetFileNameWithoutExtension(project));
            var destination = Path.Combine(fixture.DirectoryPath, Path.GetDirectoryName(project)!, "bin/Release/fixture");
            Directory.CreateDirectory(source);
            var executable = Path.GetFileNameWithoutExtension(project) + ".exe";
            File.WriteAllText(Path.Combine(source, executable), project);
            File.WriteAllText(Path.Combine(source, "dependency.dll"), "dependency");
            receipts.Add(string.Join('\t', Path.Combine(fixture.DirectoryPath, project), unit.Variant, source, destination, Path.Combine(source, executable)));
            expected.Add((Path.Combine(destination, executable), project));
            expected.Add((Path.Combine(destination, "dependency.dll"), "dependency"));
        }
        Assert.NotEmpty(expected);
        Assert.All(expected, file => Assert.False(File.Exists(file.Path)));
        var manifest = Path.Combine(fixture.DirectoryPath, "runtime-paths.tsv");
        File.WriteAllLines(manifest, receipts);
        ReleaseCommands.PrepareCheckAllRuntime(fixture.DirectoryPath, units, manifest);
        Assert.All(expected, file => Assert.Equal(file.Content, File.ReadAllText(file.Path)));
    }

    [Fact]
    public void OrdinaryInventoryCacheRoundTripsBoundedlyAndStillChecksMissingRuntimeFiles()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var source = Path.Combine(root, "original/bin-build/check-layout"); var snapshot = Path.Combine(root, "snapshot/bin-build/check-layout");
        Directory.CreateDirectory(source);
        for (int i = 0; i < 260; i++)
        {
            var path = Path.Combine(source, i.ToString("x64") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { "one.dll", "two.dll" }));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 300));
        }
        CheckAllLayout.CopyInventoryCache(root, source, snapshot);
        Assert.Equal(256, Directory.GetFiles(source).Length); Assert.Equal(256, Directory.GetFiles(snapshot).Length);
        var selected = Path.Combine(snapshot, 259.ToString("x64") + ".json");
        Assert.Equal(new[] { "one.dll", "two.dll" }, CheckAllLayout.ReadInventory(selected));
        var ordinary = Path.Combine(root, "ordinary"); var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(ordinary); Directory.CreateDirectory(actual);
        foreach (var file in CheckAllLayout.ReadInventory(selected))
        { File.WriteAllText(Path.Combine(ordinary, file), "payload"); File.WriteAllText(Path.Combine(actual, file), "payload"); }
        Assert.Empty(CheckAllLayout.Missing(ordinary, actual, CheckAllLayout.ReadInventory(selected)));
        File.Delete(Path.Combine(actual, "two.dll"));
        Assert.Equal(new[] { "two.dll" }, CheckAllLayout.Missing(ordinary, actual, CheckAllLayout.ReadInventory(selected)));
        CheckAllLayout.CopyInventoryCache(root, snapshot, source);
        Assert.Equal(256, Directory.GetFiles(source).Length);
        File.WriteAllText(selected, "[]"); Assert.Throws<ReleaseException>(() => CheckAllLayout.ReadInventory(selected));
        File.WriteAllText(selected, "[\"../outside.dll\"]"); Assert.Throws<ReleaseException>(() => CheckAllLayout.ReadInventory(selected));
        Assert.Throws<ReleaseException>(() => CheckAllLayout.CopyInventoryCache(root, source, root + "-outside"));
    }

    [Fact]
    public void SnapshotRetentionKeepsUncompletedDirectoriesAndRejectsEscapingPaths()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var parent = Path.Combine(root, "bin-build/c");
        for (int i = 0; i < 5; i++)
        {
            var directory = Path.Combine(parent, i.ToString(), "bin-build"); Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, "check-all-snapshot.json");
            File.WriteAllText(marker, JsonSerializer.Serialize(new { original = root })); File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(i - 10));
        }
        Directory.CreateDirectory(Path.Combine(parent, "active")); ReleaseCommands.PruneCheckAllSnapshots(root, 3);
        Assert.Equal(new[] { "2", "3", "4", "active" }, Directory.GetDirectories(parent).Select(Path.GetFileName).Order());
        Assert.Throws<ReleaseException>(() => ReleaseCommands.RequireCheckAllOwnedPath(root, root + "-outside"));
    }

    [Fact]
    public void SnapshotOwnerMarkerRecordsOriginalAndCurrentProcessIdentity()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var snapshot = ReleaseCommands.CheckAllSnapshotPath(root, "12345678");
        var before = DateTime.UtcNow;
        ReleaseCommands.WriteCheckAllSnapshotOwner(root, snapshot);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(snapshot, "bin-build/check-all-snapshot.owner.json")));
        var owner = document.RootElement;
        Assert.Equal(root, owner.GetProperty("original").GetString());
        Assert.Equal(process.Id, owner.GetProperty("processId").GetInt32());
        Assert.Equal(process.StartTime.ToUniversalTime(), owner.GetProperty("processStartUtc").GetDateTime());
        Assert.Equal(DateTimeKind.Utc, owner.GetProperty("processStartUtc").GetDateTime().Kind);
        Assert.Equal(DateTimeKind.Utc, owner.GetProperty("startedUtc").GetDateTime().Kind);
        Assert.InRange(owner.GetProperty("startedUtc").GetDateTime(), before, DateTime.UtcNow);
        Assert.False(File.Exists(Path.Combine(snapshot, "bin-build/check-all-snapshot.json")));
    }

    [Fact]
    public void SnapshotRetentionDeletesUncompletedSnapshotsWhoseOwnersAreGone()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var snapshot = SnapshotWithOwner(root, "gone", 42, DateTime.UtcNow.AddDays(-2));
        var lookups = new List<int>();
        ReleaseCommands.PruneCheckAllSnapshots(root, 3, findProcessStartUtc: processId => { lookups.Add(processId); return null; });
        Assert.Equal(new[] { 42 }, lookups); Assert.False(Directory.Exists(snapshot));
    }

    [Fact]
    public void SnapshotRetentionKeepsUncompletedSnapshotsOwnedByTheCurrentProcess()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var snapshot = SnapshotWithOwner(root, "alive", process.Id, process.StartTime.ToUniversalTime());
        SetSnapshotWriteTimes(snapshot, DateTime.UtcNow.AddDays(-2));
        ReleaseCommands.PruneCheckAllSnapshots(root, 3);
        Assert.True(Directory.Exists(snapshot));
    }

    [Fact]
    public void SnapshotRetentionDeletesUncompletedSnapshotsWhenProcessIdsAreReused()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var start = DateTime.UtcNow.AddDays(-2);
        var snapshot = SnapshotWithOwner(root, "reused", 42, start);
        var lookups = new List<int>();
        ReleaseCommands.PruneCheckAllSnapshots(root, 3, findProcessStartUtc: processId => { lookups.Add(processId); return start.AddTicks(1); });
        Assert.Equal(new[] { 42 }, lookups); Assert.False(Directory.Exists(snapshot));
    }

    [Fact]
    public void SnapshotRetentionKeepsFreshUnmarkedSnapshots()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var snapshot = Path.Combine(root, "bin-build/c/fresh"); Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "payload"), "fixture");
        SetSnapshotWriteTimes(snapshot, DateTime.UtcNow.AddHours(-1));
        ReleaseCommands.PruneCheckAllSnapshots(root, 3);
        Assert.True(Directory.Exists(snapshot));
    }

    [Fact]
    public void SnapshotRetentionDeletesUnmarkedSnapshotsOlderThanTwentyFourHours()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var snapshot = Path.Combine(root, "bin-build/c/legacy");
        var nested = Path.Combine(snapshot, "nested"); Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "payload"), "fixture");
        SetSnapshotWriteTimes(snapshot, DateTime.UtcNow.AddHours(-25));
        ReleaseCommands.PruneCheckAllSnapshots(root, 3);
        Assert.False(Directory.Exists(snapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotRetentionKeepsUnmarkedSnapshotsWithRecentDescendants(bool recentDirectory)
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var snapshot = Path.Combine(root, "bin-build/c/legacy");
        var nested = Path.Combine(snapshot, "nested"); Directory.CreateDirectory(nested);
        var payload = Path.Combine(nested, "payload"); File.WriteAllText(payload, "fixture");
        SetSnapshotWriteTimes(snapshot, DateTime.UtcNow.AddDays(-2));
        if (recentDirectory) Directory.SetLastWriteTimeUtc(nested, DateTime.UtcNow);
        else File.SetLastWriteTimeUtc(payload, DateTime.UtcNow);
        Assert.True(Directory.GetLastWriteTimeUtc(snapshot) < DateTime.UtcNow.AddHours(-24));
        ReleaseCommands.PruneCheckAllSnapshots(root, 3);
        Assert.True(Directory.Exists(snapshot)); Assert.True(File.Exists(payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotRetentionKeepsSnapshotsOwnedByAnotherOriginal(bool completed)
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var snapshot = SnapshotWithOwner(root, "other", 42, DateTime.UtcNow.AddDays(-2), root + "-other");
        if (completed) File.WriteAllText(Path.Combine(snapshot, "bin-build/check-all-snapshot.json"), JsonSerializer.Serialize(new { original = root + "-other" }));
        SetSnapshotWriteTimes(snapshot, DateTime.UtcNow.AddDays(-2));
        var lookups = new List<int>();
        ReleaseCommands.PruneCheckAllSnapshots(root, 3, findProcessStartUtc: processId => { lookups.Add(processId); return null; });
        Assert.Empty(lookups); Assert.True(Directory.Exists(snapshot));
    }

    [Fact]
    public void SnapshotRetentionUsesCompletionMarkerTimesForOwnedSnapshots()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var now = DateTime.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            var snapshot = SnapshotWithOwner(root, i.ToString(), 42, now.AddDays(-2));
            SetSnapshotWriteTimes(snapshot, now.AddMinutes(-i));
            var marker = Path.Combine(snapshot, "bin-build/check-all-snapshot.json");
            File.WriteAllText(marker, JsonSerializer.Serialize(new { original = root, completedUtc = now.AddMinutes(-i) }));
            File.SetLastWriteTimeUtc(marker, now.AddMinutes(i - 10));
        }
        var other = SnapshotWithOwner(root, "other", 42, now.AddDays(-2), root + "-other");
        File.WriteAllText(Path.Combine(other, "bin-build/check-all-snapshot.json"), JsonSerializer.Serialize(new { original = root + "-other" }));
        var lookups = new List<int>();
        ReleaseCommands.PruneCheckAllSnapshots(root, 3, Path.Combine(root, "bin-build/c/4"), processId => { lookups.Add(processId); return null; });
        Assert.Empty(lookups);
        Assert.Equal(new[] { "2", "3", "4", "other" }, Directory.GetDirectories(Path.Combine(root, "bin-build/c")).Select(Path.GetFileName).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotRetentionNeverDeletesTheCurrentSnapshot(bool completed)
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var current = SnapshotWithOwner(root, "current", 42, DateTime.UtcNow.AddDays(-2));
        SetSnapshotWriteTimes(current, DateTime.UtcNow.AddDays(-2));
        if (completed)
        {
            var marker = Path.Combine(current, "bin-build/check-all-snapshot.json");
            File.WriteAllText(marker, JsonSerializer.Serialize(new { original = root })); File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-2));
        }
        for (int i = 0; i < 3; i++)
        {
            var snapshot = SnapshotWithOwner(root, i.ToString(), 42, DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(Path.Combine(snapshot, "bin-build/check-all-snapshot.json"), JsonSerializer.Serialize(new { original = root }));
        }
        ReleaseCommands.PruneCheckAllSnapshots(root, 3, current, _ => null);
        Assert.True(Directory.Exists(current)); Assert.Equal(4, Directory.GetDirectories(Path.Combine(root, "bin-build/c")).Length);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("completion")]
    [InlineData("lookup")]
    public void SnapshotRetentionContinuesPastInvalidMarkersAndProcessLookupFailures(string failure)
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var invalid = SnapshotWithOwner(root, "invalid", 41, DateTime.UtcNow.AddDays(-2));
        if (failure != "lookup") File.WriteAllText(Path.Combine(invalid, "bin-build/check-all-snapshot" + (failure == "owner" ? ".owner" : "") + ".json"), "{invalid");
        var stale = SnapshotWithOwner(root, "stale", 42, DateTime.UtcNow.AddDays(-2));
        ReleaseCommands.PruneCheckAllSnapshots(root, 3, findProcessStartUtc: processId => processId == 41 ? throw new InvalidOperationException("Process start time unavailable") : null);
        Assert.True(Directory.Exists(invalid)); Assert.False(Directory.Exists(stale));
    }

    private static string SnapshotWithOwner(string root, string name, int processId, DateTime processStartUtc, string? original = null)
    {
        var snapshot = Path.Combine(root, "bin-build/c", name);
        var directory = Path.Combine(snapshot, "bin-build"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "check-all-snapshot.owner.json"), JsonSerializer.Serialize(new { original = original ?? root,
            processId, processStartUtc, startedUtc = DateTime.UtcNow.AddDays(-2) }));
        return snapshot;
    }

    private static void SetSnapshotWriteTimes(string snapshot, DateTime time)
    {
        foreach (var file in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, time);
        foreach (var directory in Directory.EnumerateDirectories(snapshot, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(directory, time);
        Directory.SetLastWriteTimeUtc(snapshot, time);
    }

    [Fact]
    public void StaleCacheRetentionRemovesOldEntriesAndKeepsRecentHits()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var payload = Path.Combine(root, "output"); Directory.CreateDirectory(payload); File.WriteAllText(Path.Combine(payload, "payload.dll"), "fixture");
        var directory = Path.Combine(root, "cache");
        var old = BuildOutputCache.Key([], ["old"]); var recent = BuildOutputCache.Key([], ["recent"]);
        new BuildOutputCache(directory, "unit", clock: () => DateTime.UtcNow.AddDays(-8)).Populate(old, payload);
        new BuildOutputCache(directory, "unit").Populate(recent, payload);
        BuildOutputCache.PruneRetained(directory, TimeSpan.FromDays(7), BuildOutputCache.DefaultMaxBytes);
        var cache = new BuildOutputCache(directory, "unit"); Assert.False(cache.Restore(old, payload)); Assert.True(cache.Restore(recent, payload));
    }

    [Fact]
    public void ProducerTelemetryPreservesEveryRowIncludingFailureAndSkip()
    {
        using var fixture = new OfflineFixtures();
        var path = Path.Combine(fixture.DirectoryPath, "producer.tsv");
        var previous = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PRODUCER_TIMINGS");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_PRODUCER_TIMINGS", path);
            var rows = TiaMcp.Engine.Tests.Shared.CheckSuite.Run("producer", (check, skip) => {
                check(true, "passed"); check(false, "failed"); skip("skipped", "reason");
            }).Select(row => (TiaMcp.Engine.Tests.Shared.CheckRow)row[0]).ToArray();
            Assert.Equal(new[] { "passed", "failed", "skipped" }, rows.Select(row => row.Message));
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => row.Number));
            Assert.True(rows[0].Passed); Assert.False(rows[1].Passed); Assert.Equal("reason", rows[2].SkipReason);
            var fields = Assert.Single(File.ReadAllLines(path)).Split('\t');
            Assert.Equal("producer", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])));
            Assert.True(double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture) >= 0); Assert.Equal("3", fields[2]);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_TEST_PRODUCER_TIMINGS", previous); }
    }

    [Fact]
    public void CleanCheckoutKeepsRootGrowthWithinTwentyFourCharacters()
    {
        using var fixture = new OfflineFixtures();
        var normal = fixture.DirectoryPath;
        var clean = ReleaseCommands.CheckAllSnapshotPath(normal, "12345678");
        Assert.True(clean.Length - normal.Length <= ReleaseCommands.CheckAllCleanRootMargin);
        ReleaseCommands.RequireCheckAllCleanRootHeadroom(normal, normal + new string('x', 24));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.RequireCheckAllCleanRootHeadroom(normal, normal + new string('x', 25)));
    }

    [Fact]
    public void FileAppCacheInvalidatesOnActualSourceImportAndSdkChanges()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        void Write(string file, string value) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        Write("build-tools/common/fixture.cs", "one"); Write("scripts/checks/sample.cs", "sample");
        Write("Version.props", "version"); Write("offline.config", "offline");
        string Key(string sdk = "10.0.401") => ReleaseCommands.CheckAllScriptInputKey(root, "sample", Path.Combine(root, "offline.config"), sdk);
        var original = Key(); Write("build-tools/common/fixture.cs", "two"); Assert.NotEqual(original, Key());
        Write("build-tools/common/fixture.cs", "one"); Assert.Equal(original, Key());
        Write("Directory.Build.props", "import"); Assert.NotEqual(original, Key());
        File.Delete(Path.Combine(root, "Directory.Build.props")); Assert.Equal(original, Key());
        Write("build-tools/common/extra.cs", "new"); Assert.NotEqual(original, Key());
        File.Delete(Path.Combine(root, "build-tools/common/extra.cs")); Assert.Equal(original, Key());
        Assert.NotEqual(original, Key("10.0.402"));
    }

    [Fact]
    public void CurrentCasePlanOverridesObsoleteWholeShardHistory()
    {
        var history = new Dictionary<string, double> { ["suite-shard-0"] = 351, ["capture"] = 173 };
        Assert.Equal(60, ReleaseCommands.CheckAllJobCost(new CheckJob("suite-shard-0", () => new(0, "", ""), []) { EstimatedSeconds = 60 }, history));
        Assert.Equal(173, ReleaseCommands.CheckAllJobCost(new("capture", () => new(0, "", ""), []), history));
    }

    [Fact]
    public void IdleReservedLaneGetsItsInputsBeforeTheGeneralBacklog()
    {
        using var fixture = new OfflineFixtures(); var order = new List<string>();
        CommandResult Run(string name) { lock (order) order.Add(name); return new(0, "", ""); }
        var rows = CheckScheduler.Run([
            new CheckJob("long-suite", () => Run("long-suite"), []) { EstimatedSeconds = 300 },
            new CheckJob("production-catalog", () => Run("production-catalog"), []) { EstimatedSeconds = 2 },
            new CheckJob("stability", () => Run("stability"), ["production-catalog"]) { Lane = "stability", EstimatedSeconds = 40 }
        ], 1, fixture.DirectoryPath, new Dictionary<string, int> { ["general"] = 1, ["stability"] = 1 });
        Assert.All(rows, row => Assert.Equal("passed", row.Status));
        Assert.Equal("production-catalog", order[0]);
    }

    [Fact]
    public void DirectVstestPreservesEveryRegisteredFilterAndExactVariant()
    {
        var root = Repository.FindRoot();
        var catalog = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/test-suites.json")))!.AsObject();
        foreach (var (name, value) in catalog)
        {
            var source = value!["arguments"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            var actual = FastSuites.VstestArguments("exact-variant.dll", source, name, "results");
            Assert.Equal("vstest", actual[0]); Assert.Equal("exact-variant.dll", actual[1]);
            var index = Array.IndexOf(source, "--filter");
            if (index >= 0) Assert.Contains("--TestCaseFilter:" + source[index + 1], actual);
            else Assert.DoesNotContain(actual, argument => argument.StartsWith("--TestCaseFilter:", StringComparison.Ordinal));
        }
        Assert.Throws<ReleaseException>(() => FastSuites.VstestArguments("suite.dll", ["--unknown"], "suite", "results"));
    }

    [Fact]
    public void OutputInventoryRejectsMissingContentInEveryVariant()
    {
        using var fixture = new OfflineFixtures();
        var normal = Path.Combine(fixture.DirectoryPath, "normal"); Directory.CreateDirectory(normal);
        File.WriteAllText(Path.Combine(normal, "suite.dll"), "assembly");
        Directory.CreateDirectory(Path.Combine(normal, "manifest"));
        File.WriteAllText(Path.Combine(normal, "manifest/package-manifest.json"), "{}");
        foreach (var variant in new[] { "default", "sdk-20", "sdk-21", "shared-false", "shared-true" })
        {
            var isolated = Path.Combine(fixture.DirectoryPath, variant); Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "suite.dll"), "assembly");
            Assert.Equal(new[] { "manifest/package-manifest.json" }, CheckAllLayout.Missing(normal, isolated, ["suite.dll", "manifest/package-manifest.json"]));
            Directory.CreateDirectory(Path.Combine(isolated, "manifest"));
            File.Copy(Path.Combine(normal, "manifest/package-manifest.json"), Path.Combine(isolated, "manifest/package-manifest.json"));
            Assert.Empty(CheckAllLayout.Missing(normal, isolated, ["suite.dll", "manifest/package-manifest.json"]));
        }
    }

    [Fact]
    public void LayoutInventoryCacheInvalidatesOnInputAdditionEditRemovalAndProperties()
    {
        var inputs = new Dictionary<string, string> { ["entry.cs"] = "one", ["content.json"] = "two" };
        var key = CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]);
        Assert.Equal(key, CheckAllLayout.InputKey(inputs.Reverse(), ["DefineConstants=ONE"]));
        using var fixture = new OfflineFixtures();
        var source = Path.Combine(fixture.DirectoryPath, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "fixture.dll"), "payload");
        var cache = new BuildOutputCache(Path.Combine(fixture.DirectoryPath, "cache"), "input-key");
        cache.Populate(key, source);
        Assert.True(cache.Restore(key, Path.Combine(fixture.DirectoryPath, "restored")));
        inputs["content.json"] = "changed";
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]));
        inputs["content.json"] = "two"; inputs["new.props"] = "import";
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]));
        inputs.Remove("new.props"); inputs.Remove("content.json");
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=ONE"]));
        inputs["content.json"] = "two";
        Assert.NotEqual(key, CheckAllLayout.InputKey(inputs, ["DefineConstants=TWO"]));
    }

    [Fact]
    public void TrxClassHistorySplitsNewExpensiveClassesAndRetainsLateProducerCost()
    {
        using var fixture = new OfflineFixtures(); var root = fixture.DirectoryPath;
        var trx = Path.Combine(root, "suite.trx");
        File.WriteAllText(trx, """
            <TestRun><TestDefinitions>
            <UnitTest id="large"><TestMethod className="NewLargeClass" name="Check" /></UnitTest>
            <UnitTest id="small"><TestMethod className="SmallClass" name="Check" /></UnitTest>
            <UnitTest id="late"><TestMethod className="Legacy" name="Rows" /></UnitTest>
            </TestDefinitions><Results>
            <UnitTestResult testId="large" testName="NewLargeClass.Check(case: 1)" duration="00:00:40" />
            <UnitTestResult testId="large" testName="NewLargeClass.Check(case: 2)" duration="00:00:40" />
            <UnitTestResult testId="small" testName="SmallClass.Check" duration="00:00:05" />
            <UnitTestResult testId="late" testName="Legacy.Rows(row: one)" duration="00:00:00.000001" />
            </Results></TestRun>
            """);
        var costs = new Dictionary<string, double> { ["M:Legacy.Rows"] = 70 };
        SuiteCosts.ImportTrx([trx], costs, Path.Combine(root, "sample.classes.json"));
        File.WriteAllText(Path.Combine(root, "sample.json"), JsonSerializer.Serialize(costs));
        var plan = SuiteCosts.Prepare("sample", Path.Combine(root, "plan"), root, null, 8);
        var keys = File.ReadLines(plan.Path).Select(line => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('\t')[0]))).ToArray();
        Assert.Contains("C:SmallClass", keys);
        Assert.Contains("T:NewLargeClass.Check(case: 1)", keys); Assert.Contains("T:NewLargeClass.Check(case: 2)", keys);
        Assert.Contains("M:Legacy.Rows", keys); Assert.Equal(155, plan.Costs.Sum());
        var classes = JsonSerializer.Deserialize<Dictionary<string, SuiteCosts.ClassCost>>(File.ReadAllText(Path.Combine(root, "sample.classes.json")))!;
        Assert.Equal(80, classes["NewLargeClass"].TrxSeconds); Assert.Equal(2, classes["NewLargeClass"].Cases);
        Assert.Equal(70, classes["Legacy"].Seconds);
    }
}
