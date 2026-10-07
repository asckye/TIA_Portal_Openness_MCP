using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record ReleaseCheckRule(string Path, string[] Checks);
internal sealed record ReleaseSelfTest(string Check, string Script, string[] Arguments);
internal sealed record ReleaseCheckPlan(string Tier, string[] SelectedChecks, string[] SkippedChecks, string[] ChangedPaths)
{
    internal bool Includes(string check) => SelectedChecks.Contains(check, StringComparer.Ordinal);
}

internal sealed record ReleaseCheckPolicy(string[] Checks, string[] Always, ReleaseCheckRule[] Rules, ReleaseSelfTest[] SelfTests)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static ReleaseCheckPolicy Load(string root)
    {
        var policy = JsonSerializer.Deserialize<ReleaseCheckPolicy>(File.ReadAllText(Path.Combine(root, "build-tools/release/release-checks.json")), JsonOptions)
            ?? throw new ReleaseException("Release check map is empty.");
        policy.Validate();
        return policy;
    }

    internal void Validate()
    {
        if (Checks.Length == 0 || Checks.Distinct(StringComparer.Ordinal).Count() != Checks.Length ||
            Always.Except(Checks, StringComparer.Ordinal).Any() || Rules.Any(rule => rule.Path.Length == 0 || rule.Path.Contains("..", StringComparison.Ordinal) ||
                rule.Checks.Except(Checks, StringComparer.Ordinal).Any()) || SelfTests.Any(test => !Checks.Contains(test.Check, StringComparer.Ordinal) ||
                    !test.Script.StartsWith("scripts/checks/", StringComparison.Ordinal) || test.Script.Contains("..", StringComparison.Ordinal)) ||
            SelfTests.Select(test => test.Check).Distinct(StringComparer.Ordinal).Count() != SelfTests.Length)
            throw new ReleaseException("Invalid release check map.");
        if (Checks.Except(Rules.SelectMany(rule => rule.Checks), StringComparer.Ordinal).Any())
            throw new ReleaseException("Every release check must be reachable from a changed path.");
    }

    internal static string Tier(string? tier, bool required = false)
    {
        if (tier is null && !required) return "full";
        if (tier is not ("quick" or "full")) throw new ReleaseException("-Tier must be explicitly quick or full.", 64);
        return tier;
    }

    internal ReleaseCheckPlan Select(string tier, IEnumerable<string>? changes)
    {
        Tier(tier);
        var paths = changes?.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
        var selected = new HashSet<string>(Always, StringComparer.Ordinal);
        if (tier == "full" || changes is null) selected.UnionWith(Checks);
        else foreach (var path in paths)
        {
            var matches = Rules.Where(rule => rule.Path.EndsWith('/') ? path.StartsWith(rule.Path, StringComparison.Ordinal) : path == rule.Path).ToArray();
            if (matches.Length == 0) { selected.UnionWith(Checks); break; }
            selected.UnionWith(matches.SelectMany(rule => rule.Checks));
        }
        return new ReleaseCheckPlan(tier, Checks.Where(selected.Contains).ToArray(), Checks.Where(check => !selected.Contains(check)).ToArray(), paths);
    }

    internal void ValidatePlan(ReleaseCheckPlan plan)
    {
        Tier(plan.Tier);
        var all = plan.SelectedChecks.Concat(plan.SkippedChecks).ToArray();
        if (all.Length != Checks.Length || all.Distinct(StringComparer.Ordinal).Count() != all.Length ||
            all.Except(Checks, StringComparer.Ordinal).Any() || Always.Except(plan.SelectedChecks, StringComparer.Ordinal).Any() ||
            (plan.Tier == "full" && plan.SkippedChecks.Length != 0)) throw new ReleaseException("Invalid release check selection.");
        if (plan.Tier == "quick" && Select("quick", plan.ChangedPaths).SelectedChecks.Except(plan.SelectedChecks, StringComparer.Ordinal).Any())
            throw new ReleaseException("Quick selection omits a check required by its changed paths.");
    }

    internal static IReadOnlyList<ReleaseArtifact> SourceInventory(string root, string git)
    {
        var result = ProcessRunner.Run(git, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], root);
        ProcessRunner.RequireSuccess(result, "Enumerate candidate inputs");
        var generated = new HashSet<string>(["manifest/release-build.json", "manifest/configurator-build.json", "manifest/multi-version-build.json",
            "manifest/delivery.json", "manifest/package-manifest.json", "manifest/tools-list.json", "docs/reference/tool-matrix.md", "docs/reference/version-tool-catalog.md"], StringComparer.Ordinal);
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(path => !generated.Contains(path) && File.Exists(Path.Combine(root, path)))
            .Select(path => new ReleaseArtifact(path, ReleaseRecords.HashFile(Path.Combine(root, path)))).ToArray();
    }

    internal string[]? ChangesSinceFull(string baseline, IReadOnlyList<ReleaseArtifact> current)
    {
        if (!File.Exists(baseline)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(baseline));
            var record = document.RootElement;
            var package = record.GetProperty("package").GetString()!;
            if (record.GetProperty("tier").GetString() != "full" || !File.Exists(package) ||
                record.GetProperty("packageSha256").GetString() != ReleaseRecords.HashFile(package)) return null;
            RequireFullPackage(package);
            var previous = ReleaseRecords.ReadRows(record, "sourceFiles").ToDictionary(row => row.Path, row => row.Sha256, StringComparer.Ordinal);
            var now = current.ToDictionary(row => row.Path, row => row.Sha256, StringComparer.Ordinal);
            return previous.Keys.Union(now.Keys, StringComparer.Ordinal).Where(path => previous.GetValueOrDefault(path) != now.GetValueOrDefault(path)).Order(StringComparer.Ordinal).ToArray();
        }
        catch { return null; }
    }

    internal void RequireFullPackage(string package)
    {
        using var archive = ZipFile.OpenRead(package);
        var records = archive.Entries.Where(entry => entry.FullName.EndsWith("/manifest/package-manifest.json", StringComparison.Ordinal)).ToArray();
        if (records.Length != 1) throw new ReleaseException("Release requires a package with one tier record.");
        using var recordStream = records[0].Open();
        using var record = JsonDocument.Parse(recordStream);
        RequireFullRecord(record.RootElement);
        var ran = record.RootElement.GetProperty("checksRan").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var skipped = record.RootElement.GetProperty("checksSkipped").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (skipped.Length != 0 || ran.Length != Checks.Length || ran.Distinct(StringComparer.Ordinal).Count() != ran.Length ||
            Checks.Except(ran, StringComparer.Ordinal).Any()) throw new ReleaseException("Full package has incomplete release checks.");
    }

    internal static void RequireFullRecord(JsonElement record)
    {
        if (!record.TryGetProperty("tier", out var tier) || tier.GetString() != "full" ||
            !record.TryGetProperty("checkStatus", out var status) || status.GetString() != "passed")
            throw new ReleaseException("Release refuses packages without passed tier=full checks.");
    }
}

internal static partial class ReleaseCommands
{
    private static ReleaseCheckPlan ReleasePlan(Options options)
    {
        var policy = ReleaseCheckPolicy.Load(Root);
        var path = Environment.GetEnvironmentVariable("TIA_MCP_RELEASE_CHECK_PLAN");
        var plan = path is null ? policy.Select(ReleaseCheckPolicy.Tier(options.Get("Tier")), null)
            : JsonSerializer.Deserialize<ReleaseCheckPlan>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new ReleaseException("Release check selection is empty.");
        policy.ValidatePlan(plan);
        if (options.Get("Tier") is { } requested && requested != plan.Tier) throw new ReleaseException("Child tier differs from the candidate check plan.");
        return plan;
    }

    private static void WriteTierRecord(ReleaseCheckPlan plan, bool passed)
    {
        var path = Path.Combine(Root, "manifest/package-manifest.json");
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        record["tier"] = plan.Tier;
        record["checkStatus"] = passed ? "passed" : "pending";
        record["checksSelected"] = JsonSerializer.SerializeToNode(plan.SelectedChecks);
        record["checksRan"] = JsonSerializer.SerializeToNode(passed ? plan.SelectedChecks : Array.Empty<string>());
        record["checksSkipped"] = JsonSerializer.SerializeToNode(plan.SkippedChecks);
        record["changedPaths"] = JsonSerializer.SerializeToNode(plan.ChangedPaths);
        WriteJson(path, record);
    }
}
