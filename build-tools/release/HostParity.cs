using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class HostParity
{
    internal const string FoundationProject = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj";

    internal static string Prepare(string root, string directory, string dotnet, string? api, string? nuget, SuiteProcess? process = null)
    {
        foreach (var major in new[] { "20", "21" })
        {
            List<string> args = ["build", Path.Combine(root, FoundationProject), "-c", "Release", "-m:1", "-nodeReuse:false",
                "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", "--output", Path.Combine(directory, "v" + major)];
            if (api != null) args.Add("-p:TiaPublicApiRoot=" + api);
            if (nuget != null) args.Add("-p:RestoreConfigFile=" + nuget);
            ProcessRunner.RequireSuccess((process ?? DotnetSuites.RunProcess)(dotnet, args, root, null), "Host parity V" + major + " build");
        }
        return directory;
    }

    internal static CommandResult Run(string root, Options options, IDictionary<string, string?>? environment = null, SuiteProcess? process = null)
    {
        var major = options.Get("EngineMajor", "21");
        if (major is not ("20" or "21")) throw new ReleaseException("-EngineMajor must be 20 or 21", 64);
        var directory = Path.GetFullPath(options.Get("ResultsDirectory", Path.Combine(root, "bin-build/test-results/host-behavior-parity")));
        Directory.CreateDirectory(directory);
        var trx = Path.Combine(directory, "host-behavior-parity.trx");
        var summary = Path.Combine(directory, "host-behavior-parity.json");
        File.Delete(trx);
        File.Delete(summary);
        var before = SuiteHang.Begin(directory, "host-behavior-parity");
        var buildDirectory = options.Get("BuildDirectory") ?? throw new ReleaseException("Parity BuildDirectory is required");
        var assembly = Path.Combine(buildDirectory, "v" + major, "TiaMcp.FoundationHost.Tests.dll");
        var command = DotnetSuites.VstestArguments(assembly, ["--filter", "FullyQualifiedName~BehaviorParityTests"], "host-behavior-parity", directory);
        var env = environment is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(environment);
        env["TIA_MCP_PARITY_ENGINE_RELEASE"] = major;
        var dotnet = options.Get("Dotnet", "dotnet");
        var output = new StringBuilder($"RUN host-behavior-parity: {dotnet} {string.Join(' ', command)}\n");
        // Parity cases also write fixed relative bin-build/P6-* fixtures. Give
        // each no-build variant its own working directory as well as TEMP.
        var working = directory;
        var result = SuiteHang.Complete(directory, "host-behavior-parity", before, (process ?? DotnetSuites.RunProcess)(dotnet, command, working, env));
        output.Append(result.StandardOutput);
        var errors = new List<string>();
        errors.AddRange(SuiteHang.Errors(directory, "host-behavior-parity"));
        var counts = new JsonObject();
        if (result.ExitCode != 0) errors.Add("dotnet test exited " + result.ExitCode);
        if (File.Exists(trx))
        {
            var evaluated = DotnetSuites.EvaluateTrx(trx, 103, 0);
            counts = evaluated.Counts;
            errors.AddRange(evaluated.Errors);
        }
        else errors.Add("missing TRX");
        var value = new JsonObject { ["suite"] = "host-behavior-parity", ["paths"] = new JsonArray("engine", "foundation-eight-releases", "engine-host-fake-worker"),
            ["engineRelease"] = major, ["minimumPassed"] = 103, ["maximumSkipped"] = 0, ["exitCode"] = result.ExitCode };
        foreach (var (key, item) in counts) value[key] = item?.DeepClone();
        value["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        File.WriteAllText(summary, PythonJson.Dumps(value, indent: 2) + "\n", new UTF8Encoding(false));
        output.Append(errors.Count != 0 ? $"FAIL host-behavior-parity: {string.Join("; ", errors)}\n" : $"COMPLETE: {counts["passed"]} host-behavior-parity checks passed; 0 failed, 0 skipped\n");
        return new CommandResult(errors.Count == 0 ? 0 : 1, output.ToString(), result.StandardError);
    }

}
