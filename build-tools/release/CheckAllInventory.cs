using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    // D5 preserves the composite engine service/registration graph until the
    // maintainer assigns its assertions individually; it has no kept Portal.
    internal static IReadOnlyDictionary<string, string> CheckAllHarnessD5() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["harness-v20-engineering-api-only"] = "2840 engine Portal/service/registration shape assertions; retained adapter API checks remain in foundation-api; individual D5 mapping required before P8-04l",
        ["harness-v21-engineering-api-only"] = "3126 engine Portal/service/registration shape assertions; retained adapter API checks remain in foundation-api; individual D5 mapping required before P8-04l"
    };

    internal static IReadOnlyDictionary<string, string> CheckAllHarnessSuccessors() => new[] { "20", "21" }
        .SelectMany(key => CheckAllHarnessModes(key).Select(mode => "harness-v" + key + "-" + (mode.Length == 0 ? "http" : mode)))
        .Where(name => !CheckAllHarnessD5().ContainsKey(name))
        .ToDictionary(name => name, name => name + "-plc", StringComparer.Ordinal);

    // These successor modes also contain assertions about deleted engine-only
    // subjects. P8-04l must obtain D5 approval before removing their engine half.
    internal static IReadOnlyDictionary<string, string> CheckAllHarnessD5Assertions() => new[] { "20", "21" }
        .SelectMany(key => new[] {
            new KeyValuePair<string, string>("harness-v" + key + "-software-lookup-only", "D5: Portal resolver/cache/IL and EmptyPlcGroupDeletion have no identical kept subject; adapter exact-selection and shared PlcListingRead assertions run in the successor"),
            new KeyValuePair<string, string>("harness-v" + key + "-native-diagnostics-only", "D5: engine woven inventory, MigrationPages timer and in-process engine/adapter sink/projection identity disappear; adapter JIT/inventory/journal, host SDK guards and native reader run in the successor"),
            new KeyValuePair<string, string>("harness-v" + key + "-example-library-only", "D5: PatchProject.ParsePatchSpec is retired with the engine CLI; kept V4 JSON parser null/malformed rejections run in the successor"),
            new KeyValuePair<string, string>("harness-v" + key + "-descriptor-catalog-only", "D5: ConnectProject remains an engine-only retired alias; successor checks the host's exact descriptor/execution coverage plus its explicit removed-alias set")
        }).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal static void ValidateCheckAllHarnessSuccessors(IReadOnlyList<CheckJob> jobs)
    {
        var names = jobs.Select(job => job.Name).ToArray();
        var successors = CheckAllHarnessSuccessors(); var decisions = CheckAllHarnessD5();
        var expected = successors.Keys.Concat(successors.Values).Concat(decisions.Keys).ToHashSet(StringComparer.Ordinal);
        if (names.Any(name => name.StartsWith("harness-v", StringComparison.Ordinal) && !expected.Contains(name)))
            throw new ReleaseException("Unknown engine harness successor job");
        if (CheckAllHarnessD5Assertions().Any(pair => !successors.ContainsKey(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ReleaseException("Unmapped engine-only harness assertion group");
        foreach (var key in new[] { "20", "21" })
        foreach (var mode in CheckAllHarnessModes(key))
        {
            string engine = "harness-v" + key + "-" + (mode.Length == 0 ? "http" : mode);
            if (names.Count(name => name == engine) != 1 || successors.ContainsKey(engine) == decisions.ContainsKey(engine)
                || (successors.TryGetValue(engine, out var plc) && (names.Count(name => name == plc) != 1
                    || Array.IndexOf(names, plc) != Array.IndexOf(names, engine) + 1)))
                throw new ReleaseException("Incomplete engine harness successor/D5 mapping: " + engine);
        }
    }

    // P8-04l retires only the engine half of this bijection, after both halves pass.
    internal static IReadOnlyDictionary<string, string> CheckAllScriptSuccessors()
    {
        var names = new List<string>();
        foreach (var key in new[] { "20", "21" })
        {
            foreach (var concurrency in new[] { "8", "16" })
            foreach (var transport in new[] { "stdio", "http" })
            foreach (var profile in new[] { "full", "lite" })
                names.Add($"stability-v{key}-c{concurrency}-{transport}-{profile}");
            foreach (var family in new[] { "contracts", "responses", "transport-usage", "usage", "campaign-inputs", "approval", "resources", "ecosystem" })
                names.Add(family + "-v" + key);
        }
        names.Add("usage-audit");
        return names.ToDictionary(name => name, name => name + "-plc", StringComparer.Ordinal);
    }

    internal static string CheckAllPlcSuccessor(string engineJob) => CheckAllScriptSuccessors().TryGetValue(engineJob, out var successor)
        ? successor : throw new ReleaseException("Unmapped engine script job: " + engineJob);

    internal static void ValidateCheckAllScriptSuccessors(IReadOnlyList<CheckJob> jobs)
    {
        var names = jobs.Select(job => job.Name).ToArray();
        foreach (var (engine, plc) in CheckAllScriptSuccessors())
            if (names.Count(name => name == engine) != 1 || names.Count(name => name == plc) != 1)
                throw new ReleaseException("Incomplete engine script successor pair: " + engine + " -> " + plc);
    }

    private static string CheckAllPlcFixtureRoot => Path.GetFullPath(Path.Combine(Root, "bin-build", "check-plc-fixtures"));
    internal static string CheckAllPlcFixture(string release) => PlcWorkerFixtures.Candidate(release, "SOURCE", "current").Worker(CheckAllPlcFixtureRoot);

    internal static CheckBuild[] CheckAllPlcFixtureBuilds(string apiRoot) => new[] { "20", "21" }.Select(key =>
    {
        var fixture = PlcWorkerFixtures.Candidate(key, "SOURCE", "current");
        return new CheckBuild("src/PlcWorker/TiaMcp.PlcWorker.csproj", "plc-sdk-" + key,
            // Same API directory as the production worker units and build-plc-worker-fixtures; inventory needs no SDK present.
            ["-p:TiaReleaseKey=" + key, "-p:SiemensEngineeringDirectory=" + Path.GetFullPath(Path.Combine(apiRoot, PlcWorkerApi(key).Directory)),
                .. fixture.Properties(CheckAllPlcFixtureRoot)]);
    }).ToArray();

    internal static JsonObject CheckAllPreflightSuite(JsonObject catalog)
    {
        var suite = catalog["release-tool"]!.DeepClone().AsObject();
        suite["arguments"] = new JsonArray("-p:UseAppHost=false", "-p:UseSharedCompilation=false");
        return suite;
    }

    internal static void AddCheckAllPreflightJobs(List<CheckJob> jobs)
    {
        // Reuse the authoritative standalone jobs, including the complete release
        // suite's Pipeline assertions. A missing mapping fails before scheduling.
        var coverage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repository, links, shipped-document links, CHANGELOG version"] = "repository",
            ["repository check self-tests"] = "repository-self-tests",
            ["dead tool references (descriptions and current docs)"] = "dead-references",
            ["product catalogs generator"] = "catalog-drift",
            ["documentation tool counts"] = "tool-counts-drift",
            ["tool usage catalog generator"] = "usage-drift",
            ["release migration tables generator"] = "migration-drift",
            ["V4 contract snapshots"] = "contract-verify",
            ["V4 response snapshots"] = "response-verify",
            ["suite runner self-tests"] = "suite-runner-self-tests",
            ["release approval gate self-tests"] = "approval-self-tests",
            ["relocation checker self-tests"] = "relocation-self-tests",
            ["strict bundle rules"] = "bundle",
            ["release documentation rules"] = "release-documentation",
            ["release tool test suite"] = "release-tool-gate",
            ["parallel pipeline behavior"] = "release-tool-gate"
        };
        var checks = PreflightChecks();
        if (checks.Count != coverage.Count || checks.Any(check => !coverage.TryGetValue(check.Name, out var name) || !jobs.Any(job => job.Name == name)))
            throw new ReleaseException("Incomplete check-all preflight coverage");
        var dependencies = checks.Select(check => coverage[check.Name]).Distinct().ToArray();
        jobs.Add(new("preflight", () => new(0, $"PREFLIGHT: {checks.Count} passed, 0 failed (shared executions)\n", ""), dependencies));
    }

    private static void AddCheckAllStaticJobs(Action<string, Func<CommandResult>, string[]?, bool> add,
        Func<string, string[], CommandResult> tool, Func<string, string, string[], CommandResult> python,
        Func<string, string, string[], bool, CommandResult> isolated, string output, string api, string nuget, int parallelism, int shards, string dotnet)
    {
        void Tool(string name, params string[] arguments) => add(name, () => tool(name, arguments), null, false);
        void Python(string name, string script, params string[] arguments) => add(name, () => python(name, script, arguments), null, false);
        Tool("catalog-drift", "generate-catalogs", "-Check");
        Tool("tool-counts-drift", "generate-tool-counts", "-Check");
        Tool("usage-drift", "generate-tool-usage", "-Check");
        Tool("migration-drift", "generate-release-migration", "-Check");
        Tool("tools-list-self-tests", "check-tools-list", "-SelfTest", "-NoBuild");
        Tool("tools-list", "check-tools-list");
        Tool("script-calls", "check-script-tool-calls");
        Tool("dead-references", "check-dead-tool-references");
        Tool("repository", "check-repository", "-NoBinaries");
        Tool("repository-self-tests", "check-repository", "-SelfTest", "-NoBuild");
        Tool("suite-runner-self-tests", "test-suites", "-SelfTest");
        Tool("adapter-boundary", "check-adapter-boundary");
        Tool("tia-features", "check-tia-features");
        Tool("bundle", "validate-bundle", "-Strict", "-NoBinaries", "-SkipSourceHashes");
        Tool("bundle-self-tests", "validate-bundle", "-SelfTest", "-NoBuild");
        Tool("release-documentation", "release", "-DocumentationOnly");
        Tool("release-dry-run", "run-release-build", "-Tier", "full", "-DryRun");
        add("preflight", () => tool("preflight", ["preflight", "-NoBuild", "-MaxParallelism", parallelism.ToString(), "-Shards", shards.ToString()]), null, true);
        Tool("production-compile-only", "build-release", "-CompileOnly", "-NoBuild", "-NuGetConfig", nuget,
            "-V20ReferenceRoot", ResolveReleaseApi(null, api, 20), "-V21ReferenceRoot", ResolveReleaseApi(null, api, 21));
        Python("python-repository", "scripts/checks/Check-Repository.py", "--no-binaries");
        Python("python-script-self-tests", "scripts/checks/Check-ScriptToolCalls.py", "--self-test");
        Python("python-script-calls", "scripts/checks/Check-ScriptToolCalls.py");
        Python("campaign-runner-self-tests", "scripts/checks/Test-CampaignInputs.py", "--self-test");
        Python("plc-script-self-tests", "scripts/checks/Test-PlcScriptSuccessors.py");
        Python("plc-harness-self-tests", "scripts/checks/Test-PlcHarnessSuccessors.py");
        // The runner finds the file program compiled by build-once below the original TEMP, not the job's private TEMP.
        Python("campaign-validator-self-tests", "scripts/checks/Test-CampaignInputs.py", "--validator-self-test");
        Python("domain-source", "scripts/checks/Test-DomainTools.py", "--source-only");
        Python("contract-self-tests", "scripts/checks/Snapshot-ToolContracts.py", "self-test");
        Python("contract-verify", "scripts/checks/Snapshot-ToolContracts.py", "verify");
        Python("response-verify", "scripts/checks/Snapshot-ToolResponses.py", "verify");
        Python("native-lifecycle", "scripts/checks/Test-NativeLifecycle.py", "--self-test");
        Python("native-mcp-session", "scripts/checks/Test-NativeMcpSession.py", "--self-test");
        Python("approval-self-tests", "scripts/checks/Test-ReleaseApprovalGate.py", "--self-test");
        Python("relocation-self-tests", "scripts/checks/Test-RelocatedBundle.py", "--self-test");
        add("static-python-csharp-parity", () =>
        {
            var directory = Path.Combine(Root, "bin-build/p", Guid.NewGuid().ToString("N")[..8]);
            var result = isolated("static-python-csharp-parity", dotnet,
                [Path.Combine(Root, "bin-build/check-scripts/Test-StaticCheckParity/Test-StaticCheckParity.dll"), "-Python", Py, "-NoBuild", "-ReleaseTool", typeof(ReleaseCommands).Assembly.Location, "-Output", directory], true);
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(output, "static-parity", Path.GetRelativePath(directory, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination, true);
                }
            return result;
        }, null, false);
        add("match-plc-name-source", () => isolated("match-plc-name-source", dotnet,
            [Path.Combine(Root, "bin-build/check-scripts/Test-MatchPlcName/Test-MatchPlcName.dll"), "-SourceOnly"], true), null, false);
        // This console project isn't a registered xUnit suite; dotnet test would
        // silently execute none of its configuration-page assertions.
        add("configuration-page", () => isolated("configuration-page", dotnet,
            ["run", "--project", Path.Combine(Root, "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj"), "-c", "Release", "--no-build", "--no-restore"], true), null, true);
    }

    internal static string[] CheckAllHarnessModes(string release) =>
    ["example-library-only", "software-lookup-only", "engineering-api-only", "", "hmi-only", "native-diagnostics-only", "process-leases-only",
        "native-export-only", "hmi-snapshot-only", "global-script-only", "graphic-selection-only", "runtime-settings-only", "test-migration-read-assembly", "test-ecosystem-assembly",
        "descriptor-catalog-only", "check-modes-self-test",
        .. release == "21" ? new[] { "test-download-route", "test-match-plc-name" } : []];

    private static string[] HarnessArguments(string mode, string key, string api) => mode switch
    {
        "" => [],
        "hmi-only" => [mode, key, ((string?)System.Xml.Linq.XDocument.Load(Path.Combine(Root, "Version.props")).Root!.Element("PropertyGroup")!.Element("TiaMcpRelease")) + ".0"],
        "example-library-only" or "process-leases-only" or "native-export-only" or "hmi-snapshot-only" or "test-match-plc-name" => [mode],
        _ => [mode, api]
    };

    internal static string[] CheckAllCandidateModes() => ["device", "import", "session", "save-close", "compile", "fallback", "source", "export"];

    internal static CheckBuild CheckAllCandidateBuild(string release, string mode)
    {
        var family = mode == "save-close" ? "CLOSE" : mode.ToUpperInvariant();
        return new($"src/Engine/TiaMcp.Engine.V{release}.csproj", "candidate-" + mode + "-" + release,
            ["-p:TiaMcpTestPolicy=P6-" + family + ":safe-v4"]);
    }

    private static CommandResult CheckAllHarness(CommandResult result, string mode, string key)
    {
        if (result.ExitCode != 0) return result;
        switch (mode)
        {
            case "software-lookup-only": RequireCount("software lookup", result.StandardOutput, "COMPLETE: (\\d+) software lookup checks passed", "softwareLookup"); break;
            case "engineering-api-only": RequireCount("engineering API", result.StandardOutput, "COMPLETE: (\\d+) engineering API checks passed", key == "20" ? "engineeringApiV20" : "engineeringApiV21"); break;
            case "": RequireCount("HTTP", result.StandardOutput, "COMPLETE: (\\d+) Foundation transport checks passed"); break;
            case "hmi-only": if (!Regex.IsMatch(result.StandardOutput, "(\\d+) HMI traversal assertions, 0 failed")) throw new ReleaseException("Incomplete HMI traversal"); break;
            case "process-leases-only": RequireCount("process leases", result.StandardOutput, "COMPLETE: (\\d+) process lease checks passed", "processLeases"); break;
            case "native-export-only": RequireCount("native export", result.StandardOutput, "COMPLETE: (\\d+) native export remoting checks passed"); break;
            case "hmi-snapshot-only": RequireCount("HMI snapshot", result.StandardOutput, "COMPLETE: (\\d+) HMI snapshot remoting checks passed"); break;
            case "global-script-only": RequireCount("global script", result.StandardOutput, "COMPLETE: (\\d+) global script bridge checks passed", key == "21" ? "globalScriptV21" : null); break;
            case "graphic-selection-only": RequireCount("graphic selection", result.StandardOutput, "COMPLETE: (\\d+) graphical selection checks passed", "graphicSelection"); break;
            case "runtime-settings-only": RequireCount("runtime settings", result.StandardOutput, "COMPLETE: (\\d+) runtime settings checks passed", key == "20" ? "runtimeSettingsV20" : "runtimeSettingsV21"); break;
            case "test-ecosystem-assembly": RequireCount("ecosystem assembly", result.StandardOutput, "COMPLETE: (\\d+) ecosystem assembly checks passed", "ecosystem"); break;
            case "test-migration-read-assembly": if (!result.StandardOutput.Contains("migration assembly checks passed", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Incomplete migration assembly"); break;
            // The fixture checks JIT, open generics and native journal assertions
            // internally; its release wrapper's inventory equality is added below.
        }
        return result;
    }

    private static void ValidateCheckAllApproval(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var evidence = doc.RootElement;
        CheckReleaseCount("approvalSafety", GetJsonInt(evidence, "checksPassed"), "Default approval checks incomplete");
        var results = evidence.GetProperty("results");
        if (GetJsonString(evidence, "status") != "passed" || GetJsonInt(evidence, "checksExpected") != 7 || GetJsonBool(evidence, "workbenchConnected") ||
            GetJsonBool(evidence, "tiaConnected") || GetJsonString(results, "direct") != "refused-before-dispatch; read-succeeded" ||
            GetJsonString(results, "CallTool") != "refused-before-dispatch") throw new ReleaseException("Default approval evidence mismatch");
    }

    private static void ValidateCheckAllJit(string text, string? enginePath, string adapterPath)
    {
        foreach (var (prefix, path) in enginePath == null ? new[] { ("adapter ", adapterPath) } : new[] { ("", enginePath), ("adapter ", adapterPath) })
        {
            using var evidence = JsonDocument.Parse(File.ReadAllText(path));
            var match = Regex.Match(text, "COMPLETE: (\\d+) " + prefix + "native diagnostic wrappers JIT prepared; (\\d+) open generic wrappers");
            if (!match.Success || int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) != GetJsonInt(evidence.RootElement, "count"))
                throw new ReleaseException(prefix + "diagnostic wrapper JIT/inventory mismatch");
        }
        RequireCount("adapter journal", text, "COMPLETE: (\\d+) adapter integration diagnostic checks passed", "adapterJournal");
        RequireCount("native journal", text, "COMPLETE: (\\d+) native journal reader checks passed", "nativeJournalReader");
    }

    // A harness successor keeps its engine job's count gates (CheckAllHarness) and, like the script successors,
    // proves which host and PlcWorker produced the result. Its native-diagnostics run has no engine inventory:
    // the adapter JIT/inventory equality and both journal gates are the engine job's, plus all four SDK guards.
    internal static void ValidateCheckAllPlcHarness(string text, string directory, string mode, string key, string host, string worker, string adapterInventory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json"))); var evidence = doc.RootElement;
        string workerHash = ReleaseRecords.HashFile(worker);
        if (GetJsonString(evidence, "mode") != (mode.Length == 0 ? "http" : mode) || GetJsonString(evidence, "release") != key
            || !evidence.TryGetProperty("nativeTiaExecuted", out var native) || native.ValueKind != JsonValueKind.False
            || GetJsonString(evidence, "workerModel") != "foundation-host-plc-worker"
            || GetJsonString(evidence, "runtimeSha256") != workerHash || GetJsonString(evidence, "sdkWorkerSha256") != workerHash
            || GetJsonString(evidence, "hostSha256") != ReleaseRecords.HashFile(host))
            throw new ReleaseException("PLC harness evidence mismatch: " + directory);
        if (mode != "native-diagnostics-only") return;
        ValidateCheckAllJit(text, null, adapterInventory);
        if (RequireCount("SDK guard", text, "COMPLETE: (\\d+) direct SDK guard checks passed") != 4) throw new ReleaseException("SDK guard checks incomplete");
    }
}
