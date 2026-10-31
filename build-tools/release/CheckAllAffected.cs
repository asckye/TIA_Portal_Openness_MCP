using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record AffectedUnit(string Id, string Project, string[] Inputs);
internal sealed record AffectedReason(string Path, string? Unit, string Job, string Reason);

internal sealed class CheckAllAffected(string[] changes, AffectedUnit[] units, string? fallback, bool quick)
{
    internal string[] Changes { get; } = changes;
    internal AffectedUnit[] Units { get; } = units;
    internal string? Fallback { get; private set; } = fallback;
    internal bool Quick { get; } = quick;
    private readonly List<AffectedReason> reasons = [];
    private readonly Dictionary<string, string> skipped = new(StringComparer.Ordinal);
    internal void RequireFull(string reason) => Fallback ??= reason;

    internal static CheckAllAffected Discover(string root, Options options, IEnumerable<CheckBuild> builds, string dotnet, string api)
    {
        var (changes, fallback) = ChangeSet(options.Get("Base", "origin/master"), args => ProcessRunner.Run(options.Get("Git", "git"), args, root));
        var units = new List<AffectedUnit>();
        foreach (var build in builds)
        {
            try { units.Add(new(build.Project + "|" + build.Variant, build.Project, CheckAllInputClosure.BuildInputs(root, build, dotnet, api))); }
            catch (Exception ex) when (CheckAllNightlyCoverage.InputError(ex))
            { fallback ??= "Cannot derive build input closure: " + build.Project + ": " + ex.Message; }
        }
        return new(changes, units.ToArray(), fallback, options.Has("Quick"));
    }

    internal static (string[] Changes, string? Fallback) ChangeSet(string basis, Func<string[], CommandResult> git)
    {
        var changes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? fallback = null;
        var common = git(["merge-base", basis, "HEAD"]);
        if (common.ExitCode != 0) fallback = "Cannot resolve merge-base with " + basis;
        else Read(["diff", "--no-renames", "--name-only", "-z", common.StandardOutput.Trim() + "..HEAD"]);
        Read(["diff", "--no-renames", "--name-only", "-z", "HEAD"]);
        Read(["ls-files", "--others", "--exclude-standard", "-z"]);
        return (changes.Order(StringComparer.Ordinal).ToArray(), fallback);

        void Read(string[] args)
        {
            var result = git(args);
            if (result.ExitCode != 0) { fallback ??= "Cannot read change set: git " + string.Join(' ', args); return; }
            changes.UnionWith(result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('\\', '/')));
        }
    }

    internal static bool Wide(string path) => CheckAllInputClosure.Covers("build-tools/release", path) || path == "tests/test-suites.json" ||
        Path.GetFileName(path).StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Contains("nuget", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
        CheckAllInputClosure.Covers("sdk", path);

    internal static string? ReleaseKey(string path) => Regex.Match(path.Replace('\\', '/'),
        @"^(?:src/Adapters/V|src/Studio/Openness/V|sdk/TIA_V)(14sp1|15[._]1|16|17|18|19|20|21)(?:/|_PublicAPI(?:/|$))", RegexOptions.IgnoreCase) is { Success: true } match
            ? match.Groups[1].Value.Replace('_', '.').ToLowerInvariant() : null;

    internal void Map(IEnumerable<CheckJob> jobs, JsonObject catalog)
    {
        var plan = jobs.ToArray();
        foreach (var path in Changes)
        {
            if (Wide(path)) Fallback ??= "Wide input: " + path;
            bool mapped = path.StartsWith("docs/", StringComparison.Ordinal) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            foreach (var job in plan)
            {
                var declaration = CheckAllJobInputs.Find(job, catalog);
                if (declaration == null) continue;
                var key = ReleaseKey(path);
                if (key != null && declaration.Release != null && key != declaration.Release) continue;
                var matching = Units.Where(unit => unit.Inputs.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                    declaration.Projects.Any(project => project == unit.Project || CheckAllInputClosure.Covers(Path.GetDirectoryName(project)!.Replace('\\', '/'), unit.Project))).ToArray();
                foreach (var unit in matching)
                { reasons.Add(new(path, unit.Id, job.Name, "path -> build input closure (including ProjectReference dependents) -> job")); mapped = true; }
                var closure = job.ResultInputs?.Closure;
                if (declaration.Paths.Concat(CheckAllJobInputs.Controls).Any(prefix => CheckAllInputClosure.Covers(prefix, path)) || closure != null &&
                    (closure.Sources.Contains(path, StringComparer.OrdinalIgnoreCase) || closure.Roots.Any(prefix => CheckAllInputClosure.Covers(prefix, path))))
                { reasons.Add(new(path, closure?.Project, job.Name, "declared script/helper/data or suite data closure")); mapped = true; }
                // The shared native adapter and worker can change all release variants.
                if ((CheckAllInputClosure.Covers("src/Adapters/Native", path) || CheckAllInputClosure.Covers("src/Adapters/build", path) ||
                    CheckAllInputClosure.Covers("src/PlcWorker", path)) && declaration.Release != null)
                { reasons.Add(new(path, "all release variants", job.Name, "shared worker/native adapter input")); mapped = true; }
                if (key != null && declaration.Release == key)
                { reasons.Add(new(path, "release " + key, job.Name, "release-key partition")); mapped = true; }
            }
            if (!mapped) Fallback ??= "Unmapped changed path: " + path;
        }
    }

    internal bool Selected(CheckJob job, CheckJobInputs? declaration)
    {
        if (Fallback != null || CheckAllJobInputs.AlwaysRun(job.Name) || declaration == null || declaration.Aggregate) return true;
        return reasons.Any(reason => reason.Job == job.Name);
    }

    internal CheckJob[] QuickPlan(IEnumerable<CheckJob> jobs, JsonObject catalog)
    {
        var all = jobs.ToArray();
        if (!Quick) return all;
        bool studio = Changes.Any(path => CheckAllInputClosure.Covers("src/Studio", path) || CheckAllInputClosure.Covers("tests/Studio", path));
        bool Eligible(CheckJob job) => !job.Name.StartsWith("stability-", StringComparison.Ordinal) &&
            (studio || !job.Name.StartsWith("studio-gui-", StringComparison.Ordinal) && job.Name != "configuration-page");
        var selected = all.Where(job => Eligible(job) && (Fallback != null || CheckAllJobInputs.AlwaysRun(job.Name) ||
            CheckAllJobInputs.Find(job, catalog) == null || reasons.Any(reason => reason.Job == job.Name))).Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var job in all.Where(Eligible))
            {
                if (selected.Contains(job.Name)) foreach (var dep in job.Dependencies)
                    if (all.Any(candidate => candidate.Name == dep && Eligible(candidate))) changed |= selected.Add(dep);
                if (job.Name != "preflight" && CheckAllJobInputs.Find(job, catalog)?.Driver == "registered suite" &&
                    job.Dependencies.Any(selected.Contains)) changed |= selected.Add(job.Name);
            }
        } while (changed);
        foreach (var job in all.Where(job => !selected.Contains(job.Name))) skipped[job.Name] = "quick: unaffected, stress excluded, or Studio GUI unchanged";
        return all.Where(job => selected.Contains(job.Name)).Select(job => job with { Dependencies = job.Dependencies.Where(dep =>
            !all.Any(candidate => candidate.Name == dep) || selected.Contains(dep)).ToArray() }).ToArray();
    }

    internal void Write(string output, IEnumerable<CheckTiming> timings, IEnumerable<string> nightlyOnly, string? nightlyId)
    {
        var rows = timings.ToArray();
        var reused = rows.Where(row => row.ReusedRunId != null).Select(row => new { job = row.Name, nightlyRunId = row.ReusedRunId, identityHash = row.IdentityHash });
        File.WriteAllText(Path.Combine(output, "affected.json"), JsonSerializer.Serialize(new
        {
            mode = Quick ? "quick" : "merge", changeSet = Changes,
            units = Units.Where(unit => unit.Inputs.Intersect(Changes, StringComparer.OrdinalIgnoreCase).Any()).Select(unit => new { unit.Id, unit.Project }),
            selectedJobs = rows.Where(row => row.ReusedRunId == null).Select(row => new { job = row.Name,
                reasons = reasons.Where(reason => reason.Job == row.Name).DefaultIfEmpty(new("", null, row.Name,
                    Fallback ?? (CheckAllJobInputs.AlwaysRun(row.Name) ? "always-run gate" : "identity unavailable/changed, aggregate, or missing nightly evidence"))) }),
            reusedJobs = reused, latestPassedNightly = nightlyId, skippedBecauseUnaffected = skipped,
            nightlyOnly = nightlyOnly.Select(job => new { job, reason = "nightly-only by maintainer decision 2026-10-30" }),
            fallbackReason = Fallback
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
