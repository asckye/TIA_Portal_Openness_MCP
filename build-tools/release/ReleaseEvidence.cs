using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

// The machine-side inputs the Git tree does not hold: the PublicAPI copies, the offline NuGet configuration,
// the .NET SDK, the Python interpreters and the machine itself.
internal sealed record EvidenceExternal(int PublicApiFiles, string PublicApiSha256, string? NuGetConfigSha256, string DotnetSdk,
    string Python, string? CompanionPython, string Machine);

// A passing check-all run of one clean commit, which `release -AdoptEvidence` accepts instead of rerunning the suites,
// captures, harness modes and soaks (P8-93). check-all writes the file; the release refuses evidence of another commit,
// a dirty tree, a changed input, a partial, failed, cached or reused run, or a run older than the commit.
internal static class ReleaseEvidence
{
    internal const string Schema = "tia-mcp/check-all-release-evidence/1";
    internal const string FileName = "release-evidence.json";
    internal const string AdoptionVariable = "TIA_MCP_RELEASE_ADOPTION";
    internal const string AdoptionSchema = "tia-mcp/release-adoption/1";
    internal static readonly string[] PushWorkflows = ["validate-bundle", "offline-checks"];
    internal static readonly string[] StudioJobs = ["configuration-page", "studio-core-false", "studio-core-true", "studio-gui-false", "studio-gui-true"];
    internal static readonly string[] CheckAllStages = ["build-once", "analyzer-warnings", "suite-output-parity", "tracked-tree-drift", "usage-audit"];
    // The release rewrites only these tracked paths; an adopted publication commits nothing else.
    internal static readonly string[] RecordFiles = ["docs/reference/tool-matrix.md", "docs/reference/version-tool-catalog.md"];
    internal const string Unavailable = "unavailable: ";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static string GitText(string root, string git, params string[] arguments)
    {
        var result = ProcessRunner.Run(git, ["-C", root, .. arguments], root);
        ProcessRunner.RequireSuccess(result, "git " + string.Join(' ', arguments));
        return result.StandardOutput.Trim();
    }

    internal static (string Commit, string Tree, DateTimeOffset CommitTime, string[] Status) SourceState(string root, string git)
    {
        var commit = GitText(root, git, "rev-parse", "HEAD");
        var tree = GitText(root, git, "rev-parse", "HEAD^{tree}");
        var time = DateTimeOffset.Parse(GitText(root, git, "log", "-1", "--format=%cI", "HEAD"), CultureInfo.InvariantCulture);
        var status = ProcessRunner.Run(git, ["-C", root, "status", "--porcelain=v1", "--untracked-files=all"], root);
        ProcessRunner.RequireSuccess(status, "git status");
        return (commit, tree, time.ToUniversalTime(), status.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    // Every tracked and unignored file of the checkout with the SHA-256 of its bytes, as the candidate inventory hashes them.
    internal static SortedDictionary<string, string> Inventory(string root, string git)
    {
        var result = ProcessRunner.Run(git, ["-C", root, "ls-files", "--cached", "--others", "--exclude-standard", "-z"], root);
        ProcessRunner.RequireSuccess(result, "Enumerate evidence inputs");
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            var file = Path.Combine(root, path);
            if (File.Exists(file)) rows[path] = ReleaseRecords.HashFile(file);
        }
        return rows;
    }

    internal static string Digest(IEnumerable<KeyValuePair<string, string>> rows)
    {
        var text = new StringBuilder();
        foreach (var (path, sha) in rows.OrderBy(row => row.Key, StringComparer.Ordinal)) text.Append(path).Append('\t').Append(sha).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static (int Count, string Sha256) DirectoryDigest(string directory)
    {
        var full = Path.GetFullPath(directory);
        var rows = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Select(file => KeyValuePair.Create(Path.GetRelativePath(full, file).Replace('\\', '/') + "\t" + new FileInfo(file).Length.ToString(CultureInfo.InvariantCulture), ReleaseRecords.HashFile(file)))
            .ToArray();
        return (rows.Length, Digest(rows));
    }

    internal static EvidenceExternal ProbeExternal(string root, string publicApiRoot, string? nugetConfig, string dotnet, string python, string? companionPython)
    {
        // A tool that cannot report its version is recorded as unavailable; check-all still runs, and the release refuses it.
        string Version(string executable)
        {
            CommandResult result;
            try { result = ProcessRunner.Run(executable, ["--version"], root); }
            catch (ReleaseException ex) { return Unavailable + ex.Message; }
            return result.ExitCode == 0 ? (result.StandardOutput + result.StandardError).Trim() : Unavailable + "exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        var api = DirectoryDigest(publicApiRoot);
        string machine;
        try { machine = CheckAllResultCache.MachineIdentity(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { machine = Unavailable + ex.GetType().Name; }
        // The companion interpreter is identified by its bytes; check-all's fixtures pass a stand-in that must not be started.
        return new EvidenceExternal(api.Count, api.Sha256, nugetConfig is null ? null : ReleaseRecords.HashFile(nugetConfig), Version(dotnet), Version(python),
            string.IsNullOrWhiteSpace(companionPython) ? null : File.Exists(companionPython) ? "sha256:" + ReleaseRecords.HashFile(companionPython) : Unavailable + "missing",
            machine);
    }

    // The source and machine state before check-all regenerates anything.
    internal static JsonObject Begin(string root, string git, Options options, EvidenceExternal external, Func<string, string?> environment, DateTimeOffset now)
    {
        var state = SourceState(root, git);
        var inputs = Inventory(root, git);
        var files = new JsonObject();
        foreach (var (path, sha) in inputs) files[path] = sha;
        return new JsonObject
        {
            ["schema"] = Schema,
            ["startedUtc"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["source"] = new JsonObject
            {
                ["commit"] = state.Commit, ["tree"] = state.Tree, ["commitTimeUtc"] = state.CommitTime.ToString("o", CultureInfo.InvariantCulture),
                ["cleanAtStart"] = state.Status.Length == 0, ["statusAtStart"] = Strings(state.Status.Take(50)),
                ["inputs"] = new JsonObject { ["count"] = inputs.Count, ["sha256"] = Digest(inputs), ["files"] = files }
            },
            ["external"] = External(external),
            ["tools"] = new JsonObject { ["os"] = Environment.OSVersion.VersionString, ["processors"] = Environment.ProcessorCount,
                ["releaseToolSha256"] = ReleaseRecords.HashFile(typeof(ReleaseEvidence).Assembly.Location) },
            ["mode"] = new JsonObject
            {
                ["clean"] = options.Has("Clean") && !string.IsNullOrEmpty(environment("TIA_MCP_CLEAN_CHECKOUT")),
                ["cold"] = options.Has("Cold"),
                ["resultCacheEnabled"] = CheckAllResultCache.Enabled(options, environment),
                ["buildCacheDisabled"] = environment("TIA_MCP_BUILD_CACHE_DISABLED") == "1",
                ["buildOnly"] = options.Has("BuildOnly"),
                ["arguments"] = Strings(options.Arguments().Order(StringComparer.Ordinal))
            }
        };
    }

    // check-all's result never depends on its evidence: a start state that cannot be read is recorded, and the release refuses it.
    internal static JsonObject BeginSafely(Func<JsonObject> begin, DateTimeOffset now)
    {
        try { return begin(); }
        catch (Exception ex) when (ex is ReleaseException or IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Console.Error.WriteLine("warning: release evidence start state unavailable: " + ex.Message);
            return new JsonObject { ["schema"] = Schema, ["startedUtc"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), ["beginError"] = ex.Message };
        }
    }

    internal static JsonObject External(EvidenceExternal external) => new()
    {
        ["publicApi"] = new JsonObject { ["count"] = external.PublicApiFiles, ["sha256"] = external.PublicApiSha256 },
        ["nugetConfigSha256"] = external.NuGetConfigSha256, ["dotnetSdk"] = external.DotnetSdk, ["python"] = external.Python,
        ["companionPython"] = external.CompanionPython, ["machine"] = external.Machine
    };

    // Writes <output>/release-evidence.json for every finished check-all run, passing or not; the release decides.
    internal static string Write(JsonObject begin, string output, IReadOnlyList<string> planned, IReadOnlyList<CheckTiming> timings, int exitCode,
        double wallSeconds, bool underThirtyMinutes, IReadOnlyList<string> drift, DateTimeOffset now)
    {
        var evidence = begin.DeepClone().AsObject();
        evidence["runId"] = Path.GetFileName(Path.GetFullPath(output).TrimEnd('\\', '/'));
        evidence["completedUtc"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        var jobs = new JsonArray();
        foreach (var row in timings)
        {
            var log = Path.Combine(output, "logs", row.Name + ".log");
            jobs.Add(new JsonObject
            {
                ["name"] = row.Name, ["status"] = row.Status, ["exitCode"] = row.ExitCode,
                ["seconds"] = Math.Round(row.Seconds, 3), ["reused"] = row.ReusedRunId is not null,
                ["ci"] = row.CiCoverage?.DeepClone(),
                ["logSha256"] = File.Exists(log) ? ReleaseRecords.HashFile(log) : null
            });
        }
        var suites = new JsonObject();
        var suiteDirectory = Path.Combine(output, "suites");
        if (Directory.Exists(suiteDirectory))
            foreach (var summary in Directory.EnumerateFiles(suiteDirectory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                JsonObject? node;
                try { node = JsonNode.Parse(File.ReadAllText(summary)) as JsonObject; }
                catch (JsonException ex) { node = new JsonObject { ["errors"] = new JsonArray(JsonValue.Create("unreadable summary: " + ex.Message)) }; }
                if (node is null) continue;
                suites[Path.GetFileNameWithoutExtension(summary)] = new JsonObject
                {
                    ["passed"] = Number(node["passed"]), ["failed"] = Number(node["failed"]), ["skipped"] = Number(node["skipped"]),
                    ["total"] = Number(node["total"]), ["minimumPassed"] = Number(node["minimumPassed"]), ["maximumSkipped"] = Number(node["maximumSkipped"]),
                    ["errors"] = node["errors"] is JsonArray errors ? errors.Count : 1, ["sha256"] = ReleaseRecords.HashFile(summary)
                };
            }
        var cache = Path.Combine(output, "build-cache.json");
        evidence["buildCache"] = File.Exists(cache) ? JsonNode.Parse(File.ReadAllText(cache)) : null;
        evidence["planned"] = Strings(planned.Distinct(StringComparer.Ordinal));
        evidence["jobs"] = jobs;
        evidence["suites"] = suites;
        evidence["result"] = new JsonObject
        {
            ["exitCode"] = exitCode, ["passed"] = timings.Count(CheckTiming.Passed), ["failed"] = timings.Count(row => !CheckTiming.Passed(row)),
            ["reused"] = timings.Count(row => row.Status == "passed (reused)" || row.ReusedRunId is not null),
            ["wallSeconds"] = Math.Round(wallSeconds, 3), ["underThirtyMinutes"] = underThirtyMinutes, ["drift"] = Strings(drift)
        };
        evidence["files"] = new JsonObject
        {
            ["report"] = HashIfPresent(Path.Combine(output, "report.json")), ["cpuPlan"] = HashIfPresent(Path.Combine(output, "cpu-plan.json"))
        };
        var path = Path.Combine(output, FileName);
        File.WriteAllText(path, evidence.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        return path;
    }

    internal static IReadOnlyDictionary<string, (int MinimumPassed, int MaximumSkipped)> SuiteRegistry(string root)
    {
        var path = Path.Combine(root, "tests/test-suites.json");
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject catalog || catalog.Count == 0) throw new ReleaseException("Suite registry is empty: " + path);
        var result = new SortedDictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var (name, node) in catalog)
        {
            if (node is not JsonObject suite) throw new ReleaseException("Suite registry entry is not an object: " + name);
            var windows = suite["platforms"]?["windows"] as JsonObject;
            result[name] = ((int)(windows?["minimumPassed"] ?? suite["minimumPassed"])!, (int)(windows?["maximumSkipped"] ?? suite["maximumSkipped"])!);
        }
        return result;
    }

    // What an adopted publication no longer runs itself, as check-all job names: each registered suite's gate exactly, and every
    // other family by name or name prefix (successor jobs keep the family prefix, with or without the -plc suffix).
    internal static IReadOnlyList<(string Name, bool Exact)> RequiredJobs(string root)
    {
        var jobs = SuiteRegistry(root).Keys.Select(suite => (suite + "-gate", true)).ToList();
        jobs.AddRange(StudioJobs.Select(name => (name, true)));
        foreach (var key in CatalogJson.Keys)
            foreach (var family in new[] { "contracts", "responses", "transport-usage", "usage" }) jobs.Add((family + "-v" + key, false));
        foreach (var key in new[] { "20", "21" })
        {
            foreach (var family in new[] { "harness", "approval", "resources", "ecosystem", "campaign-inputs" }) jobs.Add((family + "-v" + key, false));
            jobs.Add(("stability-v" + key + "-c8", false));
            jobs.Add(("stability-v" + key + "-c16", false));
        }
        jobs.AddRange(CheckAllStages.Select(name => (name, name != "usage-audit")));
        return jobs;
    }

    internal sealed record Inputs(string Root, string Git, string EvidencePath, EvidenceExternal Current, DateTimeOffset Now,
        Func<JsonObject, JsonArray>? VerifyCiCoverage = null);

    // Every refusal reason is collected; the release reports them together.
    internal static JsonObject Verify(Inputs inputs)
    {
        var path = Path.GetFullPath(inputs.EvidencePath);
        if (!File.Exists(path)) throw new ReleaseException("Adopted check-all evidence missing: " + path);
        JsonObject evidence;
        try { evidence = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new ReleaseException("Adopted evidence is not a JSON object: " + path); }
        catch (JsonException ex) { throw new ReleaseException("Adopted evidence is not valid JSON: " + path + ": " + ex.Message); }
        if (Text(evidence["schema"]) != Schema) throw new ReleaseException($"Adopted evidence schema is '{Text(evidence["schema"])}', not '{Schema}': {path}");
        var directory = Path.GetDirectoryName(path)!;
        var problems = new List<string>();
        JsonObject? ciCovered = null;
        JsonArray? verifiedCi = null;
        if (evidence["ciCovered"] != null)
        {
            try { ciCovered = CiCoverage.Validate(inputs.Root, evidence["ciCovered"]!.AsObject(), Text(evidence["source"]?["commit"])); }
            catch (Exception ex) when (ex is ReleaseException or InvalidOperationException) { problems.Add("invalid CI coverage: " + ex.Message); }
        }
        if (evidence["beginError"] is not null) problems.Add("check-all could not record its start state: " + Text(evidence["beginError"]));

        // A failed, partial, cached or reused run.
        if (evidence["result"] is not JsonObject || evidence["mode"] is not JsonObject || evidence["source"] is not JsonObject || evidence["result"]?["exitCode"] is null)
            problems.Add("evidence lacks its result, mode or source section (partial run)");
        var result = evidence["result"] as JsonObject ?? new JsonObject();
        var mode = evidence["mode"] as JsonObject ?? new JsonObject();
        if (Int(result["exitCode"]) != 0) problems.Add("check-all exited " + Int(result["exitCode"]) + " (failed run)");
        if (Int(result["failed"]) != 0) problems.Add(Int(result["failed"]) + " check-all job(s) failed or were blocked");
        if (Int(result["reused"]) != 0) problems.Add(Int(result["reused"]) + " check-all job(s) reused earlier results");
        if (result["drift"] is not JsonArray drift || drift.Count != 0) problems.Add("check-all reported tracked-tree drift: " + string.Join(", ", (result["drift"] as JsonArray ?? []).Select(Text).Take(5)));
        if (!Bool(result["underThirtyMinutes"])) problems.Add("check-all did not finish within its 30-minute gate");
        if (Bool(mode["buildOnly"])) problems.Add("check-all ran -BuildOnly (partial run)");
        if (!Bool(mode["clean"]) && !Bool(mode["cold"])) problems.Add("check-all was neither -Clean nor -Cold (incremental build outputs)");
        if (Bool(mode["resultCacheEnabled"])) problems.Add("check-all ran with its result cache enabled");
        if (!Bool(mode["buildCacheDisabled"])) problems.Add("check-all ran without -NoBuildCache");
        if (evidence["buildCache"] is JsonObject buildCache && Int(buildCache["outputsRestored"]) != 0) problems.Add("check-all restored cached build outputs");

        // The complete job list: every planned job ran, passed and left the log the evidence hashes.
        var jobs = (evidence["jobs"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var job in jobs) if (!byName.TryAdd(Text(job["name"]), job)) problems.Add("duplicate job in evidence: " + Text(job["name"]));
        var planned = (evidence["planned"] as JsonArray ?? []).Select(Text).ToArray();
        if (ciCovered != null)
            foreach (var binding in ciCovered["jobs"]!.AsArray().OfType<JsonObject>())
                foreach (var check in binding["receipt"]!["checks"]!.AsArray().Select(Text))
                    if (!planned.Contains(check, StringComparer.Ordinal)) problems.Add("CI check missing from complete plan: " + check);
        if (planned.Length == 0) problems.Add("evidence lists no planned jobs (partial run)");
        foreach (var name in planned.Where(name => !byName.ContainsKey(name))) problems.Add("planned job has no result (partial run): " + name);
        foreach (var name in byName.Keys.Where(name => !planned.Contains(name, StringComparer.Ordinal) && name != "tracked-tree-drift")) problems.Add("unplanned job in evidence: " + name);
        foreach (var job in jobs)
        {
            var name = Text(job["name"]);
            var binding = CiCoverage.Binding(name, ciCovered);
            var covered = Text(job["status"]) == "covered by CI";
            if (covered != (binding != null) || !JsonNode.DeepEquals(job["ci"], binding)) problems.Add("CI/local job mapping differs: " + name);
            if ((!covered && Text(job["status"]) != "passed") || Int(job["exitCode"]) != 0 || Bool(job["reused"])) { problems.Add($"job {name} is {Text(job["status"])} (exit {Int(job["exitCode"])})"); continue; }
            if (name == "tracked-tree-drift") continue;
            var log = Path.Combine(directory, "logs", name + ".log");
            if (Text(job["logSha256"]).Length != 64) problems.Add("job has no bound log: " + name);
            else if (!File.Exists(log)) problems.Add("job log missing next to the evidence: " + name);
            else if (ReleaseRecords.HashFile(log) != Text(job["logSha256"])) problems.Add("job log changed after the run: " + name);
        }
        foreach (var (file, key) in new[] { ("report.json", "report"), ("cpu-plan.json", "cpuPlan") })
        {
            var recorded = Text(evidence["files"]?[key]);
            var actual = Path.Combine(directory, file);
            if (recorded.Length != 64 || !File.Exists(actual) || ReleaseRecords.HashFile(actual) != recorded) problems.Add(file + " is missing or changed after the run");
        }

        // Coverage: everything an adopted release skips must have passed in this run.
        var registry = SuiteRegistry(inputs.Root);
        var suites = evidence["suites"] as JsonObject ?? new JsonObject();
        foreach (var (suite, binding) in CiCoverage.Suites(ciCovered))
        {
            if (!planned.Contains(suite + "-gate", StringComparer.Ordinal) || !planned.Any(name => name.StartsWith(suite + "-shard-", StringComparison.Ordinal)))
                problems.Add("CI suite missing from complete plan: " + suite);
            var summary = Path.Combine(directory, "suites", suite + ".json");
            if (!File.Exists(summary) || !JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(summary)), binding["receipt"]!["suites"]![suite]!["summary"]))
                problems.Add("CI summary differs from receipt: " + suite);
        }
        foreach (var (suite, gate) in registry)
        {
            if (suites[suite] is not JsonObject row) { problems.Add("registered suite missing from evidence: " + suite); continue; }
            if (Int(row["failed"]) != 0 || Int(row["errors"]) != 0 || Int(row["passed"]) < gate.MinimumPassed || Int(row["skipped"]) > gate.MaximumSkipped
                || Int(row["minimumPassed"]) != gate.MinimumPassed || Int(row["maximumSkipped"]) != gate.MaximumSkipped)
                problems.Add($"suite {suite} does not meet its registered gate ({Int(row["passed"])} passed, {Int(row["failed"])} failed, {Int(row["skipped"])} skipped; minimum {gate.MinimumPassed}, maximum skipped {gate.MaximumSkipped})");
            var summary = Path.Combine(directory, "suites", suite + ".json");
            if (!File.Exists(summary) || ReleaseRecords.HashFile(summary) != Text(row["sha256"])) problems.Add("suite summary missing or changed after the run: " + suite);
        }
        var passedJobs = jobs.Where(job => Text(job["status"]) is "passed" or "covered by CI").Select(job => Text(job["name"])).ToArray();
        foreach (var (name, exact) in RequiredJobs(inputs.Root))
            if (!passedJobs.Any(job => job == name || !exact && job.StartsWith(name + "-", StringComparison.Ordinal))) problems.Add("required check-all job missing: " + name);

        // The commit, the tree and every input.
        var source = evidence["source"] as JsonObject ?? new JsonObject();
        var state = SourceState(inputs.Root, inputs.Git);
        if (Text(source["commit"]) != state.Commit) problems.Add($"evidence is for commit {Text(source["commit"])}, HEAD is {state.Commit}");
        if (Text(source["tree"]) != state.Tree) problems.Add($"evidence is for tree {Text(source["tree"])}, HEAD's tree is {state.Tree}");
        if (!Bool(source["cleanAtStart"])) problems.Add("check-all started on a dirty tree");
        if (state.Status.Length != 0) problems.Add("the working tree is dirty: " + string.Join(", ", state.Status.Take(5)));
        var current = Inventory(inputs.Root, inputs.Git);
        var recordedInputs = source["inputs"] as JsonObject ?? new JsonObject();
        if (Text(recordedInputs["sha256"]) != Digest(current) || Int(recordedInputs["count"]) != current.Count)
        {
            var recordedFiles = recordedInputs["files"] as JsonObject ?? new JsonObject();
            var changed = current.Keys.Union(recordedFiles.Select(pair => pair.Key), StringComparer.Ordinal)
                .Where(file => Text(recordedFiles[file]) != (current.TryGetValue(file, out var sha) ? sha : "")).Take(5).ToArray();
            problems.Add("inputs changed since the run: " + (changed.Length == 0 ? "inventory digest differs" : string.Join(", ", changed)));
        }
        var started = Time(evidence["startedUtc"]);
        var completed = Time(evidence["completedUtc"]);
        if (started is null || completed is null || completed < started) problems.Add("evidence start/completion times are missing or inconsistent");
        else
        {
            if (started < state.CommitTime) problems.Add($"evidence started {started:o}, before the commit time {state.CommitTime:o}");
            if (completed > inputs.Now.ToUniversalTime() + ClockSkew) problems.Add($"evidence completion {completed:o} lies in the future");
        }

        // Inputs outside the tree.
        var external = evidence["external"] as JsonObject ?? new JsonObject();
        var now = External(inputs.Current);
        foreach (var field in new[] { "dotnetSdk", "python", "machine" })
            if (Text(external[field]).StartsWith(Unavailable, StringComparison.Ordinal) || Text(now[field]).StartsWith(Unavailable, StringComparison.Ordinal))
                problems.Add($"{field} is unavailable: evidence '{Text(external[field])}', now '{Text(now[field])}'");
            else if (Text(external[field]) != Text(now[field])) problems.Add($"{field} differs: evidence '{Text(external[field])}', now '{Text(now[field])}'");
        if (Int(external["publicApi"]?["count"]) != inputs.Current.PublicApiFiles || Text(external["publicApi"]?["sha256"]) != inputs.Current.PublicApiSha256)
            problems.Add("PublicAPI copies differ from the evidence run");
        if (inputs.Current.NuGetConfigSha256 is null || Text(external["nugetConfigSha256"]) != inputs.Current.NuGetConfigSha256)
            problems.Add("the offline NuGet configuration differs from the evidence run (pass the same -NuGetConfig)");
        if (external["companionPython"] is not null && inputs.Current.CompanionPython is not null && Text(external["companionPython"]) != inputs.Current.CompanionPython)
            problems.Add($"companion Python differs: evidence '{Text(external["companionPython"])}', now '{inputs.Current.CompanionPython}'");

        if (ciCovered != null && problems.Count == 0)
        {
            if (inputs.VerifyCiCoverage == null) problems.Add("CI-covered evidence requires online named-job verification (-Ci); offline candidate receipts are not release proof");
            else
            {
                try { verifiedCi = inputs.VerifyCiCoverage(ciCovered); }
                catch (ReleaseException ex) { problems.Add(ex.Message); }
            }
        }
        if (problems.Count != 0)
            throw new ReleaseException($"Adoption refused; {problems.Count} problem(s) with {path}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems));

        var counts = new JsonObject();
        foreach (var (suite, row) in suites.Where(pair => registry.ContainsKey(pair.Key)))
            counts[suite] = new JsonObject
            {
                ["passed"] = Int(row!["passed"]), ["failed"] = Int(row["failed"]), ["skipped"] = Int(row["skipped"]), ["total"] = Int(row["total"]),
                ["minimumPassed"] = Int(row["minimumPassed"]), ["maximumSkipped"] = Int(row["maximumSkipped"])
            };
        var relative = Path.GetRelativePath(inputs.Root, path).Replace('\\', '/');
        return new JsonObject
        {
            ["schema"] = AdoptionSchema,
            ["evidencePath"] = path,
            ["record"] = new JsonObject
            {
                ["source"] = "check-all and GitHub Actions of the same commit; the release built, packaged and checked its own product",
                ["evidence"] = new JsonObject
                {
                    ["file"] = relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(directory) + "/" + FileName : relative,
                    ["sha256"] = ReleaseRecords.HashFile(path), ["runId"] = Text(evidence["runId"]), ["commit"] = state.Commit, ["tree"] = state.Tree,
                    ["inputsSha256"] = Text(recordedInputs["sha256"]), ["startedUtc"] = Text(evidence["startedUtc"]), ["completedUtc"] = Text(evidence["completedUtc"]),
                    ["wallSeconds"] = (double?)result["wallSeconds"] ?? 0, ["jobs"] = jobs.Length, ["mode"] = Bool(mode["clean"]) ? "clean" : "cold",
                    ["dotnetSdk"] = Text(external["dotnetSdk"]), ["python"] = Text(external["python"])
                },
                ["ci"] = verifiedCi,
                ["ciCovered"] = ciCovered?.DeepClone(),
                ["ciCoveredJobs"] = new JsonArray(jobs.Where(job => Text(job["status"]) == "covered by CI").Select(job => (JsonNode?)new JsonObject
                    { ["name"] = Text(job["name"]), ["ci"] = job["ci"]!.DeepClone() }).ToArray()),
                ["suites"] = counts,
                ["adoptedChecks"] = Strings(AdoptedChecks),
                ["localChecks"] = new JsonArray()
            }
        };
    }

    // The release stages an adopted publication leaves to the evidence, and the ones it still runs itself.
    internal static readonly string[] AdoptedChecks =
    [
        "registered .NET suites (early gates, build-release, multi-version preparation and the PLC suite runs)",
        "Studio configuration, client and GUI tests",
        "Foundation and PLC transport checks and the all-release tool usage audit",
        "PLC harness modes, resource discovery, ecosystem, usage and campaign checks, stability soaks and the preparation's PLC captures"
    ];

    internal static IReadOnlyList<string> NonRecordChanges(IEnumerable<string> paths) =>
        paths.Where(path => !path.StartsWith("manifest/", StringComparison.Ordinal) && !RecordFiles.Contains(path, StringComparer.Ordinal)).ToArray();

    // The adopted publication starts on the prep commit, which already carries the version.
    internal static string? PrepCommitError(string label, string version) => label == version ? null :
        $"-AdoptEvidence needs the prep commit that already carries {version} (Version.props has {label}): run release -Version {version} -BumpOnly, " +
        "commit with the CHANGELOG entry and notes, push, and run check-all -Clean -NoBuildCache on that commit";

    // Before the build: HEAD is still the evidence commit and nothing rewrote the tree.
    internal static void RequireUnchangedTree(string root, string git, string commit)
    {
        var state = SourceState(root, git);
        if (state.Commit != commit) throw new ReleaseException("HEAD moved after the evidence was adopted: " + state.Commit);
        if (state.Status.Length != 0) throw new ReleaseException("The adopted tree changed before the build: " + string.Join(", ", state.Status.Take(5)));
    }

    // Before staging: the release commit holds only the records the build rewrote, on top of the commit the evidence tested.
    internal static void RequireRecordOnlyChanges(string root, string git, string commit)
    {
        if (GitText(root, git, "rev-parse", "HEAD") != commit) throw new ReleaseException("HEAD moved after the evidence was adopted; refusing the release commit");
        var other = NonRecordChanges(ReleaseCommands.EnumerateReleaseChanges(root, git));
        if (other.Count != 0)
            throw new ReleaseException("An adopted publication may commit only release records; the build also changed: " + string.Join(", ", other.Take(10)));
    }

    // GitHub Actions of the commit: the latest push run on master of each workflow completed successfully, every job included.
    internal static JsonArray VerifyCi(Func<string, string> get, string repository, string sha, IReadOnlyList<string> workflows, JsonObject? ciCovered = null)
    {
        var problems = new List<string>();
        var runs = Pages(get, $"repos/{repository.Trim('/')}/actions/runs?head_sha={Uri.EscapeDataString(sha)}&per_page=100", "workflow_runs");
        var records = new JsonArray();
        foreach (var workflow in workflows)
        {
            var candidates = runs.Where(run => Text(run["name"]) == workflow && Text(run["head_sha"]) == sha && Text(run["event"]) == "push" && Text(run["head_branch"]) == "master")
                .OrderByDescending(run => Int(run["run_number"])).ThenByDescending(run => Int(run["run_attempt"])).ToArray();
            if (candidates.Length == 0) { problems.Add($"no push run of {workflow} on master for {sha}"); continue; }
            var latest = candidates[0];
            var url = Text(latest["html_url"]);
            if (Text(latest["status"]) != "completed") { problems.Add($"{workflow} run {Int(latest["run_number"])} is {Text(latest["status"])}: {url}"); continue; }
            if (Text(latest["conclusion"]) != "success") { problems.Add($"{workflow} run {Int(latest["run_number"])} concluded {Text(latest["conclusion"])}: {url}"); continue; }
            var id = ((long?)latest["id"] ?? 0).ToString(CultureInfo.InvariantCulture);
            var attempt = Math.Max(1, Int(latest["run_attempt"]));
            var jobs = Pages(get, $"repos/{repository.Trim('/')}/actions/runs/{id}/attempts/{attempt}/jobs?per_page=100", "jobs");
            if (jobs.Count == 0) problems.Add($"{workflow} run {id} lists no jobs");
            foreach (var job in jobs.Where(job => Text(job["status"]) != "completed" || Text(job["conclusion"]) != "success"))
                problems.Add($"{workflow} job {Text(job["name"])} is {Text(job["status"])}/{Text(job["conclusion"])}");
            if (ciCovered != null) CiCoverage.VerifyJobs(ciCovered, repository, workflow, latest, jobs, get);
            records.Add(new JsonObject
            {
                ["workflow"] = workflow, ["runId"] = (long?)latest["id"] ?? 0, ["runNumber"] = Int(latest["run_number"]), ["runAttempt"] = attempt,
                ["url"] = url, ["conclusion"] = "success", ["jobs"] = jobs.Count, ["updatedAt"] = Text(latest["updated_at"])
            });
        }
        if (problems.Count != 0) throw new ReleaseException("Adoption refused; CI of " + sha + " has not passed:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
        return records;
    }

    private static List<JsonObject> Pages(Func<string, string> get, string url, string property)
    {
        var rows = new List<JsonObject>();
        for (var page = 1; page <= 20; page++)
        {
            var body = JsonNode.Parse(get(url + "&page=" + page.ToString(CultureInfo.InvariantCulture))) as JsonObject ?? throw new ReleaseException("GitHub answered no JSON object: " + url);
            var items = (body[property] as JsonArray ?? throw new ReleaseException("GitHub answer lacks " + property + ": " + url)).OfType<JsonObject>().ToArray();
            rows.AddRange(items);
            if (items.Length == 0 || rows.Count >= Int(body["total_count"])) return rows;
        }
        throw new ReleaseException("GitHub listing exceeds twenty pages: " + url);
    }

    // The verified adoption a release child reads through TIA_MCP_RELEASE_ADOPTION; none when the variable is unset.
    internal static JsonObject? LoadAdoption(Func<string, string?>? environment = null)
    {
        var path = (environment ?? Environment.GetEnvironmentVariable)(AdoptionVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) throw new ReleaseException("Release adoption record missing: " + path);
        var adoption = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (adoption is null || Text(adoption["schema"]) != AdoptionSchema || adoption["record"] is not JsonObject)
            throw new ReleaseException("Release adoption record is invalid: " + path);
        return adoption;
    }

    // The same gate as a suite the release runs itself (ReadSuitePassed): passes, no failure and no skip.
    internal static int SuitePassed(JsonObject adoption, string suite)
    {
        if (adoption["record"]?["suites"]?[suite] is not JsonObject row) throw new ReleaseException("Adopted evidence has no result for suite " + suite);
        var passed = Int(row["passed"]);
        if (passed <= 0 || Int(row["failed"]) != 0 || Int(row["skipped"]) != 0) throw new ReleaseException($"Adopted suite {suite} did not meet its passed/failed/skipped gates");
        return passed;
    }

    // The eight-release record of an adopted publication: its preparation ran without -Test, so its suite counts and the
    // adoption (evidence, CI runs, the checks the release ran itself) come from the verified adoption record.
    internal static JsonObject AnnotateRecord(JsonObject record, JsonObject adoption, IReadOnlyList<string> suites)
    {
        if (record["test"] is not JsonValue test || !test.TryGetValue<bool>(out var tested) || tested)
            throw new ReleaseException("An adopted release record must come from a preparation without -Test");
        var validation = record["validation"] as JsonObject ?? throw new ReleaseException("Eight-release record has no validation section");
        var rows = new JsonObject();
        foreach (var suite in suites)
        {
            if (adoption["record"]?["suites"]?[suite] is not JsonObject row) throw new ReleaseException("Adopted evidence has no result for suite " + suite);
            if (Int(row["failed"]) != 0 || Int(row["passed"]) < Int(row["minimumPassed"]) || Int(row["skipped"]) > Int(row["maximumSkipped"]))
                throw new ReleaseException("Adopted suite does not meet its gate: " + suite);
            rows[suite] = row.DeepClone();
        }
        validation["dotnetSuites"] = rows;
        record["adoptedValidation"] = adoption["record"]!.DeepClone();
        return record;
    }

    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    internal static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    internal static int Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : node is JsonValue other && other.TryGetValue<long>(out var wide) ? (int)Math.Clamp(wide, int.MinValue, int.MaxValue) : 0;
    internal static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static int Number(JsonNode? node) => Int(node);
    private static string? HashIfPresent(string path) => File.Exists(path) ? ReleaseRecords.HashFile(path) : null;
    private static DateTimeOffset? Time(JsonNode? node) =>
        DateTimeOffset.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : null;
}
