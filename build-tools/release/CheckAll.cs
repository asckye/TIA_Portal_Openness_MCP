using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static int CheckAll(Options options)
    {
        if (options.Has("CiCovered") && (options.Has("BuildOnly") || options.Has("ProveEquivalence") || options.Has("SequentialBaselineDirectory")))
            throw new ReleaseException("-CiCovered requires a complete run without a local equivalence baseline", 64);
        if (options.Has("Clean") && Environment.GetEnvironmentVariable("TIA_MCP_CLEAN_CHECKOUT") == null) return CheckAllSnapshot(options);
        var worktree = Path.GetFullPath(options.Get("Worktree", Root));
        if (!string.Equals(worktree.TrimEnd('\\', '/'), Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            var result = ProcessRunner.Run(Dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", .. options.Arguments()], worktree, forwardOutput: true);
            return result.ExitCode;
        }
        // -NoBuildCache: no verified file-app or build cache reuse, and with it no result cache (a release may adopt only such a run).
        if (options.Has("NoBuildCache")) Environment.SetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED", "1");
        int parallelism;
        int shards = FastSuites.Positive(options, "Shards", 8);
        var api = RequiredDirectory(options.Get("PublicApiRoot"), "-PublicApiRoot");
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (nuget == null) throw new ReleaseException("check-all requires an offline -NuGetConfig", 64);
        nuget = Path.GetFullPath(nuget);
        if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error, 64);
        var output = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/check-all/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6])));
        if (Directory.Exists(output)) throw new ReleaseException("Output directory must be fresh: " + output, 64);
        Directory.CreateDirectory(output);
        var python = options.Get("Python", Py); var dotnet = options.Get("Dotnet", Dotnet);
        if (!OperatingSystem.IsWindows()) throw new ReleaseException("check-all CPU isolation requires Windows", 64);
        var companionPython = CheckAllCompanionPython(options);
        using var currentProcess = Process.GetCurrentProcess();
        var available = unchecked((ulong)currentProcess.ProcessorAffinity.ToInt64());
        var clock = Stopwatch.StartNew();
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        if (options.Get("BaselineReport") is { } timingBaseline)
            SuiteCosts.ImportReport(Path.GetDirectoryName(Path.GetFullPath(timingBaseline))!, Path.Combine(Root, "bin-build/check-timings"));
        var history = Path.Combine(Root, "bin-build/check-timings/check-costs.json");
        var costs = File.Exists(history) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(history))! : new Dictionary<string, double>();
        var baselineReport = options.Get("BaselineReport") ?? (File.Exists(Path.Combine(Root, "bin-build/reviewer-run1/report.json")) ? Path.Combine(Root, "bin-build/reviewer-run1/report.json") : null);
        if (baselineReport != null)
            foreach (var row in JsonNode.Parse(File.ReadAllText(baselineReport))!["checks"]!.AsArray())
                costs[(string)row!["Name"]!] = Math.Max(costs.GetValueOrDefault((string)row["Name"]!), (double)row["Seconds"]!);
        var resources = CheckPacking.Read(Root, baselineReport);
        var suitePlan = new List<CheckJob>();
        foreach (var (name, node) in catalog)
            FastSuites.AddJobs(suitePlan, Root, name, node!.AsObject(), Path.Combine(output, "packing-suites"), dotnet,
                node["minimumPassed"]!.GetValue<int>() >= 100 ? shards : 1, null, []);
        foreach (var project in new[] { "Core", "Gui" })
        foreach (var mode in new[] { "false", "true" })
            suitePlan.Add(new CheckJob("studio-" + project.ToLowerInvariant() + "-" + mode, () => new(0, "", ""), [], Exclusive: true)
                { SensitiveGroup = $"tests/Studio/TiaOpenness.{project}.Tests/TiaOpenness.{project}.Tests.csproj" });
        int sensitiveParallelism = FastSuites.Positive(options, "SensitiveParallelism", 2);
        int sensitiveDefault = CheckPacking.ReservedCores(suitePlan.Where(job => job.Exclusive), resources, sensitiveParallelism);
        var stabilityPlan = CheckAllPlcScriptJobs().Where(name => name.StartsWith("stability-", StringComparison.Ordinal))
            .Select(name => new CheckJob(name, () => new(0, "", ""), []));
        int stabilityDefault = CheckPacking.ReservedCores(stabilityPlan, resources, FastSuites.Positive(options, "StabilityParallelism", 4), exclusive: false);
        // Give the shortest asserted deadlines first choice of complete physical
        // cores. Stability and general work retain disjoint, reserved CPU sets.
        // A machine too small for the default lanes runs a recorded degraded
        // layout; explicit lane sizes are kept exactly or refused (exit 64).
        var layout = CheckAffinity.PlanCheckAll(available, FastSuites.Positive(options, "SensitiveCores", sensitiveDefault), options.Has("SensitiveCores"),
            FastSuites.Positive(options, "StabilityCores", stabilityDefault), options.Has("StabilityCores"), CheckAffinity.PreferredPhysicalCores());
        if (layout.Isolation != CheckAffinity.FullIsolation) Console.WriteLine("check-all: cpuIsolation " + layout.Isolation + ": " + layout.Reason);
        int stabilityCores = System.Numerics.BitOperations.PopCount(layout.Stability);
        int generalCores = System.Numerics.BitOperations.PopCount(layout.General);
        var sensitiveNames = suitePlan.Where(job => job.Exclusive).Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        var packing = CheckPacking.Plan(layout.General, resources.Where(pair => !sensitiveNames.Contains(pair.Key) &&
            !pair.Key.StartsWith("stability-", StringComparison.Ordinal) && pair.Key is not ("build-once" or "suite-output-parity" or "configuration-page")).Select(pair => pair.Value),
            CheckAffinity.AvailableMemory(), Math.Min(32, FastSuites.CheckAllParallelism(options, 32))) with { Available = available };
        parallelism = packing.Capacity(available);
        int buildParallelism = FastSuites.Positive(options, "BuildParallelism", System.Numerics.BitOperations.PopCount(available));
        int stabilityParallelism = Math.Min(FastSuites.Positive(options, "StabilityParallelism", 4), Math.Min(4, stabilityCores));
        using var load = CheckLoad.Start(Path.Combine(output, "process-load.json"));
        var before = TrackedBytes();
        var timings = new List<CheckTiming>();
        var jobs = new List<CheckJob>();
        var allJobs = new List<CheckJob>();
        var planned = new List<string>();
        // The release may adopt this run (P8-93): bind the commit, the tree and every input before anything is regenerated.
        var releaseEvidence = ReleaseEvidence.BeginSafely(() => ReleaseEvidence.Begin(Root, "git", options, ReleaseEvidence.ProbeExternal(Root, api, nuget, Dotnet, python, companionPython),
            Environment.GetEnvironmentVariable, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        var ciCovered = options.Get("CiCovered") is { } candidate
            ? CiCoverage.Load(Root, Path.GetFullPath(candidate), ReleaseEvidence.GitText(Root, "git", "rev-parse", "HEAD")) : null;
        if (ciCovered != null)
        {
            releaseEvidence["ciCovered"] = ciCovered.DeepClone();
            CiCoverage.WriteSummaries(output, ciCovered);
        }
        CommandResult Tool(string name, params string[] args) => Isolated(name, dotnet, [typeof(ReleaseCommands).Assembly.Location, .. args]);
        CommandResult Python(string name, string script, params string[] args)
        {
            var temp = Array.IndexOf(args, "--temp-root");
            if (temp >= 0 && script == "scripts/checks/Test-ReleaseApprovalGate.py") Directory.CreateDirectory(args[temp + 1]);
            return Isolated(name, python, ["-X", "utf8", Path.Combine(Root, script), .. args]);
        }
        CommandResult Isolated(string name, string executable, string[] args, bool approval = true)
        {
            var environment = FastSuites.EnvironmentFor(Path.Combine(output, "isolation", name), api);
            var config = Path.Combine(environment["TIA_MCP_DATA_DIRECTORY"]!, "config"); Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=" + approval.ToString().ToLowerInvariant() + "\ntimeoutSeconds=120\n");
            environment["TIA_MCP_RELEASE_TEMP_ROOT"] = environment["TEMP"];
            environment["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget; environment["RestoreConfigFile"] = nuget;
            environment["PYTHONIOENCODING"] = "utf-8";
            environment["TIA_MCP_CHECK_NO_BUILD"] = "1";
            environment["TIA_MCP_PLC_TOOLS_PYTHON"] = companionPython;
            environment["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
            return SuiteHang.RunProcess(executable, args, Root, environment);
        }
        void Add(string name, Func<CommandResult> run, string[]? depends = null, bool exclusive = false)
        {
            var job = new CheckJob(name, run, depends ?? [], Exclusive: exclusive);
            jobs.Add(job with { Timeout = CheckScheduler.JobTimeout(job, costs) });
        }
        void Wave(string[]? prerequisites = null)
        {
            if (jobs.Count == 0) return;
            for (int index = 0; index < jobs.Count; index++) jobs[index] = jobs[index] with { EstimatedSeconds = CheckAllJobCost(jobs[index], costs) };
            planned.AddRange(jobs.Select(job => job.Name));
            allJobs.AddRange(jobs.Select(job => job with { Dependencies = [.. job.Dependencies, .. prerequisites ?? []] }));
            timings.AddRange(CheckScheduler.Run(jobs.ToArray(), parallelism, Path.Combine(output, "logs"), prerequisites: timings.ToArray(), packing: packing));
            jobs.Clear();
        }
        // Generation is an ordered prerequisite of compilation/capture. -Check is
        // also retained below, so regeneration never hides source drift.
        Add("generate-catalogs", () => Tool("generate-catalogs", "generate-catalogs"));
        Add("generate-tool-usage", () =>
        {
            var usage = Tool("generate-tool-usage", "generate-tool-usage");
            if (usage.ExitCode != 0) return usage;
            var counts = Tool("generate-tool-counts", "generate-tool-counts");
            return new(counts.ExitCode, usage.StandardOutput + counts.StandardOutput,
                usage.StandardError + counts.StandardError);
        }, ["generate-catalogs"]);
        Wave();
        if (!CheckAllPrerequisitesPassed(timings)) return Finish();
        var builds = CheckAllBuildInventory(catalog, api);
        AddCheckAllStaticJobs(Add, Tool, Python, Isolated, output, api, nuget, parallelism, shards, dotnet);
        jobs.RemoveAll(job => job.Name == "preflight");
        var staticJobs = CiCoverage.Rewrite(jobs, ciCovered).ToArray(); jobs.Clear();
        // Migration writes documentation only, but source checks must see its
        // final bytes. Keep the generation job and every drift check.
        if (!options.Has("BuildOnly"))
        {
            Add("generate-release-migration", () => Tool("generate-release-migration", "generate-release-migration"));
            Wave(["generate-tool-usage"]);
        }
        var runtimeManifest = Path.Combine(output, "build/runtime-paths.tsv");
        var runtimeReady = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CheckJob? parity = null;
        Task<CheckTiming>? parityTask = null;
        if (!options.Has("BuildOnly"))
        {
            parity = new("suite-output-parity", () => CheckAllLayout.Verify(Root, builds, dotnet, nuget, api,
                Path.Combine(output, "suite-output-parity"), runtimeManifest, buildParallelism, independent: true,
                awaitRuntime: () => CheckScheduler.AwaitRuntime(runtimeReady.Task)), ["generate-tool-usage", "generate-release-migration"]);
            parity = parity with { Priority = 2, Affinity = layout.General, EstimatedSeconds = CheckAllJobCost(parity, costs), Timeout = CheckScheduler.JobTimeout(parity, costs) };
            parityTask = CheckScheduler.Start(parity, Path.Combine(output, "logs"));
        }
        Add("build-once", () =>
        {
            try
            {
                var result = CheckAllCachedBuild(options, builds, output, dotnet, api, nuget, buildParallelism);
                runtimeReady.TrySetResult(result);
                return result;
            }
            catch (Exception ex) { runtimeReady.TrySetResult(new(1, "", ex.Message)); throw; }
        });
        jobs[^1] = jobs[^1] with { Priority = 2, Affinity = available };
        Add("analyzer-warnings", () =>
        {
            var directory = Path.Combine(output, "build/analyzer-warnings");
            var result = Tool("analyzer-warnings", "check-analyzer-warnings", "-Check", "-Complete", "-DiagnosticsDirectory", directory);
            if (result.ExitCode == 0) AnalyzerWarnings.RememberDirectory(Root, directory);
            return result;
        }, ["build-once"]);
        if (!options.Has("BuildOnly")) jobs.AddRange(staticJobs.Where(job => CheckAllEarly(job.Name))
            .Select(CheckAllEarlyDependencies).Select(job => job with { Priority = 1, Affinity = layout.General }));
        try { Wave(["generate-tool-usage"]); }
        finally { runtimeReady.TrySetResult(new(1, "", "Build wave did not publish runtime outputs.")); }
        if (!CheckAllPrerequisitesPassed(timings.Where(row => row.Name is "generate-catalogs" or "generate-tool-usage" or "build-once")))
        {
            if (parityTask != null) { planned.Add(parity!.Name); allJobs.Add(parity); timings.Add(parityTask.GetAwaiter().GetResult()); }
            return Finish();
        }
        AnalyzerWarnings.RememberDirectory(Root, Path.Combine(output, "build/analyzer-warnings"));
        if (options.Has("BuildOnly")) return Finish();
        jobs.Add(parity!);
        string host = Path.Combine(Root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe");
        string harness = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe");
        // Stability shares wall time, but no CPUs, with the general lane. The
        // Windows job includes the client, both hosts, workers and descendants.
        foreach (var release in new[] { "20", "21" })
        foreach (var concurrency in new[] { "8", "16" })
        foreach (var transport in new[] { "stdio", "http" })
        foreach (var profile in new[] { "full", "lite" })
        {
            var name = CheckAllPlcSuccessor($"stability-v{release}-c{concurrency}-{transport}-{profile}");
            var key = release; var c = concurrency; var t = transport; var p = profile;
            Add(name, () =>
            {
                var directory = Path.Combine(output, name);
                var result = Python(name, "scripts/checks/Test-LocalStability.py", ["--exe", host, "--major", key, "--host-harness", harness,
                    "--public-api", ResolveReleaseApi(null, api, int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)),
                    "--plc-worker", CheckAllPlcFixture(key),
                    "--rounds", "50", "--concurrency", c, "--transports", t, "--profiles", p, "--output", directory]);
                if (result.ExitCode == 0)
                {
                    var evidence = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "result.json")))!;
                    if ((string?)evidence["status"] != "passed" || (int?)evidence["rounds"] != 50 || evidence["runs"]!.AsArray().Count != 1 ||
                        (string?)evidence["runtimeSha256"] != ReleaseRecords.HashFile(CheckAllPlcFixture(key)) || (string?)evidence["hostSha256"] != ReleaseRecords.HashFile(host) ||
                        (string?)evidence["sdkWorkerSha256"] != ReleaseRecords.HashFile(CheckAllPlcFixture(key)))
                        throw new ReleaseException("Stability evidence mismatch");
                }
                return result;
            }, null);
            jobs[^1] = jobs[^1] with { Lane = "stability", Affinity = layout.Stability };
        }
        foreach (var (name, node) in catalog)
        {
            var suite = node!.AsObject();
            string? sdk = name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true ? api : null;
            FastSuites.AddJobs(jobs, Root, name, suite, Path.Combine(output, "suites"), dotnet, suite["minimumPassed"]!.GetValue<int>() >= 100 ? shards : 1, sdk, [],
                options.Get("SequentialBaselineDirectory") is { } baseline ? Path.Combine(baseline, name + ".trx") : null, options.Has("ProveEquivalence"), manifest: runtimeManifest, diagnostics: true);
        }
        foreach (var mode in new[] { "false", "true" })
        foreach (var project in new[] { "Core", "Gui" })
        {
            var name = $"studio-{project.ToLowerInvariant()}-{mode}"; var selectedMode = mode; var selectedProject = project;
            Add(name, () => Isolated(name, dotnet, ["test", $"tests/Studio/TiaOpenness.{selectedProject}.Tests/TiaOpenness.{selectedProject}.Tests.csproj", "-c", "Release", "--no-build", "--no-restore",
                "-p:TiaSharedAdapterPaths=" + selectedMode,
                "--disable-build-servers", "-m:1", "-nodeReuse:false", "--logger", "trx;LogFileName=" + name + ".trx", "--results-directory", Path.Combine(output, "studio")]), exclusive: true);
            jobs[^1] = jobs[^1] with { SensitiveGroup = $"tests/Studio/TiaOpenness.{project}.Tests/TiaOpenness.{project}.Tests.csproj" };
        }
        jobs.AddRange(staticJobs.Where(job => !CheckAllEarly(job.Name)));
        // Preflight also depends on the source-only checks completed in the build
        // wave. Retain their results in its aggregate instead of executing twice.
        var preflightInputs = jobs.Concat(staticJobs.Where(job => CheckAllEarly(job.Name))).ToList();
        AddCheckAllPreflightJobs(preflightInputs);
        jobs.Add(preflightInputs[^1]);
        foreach (var release in CatalogJson.Keys)
        {
            var key = release;
            foreach (var kind in new[] { "contracts", "responses" })
            {
                // V20/V21 capture through FoundationHost and the PLC SDK fixture (the engine halves are retired).
                var plc = key is "20" or "21";
                var category = kind;
                var name = plc ? CheckAllPlcSuccessor(category + "-v" + key) : category + "-v" + key;
                var captureDirectory = Path.Combine(output, plc ? category + "-plc" : category);
                Add(name, () =>
                {
                    var script = "scripts/checks/Snapshot-Tool" + (category == "contracts" ? "Contracts" : "Responses") + ".py";
                    var args = new List<string> { "capture", "--repo-root", Root, "--public-api-root", api, "--releases", key, "--exe", key + "=" + host, "--output", captureDirectory };
                    if (plc) args.AddRange(["--engine-host", host, "--plc-worker", key + "=" + CheckAllPlcFixture(key)]);
                    if (category == "responses") args.AddRange(["--temp-root", FastSuites.TemporaryDirectory()]);
                    var result = Python(name, script, args.ToArray());
                    if (result.ExitCode != 0) return result;
                    // Both the existing semantic comparison and an exact-byte check.
                    result = Python(name + "-compare", script, "compare", "--baseline", Path.Combine(Root, "manifest/contracts/v4", category == "contracts" ? "baseline" : "responses"), "--current", captureDirectory, "--releases", key);
                    var old = Path.Combine(Root, "manifest/contracts/v4", category == "contracts" ? "baseline" : "responses", key + ".json");
                    var current = Path.Combine(captureDirectory, key + ".json");
                    if (result.ExitCode == 0 && !File.ReadAllBytes(old).SequenceEqual(File.ReadAllBytes(current)))
                        return result with { ExitCode = 1, StandardError = "Capture bytes differ: " + key };
                    return result;
                }, null);
            }
            var usageName = "transport-usage-v" + key;
            if (key is "20" or "21")
            {
                var plcName = CheckAllPlcSuccessor(usageName);
                Add(plcName, () => Python(plcName, "scripts/checks/Test-FoundationTransport.py", "--plc-worker",
                    "--fixture", Path.Combine(Root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe"),
                    "--host-exe", host, "--releases", key, "--plc-sdk-worker", key + "=" + CheckAllPlcFixture(key),
                    "--public-api-root", api, "--temp-root", FastSuites.TemporaryDirectory(), "--output", Path.Combine(output, plcName)));
            }
            else Add(usageName, () => Python(usageName, "scripts/checks/Test-FoundationTransport.py", "--fixture", Path.Combine(Root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe"),
                "--host-exe", host, "--releases", key, "--temp-root", FastSuites.TemporaryDirectory(), "--output", Path.Combine(output, usageName)));
            var sourceUsageName = "usage-v" + key;
            if (key is "20" or "21")
            {
                var plcName = CheckAllPlcSuccessor(sourceUsageName);
                Add(plcName, () => Python(plcName, "scripts/checks/Test-ToolUsage.py", "--foundation-host", host,
                    "--plc-worker", key + "=" + CheckAllPlcFixture(key), "--public-api-root", api,
                    "--releases", key, "--evidence", Path.Combine(Root, "bin-build/u", Guid.NewGuid().ToString("N")),
                    "--coverage-output", Path.Combine(output, plcName + ".json")));
            }
            else Add(sourceUsageName, () => Python(sourceUsageName, "scripts/checks/Test-ToolUsage.py", "--foundation-host", host, "--public-api-root", api,
                "--releases", key, "--evidence", Path.Combine(Root, "bin-build/u", Guid.NewGuid().ToString("N")), "--coverage-output", Path.Combine(output, sourceUsageName + ".json")));
        }
        foreach (var release in new[] { "20", "21" })
        {
            var key = release;
            var targetApi = ResolveReleaseApi(null, api, int.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
            foreach (var mode in CheckAllPlcHarnessModes(key))
            {
                var selected = mode; var plcName = CheckAllPlcHarnessJob(key, mode);
                Add(plcName, () =>
                {
                    var inventory = Path.Combine(output, "adapter-coverage-v" + key + ".json");
                    var result = CheckAllHarness(Python(plcName, "scripts/checks/Test-PlcHarness.py",
                        "--release", key, "--mode", selected.Length == 0 ? "http" : selected,
                        "--host", host, "--worker", CheckAllPlcFixture(key), "--harness", harness,
                        "--release-tool", CheckAllBuild.ReleaseTool(Root, runtimeManifest),
                        "--runtime-manifest", runtimeManifest,
                        "--api", targetApi, "--public-api-root", api, "--adapter-inventory", inventory,
                        "--output", Path.Combine(output, plcName)), selected, key);
                    if (result.ExitCode == 0)
                        ValidateCheckAllPlcHarness(result.StandardOutput, Path.Combine(output, plcName), selected, key, host, CheckAllPlcFixture(key), inventory);
                    return result;
                }, selected == "native-diagnostics-only" ? ["adapter-coverage-v" + key] : null);
            }
            var plcCampaignName = CheckAllPlcSuccessor("campaign-inputs-v" + key);
            Add(plcCampaignName, () => Python(plcCampaignName, "scripts/checks/Test-CampaignInputs.py", "--exe", host, "--major", key,
                "--public-api", targetApi, "--plc-worker", CheckAllPlcFixture(key), "--output", Path.Combine(output, plcCampaignName)));
            // The adapter inventory the harness diagnostics job compares with: the PLC SDK fixture's adapter.
            var coverageName = "adapter-coverage-v" + key;
            Add(coverageName, () => Isolated(coverageName, dotnet, [Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"), "verify",
                Path.Combine(Path.GetDirectoryName(CheckAllPlcFixture(key))!, "TiaMcp.Adapter." + key + ".dll"), Path.Combine(output, coverageName + ".json")]));
            var approvalName = CheckAllPlcSuccessor("approval-v" + key);
            Add(approvalName, () =>
            {
                var result = Python(approvalName, "scripts/checks/Test-ReleaseApprovalGate.py", ["--product", "engine", "--major", key, "--exe", host,
                    "--portal-root", targetApi, "--public-api", targetApi, "--host-harness", harness,
                    "--plc-worker", CheckAllPlcFixture(key),
                    "--temp-root", FastSuites.TemporaryDirectory(), "--output", Path.Combine(output, approvalName)]);
                if (result.ExitCode == 0) ValidateCheckAllApproval(Path.Combine(output, approvalName, "result.json"));
                return result;
            }, null);
            var resourceName = CheckAllPlcSuccessor("resources-v" + key);
            Add(resourceName, () => Python(resourceName, "scripts/checks/Test-ResourceDiscovery.py", ["--exe", host, "--portal-root", targetApi, "--major", key,
                "--plc-worker", CheckAllPlcFixture(key), "--public-api", targetApi, "--usage-output", Path.Combine(output, resourceName + ".json")]), null);
            var ecosystemName = CheckAllPlcSuccessor("ecosystem-v" + key);
            Add(ecosystemName, () =>
            {
                var evidenceDirectory = Path.Combine(output, ecosystemName);
                var result = Python(ecosystemName, "scripts/checks/Test-V21Ecosystem.py", ["--exe", host, "--major", key, "--host-harness", harness, "--public-api", targetApi,
                    "--schema-root", Path.Combine(Path.GetDirectoryName(ResolveReleaseApi(null, api, 21))!, "Schemas"), "--plc-worker", CheckAllPlcFixture(key), "--output", evidenceDirectory]);
                if (result.ExitCode == 0)
                {
                    var evidencePath = Directory.EnumerateFiles(evidenceDirectory, "result.json", SearchOption.AllDirectories).Single();
                    using var doc = JsonDocument.Parse(File.ReadAllText(evidencePath)); var evidence = doc.RootElement;
                    CheckReleaseCount("v21Ecosystem", GetJsonInt(evidence, "checks"), "Ecosystem checks incomplete");
                    if (GetJsonString(evidence, "status") != "passed" || GetJsonBool(evidence, "selfTestOnly") || GetJsonBool(evidence, "nativeTiaExecuted") || GetJsonString(evidence, "runtimeSha256") != ReleaseRecords.HashFile(CheckAllPlcFixture(key)) ||
                        GetJsonString(evidence, "hostSha256") != ReleaseRecords.HashFile(host) || GetJsonString(evidence, "sdkWorkerSha256") != ReleaseRecords.HashFile(CheckAllPlcFixture(key)))
                        throw new ReleaseException("Ecosystem evidence mismatch");
                }
                return result;
            }, null);
        }
        {
            var auditName = CheckAllPlcSuccessor("usage-audit");
            string UsageName(string key) => key is "20" or "21" ? CheckAllPlcSuccessor("usage-v" + key) : "usage-v" + key;
            Add(auditName, () =>
            {
                var receipts = new JsonArray();
                foreach (var key in CatalogJson.Keys)
                {
                    var path = Path.Combine(output, UsageName(key) + ".json");
                    var captured = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
                    var receipt = captured.Single()!.DeepClone().AsObject();
                    if (key is "20" or "21")
                    {
                        // A V20/V21 receipt must come from this host and PlcWorker. The audit row keeps the
                        // committed receipt fields, so the committed manifest bytes remain the comparison target.
                        var worker = ReleaseRecords.HashFile(CheckAllPlcFixture(key));
                        if ((string?)receipt["runtimeSha256"] != worker || (string?)receipt["sdkWorkerSha256"] != worker ||
                            (string?)receipt["hostSha256"] != ReleaseRecords.HashFile(host) || (string?)receipt["workerModel"] != "foundation-host-plc-worker")
                            throw new ReleaseException("PLC usage receipt evidence mismatch: " + key);
                        foreach (var field in new[] { "hostSha256", "sdkWorkerSha256", "runtimeSha256", "workerModel" }) receipt.Remove(field);
                    }
                    receipts.Add(receipt);
                }
                var records = Path.Combine(output, "usage-receipts-plc.json"); File.WriteAllText(records, receipts.ToJsonString());
                var coverage = Isolated(auditName + "-coverage", python, ["-X", "utf8", "-c",
                    "import importlib.util, json, sys\nfrom pathlib import Path\nscript = Path(sys.argv[1])\nsys.path.insert(0, str(script.parent))\nspec = importlib.util.spec_from_file_location('usage_coverage', script)\nmodule = importlib.util.module_from_spec(spec)\nspec.loader.exec_module(module)\nmodule.coverage(json.loads(Path(sys.argv[2]).read_text('utf-8')), Path(sys.argv[3]))\n",
                    Path.Combine(Root, "scripts/checks/Test-ToolUsage.py"), records, Path.Combine(output, "tool-usage-coverage-plc.json")]);
                if (coverage.ExitCode != 0) return coverage;
                var audit = CatalogJson.Encode(CatalogAudits.Usage(Root, recordsPath: records));
                File.WriteAllText(Path.Combine(output, "tool-usage-audit-plc.json"), audit);
                // No tracked file is written; the audit is compared with the committed manifest bytes.
                if (!File.ReadAllBytes(Path.Combine(Root, "manifest/tool-usage-coverage.json")).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(audit)))
                    return new(1, coverage.StandardOutput, coverage.StandardError + "PLC usage audit differs from manifest/tool-usage-coverage.json\n");
                return new(0, coverage.StandardOutput + "PLC usage audit passed; eight receipts validated; manifest bytes match\n", coverage.StandardError);
            }, CatalogJson.Keys.Select(UsageName).ToArray());
        }
        ValidateCheckAllPlcScriptJobs(jobs);
        ValidateCheckAllPlcHarnessJobs(jobs);
        CiCoverage.RequirePlan([.. allJobs, .. jobs], ciCovered);
        var scheduled = CiCoverage.Rewrite(CheckAllRuntimeDependencies(jobs, independentParity: true), ciCovered).Select(job => job with
        {
            EstimatedSeconds = CheckAllJobCost(job, costs),
            Timeout = CheckScheduler.JobTimeout(job, costs),
            Priority = job.Priority > 0 || !staticJobs.Any(item => item.Name == job.Name) ? job.Priority : 1
        }).ToList();
        var sensitiveLanes = CheckAffinity.AssignLanes(scheduled, layout, sensitiveParallelism);
        var resultCache = new CheckAllResultCache(Root, output, runtimeManifest, api, nuget, dotnet, python, companionPython, options,
            JsonSerializer.Serialize(new { parallelism, shards, stabilityParallelism, lanes = sensitiveLanes, available, stability = layout.Stability,
                sensitive = layout.Sensitive, general = layout.General }));
        var plan = scheduled.Select(job => job.CiCoverage != null ? job : resultCache.Bind(CheckAllResultCache.ReusableStatic(job.Name)
            ? job with { ResultInputs = new(job.Name.StartsWith("python-", StringComparison.Ordinal) ? "python" : job.Name.EndsWith("-drift", StringComparison.Ordinal) ? "generator" : "static", job.Name) } : job)).ToArray();
        CheckAllInputClosure.WriteInventory(Root, plan, output);
        var lanes = new Dictionary<string, int>(sensitiveLanes) { ["general"] = parallelism, ["stability"] = stabilityParallelism };
        allJobs.AddRange(plan);
        var priorities = CheckScheduler.CriticalCosts(allJobs);
        File.WriteAllText(Path.Combine(output, "cpu-plan.json"), JsonSerializer.Serialize(new { availableMask = available.ToString("X"),
            cpuIsolation = layout.Isolation, cpuIsolationReason = layout.Reason,
            stabilityMask = layout.Stability.ToString("X"), sensitiveMask = layout.Sensitive.ToString("X"), generalMask = layout.General.ToString("X"), stabilityCores, generalCores,
            physicalCores = CheckAffinity.PhysicalCores().Select(core => new { mask = core.Mask.ToString("X"), efficiencyClass = core.Efficiency }),
            buildParallelism, stabilityParallelism, sensitiveParallelism = sensitiveLanes.Count, sensitiveCores = System.Numerics.BitOperations.PopCount(layout.Sensitive), sensitiveMinWorkerThreads = CheckAffinity.BaselineWorkers, generalParallelism = parallelism, packing, companionPython,
            checks = allJobs.Select(job => new { job.Name, job.Lane, job.SensitiveGroup, job.Slots, job.Exclusive, job.Priority, affinityMask = job.Affinity?.ToString("X"), job.EstimatedSeconds, timeoutSeconds = job.Timeout.TotalSeconds, criticalSeconds = priorities[job.Name], job.Dependencies,
                runtimeManifestDependency = job.Name == "suite-output-parity" ? "build-once (comparison phase only)" : null,
                resultCache = job.ResultCachePolicy ?? "always rerun: generated evidence, mutable machine inputs or aggregate gate" }) }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        planned.AddRange(plan.Select(job => job.Name));
        timings.AddRange(CheckScheduler.Run(plan, parallelism, Path.Combine(output, "logs"),
            lanes, timings.ToArray(), packing, new Dictionary<Task<CheckTiming>, CheckJob> { [parityTask!] = plan.Single(job => job.Name == "suite-output-parity") }));
        return Finish();

        int Finish()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            foreach (var row in timings.Where(row => row.Status == "passed")) costs[row.Name] = Math.Max(costs.GetValueOrDefault(row.Name), row.Seconds);
            File.WriteAllText(history, JsonSerializer.Serialize(costs, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            foreach (var row in timings.Where(row => row.Status == "passed" && row.Resources is { CpuSeconds: > 0 })) resources[row.Name] = row.Resources!;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(history)!, "check-resources.json"), JsonSerializer.Serialize(resources, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            var after = TrackedBytes();
            var drift = after.Where(pair => !before.TryGetValue(pair.Key, out var hash) || pair.Value != hash).Select(pair => pair.Key)
                .Concat(before.Keys.Except(after.Keys, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            // These two generators own generation timestamps. Only the timestamp
            // fields are ignored; any other byte change remains a failed drift gate.
            var unexpected = drift.Where(path => !TimestampOnly(path, before)).ToArray();
            timings.Add(new("tracked-tree-drift", unexpected.Length == 0 ? "passed" : "failed", unexpected.Length == 0 ? 0 : 1, 0, string.Join(", ", unexpected)));
            var performance = CheckScheduler.Performance(allJobs, timings, DateTime.UtcNow - clock.Elapsed, clock.Elapsed.TotalSeconds);
            var report = new { schemaVersion = 1, machineCores = Environment.ProcessorCount, cpuIsolation = layout.Isolation, cpuIsolationReason = layout.Reason,
                parallelism, shards, cold = options.Has("Cold"), clean = options.Has("Clean"),
                wallSeconds = clock.Elapsed.TotalSeconds, underThirtyMinutes = clock.Elapsed.TotalSeconds < 1800, buildOnly = options.Has("BuildOnly"), nativeTiaExecuted = false,
                passed = timings.Count(CheckTiming.Passed), reused = timings.Count(row => row.Status == "passed (reused)"),
                ciCovered = timings.Count(row => row.Status == "covered by CI"),
                failed = timings.Count(row => !CheckTiming.Passed(row)), drift, performance, checks = timings };
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            WriteTimingComparison(output, timings, options.Get("BaselineReport"), layout);
            File.AppendAllText(Path.Combine(output, "comparison.md"), $"\nDependency critical path ({performance.CriticalPathSeconds:F3}s of job execution): {string.Join(" -> ", performance.CriticalPath)}\nAverage job concurrency: {performance.AverageConcurrency:F3}; peak: {performance.PeakConcurrency}. Includes process waits and reused-evidence lookups, not CPU utilization.\n");
            var exitCode = report.failed == 0 && report.underThirtyMinutes ? 0 : 1;
            string evidencePath;
            try { evidencePath = ReleaseEvidence.Write(releaseEvidence, output, planned, timings, exitCode, report.wallSeconds, report.underThirtyMinutes, unexpected, DateTimeOffset.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { evidencePath = "not written (" + ex.Message + ")"; }
            Console.WriteLine($"check-all: {report.passed} passed; {report.failed} failed/blocked; {report.wallSeconds:F3}s; report: {output}; release evidence: {evidencePath}");
            Console.WriteLine($"Dependency critical path: {string.Join(" -> ", performance.CriticalPath)} ({performance.CriticalPathSeconds:F3}s); achieved job concurrency: average {performance.AverageConcurrency:F3}, peak {performance.PeakConcurrency}");
            return exitCode;
        }
    }

    internal static string? CheckAllCompanionPython(Options options)
    {
        var python = options.Get("CompanionPython") ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        if (string.IsNullOrWhiteSpace(python))
        {
            var roots = new[] { Root, options.Get("PublicApiRoot") is { } api ? Path.GetDirectoryName(Path.GetFullPath(api))! : Root };
            python = roots.Select(root => Path.Combine(root, "TiaMcp_Output/ecosystem-python/Scripts/python.exe")).FirstOrDefault(File.Exists);
        }
        return string.IsNullOrWhiteSpace(python) ? null : ResolveApplication(python);
    }

    internal static string ResolveResultApplication(string application) => ResolveApplication(application);

    internal static bool CheckAllPrerequisitesPassed(IEnumerable<CheckTiming> timings) => timings.All(CheckTiming.Passed);

    internal static double CheckAllJobCost(CheckJob job, IReadOnlyDictionary<string, double> history) =>
        job.Name == "preflight" ? 0 : job.EstimatedSeconds > 0 ? job.EstimatedSeconds
        : history.TryGetValue(job.Name, out var seconds) ? seconds : CheckAllEstimatedCost(job.Name);

    private static double CheckAllEstimatedCost(string name) => name.StartsWith("responses-", StringComparison.Ordinal) ? 60
        : name.StartsWith("usage-v", StringComparison.Ordinal) || name.StartsWith("harness-", StringComparison.Ordinal) ? 25
        : name.Contains("catalog", StringComparison.Ordinal) ? 5 : 1;

    private static Dictionary<string, string> TrackedBytes()
    {
        var result = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], Root); ProcessRunner.RequireSuccess(result, "source file inventory");
        return CheckAllTrackedBytes(Root, result.StandardOutput);
    }

    internal static Dictionary<string, string> CheckAllTrackedBytes(string root, string inventory)
        // An unmerged index lists a working file once per stage; drift still checks every unique file's bytes.
        => inventory.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
            .Where(path => File.Exists(Path.Combine(root, path))).ToDictionary(path => path,
                path => Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, path))), StringComparer.Ordinal);

    private static bool TimestampOnly(string path, Dictionary<string, string> before)
    {
        if (path is not ("manifest/version-tools.json" or "manifest/tool-usage-coverage.json") || !before.TryGetValue(path, out var bytes) || !File.Exists(Path.Combine(Root, path))) return false;
        var old = JsonNode.Parse(Convert.FromBase64String(bytes))!.AsObject(); var current = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Root, path)))!.AsObject();
        old.Remove("generatedAtUtc"); current.Remove("generatedAtUtc");
        return JsonNode.DeepEquals(old, current);
    }

    private static void WriteTimingComparison(string output, List<CheckTiming> timings, string? baseline, CheckAffinity.CheckAllCpuLayout layout)
    {
        var old = baseline == null ? new Dictionary<string, double> { ["foundation"] = 656, ["foundation-api"] = 666, ["release-tool"] = 144, ["engine-host"] = 141 } : JsonNode.Parse(File.ReadAllText(baseline))!["checks"]!.AsArray()
            .ToDictionary(row => (string)row!["Name"]!, row => (double)row!["Seconds"]!);
        var lines = new List<string> { "cpuIsolation: " + layout.Isolation };
        if (layout.Reason != null) lines.Add("cpuIsolationReason: " + layout.Reason);
        lines.AddRange(["", "Suite times span the first shard start through the final merge, including queue time. Reused and CI-covered passes are evidence lookups, not new local execution measurements. Raw process timings remain in report.json.", "", "| Check | Old seconds | New seconds | Reused checks | CI-covered checks | Result |", "|---|---:|---:|---:|---:|---|"]);
        foreach (var group in timings.GroupBy(row => Regex.Replace(row.Name, "-(shard-[0-9]+|gate)$", "")))
        {
            var rows = group.ToArray();
            var seconds = rows.Length == 1 || rows.Any(row => row.StartedAtUtc == default) ? rows.Max(row => row.Seconds)
                : (rows.Max(row => row.StartedAtUtc.AddSeconds(row.Seconds)) - rows.Min(row => row.StartedAtUtc)).TotalSeconds;
            lines.Add($"| {group.Key} | {(old.TryGetValue(group.Key, out var value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "NOT MEASURED")} | {seconds.ToString("F3", CultureInfo.InvariantCulture)} | {rows.Count(row => row.Status == "passed (reused)")} | {rows.Count(row => row.Status == "covered by CI")} | {(rows.All(CheckTiming.Passed) ? "passed" : "failed/blocked")} |");
        }
        File.WriteAllText(Path.Combine(output, "comparison.md"), string.Join("\n", lines) + "\n");
    }

    private static CheckBuild[] CheckAllBuildInventory(JsonObject catalog, string apiRoot)
    {
        var units = catalog.Select(pair => CheckAllBuild.Suite(pair.Value!.AsObject())).ToList();
        foreach (var solution in new[] { "TiaPortalOpenness.Offline.slnx", "TiaPortalOpenness.slnx" })
            foreach (var project in XDocument.Load(Path.Combine(Root, solution)).Descendants("Project")) units.Add(new((string)project.Attribute("Path")!, "", []));
        // Same properties as build-plc-workers: the worker references Siemens.Engineering directly (OPC UA modelled phases).
        foreach (var key in CatalogJson.Keys)
            units.Add(new("src/PlcWorker/TiaMcp.PlcWorker.csproj", "", ["-p:TiaReleaseKey=" + key,
                "-p:SiemensEngineeringDirectory=" + Path.GetFullPath(Path.Combine(apiRoot, ReleaseCommands.PlcWorkerApi(key).Directory))]));
        units.AddRange(CheckAllPlcFixtureBuilds(apiRoot));
        foreach (var mode in new[] { "false", "true" })
        foreach (var project in new[] { "src/Studio/Bridge/TiaOpenness.Bridge.csproj", "src/Studio/Gui/TiaOpenness.Gui.csproj", "tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj", "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj" })
            units.Add(new(project, "", ["-p:TiaSharedAdapterPaths=" + mode]));
        return CheckAllBuild.WithTestFixtures(units).ToArray();
    }

}
