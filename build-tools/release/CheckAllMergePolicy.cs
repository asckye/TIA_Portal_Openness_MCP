using System.Text.Json;

namespace TiaMcp.ReleaseTool;

// Maintainer-approved tier moves. Unknown changes retain the complete merge plan.
internal sealed class CheckAllMergePolicy(string[] changes, string? fallback)
{
    internal readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal);

    internal bool Includes(string job, string profile)
    {
        if (profile != "merge" || fallback != null) return true;
        Func<string, bool>? condition = SelfTestInputs(job) is { } inputs ? path => ExpandScripts(inputs).Any(input => CheckAllInputClosure.Covers(input, path)) : null;
        if (condition == null || changes.Any(condition)) return true;
        Excluded[job] = "merge tier: no changed input matched the approved rule; nightly/baseline/release retain the full check";
        return false;
    }

    internal CheckJob[] Apply(IEnumerable<CheckJob> jobs, string profile)
    {
        var all = jobs.ToArray();
        if (profile != "merge" || fallback != null) return all;
        var removed = all.Where(job => !Includes(job.Name, profile)).Select(job => job.Name).Concat(Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        return all.Where(job => !removed.Contains(job.Name)).Select(job => job with
            { Dependencies = job.Dependencies.Where(dep => !removed.Contains(dep)).ToArray() }).ToArray();
    }

    private static IEnumerable<string> ExpandScripts(IEnumerable<string> inputs) => inputs.SelectMany(input => input.EndsWith(".py", StringComparison.Ordinal)
        ? CheckAllScriptInputs.Closure(input) : new[] { input });

    internal static string[]? SelfTestInputs(string job)
    {
        string[]? specific = SelfTestSubjects(job);
        if (specific == null) return null;
        string? category = job switch
        {
            "tools-list-self-tests" => "Generators", "repository-self-tests" => "StaticChecks", "bundle-self-tests" => "BundleParity", _ => null
        };
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var tests = category == null ? Array.Empty<string>() : Directory.EnumerateFiles(Path.Combine(root, "tests/Release/TiaMcp.ReleaseTool.Tests"), "*.cs")
            .Where(file => File.ReadAllText(file).Contains("Trait(\"Category\", \"" + category + "\")", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).ToArray();
        var subjects = category switch
        {
            "Generators" => new[] { "CatalogData", "CatalogGenerator", "CatalogAudits", "ToolUsageGenerator", "ToolCountGenerator", "ReleaseMigrationGenerator", "PrereleaseNotes", "GeneratorCommands", "SourceRoots" },
            "StaticChecks" => new[] { "StaticChecks", "AnalyzerWarnings", "AdapterBoundary", "BundleLayoutChecks", "CommentHygiene", "DeadToolReferences", "EnvelopeRewrite", "McpText", "PythonScriptCalls", "RatchetChecks", "RepositoryChecks", "ResponseEnvelopes", "ScriptToolCalls", "SourceCheck", "SourceRoots", "SwallowedExceptions", "TiaFeatures", "CheckAllAnalyzerResult" },
            "BundleParity" => new[] { "ReleaseValidation", "ReleaseRecords", "ReleaseVersions", "ReleaseRecordSnapshot", "ReleaseAdoption", "SourceRoots", "BundleLayoutChecks", "BundleManifestRequirements", "PackageDependencies" },
            _ => Array.Empty<string>()
        };
        var shared = specific.Any(path => path.EndsWith(".cs", StringComparison.Ordinal)) ? new[] { "build-tools/common", "tests/Shared", "build-tools/release/Program.cs",
            "build-tools/release/ReleaseCommands.cs", "build-tools/release/ReleaseCommandTable.cs", "build-tools/release/SuiteHang.cs" } : Array.Empty<string>();
        return specific.Concat(tests).Concat(subjects.Select(name => "build-tools/release/" + name + ".cs")).Concat(shared).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[]? SelfTestSubjects(string job) => job switch
    {
        "tools-list-self-tests" => ["build-tools/release/ToolsListChecks.cs"],
        "repository-self-tests" => ["build-tools/release/RepositoryChecks.cs", "build-tools/release/DocumentationLinks.cs"],
        "suite-runner-self-tests" => ["build-tools/release/DotnetSuites.cs", "build-tools/release/DotnetSuiteSelfTests.cs", "build-tools/release/SuiteRunners.cs", "build-tools/release/SuiteSharding.cs", "build-tools/release/FastSuites.cs", "build-tools/release/CheckScheduler.cs"],
        "bundle-self-tests" => ["build-tools/release/BundleLayoutChecks.cs", "build-tools/release/BundleManifestRequirements.cs", "build-tools/release/PackageDependencies.cs"],
        "contract-self-tests" => ["scripts/checks/Snapshot-ToolContracts.py"],
        "campaign-runner-self-tests" or "campaign-validator-self-tests" => ["scripts/checks/Test-CampaignInputs.py", "scripts/checks/Test-CampaignInputs.cs"],
        "check-all-speedup-self-tests" => ["scripts/checks/Test-CheckAllSpeedups.py", "scripts/checks/call_latency.py", "scripts/checks/Test-FoundationTransport.py", "scripts/checks/Snapshot-ToolResponses.py"],
        "plc-script-self-tests" => ["scripts/checks/Test-PlcScriptSuccessors.py", "build-tools/release/CheckAll.cs", "build-tools/release/CheckAllInventory.cs"],
        "plc-harness-self-tests" => ["scripts/checks/Test-PlcHarnessSuccessors.py", "build-tools/release/CheckAll.cs", "build-tools/release/CheckAllInventory.cs", "tests/Engine/TiaMcp.Engine.Harness"],
        "approval-self-tests" => ["scripts/checks/Test-ReleaseApprovalGate.py"],
        "relocation-self-tests" => ["scripts/checks/Test-RelocatedBundle.py"],
        "native-lifecycle" => ["scripts/checks/Test-NativeLifecycle.py"],
        "native-mcp-session" => ["scripts/checks/Test-NativeMcpSession.py"],
        _ => null
    };

    internal static CheckJob[] UpdaterTail(IEnumerable<CheckJob> jobs)
    {
        var all = jobs.ToArray();
        var following = all.Where(job => job.Name.StartsWith("updater-", StringComparison.Ordinal)).Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var job in all.Where(job => job.Dependencies.Any(following.Contains))) changed |= following.Add(job.Name);
        } while (changed);
        var tail = all.Where(job => !following.Contains(job.Name)).Select(job => job.Name).ToArray();
        return all.Select(job => job.GlobalExclusive && job.Name.StartsWith("updater-", StringComparison.Ordinal)
            ? job with { Dependencies = job.Dependencies.Concat(tail).Distinct(StringComparer.Ordinal).ToArray(), AllowFailedDependencies = true } : job).ToArray();
    }

    internal void Write(string output) => File.WriteAllText(Path.Combine(output, "merge-tier-selection.json"),
        JsonSerializer.Serialize(new { changes, fallback, excluded = Excluded }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}

internal static class CheckAllFailureLedger
{
    internal static string RootFor(string root, string? original)
    {
        if (string.IsNullOrWhiteSpace(original)) return root;
        var snapshots = Path.GetFullPath(Path.Combine(original, "bin-build/c")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(root).StartsWith(snapshots, StringComparison.OrdinalIgnoreCase) ? original : root;
    }

    internal static void Append(string root, string output, IEnumerable<CheckTiming> timings)
    {
        var path = Path.Combine(root, "bin-build/check-failures.jsonl");
        ReleaseCommands.RequireCheckAllOwnedPath(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var mutex = new Mutex(false, "Local\\TIA-CheckFailures-" + CheckAllResultCache.Hash(Path.GetFullPath(root).ToUpperInvariant()));
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("Failure ledger is locked: " + path);
        try
        {
            foreach (var row in timings.Where(row => !CheckTiming.Passed(row)))
                File.AppendAllText(path, JsonSerializer.Serialize(new { run = Path.GetFileName(output), report = Path.Combine(output, "report.json"),
                    job = row.Name, classification = "unclassified", allowedClasses = new[] { "real", "flaky", "environment", "self-regression", "unclassified" },
                    row.Status, row.ExitCode, row.Error, recordedUtc = DateTime.UtcNow }) + "\n");
        }
        finally { mutex.ReleaseMutex(); }
    }
}
