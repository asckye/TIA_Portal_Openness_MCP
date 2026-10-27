using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class CiCoverageTests
{
    private static string Root => Repository.FindRoot();
    private const string Sha = "1111111111111111111111111111111111111111";

    internal static JsonObject Candidate(string root, string profileName, string sha)
    {
        var profile = CiCoverage.Profiles[profileName];
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root, "windows");
        var suites = new JsonObject();
        foreach (var name in profile.Suites)
        {
            var suite = catalog[name]!;
            int minimum = ReleaseEvidence.Int(suite["minimumPassed"]);
            suites[name] = new JsonObject
            {
                ["registration"] = suite.DeepClone(), ["trxSha256"] = new string('a', 64),
                ["summary"] = new JsonObject { ["suite"] = name, ["passed"] = minimum, ["executed"] = minimum, ["failed"] = 0, ["skipped"] = 0,
                    ["total"] = minimum, ["minimumPassed"] = minimum, ["maximumSkipped"] = suite["maximumSkipped"]!.DeepClone(), ["errors"] = new JsonArray() }
            };
        }
        return CiCoverage.Assemble([(new JsonObject
        {
            ["schema"] = CiCoverage.Schema, ["profile"] = profileName, ["repository"] = "owner/repo", ["commit"] = sha,
            ["runId"] = 101L, ["runAttempt"] = 2L, ["jobName"] = profile.Job, ["platform"] = "windows",
            ["workflowSha256"] = ReleaseRecords.HashFile(Path.Combine(root, profile.File)), ["suites"] = suites, ["checks"] = ReleaseEvidence.Strings(profile.Checks)
        }, 301L)]);
    }

    [Fact]
    public void ProfileSuiteListsMatchTheWholeWindowsWorkflowCommandsAndFoundationRunnerUnion()
    {
        foreach (var (name, profile) in CiCoverage.Profiles)
        {
            var workflow = File.ReadAllText(Path.Combine(Root, profile.File));
            var block = workflow.Split("\n  " + profile.Job + ":", StringSplitOptions.None)[1].Split("\n  foundation-transport:", StringSplitOptions.None)[0];
            var suites = Regex.Matches(block, @"-Suite ([a-z0-9-]+)").Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
            Assert.Equal(profile.Suites.Order(), suites);
            Assert.Contains("runs-on: windows-latest", block);
            Assert.Contains("ci-coverage -Profile " + name, block);
            if (name == "release")
            {
                foreach (var command in new[] { "check-repository -NoBinaries", "validate-bundle -Strict -NoBinaries -SkipSourceHashes", "validate-bundle -SelfTest",
                    "release -DocumentationOnly", "run-release-build -Tier full -DryRun", "python -X utf8 scripts/checks/Test-NativeLifecycle.py --self-test",
                    "python -X utf8 scripts/checks/Test-NativeMcpSession.py --self-test", "check-tia-features", "dotnet run scripts/checks/Test-MatchPlcName.cs -- -SourceOnly" })
                    Assert.True(block.IndexOf(command, StringComparison.Ordinal) < block.IndexOf("ci-coverage -Profile release", StringComparison.Ordinal)
                        && block.Contains(command, StringComparison.Ordinal), command);
                Assert.Contains("$PSNativeCommandUseErrorActionPreference = $true", block);
                Assert.Contains("set -euo pipefail", block);
                // This excluded build writes a tracked generated record. Bind the
                // unchanged source first; adoption still requires the entire job.
                Assert.True(block.IndexOf("ci-coverage -Profile release", StringComparison.Ordinal) < block.IndexOf("build-configurator -Test", StringComparison.Ordinal));
            }
            if (name != "foundation") continue;
            Assert.Contains("-Shards 8 -GateOnly -RunnerResults", block);
            var runner = workflow.Split("\n  foundation-suites:", StringSplitOptions.None)[1].Split("\n  foundation-suites-gate:", StringSplitOptions.None)[0];
            Assert.Equal(profile.Suites.Order(), Regex.Matches(runner, @"-Suite ([a-z0-9-]+)").Select(match => match.Groups[1].Value).Order());
            Assert.Contains("-Shards 8 -MaxParallelism 2 -RunnerIndex ${{ matrix.runner }} -RunnerCount 4", runner);
            Assert.Contains("runner: [0, 1, 2, 3]", runner);
        }
    }

    [Fact]
    public void EveryPlannedJobIsEitherLocalOrBoundToOneNamedCiJobAndNoModeKeepsTheOriginalActions()
    {
        var candidate = CiCoverage.Validate(Root, Candidate(Root, "release", Sha), Sha);
        int localCalls = 0, coveredCalls = 0;
        var jobs = CiCoverage.Suites(candidate).Keys.SelectMany(suite => new[]
        {
            new CheckJob(suite + "-shard-0", () => { coveredCalls++; return new(0, "unexpected local execution", ""); }, []),
            new CheckJob(suite + "-gate", () => { coveredCalls++; return new(0, "unexpected local gate", ""); }, [suite + "-shard-0"])
        }).Concat(CiCoverage.Profiles["release"].Checks.Select(name => new CheckJob(name, () => { coveredCalls++; return new(0, "unexpected local check", ""); }, [])))
            .Concat(new[] { "local", "repository", "bundle" }.Select(name => new CheckJob(name, () => { Interlocked.Increment(ref localCalls); return new(0, "local", ""); }, []))).ToArray();
        var unchanged = CiCoverage.Apply(jobs, null);
        Assert.Equal(jobs, unchanged);
        Assert.All(unchanged, job => Assert.Null(job.CiCoverage));
        using var temp = new OfflineFixtures();
        var mapped = CiCoverage.Apply(jobs, candidate);
        Assert.Equal(jobs.Select(job => job.Name), mapped.Select(job => job.Name));
        Assert.All(mapped, job => Assert.Equal(job.Name is "local" or "repository" or "bundle", job.CiCoverage == null));
        var rows = CheckScheduler.Run(mapped, 2, temp.DirectoryPath);
        Assert.All(rows, row => Assert.True(CheckTiming.Passed(row)));
        Assert.Equal(19, rows.Count(row => row.Status == "covered by CI"));
        Assert.Equal(0, coveredCalls); Assert.Equal(3, localCalls);
        Assert.Throws<ReleaseException>(() => CiCoverage.Apply(jobs.Where(job => job.Name != "release-tool-gate").ToArray(), candidate));
        Assert.False(CheckTiming.Passed(new("forged", "covered by CI", 0, 0, null)));
    }

    [Theory]
    [InlineData("missing-suite")]
    [InlineData("extra-suite")]
    [InlineData("filter")]
    [InlineData("minimum")]
    [InlineData("skips")]
    [InlineData("failure")]
    [InlineData("counts")]
    [InlineData("commit")]
    [InlineData("platform")]
    [InlineData("workflow")]
    [InlineData("duplicate")]
    public void OfflineCandidatesMustHaveExactRegistrationsAndAllUnchangedGates(string change)
    {
        var candidate = Candidate(Root, "release", Sha);
        var receipt = candidate["jobs"]![0]!["receipt"]!;
        var suites = receipt["suites"]!.AsObject();
        var row = suites["release-tool"]!;
        switch (change)
        {
            case "missing-suite": suites.Remove("release-tool"); break;
            case "extra-suite": suites["unknown"] = row.DeepClone(); break;
            case "filter": row["registration"]!["arguments"] = new JsonArray("--filter", "Name=one"); break;
            case "minimum": row["summary"]!["minimumPassed"] = 1; break;
            case "skips": row["summary"]!["skipped"] = 1; break;
            case "failure": row["summary"]!["failed"] = 1; break;
            case "counts": row["summary"]!["total"] = 0; break;
            case "commit": receipt["commit"] = new string('2', 40); break;
            case "platform": receipt["platform"] = "linux"; break;
            case "workflow": receipt["workflowSha256"] = new string('0', 64); break;
            case "duplicate": candidate["jobs"]!.AsArray().Add(candidate["jobs"]![0]!.DeepClone()); break;
        }
        Assert.Throws<ReleaseException>(() => CiCoverage.Validate(Root, candidate, Sha));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("running")]
    [InlineData("commit")]
    [InlineData("attempt")]
    [InlineData("run")]
    [InlineData("latest-attempt")]
    [InlineData("log-missing")]
    [InlineData("log-counts")]
    public void AdoptionRefusesEveryUnprovenNamedJob(string change)
    {
        var candidate = CiCoverage.Validate(Root, Candidate(Root, "release", Sha), Sha);
        Assert.Throws<ReleaseException>(() => VerifyRemote(candidate, change));
    }

    [Fact]
    public void AdoptionAcceptsOnlyTheExactReceiptInTheSuccessfulSameCommitAttemptJobLog()
    {
        var candidate = CiCoverage.Validate(Root, Candidate(Root, "release", Sha), Sha);
        var runs = VerifyRemote(candidate);
        Assert.Equal(101L, (long)Assert.Single(runs)!["runId"]!);
        Assert.Equal(2, ReleaseEvidence.Int(runs[0]!["runAttempt"]));
    }

    internal static JsonArray VerifyRemote(JsonObject candidate, string change = "")
    {
        var receipt = candidate["jobs"]![0]!["receipt"]!;
        var workflow = CiCoverage.Profiles[ReleaseEvidence.Text(receipt["profile"])].Workflow;
        var sha = ReleaseEvidence.Text(receipt["commit"]);
        var run = new JsonObject { ["id"] = 101L, ["name"] = workflow, ["head_sha"] = sha, ["event"] = "push", ["head_branch"] = "master",
            ["status"] = "completed", ["conclusion"] = "success", ["run_number"] = 10, ["run_attempt"] = change == "latest-attempt" ? 3 : 2 };
        var job = new JsonObject { ["id"] = 301L, ["name"] = receipt["jobName"]!.DeepClone(), ["head_sha"] = change == "commit" ? new string('2', 40) : sha,
            ["run_id"] = change == "run" ? 102L : 101L, ["run_attempt"] = change == "attempt" ? 1 : 2,
            ["status"] = change == "running" ? "in_progress" : "completed", ["conclusion"] = change == "failed" ? "failure" : change == "cancelled" ? "cancelled" : "success" };
        var logged = receipt.DeepClone().AsObject();
        if (change == "log-counts") logged["suites"]!["release-tool"]!["summary"]!["passed"] = 1;
        string Get(string url)
        {
            if (url.EndsWith("/logs", StringComparison.Ordinal))
            {
                Assert.Equal("repos/owner/repo/actions/jobs/301/logs", url);
                return change == "log-missing" ? "no receipt" : "2026-10-27T00:00:00Z " + CiCoverage.Encode(logged) + "\n";
            }
            if (url.Contains("/jobs?", StringComparison.Ordinal)) return new JsonObject { ["total_count"] = change == "missing" ? 0 : 1,
                ["jobs"] = change == "missing" ? new JsonArray() : new JsonArray(job.DeepClone()) }.ToJsonString();
            return new JsonObject { ["total_count"] = 1, ["workflow_runs"] = new JsonArray(run.DeepClone()) }.ToJsonString();
        }
        return ReleaseEvidence.VerifyCi(Get, "owner/repo", sha, [workflow], candidate);
    }
}
