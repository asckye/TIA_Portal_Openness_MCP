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
            ["TIA_MCP_TEST_SHARD_ASSEMBLY"] = null, ["TIA_MCP_TEST_SHARD_PLAN"] = null, ["TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD"] = null,
            ["NuGetAudit"] = "false", ["TIA_MCP_CHECK_SYSTEM_TEMP_ROOT"] = SystemTemp
        };
    }

    internal static void AddJobs(List<CheckJob> jobs, string root, string name, JsonObject suite, string output, string dotnet,
        int shards, string? api, string[] dependencies, string? baseline = null, bool prove = false, string[]? extra = null, string? history = null, string? jobPrefix = null)
    {
        var unit = CheckAllBuild.Suite(suite);
        var directory = Path.Combine(output, name);
        history ??= Path.Combine(root, "bin-build/check-timings");
        bool reserveSensitive = TimeSensitiveTests.ReserveShard(unit.Project);
        var plan = SuiteCosts.Prepare(name, directory, history, baseline, shards, reserveSensitive); shards = plan.Costs.Length;
        var paths = Enumerable.Range(0, shards).Select(i => Path.Combine(directory, "shard-" + i, name + ".trx")).ToArray();
        var sequential = baseline;
        if (prove)
        {
            sequential = Path.Combine(directory, "sequential", name + ".trx");
            jobs.Add(new(jobPrefix + name + "-sequential", () => RunShard(root, name, suite, Path.GetDirectoryName(sequential)!, dotnet, api, unit, 0, 1, extra), dependencies, Exclusive: true));
        }
        var shardNames = new List<string>();
        for (int i = 0; i < shards; i++)
        {
            var index = i; var jobName = jobPrefix + name + "-shard-" + i; shardNames.Add(jobName);
            jobs.Add(new(jobName, () => RunShard(root, name, suite, Path.GetDirectoryName(paths[index])!, dotnet, api, unit, index, shards, extra, plan.Path, reserveSensitive), dependencies,
                Exclusive: reserveSensitive && index == shards - 1 || name is "worker-robustness" or "worker-channel") { EstimatedSeconds = plan.Costs[index] + 5 });
        }
        // Merge runs even after a failed test process, so partial totals/failures remain
        // reviewable. The caller schedules merges in a second wave without prerequisites.
        jobs.Add(new(jobPrefix + name + "-gate", () => { SuiteCosts.Record(name, directory, history); return SuiteSharding.Merge(name, suite, paths, output, sequential); }, shardNames.ToArray()) { AllowFailedDependencies = true });
    }

    internal static CommandResult RunShard(string root, string name, JsonObject suite, string directory, string dotnet, string? api,
        CheckBuild unit, int index, int count, string[]? extra = null, string? plan = null, bool reserveSensitive = false)
    {
        Directory.CreateDirectory(directory);
        var environment = EnvironmentFor(directory, api);
        if (unit.Project.StartsWith("tests/Studio/", StringComparison.Ordinal)) environment["TIA_MCP_BUNDLE_ROOT"] = root;
        environment["TIA_MCP_TEST_SHARD_COUNT"] = count.ToString(); environment["TIA_MCP_TEST_SHARD_INDEX"] = index.ToString();
        environment["TIA_MCP_TEST_SHARD_ASSEMBLY"] = Path.GetFileNameWithoutExtension(unit.Project);
        environment["TIA_MCP_TEST_SHARD_PLAN"] = plan;
        environment["TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD"] = reserveSensitive ? "1" : null;
        environment["TIA_MCP_TEST_CASE_TIMINGS"] = Path.Combine(directory, "case-timings.tsv");
        File.Delete(environment["TIA_MCP_TEST_CASE_TIMINGS"]!);
        environment["TIA_MCP_TEST_PRODUCER_TIMINGS"] = Path.Combine(directory, "producer-timings.tsv");
        File.Delete(environment["TIA_MCP_TEST_PRODUCER_TIMINGS"]!);
        var trx = Path.Combine(directory, name + ".trx"); File.Delete(trx);
        environment["TIA_MCP_TEST_PHASE_TIMINGS"] = Path.Combine(directory, "phase-timings.json");
        var assembly = CheckAllBuild.TestAssembly(root, unit);
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
        }
        var result = ProcessRunner.Run(dotnet, args, root, environment);
        if (!File.Exists(trx)) return result with { ExitCode = 1, StandardError = result.StandardError + "\nMissing shard TRX: " + trx };
        var errors = DotnetSuites.ReadTrx(trx).Errors;
        if (errors.Count > 0) return result with { ExitCode = 1, StandardError = result.StandardError + "\n" + string.Join("\n", errors) };
        return result;
    }

    internal static List<string> VstestArguments(string assembly, IEnumerable<string> arguments, string name, string directory)
    {
        var source = arguments.ToArray();
        var result = new List<string> { "vstest", assembly, "--logger:trx;LogFileName=" + name + ".trx", "--ResultsDirectory:" + directory };
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
            ReleaseCommands.PrepareCheckAllRuntime(root, builds, Path.Combine(output, "build/runtime-paths.tsv"));
        }
        var jobs = new List<CheckJob>();
        foreach (var name in names)
        {
            var suite = catalog[name]!.AsObject();
            string? api = name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true
                ? options.Get("PublicApiRoot") ?? Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT") : null;
            AddJobs(jobs, root, name, suite, output, dotnet, suite["minimumPassed"]!.GetValue<int>() >= 100 ? count : 1, api, [],
                options.Get("SequentialBaselineDirectory") is { } baseline ? Path.Combine(baseline, name + ".trx") : null, options.Has("ProveEquivalence"), options.All("DotnetArg").ToArray());
        }
        var rows = CheckScheduler.Run(jobs.Where(job => !job.Name.EndsWith("-gate", StringComparison.Ordinal)).ToArray(), parallelism, Path.Combine(output, "logs"));
        var gates = CheckScheduler.Run(jobs.Where(job => job.Name.EndsWith("-gate", StringComparison.Ordinal)).Select(job => job with { Dependencies = [] }).ToArray(), parallelism, Path.Combine(output, "gates"));
        File.WriteAllText(Path.Combine(output, "report.json"), System.Text.Json.JsonSerializer.Serialize(new
        { passed = gates.Count(row => row.Status == "passed"), failed = gates.Count(row => row.Status != "passed"), checks = rows.Concat(gates) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(rows.Concat(gates).All(row => row.Status == "passed") ? 0 : 1, $"Suites: {gates.Count(row => row.Status == "passed")} passed; {gates.Count(row => row.Status != "passed")} failed. Evidence: {output}\n", "");
    }
}
