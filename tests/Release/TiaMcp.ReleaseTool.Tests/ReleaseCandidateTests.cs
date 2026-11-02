using System.IO.Compression;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseCandidateTests
{
    [Theory]
    [InlineData(0xFFUL)]
    [InlineData(0x55AAUL)]
    [InlineData(0xF00000000000000FUL)]
    public void ConcurrentPreparationJobsHaveDisjointCpuBudgets(ulong available)
    {
        var masks = ReleaseCommands.PreparationCpuMasks(available);
        Assert.Equal(0UL, masks.Evidence!.Value & masks.Candidate!.Value);
        Assert.Equal(available, masks.Evidence.Value | masks.Candidate.Value);
        Assert.Equal((null, (ulong?)null), ReleaseCommands.PreparationCpuMasks(3));
    }

    [Theory]
    [InlineData("commit", "tree", "master", true)]
    [InlineData("other", "tree", "master", false)]
    [InlineData("commit", "other", "master", false)]
    [InlineData("commit", "tree", "topic", false)]
    public void PublicationBindsTheExactBranchTreeAndCommit(string commit, string tree, string branch, bool accepted)
    {
        var candidate = new JsonObject { ["commit"] = "commit", ["tree"] = "tree", ["branch"] = "master" };
        if (accepted) ReleaseCandidate.RequireSource(candidate, commit, tree, branch, true);
        else Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequireSource(candidate, commit, tree, branch, true));
    }

    [Theory]
    [InlineData("manifest/release-build.json", true)]
    [InlineData("docs/reference/tool-matrix.md", true)]
    [InlineData("manifest/new-policy.json", false)]
    [InlineData("Version.props", false)]
    [InlineData("build-tools/release/ReleaseCandidate.cs", false)]
    [InlineData("docs/releases/v4.0.0.md", false)]
    public void RecordOnlyPreflightUsesAnExactAllowList(string path, bool accepted)
    {
        if (accepted) ReleaseCandidate.RequireRecordOnly([path]);
        else Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequireRecordOnly([path]));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../outside")]
    [InlineData("a\\outside")]
    [InlineData("a//outside")]
    public void CandidateFileBindingsCannotEscapeTheirRoot(string path)
    {
        using var tree = new StaticTree();
        Assert.Throws<ReleaseException>(() => ReleaseCandidate.Resolve(tree.Root, path));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("--output=outside")]
    [InlineData("abc123")]
    public void CandidatePrepMustBeAnObjectIdRatherThanGitOptionsOrMutableRefs(string value)
    {
        ReleaseCandidate.RequireCommitId(new string('a', 40));
        ReleaseCandidate.RequireCommitId(new string('b', 64));
        Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequireCommitId(value));
    }

    [Theory]
    [InlineData("4.0.0-beta.3", "4.0.0-beta.3", true)]
    [InlineData("4.0.0-beta.4", "4.0.0-beta.3", false)]
    [InlineData("4.0.0-beta.3", "4.0.0-beta.4", false)]
    public void CandidateVersionCannotCreateATagForAnotherVersion(string properties, string delivery, bool accepted)
    {
        if (accepted) ReleaseCandidate.RequireVersion("4.0.0-beta.3", properties, delivery);
        else Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequireVersion("4.0.0-beta.3", properties, delivery));
    }

    [Fact]
    public void BindingRejectsSameLengthTamperingMissingFilesAndSizeChanges()
    {
        using var tree = new StaticTree();
        var file = tree.Write("package.zip", "original");
        var receipt = ReleaseCandidate.FileBinding(tree.Root, file);
        ReleaseCandidate.VerifyBinding(tree.Root, receipt);
        File.WriteAllText(file, "modified");
        Assert.Throws<ReleaseException>(() => ReleaseCandidate.VerifyBinding(tree.Root, receipt));
        File.WriteAllText(file, "original-more");
        Assert.Throws<ReleaseException>(() => ReleaseCandidate.VerifyBinding(tree.Root, receipt));
        File.Delete(file);
        Assert.Throws<ReleaseException>(() => ReleaseCandidate.VerifyBinding(tree.Root, receipt));
    }

    [Theory]
    [InlineData("passed", true, true)]
    [InlineData("passed", false, false)]
    [InlineData("failed", true, false)]
    [InlineData("timeout", false, false)]
    [InlineData("running", true, false)]
    public void NightlyFailuresAndTimeoutsBlockPublication(string status, bool underThirtyMinutes, bool accepted)
    {
        var latest = new JsonObject { ["rev"] = "ancestor", ["status"] = status, ["underThirtyMinutes"] = underThirtyMinutes };
        if (accepted) CandidatePublicationGate.RequireNightly(latest, revision => revision == "ancestor");
        else Assert.Throws<ReleaseException>(() => CandidatePublicationGate.RequireNightly(latest, _ => true));
        CandidatePublicationGate.RequireNightly(latest, _ => false);
    }

    [Fact]
    public void AnUnresolvedPostPublicationFailureBlocksTheNextRelease()
    {
        var record = new JsonObject { ["version"] = "4.0.0-beta.1", ["status"] = "failed" };
        Assert.Contains("4.0.0-beta.1", Assert.Throws<ReleaseException>(() => CandidatePublicationGate.RequirePublication([record])).Message);
        record["status"] = "passed";
        CandidatePublicationGate.RequirePublication([record]);
    }

    [Fact]
    public void AsynchronousWorkflowReadUsesTheLatestAttemptOfTheExactCommitAndTag()
    {
        JsonObject Run(string commit, string title, int number, int attempt, string status, string conclusion) => new()
        {
            ["head_sha"] = commit, ["name"] = "Verify published release", ["display_title"] = title,
            ["run_number"] = number, ["run_attempt"] = attempt, ["status"] = status, ["conclusion"] = conclusion
        };
        var runs = new JsonArray(Run("sha", "Verify v4.0.0", 1, 1, "completed", "failure"),
            Run("other", "Verify v4.0.0", 9, 1, "completed", "success"), Run("sha", "Verify v3.0.0", 10, 1, "completed", "success"));
        string Status() => CandidatePublicationGate.WorkflowStatus(runs, "sha", "Verify published release", "Verify v4.0.0");
        Assert.Equal("failed", Status());
        runs.Add(Run("sha", "Verify v4.0.0", 1, 2, "in_progress", "")); Assert.Equal("pending", Status());
        runs[3]!["status"] = "completed"; runs[3]!["conclusion"] = "success"; Assert.Equal("passed", Status());
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("failure", false)]
    [InlineData("skipped", false)]
    [InlineData("cancelled", false)]
    public void PostPublicationChecksRequireEveryWorkflowJobToPass(string conclusion, bool accepted)
    {
        Assert.False(CandidatePublicationGate.JobsPassed([]));
        Assert.Equal(accepted, CandidatePublicationGate.JobsPassed(new JsonArray(new JsonObject { ["status"] = "completed", ["conclusion"] = conclusion })));
    }

    [Fact]
    public void PublicationRecordPreservesTheFactsTheNextPrepUsesToAcceptRecordLag()
    {
        using var tree = new StaticTree();
        var published = new JsonObject { ["published_at"] = "2026-10-30T19:00:52Z", ["html_url"] = "https://example.invalid/release" };
        var package = new JsonObject { ["path"] = "bundle.zip", ["size"] = 10, ["files"] = 2, ["sha256"] = new string('a', 64) };
        var record = CandidatePublicationGate.PublicationFacts("4.0.0-beta.3", "commit", published, package);
        tree.Write("manifest/publication-v4.0.0-beta.3.json", record.ToJsonString());
        Assert.True(ReleaseVersions.IsPublishedRecordLag(tree.Root, "4.0.0-beta.4", "4.0.0-beta.3"));
        Assert.False((bool)record["latest"]!); Assert.True((bool)record["prerelease"]!);
        Assert.Equal("commit", (string)record["sourceCommit"]!);
        published.Remove("published_at");
        Assert.Throws<ReleaseException>(() => CandidatePublicationGate.PublicationFacts("4.0.0-beta.3", "commit", published, package));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("worker")]
    [InlineData("harness")]
    public void BinaryAdoptionComparesEveryDependencyAndInventory(string role)
    {
        using var tree = new StaticTree();
        var files = new Dictionary<string, string>();
        foreach (var name in new[] { "host", "worker", "harness" })
        {
            files[name] = tree.Write(name + "/app.exe", name);
            tree.Write(name + "/dependency.dll", "dependency");
        }
        var receipt = new JsonObject { ["schema"] = BinaryCheckReceipts.Schema, ["status"] = "passed", ["binaries"] = BinaryCheckReceipts.Binaries(files) };
        Assert.True(BinaryCheckReceipts.Matches(receipt, BinaryCheckReceipts.Binaries(files)));
        tree.Write("host/worker/unrelated-production-worker.exe", "separately selected product");
        Assert.True(BinaryCheckReceipts.Matches(receipt, BinaryCheckReceipts.Binaries(files)));
        var dependency = tree.Write(role + "/dependency.dll", "changed");
        Assert.False(BinaryCheckReceipts.Matches(receipt, BinaryCheckReceipts.Binaries(files)));
        File.WriteAllText(dependency, "dependency"); Assert.True(BinaryCheckReceipts.Matches(receipt, BinaryCheckReceipts.Binaries(files)));
        var added = tree.Write(role + "/new.DLL", "new"); Assert.False(BinaryCheckReceipts.Matches(receipt, BinaryCheckReceipts.Binaries(files)));
        File.Delete(added); File.Delete(dependency); Assert.False(BinaryCheckReceipts.Matches(receipt, BinaryCheckReceipts.Binaries(files)));
    }

    [Fact]
    public void AFailedOrMutatingBinaryJobCannotLeaveAnAdoptableReceipt()
    {
        using var tree = new StaticTree();
        var app = tree.Write("built/app.exe", "binary");
        var output = Path.Combine(tree.Root, "run"); Directory.CreateDirectory(output);
        int code = 1; bool mutate = false;
        var job = BinaryCheckReceipts.Bind(new("probe", () =>
        {
            if (mutate) File.AppendAllText(app, "changed");
            return new(code, "passed-looking output", "");
        }, []), tree.Root, output, new Dictionary<string, string> { ["host"] = app });
        var path = Path.Combine(output, "binary-receipts/probe.json");
        Assert.Equal(1, job.Run().ExitCode); Assert.False(File.Exists(path));
        code = 0; Assert.Equal(0, job.Run().ExitCode); Assert.True(File.Exists(path));
        mutate = true; Assert.Throws<ReleaseException>(() => job.Run()); Assert.False(File.Exists(path));
    }

    [Fact]
    public void WorkerProtocolIsAdoptedOnlyForIdenticalBytesOtherwiseTheJobRuns()
    {
        using var tree = new StaticTree();
        var root = tree.Root; var output = Path.Combine(root, "evidence"); Directory.CreateDirectory(output);
        var logs = Path.Combine(root, "bin-build/multi-version"); Directory.CreateDirectory(logs);
        var host = tree.Write("runtime/v20/TiaMcp.FoundationHost.exe", "host");
        var worker = new PlcWorkerFixtureBuild("20", "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"));
        Directory.CreateDirectory(Path.GetDirectoryName(worker)!); File.WriteAllText(worker, "worker");
        var harness = tree.Write("harness/app.exe", "harness");
        var binaryInputs = new Dictionary<string, string> { ["host"] = host, ["worker"] = worker, ["harness"] = harness };
        var sourceName = "transport-usage-v20-plc";
        tree.Write("evidence/transport-usage-v20-plc/result.json", "{\"nativeTiaExecuted\":false}");
        var job = BinaryCheckReceipts.Bind(new(sourceName, () => new(0, "COMPLETE: 999 Foundation transport checks passed\n", ""), []), root, output, binaryInputs);
        Assert.Equal(0, job.Run().ExitCode);
        var sourceLog = tree.Write("evidence/logs/" + sourceName + ".log", "COMPLETE: 999 Foundation transport checks passed\n");
        var evidence = new JsonObject
        {
            ["runId"] = "nightly-1", ["binaryReceipts"] = new JsonObject { [sourceName] = ReleaseCandidate.FileBinding(output, Path.Combine(output, "binary-receipts", sourceName + ".json")) },
            ["jobs"] = new JsonArray(new JsonObject { ["name"] = sourceName, ["status"] = "passed", ["reused"] = false, ["logSha256"] = ReleaseRecords.HashFile(sourceLog) })
        };
        var path = tree.Write("evidence/release-evidence.json", evidence.ToJsonString());
        var target = ReleaseCommands.PlcReleaseJobs(root, root, logs, root, "dotnet", "python").Single(job => job.Release == "20" && job.Mode == "worker-protocol");
        var decisions = new JsonObject();
        Assert.True(BinaryCheckReceipts.TryAdopt(path, target, root, logs, harness, decisions));
        Assert.Equal("nightly-1", (string)decisions[target.Name]!["sourceRunId"]!);
        File.AppendAllText(worker, "changed");
        Assert.False(BinaryCheckReceipts.TryAdopt(path, target, root, logs, harness, decisions));
        Assert.Equal("ran", (string)decisions[target.Name]!["mode"]!);
        File.WriteAllText(worker, "worker"); File.AppendAllText(sourceLog, "tampered");
        Assert.Throws<ReleaseException>(() => BinaryCheckReceipts.TryAdopt(path, target, root, logs, harness, decisions));
    }

    [Fact]
    public void CandidateInventoryIncludesAllRuntimeBytesAndRootExecutables()
    {
        using var tree = new StaticTree();
        var zip = Path.Combine(tree.Root, "bundle.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var path in new[] { "runtime/host.exe", "runtime/dependency.dll", "runtime/release-key.txt", "launcher.exe", "docs/readme.md" })
            {
                using var writer = new StreamWriter(archive.CreateEntry("bundle/" + path).Open()); writer.Write(path);
            }
        var rows = ReleaseCandidate.PackageInventory(zip);
        Assert.Equal(4, rows.Count); Assert.False(rows.ContainsKey("docs/readme.md"));
        Assert.All(rows, row => Assert.Matches("^[0-9a-f]{64}$", (string)row.Value!));
    }

    [Fact]
    public void CandidateChecksCannotUseDifferentRuntimeBytesAndUpdaterMapsToItsBuildOutput()
    {
        using var tree = new StaticTree();
        var host = tree.Write("runtime/v20/host.exe", "host");
        var updater = tree.Write("bin-build/updater/TiaMcp.Updater.exe", "updater");
        var hashes = new JsonObject { ["runtime/v20/host.exe"] = ReleaseRecords.HashFile(host), ["runtime/tools/TiaMcp.Updater.exe"] = ReleaseRecords.HashFile(updater) };
        ReleaseCandidate.RequireRuntimeFiles(tree.Root, hashes);
        File.AppendAllText(host, "changed"); Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequireRuntimeFiles(tree.Root, hashes));
        File.WriteAllText(host, "host"); File.AppendAllText(updater, "changed");
        Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequireRuntimeFiles(tree.Root, hashes));
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("passed", true)]
    [InlineData("quick", false)]
    [InlineData("omitted", false)]
    [InlineData("cached", false)]
    [InlineData("ran", false)]
    public void ExternalCandidateReceiptPreservesTheFullColdPlanAndLegacyPublisherStillRefusesPending(string variant, bool accepted)
    {
        using var tree = new StaticTree();
        var root = Repository.FindRoot(); var checks = ReleaseCheckPolicy.Load(root).Select("full", null).SelectedChecks;
        var tier = new JsonObject
        {
            ["tier"] = variant == "quick" ? "quick" : "full", ["checkStatus"] = variant == "passed" ? "passed" : "pending",
            ["buildCacheEnabled"] = variant == "cached", ["checksSelected"] = ReleaseEvidence.Strings(checks),
            ["checksRan"] = ReleaseEvidence.Strings(variant is "passed" or "ran" ? checks : []), ["checksSkipped"] = new JsonArray()
        };
        if (variant == "omitted") tier["checksSelected"]!.AsArray().RemoveAt(0);
        var zip = Path.Combine(tree.Root, "bundle.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("bundle/manifest/package-manifest.json").Open())) writer.Write(tier.ToJsonString());
        if (accepted) ReleaseCandidate.RequirePackage(root, zip);
        else Assert.Throws<ReleaseException>(() => ReleaseCandidate.RequirePackage(root, zip));
        if (variant == "pending") Assert.Throws<ReleaseException>(() => ReleaseCheckPolicy.Load(root).RequireFullPackage(zip, requireColdBuild: true));
    }

    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("zip", false)]
    [InlineData("record", false)]
    [InlineData("receipt", false)]
    [InlineData("evidence", false)]
    [InlineData("binaryHashes", false)]
    [InlineData("omittedCheck", false)]
    [InlineData("omittedReceipt", false)]
    [InlineData("wrongPrep", false)]
    [InlineData("failedReceipt", false)]
    public void SealedCandidateBindsPackageRecordsEvidenceAndEveryRequiredReceipt(string mutation, bool accepted)
    {
        using var tree = new StaticTree();
        var root = tree.Root;
        tree.Write("build-tools/release/release-checks.json", File.ReadAllText(Path.Combine(Repository.FindRoot(), "build-tools/release/release-checks.json")));
        var checks = ReleaseCheckPolicy.Load(root).Select("full", null).SelectedChecks;
        var zip = tree.Write("candidate/bundle.zip", "immutable package bytes");
        var sidecar = tree.Write("candidate/bundle.sha256", ReleaseRecords.HashFile(zip) + "  bundle.zip\n");
        var result = tree.Write("candidate/package-result.json", new JsonObject { ["path"] = zip, ["sha256"] = ReleaseRecords.HashFile(zip) }.ToJsonString());
        var evidence = tree.Write("evidence/release-evidence.json", "passed evidence");
        var adoption = tree.Write("candidate/adoption.json", new JsonObject
        {
            ["schema"] = ReleaseEvidence.AdoptionSchema,
            ["record"] = new JsonObject { ["evidence"] = new JsonObject { ["commit"] = "prep", ["sha256"] = ReleaseRecords.HashFile(evidence) } }
        }.ToJsonString());
        var receipts = new JsonArray();
        foreach (var name in ReleaseCandidate.RequiredReceipts(root).Distinct())
        {
            var file = tree.Write("receipts/" + name + ".json", new JsonObject { ["name"] = name, ["status"] = "passed" }.ToJsonString());
            var row = ReleaseCandidate.FileBinding(root, file); row["name"] = name; row["status"] = "passed"; receipts.Add(row);
        }
        var records = new JsonArray();
        foreach (var path in ReleaseCandidate.RecordPaths) records.Add(ReleaseCandidate.FileBinding(root, tree.Write(path, "record:" + path)));
        var tool = tree.Write("candidate/tool.dll", "checked tool");
        var hashes = new JsonObject { ["runtime/host.exe"] = new string('a', 64) };
        var candidate = new JsonObject
        {
            ["prepCommit"] = "prep", ["package"] = ReleaseCandidate.FileBinding(root, zip), ["sidecar"] = ReleaseCandidate.FileBinding(root, sidecar),
            ["packageResult"] = ReleaseCandidate.FileBinding(root, result), ["evidence"] = ReleaseCandidate.FileBinding(root, evidence),
            ["adoption"] = ReleaseCandidate.FileBinding(root, adoption), ["records"] = records, ["receipts"] = receipts,
            ["toolFiles"] = new JsonArray(ReleaseCandidate.FileBinding(root, tool)), ["evidenceFiles"] = new JsonArray(ReleaseCandidate.FileBinding(root, evidence)),
            ["binaryHashes"] = hashes, ["binaryHashesSha256"] = ReleaseEvidence.Digest(hashes.Select(pair => KeyValuePair.Create(pair.Key, (string)pair.Value!))),
            ["checks"] = ReleaseEvidence.Strings(checks)
        };
        switch (mutation)
        {
            case "zip": File.AppendAllText(zip, "changed"); break;
            case "record": tree.Write(ReleaseCandidate.RecordPaths[0], "changed"); break;
            case "receipt": tree.Write("receipts/early-gates.json", "changed"); break;
            case "evidence": File.AppendAllText(evidence, "changed"); break;
            case "binaryHashes": hashes["runtime/host.exe"] = new string('b', 64); break;
            case "omittedCheck": candidate["checks"]!.AsArray().RemoveAt(0); break;
            case "omittedReceipt": receipts.RemoveAt(0); break;
            case "wrongPrep": candidate["prepCommit"] = "another"; break;
            case "failedReceipt":
                var failed = tree.Write("receipts/early-gates.json", "{\"name\":\"early-gates\",\"status\":\"failed\"}");
                var failedRow = receipts.Single(row => (string?)row?["name"] == "early-gates")!;
                failedRow["sha256"] = ReleaseRecords.HashFile(failed); failedRow["length"] = new FileInfo(failed).Length;
                break;
        }
        if (accepted) ReleaseCandidate.VerifyFiles(root, candidate);
        else Assert.Throws<ReleaseException>(() => ReleaseCandidate.VerifyFiles(root, candidate));
    }
}
