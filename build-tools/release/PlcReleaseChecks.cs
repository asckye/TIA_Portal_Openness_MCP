using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;

namespace TiaMcp.ReleaseTool;

internal sealed record PlcReleaseJob(string Name, string Check, string Executable, string[] Arguments,
    string Evidence = "", string Mode = "", string Release = "");

internal static partial class ReleaseCommands
{
    // One installed worker layout for every release.
    internal static string PlcReleaseInstallDirectory(string root, string key) =>
        Path.Combine(root, "runtime/v" + key, "worker");

    private static string ReleasePlcFixture(string logs, string key) =>
        new PlcWorkerFixtureBuild(key, "sdk").Worker(Path.Combine(logs, "plc-fixtures"));

    internal static string PreparePlcReleaseFilePrograms(string root, string dotnet, string? nuget, string logs,
        string systemTemp, SuiteProcess? process = null)
    {
        systemTemp = Path.GetFullPath(systemTemp);
        Directory.CreateDirectory(systemTemp);
        Directory.CreateDirectory(logs);
        // Test-PlcHarness.py and Test-CampaignInputs.py start their file apps with dotnet run --file --no-build, which
        // finds the program below TEMP. Compile into the caller-owned cache and export the same path to jobs;
        // their private TEMP directories may be removed before a later stage starts.
        // Same arguments as build-file-programs: a file-based build rejects -m and -nodeReuse.
        foreach (var program in PlcReleaseFilePrograms)
        {
            var arguments = new List<string> { "build", Path.Combine(root, program), "-c", "Release", "--nologo", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false" };
            if (!string.IsNullOrWhiteSpace(nuget)) arguments.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
            var built = (process ?? DotnetSuites.RunProcess)(dotnet, arguments, root, new Dictionary<string, string?>
            {
                ["TEMP"] = systemTemp, ["TMP"] = systemTemp, ["TMPDIR"] = systemTemp,
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1"
            });
            WriteLog(Path.Combine(logs, Path.GetFileNameWithoutExtension(program) + "-plc-build.log"), built);
            ProcessRunner.RequireSuccess(built, "PLC file app build: " + program);
        }
        return systemTemp;
    }

    internal static readonly string[] PlcReleaseFilePrograms = ["scripts/checks/Test-PlcHarness.cs", "scripts/checks/Test-CampaignInputs.cs"];
    internal const string PlcReleaseSystemTempVariable = "TIA_MCP_CHECK_SYSTEM_TEMP_ROOT";

    internal static void InstallReleasePlcWorker(string root, string key)
    {
        if (!PlcWorkerFixtures.Keys.Contains(key, StringComparer.Ordinal)) throw new ReleaseException("Unknown PLC release: " + key);
        var source = Path.Combine(root, $"src/PlcWorker/bin/{key}/Release/net48");
        var destination = PlcReleaseInstallDirectory(root, key);
        if (!Directory.Exists(source)) throw new ReleaseException("Worker build output missing: " + source);
        Directory.CreateDirectory(destination);
        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new ReleaseException("Worker staging must not be a reparse point: " + destination);
        var payload = Directory.EnumerateFiles(source).Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".config").ToArray();
        foreach (var path in payload)
            if (Path.GetFileName(path).StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                throw new ReleaseException("Siemens PublicAPI redistribution is forbidden: " + path);
        if (payload.Count(path => Path.GetExtension(path) == ".exe") != 1 ||
            !payload.Any(path => Path.GetFileName(path) == $"TiaMcp.PlcWorker.{key}.exe"))
            throw new ReleaseException("Exactly one matching PLC executable is required: " + key);
        // These directories are build staging owned by this checkout. An older
        // engine's dependency closure must not survive beside the new worker.
        var names = payload.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in Directory.EnumerateFiles(destination))
            if (!names.Contains(Path.GetFileName(old))) File.Delete(old);
        foreach (var path in payload)
        {
            if (Path.GetFileName(path).StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                throw new ReleaseException("Siemens PublicAPI redistribution is forbidden: " + path);
            File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), true);
        }
        if (!File.Exists(Path.Combine(destination, $"TiaMcp.PlcWorker.{key}.exe")) ||
            Directory.EnumerateFiles(destination, "TiaMcp.Adapter.*.dll").Count() != 1 ||
            !File.Exists(Path.Combine(destination, $"TiaMcp.Adapter.{key}.dll")))
            throw new ReleaseException("Exactly one matching PLC worker/adapter is required: " + key);
        if (key is "20" or "21")
        {
            var staging = Path.GetFullPath(Path.Combine(root, "runtime/v" + key, "plc-worker"));
            var boundary = Path.GetFullPath(Path.Combine(root, "runtime")) + Path.DirectorySeparatorChar;
            if (!staging.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new ReleaseException("Legacy PLC staging escaped the runtime directory");
            if (Directory.Exists(staging))
            {
                if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(staging).Any())
                    throw new ReleaseException("Unexpected legacy PLC staging directory: " + staging);
                foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    // Preparation and its tests share this command inventory.
    internal static IReadOnlyList<PlcReleaseJob> PlcReleaseJobs(string root, string apiRoot, string logs,
        string temp, string dotnet, string python, string? weaver = null)
    {
        var jobs = new List<PlcReleaseJob>();
        var harness = Path.Combine(root, "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe");
        weaver ??= Path.Combine(root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll");
        foreach (var key in new[] { "20", "21" })
        {
            var api = Path.Combine(apiRoot, PlcWorkerApi(key).Directory);
            var worker = ReleasePlcFixture(logs, key);
            var host = Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe");
            var inventory = Path.Combine(logs, "adapter-coverage-v" + key + "-plc.json");
            void Add(string name, string check, string script, string[] args, string evidence = "", string mode = "") =>
                jobs.Add(new(name + "-v" + key + "-plc", check, python, [Path.Combine(root, "scripts/checks", script), .. args], evidence, mode, key));
            jobs.Add(new("native-coverage-v" + key + "-plc", "native-coverage", dotnet,
                [weaver, "verify", Path.Combine(PlcReleaseInstallDirectory(root, key), $"TiaMcp.Adapter.{key}.dll"), Path.Combine(logs, "native-coverage-v" + key + "-plc.json")], Release: key));
            jobs.Add(new("adapter-native-coverage-v" + key + "-plc", "native-coverage", dotnet,
                [weaver, "verify", Path.Combine(Path.GetDirectoryName(worker)!, "TiaMcp.Adapter." + key + ".dll"), inventory], Release: key));
            // Diagnostics consumes the inventory even in a quick native-diagnostics-only plan.
            jobs.Add(new("jit-adapter-coverage-v" + key + "-plc", "native-diagnostics", dotnet,
                [weaver, "verify", Path.Combine(Path.GetDirectoryName(worker)!, "TiaMcp.Adapter." + key + ".dll"), inventory], Release: key));
            foreach (var mode in PlcHarnessModes(key))
            {
                var name = PlcHarnessJob(key, mode);
                var check = mode == "native-diagnostics-only" ? "native-diagnostics" : "plc-functional";
                jobs.Add(new(name, check, python, [Path.Combine(root, "scripts/checks/Test-PlcHarness.py"),
                    "--release", key, "--mode", mode.Length == 0 ? "http" : mode, "--host", host, "--worker", worker,
                    "--harness", harness, "--api", api, "--public-api-root", apiRoot, "--adapter-inventory", inventory,
                    "--output", Path.Combine(logs, name)], Mode: mode, Release: key));
            }
            foreach (var suite in new[] { "worker-robustness", "engine-host", "foundation-api" })
                jobs.Add(new((suite == "foundation-api" ? "engineering-api" : suite) + "-v" + key + "-plc", suite == "worker-robustness" ? "plc-worker-isolation" : suite == "engine-host" ? "http-concurrency" : "plc-functional", "test-suites",
                    // foundation-api does not depend on the release: offline-suites and both engineering-api successors name the
                    // same sharded run and registered suite gate.
                    suite == "foundation-api" ? FoundationApiSuiteArguments(dotnet, apiRoot, logs)
                        : ["-Suite", suite, "-Dotnet", dotnet, "-ResultsDirectory", Path.Combine(logs, suite + "-v" + key + "-plc")], Release: key));
            jobs.Add(new("native-safety-v" + key + "-plc", "offline-suites",
                Path.Combine(root, $"tests/Engine/TiaMcp.Engine.Native.Tests/V{key}/bin/Release/net48/TiaMcp.Engine.Native.Tests.V{key}.exe"), ["--self-test"], Mode: "native-safety", Release: key));
            Add("worker-protocol", "plc-worker-isolation", "Test-FoundationTransport.py",
                ["--plc-worker", "--host-exe", host, "--releases", key, "--plc-sdk-worker", key + "=" + worker,
                 "--public-api-root", apiRoot, "--temp-root", Path.Combine(temp, "transport-v" + key + "-plc"), "--output", Path.Combine(logs, "transport-v" + key + "-plc")], mode: "worker-protocol");
            var approval = Path.Combine(logs, "approval-v" + key + "-plc");
            Add("approval", "plc-approval", "Test-ReleaseApprovalGate.py", ["--product", "engine", "--major", key,
                "--exe", host, "--portal-root", api, "--public-api", api, "--host-harness", harness, "--plc-worker", worker,
                "--temp-root", temp, "--output", approval], Path.Combine(approval, "result.json"));
            Add("resources", "resource-discovery", "Test-ResourceDiscovery.py", ["--exe", host, "--portal-root", api, "--major", key,
                "--plc-worker", worker, "--public-api", api, "--usage-output", Path.Combine(logs, "resources-v" + key + "-plc.json")], mode: "resources");
            var ecosystem = Path.Combine(logs, "ecosystem-v" + key + "-plc");
            Add("ecosystem", "plc-ecosystem", "Test-V21Ecosystem.py", ["--exe", host, "--major", key, "--host-harness", harness,
                "--public-api", api, "--schema-root", Path.Combine(Path.GetDirectoryName(Path.Combine(apiRoot, PlcWorkerApi("21").Directory))!, "Schemas"),
                "--plc-worker", worker, "--output", ecosystem], ecosystem);
            Add("usage", "resource-discovery", "Test-ToolUsage.py", ["--foundation-host", host, "--plc-worker", key + "=" + worker,
                "--public-api-root", apiRoot, "--releases", key, "--evidence", Path.Combine(logs, "usage-v" + key + "-plc"),
                "--coverage-output", Path.Combine(logs, "usage-v" + key + "-plc.json")]);
            Add("campaign-inputs", "plc-functional", "Test-CampaignInputs.py", ["--exe", host, "--major", key,
                "--public-api", api, "--plc-worker", worker, "--output", Path.Combine(logs, "campaign-v" + key + "-plc")]);
            foreach (var kind in new[] { "contracts" })
            foreach (var source in new[] { false, true })
            foreach (var transport in new[] { "stdio", "http" })
            {
                var selectedHost = source ? Path.Combine(root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe") : host;
                var name = kind + (source ? "-source" : "") + "-" + transport;
                var output = Path.Combine(logs, name + "-v" + key + "-plc");
                const string script = "Snapshot-ToolContracts.py";
                Add(name, "plc-" + kind, script, ["capture", "--repo-root", root, "--public-api-root", apiRoot,
                    "--releases", key, "--exe", key + "=" + selectedHost, "--engine-host", selectedHost, "--plc-worker", key + "=" + worker,
                    "--transport", transport,
                    "--output", output], output, "capture");
            }
        }
        return jobs;
    }

    internal static void ValidatePlcReleaseJob(PlcReleaseJob job, CommandResult result, string root, string logs)
    {
        ProcessRunner.RequireSuccess(result, job.Name);
        var key = job.Release;
        var host = Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe");
        if (job.Name.StartsWith("harness-", StringComparison.Ordinal))
        {
            ProcessRunner.RequireSuccess(Harness(result, job.Mode, key), job.Name);
            // The successor gate: exact host/worker identity, and for diagnostics the adapter JIT inventory and four SDK guards.
            ValidatePlcHarness(result.StandardOutput, job.Arguments[Array.IndexOf(job.Arguments, "--output") + 1], job.Mode, key, host,
                ReleasePlcFixture(logs, key), job.Arguments[Array.IndexOf(job.Arguments, "--adapter-inventory") + 1]);
        }
        if (job.Mode == "native-safety") RequireCount(job.Name, result.StandardOutput, "COMPLETE: (\\d+) native harness safety checks passed; live TIA tests NOT RUN");
        if (job.Mode == "worker-protocol") RequireCount(job.Name, result.StandardOutput, "COMPLETE: (\\d+) Foundation transport checks passed", "workerProtocol");
        if (job.Mode == "resources") RequireCount(job.Name, result.StandardOutput, "COMPLETE: (\\d+) resource discovery checks passed");
        if (job.Check == "plc-approval") ValidateApproval(job.Evidence);
        if (job.Check == "plc-ecosystem")
        {
            var path = job.Check == "plc-ecosystem" ? Directory.EnumerateFiles(job.Evidence, "result.json", SearchOption.AllDirectories).Single() : job.Evidence;
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var evidence = doc.RootElement;
            var worker = ReleasePlcFixture(logs, key);
            if (GetJsonString(evidence, "status") != "passed" || GetJsonBool(evidence, "nativeTiaExecuted") ||
                GetJsonString(evidence, "runtimeSha256") != FileHash.Sha256(worker) ||
                GetJsonString(evidence, "sdkWorkerSha256") != FileHash.Sha256(worker) ||
                GetJsonString(evidence, "hostSha256") != FileHash.Sha256(Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe")))
                throw new ReleaseException("PLC runtime evidence mismatch: " + job.Name);
            if (job.Check == "plc-ecosystem")
            {
                CheckReleaseCount("v21Ecosystem", GetJsonInt(evidence, "checks"), "PLC ecosystem checks incomplete");
                if (GetJsonBool(evidence, "selfTestOnly")) throw new ReleaseException("PLC ecosystem only ran self-tests");
            }

        }
    }

    internal static void RunPlcReleaseJobs(IEnumerable<PlcReleaseJob> jobs, string root, string logs,
        Func<PlcReleaseJob, CommandResult> run, Action<string, string[]> compare)
    {
        var ordered = jobs.ToArray();
        void Compare(PlcReleaseJob job)
        {
            if (job.Mode != "capture") return;
            const string kind = "baseline";
            var baseline = Path.Combine(root, "manifest/contracts/v4", kind);
            compare(job.Arguments[0], ["compare", "--baseline", baseline, "--current", job.Evidence, "--releases", job.Release]);
            if (!File.ReadAllBytes(Path.Combine(baseline, job.Release + ".json")).SequenceEqual(File.ReadAllBytes(Path.Combine(job.Evidence, job.Release + ".json"))))
                throw new ReleaseException("PLC capture bytes differ: " + job.Name);
        }
        foreach (var job in ordered)
        {
            ResetPlcReleaseJobOutputs(job, logs);
            ValidatePlcReleaseJob(job, run(job), root, logs);
            Compare(job);
        }
    }

    // Both coverage jobs and the JIT diagnostics job write the adapter inventory that the harness modes read.
    internal static bool IsPlcInventoryProducer(PlcReleaseJob job) => job.Check == "native-coverage" || job.Name.StartsWith("jit-adapter-coverage-", StringComparison.Ordinal);

    // Remove only this job's previous outputs below this run's log directory.
    internal static void ResetPlcReleaseJobOutputs(PlcReleaseJob job, string logs)
    {
        var prefix = Path.GetFullPath(logs).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targets = job.Arguments.Select((value, index) => (value, index))
            .Where(pair => pair.value is "--output" or "--evidence" or "--coverage-output" or "--usage-output" or "-ResultsDirectory" && pair.index + 1 < job.Arguments.Length)
            .Select(pair => job.Arguments[pair.index + 1]).Append(job.Evidence).Where(path => path.Length != 0);
        foreach (var target in targets.Select(path => Path.GetFullPath(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(target)) Directory.Delete(target, true);
            else if (File.Exists(target)) File.Delete(target);
        }
    }

    private static readonly IReadOnlyDictionary<string, (int Count, bool Exact)> BuildReleaseCountRules =
        new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
        {
            ["nativeMcpSafety"] = (8, false), ["nativeSupervisor"] = (1, false), ["diagnosticBehavior"] = (36, false), ["diagnosticRejection"] = (5, false),
            ["nativeJournalReader"] = (3, false), ["adapterJournal"] = (8, false), ["processLeases"] = (2, false),
            ["workerProtocol"] = (58, false), ["approvalSafety"] = (7, true),
            ["softwareLookup"] = (45, false),
            ["v21Ecosystem"] = (43, false), ["globalScriptV21"] = (8, true), ["graphicSelection"] = (8, false),
            ["runtimeSettingsV20"] = (8, false), ["runtimeSettingsV21"] = (9, false), ["ecosystem"] = (31, false)
        };

    private static int RequireCount(string name, string text, string pattern, string? rule = null)
    {
        var match = Regex.Match(text, pattern);
        if (!match.Success) throw new ReleaseException(name + " did not report a complete check count");
        var actual = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (rule is not null) CheckReleaseCount(rule, actual, name + " checks incomplete");
        return actual;
    }

    private static void CheckReleaseCount(string key, int actual, string message)
    {
        if (!BuildReleaseCountRules.TryGetValue(key, out var expected)) throw new ReleaseException("Unknown check-count gate: " + key);
        if ((expected.Exact && actual != expected.Count) || (!expected.Exact && actual < expected.Count))
            throw new ReleaseException($"{message}; expected {(expected.Exact ? "exactly" : "at least")} {expected.Count}, actual {actual}");
    }

    private static int GetArrayLength(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    internal static string[] HarnessModes(string release) =>
    ["example-library-only", "software-lookup-only", "engineering-api-only", "", "hmi-only", "native-diagnostics-only", "process-leases-only",
        "native-export-only", "hmi-snapshot-only", "global-script-only", "graphic-selection-only", "runtime-settings-only", "test-migration-read-assembly", "test-ecosystem-assembly",
        "descriptor-catalog-only", "check-modes-self-test",
        .. release == "21" ? new[] { "test-download-route", "test-match-plc-name" } : []];

    internal static string[] PlcHarnessModes(string release) =>
        HarnessModes(release).Where(mode => !new[] { "engineering-api-only" }.Contains(mode)).ToArray();

    internal static string PlcHarnessJob(string release, string mode) =>
        "harness-v" + release + "-" + (mode.Length == 0 ? "http" : mode) + "-plc";

    private static CommandResult Harness(CommandResult result, string mode, string key)
    {
        if (result.ExitCode != 0) return result;
        switch (mode)
        {
            case "software-lookup-only": RequireCount("software lookup", result.StandardOutput, "COMPLETE: (\\d+) software lookup checks passed", "softwareLookup"); break;
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

    private static void ValidateApproval(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var evidence = doc.RootElement;
        CheckReleaseCount("approvalSafety", GetJsonInt(evidence, "checksPassed"), "Default approval checks incomplete");
        var results = evidence.GetProperty("results");
        if (GetJsonString(evidence, "status") != "passed" || GetJsonInt(evidence, "checksExpected") != 7 || GetJsonBool(evidence, "workbenchConnected") ||
            GetJsonBool(evidence, "tiaConnected") || GetJsonString(results, "direct") != "refused-before-dispatch; read-succeeded" ||
            GetJsonString(results, "CallTool") != "refused-before-dispatch") throw new ReleaseException("Default approval evidence mismatch");
    }

    private static void ValidateJit(string text, string adapterPath)
    {
        using (var evidence = JsonDocument.Parse(File.ReadAllText(adapterPath)))
        {
            var match = Regex.Match(text, "COMPLETE: (\\d+) adapter native diagnostic wrappers JIT prepared; (\\d+) open generic wrappers");
            if (!match.Success || int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) != GetJsonInt(evidence.RootElement, "count"))
                throw new ReleaseException("adapter diagnostic wrapper JIT/inventory mismatch");
        }
        RequireCount("adapter journal", text, "COMPLETE: (\\d+) adapter integration diagnostic checks passed", "adapterJournal");
        RequireCount("native journal", text, "COMPLETE: (\\d+) native journal reader checks passed", "nativeJournalReader");
    }

    internal static void ValidatePlcHarness(string text, string directory, string mode, string key, string host, string worker, string adapterInventory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json"))); var evidence = doc.RootElement;
        string workerHash = FileHash.Sha256(worker);
        if (GetJsonString(evidence, "mode") != (mode.Length == 0 ? "http" : mode) || GetJsonString(evidence, "release") != key
            || !evidence.TryGetProperty("nativeTiaExecuted", out var native) || native.ValueKind != JsonValueKind.False
            || GetJsonString(evidence, "workerModel") != "foundation-host-plc-worker"
            || GetJsonString(evidence, "runtimeSha256") != workerHash || GetJsonString(evidence, "sdkWorkerSha256") != workerHash
            || GetJsonString(evidence, "hostSha256") != FileHash.Sha256(host))
            throw new ReleaseException("PLC harness evidence mismatch: " + directory);
        if (mode != "native-diagnostics-only") return;
        ValidateJit(text, adapterInventory);
        if (RequireCount("SDK guard", text, "COMPLETE: (\\d+) direct SDK guard checks passed") != 4) throw new ReleaseException("SDK guard checks incomplete");
    }
}
