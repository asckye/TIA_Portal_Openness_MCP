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
        var checkProfile = CheckAllProfiles.Parse(options);
        if (options.Has("Affected") && (checkProfile != "merge" || options.Has("CiCovered")))
            throw new ReleaseException("-Affected requires the local merge profile", 64);
        if (options.Has("Quick") && (!options.Has("Affected") || options.Has("Clean") || options.Has("Cold") || options.Has("NoBuildCache") || options.Has("BuildOnly")))
            throw new ReleaseException("-Quick requires -Affected and a warm build (no -Clean/-Cold/-NoBuildCache/-BuildOnly)", 64);
        if (checkProfile == "nightly" && options.Has("CiCovered"))
            throw new ReleaseException("nightly requires local same-run subset/superset TRXs; use -CiCovered with merge", 64);
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
        var derivations = CheckAllProfiles.Plan(catalog, api, nuget, companionPython, dotnet);
        CheckAllProfiles.WriteInventory(catalog, output, checkProfile, derivations);
        if (options.Get("BaselineReport") is { } timingBaseline)
            SuiteCosts.ImportReport(Path.GetDirectoryName(Path.GetFullPath(timingBaseline))!, Path.Combine(Root, "bin-build/check-timings"));
        var history = Path.Combine(Root, "bin-build/check-timings/check-costs.json");
        var costs = File.Exists(history) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(history))! : new Dictionary<string, double>();
        var baselineReport = options.Get("BaselineReport") ?? (File.Exists(Path.Combine(Root, "bin-build/reviewer-run1/report.json")) ? Path.Combine(Root, "bin-build/reviewer-run1/report.json") : null);
        if (baselineReport != null)
            foreach (var row in JsonNode.Parse(File.ReadAllText(baselineReport))!["checks"]!.AsArray())
                costs[(string)row!["Name"]!] = Math.Max(costs.GetValueOrDefault((string)row["Name"]!), (double)row["Seconds"]!);
        var resources = CheckPacking.Read(Root, baselineReport);
        var orderCosts = CheckPacking.ReadOrderCosts(Root, baselineReport);
        var suitePlan = new List<CheckJob>();
        foreach (var (name, node) in catalog)
        {
            if (!CheckAllProfiles.RunsSeparately(name, checkProfile, derivations)) continue;
            FastSuites.AddJobs(suitePlan, Root, name, node!.AsObject(), Path.Combine(output, "packing-suites"), dotnet,
                node["minimumPassed"]!.GetValue<int>() >= 100 ? shards : 1, null, []);
        }
        foreach (var project in new[] { "Core", "Gui" })
        foreach (var mode in new[] { "false", "true" })
            suitePlan.Add(new CheckJob("studio-" + project.ToLowerInvariant() + "-" + mode, () => new(0, "", ""), [], Exclusive: true)
                { SensitiveGroup = $"tests/Studio/TiaOpenness.{project}.Tests/TiaOpenness.{project}.Tests.csproj" });
        int sensitiveParallelism = FastSuites.Positive(options, "SensitiveParallelism", CheckAffinity.DefaultCheckAllSensitiveWorkers);
        int sensitiveDefault = CheckPacking.ReservedCores(suitePlan.Where(job => job.Exclusive), resources, sensitiveParallelism);
        var stabilityPlan = CheckAllPlcScriptJobs().Where(name => name.StartsWith("stability-", StringComparison.Ordinal))
            .Select(name => new CheckJob(name, () => new(0, "", ""), []));
        int stabilityDefault = CheckPacking.ReservedCores(stabilityPlan, resources, FastSuites.Positive(options, "StabilityParallelism", 4), exclusive: false);
        // Give the shortest asserted deadlines first choice of complete physical
        // cores. Stability and general work retain disjoint, reserved CPU sets.
        // A machine too small for the default lanes runs a recorded degraded
        // layout; explicit lane sizes are kept exactly or refused (exit 64).
        var sensitiveWorkers = CheckAffinity.PlanCheckAllWorkers(available, FastSuites.Positive(options, "SensitiveCores", sensitiveDefault), options.Has("SensitiveCores"),
            FastSuites.Positive(options, "StabilityCores", stabilityDefault), options.Has("StabilityCores"), CheckAffinity.PreferredPhysicalCores(),
            sensitiveParallelism, options.Has("SensitiveParallelism"), CheckPacking.ReservedCores(suitePlan.Where(job => job.Exclusive), resources, 2));
        var layout = sensitiveWorkers.Cpu;
        if (sensitiveWorkers.Reason != null) Console.WriteLine("check-all: " + sensitiveWorkers.Reason);
        if (layout.Isolation != CheckAffinity.FullIsolation) Console.WriteLine("check-all: cpuIsolation " + layout.Isolation + ": " + layout.Reason);
        int stabilityCores = System.Numerics.BitOperations.PopCount(layout.Stability);
        int generalCores = System.Numerics.BitOperations.PopCount(layout.General);
        var sensitiveNames = suitePlan.Where(job => job.Exclusive).Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        var packing = CheckPacking.Plan(layout.General, resources.Where(pair => !sensitiveNames.Contains(pair.Key) &&
            !pair.Key.StartsWith("stability-", StringComparison.Ordinal) && pair.Key is not ("build-once" or "suite-output-parity" or "configuration-page")).Select(pair => pair.Value),
            CheckAffinity.AvailableMemory(), Math.Min(32, FastSuites.CheckAllParallelism(options, 32))) with { Available = available,
                // An alternate compiler (including the synthetic build fixture)
                // cannot inherit another executable's measured CPU reservations.
                History = string.Equals(ResolveResultApplication(dotnet), ResolveResultApplication(Dotnet), StringComparison.OrdinalIgnoreCase)
                    ? resources : new Dictionary<string, CheckResourceUsage>() };
        parallelism = packing.Parallelism;
        int buildParallelism = FastSuites.Positive(options, "BuildParallelism", System.Numerics.BitOperations.PopCount(available));
        int stabilityParallelism = Math.Min(FastSuites.Positive(options, "StabilityParallelism", 4), Math.Min(4, stabilityCores));
        using var load = CheckLoad.Start(Path.Combine(output, "process-load.json"));
        var before = TrackedBytes();
        var timings = new List<CheckTiming>();
        var jobs = new List<CheckJob>();
        var background = new Dictionary<Task<CheckTiming>, CheckJob>();
        var allJobs = new List<CheckJob>();
        var planned = new List<string>();
        CheckAllAffected? affected = null;
        bool recordIdentities = NightlyRuns.EvidenceRun(options);
        string coverageMachine;
        try { coverageMachine = CheckAllResultCache.MachineIdentity(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { coverageMachine = "unavailable:" + Guid.NewGuid().ToString("N"); }
        var nightlyCoverage = new CheckAllNightlyCoverage(output, coverageMachine, options.Has("Affected") && !options.Has("Quick") &&
            !options.Has("NoResultCache") && !options.Has("NoBuildCache") && !options.Has("ProveEquivalence") && !options.Has("SequentialBaselineDirectory")
            ? Path.Combine(NightlyRuns.DirectoryFor(Root), "latest-passed.json") : null, recordPasses: recordIdentities);
        var nightlyOnly = new List<string>();
        bool Early(string name) => CheckAllEarly(name) && (!options.Has("Affected") || CheckAllJobInputs.AlwaysRun(name));
        // The release may adopt this run (P8-93): bind the commit, the tree and every input before anything is regenerated.
        var releaseEvidence = ReleaseEvidence.BeginSafely(() => ReleaseEvidence.Begin(Root, "git", options, ReleaseEvidence.ProbeExternal(Root, api, nuget, Dotnet, python, companionPython),
            Environment.GetEnvironmentVariable, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        if (recordIdentities && releaseEvidence["source"]?["cleanAtStart"]?.GetValue<bool>() == true && Environment.GetEnvironmentVariable("TIA_MCP_NIGHTLY_MANAGED") != "1")
            NightlyRuns.Save(NightlyRuns.DirectoryFor(Root), new(Path.GetFileName(Path.TrimEndingDirectorySeparator(output)),
                (string)releaseEvidence["source"]!["commit"]!, Path.Combine(output, "report.json"), "running", 0, false, DateTime.UtcNow - clock.Elapsed));
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
            ConfigureCheckEnvironment(environment, approval);
            return SuiteHang.RunProcess(executable, args, Root, environment);
        }
        void ConfigureCheckEnvironment(IDictionary<string, string?> environment, bool approval = true)
        {
            var config = Path.Combine(environment["TIA_MCP_DATA_DIRECTORY"]!, "config"); Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=" + approval.ToString().ToLowerInvariant() + "\ntimeoutSeconds=120\n");
            environment["TIA_MCP_RELEASE_TEMP_ROOT"] = environment["TEMP"];
            environment["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget; environment["RestoreConfigFile"] = nuget;
            environment["PYTHONIOENCODING"] = "utf-8";
            environment["TIA_MCP_CHECK_NO_BUILD"] = "1";
            environment["TIA_MCP_PLC_TOOLS_PYTHON"] = companionPython;
            environment["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
        }
        SuiteProcess SuiteProcessFor(string name) => (executable, arguments, cwd, environment) =>
        {
            CheckAllProfiles.ApplyEnvironment(derivations.Executions[name], environment!, CheckAllProfiles.StudioSuites.Contains(name));
            return DotnetSuites.RunProcess(executable, arguments, cwd, environment);
        };
        void Add(string name, Func<CommandResult> run, string[]? depends = null, bool exclusive = false)
        {
            var job = new CheckJob(name, run, depends ?? [], Exclusive: exclusive);
            jobs.Add(job with { Timeout = CheckScheduler.JobTimeout(job, costs) });
        }
        void Wave(string[]? prerequisites = null)
        {
            if (jobs.Count == 0) return;
            for (int index = 0; index < jobs.Count; index++) jobs[index] = jobs[index] with {
                EstimatedSeconds = CheckAllJobCost(jobs[index], costs), MeasuredSeconds = orderCosts.TryGetValue(jobs[index].Name, out var measured) ? measured : null };
            planned.AddRange(jobs.Select(job => job.Name));
            allJobs.AddRange(jobs.Select(job => job with { Dependencies = [.. job.Dependencies, .. prerequisites ?? []] }));
            // The build wave has its own MSBuild parallelism and an already
            // running parity task. Its sum of sequential command memory peaks
            // is not an individual runtime-check reservation. Keep the memory
            // fallback, but retain measured CPU and the parity reservation.
            timings.AddRange(CheckScheduler.Run(jobs.ToArray(), parallelism, Path.Combine(output, "logs"), prerequisites: timings.ToArray(),
                packing: packing with { MeasuredMemory = false }, background: background));
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
        if (options.Has("Affected")) affected = CheckAllAffected.Discover(Root, options, builds, dotnet, api);
        var earlyIdentity = recordIdentities
            ? new CheckAllResultCache(Root, output, Path.Combine(output, "build/runtime-paths.tsv"), api, nuget, dotnet, python, companionPython, options, "source-only", discoverVersions: false) : null;
        CheckJob BindEarly(CheckJob job)
        {
            var declaration = CheckAllJobInputs.Find(job, catalog);
            if (earlyIdentity == null || declaration?.SourceOnly != true || CheckAllJobInputs.AlwaysRun(job.Name) || job.CiCoverage != null) return job;
            var spec = new CheckAllResultSpec(declaration.Driver.EndsWith(".py", StringComparison.Ordinal) ? "python" : "job", JsonSerializer.Serialize(declaration));
            return job with { NightlyCoverage = nightlyCoverage.Bind(job, () => earlyIdentity.JobIdentity(job, spec, declaration), false, declaration.Outputs) };
        }
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
        if (!options.Has("BuildOnly") && !options.Has("Quick"))
        {
            parity = new("suite-output-parity", () => CheckAllLayout.Verify(Root, builds, dotnet, nuget, api,
                Path.Combine(output, "suite-output-parity"), runtimeManifest, buildParallelism, independent: true,
                awaitRuntime: () => CheckScheduler.AwaitRuntime(runtimeReady.Task)), ["generate-tool-usage", "generate-release-migration"]);
            parity = parity with { Priority = 2, Affinity = layout.General, EstimatedSeconds = CheckAllJobCost(parity, costs), Timeout = CheckScheduler.JobTimeout(parity, costs) };
            if (!packing.CanAdmit(parity, layout.General, [])) throw new ReleaseException("Parity exceeds the available check CPU/memory budget.");
            parityTask = CheckScheduler.Start(parity, Path.Combine(output, "logs"));
            background.Add(parityTask, parity);
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
        if (!options.Has("BuildOnly")) jobs.AddRange(staticJobs.Where(job => Early(job.Name))
            .Select(CheckAllEarlyDependencies).Select(job => BindEarly(job with { Priority = 1, Affinity = layout.General })));
        try { Wave(["generate-tool-usage"]); }
        finally { runtimeReady.TrySetResult(new(1, "", "Build wave did not publish runtime outputs.")); }
        if (!CheckAllPrerequisitesPassed(timings.Where(row => row.Name is "generate-catalogs" or "generate-tool-usage" or "build-once")))
        {
            if (parityTask != null) { planned.Add(parity!.Name); allJobs.Add(parity); timings.Add(parityTask.GetAwaiter().GetResult()); }
            return Finish();
        }
        AnalyzerWarnings.RememberDirectory(Root, Path.Combine(output, "build/analyzer-warnings"));
        if (options.Has("BuildOnly")) return Finish();
        if (parity != null) jobs.Add(parity);
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
        var workerCopies = new SuiteWorkerCopies(output);
        foreach (var (name, node) in catalog)
        {
            if (!CheckAllProfiles.RunsSeparately(name, checkProfile, derivations)) continue;
            var suite = node!.AsObject();
            string? sdk = name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true ? api : null;
            FastSuites.AddJobs(jobs, Root, name, suite, Path.Combine(output, "suites"), dotnet, suite["minimumPassed"]!.GetValue<int>() >= 100 ? shards : 1, sdk, [],
                options.Get("SequentialBaselineDirectory") is { } baseline ? Path.Combine(baseline, name + ".trx") : null, options.Has("ProveEquivalence"), manifest: runtimeManifest, diagnostics: true, workerCopies: workerCopies, process: SuiteProcessFor(name));
        }
        BindEngineHostResult(jobs, Root, output, runtimeManifest);
        CheckAllProfiles.AddStudio(jobs, Root, catalog, Path.Combine(output, "suites"), dotnet, checkProfile, runtimeManifest,
            history: costs, processFor: SuiteProcessFor, api: api);
        if (checkProfile == "merge") CheckAllProfiles.AddDerived(jobs, catalog, Path.Combine(output, "suites"), derivations);
        else CheckAllProfiles.AddNightlyProofs(jobs, catalog, Path.Combine(output, "suites"));
        jobs.AddRange(staticJobs.Where(job => !Early(job.Name)));
        // Preflight also depends on the source-only checks completed in the build
        // wave. Retain their results in its aggregate instead of executing twice.
        var preflightInputs = jobs.Concat(staticJobs.Where(job => Early(job.Name))).ToList();
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
                    var result = CheckAllHarness(Python(plcName, "scripts/checks/Test-PlcHarness.py", [
                        "--release", key, "--mode", selected.Length == 0 ? "http" : selected,
                        "--host", host, "--worker", CheckAllPlcFixture(key), "--harness", harness,
                        "--release-tool", CheckAllBuild.ReleaseTool(Root, runtimeManifest),
                        "--runtime-manifest", runtimeManifest,
                        .. selected == "native-diagnostics-only" ? new[] { "--engine-host-results", Path.Combine(output, "suites/engine-host.reference.json") } : [],
                        "--api", targetApi, "--public-api-root", api, "--adapter-inventory", inventory,
                        "--output", Path.Combine(output, plcName)]), selected, key);
                    if (result.ExitCode == 0)
                        ValidateCheckAllPlcHarness(result.StandardOutput, Path.Combine(output, plcName), selected, key, host, CheckAllPlcFixture(key), inventory);
                    return result;
                }, selected == "native-diagnostics-only" ? ["adapter-coverage-v" + key, "engine-host-gate", "engine-host-reference"] : null);
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
        var declaredPlan = allJobs.Concat(jobs).ToArray();
        Add("affected-inputs-self-test", () => CheckAllJobInputs.Validate(declaredPlan, catalog));
        CiCoverage.RequirePlan([.. allJobs, .. jobs], ciCovered);
        var scheduled = CiCoverage.Rewrite(CheckAllRuntimeDependencies(jobs, independentParity: true), ciCovered).Select(CheckAllScratchIsolation).Select(job => job with
        {
            EstimatedSeconds = CheckAllJobCost(job, costs),
            MeasuredSeconds = orderCosts.TryGetValue(job.Name, out var measured) ? measured : null,
            Timeout = CheckScheduler.JobTimeout(job, costs),
            Priority = job.Priority > 0 || !staticJobs.Any(item => item.Name == job.Name) ? job.Priority : 1
        }).ToList();
        var sensitiveLanes = CheckAffinity.AssignLanes(scheduled, layout, sensitiveWorkers.Workers);
        workerCopies.Prepare(Root, scheduled, runtimeManifest);
        var resultCache = new CheckAllResultCache(Root, output, runtimeManifest, api, nuget, dotnet, python, companionPython, options,
            JsonSerializer.Serialize(new { parallelism, shards, stabilityParallelism, lanes = sensitiveLanes, available, stability = layout.Stability,
                sensitive = layout.Sensitive, general = layout.General }));
        // Merge evidence is either fresh execution or the same job identity from
        // the latest passed nightly. The developer result cache is not merge evidence.
        scheduled = scheduled.Select(job =>
        {
            if (job.ResultInputs is not { } spec) return job;
            try { return job with { ResultInputs = resultCache.Prepare(spec) }; }
            catch (Exception ex) when (CheckAllNightlyCoverage.InputError(ex))
            {
                affected?.RequireFull("Cannot derive suite identity closure: " + job.Name + ": " + ex.Message);
                return job with { ResultInputs = null, ResultCachePolicy = "execute: identity closure unavailable" };
            }
        }).ToList();
        affected?.Map(scheduled, catalog);
        if (checkProfile == "merge")
        {
            nightlyOnly.AddRange(scheduled.Where(job => CheckAllJobInputs.NightlyOnly(job.Name)).Select(job => job.Name));
            scheduled.RemoveAll(job => CheckAllJobInputs.NightlyOnly(job.Name));
        }
        if (affected != null) scheduled = affected.QuickPlan(scheduled, catalog).ToList();
        var plan = scheduled.Select(job =>
        {
            var declaration = CheckAllJobInputs.Find(job, catalog);
            if (options.Has("Quick") || job.CiCoverage != null || job.ResultInputs?.AlwaysRun != null || declaration == null || declaration.Aggregate || CheckAllJobInputs.AlwaysRun(job.Name)) return job;
            if (!recordIdentities && (affected == null || affected.Selected(job, declaration) || nightlyCoverage.NightlyId == null)) return job;
            var spec = job.ResultInputs ?? new CheckAllResultSpec(declaration.Driver.EndsWith(".py", StringComparison.Ordinal) || declaration.DefaultPython ? "python" : "job",
                JsonSerializer.Serialize(declaration));
            var artifacts = spec.Trx != null ? new[] { Path.GetRelativePath(output, Path.Combine(spec.Directory!, spec.Trx)).Replace('\\', '/') } : declaration.Outputs;
            return job with { NightlyCoverage = nightlyCoverage.Bind(job, () => resultCache.JobIdentity(job, spec, declaration),
                affected != null && !affected.Quick && !affected.Selected(job, declaration), artifacts),
                ResultCachePolicy = "execute or byte-identical latest passed nightly (including sensitive jobs)" };
        }).ToArray();
        CheckAllInputClosure.WriteInventory(Root, plan, output);
        var lanes = new Dictionary<string, int>(sensitiveLanes) { ["general"] = parallelism, ["stability"] = stabilityParallelism };
        allJobs.AddRange(plan);
        var priorities = CheckScheduler.CriticalCosts(allJobs);
        File.WriteAllText(Path.Combine(output, "cpu-plan.json"), JsonSerializer.Serialize(new { availableMask = available.ToString("X"),
            cpuIsolation = layout.Isolation, cpuIsolationReason = layout.Reason,
            stabilityMask = layout.Stability.ToString("X"), sensitiveMask = layout.Sensitive.ToString("X"), generalMask = layout.General.ToString("X"), stabilityCores, generalCores,
            physicalCores = CheckAffinity.PhysicalCores().Select(core => new { mask = core.Mask.ToString("X"), efficiencyClass = core.Efficiency }),
            buildParallelism, stabilityParallelism, sensitiveParallelism = sensitiveLanes.Count, requestedSensitiveParallelism = sensitiveWorkers.RequestedWorkers,
            sensitiveParallelismReason = sensitiveWorkers.Reason, sensitiveCores = System.Numerics.BitOperations.PopCount(layout.Sensitive), sensitiveMinWorkerThreads = CheckAffinity.BaselineWorkers, generalParallelism = parallelism, packing, companionPython,
            profile = checkProfile,
            checks = allJobs.Select(job => new { job.Name, profiles = CheckAllProfiles.Profiles(job.Name, derivations), job.Lane, job.SensitiveGroup, job.Slots, job.Exclusive, job.GlobalExclusive, job.Priority, affinityMask = job.Affinity?.ToString("X"), job.EstimatedSeconds, job.MeasuredSeconds, job.SchedulingSeconds,
                cpuDemand = packing.Cpu(job), memoryBytes = packing.Memory(job), timeoutSeconds = job.Timeout.TotalSeconds, criticalSeconds = priorities[job.Name], job.Dependencies,
                runtimeManifestDependency = job.Name == "suite-output-parity" ? "build-once (comparison phase only)" : null,
                resultCache = job.ResultCachePolicy ?? "always rerun: generated evidence, mutable machine inputs or aggregate gate" }) }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        planned.AddRange(plan.Select(job => job.Name));
        timings.AddRange(CheckScheduler.Run(plan, parallelism, Path.Combine(output, "logs"),
            lanes, timings.ToArray(), packing, parityTask == null ? null : new Dictionary<Task<CheckTiming>, CheckJob> { [parityTask] = plan.Single(job => job.Name == "suite-output-parity") }));
        return Finish();

        int Finish()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            foreach (var row in timings.Where(row => row.Status == "passed")) costs[row.Name] = Math.Max(costs.GetValueOrDefault(row.Name), row.Seconds);
            File.WriteAllText(history, JsonSerializer.Serialize(costs, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            foreach (var row in timings.Where(row => row.Status == "passed" && row.Seconds > 0)) orderCosts[row.Name] = row.Seconds;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(history)!, "check-order-costs.json"), JsonSerializer.Serialize(orderCosts, new JsonSerializerOptions { WriteIndented = true }) + "\n");
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
            affected?.Write(output, timings, nightlyOnly, nightlyCoverage?.NightlyId);
            nightlyCoverage?.Save();
            var report = new { schemaVersion = 1, profile = checkProfile, quick = options.Has("Quick"), affected = options.Has("Affected"),
                coverage = new { executed = timings.Count(row => row.ReusedRunId == null), reused = timings.Count(row => row.ReusedRunId != null),
                    nightlyRunId = nightlyCoverage?.NightlyId, nightlyOnly = nightlyOnly.Count,
                    complete = !options.Has("Quick") && !options.Has("BuildOnly") && planned.All(name => timings.Any(row => row.Name == name && CheckTiming.Passed(row))) },
                machineCores = Environment.ProcessorCount, cpuIsolation = layout.Isolation, cpuIsolationReason = layout.Reason,
                parallelism, shards, sensitiveParallelism = sensitiveWorkers.Workers, requestedSensitiveParallelism = sensitiveWorkers.RequestedWorkers,
                sensitiveParallelismReason = sensitiveWorkers.Reason, cold = options.Has("Cold"), clean = options.Has("Clean"),
                wallSeconds = clock.Elapsed.TotalSeconds, underThirtyMinutes = clock.Elapsed.TotalSeconds < 1800, buildOnly = options.Has("BuildOnly"), nativeTiaExecuted = false,
                passed = timings.Count(CheckTiming.Passed), reused = timings.Count(row => row.Status == "passed (reused)"),
                ciCovered = timings.Count(row => row.Status == "covered by CI"),
                failed = timings.Count(row => !CheckTiming.Passed(row)), drift, performance, checks = timings };
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            WriteTimingComparison(output, timings, options.Get("BaselineReport"), layout);
            File.AppendAllText(Path.Combine(output, "comparison.md"), $"\nSensitive workers: {sensitiveWorkers.Workers} configured / {sensitiveWorkers.RequestedWorkers} requested. {sensitiveWorkers.Reason}\n");
            File.AppendAllText(Path.Combine(output, "comparison.md"), $"\nDependency critical path ({performance.CriticalPathSeconds:F3}s of job execution): {string.Join(" -> ", performance.CriticalPath)}\nAverage job concurrency: {performance.AverageConcurrency:F3}; peak: {performance.PeakConcurrency}. Includes process waits and reused-evidence lookups, not CPU utilization.\n");
            var exitCode = report.failed == 0 && report.underThirtyMinutes ? 0 : 1;
            string evidencePath;
            try { evidencePath = ReleaseEvidence.Write(releaseEvidence, output, planned, timings, exitCode, report.wallSeconds, report.underThirtyMinutes, unexpected, DateTimeOffset.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { evidencePath = "not written (" + ex.Message + ")"; }
            // The nightly runner publishes only after the complete outer command
            // exits within its deadline, including checkout and runner startup.
            if (recordIdentities && releaseEvidence["source"]?["cleanAtStart"]?.GetValue<bool>() == true && Environment.GetEnvironmentVariable("TIA_MCP_NIGHTLY_MANAGED") != "1")
                NightlyRuns.Complete(Root, output, (string?)releaseEvidence["source"]?["commit"] ?? ReleaseEvidence.GitText(Root, "git", "rev-parse", "HEAD"), exitCode, report.wallSeconds);
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

    // Ecosystem checks delete their scratch tree before the harness exits. Keep
    // repository readers out until that strict cleanup has completed.
    internal static CheckJob CheckAllScratchIsolation(CheckJob job) => job.Name is "harness-v20-test-ecosystem-assembly-plc" or "harness-v21-test-ecosystem-assembly-plc"
        ? job with { GlobalExclusive = true } : job;

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
