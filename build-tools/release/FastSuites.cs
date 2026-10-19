using System;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class FastSuites
{
    private static readonly string SystemTemp = System.Environment.GetEnvironmentVariable("TIA_MCP_CHECK_SYSTEM_TEMP_ROOT") ?? Path.GetTempPath();
    internal static int Positive(Options options, string name, int fallback)
    {
        if (options.Get(name) is not { } text) return fallback;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1) throw new ReleaseException("-" + name + " must be a positive integer", 64);
        return value;
    }

    internal static int CpuParallelism(string root, Options options)
    {
        if (options.Has("CpuParallelism")) return Positive(options, "CpuParallelism", 1);
        if (options.Has("MaxParallelism")) return Positive(options, "MaxParallelism", System.Environment.ProcessorCount);
        var path = Path.Combine(root, "bin-build/check-timings/cpu-budget.json");
        int measured = System.Environment.ProcessorCount;
        if (File.Exists(path))
        {
            var profile = JsonNode.Parse(File.ReadAllText(path))!;
            if ((bool?)profile["representativeIdleMeasurement"] == true && (int?)profile["logicalCores"] == System.Environment.ProcessorCount && (int?)profile["parallelism"] is > 0)
                measured = (int)profile["parallelism"]!;
        }
        return Math.Min(measured, Positive(options, "MaxParallelism", measured));
    }

    internal static int GeneralCapacity(int requested, int cores, bool explicitLimit) =>
        explicitLimit ? requested : Math.Min(requested, cores);

    internal static int SensitiveCoreBudget(ulong available, IReadOnlyList<CheckJob> jobs)
    {
        int groups = jobs.Where(job => job.Exclusive).Select(job => job.SensitiveGroup ?? job.Name).Distinct(StringComparer.Ordinal).Count();
        return groups == 0 ? 0 : Math.Min(CheckAffinity.DefaultSensitiveCores(available), 4 * Math.Min(2, groups));
    }

    internal static string TemporaryDirectory() => Path.Combine(SystemTemp, "p8s", Guid.NewGuid().ToString("N")[..8]);

    internal static Dictionary<string, string?> EnvironmentFor(string directory, string? api = null)
    {
        Directory.CreateDirectory(directory);
        // Some fixtures deliberately enforce Windows path bounds. Long report
        // names must not consume the application's allowed destination length.
        // Git fixtures and installed-delivery guards require no checkout ancestor.
        // Data lives below TEMP so the same isolation guard also works in -Clean.
        var isolation = Path.Combine(System.Environment.GetEnvironmentVariable("TIA_MCP_CHECK_ISOLATION_ROOT") ?? Path.Combine(SystemTemp, "p8t"), Guid.NewGuid().ToString("N")[..8]);
        foreach (var child in new[] { "data", "local", "roaming" }) Directory.CreateDirectory(Path.Combine(isolation, child));
        File.WriteAllText(Path.Combine(directory, "isolation.txt"), isolation + "\n");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = isolation, ["TMP"] = isolation, ["TMPDIR"] = isolation,
            ["TIA_MCP_DATA_DIRECTORY"] = Path.Combine(isolation, "data"), ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(isolation, "data", "diagnostics"),
            ["LOCALAPPDATA"] = Path.Combine(isolation, "local"), ["APPDATA"] = Path.Combine(isolation, "roaming"),
            ["TIA_MCP_TEST_PIPE_SCOPE"] = Guid.NewGuid().ToString("N"), ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api,
            ["TIA_MCP_TEST_PRODUCT_ROOT"] = null, ["TIA_MCP_TEST_SHARD_COUNT"] = null, ["TIA_MCP_TEST_SHARD_INDEX"] = null,
            ["TIA_MCP_TEST_SENSITIVE_SHARDS"] = null, ["TIA_MCP_TEST_SHARD_ASSEMBLY"] = null, ["TIA_MCP_TEST_SHARD_PLAN"] = null, ["TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD"] = null,
            ["NuGetAudit"] = "false", ["TIA_MCP_CHECK_SYSTEM_TEMP_ROOT"] = SystemTemp
        };
    }

    internal static void AddJobs(List<CheckJob> jobs, string root, string name, JsonObject suite, string output, string dotnet,
        int shards, string? api, string[] dependencies, string? baseline = null, bool prove = false, string[]? extra = null, string? history = null, string? jobPrefix = null, string? manifest = null)
    {
        var unit = CheckAllBuild.Suite(suite);
        var directory = Path.Combine(output, name);
        history ??= Path.Combine(root, "bin-build/check-timings");
        bool reserveSensitive = TimeSensitiveTests.ReserveShard(unit.Project);
        int sensitiveShards = reserveSensitive ? TimeSensitiveTests.ReservedShardCount(unit.Project) : 0;
        var plan = SuiteCosts.Prepare(name, directory, history, baseline, shards, reserveSensitive, sensitiveShards); shards = plan.Costs.Length;
        // Costs set deadlines only when the history covers at least the registered minimum number of cases.
        bool measuredPlan = plan.MeasuredCases >= (suite["minimumPassed"]?.GetValue<int>() ?? int.MaxValue);
        var paths = Enumerable.Range(0, shards).Select(i => Path.Combine(directory, "shard-" + i, name + ".trx")).ToArray();
        var sequential = baseline;
        if (prove)
        {
            sequential = Path.Combine(directory, "sequential", name + ".trx");
            jobs.Add(new(jobPrefix + name + "-sequential", () => RunShard(root, name, suite, Path.GetDirectoryName(sequential)!, dotnet, api, unit, 0, 1, extra, manifest: manifest), dependencies, Exclusive: true) {
                SensitiveGroup = unit.Project, EstimatedSeconds = measuredPlan ? plan.Costs.Sum() + 5 : ColdShardSeconds * shards });
        }
        var shardNames = new List<string>();
        for (int i = 0; i < shards; i++)
        {
            var index = i; var jobName = jobPrefix + name + "-shard-" + i; shardNames.Add(jobName);
            jobs.Add(new(jobName, () => RunShard(root, name, suite, Path.GetDirectoryName(paths[index])!, dotnet, api, unit, index, shards, extra, plan.Path, reserveSensitive, manifest, sensitiveShards: sensitiveShards), dependencies,
                Exclusive: reserveSensitive && index >= shards - sensitiveShards) { EstimatedSeconds = measuredPlan ? plan.Costs[index] + 5 : ColdShardSeconds,
                    SensitiveGroup = sensitiveShards > 1 && index == shards - 1 ? unit.Project + "#portal-lane" : unit.Project });
        }
        // Merge runs even after a failed test process, so partial totals/failures remain
        // reviewable. The caller schedules merges in a second wave without prerequisites.
        jobs.Add(new(jobPrefix + name + "-gate", () => { SuiteCosts.Record(name, directory, history); return SuiteSharding.Merge(name, suite, paths, output, sequential); }, shardNames.ToArray()) { AllowFailedDependencies = true });
    }

    // Without timing history a shard has no estimate; three times this allowance (one hour) bounds it instead of the
    // 300-second floor, which a cold 29,000-case suite in four shards exceeds on CI (P8-72 host/CI evidence 2026-10-19).
    internal const double ColdShardSeconds = 1200;

    internal static CommandResult RunShard(string root, string name, JsonObject suite, string directory, string dotnet, string? api,
        CheckBuild unit, int index, int count, string[]? extra = null, string? plan = null, bool reserveSensitive = false, string? manifest = null, SuiteProcess? process = null, int sensitiveShards = 1)
    {
        Directory.CreateDirectory(directory);
        var environment = EnvironmentFor(directory, api);
        if (unit.Project.StartsWith("tests/Studio/", StringComparison.Ordinal)) environment["TIA_MCP_BUNDLE_ROOT"] = root;
        environment["TIA_MCP_TEST_SHARD_COUNT"] = count.ToString(); environment["TIA_MCP_TEST_SHARD_INDEX"] = index.ToString();
        environment["TIA_MCP_TEST_SHARD_ASSEMBLY"] = Path.GetFileNameWithoutExtension(unit.Project);
        environment["TIA_MCP_TEST_SHARD_PLAN"] = plan;
        environment["TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD"] = reserveSensitive ? "1" : null;
        environment["TIA_MCP_TEST_SENSITIVE_SHARDS"] = reserveSensitive ? sensitiveShards.ToString() : null;
        environment["TIA_MCP_TEST_CASE_TIMINGS"] = Path.Combine(directory, "case-timings.tsv");
        File.Delete(environment["TIA_MCP_TEST_CASE_TIMINGS"]!);
        environment["TIA_MCP_TEST_PRODUCER_TIMINGS"] = Path.Combine(directory, "producer-timings.tsv");
        File.Delete(environment["TIA_MCP_TEST_PRODUCER_TIMINGS"]!);
        var trx = Path.Combine(directory, name + ".trx"); File.Delete(trx);
        var before = SuiteHang.Begin(directory, name);
        environment["TIA_MCP_TEST_PHASE_TIMINGS"] = Path.Combine(directory, "phase-timings.json");
        string? assembly;
        try { assembly = CheckAllBuild.TestAssembly(root, unit, manifest, name); }
        catch (ReleaseException ex) { return new(1, "", ex.Message + "\n"); }
        List<string> args;
        if (assembly != null && (extra == null || extra.Length == 0))
            args = VstestArguments(assembly, suite["arguments"]!.AsArray().Select(a => a!.GetValue<string>()), name, directory);
        else
        {
            args = ["test", Path.Combine(root, unit.Project), "-c", "Release", "--no-build", "--no-restore", "--nologo", "--disable-build-servers", "-m:1", "-nodeReuse:false"];
            args.AddRange(suite["arguments"]!.AsArray().Select(a => a!.GetValue<string>()));
            if (assembly != null) args.Add("-p:TargetPath=" + assembly);
            if (api != null) args.Add("-p:TiaPublicApiRoot=" + api);
            if (extra != null) args.AddRange(extra);
            args.AddRange(["--logger", "trx;LogFileName=" + name + ".trx", "--results-directory", directory]);
            args.AddRange(SuiteHang.TestArguments);
        }
        var result = SuiteHang.Complete(directory, name, before, (process ?? DotnetSuites.RunProcess)(dotnet, args, root, environment));
        if (!File.Exists(trx)) return result with { ExitCode = 1, StandardError = result.StandardError + "\nMissing shard TRX: " + trx };
        var errors = DotnetSuites.ReadTrx(trx).Errors;
        if (errors.Count > 0) return result with { ExitCode = 1, StandardError = result.StandardError + "\n" + string.Join("\n", errors) };
        return result;
    }

    internal static List<string> VstestArguments(string assembly, IEnumerable<string> arguments, string name, string directory)
    {
        var source = arguments.ToArray();
        var result = new List<string> { "vstest", assembly, "--logger:trx;LogFileName=" + name + ".trx", "--ResultsDirectory:" + directory };
        result.Add(SuiteHang.VstestArgument);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i].StartsWith("-p:", StringComparison.Ordinal)) continue; // Already represented by the exact built variant.
            if (source[i] == "--filter" && i + 1 < source.Length) result.Add("--TestCaseFilter:" + source[++i]);
            else throw new ReleaseException("Unsupported registered VSTest argument: " + source[i]);
        }
        return result;
    }

    internal static CommandResult Run(string root, Options options, JsonObject catalog)
    {
        var output = Path.GetFullPath(options.Get("ResultsDirectory", Path.Combine(root, "bin-build/test-results-fast")));
        var dotnet = options.Get("Dotnet", "dotnet");
        Directory.CreateDirectory(output);
        using var load = CheckLoad.Current == null ? CheckLoad.Start(Path.Combine(output, "process-load.json")) : null;
        int parallelism = CpuParallelism(root, options);
        int count = Positive(options, "Shards", 8);
        var names = options.All("Suite");
        var builds = CheckAllBuild.WithTestFixtures(names.Select(name => CheckAllBuild.Suite(catalog[name]!.AsObject()))).ToArray();
        var manifestPath = Path.GetFullPath(options.Get("RuntimeManifest", Path.Combine(output, "build/runtime-paths.tsv")));
        string? manifest = !options.Has("NoBuild") || options.Has("RuntimeManifest") || File.Exists(manifestPath) ? manifestPath : null;
        if (!options.Has("NoBuild"))
        {
            var build = CheckAllBuild.Run(root, builds, Path.Combine(output, "build"), dotnet,
                options.Get("PublicApiRoot") ?? Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT"), options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG"), Positive(options, "BuildParallelism", parallelism), false);
            if (build.ExitCode != 0)
            {
                File.WriteAllText(Path.Combine(output, "report.json"), System.Text.Json.JsonSerializer.Serialize(new
                { exitCode = build.ExitCode, build = "failed", error = build.StandardError, output = build.StandardOutput }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
                return build;
            }
            manifest = Path.Combine(output, "build/runtime-paths.tsv");
            ReleaseCommands.PrepareCheckAllRuntime(root, builds, manifest);
        }
        var jobs = new List<CheckJob>();
        foreach (var name in names)
        {
            var suite = catalog[name]!.AsObject();
            string? api = name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true
                ? options.Get("PublicApiRoot") ?? Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT") : null;
            AddJobs(jobs, root, name, suite, output, dotnet, suite["minimumPassed"]!.GetValue<int>() >= 100 ? count : 1, api, [],
                options.Get("SequentialBaselineDirectory") is { } baseline ? Path.Combine(baseline, name + ".trx") : null, options.Has("ProveEquivalence"), options.All("DotnetArg").ToArray(), manifest: manifest);
        }
        if (options.Has("SensitiveCores")) _ = Positive(options, "SensitiveCores", 1);
        if (options.Has("SensitiveParallelism")) _ = Positive(options, "SensitiveParallelism", 1);
        var lanes = new Dictionary<string, int> { ["general"] = parallelism };
        if (OperatingSystem.IsWindows() && jobs.Any(job => job.Exclusive))
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var available = unchecked((ulong)current.ProcessorAffinity.ToInt64());
            var masks = CheckAffinity.SplitSensitive(available, Positive(options, "SensitiveCores", SensitiveCoreBudget(available, jobs)));
            parallelism = GeneralCapacity(parallelism, System.Numerics.BitOperations.PopCount(masks.General), options.Has("MaxParallelism") || options.Has("CpuParallelism"));
            lanes["general"] = parallelism;
            jobs = jobs.Select(job => job with { Lane = job.Exclusive ? "sensitive" : "general", Affinity = job.Exclusive ? masks.Stability : masks.General,
                Timeout = CheckScheduler.JobTimeout(job, new Dictionary<string, double>()) }).ToList();
            foreach (var lane in CheckAffinity.AssignSensitiveLanes(jobs, masks.Stability, Positive(options, "SensitiveParallelism", 2)))
                lanes[lane.Key] = lane.Value;
            File.WriteAllText(Path.Combine(output, "cpu-plan.json"), System.Text.Json.JsonSerializer.Serialize(new {
                sensitiveMask = masks.Stability.ToString("X"), generalMask = masks.General.ToString("X"),
                sensitiveMinWorkerThreads = CheckAffinity.BaselineWorkers, lanes,
                checks = jobs.Select(job => new { job.Name, job.Lane, job.SensitiveGroup, affinityMask = job.Affinity?.ToString("X"), job.EstimatedSeconds })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        var rows = CheckScheduler.Run(jobs.Where(job => !job.Name.EndsWith("-gate", StringComparison.Ordinal)).ToArray(), parallelism, Path.Combine(output, "logs"), lanes);
        var gates = CheckScheduler.Run(jobs.Where(job => job.Name.EndsWith("-gate", StringComparison.Ordinal)).Select(job => job with { Dependencies = [] }).ToArray(), parallelism, Path.Combine(output, "gates"));
        File.WriteAllText(Path.Combine(output, "report.json"), System.Text.Json.JsonSerializer.Serialize(new
        { passed = gates.Count(row => row.Status == "passed"), failed = gates.Count(row => row.Status != "passed"), checks = rows.Concat(gates) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(rows.Concat(gates).All(row => row.Status == "passed") ? 0 : 1, $"Suites: {gates.Count(row => row.Status == "passed")} passed; {gates.Count(row => row.Status != "passed")} failed. Evidence: {output}\n", "");
    }
}
