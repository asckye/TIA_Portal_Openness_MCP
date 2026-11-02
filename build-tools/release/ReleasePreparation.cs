using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record CandidatePreparation(string Commit, string Tree, string EvidencePath, Task<CommandResult>? EvidenceRun);

internal static partial class ReleaseCommands
{
    internal static (ulong? Evidence, ulong? Candidate) PreparationCpuMasks(ulong available)
    {
        var count = System.Numerics.BitOperations.PopCount(available);
        if (count < 4) return (null, null);
        ulong evidence = 0; var remaining = count / 2;
        for (var bit = 0; bit < 64 && remaining > 0; bit++)
            if ((available & (1UL << bit)) != 0) { evidence |= 1UL << bit; remaining--; }
        return (evidence, available & ~evidence);
    }

    private static int PrepareReleaseCandidate(Options options)
    {
        var version = ReleaseVersions.Require(options.Get("Version"), "release-prepare requires -Version X.Y.Z[-beta.N|-rc.N]").Label;
        if (options.Has("SkipBuild") || options.Has("Resume") || options.Has("BumpOnly") || options.Has("EarlyGatesOnly") ||
            options.Has("KillStrayEngine") || ReleaseCheckPolicy.Tier(options.Get("Tier")) != "full" || ReleasePlan(options).Tier != "full")
            throw new ReleaseException("release-prepare requires the complete full candidate pipeline");
        var git = options.Get("Git", "git");
        var state = ReleaseEvidence.SourceState(Root, git);
        if (state.Status.Length != 0) throw new ReleaseException("Commit the version bump, CHANGELOG and notes before release-prepare");
        if (!options.Has("NoPush") && !options.Has("DryRun") && GitText(git, ["rev-parse", "--abbrev-ref", "HEAD"]) != "master")
            throw new ReleaseException("Preparation pushes only from master; use -NoPush for a detached rehearsal");
        if (ReleaseEvidence.PrepCommitError(ReleaseVersions.ReadVersionProps(Root).Label, version) is { } error) throw new ReleaseException(error);
        Prerequisites(Options.Parse([.. options.Arguments(), "-Offline"], new HashSet<string> { "Offline" }), publicationCredentials: false);
        var directory = Path.Combine(Root, "bin-build/release-preparation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidence = options.Get("AdoptEvidence");
        Task<CommandResult>? run = null;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var masks = OperatingSystem.IsWindows() ? PreparationCpuMasks(unchecked((ulong)process.ProcessorAffinity.ToInt64())) : (null, (ulong?)null);
        if (evidence is null)
        {
            var output = Path.Combine(directory, "evidence");
            var ready = Path.Combine(directory, "snapshot-ready.txt");
            var args = new List<string> { typeof(ReleaseCommands).Assembly.Location, "check-all", "-Profile", "nightly", "-Clean", "-NoBuildCache", "-NoResultCache",
                "-PublicApiRoot", ResolveReleaseApiRoot(options), "-OutputDirectory", output, "-SnapshotReadyFile", ready };
            foreach (var name in new[] { "NuGetConfig", "Python", "Dotnet", "MaxParallelism", "Shards" })
                if (options.Get(name) is { } value) args.AddRange(["-" + name, value]);
            if (options.Get("EcosystemPython") is { } companion) args.AddRange(["-CompanionPython", companion]);
            // check-all copies the prep tree before signalling readiness. Its private outputs
            // cannot race with the publication build or observe the rewritten records.
            run = Task.Run(() =>
            {
                using var affinity = CheckAffinity.Enter(masks.Item1);
                return ProcessRunner.Run(Dotnet, args, Root, new Dictionary<string, string?> { [ReleaseEvidence.AdoptionVariable] = null },
                    logPath: Path.Combine(directory, "check-all.log"));
            });
            while (!File.Exists(ready) && !run.IsCompleted) Thread.Sleep(100);
            if (!File.Exists(ready))
            {
                ProcessRunner.RequireSuccess(run.GetAwaiter().GetResult(), "Preparation evidence snapshot");
                throw new ReleaseException("Preparation evidence child did not confirm its isolated source snapshot");
            }
            evidence = Path.Combine(output, ReleaseEvidence.FileName);
            // On small hosts run the evidence first; otherwise the soaks and candidate
            // checks have disjoint CPU jobs, including every descendant process.
            if (masks.Item2 is null) ProcessRunner.RequireSuccess(run.GetAwaiter().GetResult(), "Preparation evidence on a small host");
        }
        var previousCache = Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED");
        var previousAdoption = Environment.GetEnvironmentVariable(ReleaseEvidence.AdoptionVariable);
        Environment.SetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED", "1");
        Environment.SetEnvironmentVariable(ReleaseEvidence.AdoptionVariable, null);
        try
        {
            using var affinity = CheckAffinity.Enter(run is null ? null : masks.Item2);
            return RunReleaseProduction(options, new(state.Commit, state.Tree, Path.GetFullPath(evidence), run));
        }
        finally
        {
            // Own and join the evidence child even when packaging fails. No process-name cleanup.
            Environment.SetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED", previousCache);
            Environment.SetEnvironmentVariable(ReleaseEvidence.AdoptionVariable, previousAdoption);
            if (run is not null) ProcessRunner.RequireSuccess(run.GetAwaiter().GetResult(), "Preparation check-all evidence");
        }
    }

    private static int FinishPreparedCandidate(Options options, CandidatePreparation preparation, JsonObject? adoption,
        ReleaseCheckPlan plan, string logs, string packageDirectory, ReleaseStageClock clock, string python, string temp, string api, string frozenZipHash)
    {
        var git = options.Get("Git", "git");
        var version = options.Get("Version")!;
        var adoptedBuildChecks = adoption is not null;
        if (preparation.EvidenceRun is { } run)
            clock.Run("check-all-evidence", () => ProcessRunner.RequireSuccess(run.GetAwaiter().GetResult(), "Preparation check-all evidence"));
        if (adoption is null)
        {
            var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
            var companion = options.Get("EcosystemPython") ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
            var external = ReleaseEvidence.ProbeExternal(Root, api, nuget, Dotnet, python, companion);
            clock.Run("adopt-evidence", () => adoption = ReleaseEvidence.Verify(new(Root, git, preparation.EvidencePath,
                external, DateTimeOffset.UtcNow, TimeGateException: options.Get("AcceptTimeGateException"),
                SpotCheckColdUnits: SpotCheckColdUnits(options), AllowRecordChanges: true)));
        }
        if (!adoptedBuildChecks)
        {
            var record = adoption!["record"]!.AsObject();
            record["source"] = "Independent complete check-all plus a complete candidate build/check run; no build checks were skipped by adoption";
            record["adoptedChecks"] = new JsonArray();
            var ran = new JsonObject();
            foreach (var job in PlcReleaseJobs(Root, api, Path.Combine(Root, "bin-build/multi-version"), temp, Dotnet, python)
                .Where(job => IsAdoptionBinaryPlcJob(job) || job.Name.StartsWith("harness-", StringComparison.Ordinal) || job.Mode == "capture"))
                ran[job.Name] = new JsonObject { ["mode"] = "ran", ["reason"] = "Complete preparation without -AdoptEvidence" };
            record["binaryChecks"] = ran;
        }
        var adoptionPath = Path.Combine(logs, "candidate-adoption.json");
        WriteJson(adoptionPath, adoption!);
        clock.Run("candidate-record-only-diff", () =>
        {
            if (GitText(git, ["rev-parse", "HEAD"]) != preparation.Commit) throw new ReleaseException("HEAD moved during preparation");
            ReleaseCandidate.RequireRecordOnly(EnumerateReleaseChanges(Root, git));
        });
        var resultPath = Path.Combine(packageDirectory, "package-result.json");
        var result = ReleaseCandidate.Read(resultPath);
        var zip = Path.GetFullPath(ReleaseEvidence.Text(result["path"]));
        clock.Run("candidate-record-bindings", () => ReleaseCandidate.RequirePackage(Root, zip));
        var dryRun = options.Has("DryRun");
        if (!dryRun && EnumerateReleaseChanges(Root, git).Length != 0)
            CommitReleaseChanges(Root, git, "Prepare release " + version + ": verified package records");
        var state = ReleaseEvidence.SourceState(Root, git);
        clock.Run("verify-package", () => RunReleaseExternal(python,
            [Path.Combine(Root, "scripts/checks/Verify-ReleaseAsset.py"), zip, "--git", ResolveApplication(git), .. dryRun ? new[] { "--working-tree" } : []],
            "verify-package", logs, temp, api));
        if (ReleaseRecords.HashFile(zip) != frozenZipHash) throw new ReleaseException("Candidate ZIP changed before sealing");
        var receipts = new JsonArray();
        foreach (var directory in new[] { "stage-receipts", "check-receipts" })
            foreach (var file in Directory.GetFiles(Path.Combine(logs, directory), "*.json").Order(StringComparer.Ordinal))
            {
                var receipt = ReleaseCandidate.Read(file);
                if (ReleaseEvidence.Text(receipt["status"]) != "passed") throw new ReleaseException("Preparation check failed: " + file);
                var row = ReleaseCandidate.FileBinding(Root, file);
                row["name"] = receipt["name"]!.DeepClone(); row["status"] = "passed";
                receipts.Add(row);
                if (receipt["log"] is { } log) receipts.Add(LogReceipt(log));
            }
        JsonObject LogReceipt(JsonNode log)
        {
            var row = log.DeepClone().AsObject(); row["name"] = "log:" + ReleaseEvidence.Text(row["path"]); row["status"] = "passed"; return row;
        }
        var tool = Path.Combine(logs, "publication-tool"); Directory.CreateDirectory(tool);
        var originalTool = Path.GetDirectoryName(typeof(ReleaseCommands).Assembly.Location)!;
        foreach (var row in BuildReproducibility.Inventory(originalTool))
        {
            var target = ReleaseCandidate.Resolve(tool, row.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(ReleaseCandidate.Resolve(originalTool, row.Path), target);
        }
        var evidenceDirectory = Path.GetDirectoryName(preparation.EvidencePath)!;
        var evidenceFiles = Directory.EnumerateFiles(evidenceDirectory, "*", SearchOption.AllDirectories).Where(file =>
        {
            var relative = Path.GetRelativePath(evidenceDirectory, file).Replace('\\', '/');
            return !relative.StartsWith("build/", StringComparison.Ordinal) && !relative.StartsWith("isolation/", StringComparison.Ordinal) &&
                !relative.StartsWith("suite-output-parity/", StringComparison.Ordinal) && Path.GetExtension(file) is ".json" or ".log" or ".trx";
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void BindRecordedArtifact(JsonNode? node)
        {
            if (node is not JsonObject row || ReleaseEvidence.Text(row["path"]) is not { Length: > 0 } relative) return;
            var file = ReleaseCandidate.Resolve(Root, relative);
            if (!File.Exists(file) || ReleaseRecords.HashFile(file) != ReleaseEvidence.Text(row["sha256"]))
                throw new ReleaseException("Prepared validation artifact changed: " + relative);
            evidenceFiles.Add(file);
            if (row["artifacts"] is JsonArray artifacts) foreach (var artifact in artifacts) BindRecordedArtifact(artifact);
        }
        foreach (var record in ReleaseCandidate.RecordPaths.Where(path => path.StartsWith("manifest/", StringComparison.Ordinal)))
            if (File.Exists(Path.Combine(Root, record)) && ReleaseCandidate.Read(Path.Combine(Root, record))["validationArtifacts"] is JsonArray artifacts)
                foreach (var artifact in artifacts) BindRecordedArtifact(artifact);
        foreach (var check in adoption!["record"]!["localChecks"]!.AsArray()) BindRecordedArtifact(check);
        var candidate = new JsonObject
        {
            ["schema"] = ReleaseCandidate.Schema, ["status"] = dryRun ? "dry-run" : "passed", ["version"] = version,
            ["prepCommit"] = preparation.Commit, ["prepTree"] = preparation.Tree, ["commit"] = state.Commit, ["tree"] = state.Tree,
            ["branch"] = GitText(git, ["rev-parse", "--abbrev-ref", "HEAD"]),
            ["inputsSha256"] = ReleaseEvidence.Digest(ReleaseEvidence.Inventory(Root, git)),
            ["package"] = ReleaseCandidate.FileBinding(Root, zip), ["sidecar"] = ReleaseCandidate.FileBinding(Root, Path.ChangeExtension(zip, ".sha256")),
            ["packageResult"] = ReleaseCandidate.FileBinding(Root, resultPath), ["binaryHashes"] = ReleaseCandidate.PackageInventory(zip),
            ["evidence"] = ReleaseCandidate.FileBinding(Root, preparation.EvidencePath), ["adoption"] = ReleaseCandidate.FileBinding(Root, adoptionPath),
            ["records"] = new JsonArray(ReleaseCandidate.RecordPaths.Where(path => File.Exists(Path.Combine(Root, path)))
                .Select(path => (JsonNode?)ReleaseCandidate.FileBinding(Root, Path.Combine(Root, path))).ToArray()),
            ["checks"] = ReleaseEvidence.Strings(plan.SelectedChecks), ["receipts"] = receipts,
            ["toolFiles"] = new JsonArray(Directory.GetFiles(tool, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (JsonNode?)ReleaseCandidate.FileBinding(Root, file)).ToArray()),
            ["evidenceFiles"] = new JsonArray(evidenceFiles.Order(StringComparer.Ordinal).Select(file => (JsonNode?)ReleaseCandidate.FileBinding(Root, file)).ToArray())
        };
        candidate["binaryHashesSha256"] = ReleaseEvidence.Digest(candidate["binaryHashes"]!.AsObject()
            .Select(pair => KeyValuePair.Create(pair.Key, ReleaseEvidence.Text(pair.Value))));
        var candidatePath = Path.Combine(packageDirectory, "release-candidate.json");
        WriteJson(candidatePath, candidate);
        if (!dryRun) ReleaseCandidate.Verify(Root, git, candidate, requireBranch: false);
        if (!dryRun && !options.Has("NoPush")) RunGitChecked(git, ["push", "origin", "HEAD:master"], "push verified preparation records");
        Console.WriteLine("Prepared immutable candidate: " + candidatePath + (dryRun ? " (dry run; cannot publish)" : ""));
        Console.WriteLine("Record-only release-day checks: " + string.Join(", ", ReleaseCandidate.RecordChecks));
        clock.Print();
        return 0;
    }
}
