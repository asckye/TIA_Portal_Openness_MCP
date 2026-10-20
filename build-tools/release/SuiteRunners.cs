using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

// Splits the shard jobs of one test-suites invocation across several machines (-RunnerIndex/-RunnerCount) and
// evaluates the registered per-suite gates over the union of their results (-GateOnly). Every runner and the gate
// compute the same shard plan from the same inputs; runner.json records each runner's share and plan digests so
// the gate can prove that every shard ran exactly once and that all runners partitioned the suites identically.
internal static class SuiteRunners
{
    internal const string ManifestName = "runner.json";
    internal const int ManifestFormat = 1;

    internal sealed record Partition(int Index, int Count);

    internal static Partition? Parse(Options options)
    {
        if (!options.Has("RunnerIndex") && !options.Has("RunnerCount")) return null;
        if (!options.Has("RunnerIndex") || !options.Has("RunnerCount")) throw new ReleaseException("-RunnerIndex and -RunnerCount are used together", 64);
        int count = FastSuites.Positive(options, "RunnerCount", 1);
        if (!int.TryParse(options.Get("RunnerIndex"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index >= count)
            throw new ReleaseException("-RunnerIndex must be an integer from 0 to RunnerCount - 1", 64);
        if (options.Has("GateOnly")) throw new ReleaseException("-GateOnly evaluates every runner's results; omit -RunnerIndex/-RunnerCount", 64);
        if (options.Has("ProveEquivalence")) throw new ReleaseException("-ProveEquivalence runs a suite's sequential baseline beside its shards and cannot be split across runners", 64);
        return new(index, count);
    }

    internal static bool IsGate(CheckJob job) => job.Name.EndsWith("-gate", StringComparison.Ordinal);

    // Longest estimate first onto the least-loaded runner; ties keep the job order and the lowest runner index.
    // Without timing history every shard has the same estimate, so this is round-robin in suite and shard order.
    internal static Dictionary<string, int> Assign(IReadOnlyList<CheckJob> jobs, int count)
    {
        var loads = new double[count];
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (job, _) in jobs.Where(job => !IsGate(job)).Select((job, order) => (job, order))
            .OrderByDescending(pair => pair.job.EstimatedSeconds).ThenBy(pair => pair.order))
        {
            int index = Array.IndexOf(loads, loads.Min());
            result.Add(job.Name, index);
            loads[index] += job.EstimatedSeconds;
        }
        return result;
    }

    internal static string PlanDigest(string output, string suite)
    {
        var path = Path.Combine(output, suite, "shard-plan.tsv");
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : "none";
    }

    internal static JsonObject Manifest(Partition partition, int shards, IReadOnlyList<string> suites, IReadOnlyDictionary<string, string> jobSuites,
        IEnumerable<string> assigned, string output) => new()
    {
        ["format"] = ManifestFormat, ["runnerIndex"] = partition.Index, ["runnerCount"] = partition.Count, ["shards"] = shards,
        ["suites"] = new JsonArray(suites.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
        ["shardCounts"] = new JsonObject(suites.Select(name => KeyValuePair.Create(name, (JsonNode?)JsonValue.Create(
            jobSuites.Count(pair => pair.Value == name && !pair.Key.EndsWith("-gate", StringComparison.Ordinal)))))),
        ["plans"] = new JsonObject(suites.Select(name => KeyValuePair.Create(name, (JsonNode?)JsonValue.Create(PlanDigest(output, name))))),
        ["jobs"] = new JsonArray(assigned.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
    };

    internal static void WriteManifest(string output, JsonObject manifest) =>
        File.WriteAllText(Path.Combine(output, ManifestName), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));

    // Copies each expected shard's directory from the one runner that ran it into the gate's output, so the registered
    // gate jobs merge exactly the files they would merge after a single-machine run. Returns the coverage errors.
    internal static List<string> Collect(string resultsRoot, string output, int shards, IReadOnlyList<string> suites,
        IReadOnlyDictionary<string, string> jobSuites)
    {
        var errors = new List<string>();
        if (!Directory.Exists(resultsRoot)) return [$"runner results directory does not exist: {resultsRoot}"];
        var runners = new List<(string Directory, JsonObject Manifest)>();
        foreach (var path in Directory.EnumerateFiles(resultsRoot, ManifestName, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject manifest) runners.Add((Path.GetDirectoryName(path)!, manifest));
                else errors.Add(path + ": runner manifest is not an object");
            }
            catch (JsonException ex) { errors.Add(path + ": " + ex.Message); }
        }
        var expected = jobSuites.Keys.Where(name => !name.EndsWith("-gate", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        // Only shards copied from a runner below may reach a gate; an earlier run's files never count.
        foreach (var job in expected)
        {
            var stale = Path.Combine(output, jobSuites[job], ShardDirectory(job, jobSuites[job]));
            if (Directory.Exists(stale)) Directory.Delete(stale, true);
        }
        if (runners.Count == 0) return [$"no {ManifestName} below {resultsRoot}"];
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexes = new HashSet<int>();
        int? count = null;
        foreach (var (directory, manifest) in runners)
        {
            string where = Path.GetRelativePath(resultsRoot, directory);
            int? Number(string key) => manifest[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
            if (Number("format") != ManifestFormat) { errors.Add(where + ": unsupported runner manifest format"); continue; }
            var runnerCount = Number("runnerCount"); var index = Number("runnerIndex");
            if (runnerCount is not > 0 || index is not >= 0 || index >= runnerCount) { errors.Add(where + ": invalid runner index/count"); continue; }
            count ??= runnerCount;
            if (runnerCount != count) errors.Add(where + $": runnerCount {runnerCount} differs from {count}");
            if (!indexes.Add(index.Value)) errors.Add(where + $": runner {index} reported twice");
            if (Number("shards") != shards) errors.Add(where + $": ran -Shards {Number("shards")}, the gate expects {shards}");
            var names = manifest["suites"] is JsonArray list ? list.Select(item => (string?)item).ToArray() : [];
            if (!names.SequenceEqual(suites)) errors.Add(where + ": ran a different suite list: " + string.Join(", ", names));
            foreach (var suite in suites)
            {
                if ((string?)manifest["plans"]?[suite] != PlanDigest(output, suite)) errors.Add(where + $": {suite} shard plan differs from the gate's plan");
                int planned = jobSuites.Count(pair => pair.Value == suite && expected.Contains(pair.Key));
                if ((int?)manifest["shardCounts"]?[suite] != planned) errors.Add(where + $": {suite} shard count differs from {planned}");
            }
            foreach (var job in manifest["jobs"] is JsonArray jobs ? jobs.Select(item => (string?)item ?? "") : [])
            {
                if (!expected.Contains(job)) { errors.Add(where + ": ran an unexpected job " + job); continue; }
                if (owners.TryGetValue(job, out var other)) { errors.Add($"{job} ran on two runners: {other} and {where}"); continue; }
                owners[job] = where;
                string suite = jobSuites[job], shard = ShardDirectory(job, suite);
                var source = Path.Combine(directory, suite, shard);
                if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(output, suite, shard));
            }
        }
        if (count is { } total)
            foreach (var missing in Enumerable.Range(0, total).Where(index => !indexes.Contains(index)))
                errors.Add($"runner {missing} of {total} has no {ManifestName}");
        foreach (var job in expected.Where(job => !owners.ContainsKey(job)).Order(StringComparer.Ordinal))
            errors.Add(job + " was not run by any runner");
        return errors;
    }

    private static string ShardDirectory(string job, string suite) => "shard-" + job[(suite.Length + "-shard-".Length)..];

    private static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
    }
}
