using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record NightlyRun(string RunId, string Revision, string ReportPath, string Status, double WallSeconds,
    bool UnderThirtyMinutes, DateTime CompletedUtc, string? ReportSha256 = null, string? EvidenceSha256 = null, string? IdentitiesSha256 = null);

internal static class NightlyRuns
{
    internal static bool EvidenceRun(Options options) => options.Get("Profile", "merge") == "nightly" &&
        options.Has("NoBuildCache") && (options.Has("Clean") || options.Has("Cold")) && !options.Has("BuildOnly") &&
        !options.Has("Quick") && !options.Has("Affected");

    // The nightly command creates its run directory (owner file, log, detached worktree) before check-all writes there.
    // Only that exact managed layout counts as fresh; anything else (an earlier report or evidence) is refused.
    internal static bool ManagedFreshOutput(string output) =>
        ManagedFreshOutput(output, Environment.GetEnvironmentVariable("TIA_MCP_NIGHTLY_MANAGED"));

    internal static bool ManagedFreshOutput(string output, string? managed)
    {
        if (managed != "1") return false;
        var allowed = new HashSet<string>(["nightly-owner.json", "nightly.log", "worktree"], StringComparer.OrdinalIgnoreCase);
        return File.Exists(Path.Combine(output, "nightly-owner.json")) &&
            Directory.EnumerateFileSystemEntries(output).All(entry => allowed.Contains(Path.GetFileName(entry)));
    }

    internal static string DirectoryFor(string root) => Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_MCP_NIGHTLY_ROOT") ??
        Path.Combine(Environment.GetEnvironmentVariable("TIA_MCP_CHECK_ORIGINAL_ROOT") ?? root, "bin-build/nightly"));

    internal static void Save(string directory, NightlyRun run)
    {
        Directory.CreateDirectory(directory);
        using var mutex = new Mutex(false, "Local\\TIA-Nightly-" + CheckAllResultCache.Hash(Path.GetFullPath(directory).ToUpperInvariant()));
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromMinutes(1)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("Nightly history is locked: " + directory);
        try
        {
            var path = Path.Combine(directory, "history.json");
            var rows = File.Exists(path) ? JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(path)) ?? throw new IOException("Invalid nightly history") : [];
            if (rows.Any(row => row == null)) throw new IOException("Invalid nightly history entry");
            rows = [.. rows.Where(row => row.RunId != run.RunId || row.ReportPath != run.ReportPath), run];
            Write(path, rows.OrderBy(row => row.CompletedUtc).ToArray());
            var latest = rows.MaxBy(row => row.CompletedUtc)!;
            Write(Path.Combine(directory, "latest.json"), Pointer(latest));
            if (rows.Where(row => row.Status == "passed").MaxBy(row => row.CompletedUtc) is { } passed)
            {
                Write(Path.Combine(directory, "latest-passed.json"), Pointer(passed));
            }
            else File.Delete(Path.Combine(directory, "latest-passed.json"));
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static object Pointer(NightlyRun run) => new { runId = run.RunId, rev = run.Revision, reportPath = run.ReportPath,
        status = run.Status, wallSeconds = run.WallSeconds, underThirtyMinutes = run.UnderThirtyMinutes, completedUtc = run.CompletedUtc,
        reportSha256 = run.ReportSha256, evidenceSha256 = run.EvidenceSha256, identitiesSha256 = run.IdentitiesSha256 };

    internal static void Complete(string root, string output, string revision, int exitCode, double wallSeconds)
    {
        var report = Path.Combine(output, "report.json"); var evidence = Path.Combine(output, ReleaseEvidence.FileName);
        var identities = Path.Combine(output, "nightly-identities.json");
        bool passed = false;
        try
        {
            passed = exitCode == 0 && wallSeconds < 1800 && File.Exists(report) && File.Exists(evidence) && File.Exists(identities) &&
                JsonNode.Parse(File.ReadAllText(report)) is JsonObject reportNode && JsonNode.Parse(File.ReadAllText(evidence)) is JsonObject evidenceNode &&
                CheckAllNightlyCoverage.PassedNightly(reportNode, evidenceNode);
        }
        catch (Exception ex) when (CheckAllNightlyCoverage.InputError(ex)) { Console.Error.WriteLine("Nightly report unavailable: " + ex.Message); }
        Save(DirectoryFor(root), new(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output))), revision, report, passed ? "passed" : wallSeconds >= 1800 ? "timed-out" : "failed",
            wallSeconds, wallSeconds < 1800, DateTime.UtcNow, File.Exists(report) ? CheckAllResultCache.FileHash(report) : null,
            File.Exists(evidence) ? CheckAllResultCache.FileHash(evidence) : null, File.Exists(identities) ? CheckAllResultCache.FileHash(identities) : null));
    }

    internal static string? Block(IEnumerable<NightlyRun> history, string commit, string? previousRelease, Func<string, string, bool> ancestor)
    {
        var rows = history.ToArray();
        if (rows.Any(row => row == null || string.IsNullOrWhiteSpace(row.Revision) || string.IsNullOrWhiteSpace(row.RunId)))
            return "Cannot verify nightly release block: invalid nightly history entry";
        var relevant = rows.Where(row => ancestor(row.Revision, commit) && (row.Revision == commit || previousRelease == null ||
            row.Revision != previousRelease && ancestor(previousRelease, row.Revision))).OrderBy(row => row.CompletedUtc).ToArray();
        var last = relevant.LastOrDefault();
        return last != null && (last.Status != "passed" || !last.UnderThirtyMinutes || !double.IsFinite(last.WallSeconds) || last.WallSeconds < 0 || last.WallSeconds >= 1800)
            ? $"Release blocked by nightly {last.RunId} ({last.Status}, {last.WallSeconds:F1}s) for {last.Revision}; a later passing nightly is required. No override is available." : null;
    }

    internal static void RequireRelease(string root, string git = "git")
    {
        var directory = DirectoryFor(root); var path = Path.Combine(directory, "history.json");
        if (!File.Exists(path))
        {
            if (File.Exists(Path.Combine(directory, "latest.json"))) throw new ReleaseException("Nightly history missing while latest.json exists; restore the history before release");
            return;
        }
        try
        {
            var rows = JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(path)) ?? throw new IOException("Empty nightly history");
            var commit = ReleaseEvidence.GitText(root, git, "rev-parse", "HEAD");
            var previous = ProcessRunner.Run(git, ["describe", "--tags", "--abbrev=0", "--match", "v[0-9]*", "HEAD"], root);
            string? previousCommit = previous.ExitCode == 0 ? ReleaseEvidence.GitText(root, git, "rev-parse", previous.StandardOutput.Trim() + "^{commit}") : null;
            bool Ancestor(string left, string right)
            {
                var result = ProcessRunner.Run(git, ["merge-base", "--is-ancestor", left, right], root);
                if (result.ExitCode is not (0 or 1)) throw new IOException("Cannot resolve nightly ancestry: " + left);
                return result.ExitCode == 0;
            }
            if (Block(rows, commit, previousCommit, Ancestor) is { } error) throw new ReleaseException(error);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        { throw new ReleaseException("Cannot verify nightly release block: " + ex.Message); }
    }

    internal static void Prune(string root, string directory, int keep, DateTime now)
    {
        if (keep < 1) throw new ReleaseException("-Keep must be a positive integer", 64);
        ReleaseCommands.RequireCheckAllOwnedPath(root, directory);
        var history = JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!;
        // A killed runner cannot execute finally. Its original deadline still
        // defines the failure time; discovering it later must not revoke a
        // genuinely later passed nightly.
        foreach (var abandoned in history.Where(row => row.Status == "running" && now - row.CompletedUtc >= TimeSpan.FromMinutes(30)).ToArray())
            Save(directory, abandoned with { Status = "timed-out", WallSeconds = 1800, UnderThirtyMinutes = false, CompletedUtc = abandoned.CompletedUtc.AddMinutes(30) });
        history = JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(directory, "history.json")))!;
        var retained = history.OrderByDescending(row => row.CompletedUtc).Take(keep).Select(row => row.RunId).ToHashSet(StringComparer.Ordinal);
        // Never lose the source of identity reuse, even after newer failures.
        if (history.Where(row => row.Status == "passed").MaxBy(row => row.CompletedUtc) is { } passed) retained.Add(passed.RunId);
        foreach (var row in history.Where(row => row.Status != "running" && !retained.Contains(row.RunId) && (row.Status == "passed" || now - row.CompletedUtc >= TimeSpan.FromDays(14))))
        {
            if (Path.GetFileName(row.RunId) != row.RunId) throw new IOException("Invalid nightly run id");
            var target = Path.Combine(directory, row.RunId); ReleaseCommands.RequireCheckAllOwnedPath(directory, target);
            if (!Path.GetDirectoryName(Path.GetFullPath(row.ReportPath))!.Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(target)) continue;
            if (!File.Exists(Path.Combine(target, "nightly-owner.json"))) continue;
            var owner = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "nightly-owner.json")))!;
            if ((string?)owner["root"] != root) continue;
            // Walk without following links before a recursive delete.
            _ = ReleaseCommands.CheckAllSnapshotNewestWriteUtc(directory, target);
            Directory.Delete(target, true);
        }
    }

    internal static void Write(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, path, true);
    }
}

internal static partial class ReleaseCommands
{
    private static int Nightly(Options options)
    {
        var nuget = Path.GetFullPath(options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG") ??
            throw new ReleaseException("nightly requires an offline -NuGetConfig", 64));
        if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error, 64);
        RequiredDirectory(options.Get("PublicApiRoot"), "-PublicApiRoot");
        int keep = FastSuites.Positive(options, "Keep", 10);
        var git = options.Get("Git", "git");
        var revision = ReleaseEvidence.GitText(Root, git, "rev-parse", options.Get("Rev", "origin/master") + "^{commit}");
        var directory = Path.Combine(Root, "bin-build/nightly");
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + revision[..8];
        var output = Path.Combine(directory, id);
        if (Directory.Exists(output)) throw new ReleaseException("Nightly directory must be fresh: " + output);
        RequireCheckAllOwnedPath(Root, output); Directory.CreateDirectory(output);
        NightlyRuns.Write(Path.Combine(output, "nightly-owner.json"), new { root = Root, revision });
        NightlyRuns.Save(directory, new(id, revision, Path.Combine(output, "report.json"), "running", 0, false, DateTime.UtcNow));
        var clock = Stopwatch.StartNew(); int exitCode = 1;
        // Print only: the maintainer registers the 03:00 task. No credentials or registration side effects.
        Console.WriteLine(NightlySchedule(Root, options));
        try
        {
            var checkout = Path.Combine(output, "worktree");
            ProcessRunner.RequireSuccess(ProcessRunner.Run(git, ["init", "--quiet", checkout], Root), "initialize detached nightly worktree");
            var common = ReleaseEvidence.GitText(Root, git, "rev-parse", "--path-format=absolute", "--git-common-dir");
            File.WriteAllText(Path.Combine(checkout, ".git/objects/info/alternates"), Path.Combine(common, "objects").Replace('\\', '/') + "\n");
            File.WriteAllText(Path.Combine(checkout, ".git/HEAD"), revision + "\n");
            File.WriteAllText(Path.Combine(checkout, ".git/packed-refs"), ReleaseEvidence.GitText(Root, git, "for-each-ref", "--format=%(objectname) %(refname)").Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            ProcessRunner.RequireSuccess(ProcessRunner.Run(git, ["read-tree", "HEAD"], checkout), "nightly detached index");
            ProcessRunner.RequireSuccess(ProcessRunner.Run(git, ["checkout-index", "--all"], checkout), "nightly committed source");
            // Run this revision's tool, never the caller's binary. -Clean retains
            // the existing clean-snapshot and cold-build semantics.
            var args = new List<string> { "run", "--project", Path.Combine(checkout, "build-tools/release"), "--", "check-all", "-Profile", "nightly", "-Clean", "-NoBuildCache", "-OutputDirectory", output };
            foreach (var key in new[] { "PublicApiRoot", "NuGetConfig", "CompanionPython", "Python", "Dotnet", "BuildParallelism", "MaxParallelism" })
                if (options.Get(key) is { } value) args.AddRange(["-" + key, Path.IsPathRooted(value) || key.EndsWith("Parallelism", StringComparison.Ordinal) ? value :
                    key is "PublicApiRoot" or "NuGetConfig" or "CompanionPython" ? Path.GetFullPath(value) : value]);
            int remaining = (int)Math.Max(0, 1800000 - clock.Elapsed.TotalMilliseconds);
            if (remaining == 0) throw new ReleaseException("Nightly timed out preparing the detached worktree");
            exitCode = ProcessRunner.Run(Dotnet, args, checkout, new Dictionary<string, string?> { ["TIA_MCP_NIGHTLY_ROOT"] = directory,
                ["TIA_MCP_NIGHTLY_MANAGED"] = "1",
                ["TIA_MCP_CHECK_ORIGINAL_ROOT"] = Root, ["TIA_MCP_CHECK_RUNNER"] = null,
                ["RestoreConfigFile"] = nuget, ["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget, ["NuGetAudit"] = "false" }, logPath: Path.Combine(output, "nightly.log"),
                timeoutMilliseconds: remaining, forwardOutput: true).ExitCode;
        }
        catch (Exception ex) when (ex is ReleaseException or IOException) { Console.Error.WriteLine(ex.Message); }
        finally
        {
            NightlyRuns.Complete(Root, output, revision, exitCode, clock.Elapsed.TotalSeconds);
            NightlyRuns.Prune(Root, directory, keep, DateTime.UtcNow);
        }
        return exitCode;
    }

    internal static string NightlySchedule(string root, Options options)
    {
        // PowerShell single quotes keep the complete /TR value one argument.
        static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var args = new List<string> { Quote(ResolveResultApplication(options.Get("Dotnet", Dotnet))), "run", "--project", Quote(Path.Combine(root, "build-tools/release")) };
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (nuget != null) args.AddRange(["-p:RestoreConfigFile=" + Quote(Path.GetFullPath(nuget)), "-p:NuGetAudit=false"]);
        args.AddRange(["--", "nightly"]);
        args.AddRange(["-Worktree", Quote(root)]);
        foreach (var key in new[] { "PublicApiRoot", "NuGetConfig", "CompanionPython", "Python", "Dotnet" })
        {
            var value = options.Get(key) ?? key switch
            {
                "NuGetConfig" => nuget, "CompanionPython" => CheckAllCompanionPython(options),
                "Python" => Py, "Dotnet" => Dotnet, _ => null
            };
            if (value != null) args.AddRange(["-" + key, Quote(value)]);
        }
        return "schtasks.exe /Create /SC DAILY /ST 03:00 /TN TIA-MCP-Nightly /TR '" + string.Join(' ', args).Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
