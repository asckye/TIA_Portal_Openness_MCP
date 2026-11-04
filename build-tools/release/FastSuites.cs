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
        return EnvironmentValues(isolation, Guid.NewGuid().ToString("N"), api);
    }

    internal static Dictionary<string, string?> EnvironmentValues(string isolation, string pipeScope, string? api) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = isolation, ["TMP"] = isolation, ["TMPDIR"] = isolation,
            ["TIA_MCP_DATA_DIRECTORY"] = Path.Combine(isolation, "data"), ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(isolation, "data", "diagnostics"),
            ["LOCALAPPDATA"] = Path.Combine(isolation, "local"), ["APPDATA"] = Path.Combine(isolation, "roaming"),
            ["TIA_MCP_TEST_PIPE_SCOPE"] = pipeScope, ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api,
            ["TIA_MCP_TEST_PRODUCT_ROOT"] = null, ["TIA_MCP_TEST_SHARD_COUNT"] = null, ["TIA_MCP_TEST_SHARD_INDEX"] = null,
            ["TIA_MCP_TEST_WORKER_FIXTURE_ROOT"] = null, ["TIA_MCP_TEST_REGISTERED_OUTPUT"] = null,
            ["TIA_MCP_TEST_SENSITIVE_SHARDS"] = null, ["TIA_MCP_TEST_SHARD_ASSEMBLY"] = null, ["TIA_MCP_TEST_SHARD_PLAN"] = null, ["TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD"] = null,
            ["NuGetAudit"] = "false", ["TIA_MCP_CHECK_SYSTEM_TEMP_ROOT"] = SystemTemp
        };

    internal static CommandResult RunShard(string root, string name, JsonObject suite, string directory, string dotnet, string? api,
        SuiteBuild unit, int index, int count, string[]? extra = null, string? plan = null, bool reserveSensitive = false, SuiteProcess? process = null, int sensitiveShards = 1, bool diagnostics = false)
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
        List<string> args = ["test", Path.Combine(root, unit.Project), "-c", "Release", "--no-build", "--no-restore", "--nologo", "--disable-build-servers", "-m:1", "-nodeReuse:false"];
        args.AddRange(suite["arguments"]!.AsArray().Select(a => a!.GetValue<string>()));
        if (api != null) args.Add("-p:TiaPublicApiRoot=" + api);
        if (extra != null) args.AddRange(extra);
        args.AddRange(["--logger", "trx;LogFileName=" + name + ".trx", "--results-directory", directory]);
        args.AddRange(SuiteHang.TestArguments);
        if (diagnostics)
        {
            var diagnostic = Path.Combine(directory, name + ".vstest.log") + ";TraceLevel=Info";
            args.AddRange(["--diag", diagnostic]);
        }
        var result = SuiteHang.Complete(directory, name, before, (process ?? DotnetSuites.RunProcess)(dotnet, args, root, environment));
        if (!File.Exists(trx)) return result with { ExitCode = 1, StandardError = result.StandardError + "\nMissing shard TRX: " + trx };
        var errors = DotnetSuites.ReadTrx(trx).Errors;
        if (errors.Count > 0) return result with { ExitCode = 1, StandardError = result.StandardError + "\n" + string.Join("\n", errors) };
        return result;
    }
}

internal sealed record SuiteBuild(string Project, string Variant, string[] Properties)
{
    internal static SuiteBuild Suite(JsonObject suite)
    {
        var properties = DotnetSuites.BuildArguments(suite["arguments"]!.AsArray().Select(a => a!.GetValue<string>())).ToArray();
        return new(suite["project"]!.GetValue<string>(), "default", properties);
    }
}
