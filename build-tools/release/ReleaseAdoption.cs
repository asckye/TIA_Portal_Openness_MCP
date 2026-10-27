using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

// Per-stage wall times of one release run, written to <run>/stage-times.json after every stage.
internal sealed class ReleaseStageClock(string directory)
{
    private readonly List<(string Stage, double Seconds, string Status)> rows = [];
    private readonly Stopwatch total = Stopwatch.StartNew();

    internal void Run(string stage, Action action)
    {
        var timer = Stopwatch.StartNew();
        var status = "failed";
        try
        {
            action();
            status = "passed";
        }
        finally
        {
            timer.Stop();
            lock (rows) rows.Add((stage, timer.Elapsed.TotalSeconds, status));
            Console.WriteLine($"STAGE {stage}: {status}; {timer.Elapsed.TotalSeconds:F1}s");
            Save();
        }
    }

    internal void Print()
    {
        lock (rows)
            foreach (var row in rows) Console.WriteLine($"  {row.Stage,-44} {row.Seconds,9:F1}s  {row.Status}");
        Console.WriteLine($"  {"total",-44} {total.Elapsed.TotalSeconds,9:F1}s");
    }

    private void Save()
    {
        JsonArray stages;
        lock (rows) stages = new JsonArray(rows.Select(row => (JsonNode?)new JsonObject
            { ["stage"] = row.Stage, ["seconds"] = Math.Round(row.Seconds, 1), ["status"] = row.Status }).ToArray());
        var document = new JsonObject { ["stages"] = stages, ["totalSeconds"] = Math.Round(total.Elapsed.TotalSeconds, 1) };
        File.WriteAllText(Path.Combine(directory, "stage-times.json"), document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }
}

internal static partial class ReleaseCommands
{
    // The suites the multi-version preparation runs with -Test; an adopted record takes their counts from the evidence.
    internal static readonly string[] MultiVersionSuites = ["foundation-api", "prompt-registration", "software-read", "special-export-shape", "device-add", "hardware-catalog", "diagnostic-membership"];

    // Independent read-only release checks run side by side; a failure stops new starts and every failure is reported.
    private static void RunReleaseParallel(IReadOnlyList<(string Name, Action Run)> checks, string logDirectory, int maxParallelism)
    {
        ParallelPipeline.Run(checks.Select(check => (check.Name, (Func<PipelineResult>)(() =>
        {
            check.Run();
            return new PipelineResult(check.Name, 0, check.Name + " passed." + Environment.NewLine, "");
        }))).ToArray(), logDirectory, maxParallelism);
    }

    // release -BumpOnly: the mechanical version bump of the prep commit, which an adopted publication requires.
    private static int BumpReleaseOnly(string version, string git)
    {
        BumpReleaseVersion(version);
        var changed = EnumerateReleaseChanges(Root, git);
        Console.WriteLine($"Version files for {version} ({changed.Length} changed path(s)):");
        foreach (var path in changed) Console.WriteLine("  " + path);
        Console.WriteLine("Add the CHANGELOG entry and release notes, commit this prep commit, push it so CI runs, run check-all -Clean -NoBuildCache on it, " +
            "then run release -AdoptEvidence <check-all output>/" + ReleaseEvidence.FileName + ".");
        return 0;
    }

    // release -AdoptEvidence: verified before anything is built or rewritten; children read the result through TIA_MCP_RELEASE_ADOPTION.
    private static JsonObject AdoptReleaseEvidence(Options options, string version, string git, string publicApiRoot, string? nugetConfig,
        string python, string logRoot, string token, bool dryRun)
    {
        if (ReleaseEvidence.PrepCommitError(ReleaseVersions.ReadVersionProps(Root).Label, version) is { } prepError) throw new ReleaseException(prepError);
        var companion = options.Get("EcosystemPython") ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        var external = ReleaseEvidence.ProbeExternal(Root, publicApiRoot, nugetConfig is null ? null : Path.GetFullPath(nugetConfig), Dotnet, python,
            string.IsNullOrWhiteSpace(companion) ? null : ResolveApplication(companion));
        var evidencePath = Path.GetFullPath(options.Get("AdoptEvidence")!);
        var repository = options.Get("Repository", "asckye/TIA_Portal_Openness_MCP");
        var adoption = ReleaseEvidence.Verify(new ReleaseEvidence.Inputs(Root, git, evidencePath, external, DateTimeOffset.UtcNow,
            dryRun ? null : coverage => VerifyReleaseCi(options.Get("ApiBaseUrl", "https://api.github.com"), token, repository,
                ReleaseEvidence.GitText(Root, git, "rev-parse", "HEAD"), coverage)));
        var record = adoption["record"]!.AsObject();
        var commit = ReleaseEvidence.Text(record["evidence"]?["commit"]);
        if (record["ci"] == null) record["ci"] = dryRun
            ? new JsonObject { ["status"] = "not verified in a dry run; publication requires " + string.Join(" and ", ReleaseEvidence.PushWorkflows) + " of " + commit }
            : VerifyReleaseCi(options.Get("ApiBaseUrl", "https://api.github.com"), token, repository, commit);
        File.Copy(evidencePath, Path.Combine(logRoot, "adopted-" + ReleaseEvidence.FileName), true);
        var path = Path.Combine(logRoot, "adopted-validation.json");
        WriteJson(path, adoption);
        Environment.SetEnvironmentVariable(ReleaseEvidence.AdoptionVariable, path);
        Console.WriteLine($"ADOPTED check-all evidence {ReleaseEvidence.Text(record["evidence"]?["file"])} (sha256 {ReleaseEvidence.Text(record["evidence"]?["sha256"])}) for {commit}; " +
            (dryRun ? "CI not verified in a dry run" : "CI runs " + string.Join(", ", record["ci"]!.AsArray().Select(run => ReleaseEvidence.Text(run?["workflow"]) + " " + run?["runId"]))));
        Console.WriteLine("  The suites, Studio tests, transport checks, PLC harness modes, soaks and preparation captures are adopted; the release builds, packages and checks its own product.");
        return adoption;
    }

    private static JsonArray VerifyReleaseCi(string apiBase, string token, string repository, string sha, JsonObject? ciCovered = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMcp-Release");
        return ReleaseEvidence.VerifyCi(url =>
        {
            using var response = client.GetAsync(apiBase.TrimEnd('/') + "/" + url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) throw new ReleaseException($"GitHub API answered HTTP {(int)response.StatusCode} for {url}");
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }, repository, sha, ReleaseEvidence.PushWorkflows, ciCovered);
    }

    // After the version step the adopted tree must still be the evidence's tree; nothing may have been rewritten.
    private static void RequireAdoptedTreeUnchanged(string git, JsonObject adoption) =>
        ReleaseEvidence.RequireUnchangedTree(Root, git, ReleaseEvidence.Text(adoption["record"]?["evidence"]?["commit"]));

    // An adopted publication commits only the records the build rewrote, on top of the commit the evidence tested.
    private static void RequireAdoptedRecordChanges(string git, JsonObject adoption) =>
        ReleaseEvidence.RequireRecordOnlyChanges(Root, git, ReleaseEvidence.Text(adoption["record"]?["evidence"]?["commit"]));

    // The release checks that check-all does not run: host behaviour parity of both majors, the native-call coverage of the installed
    // and fixture adapters and the native harness safety checks. They run on this release's own build after the preparation.
    private static void RunAdoptionLocalChecks(JsonObject adoption, string api, string python, string? nuget, string logRoot, string runTemp, int parallelism)
    {
        var local = Path.Combine(logRoot, "adoption-local");
        Directory.CreateDirectory(local);
        var cli = ReleaseCliHome(runTemp);
        var dotnet = Dotnet;
        var logs = Path.Combine(Root, "bin-build/multi-version");
        var receipts = new System.Collections.Concurrent.ConcurrentBag<JsonObject>();
        JsonObject Receipt(string name, string check, string file) => new()
        {
            ["name"] = name, ["check"] = check, ["status"] = "passed", ["path"] = Path.GetRelativePath(Root, file).Replace('\\', '/'),
            ["sha256"] = ReleaseRecords.HashFile(file), ["counts"] = ReleaseEvidence.Strings(ReleaseRecords.CountLines(Root, File.ReadAllText(file)))
        };
        var jobs = PlcReleaseJobs(Root, api, logs, runTemp, dotnet, python).Where(IsAdoptionLocalPlcJob).ToArray();
        RunReleaseParallel(
        [
            ("host-behavior-parity", () =>
            {
                // The same two runs as the preparation's -Test path, each in its own isolated data, temp and CLI folders.
                foreach (var major in new[] { "20", "21" })
                {
                    var name = "host-behavior-parity-v" + major;
                    var parityOptions = Options.Parse(["-Dotnet", dotnet, "-EngineMajor", major, "-ResultsDirectory", Path.Combine(local, name)], new HashSet<string>());
                    var log = Path.Combine(local, name + ".log");
                    var result = RunBuildIsolated(dotnet, [], log, runTemp, cli, api, nuget, false, name, environment =>
                    {
                        if (!string.IsNullOrWhiteSpace(nuget)) environment["RestoreConfigFile"] = Path.GetFullPath(nuget);
                        return HostParity.Run(Root, parityOptions, environment);
                    });
                    ProcessRunner.RequireSuccess(result, "Host behavior parity gate failed: V" + major);
                    receipts.Add(Receipt(name, "offline-suites", log));
                }
            }),
            ("plc-local", () =>
            {
                RunPlcReleaseJobs(jobs, Root, logs, job =>
                {
                    ResetPlcReleaseJobOutputs(job, logs);
                    return RunBuildIsolated(job.Executable, job.Arguments, Path.Combine(local, job.Name + ".log"), runTemp, cli, api, nuget, false, job.Name);
                }, (script, _) => throw new ReleaseException("Adopted local PLC checks include no capture: " + script), parallelism: Math.Max(1, Math.Min(6, parallelism)));
                foreach (var job in jobs) receipts.Add(Receipt(job.Name, job.Check, Path.Combine(local, job.Name + ".log")));
            })
        ], Path.Combine(local, "pipeline"), 2);
        var target = adoption["record"]!["localChecks"]!.AsArray();
        foreach (var receipt in receipts.OrderBy(row => ReleaseEvidence.Text(row["name"]), StringComparer.Ordinal)) target.Add(receipt);
        WriteJson(Environment.GetEnvironmentVariable(ReleaseEvidence.AdoptionVariable) ?? throw new ReleaseException("Adoption record path missing"), adoption);
    }

    internal static bool IsAdoptionLocalPlcJob(PlcReleaseJob job) =>
        job.Check == "native-coverage" || job.Name.StartsWith("jit-adapter-coverage-", StringComparison.Ordinal) || job.Mode == "native-safety";

    // build-release under adoption: the suite counts of its record come from the evidence; the self-tests that report counts and the
    // tool usage check still run here.
    internal sealed record AdoptedCommonCounts(int WriteGuard, int CrashEvidence, int Offline, int OfflineV20, int KeptCode, int KeptCodeV20,
        int VersionPolicy, int Updater, int NativeMcpSafety, Match Lifecycle);

    private static AdoptedCommonCounts AdoptedCommonValidation(JsonObject adoption, string dotnet, string python, string outputDirectory, string runTemp, string cliHome, string apiRoot)
    {
        RunBuildSpec("approval-gate-self-test", python, [Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var lifecycle = RunBuildSpec("native-supervisor", python, [Path.Combine(Root, "scripts/checks/Test-NativeLifecycle.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var lifecycleMatch = Regex.Match(lifecycle, "COMPLETE: (\\d+) native supervisor checks passed; live TIA tests NOT RUN");
        if (!lifecycleMatch.Success || int.Parse(lifecycleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 1)
            throw new ReleaseException("Native supervisor offline checks incomplete");
        var mcpSafetyText = RunBuildSpec("native-mcp-safety", python, [Path.Combine(Root, "scripts/checks/Test-NativeMcpSession.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var nativeMcpSafety = RequireCount("nativeMcpSafety", mcpSafetyText, "COMPLETE: (\\d+) native MCP safety checks passed");
        RunBuildSpec("tool-usage", dotnet, [typeof(ReleaseCommands).Assembly.Location, "generate-tool-usage", "-Check"], outputDirectory, runTemp, cliHome, apiRoot);
        // The suites of this stage, each under the gate ReadSuitePassed applies to a run of its own.
        _ = ReleaseEvidence.SuitePassed(adoption, "source-contracts");
        foreach (var suite in ReleaseCommandTable.OfflineSuites) _ = ReleaseEvidence.SuitePassed(adoption, suite);
        return new AdoptedCommonCounts(ReleaseEvidence.SuitePassed(adoption, "write-guard"), ReleaseEvidence.SuitePassed(adoption, "crash-evidence"),
            ReleaseEvidence.SuitePassed(adoption, "offline"), ReleaseEvidence.SuitePassed(adoption, "offline-v20"),
            ReleaseEvidence.SuitePassed(adoption, "kept-code"), ReleaseEvidence.SuitePassed(adoption, "kept-code-v20"),
            ReleaseEvidence.SuitePassed(adoption, "version-policy"), ReleaseEvidence.SuitePassed(adoption, "updater"), nativeMcpSafety, lifecycleMatch);
    }

    // build-multi-version -CompleteOnly under adoption: the record names the evidence and carries the adopted suite counts.
    private static void AnnotateAdoptedRecord(string recordPath)
    {
        if (ReleaseEvidence.LoadAdoption() is not { } adoption) return;
        var record = JsonNode.Parse(File.ReadAllText(recordPath)) as JsonObject ?? throw new ReleaseException("Eight-release record is not a JSON object");
        WriteJson(recordPath, ReleaseEvidence.AnnotateRecord(record, adoption, MultiVersionSuites));
    }

    // check-release-evidence: the release's adoption checks, read-only, on this checkout; -Ci also reads GitHub Actions of the commit.
    private static int CheckReleaseEvidence(Options options)
    {
        var evidence = options.Get("Evidence") ?? throw new ReleaseException("-Evidence <check-all output>/" + ReleaseEvidence.FileName + " is required", 64);
        var api = Path.GetFullPath(options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required", 64));
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        var companion = options.Get("EcosystemPython") ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        var git = options.Get("Git", "git");
        var external = ReleaseEvidence.ProbeExternal(Root, api, nuget is null ? null : Path.GetFullPath(nuget), Dotnet, options.Get("Python", Py),
            string.IsNullOrWhiteSpace(companion) ? null : ResolveApplication(companion));
        string? token = null;
        string Token()
        {
            token ??= ReleasePrerequisites.GetToken(options.Get("Token") ?? "", ResolveApplication(git), Root, localOnly: false);
            ReleasePrerequisites.RequireToken(token);
            return token;
        }
        var adoption = ReleaseEvidence.Verify(new ReleaseEvidence.Inputs(Root, git, evidence, external, DateTimeOffset.UtcNow,
            options.Has("Ci") ? coverage => VerifyReleaseCi(options.Get("ApiBaseUrl", "https://api.github.com"), Token(), options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"),
                ReleaseEvidence.GitText(Root, git, "rev-parse", "HEAD"), coverage) : null));
        var record = adoption["record"]!.AsObject();
        var commit = ReleaseEvidence.Text(record["evidence"]?["commit"]);
        Console.WriteLine($"Evidence accepted for {commit}: {record["evidence"]?["jobs"]} passed jobs, {record["suites"]!.AsObject().Count} registered suites, " +
            $"mode {ReleaseEvidence.Text(record["evidence"]?["mode"])}, {record["evidence"]?["wallSeconds"]}s.");
        if (!options.Has("Ci"))
        {
            Console.WriteLine("CI not checked; pass -Ci to read " + string.Join(" and ", ReleaseEvidence.PushWorkflows) + " of the commit (token from -Token, GITHUB_TOKEN or Git).");
            return 0;
        }
        foreach (var run in record["ci"] as JsonArray ?? VerifyReleaseCi(options.Get("ApiBaseUrl", "https://api.github.com"), Token(), options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"), commit))
            Console.WriteLine($"CI {ReleaseEvidence.Text(run?["workflow"])}: run {run?["runId"]} attempt {run?["runAttempt"]} success, {run?["jobs"]} jobs; {ReleaseEvidence.Text(run?["url"])}");
        return 0;
    }
}
