using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

// release -AdoptEvidence (P8-93): a publication adopts only a complete, passing, uncached check-all run of its own clean commit
// and the passing push CI of that commit; every refusal case below names its reason.
public sealed class ReleaseEvidenceTests
{
    private static readonly EvidenceExternal Machine = new(3, new string('a', 64), new string('b', 64), "10.0.402", "Python 3.12.7", null, "machine-1");

    [Theory]
    [InlineData("quick")]
    [InlineData("affected")]
    public void DevelopmentRunsCannotBecomeReleaseEvidence(string mode)
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => evidence["mode"]![mode] = true);
        Assert.Contains("quick/affected runs cannot supply release evidence", Assert.Throws<ReleaseException>(fixture.Verify).Message);
    }

    [Fact]
    public void MergeCannotBecomeReleaseEvidenceOrSupplyKeptCodeV20RecordCounts()
    {
        using var fixture = new EvidenceFixture();
        var adoption = fixture.Verify();
        Assert.Equal("nightly", (string)adoption["record"]!["evidence"]!["profile"]!);
        fixture.Edit(evidence => evidence["mode"]!["profile"] = "merge");
        Assert.Contains("full nightly profile", Assert.Throws<ReleaseException>(fixture.Verify).Message);
        adoption["record"]!["evidence"]!["profile"] = "merge";
        adoption["record"]!["suites"]!["kept-code-v20"] = new JsonObject { ["passed"] = 4908, ["failed"] = 0, ["skipped"] = 0 };
        Assert.Contains("keptCodeV20Passed", Assert.Throws<ReleaseException>(() => ReleaseEvidence.SuitePassed(adoption, "kept-code-v20")).Message);
        Assert.Throws<ReleaseException>(() => ReleaseEvidence.AnnotateRecord(new JsonObject { ["test"] = false, ["validation"] = new JsonObject() }, adoption, ["kept-code-v20"]));
        adoption["record"]!["evidence"]!["profile"] = "nightly";
        Assert.Equal(4908, ReleaseEvidence.SuitePassed(adoption, "kept-code-v20"));
        adoption["record"]!["suites"]!["kept-code-v20"]!["execution"] = "derived";
        Assert.Throws<ReleaseException>(() => ReleaseEvidence.SuitePassed(adoption, "kept-code-v20"));
    }

    [Fact]
    public void HistoricalEvidenceWithoutAProfileStillMeansTheOriginalFullRun()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => evidence["mode"]!.AsObject().Remove("profile"));
        Assert.Equal("full", (string)fixture.Verify()["record"]!["evidence"]!["profile"]!);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void AcceptsTheCompleteEvidenceOfTheCleanHead()
    {
        using var fixture = new EvidenceFixture();
        var adoption = fixture.Verify();

        var record = adoption["record"]!.AsObject();
        Assert.Equal(ReleaseEvidence.AdoptionSchema, ReleaseEvidence.Text(adoption["schema"]));
        Assert.Equal(fixture.GitText("rev-parse", "HEAD"), ReleaseEvidence.Text(record["evidence"]!["commit"]));
        Assert.Equal(fixture.GitText("rev-parse", "HEAD^{tree}"), ReleaseEvidence.Text(record["evidence"]!["tree"]));
        Assert.Equal("clean", ReleaseEvidence.Text(record["evidence"]!["mode"]));
        Assert.Equal(2, ReleaseEvidence.Int(record["suites"]!["alpha"]!["passed"]));
        Assert.Equal(ReleaseEvidence.AdoptedChecks.Length, record["adoptedChecks"]!.AsArray().Count);
        Assert.DoesNotContain(":\\", record.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesEvidenceOfAnotherCommit()
    {
        using var fixture = new EvidenceFixture();
        File.AppendAllText(Path.Combine(fixture.Root, "README.md"), "next\n");
        fixture.Git("commit", "-q", "-am", "next commit");

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("evidence is for commit", error.Message);
        Assert.Contains("evidence is for tree", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesADirtyTreeAndAChangedInput()
    {
        using var fixture = new EvidenceFixture();
        File.AppendAllText(Path.Combine(fixture.Root, "README.md"), "uncommitted\n");

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("the working tree is dirty", error.Message);
        Assert.Contains("inputs changed since the run: README.md", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesAnUntrackedInput()
    {
        using var fixture = new EvidenceFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "new-input.txt"), "new\n");

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("the working tree is dirty", error.Message);
        Assert.Contains("new-input.txt", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesARunThatStartedOnADirtyTree()
    {
        using var fixture = new EvidenceFixture(beforeBegin: root => File.AppendAllText(Path.Combine(root, "README.md"), "dirty at start\n"));
        fixture.Git("checkout", "--", "README.md");

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("check-all started on a dirty tree", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesChangedInputsOutsideTheTree()
    {
        using var fixture = new EvidenceFixture();
        var changed = Machine with { PublicApiSha256 = new string('c', 64), DotnetSdk = "10.0.500", Python = "Python 3.13.0", Machine = "machine-2", NuGetConfigSha256 = new string('d', 64) };

        var error = Assert.Throws<ReleaseException>(() => fixture.Verify(changed));
        Assert.Contains("PublicAPI copies differ", error.Message);
        Assert.Contains("dotnetSdk differs", error.Message);
        Assert.Contains("python differs", error.Message);
        Assert.Contains("machine differs", error.Message);
        Assert.Contains("offline NuGet configuration differs", error.Message);

        error = Assert.Throws<ReleaseException>(() => fixture.Verify(Machine with { NuGetConfigSha256 = null }));
        Assert.Contains("offline NuGet configuration differs", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesToolsThatCouldNotReportTheirVersionAndAnUnreadableStartState()
    {
        var unavailable = Machine with { DotnetSdk = ReleaseEvidence.Unavailable + "exit 2", Python = ReleaseEvidence.Unavailable + "exit 2" };
        using var fixture = new EvidenceFixture(external: unavailable);
        var error = Assert.Throws<ReleaseException>(() => fixture.Verify(unavailable));
        Assert.Contains("dotnetSdk is unavailable", error.Message);
        Assert.Contains("python is unavailable", error.Message);

        var stub = ReleaseEvidence.BeginSafely(() => throw new ReleaseException("git is missing"), DateTimeOffset.UtcNow);
        Assert.Equal("git is missing", ReleaseEvidence.Text(stub["beginError"]));
        using var broken = new EvidenceFixture();
        broken.Edit(evidence => evidence["beginError"] = "git is missing");
        Assert.Contains("could not record its start state: git is missing", Assert.Throws<ReleaseException>(broken.Verify).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesAPartialRun()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence =>
        {
            evidence["planned"]!.AsArray().Add("not-run-job");
            evidence["mode"]!["buildOnly"] = true;
            var jobs = evidence["jobs"]!.AsArray();
            jobs.Remove(jobs.Single(job => ReleaseEvidence.Text(job!["name"]) == "studio-gui-true"));
            evidence["planned"] = ReleaseEvidence.Strings(evidence["planned"]!.AsArray().Select(ReleaseEvidence.Text).Where(name => name != "studio-gui-true"));
            evidence["suites"]!.AsObject().Remove("beta");
        });

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("planned job has no result (partial run): not-run-job", error.Message);
        Assert.Contains("-BuildOnly", error.Message);
        Assert.Contains("required check-all job missing: studio-gui-true", error.Message);
        Assert.Contains("registered suite missing from evidence: beta", error.Message);
    }

    [Fact]
    public void TimeGateExceptionAcceptsOnlyARunWhoseSoleFailureIsTheGate()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => { evidence["result"]!["exitCode"] = 1; evidence["result"]!["underThirtyMinutes"] = false; evidence["result"]!["wallSeconds"] = 1985.0; });
        Assert.Contains("30-minute gate", Assert.Throws<ReleaseException>(fixture.Verify).Message);
        var record = fixture.VerifyWithTimeGateException("maintainer 2026-10-27, beta.2 only")["record"]!;
        Assert.Equal("maintainer 2026-10-27, beta.2 only", ReleaseEvidence.Text(record["timeGateException"]?["approval"]));
        Assert.Equal(1800, (int)record["timeGateException"]!["gateSeconds"]!);
    }

    [Fact]
    public void TimeGateExceptionNeverExcusesAFailedJob()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => { evidence["result"]!["exitCode"] = 1; evidence["result"]!["failed"] = 1; evidence["result"]!["underThirtyMinutes"] = false; });
        var error = Assert.Throws<ReleaseException>(() => fixture.VerifyWithTimeGateException("maintainer 2026-10-27, beta.2 only"));
        Assert.Contains("check-all exited 1", error.Message);
        Assert.Contains("30-minute gate", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesAFailedRun()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence =>
        {
            evidence["result"]!["exitCode"] = 1;
            evidence["result"]!["failed"] = 1;
            evidence["result"]!["underThirtyMinutes"] = false;
            evidence["result"]!["drift"] = ReleaseEvidence.Strings(["manifest/tools-list.json"]);
            var job = evidence["jobs"]!.AsArray().Single(job => ReleaseEvidence.Text(job!["name"]) == "responses-v19-plc")!;
            job["status"] = "failed";
            job["exitCode"] = 1;
        });

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("check-all exited 1", error.Message);
        Assert.Contains("1 check-all job(s) failed", error.Message);
        Assert.Contains("job responses-v19-plc is failed", error.Message);
        Assert.Contains("tracked-tree drift: manifest/tools-list.json", error.Message);
        Assert.Contains("30-minute gate", error.Message);
        Assert.Contains("required check-all job missing: responses-v19", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesAReusedOrCachedRun()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence =>
        {
            evidence["result"]!["reused"] = 1;
            evidence["jobs"]!.AsArray().First(job => ReleaseEvidence.Text(job!["name"]) == "catalog-drift")!["reused"] = true;
            evidence["mode"]!["resultCacheEnabled"] = true;
            evidence["mode"]!["clean"] = false;
            evidence["mode"]!["buildCacheDisabled"] = false;
            evidence["buildCache"]!["outputsRestored"] = 3;
        });

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("reused earlier results", error.Message);
        Assert.Contains("job catalog-drift is passed", error.Message);
        Assert.Contains("result cache enabled", error.Message);
        Assert.Contains("neither -Clean nor -Cold", error.Message);
        Assert.Contains("without -NoBuildCache", error.Message);
        Assert.Contains("restored cached build outputs", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesASuiteBelowItsRegisteredGateAndAChangedSummary()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => evidence["suites"]!["alpha"]!["passed"] = 1);
        File.AppendAllText(Path.Combine(fixture.Output, "suites", "beta.json"), "\n");

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("suite alpha does not meet its registered gate (1 passed", error.Message);
        Assert.Contains("suite summary missing or changed after the run: beta", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesChangedOrMissingJobLogsAndReport()
    {
        using var fixture = new EvidenceFixture();
        File.AppendAllText(Path.Combine(fixture.Output, "logs", "contracts-v20-plc.log"), "edited\n");
        File.Delete(Path.Combine(fixture.Output, "logs", "usage-audit-plc.log"));
        File.AppendAllText(Path.Combine(fixture.Output, "report.json"), "\n");

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("job log changed after the run: contracts-v20-plc", error.Message);
        Assert.Contains("job log missing next to the evidence: usage-audit-plc", error.Message);
        Assert.Contains("report.json is missing or changed after the run", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesEvidenceOlderThanTheCommitOrFromTheFuture()
    {
        using var older = new EvidenceFixture(started: DateTimeOffset.UtcNow.AddHours(-2));
        var error = Assert.Throws<ReleaseException>(older.Verify);
        Assert.Contains("before the commit time", error.Message);

        using var future = new EvidenceFixture(completed: DateTimeOffset.UtcNow.AddDays(1));
        error = Assert.Throws<ReleaseException>(future.Verify);
        Assert.Contains("lies in the future", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesAnotherSchemaAndAMissingFile()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => evidence["schema"] = "tia-mcp/check-all-release-evidence/0");
        Assert.Contains("schema", Assert.Throws<ReleaseException>(fixture.Verify).Message);

        File.Delete(fixture.EvidencePath);
        Assert.Contains("evidence missing", Assert.Throws<ReleaseException>(fixture.Verify).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RefusesEvidenceWithoutItsResultSection()
    {
        using var fixture = new EvidenceFixture();
        fixture.Edit(evidence => evidence.Remove("result"));

        var error = Assert.Throws<ReleaseException>(fixture.Verify);
        Assert.Contains("lacks its result, mode or source section (partial run)", error.Message);
        Assert.Contains("30-minute gate", error.Message);
    }

    [Theory]
    [InlineData("-AdoptEvidence", "-EarlyGatesOnly")]
    [InlineData("-AdoptEvidence", "-Resume")]
    [InlineData("-BumpOnly", "-DryRun")]
    [InlineData("-BumpOnly", "-AdoptEvidence")]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseRefusesAdoptionAndBumpCombinationsBeforeAnyEffect(string first, string second)
    {
        var arguments = new List<string> { "-Tier", "full", "-Version", "4.0.0-beta.2" };
        foreach (var option in new[] { first, second }) arguments.AddRange(option == "-AdoptEvidence" ? [option, "missing/release-evidence.json"] : [option]);
        var options = Options.Parse(arguments.ToArray(), new HashSet<string>(["DryRun", "EarlyGatesOnly", "Resume", "BumpOnly"], StringComparer.OrdinalIgnoreCase));

        Assert.Contains("cannot be combined", Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("release", options)).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void WriterBindsEveryJobLogSuiteSummaryAndResult()
    {
        using var fixture = new EvidenceFixture();
        var evidence = JsonNode.Parse(File.ReadAllText(fixture.EvidencePath))!.AsObject();

        var jobs = evidence["jobs"]!.AsArray();
        Assert.All(jobs.Where(job => ReleaseEvidence.Text(job!["name"]) != "tracked-tree-drift"), job => Assert.Matches("^[0-9a-f]{64}$", ReleaseEvidence.Text(job!["logSha256"])));
        Assert.Equal(jobs.Count, ReleaseEvidence.Int(evidence["result"]!["passed"]));
        Assert.Equal(0, ReleaseEvidence.Int(evidence["result"]!["reused"]));
        Assert.Equal(evidence["planned"]!.AsArray().Count, evidence["planned"]!.AsArray().Select(ReleaseEvidence.Text).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, ReleaseEvidence.Int(evidence["suites"]!["alpha"]!["minimumPassed"]));
        Assert.True(ReleaseEvidence.Bool(evidence["source"]!["cleanAtStart"]));
        Assert.True(ReleaseEvidence.Bool(evidence["mode"]!["clean"]));
        Assert.False(ReleaseEvidence.Bool(evidence["mode"]!["resultCacheEnabled"]));
        Assert.Equal(fixture.GitText("rev-parse", "HEAD"), ReleaseEvidence.Text(evidence["source"]!["commit"]));
        Assert.Equal(3, ReleaseEvidence.Int(evidence["source"]!["inputs"]!["count"]));
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RequiresEverySuiteGateAndEveryReleaseFamily()
    {
        using var fixture = new EvidenceFixture();
        var required = ReleaseEvidence.RequiredJobs(fixture.Root);

        Assert.Contains(("alpha-gate", true), required);
        Assert.Contains(("beta-gate", true), required);
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (var family in new[] { "contracts", "responses", "transport-usage", "usage" })
                Assert.Contains((family + "-v" + key, false), required);
        foreach (var key in new[] { "20", "21" })
            foreach (var family in new[] { "harness", "approval", "resources", "ecosystem", "campaign-inputs" })
                Assert.Contains((family + "-v" + key, false), required);
        Assert.Contains(("stability-v21-c16", false), required);
        Assert.All(ReleaseEvidence.StudioJobs, job => Assert.Contains((job, true), required));
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void CiVerificationAcceptsTheLatestSuccessfulPushRunsWithAllJobs()
    {
        var sha = new string('1', 40);
        var runs = Runs(sha, Run(101, "validate-bundle", sha, "push", "master", "completed", "success", attempt: 1),
            Run(102, "offline-checks", sha, "push", "master", "completed", "failure", attempt: 1, number: 7),
            Run(103, "offline-checks", sha, "push", "master", "completed", "success", attempt: 2, number: 7),
            Run(104, "offline-checks", sha, "pull_request", "feature", "completed", "failure", attempt: 1, number: 8));

        var records = ReleaseEvidence.VerifyCi(Get(runs, Jobs(("build", "completed", "success"), ("gate", "completed", "success"))), "owner/repo", sha, ReleaseEvidence.PushWorkflows);

        Assert.Equal(2, records.Count);
        Assert.Equal(101L, (long)records[0]!["runId"]!);
        Assert.Equal(103L, (long)records[1]!["runId"]!);
        Assert.Equal(2, ReleaseEvidence.Int(records[1]!["runAttempt"]));
        Assert.Equal(2, ReleaseEvidence.Int(records[1]!["jobs"]));
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void CiVerificationRefusesMissingFailedRunningAndIncompleteRuns()
    {
        var sha = new string('2', 40);
        var missing = Runs(sha, Run(201, "validate-bundle", sha, "pull_request", "master", "completed", "success"));
        var error = Assert.Throws<ReleaseException>(() => ReleaseEvidence.VerifyCi(Get(missing, Jobs(("a", "completed", "success"))), "owner/repo", sha, ReleaseEvidence.PushWorkflows));
        Assert.Contains("no push run of validate-bundle on master", error.Message);
        Assert.Contains("no push run of offline-checks on master", error.Message);

        var failed = Runs(sha, Run(202, "validate-bundle", sha, "push", "master", "completed", "failure"),
            Run(203, "offline-checks", sha, "push", "master", "in_progress", null));
        error = Assert.Throws<ReleaseException>(() => ReleaseEvidence.VerifyCi(Get(failed, Jobs(("a", "completed", "success"))), "owner/repo", sha, ReleaseEvidence.PushWorkflows));
        Assert.Contains("validate-bundle run 1 concluded failure", error.Message);
        Assert.Contains("offline-checks run 1 is in_progress", error.Message);

        var passed = Runs(sha, Run(204, "validate-bundle", sha, "push", "master", "completed", "success"),
            Run(205, "offline-checks", sha, "push", "master", "completed", "success"));
        error = Assert.Throws<ReleaseException>(() => ReleaseEvidence.VerifyCi(Get(passed, Jobs(("studio", "completed", "skipped"))), "owner/repo", sha, ReleaseEvidence.PushWorkflows));
        Assert.Contains("job studio is completed/skipped", error.Message);

        var other = Runs(sha, Run(206, "validate-bundle", new string('3', 40), "push", "master", "completed", "success"));
        error = Assert.Throws<ReleaseException>(() => ReleaseEvidence.VerifyCi(Get(other, Jobs(("a", "completed", "success"))), "owner/repo", sha, ["validate-bundle"]));
        Assert.Contains("no push run of validate-bundle", error.Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void AdoptedPublicationCommitsOnlyReleaseRecords()
    {
        Assert.Empty(ReleaseEvidence.NonRecordChanges(["manifest/multi-version-build.json", "manifest/contracts/v4/x.json", "docs/reference/tool-matrix.md", "docs/reference/version-tool-catalog.md"]));
        Assert.Equal(["Version.props", "src/Logic/Tool.cs", "docs/reference/capabilities.md"],
            ReleaseEvidence.NonRecordChanges(["manifest/delivery.json", "Version.props", "src/Logic/Tool.cs", "docs/reference/capabilities.md"]));
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseCommitGuardRefusesNonRecordChangesAndAMovedHead()
    {
        using var fixture = new EvidenceFixture();
        var commit = fixture.GitText("rev-parse", "HEAD");
        ReleaseEvidence.RequireUnchangedTree(fixture.Root, "git", commit);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "manifest"));
        File.WriteAllText(Path.Combine(fixture.Root, "manifest", "multi-version-build.json"), "{}\n");
        ReleaseEvidence.RequireRecordOnlyChanges(fixture.Root, "git", commit);
        Assert.Contains("tree changed before the build", Assert.Throws<ReleaseException>(() => ReleaseEvidence.RequireUnchangedTree(fixture.Root, "git", commit)).Message);

        File.AppendAllText(Path.Combine(fixture.Root, "tests", "test-suites.json"), "\n");
        var error = Assert.Throws<ReleaseException>(() => ReleaseEvidence.RequireRecordOnlyChanges(fixture.Root, "git", commit));
        Assert.Contains("only release records; the build also changed: tests/test-suites.json", error.Message);

        fixture.Git("checkout", "--", "tests/test-suites.json");
        File.AppendAllText(Path.Combine(fixture.Root, "README.md"), "moved\n");
        fixture.Git("commit", "-q", "-am", "moved head");
        Assert.Contains("HEAD moved", Assert.Throws<ReleaseException>(() => ReleaseEvidence.RequireRecordOnlyChanges(fixture.Root, "git", commit)).Message);
        Assert.Contains("HEAD moved", Assert.Throws<ReleaseException>(() => ReleaseEvidence.RequireUnchangedTree(fixture.Root, "git", commit)).Message);
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void CandidateCapturesOfOneGroupRunSideBySideAndCompareInTableOrder()
    {
        using var tree = new StaticTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "TIA_V20_PublicAPI/V20"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "TIA_V21_PublicAPI/V21/net48"));
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        int running = 0, peak = 0;
        var captures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var comparisons = new List<string>();
        ReleaseCommands.RunCandidateChecks(new("quick", ["plc-responses"], [], []),
            tree.Root, tree.Root, tree.Root, tree.Root, "dotnet-double", "python-double", 4,
            (name, action) => { if (name == "plc-responses") action(); },
            (_, args) => { lock (comparisons) comparisons.Add(args[Array.IndexOf(args, "--current") + 1]); },
            (_, _) => throw new InvalidOperationException("Unexpected Foundation capture action"),
            (name, executable, args, _, _) =>
            {
                if (executable != "python-double") return new(0, "", "");
                var current = Interlocked.Increment(ref running);
                lock (captures) peak = Math.Max(peak, current);
                Thread.Sleep(200);
                var arguments = args.ToArray();
                var output = arguments[Array.IndexOf(arguments, "--output") + 1];
                var key = arguments[Array.IndexOf(arguments, "--releases") + 1];
                Directory.CreateDirectory(output);
                File.Copy(Path.Combine(root, "manifest/contracts/v4/responses", key + ".json"), Path.Combine(output, key + ".json"));
                captures.Add(name);
                Interlocked.Decrement(ref running);
                return new(0, "", "");
            });

        var expected = ReleaseCommands.PlcReleaseJobs(root, tree.Root, tree.Root, tree.Root, "dotnet-double", "python-double")
            .Where(job => job.Check == "plc-responses").Select(job => job.Evidence).ToArray();
        Assert.Equal(8, captures.Count);
        Assert.InRange(peak, 2, 4);
        Assert.Equal(expected, comparisons);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void AdoptionRequiresThePrepCommitToCarryTheVersion()
    {
        Assert.Null(ReleaseEvidence.PrepCommitError("4.0.0-beta.2", "4.0.0-beta.2"));
        var error = ReleaseEvidence.PrepCommitError("4.0.0-beta.1", "4.0.0-beta.2");
        Assert.Contains("prep commit that already carries 4.0.0-beta.2 (Version.props has 4.0.0-beta.1)", error);
        Assert.Contains("-BumpOnly", error);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void AdoptedSuiteCountsKeepTheReleaseSuiteGate()
    {
        var adoption = Adoption(("offline", 303, 0, 0), ("kept-code", 10, 0, 1), ("updater", 0, 0, 0));

        Assert.Equal(303, ReleaseEvidence.SuitePassed(adoption, "offline"));
        Assert.Contains("did not meet", Assert.Throws<ReleaseException>(() => ReleaseEvidence.SuitePassed(adoption, "kept-code")).Message);
        Assert.Contains("did not meet", Assert.Throws<ReleaseException>(() => ReleaseEvidence.SuitePassed(adoption, "updater")).Message);
        Assert.Contains("no result", Assert.Throws<ReleaseException>(() => ReleaseEvidence.SuitePassed(adoption, "write-guard")).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void AnnotatedRecordNamesTheEvidenceAndKeepsItsFields()
    {
        var adoption = Adoption(("foundation-api", 30000, 0, 1), ("prompt-registration", 195, 0, 0));
        var record = new JsonObject { ["tier"] = "full", ["test"] = false, ["validation"] = new JsonObject { ["nativeTiaExecuted"] = false } };

        var annotated = ReleaseEvidence.AnnotateRecord(record, adoption, ["foundation-api", "prompt-registration"]);

        Assert.Equal(30000, ReleaseEvidence.Int(annotated["validation"]!["dotnetSuites"]!["foundation-api"]!["passed"]));
        Assert.Equal(1, ReleaseEvidence.Int(annotated["validation"]!["dotnetSuites"]!["foundation-api"]!["skipped"]));
        Assert.Equal("check-all", ReleaseEvidence.Text(annotated["adoptedValidation"]!["evidence"]!["file"]));
        Assert.False(ReleaseEvidence.Bool(annotated["validation"]!["nativeTiaExecuted"]));

        var tested = new JsonObject { ["test"] = true, ["validation"] = new JsonObject() };
        Assert.Contains("without -Test", Assert.Throws<ReleaseException>(() => ReleaseEvidence.AnnotateRecord(tested, adoption, ["prompt-registration"])).Message);
        var untested = new JsonObject { ["test"] = false, ["validation"] = new JsonObject() };
        Assert.Contains("no result for suite device-add", Assert.Throws<ReleaseException>(() => ReleaseEvidence.AnnotateRecord(untested, adoption, ["device-add"])).Message);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void ChildrenReadTheAdoptionOnlyWhenTheReleaseSetIt()
    {
        Assert.Null(ReleaseEvidence.LoadAdoption(_ => null));
        var path = Path.Combine(Path.GetTempPath(), "release-adoption-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, new JsonObject { ["schema"] = "other" }.ToJsonString());
            Assert.Contains("invalid", Assert.Throws<ReleaseException>(() => ReleaseEvidence.LoadAdoption(_ => path)).Message);
            File.WriteAllText(path, Adoption(("offline", 1, 0, 0)).ToJsonString());
            Assert.NotNull(ReleaseEvidence.LoadAdoption(name => name == ReleaseEvidence.AdoptionVariable ? path : null));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void AdoptedLocalPlcChecksAreTheCoverageAndNativeSafetyJobsOnly()
    {
        var jobs = ReleaseCommands.PlcReleaseJobs("root", "api", "logs", "temp", "dotnet", "python").Where(ReleaseCommands.IsAdoptionLocalPlcJob).ToArray();

        Assert.Equal(8, jobs.Length);
        Assert.All(jobs, job => Assert.True(job.Check is "native-coverage" or "native-diagnostics" or "offline-suites"));
        Assert.DoesNotContain(jobs, job => job.Mode == "capture" || job.Executable == "test-suites" || job.Name.StartsWith("harness-", StringComparison.Ordinal));
        Assert.Equal(2, jobs.Count(job => job.Mode == "native-safety"));
    }

    private static JsonObject Adoption(params (string Suite, int Passed, int Failed, int Skipped)[] suites)
    {
        var rows = new JsonObject();
        foreach (var suite in suites)
            rows[suite.Suite] = new JsonObject { ["passed"] = suite.Passed, ["failed"] = suite.Failed, ["skipped"] = suite.Skipped, ["total"] = suite.Passed + suite.Failed + suite.Skipped,
                ["minimumPassed"] = Math.Max(1, suite.Passed), ["maximumSkipped"] = suite.Skipped };
        return new JsonObject
        {
            ["schema"] = ReleaseEvidence.AdoptionSchema,
            ["record"] = new JsonObject { ["evidence"] = new JsonObject { ["file"] = "check-all" }, ["suites"] = rows, ["localChecks"] = new JsonArray() }
        };
    }

    [Fact]
    public void CiCoveredEvidenceCannotBeAdoptedOfflineAndKeepsEveryNamedJobInTheAdoptionRecord()
    {
        using var fixture = new EvidenceFixture();
        fixture.InstallCiCoverage();
        Assert.Contains("online named-job verification", Assert.Throws<ReleaseException>(() => fixture.Verify()).Message);
        var adoption = fixture.VerifyCoverage(coverage => CiCoverageTests.VerifyRemote(coverage));
        Assert.Single(adoption["record"]!["ci"]!.AsArray());
        Assert.Equal(19, adoption["record"]!["ciCoveredJobs"]!.AsArray().Count);
        Assert.All(adoption["record"]!["ciCoveredJobs"]!.AsArray(), row =>
        {
            Assert.Equal(301L, (long)row!["ci"]!["jobId"]!);
            Assert.Equal(101L, (long)row["ci"]!["receipt"]!["runId"]!);
            Assert.Equal(2, (int)row["ci"]!["receipt"]!["runAttempt"]!);
        });
        fixture.Edit(evidence => evidence["jobs"]!.AsArray().Single(job => (string?)job!["name"] == "release-tool-shard-0")!["status"] = "passed");
        Assert.Contains("mapping differs", Assert.Throws<ReleaseException>(() => fixture.VerifyCoverage(coverage => CiCoverageTests.VerifyRemote(coverage))).Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("commit")]
    [InlineData("attempt")]
    [InlineData("latest-attempt")]
    [InlineData("log-missing")]
    public void EvidenceAdoptionRefusesUnverifiedCiCoverage(string change)
    {
        using var fixture = new EvidenceFixture();
        fixture.InstallCiCoverage();
        Assert.Contains("Adoption refused", Assert.Throws<ReleaseException>(() => fixture.VerifyCoverage(coverage => CiCoverageTests.VerifyRemote(coverage, change))).Message);
    }

    [Fact]
    public void CiReceiptReadsEveryRealTrxAndRefusesSummaryOrGateTampering()
    {
        using var fixture = new EvidenceFixture();
        fixture.InstallCiCoverage();
        var gates = ReleaseEvidence.SuiteRegistry(fixture.Root);
        foreach (var (suite, gate) in gates)
            DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(fixture.Output, "suites", suite + ".trx"), Enumerable.Repeat("Passed", gate.MinimumPassed).ToArray());
        var environment = new Dictionary<string, string?> { ["GITHUB_ACTIONS"] = "true", ["GITHUB_SHA"] = fixture.GitText("rev-parse", "HEAD"),
            ["GITHUB_JOB"] = "validate-bundle", ["GITHUB_REPOSITORY"] = "owner/repo", ["GITHUB_RUN_ID"] = "101", ["GITHUB_RUN_ATTEMPT"] = "2" };
        Assert.Throws<ReleaseException>(() => CiCoverage.Receipt(fixture.Root, "release", Path.Combine(fixture.Output, "suites"), key => environment.GetValueOrDefault(key), () => false));
        JsonObject Read() => CiCoverage.Receipt(fixture.Root, "release", Path.Combine(fixture.Output, "suites"), key => environment.GetValueOrDefault(key), () => true);
        var receipt = Read();
        Assert.Equal(gates.Keys.Order(), receipt["suites"]!.AsObject().Select(pair => pair.Key).Order());
        var candidate = Path.Combine(fixture.Output, "ci-covered.json");
        File.WriteAllText(candidate, CiCoverage.Assemble([(receipt, 301L)]).ToJsonString());
        Assert.NotNull(CiCoverage.Load(fixture.Root, candidate, fixture.GitText("rev-parse", "HEAD")));
        var summary = Path.Combine(fixture.Output, "suites", "release-tool.json");
        var original = File.ReadAllText(summary);
        var changed = JsonNode.Parse(original)!;
        changed["passed"] = 1;
        File.WriteAllText(summary, changed.ToJsonString());
        Assert.Contains("differs from TRX", Assert.Throws<ReleaseException>(Read).Message);
        File.WriteAllText(summary, original);
        DotnetSuiteSelfTests.SyntheticTrx(Path.Combine(fixture.Output, "suites", "release-tool.trx"), ["Failed"]);
        Assert.Contains("gate failed", Assert.Throws<ReleaseException>(Read).Message);
        File.AppendAllText(Path.Combine(fixture.Root, "README.md"), "dirty input\n");
        Assert.Contains("clean checkout", Assert.Throws<ReleaseException>(() => CiCoverage.Load(fixture.Root, candidate, fixture.GitText("rev-parse", "HEAD"))).Message);
    }

    private static JsonObject Run(long id, string name, string sha, string trigger, string branch, string status, string? conclusion, int attempt = 1, int number = 1) => new()
    {
        ["id"] = id, ["name"] = name, ["head_sha"] = sha, ["event"] = trigger, ["head_branch"] = branch, ["status"] = status, ["conclusion"] = conclusion,
        ["run_number"] = number, ["run_attempt"] = attempt, ["html_url"] = "https://github.invalid/runs/" + id, ["updated_at"] = "2026-10-22T00:00:00Z"
    };

    private static string Runs(string sha, params JsonObject[] runs) => new JsonObject { ["total_count"] = runs.Length, ["workflow_runs"] = new JsonArray(runs) }.ToJsonString();

    private static string Jobs(params (string Name, string Status, string Conclusion)[] jobs) => new JsonObject
    {
        ["total_count"] = jobs.Length,
        ["jobs"] = new JsonArray(jobs.Select(job => (JsonNode?)new JsonObject { ["name"] = job.Name, ["status"] = job.Status, ["conclusion"] = job.Conclusion }).ToArray())
    }.ToJsonString();

    private static Func<string, string> Get(string runs, string jobs) => url =>
    {
        Assert.StartsWith("repos/owner/repo/actions/runs", url, StringComparison.Ordinal);
        if (url.Contains("&page=2", StringComparison.Ordinal)) return url.Contains("/jobs?", StringComparison.Ordinal) ? Jobs() : Runs("");
        return url.Contains("/jobs?", StringComparison.Ordinal) ? jobs : runs;
    };

    // A Git repository with a two-suite registry and the check-all output of one complete, passing, clean run of its HEAD.
    private sealed class EvidenceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "release-evidence-" + Guid.NewGuid().ToString("N"));
        internal string Output { get; }
        internal string EvidencePath { get; }

        internal EvidenceFixture(Action<string>? beforeBegin = null, DateTimeOffset? started = null, DateTimeOffset? completed = null, EvidenceExternal? external = null)
        {
            Directory.CreateDirectory(Path.Combine(Root, "tests"));
            Git("init", "--initial-branch=master");
            Git("config", "user.name", "Evidence fixture");
            Git("config", "user.email", "evidence-fixture@example.invalid");
            Git("config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(Root, "README.md"), "fixture\n");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), "bin-build/\n");
            File.WriteAllText(Path.Combine(Root, "tests/test-suites.json"), """
                {
                  "alpha": { "project": "tests/Alpha.csproj", "minimumPassed": 2, "maximumSkipped": 0, "arguments": [] },
                  "beta": { "project": "tests/Beta.csproj", "minimumPassed": 1, "maximumSkipped": 1, "arguments": [],
                    "platforms": { "windows": { "minimumPassed": 1, "maximumSkipped": 1, "reason": "fixture" } } }
                }
                """);
            Git("add", "README.md", ".gitignore", "tests/test-suites.json");
            Git("commit", "-q", "-m", "fixture base");
            beforeBegin?.Invoke(Root);
            Output = Path.Combine(Root, "bin-build", "check-all", "run-1");
            Directory.CreateDirectory(Path.Combine(Output, "logs"));
            Directory.CreateDirectory(Path.Combine(Output, "suites"));
            var environment = new Dictionary<string, string?> { ["TIA_MCP_CLEAN_CHECKOUT"] = Root, ["TIA_MCP_BUILD_CACHE_DISABLED"] = "1" };
            var options = Options.Parse(["-Clean", "-NoBuildCache", "-Profile", "nightly"], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Clean", "NoBuildCache" });
            var begin = ReleaseEvidence.Begin(Root, "git", options, external ?? Machine, name => environment.GetValueOrDefault(name), started ?? DateTimeOffset.UtcNow.AddSeconds(1));

            var names = ReleaseEvidence.RequiredJobs(Root).Select(job => job.Exact ? job.Name : job.Name + "-plc").Append("catalog-drift").Distinct(StringComparer.Ordinal).ToArray();
            var planned = names.Where(name => name != "tracked-tree-drift").ToArray();
            foreach (var name in planned) File.WriteAllText(Path.Combine(Output, "logs", name + ".log"), "PASS " + name + "\n");
            File.WriteAllText(Path.Combine(Output, "suites", "alpha.json"), """{"suite":"alpha","passed":2,"failed":0,"skipped":0,"total":2,"minimumPassed":2,"maximumSkipped":0,"errors":[]}""");
            File.WriteAllText(Path.Combine(Output, "suites", "beta.json"), """{"suite":"beta","passed":4,"failed":0,"skipped":1,"total":5,"minimumPassed":1,"maximumSkipped":1,"errors":[]}""");
            File.WriteAllText(Path.Combine(Output, "report.json"), "{\"passed\":1}\n");
            File.WriteAllText(Path.Combine(Output, "cpu-plan.json"), "{\"checks\":[]}\n");
            File.WriteAllText(Path.Combine(Output, "build-cache.json"), "{\"mode\":\"population-disabled\",\"outputsRestored\":0}\n");
            var timings = names.Select(name => new CheckTiming(name, "passed", 0, 1.5, null)).ToArray();
            EvidencePath = ReleaseEvidence.Write(begin, Output, [.. planned, .. planned.Take(2)], timings, 0, 900, true, [], completed ?? DateTimeOffset.UtcNow.AddSeconds(2));
        }

        internal JsonObject Verify() => Verify(Machine);

        internal JsonObject VerifyWithTimeGateException(string approval) =>
            ReleaseEvidence.Verify(new ReleaseEvidence.Inputs(Root, "git", EvidencePath, Machine, DateTimeOffset.UtcNow.AddMinutes(1), null, approval));

        internal JsonObject Verify(EvidenceExternal current) =>
            ReleaseEvidence.Verify(new ReleaseEvidence.Inputs(Root, "git", EvidencePath, current, DateTimeOffset.UtcNow.AddMinutes(1)));

        internal JsonObject VerifyCoverage(Func<JsonObject, JsonArray> verify) =>
            ReleaseEvidence.Verify(new ReleaseEvidence.Inputs(Root, "git", EvidencePath, Machine, DateTimeOffset.UtcNow.AddMinutes(1), verify));

        internal void InstallCiCoverage()
        {
            var repo = TiaMcp.BuildCommon.Repository.FindRoot();
            var profile = CiCoverage.Profiles["release"];
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "tests/test-suites.json")))!.AsObject();
            var catalog = new JsonObject();
            foreach (var name in profile.Suites)
            {
                catalog[name] = source[name]!.DeepClone();
                var project = Path.Combine(Root, (string)catalog[name]!["project"]!);
                Directory.CreateDirectory(Path.GetDirectoryName(project)!); File.WriteAllText(project, "<Project />\n");
            }
            File.WriteAllText(Path.Combine(Root, "tests/test-suites.json"), catalog.ToJsonString());
            var workflow = Path.Combine(Root, profile.File);
            Directory.CreateDirectory(Path.GetDirectoryName(workflow)!); File.Copy(Path.Combine(repo, profile.File), workflow);
            Git("add", "tests", ".github"); Git("commit", "-q", "-m", "CI fixture registry");
            var environment = new Dictionary<string, string?> { ["TIA_MCP_CLEAN_CHECKOUT"] = Root, ["TIA_MCP_BUILD_CACHE_DISABLED"] = "1" };
            var options = Options.Parse(["-Clean", "-NoBuildCache", "-Profile", "nightly"], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Clean", "NoBuildCache" });
            var begin = ReleaseEvidence.Begin(Root, "git", options, Machine, name => environment.GetValueOrDefault(name), DateTimeOffset.UtcNow.AddSeconds(1));
            var candidate = CiCoverage.Validate(Root, CiCoverageTests.Candidate(Root, "release", GitText("rev-parse", "HEAD")), GitText("rev-parse", "HEAD"));
            begin["ciCovered"] = candidate.DeepClone();
            CiCoverage.WriteSummaries(Output, candidate);
            var names = ReleaseEvidence.RequiredJobs(Root).Select(job => job.Exact ? job.Name : job.Name + "-plc")
                .Concat(profile.Suites.Select(suite => suite + "-shard-0")).Concat(profile.Checks).Concat(["repository", "bundle"]).Distinct().ToArray();
            foreach (var name in names) File.WriteAllText(Path.Combine(Output, "logs", name + ".log"), "PASS " + name + "\n");
            var timings = names.Select(name => new CheckTiming(name, CiCoverage.Binding(name, candidate) == null ? "passed" : "covered by CI", 0, 1, null)
                { CiCoverage = CiCoverage.Binding(name, candidate)?.DeepClone().AsObject() }).ToArray();
            ReleaseEvidence.Write(begin, Output, names, timings, 0, 900, true, [], DateTimeOffset.UtcNow.AddSeconds(2));
        }

        internal void Edit(Action<JsonObject> change)
        {
            var evidence = JsonNode.Parse(File.ReadAllText(EvidencePath))!.AsObject();
            change(evidence);
            File.WriteAllText(EvidencePath, evidence.ToJsonString());
        }

        internal string GitText(params string[] arguments) => ProcessRunner.Run("git", ["-C", Root, .. arguments], Root).StandardOutput.Trim();

        internal void Git(params string[] arguments)
        {
            var result = ProcessRunner.Run("git", ["-C", Root, .. arguments], Root);
            ProcessRunner.RequireSuccess(result, "Fixture git " + string.Join(' ', arguments));
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
