using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

// Offline candidates are deliberately untrusted. Publication binds every receipt
// to the latest same-commit Actions attempt and to the successful job's log.
internal static class CiCoverage
{
    internal const string Schema = "tia-mcp/ci-coverage/1";
    internal const string Marker = "TIA_MCP_CI_COVERAGE=";
    internal sealed record Profile(string Workflow, string File, string Job, string[] Suites, string[] Checks, string? ActionsJob = null);
    internal static readonly IReadOnlyDictionary<string, Profile> Profiles = new Dictionary<string, Profile>(StringComparer.Ordinal)
    {
        ["studio-true"] = new("offline-checks", ".github/workflows/offline-checks.yml", "studio-tests (true)",
            ["studio-core-true", "studio-gui-true"], [], "studio-tests"),
        ["foundation"] = new("offline-checks", ".github/workflows/offline-checks.yml", "foundation-suites-gate",
            ["foundation", "engine-host", "shared-lifecycle-plc", "worker-progress-foundation", "prompt-registration", "software-read",
                "special-export-shape", "device-add", "hardware-catalog", "diagnostic-membership", "adapter-contracts", "worker-channel",
                "compile-candidate", "compile-candidate-no-safety", "fallback-vci-modern", "fallback-vci-initial", "fallback-vci-classic", "workbench-control-protocol"], []),
        ["release"] = new("validate-bundle", ".github/workflows/validate.yml", "validate-bundle",
            ["release-tool", "generation-model", "generation-planning", "crash-evidence", "write-guard", "source-contracts"],
            ["bundle-self-tests", "release-documentation", "release-dry-run", "native-lifecycle", "native-mcp-session", "tia-features", "match-plc-name-source"])
    };

    private static long Id(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var id) && id > 0 ? id :
        throw new ReleaseException("CI coverage needs positive run/attempt/job IDs", 64);
    private static string Text(JsonNode? node) => ReleaseEvidence.Text(node);
    private static int Int(JsonNode? node) => ReleaseEvidence.Int(node);
    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new ReleaseException("CI coverage is not an object: " + path, 64);

    internal static string Encode(JsonObject receipt) => Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(receipt.ToJsonString()));

    internal static JsonObject Receipt(string root, string profileName, string directory, Func<string, string?> environment, Func<bool>? windows = null)
    {
        if (!(windows ?? OperatingSystem.IsWindows)() || environment("GITHUB_ACTIONS") != "true") throw new ReleaseException("CI receipts require a Windows Actions job", 64);
        if (!Profiles.TryGetValue(profileName, out var profile)) throw new ReleaseException("Unknown CI coverage profile: " + profileName, 64);
        var state = ReleaseEvidence.SourceState(root, "git");
        var sha = state.Commit;
        if (state.Status.Length != 0 || environment("GITHUB_SHA") != sha || environment("GITHUB_JOB") != (profile.ActionsJob ?? profile.Job))
            throw new ReleaseException("CI receipt needs its profile's unchanged, clean checkout", 64);
        if (profileName == "foundation")
        {
            var report = Read(Path.Combine(directory, "report.json"));
            var gates = (report["checks"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            if (report["coverageErrors"] is not JsonArray { Count: 0 } || Int(report["failed"]) != 0
                || !gates.Select(gate => Text(gate["Name"])).Order(StringComparer.Ordinal).SequenceEqual(profile.Suites.Select(suite => suite + "-gate").Order(StringComparer.Ordinal))
                || gates.Any(gate => Text(gate["Status"]) != "passed" || Int(gate["ExitCode"]) != 0))
                throw new ReleaseException("CI runner union did not pass every registered gate");
        }
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root, "windows");
        var suites = new JsonObject();
        foreach (var name in profile.Suites)
        {
            var registration = catalog[name]!.AsObject();
            var summary = Read(Path.Combine(directory, name + ".json"));
            var (counts, errors) = DotnetSuites.EvaluateTrx(Path.Combine(directory, name + ".trx"), Int(registration["minimumPassed"]), Int(registration["maximumSkipped"]));
            if (errors.Count != 0 || summary["errors"] is not JsonArray { Count: 0 }) throw new ReleaseException("CI suite gate failed: " + name);
            foreach (var key in new[] { "passed", "failed", "skipped", "total", "executed" })
                if (!JsonNode.DeepEquals(summary[key], counts[key])) throw new ReleaseException("CI summary differs from TRX: " + name + "/" + key);
            foreach (var key in new[] { "minimumPassed", "maximumSkipped" })
                if (!JsonNode.DeepEquals(summary[key], registration[key])) throw new ReleaseException("CI summary gate differs from registration: " + name);
            // Bind filters, build properties and the platform's registered gates.
            var compact = new JsonObject();
            foreach (var key in new[] { "suite", "passed", "failed", "skipped", "total", "executed", "minimumPassed", "maximumSkipped", "errors" }) compact[key] = summary[key]?.DeepClone();
            suites[name] = new JsonObject { ["registration"] = registration.DeepClone(), ["summary"] = compact,
                ["trxSha256"] = ReleaseRecords.HashFile(Path.Combine(directory, name + ".trx")) };
        }
        return new JsonObject
        {
            ["schema"] = Schema, ["profile"] = profileName, ["repository"] = environment("GITHUB_REPOSITORY"), ["commit"] = sha,
            ["runId"] = long.Parse(environment("GITHUB_RUN_ID")!, CultureInfo.InvariantCulture),
            ["runAttempt"] = long.Parse(environment("GITHUB_RUN_ATTEMPT")!, CultureInfo.InvariantCulture), ["jobName"] = profile.Job,
            ["workflowSha256"] = ReleaseRecords.HashFile(Path.Combine(root, profile.File)), ["platform"] = "windows", ["suites"] = suites,
            ["checks"] = ReleaseEvidence.Strings(profile.Checks)
        };
    }

    internal static JsonObject Assemble(IEnumerable<(JsonObject Receipt, long JobId)> receipts) => new()
    {
        ["schema"] = Schema,
        ["jobs"] = new JsonArray(receipts.Select(pair => (JsonNode?)new JsonObject { ["jobId"] = pair.JobId, ["receipt"] = pair.Receipt.DeepClone() }).ToArray())
    };

    internal static JsonObject Load(string root, string path, string commit)
    {
        var state = ReleaseEvidence.SourceState(root, "git");
        if (state.Status.Length != 0 || state.Commit != commit) throw new ReleaseException("-CiCovered requires a clean checkout of the receipt commit", 64);
        return Validate(root, Read(path), commit);
    }

    internal static JsonObject Validate(string root, JsonObject candidate, string commit)
    {
        if (Text(candidate["schema"]) != Schema || candidate["jobs"] is not JsonArray { Count: > 0 } jobs || jobs.Any(job => job is not JsonObject))
            throw new ReleaseException("Invalid CI coverage candidate", 64);
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root, "windows");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<long>();
        string? repository = null;
        foreach (var row in jobs.OfType<JsonObject>())
        {
            var jobId = Id(row["jobId"]);
            if (!identities.Add(jobId)) throw new ReleaseException("Duplicate CI job ID", 64);
            var receipt = row["receipt"] as JsonObject ?? throw new ReleaseException("Missing CI receipt", 64);
            var profileName = Text(receipt["profile"]);
            if (!Profiles.TryGetValue(profileName, out var profile) || !seen.Add(profileName)) throw new ReleaseException("Unknown or duplicate CI profile: " + profileName, 64);
            if (Text(receipt["schema"]) != Schema || Text(receipt["commit"]) != commit || Text(receipt["platform"]) != "windows"
                || Text(receipt["jobName"]) != profile.Job || Text(receipt["workflowSha256"]) != ReleaseRecords.HashFile(Path.Combine(root, profile.File)))
                throw new ReleaseException("CI receipt commit/platform/job/workflow differs: " + profileName, 64);
            _ = Id(receipt["runId"]); _ = Id(receipt["runAttempt"]);
            var repo = Text(receipt["repository"]);
            if (!Regex.IsMatch(repo, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z") || repository != null && repository != repo)
                throw new ReleaseException("Invalid or conflicting CI repository", 64);
            repository = repo;
            if (receipt["checks"] is not JsonArray checks || !checks.Select(Text).SequenceEqual(profile.Checks)) throw new ReleaseException("CI profile check list differs", 64);
            var suites = receipt["suites"] as JsonObject ?? throw new ReleaseException("Missing CI suites", 64);
            if (!suites.Select(pair => pair.Key).Order(StringComparer.Ordinal).SequenceEqual(profile.Suites.Order(StringComparer.Ordinal)))
                throw new ReleaseException("CI profile does not cover its exact complete suite set: " + profileName, 64);
            foreach (var name in profile.Suites)
            {
                if (!JsonNode.DeepEquals(suites[name]?["registration"], catalog[name])) throw new ReleaseException("CI registration differs: " + name, 64);
                var summary = suites[name]?["summary"] as JsonObject ?? throw new ReleaseException("Missing CI summary: " + name, 64);
                var registration = catalog[name]!;
                foreach (var key in new[] { "passed", "failed", "skipped", "total", "executed", "minimumPassed", "maximumSkipped" })
                    if (summary[key] is not JsonValue value || !value.TryGetValue<int>(out var count) || count < 0) throw new ReleaseException("Invalid CI count: " + name + "/" + key, 64);
                if (summary["errors"] is not JsonArray { Count: 0 } || Int(summary["failed"]) != 0 || Int(summary["passed"]) < Int(registration["minimumPassed"])
                    || Int(summary["skipped"]) > Int(registration["maximumSkipped"]) || Int(summary["executed"]) != Int(summary["passed"])
                    || (long)Int(summary["total"]) != (long)Int(summary["passed"]) + Int(summary["skipped"])
                    || Int(summary["minimumPassed"]) != Int(registration["minimumPassed"]) || Int(summary["maximumSkipped"]) != Int(registration["maximumSkipped"]))
                    throw new ReleaseException("CI registered gate failed: " + name, 64);
            }
        }
        return candidate.DeepClone().AsObject();
    }

    internal static Dictionary<string, JsonObject> Suites(JsonObject? candidate)
    {
        var suites = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (candidate == null) return suites;
        foreach (var row in candidate["jobs"]!.AsArray().OfType<JsonObject>())
            foreach (var suite in row["receipt"]!["suites"]!.AsObject()) suites.Add(suite.Key, row);
        return suites;
    }

    internal static JsonObject? Binding(string name, JsonObject? candidate)
    {
        if (candidate != null)
            foreach (var row in candidate["jobs"]!.AsArray().OfType<JsonObject>())
                if (row["receipt"]!["checks"]!.AsArray().Any(check => Text(check) == name)) return row;
        foreach (var (suite, row) in Suites(candidate))
            if (name == suite + "-gate" || Regex.IsMatch(name, "\\A" + Regex.Escape(suite) + @"-shard-[0-9]+\z")) return row;
        return null;
    }

    internal static string Description(JsonObject row) => $"covered by CI run {row["receipt"]!["runId"]} attempt {row["receipt"]!["runAttempt"]} job {row["jobId"]} ({Text(row["receipt"]!["jobName"])})";

    internal static List<CheckJob> Apply(IReadOnlyList<CheckJob> jobs, JsonObject? candidate)
    {
        RequirePlan(jobs, candidate);
        return Rewrite(jobs, candidate);
    }

    internal static void RequirePlan(IReadOnlyList<CheckJob> jobs, JsonObject? candidate)
    {
        if (candidate == null) return;
        foreach (var row in candidate["jobs"]!.AsArray().OfType<JsonObject>())
            foreach (var check in row["receipt"]!["checks"]!.AsArray().Select(Text))
                if (jobs.Count(job => job.Name == check) != 1) throw new ReleaseException("CI mapping has no unique local check: " + check);
        foreach (var suite in Suites(candidate).Keys)
            if (jobs.Count(job => job.Name == suite + "-gate") != 1 || !jobs.Any(job => job.Name.StartsWith(suite + "-shard-", StringComparison.Ordinal))
                && !CheckAllProfiles.Derived.ContainsKey(suite))
                throw new ReleaseException("CI mapping has no complete local suite plan: " + suite);
    }

    internal static List<CheckJob> Rewrite(IReadOnlyList<CheckJob> jobs, JsonObject? candidate) =>
        jobs.Select(job => Binding(job.Name, candidate) is { } row ? job with
        {
            Run = () => new(0, Description(row) + "\n", ""), CiCoverage = row.DeepClone().AsObject(), ResultCache = null, ResultInputs = null,
            ResultCachePolicy = Description(row)
        } : job).ToList();

    internal static void WriteSummaries(string output, JsonObject candidate)
    {
        Directory.CreateDirectory(Path.Combine(output, "suites"));
        foreach (var (suite, row) in Suites(candidate))
            File.WriteAllText(Path.Combine(output, "suites", suite + ".json"), row["receipt"]!["suites"]![suite]!["summary"]!.ToJsonString() + "\n", new UTF8Encoding(false));
    }

    internal static void VerifyJobs(JsonObject candidate, string repository, string workflow, JsonObject run, IReadOnlyList<JsonObject> jobs, Func<string, string> get)
    {
        foreach (var row in candidate["jobs"]!.AsArray().OfType<JsonObject>().Where(row => Profiles[Text(row["receipt"]!["profile"])].Workflow == workflow))
        {
            var receipt = row["receipt"]!;
            if (Text(receipt["repository"]) != repository || Id(receipt["runId"]) != Id(run["id"]) || Id(receipt["runAttempt"]) != Id(run["run_attempt"]))
                throw new ReleaseException("Adoption refused; CI coverage is from another repository/run/attempt");
            var job = jobs.SingleOrDefault(job => Id(job["id"]) == Id(row["jobId"]));
            if (job == null || Text(job["name"]) != Text(receipt["jobName"]) || Text(job["head_sha"]) != Text(receipt["commit"])
                || Id(job["run_id"]) != Id(receipt["runId"]) || Id(job["run_attempt"]) != Id(receipt["runAttempt"])
                || Text(job["status"]) != "completed" || Text(job["conclusion"]) != "success")
                throw new ReleaseException("Adoption refused; named CI job is missing, unsuccessful or from another commit/attempt");
            var log = get($"repos/{repository}/actions/jobs/{Id(row["jobId"])}/logs");
            var receipts = Regex.Matches(log, Regex.Escape(Marker) + @"([A-Za-z0-9+/=]+)")
                .Select(match => JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)))).ToArray();
            if (!receipts.Any(actual => JsonNode.DeepEquals(actual, receipt))) throw new ReleaseException("Adoption refused; successful CI job log lacks the exact gate receipt");
        }
    }

    internal static int Command(string root, Options options)
    {
        var receipts = options.All("Receipt").ToArray();
        JsonObject result;
        if (receipts.Length > 0)
        {
            var ids = options.All("JobId").ToArray();
            if (ids.Length != receipts.Length) throw new ReleaseException("Pair every -Receipt with a -JobId", 64);
            result = Assemble(receipts.Select((path, index) => (Read(path), long.Parse(ids[index], CultureInfo.InvariantCulture))));
            result = Validate(root, result, ReleaseEvidence.GitText(root, "git", "rev-parse", "HEAD"));
        }
        else
        {
            result = Receipt(root, options.Get("Profile") ?? "", Path.GetFullPath(options.Get("ResultsDirectory") ?? throw new ReleaseException("-ResultsDirectory is required", 64)), Environment.GetEnvironmentVariable);
            Console.WriteLine(Encode(result));
        }
        var output = options.Get("Output") ?? throw new ReleaseException("-Output is required", 64);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, result.ToJsonString(new() { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        return 0;
    }
}
