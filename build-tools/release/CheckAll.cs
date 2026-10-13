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
        if (options.Has("Clean") && Environment.GetEnvironmentVariable("TIA_MCP_CLEAN_CHECKOUT") == null) return CheckAllSnapshot(options);
        var worktree = Path.GetFullPath(options.Get("Worktree", Root));
        if (!string.Equals(worktree.TrimEnd('\\', '/'), Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            var result = ProcessRunner.Run(Dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", .. options.Arguments()], worktree, forwardOutput: true);
            return result.ExitCode;
        }
        int parallelism = FastSuites.CpuParallelism(Root, options);
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
        int stabilityCores = FastSuites.Positive(options, "StabilityCores", Math.Min(8, Math.Max(1, Environment.ProcessorCount / 3)));
        var masks = CheckAffinity.SplitForMachine(available, stabilityCores);
        int generalCores = System.Numerics.BitOperations.PopCount(masks.General);
        parallelism = Math.Min(parallelism, generalCores);
        int stabilityParallelism = Math.Min(FastSuites.Positive(options, "StabilityParallelism", 4), Math.Min(4, stabilityCores));
        var clock = Stopwatch.StartNew();
        using var load = CheckLoad.Start(Path.Combine(output, "process-load.json"));
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        var before = TrackedBytes();
        var timings = new List<CheckTiming>();
        var jobs = new List<CheckJob>();
        if (options.Get("BaselineReport") is { } timingBaseline)
            SuiteCosts.ImportReport(Path.GetDirectoryName(Path.GetFullPath(timingBaseline))!, Path.Combine(Root, "bin-build/check-timings"));
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
            return ProcessRunner.Run(executable, args, Root, environment);
        }
        void Add(string name, Func<CommandResult> run, string[]? depends = null, bool exclusive = false) => jobs.Add(new(name, run, depends ?? [], Exclusive: exclusive));
        void Wave()
        {
            if (jobs.Count == 0) return;
            timings.AddRange(CheckScheduler.Run(jobs.ToArray(), parallelism, Path.Combine(output, "logs")));
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
        Add("build-once", () => CheckAllCachedBuild(options, builds, output, dotnet, api, nuget,
            FastSuites.Positive(options, "BuildParallelism", parallelism)));
        Wave();
        if (!CheckAllPrerequisitesPassed(timings)) return Finish();
        AnalyzerWarnings.RememberDirectory(Root, Path.Combine(output, "build/analyzer-warnings"));
        Add("analyzer-warnings", () => Tool("analyzer-warnings", "check-analyzer-warnings", "-Check", "-Complete",
            "-DiagnosticsDirectory", Path.Combine(output, "build/analyzer-warnings")));
        Wave();
        if (options.Has("BuildOnly")) return Finish();
        Add("suite-output-parity", () => CheckAllLayout.Verify(Root, builds, dotnet, nuget, api, Path.Combine(output, "suite-output-parity")));
        Wave();
        string host = Path.Combine(Root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe");
        string harness = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe");
        string fixture(string release) => Path.Combine(Root, $"src/Engine/bin/check-all/TiaMcp.Engine.V{release}/sdk-{release}/Release/net48/TiaMcp.Engine.V{release}.exe");
        string engine(string release) => Path.Combine(Root, "src/Engine", release == "20" ? "bin-v20" : "bin", "Release/net48/TiaMcp.Engine.V" + release + ".exe");
        string fixtureCatalog(string release) => Path.Combine(Path.GetDirectoryName(fixture(release))!, "tool-catalog.json");
        foreach (var release in new[] { "20", "21" })
        {
            var key = release;
            Add("fixture-catalog-v" + key, () => Isolated("fixture-catalog-v" + key, fixture(key), ["--bundle-root", Root, "--write-tool-catalog", fixtureCatalog(key)]));
            Add("production-catalog-v" + key, () => Isolated("production-catalog-v" + key, engine(key), ["--bundle-root", Root, "--write-tool-catalog", Path.Combine(Path.GetDirectoryName(engine(key))!, "tool-catalog.json")]));
        }
        foreach (var key in new[] { "20", "21" })
        {
            // Keep the transport helper's historical SDK-root layout; copies are
            // private artifacts, never production binaries or manifest evidence.
            var release = key;
            Add("sdk-fixture-copy-v" + key, () =>
            {
                var destination = Path.Combine(output, "sdk-fixtures", release); Directory.CreateDirectory(destination);
                foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(fixture(release))!)) File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), true);
                return new(0, "SDK fixture copied\n", "");
            }, ["fixture-catalog-v" + key]);
        }
        // Stability shares wall time, but no CPUs, with the general lane. The
        // Windows job includes the client, both hosts, workers and descendants.
        foreach (var release in new[] { "20", "21" })
        foreach (var concurrency in new[] { "8", "16" })
        foreach (var transport in new[] { "stdio", "http" })
        foreach (var profile in new[] { "full", "lite" })
        {
            var name = $"stability-v{release}-c{concurrency}-{transport}-{profile}";
            var key = release; var c = concurrency; var t = transport; var p = profile;
            Add(name, () =>
            {
                var directory = Path.Combine(output, name);
                var result = Python(name, "scripts/checks/Test-LocalStability.py", "--exe", engine(key), "--major", key, "--host-harness", harness,
                    "--public-api", ResolveReleaseApi(null, api, int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)), "--engine-worker", fixture(key), "--engine-catalog", fixtureCatalog(key),
                    "--rounds", "50", "--concurrency", c, "--transports", t, "--profiles", p, "--output", directory);
                if (result.ExitCode == 0)
                {
                    var evidence = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "result.json")))!;
                    if ((string?)evidence["status"] != "passed" || (int?)evidence["rounds"] != 50 || evidence["runs"]!.AsArray().Count != 1 ||
                        (string?)evidence["runtimeSha256"] != ReleaseRecords.HashFile(engine(key)) || (string?)evidence["hostSha256"] != ReleaseRecords.HashFile(host) ||
                        (string?)evidence["sdkWorkerSha256"] != ReleaseRecords.HashFile(fixture(key)))
                        throw new ReleaseException("Stability evidence mismatch");
                }
                return result;
            }, ["fixture-catalog-v" + key, "production-catalog-v" + key]);
            jobs[^1] = jobs[^1] with { Lane = "stability", Affinity = masks.Stability };
        }
        foreach (var (name, node) in catalog)
        {
            var suite = node!.AsObject();
            string? sdk = name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true ? api : null;
            FastSuites.AddJobs(jobs, Root, name, suite, Path.Combine(output, "suites"), dotnet, suite["minimumPassed"]!.GetValue<int>() >= 100 ? shards : 1, sdk, [],
                options.Get("SequentialBaselineDirectory") is { } baseline ? Path.Combine(baseline, name + ".trx") : null, options.Has("ProveEquivalence"));
        }
        foreach (var mode in new[] { "false", "true" })
        foreach (var project in new[] { "Core", "Gui" })
        {
            var name = $"studio-{project.ToLowerInvariant()}-{mode}"; var selectedMode = mode; var selectedProject = project;
            Add(name, () => Isolated(name, dotnet, ["test", $"tests/Studio/TiaOpenness.{selectedProject}.Tests/TiaOpenness.{selectedProject}.Tests.csproj", "-c", "Release", "--no-build", "--no-restore",
                "-p:TiaSharedAdapterPaths=" + selectedMode,
                "--disable-build-servers", "-m:1", "-nodeReuse:false", "--logger", "trx;LogFileName=" + name + ".trx", "--results-directory", Path.Combine(output, "studio")]), exclusive: true);
        }
        AddCheckAllStaticJobs(Add, Tool, Python, Isolated, output, api, nuget, parallelism, shards, dotnet);
        jobs.RemoveAll(job => job.Name == "preflight");
        AddCheckAllPreflightJobs(jobs, catalog, output, dotnet, shards);
        foreach (var release in CatalogJson.Keys)
        {
            var key = release;
            foreach (var kind in new[] { "contracts", "responses" })
            {
                var category = kind; var name = category + "-v" + key;
                Add(name, () =>
                {
                    var script = "scripts/checks/Snapshot-Tool" + (category == "contracts" ? "Contracts" : "Responses") + ".py";
                    var args = new List<string> { "capture", "--repo-root", Root, "--public-api-root", api, "--releases", key, "--exe", key + "=" + host, "--output", Path.Combine(output, category) };
                    if (key is "20" or "21") args.AddRange(["--engine-host", host, "--engine-worker", key + "=" + fixture(key), "--engine-catalog", key + "=" + fixtureCatalog(key)]);
                    if (category == "responses") args.AddRange(["--temp-root", FastSuites.TemporaryDirectory()]);
                    var result = Python(name, script, args.ToArray());
                    if (result.ExitCode != 0) return result;
                    // Both the existing semantic comparison and an exact-byte check.
                    result = Python(name + "-compare", script, "compare", "--baseline", Path.Combine(Root, "manifest/contracts/v4", category == "contracts" ? "baseline" : "responses"), "--current", Path.Combine(output, category), "--releases", key);
                    var old = Path.Combine(Root, "manifest/contracts/v4", category == "contracts" ? "baseline" : "responses", key + ".json");
                    var current = Path.Combine(output, category, key + ".json");
                    if (result.ExitCode == 0 && !File.ReadAllBytes(old).SequenceEqual(File.ReadAllBytes(current)))
                        return result with { ExitCode = 1, StandardError = "Capture bytes differ: " + key };
                    return result;
                }, key is "20" or "21" ? ["fixture-catalog-v" + key] : null);
            }
            var usageName = "transport-usage-v" + key;
            Add(usageName, () => Python(usageName, "scripts/checks/Test-FoundationTransport.py", "--fixture", Path.Combine(Root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe"),
                "--host-exe", host, "--releases", key, "--sdk-fixture-root", Path.Combine(output, "sdk-fixtures"), "--temp-root", FastSuites.TemporaryDirectory(), "--output", Path.Combine(output, usageName)), ["sdk-fixture-copy-v20", "sdk-fixture-copy-v21"]);
            var sourceUsageName = "usage-v" + key;
            Add(sourceUsageName, () => Python(sourceUsageName, "scripts/checks/Test-ToolUsage.py", "--foundation-host", host, "--engine-host", host,
                "--engine-v20", fixture("20"), "--engine-v21", fixture("21"), "--harness", harness, "--public-api-root", api,
                "--releases", key, "--evidence", Path.Combine(Root, "bin-build/u", Guid.NewGuid().ToString("N")), "--coverage-output", Path.Combine(output, sourceUsageName + ".json")), ["fixture-catalog-v20", "fixture-catalog-v21"]);
        }
        foreach (var release in new[] { "20", "21" })
        {
            var key = release;
            var targetApi = ResolveReleaseApi(null, api, int.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
            foreach (var mode in CheckAllHarnessModes(key))
            {
                var selected = mode; var name = "harness-v" + key + "-" + (mode.Length == 0 ? "http" : mode);
                Add(name, () =>
                {
                    var copy = Path.Combine(Root, "bin-build/h", Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(copy);
                    foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(engine(key))!)) File.Copy(path, Path.Combine(copy, Path.GetFileName(path)), true);
                    var copiedEngine = Path.Combine(copy, Path.GetFileName(engine(key)));
                    var result = CheckAllHarness(Isolated(name, harness, [copiedEngine, .. HarnessArguments(selected, key, targetApi)]), selected, key);
                    if (result.ExitCode == 0 && selected == "native-diagnostics-only") ValidateCheckAllJit(result.StandardOutput,
                        Path.Combine(output, "native-coverage-v" + key + ".json"), Path.Combine(output, "adapter-coverage-v" + key + ".json"));
                    return result;
                }, selected == "native-diagnostics-only" ? ["native-coverage-v" + key, "adapter-coverage-v" + key] : null);
            }
            var campaignName = "campaign-inputs-v" + key;
            Add(campaignName, () => Python(campaignName, "scripts/checks/Test-CampaignInputs.py", "--exe", engine(key), "--major", key,
                "--public-api", targetApi, "--host-harness", harness, "--output", Path.Combine(output, campaignName)), ["production-catalog-v" + key]);
            var negativeName = "candidate-negative-v" + key;
            Add(negativeName, () =>
            {
                var result = Isolated(negativeName, harness, [engine(key), "device-candidate-only", targetApi, "safe-v4"]);
                return result.ExitCode != 0 && (result.StandardOutput + result.StandardError).Contains("CreateDevice selects exactly the expected method", StringComparison.Ordinal)
                    ? new(0, result.StandardOutput + result.StandardError + "PASS wrong candidate policy selection exits nonzero\n", "")
                    : new(1, result.StandardOutput, result.StandardError + "Wrong candidate policy was not rejected at method selection.\n");
            });
            foreach (var mode in CheckAllCandidateModes())
            foreach (var policy in new[] { "current", "safe-v4" })
            {
                var selectedMode = mode; var selectedPolicy = policy;
                var name = $"candidate-v{key}-{mode}-{policy}";
                Add(name, () =>
                {
                    var unit = CheckAllCandidateBuild(key, selectedMode);
                    var source = selectedPolicy == "current" ? engine(key)
                        : Path.Combine(Root, "src/Engine/bin/check-all", Path.GetFileNameWithoutExtension(unit.Project), unit.Variant, $"Release/net48/TiaMcp.Engine.V{key}.exe");
                    return Isolated(name, harness, [source, selectedMode + "-candidate-only", targetApi, selectedPolicy]);
                });
            }
            foreach (var type in new[] { "native", "adapter" })
            {
                var name = type + "-coverage-v" + key; var selected = type;
                Add(name, () => Isolated(name, dotnet, [Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"), "verify",
                    selected == "native" ? engine(key) : Path.Combine(Path.GetDirectoryName(engine(key))!, "TiaMcp.Adapter." + key + ".dll"), Path.Combine(output, name + ".json")]));
            }
            var approvalName = "approval-v" + key;
            Add(approvalName, () =>
            {
                var result = Python(approvalName, "scripts/checks/Test-ReleaseApprovalGate.py", "--product", "engine", "--major", key, "--exe", engine(key),
                    "--portal-root", targetApi, "--public-api", targetApi, "--host-harness", harness, "--engine-worker", fixture(key), "--engine-catalog", fixtureCatalog(key),
                    "--temp-root", FastSuites.TemporaryDirectory(), "--output", Path.Combine(output, approvalName));
                if (result.ExitCode == 0) ValidateCheckAllApproval(Path.Combine(output, approvalName, "result.json"));
                return result;
            }, ["fixture-catalog-v" + key]);
            var resourceName = "resources-v" + key;
            Add(resourceName, () => Python(resourceName, "scripts/checks/Test-ResourceDiscovery.py", "--exe", host, "--portal-root", targetApi, "--major", key,
                "--engine-worker", fixture(key), "--engine-catalog", fixtureCatalog(key), "--public-api", targetApi, "--usage-output", Path.Combine(output, resourceName + ".json")), ["fixture-catalog-v" + key]);
            var ecosystemName = "ecosystem-v" + key;
            Add(ecosystemName, () =>
            {
                var evidenceDirectory = Path.Combine(output, ecosystemName);
                var result = Python(ecosystemName, "scripts/checks/Test-V21Ecosystem.py", "--exe", engine(key), "--major", key, "--host-harness", harness, "--public-api", targetApi,
                    "--schema-root", Path.Combine(Path.GetDirectoryName(ResolveReleaseApi(null, api, 21))!, "Schemas"), "--engine-worker", fixture(key), "--engine-catalog", fixtureCatalog(key), "--output", evidenceDirectory);
                if (result.ExitCode == 0)
                {
                    var evidencePath = Directory.EnumerateFiles(evidenceDirectory, "result.json", SearchOption.AllDirectories).Single();
                    using var doc = JsonDocument.Parse(File.ReadAllText(evidencePath)); var evidence = doc.RootElement;
                    CheckReleaseCount("v21Ecosystem", GetJsonInt(evidence, "checks"), "Ecosystem checks incomplete");
                    if (GetJsonString(evidence, "status") != "passed" || GetJsonBool(evidence, "selfTestOnly") || GetJsonBool(evidence, "nativeTiaExecuted") || GetJsonString(evidence, "runtimeSha256") != ReleaseRecords.HashFile(engine(key)))
                        throw new ReleaseException("Ecosystem evidence mismatch");
                }
                return result;
            }, ["fixture-catalog-v" + key]);
        }
        Add("usage-audit", () =>
        {
            var receipts = new JsonArray();
            foreach (var key in CatalogJson.Keys)
            {
                var path = Path.Combine(output, "usage-v" + key + ".json");
                var captured = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
                var receipt = captured.Single();
                receipts.Add(receipt!.DeepClone());
            }
            var records = Path.Combine(output, "usage-receipts.json"); File.WriteAllText(records, receipts.ToJsonString());
            var coverage = Isolated("usage-coverage", python, ["-X", "utf8", "-c",
                "import importlib.util, json, sys\nfrom pathlib import Path\nscript = Path(sys.argv[1])\nsys.path.insert(0, str(script.parent))\nspec = importlib.util.spec_from_file_location('usage_coverage', script)\nmodule = importlib.util.module_from_spec(spec)\nspec.loader.exec_module(module)\nmodule.coverage(json.loads(Path(sys.argv[2]).read_text('utf-8')), Path(sys.argv[3]))\n",
                Path.Combine(Root, "scripts/checks/Test-ToolUsage.py"), records, Path.Combine(output, "tool-usage-coverage.json")]);
            if (coverage.ExitCode != 0) return coverage;
            return Tool("usage-audit", "audit-tool-usage", "-Records", records);
        }, CatalogJson.Keys.Select(key => "usage-v" + key).ToArray());
        Add("generate-release-migration", () => Tool("generate-release-migration", "generate-release-migration"));
        var history = Path.Combine(Root, "bin-build/check-timings/check-costs.json");
        var costs = File.Exists(history) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(history))! : new Dictionary<string, double>();
        var baselineReport = options.Get("BaselineReport") ?? (File.Exists(Path.Combine(Root, "bin-build/reviewer-run1/report.json")) ? Path.Combine(Root, "bin-build/reviewer-run1/report.json") : null);
        if (costs.Count == 0 && baselineReport != null)
            foreach (var row in JsonNode.Parse(File.ReadAllText(baselineReport))!["checks"]!.AsArray()) costs[(string)row!["Name"]!] = (double)row["Seconds"]!;
        var plan = jobs.Select(job => job with
        {
            Affinity = job.Affinity ?? masks.General,
            EstimatedSeconds = CheckAllJobCost(job, costs),
            Dependencies = job.Name == "migration-drift" || job.Name == "preflight-" + PreflightChecks().FindIndex(check => check.Name == "release migration tables generator") ? ["generate-release-migration"] : job.Dependencies
        }).ToArray();
        var priorities = CheckScheduler.CriticalCosts(plan);
        File.WriteAllText(Path.Combine(output, "cpu-plan.json"), JsonSerializer.Serialize(new { availableMask = available.ToString("X"),
            stabilityMask = masks.Stability.ToString("X"), generalMask = masks.General.ToString("X"), stabilityCores, generalCores,
            physicalCores = CheckAffinity.PhysicalCores().Select(core => new { mask = core.Mask.ToString("X"), efficiencyClass = core.Efficiency }),
            stabilityParallelism, generalParallelism = parallelism, companionPython,
            checks = plan.Select(job => new { job.Name, job.Lane, job.Slots, job.Exclusive, job.EstimatedSeconds, criticalSeconds = priorities[job.Name], job.Dependencies }) }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        timings.AddRange(CheckScheduler.Run(plan, parallelism, Path.Combine(output, "logs"),
            new Dictionary<string, int> { ["general"] = parallelism, ["stability"] = stabilityParallelism }));
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        foreach (var row in timings.Where(row => row.Status == "passed")) costs[row.Name] = row.Seconds;
        File.WriteAllText(history, JsonSerializer.Serialize(costs, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return Finish();

        int Finish()
        {
            var after = TrackedBytes();
            var drift = after.Where(pair => !before.TryGetValue(pair.Key, out var hash) || pair.Value != hash).Select(pair => pair.Key)
                .Concat(before.Keys.Except(after.Keys, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            // These two generators own generation timestamps. Only the timestamp
            // fields are ignored; any other byte change remains a failed drift gate.
            var unexpected = drift.Where(path => !TimestampOnly(path, before)).ToArray();
            timings.Add(new("tracked-tree-drift", unexpected.Length == 0 ? "passed" : "failed", unexpected.Length == 0 ? 0 : 1, 0, string.Join(", ", unexpected)));
            var report = new { schemaVersion = 1, machineCores = Environment.ProcessorCount, parallelism, shards, cold = options.Has("Cold"), clean = options.Has("Clean"),
                wallSeconds = clock.Elapsed.TotalSeconds, underFiveMinutes = clock.Elapsed.TotalSeconds < 300, buildOnly = options.Has("BuildOnly"), nativeTiaExecuted = false,
                passed = timings.Count(row => row.Status == "passed"), failed = timings.Count(row => row.Status != "passed"), drift, checks = timings };
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            WriteTimingComparison(output, timings, options.Get("BaselineReport"));
            Console.WriteLine($"check-all: {report.passed} passed; {report.failed} failed/blocked; {report.wallSeconds:F3}s; report: {output}");
            return report.failed == 0 && report.underFiveMinutes ? 0 : 1;
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

    internal static bool CheckAllPrerequisitesPassed(IEnumerable<CheckTiming> timings) => timings.All(row => row.Status == "passed");

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

    private static void WriteTimingComparison(string output, List<CheckTiming> timings, string? baseline)
    {
        var old = baseline == null ? new Dictionary<string, double> { ["foundation"] = 656, ["foundation-api"] = 666, ["release-tool"] = 144, ["engine-host"] = 141 } : JsonNode.Parse(File.ReadAllText(baseline))!["checks"]!.AsArray()
            .ToDictionary(row => (string)row!["Name"]!, row => (double)row!["Seconds"]!);
        var lines = new List<string> { "Suite times span the first shard start through the final merge, including queue time. Raw process timings remain in report.json.", "", "| Check | Old seconds | New seconds | Result |", "|---|---:|---:|---|" };
        foreach (var group in timings.GroupBy(row => Regex.Replace(row.Name, "-(shard-[0-9]+|gate)$", "")))
        {
            var rows = group.ToArray();
            var seconds = rows.Length == 1 || rows.Any(row => row.StartedAtUtc == default) ? rows.Max(row => row.Seconds)
                : (rows.Max(row => row.StartedAtUtc.AddSeconds(row.Seconds)) - rows.Min(row => row.StartedAtUtc)).TotalSeconds;
            lines.Add($"| {group.Key} | {(old.TryGetValue(group.Key, out var value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "NOT MEASURED")} | {seconds.ToString("F3", CultureInfo.InvariantCulture)} | {(rows.All(row => row.Status == "passed") ? "passed" : "failed/blocked")} |");
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
        units.Add(CheckAllBuild.Suite(CheckAllPreflightSuite(catalog)));
        foreach (var key in new[] { "20", "21" }) units.Add(new($"src/Engine/TiaMcp.Engine.V{key}.csproj", "sdk-" + key, ["-p:TiaMcpEngineWorkerSdkFixture=true"]));
        foreach (var key in new[] { "20", "21" })
        foreach (var mode in CheckAllCandidateModes()) units.Add(CheckAllCandidateBuild(key, mode));
        foreach (var mode in new[] { "false", "true" })
        foreach (var project in new[] { "src/Studio/Bridge/TiaOpenness.Bridge.csproj", "src/Studio/Gui/TiaOpenness.Gui.csproj", "tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj", "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj" })
            units.Add(new(project, "", ["-p:TiaSharedAdapterPaths=" + mode]));
        return CheckAllBuild.WithTestFixtures(units).ToArray();
    }

}
