using System.Text.Json;
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

    internal static string EngineCompatibilityDirectory(string root, string key) =>
        Path.Combine(root, "bin-build/engine-compat", "v" + key, "worker");

    // The engine cross-checks keep their old packaged pair until P8-04l: the installed
    // host's files beside the private engine worker directory. Only the repository's
    // check scripts start this copy, through the private cross-check route.
    internal static string StageEngineCompatibilityHost(string root, string key)
    {
        var installed = Path.Combine(root, "runtime", "v" + key);
        var destination = Path.GetDirectoryName(EngineCompatibilityDirectory(root, key))!;
        Directory.CreateDirectory(destination);
        if (Directory.Exists(installed))
            foreach (var file in Directory.EnumerateFiles(installed))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        return Path.Combine(destination, "TiaMcp.FoundationHost.exe");
    }

    // FoundationHost reads this together with --engine-worker/--engine-catalog (HostOptions).
    internal const string EngineCrossCheckVariable = "TIA_MCP_ENGINE_CROSS_CHECK_DIRECTORY";

    internal static IDictionary<string, string?> WithEngineCrossCheck(IDictionary<string, string?> environment)
    {
        environment[EngineCrossCheckVariable] = Path.GetFullPath(Path.Combine(Root, "bin-build/engine-compat"));
        return environment;
    }

    internal static readonly string[] PlcReleaseGroups =
    ["functional", "worker-isolation", "approval", "ecosystem", "stability", "isolated-stability", "contracts", "responses"];

    private static string ReleasePlcFixture(string logs, string key) =>
        new PlcWorkerFixtureBuild(key, "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"));

    private static void PreparePlcReleaseChecks(string dotnet, string apiRoot, string? nuget, string logs,
        string temp, string cli, bool production)
    {
        if (production)
        {
            var args = new List<string> { "build-plc-workers", "-PublicApiRoot", apiRoot, "-Dotnet", dotnet,
                "-ReleaseKeys", "20", "-ReleaseKeys", "21", "-UseReferenceAssemblyPackage", "-EvidenceDirectory", Path.Combine(logs, "plc-production") };
            if (nuget is not null) args.AddRange(["-NuGetConfig", nuget]);
            if (RunSelfCommand(args.ToArray(), "Branch PLC worker builds") != 0) throw new ReleaseException("PLC worker build failed");
            foreach (var key in new[] { "20", "21" })
            {
                InstallReleasePlcWorker(Root, key);
                var catalog = RunBuildIsolated(Path.Combine(Root, $"runtime/v{key}/TiaMcp.FoundationHost.exe"),
                    ["--catalog", "--plc-worker", "--release-key", key, "--profile", "full"], Path.Combine(logs, "tool-catalog-v" + key + "-plc.log"),
                    temp, cli, apiRoot, nuget, false, "PLC worker catalog");
                ProcessRunner.RequireSuccess(catalog, "PLC host catalog");
                using var doc = JsonDocument.Parse(catalog.StandardOutput);
                if (!doc.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(new CatalogData(Root).Rosters[key])) throw new ReleaseException("PLC host catalog mismatch: " + key);
            }
        }
        void Build(string project, string[] properties)
        {
            RestoreBuildProject(dotnet, project, nuget, logs, temp, cli, apiRoot, properties);
            RunBuildRaw(dotnet, ["build", project, "-c", "Release", "--no-restore", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", .. properties],
                Path.Combine(logs, Path.GetFileNameWithoutExtension(project) + "-plc.log"), temp, cli, apiRoot, nuget, "PLC release prerequisite");
        }
        Build(Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj"), []);
        Build(Path.Combine(Root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/TiaMcp.WorkerChannel.TransportFixture.csproj"), []);
        foreach (var key in new[] { "20", "21" })
        {
            var api = ResolveReleaseApi(null, apiRoot, int.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
            var unit = new PlcWorkerFixtureBuild(key, "sdk", null);
            string[] properties = ["-p:TiaReleaseKey=" + key, "-p:SiemensEngineeringDirectory=" + api,
                "-p:NativeCallWeaverPath=" + Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"),
                .. unit.Properties(Path.Combine(logs, "plc-fixtures"))];
            Build(Path.Combine(Root, "src/PlcWorker/TiaMcp.PlcWorker.csproj"), properties);
            var metadata = WorkerAssemblyMetadata.Read(ReleasePlcFixture(logs, key));
            if (metadata.GetValueOrDefault("TiaMcpPlcWorkerSdkFixture") != "true" || metadata.GetValueOrDefault("TiaReleaseKey") != key || metadata.GetValueOrDefault("TiaMcpTestPolicy") is not null)
                throw new ReleaseException("PLC release fixture metadata mismatch: " + key);
            Build(Path.Combine(Root, $"tests/Engine/TiaMcp.Engine.Native.Tests/V{key}/TiaMcp.Engine.Native.Tests.V{key}.csproj"), ["-p:SiemensEngineeringDirectory=" + api]);
        }
        // Test-PlcHarness.py and Test-CampaignInputs.py start their file apps with dotnet run --file --no-build, which
        // finds the program below TEMP. Compile them below this process's TEMP and name it for the jobs, whose own
        // TEMP is private (the check-all mechanism); a cold release chain has no other build of these file apps.
        // Same arguments as build-file-programs: a file-based build rejects -m and -nodeReuse.
        foreach (var program in PlcReleaseFilePrograms)
        {
            var arguments = new List<string> { "build", Path.Combine(Root, program), "-c", "Release", "--nologo", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false" };
            if (!string.IsNullOrWhiteSpace(nuget)) arguments.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
            var built = ProcessRunner.Run(dotnet, arguments, Root, new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1" });
            WriteLog(Path.Combine(logs, Path.GetFileNameWithoutExtension(program) + "-plc-build.log"), built);
            ProcessRunner.RequireSuccess(built, "PLC file app build: " + program);
        }
        Environment.SetEnvironmentVariable(PlcReleaseSystemTempVariable, Path.GetTempPath());
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

    // The same command inventory is used by preparation, branch-gate and the
    // candidate captures. No scheduler/cache/sharding state is shared with CheckAll.
    internal static IReadOnlyList<PlcReleaseJob> PlcReleaseJobs(string root, string apiRoot, string logs,
        string temp, string dotnet, string python, int rounds = 50)
    {
        if (rounds < 10) throw new ReleaseException("PLC release checks require at least 10 stability rounds.");
        var jobs = new List<PlcReleaseJob>();
        var harness = Path.Combine(root, "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe");
        var weaver = Path.Combine(root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll");
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
            foreach (var mode in CheckAllHarnessModes(key))
            {
                var engine = "harness-v" + key + "-" + (mode.Length == 0 ? "http" : mode);
                if (!CheckAllHarnessSuccessors().TryGetValue(engine, out var name)) continue; // Engine-only D5 remains in build-release.
                var check = mode == "native-diagnostics-only" ? "native-diagnostics" : "plc-functional";
                jobs.Add(new(name, check, python, [Path.Combine(root, "scripts/checks/Test-PlcHarness.py"),
                    "--release", key, "--mode", mode.Length == 0 ? "http" : mode, "--host", host, "--worker", worker,
                    "--harness", harness, "--api", api, "--public-api-root", apiRoot, "--adapter-inventory", inventory,
                    "--output", Path.Combine(logs, name)], Mode: mode, Release: key));
            }
            foreach (var suite in new[] { "worker-robustness", "engine-host", "foundation-api" })
                jobs.Add(new((suite == "foundation-api" ? "engineering-api" : suite) + "-v" + key + "-plc", suite == "worker-robustness" ? "plc-worker-isolation" : suite == "engine-host" ? "http-concurrency" : "plc-functional", "test-suites",
                    // foundation-api does not depend on the release: offline-suites and both engineering-api successors name the
                    // same sharded run (same binaries, arguments and suite gate), executed once per process (SharedSuiteRuns).
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
            foreach (var concurrency in new[] { "8", "16" })
            {
                var name = "stability-c" + concurrency;
                var evidence = Path.Combine(logs, name + "-v" + key + "-plc");
                Add(name, concurrency == "8" ? "plc-stability" : "plc-isolated-stability", "Test-LocalStability.py",
                    ["--exe", host, "--major", key, "--host-harness", harness, "--public-api", api, "--plc-worker", worker,
                     "--rounds", rounds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--concurrency", concurrency,
                     "--output", evidence], Path.Combine(evidence, "result.json"));
            }
            Add("usage", "resource-discovery", "Test-ToolUsage.py", ["--foundation-host", host, "--plc-worker", key + "=" + worker,
                "--public-api-root", apiRoot, "--releases", key, "--evidence", Path.Combine(logs, "usage-v" + key + "-plc"),
                "--coverage-output", Path.Combine(logs, "usage-v" + key + "-plc.json")]);
            Add("campaign-inputs", "plc-functional", "Test-CampaignInputs.py", ["--exe", host, "--major", key,
                "--public-api", api, "--plc-worker", worker, "--output", Path.Combine(logs, "campaign-v" + key + "-plc")]);
            foreach (var kind in new[] { "contracts", "responses" })
            foreach (var source in new[] { false, true })
            foreach (var transport in new[] { "stdio", "http" })
            {
                var selectedHost = source ? Path.Combine(root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe") : host;
                var name = kind + (source ? "-source" : "") + "-" + transport;
                var output = Path.Combine(logs, name + "-v" + key + "-plc");
                var script = "Snapshot-Tool" + (kind == "contracts" ? "Contracts" : "Responses") + ".py";
                Add(name, "plc-" + kind, script, ["capture", "--repo-root", root, "--public-api-root", apiRoot,
                    "--releases", key, "--exe", key + "=" + selectedHost, "--engine-host", selectedHost, "--plc-worker", key + "=" + worker,
                    "--transport", transport, .. kind == "responses" ? new[] { "--temp-root", Path.Combine(temp, name + "-v" + key), "--dotnet-root", Path.Combine(root, "runtime/dotnet") } : [],
                    "--output", output], output, "capture");
            }
        }
        return jobs;
    }

    internal static void ValidatePlcReleaseJob(PlcReleaseJob job, CommandResult result, string root, string logs, int rounds = 50)
    {
        ProcessRunner.RequireSuccess(result, job.Name);
        var key = job.Release;
        var host = Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe");
        if (job.Name.StartsWith("harness-", StringComparison.Ordinal))
        {
            ProcessRunner.RequireSuccess(CheckAllHarness(result, job.Mode, key), job.Name);
            // The check-all successor gate: exact host/worker identity, and for diagnostics the adapter JIT inventory and four SDK guards.
            ValidateCheckAllPlcHarness(result.StandardOutput, job.Arguments[Array.IndexOf(job.Arguments, "--output") + 1], job.Mode, key, host,
                ReleasePlcFixture(logs, key), job.Arguments[Array.IndexOf(job.Arguments, "--adapter-inventory") + 1]);
        }
        if (job.Name.StartsWith("usage-", StringComparison.Ordinal))
        {
            // Same receipt identity as check-all's PLC usage audit.
            var receipt = JsonNode.Parse(File.ReadAllText(job.Arguments[Array.IndexOf(job.Arguments, "--coverage-output") + 1]))!.AsArray().Single()!;
            var worker = ReleaseRecords.HashFile(ReleasePlcFixture(logs, key));
            if ((string?)receipt["releaseKey"] != key || (string?)receipt["runtimeSha256"] != worker || (string?)receipt["sdkWorkerSha256"] != worker ||
                (string?)receipt["hostSha256"] != ReleaseRecords.HashFile(host) || (string?)receipt["workerModel"] != "foundation-host-plc-worker")
                throw new ReleaseException("PLC usage receipt evidence mismatch: " + job.Name);
        }
        if (job.Mode == "native-safety") RequireCount(job.Name, result.StandardOutput, "COMPLETE: (\\d+) native harness safety checks passed; live TIA tests NOT RUN");
        if (job.Mode == "worker-protocol") RequireCount(job.Name, result.StandardOutput, "COMPLETE: (\\d+) Foundation transport checks passed", "workerProtocol");
        if (job.Mode == "resources") RequireCount(job.Name, result.StandardOutput, "COMPLETE: (\\d+) resource discovery checks passed");
        if (job.Check == "plc-approval") ValidateCheckAllApproval(job.Evidence);
        if (job.Check is "plc-stability" or "plc-isolated-stability" or "plc-ecosystem")
        {
            var path = job.Check == "plc-ecosystem" ? Directory.EnumerateFiles(job.Evidence, "result.json", SearchOption.AllDirectories).Single() : job.Evidence;
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var evidence = doc.RootElement;
            var worker = ReleasePlcFixture(logs, key);
            if (GetJsonString(evidence, "status") != "passed" || GetJsonBool(evidence, "nativeTiaExecuted") ||
                GetJsonString(evidence, "runtimeSha256") != ReleaseRecords.HashFile(worker) ||
                GetJsonString(evidence, "sdkWorkerSha256") != ReleaseRecords.HashFile(worker) ||
                GetJsonString(evidence, "hostSha256") != ReleaseRecords.HashFile(Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe")))
                throw new ReleaseException("PLC runtime evidence mismatch: " + job.Name);
            if (job.Check == "plc-ecosystem")
            {
                CheckReleaseCount("v21Ecosystem", GetJsonInt(evidence, "checks"), "PLC ecosystem checks incomplete");
                if (GetJsonBool(evidence, "selfTestOnly")) throw new ReleaseException("PLC ecosystem only ran self-tests");
            }
            else if (GetJsonInt(evidence, "rounds") != rounds || GetArrayLength(evidence, "runs") != 4 ||
                (job.Check == "plc-isolated-stability" && !GetJsonBool(evidence, "isolatedWorker")))
                throw new ReleaseException("PLC stability matrix incomplete: " + job.Name);
        }
    }

    internal static void RunPlcReleaseJobs(IEnumerable<PlcReleaseJob> jobs, string root, string logs,
        Func<PlcReleaseJob, CommandResult> run, Action<string, string[]> compare, int rounds = 50, int parallelism = 1)
    {
        var ordered = jobs.ToArray();
        void Compare(PlcReleaseJob job)
        {
            if (job.Mode != "capture") return;
            var kind = job.Check == "plc-contracts" ? "baseline" : "responses";
            var baseline = Path.Combine(root, "manifest/contracts/v4", kind);
            compare(job.Arguments[0], ["compare", "--baseline", baseline, "--current", job.Evidence, "--releases", job.Release]);
            if (!File.ReadAllBytes(Path.Combine(baseline, job.Release + ".json")).SequenceEqual(File.ReadAllBytes(Path.Combine(job.Evidence, job.Release + ".json"))))
                throw new ReleaseException("PLC capture bytes differ: " + job.Name);
        }
        if (parallelism <= 1)
        {
            foreach (var job in ordered)
            {
                ValidatePlcReleaseJob(job, run(job), root, logs, rounds);
                Compare(job);
            }
            return;
        }
        // Release runs: the adapter inventory producers first, in table order (the harness and diagnostics jobs read their
        // inventory); then the independent jobs of both releases in parallel, each in its own isolated host directory; then
        // the stability soaks and the shared test-suites runs one at a time, as before. No new job starts after a failure,
        // and every failure of the parallel stage is reported. The captures are compared in table order at the end.
        var first = ordered.Where(IsPlcInventoryProducer).ToArray();
        var exclusive = ordered.Where(job => !IsPlcInventoryProducer(job) && IsExclusivePlcJob(job)).ToArray();
        var concurrent = ordered.Where(job => !IsPlcInventoryProducer(job) && !IsExclusivePlcJob(job)).ToArray();
        foreach (var job in first) ValidatePlcReleaseJob(job, run(job), root, logs, rounds);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        Parallel.ForEach(concurrent, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, (job, state) =>
        {
            if (!failures.IsEmpty) { state.Stop(); return; }
            try { ValidatePlcReleaseJob(job, run(job), root, logs, rounds); }
            catch (Exception ex) { failures.Enqueue(ex); state.Stop(); }
        });
        if (!failures.IsEmpty)
            throw new ReleaseException(string.Join(Environment.NewLine, failures.Select(ex => ex.Message)));
        foreach (var job in exclusive) ValidatePlcReleaseJob(job, run(job), root, logs, rounds);
        foreach (var job in ordered) Compare(job);
    }

    // Both coverage jobs and the JIT diagnostics job write the adapter inventory that the harness modes read.
    internal static bool IsPlcInventoryProducer(PlcReleaseJob job) => job.Check == "native-coverage" || job.Name.StartsWith("jit-adapter-coverage-", StringComparison.Ordinal);

    // Stability soaks measure growth over many rounds; test-suites runs are shared between jobs by argument identity.
    internal static bool IsExclusivePlcJob(PlcReleaseJob job) => job.Check is "plc-stability" or "plc-isolated-stability" || job.Executable == "test-suites";

    private static void RunPlcReleaseChecks(ReleaseCheckPlan plan, Options options, string apiRoot, string logs,
        string temp, string cli, string dotnet, string python, string? nuget, int rounds = 50)
    {
        var jobs = PlcReleaseJobs(Root, apiRoot, logs, temp, dotnet, python, rounds).Where(job => PlcReleaseSelected(plan, job)).ToArray();
        var sharedRuns = new SortedDictionary<string, string>(StringComparer.Ordinal);
        RunPlcReleaseJobs(jobs, Root, logs, ShareIdenticalSuiteRuns(job =>
        {
            ResetPlcReleaseJobOutputs(job, logs);
            if (job.Executable == "test-suites")
            {
                var args = job.Arguments.ToList();
                if (nuget is not null) args.Add("-DotnetArg=-p:RestoreConfigFile=" + nuget);
                return RunBuildIsolated("test-suites", args, Path.Combine(logs, job.Name + ".log"), temp, cli, apiRoot, nuget, false, job.Name,
                    environment => DotnetSuites.Run(Root, Options.Parse(args.ToArray(), new HashSet<string>()), environment));
            }
            var selectedPython = job.Check == "plc-ecosystem" ? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON") ?? job.Executable : job.Executable;
            return RunBuildIsolated(selectedPython, job.Arguments, Path.Combine(logs, job.Name + ".log"), temp, cli, apiRoot, nuget, job.Check == "plc-approval", job.Name);
        }, sharedRuns, logs), (script, args) => RunBuildRaw(python, [script, .. args], Path.Combine(logs, Path.GetFileNameWithoutExtension(script) + "-plc-compare.log"), temp, cli, apiRoot, nuget, "PLC snapshot comparison"), rounds, Math.Min(6, GetMaxParallelism(options)));
        WriteJson(Path.Combine(logs, "plc-checks-result.json"), new { status = "passed", checks = jobs.Select(job => job.Name), sharedRuns, rounds,
            nativeTiaExecuted = false, workers = new[] { "20", "21" }.Select(key => new { releaseKey = key,
                workerSha256 = ReleaseRecords.HashFile(ReleasePlcFixture(logs, key)),
                productionWorkerSha256 = ReleaseRecords.HashFile(Path.Combine(PlcReleaseInstallDirectory(Root, key), $"TiaMcp.PlcWorker.{key}.exe")),
                hostSha256 = ReleaseRecords.HashFile(Path.Combine(Root, $"runtime/v{key}/TiaMcp.FoundationHost.exe")) }) });
    }

    // Every PLC JSON evidence file of the run, each once: the approval receipts are already listed, and the prepared
    // record refuses a duplicate path (ReleaseRecords.ReuseReason).
    internal static void AddPlcReleaseEvidence(List<string> evidencePaths, string logs)
    {
        var listed = evidencePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        evidencePaths.AddRange(Directory.EnumerateFiles(logs, "*.json", SearchOption.AllDirectories)
            .Where(path => path.Contains("-plc", StringComparison.Ordinal) || path.Contains("plc-fixtures", StringComparison.Ordinal))
            .Where(path => listed.Add(Path.GetFullPath(path))));
    }

    // The release-independent foundation-api run: sharded like CI, results where offline-suites reads its record row.
    internal static string[] FoundationApiSuiteArguments(string dotnet, string apiRoot, string logs) =>
        ["-Suite", "foundation-api", "-Dotnet", dotnet, "-Shards", "8", "-PublicApiRoot", apiRoot, "-ResultsDirectory", Path.Combine(logs, "dotnet-suites", "foundation-api")];

    // test-suites runs of this process by argument identity; offline-suites registers its foundation-api run here.
    private static readonly Dictionary<string, (string Job, CommandResult Result)> SharedSuiteRuns = new(StringComparer.Ordinal);

    private static string SuiteIdentity(IEnumerable<string> arguments) => string.Join('\0', arguments);

    // Runs a test-suites command once and registers it, so every later job with the same arguments shares its result.
    internal static CommandResult RunSharedSuite(string name, string[] arguments, string logs, Func<string[], CommandResult> run,
        IDictionary<string, (string Job, CommandResult Result)>? runs = null)
    {
        runs ??= SharedSuiteRuns;
        if (runs.TryGetValue(SuiteIdentity(arguments), out var earlier)) return earlier.Result;
        ResetPlcReleaseJobOutputs(new PlcReleaseJob(name, "", "test-suites", arguments), logs);
        var result = run(arguments);
        runs[SuiteIdentity(arguments)] = (name, result);
        return result;
    }

    // A test-suites job whose arguments equal an earlier run's (the release-independent foundation-api run of
    // offline-suites and both engineering-api successors) reuses that run and its result: it passes or fails with it,
    // and its log names it.
    internal static Func<PlcReleaseJob, CommandResult> ShareIdenticalSuiteRuns(Func<PlcReleaseJob, CommandResult> run,
        IDictionary<string, string> sharedRuns, string logs, IDictionary<string, (string Job, CommandResult Result)>? runs = null)
    {
        runs ??= SharedSuiteRuns;
        return job =>
        {
            if (job.Executable != "test-suites") return run(job);
            var identity = SuiteIdentity(job.Arguments);
            if (runs.TryGetValue(identity, out var earlier))
            {
                sharedRuns[job.Name] = earlier.Job;
                WriteLog(Path.Combine(logs, job.Name + ".log"), new CommandResult(earlier.Result.ExitCode,
                    "Shared run: " + earlier.Job + " (same suite, binaries, arguments and gate)" + Environment.NewLine + earlier.Result.StandardOutput, earlier.Result.StandardError));
                return earlier.Result;
            }
            var result = run(job);
            runs[identity] = (job.Name, result);
            return result;
        };
    }

    // build-multi-version reuses bin-build/multi-version, and several check scripts refuse an existing evidence
    // directory. Remove only this job's own previous outputs, and only below this run's log directory.
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

    internal static bool PlcReleaseSelected(ReleaseCheckPlan plan, PlcReleaseJob job) => plan.Includes(job.Check)
        || job.Check == "plc-approval" && plan.Includes("foundation-approval")
        || job.Check == "resource-discovery" && plan.Includes("foundation-transport");
}
