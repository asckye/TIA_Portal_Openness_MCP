using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class HostParity
{
    internal const string FoundationProject = "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj";

    internal static string Prepare(string root, string directory, string dotnet, string? api, string? nuget)
    {
        foreach (var major in new[] { "20", "21" })
        {
            List<string> args = ["build", Path.Combine(root, FoundationProject), "-c", "Release", "-m:1", "-nodeReuse:false",
                "-p:DefineConstants=TIA_V" + major, "--output", Path.Combine(directory, "v" + major)];
            if (api != null) args.Add("-p:TiaPublicApiRoot=" + api);
            if (nuget != null) args.Add("-p:RestoreConfigFile=" + nuget);
            ProcessRunner.RequireSuccess(ProcessRunner.Run(dotnet, args, root), "Host parity V" + major + " build");
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
        var command = VstestArguments(assembly, ["--filter", "FullyQualifiedName~BehaviorParityTests"], "host-behavior-parity", directory);
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

    private static List<string> VstestArguments(string assembly, IEnumerable<string> arguments, string name, string directory)
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
}
