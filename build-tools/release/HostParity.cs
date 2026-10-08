using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class HostParity
{
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
        var command = new List<string> { "test", Path.Combine(root, "tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj"),
            "-c", "Release", "--disable-build-servers", "-m:1", "--filter", "FullyQualifiedName~BehaviorParityTests", "--logger", "trx;LogFileName=host-behavior-parity.trx", "--results-directory", directory };
        if (options.Has("NoBuild")) command.AddRange(["--no-build", "--no-restore"]);
        else if (major == "20") command.Add("-p:DefineConstants=TIA_V20");
        var env = environment is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(environment);
        env["TIA_MCP_PARITY_ENGINE_RELEASE"] = major;
        var dotnet = options.Get("Dotnet", "dotnet");
        var output = new StringBuilder($"RUN host-behavior-parity: {dotnet} {string.Join(' ', command)}\n");
        var result = (process ?? DotnetSuites.RunProcess)(dotnet, command, root, env);
        output.Append(result.StandardOutput);
        var errors = new List<string>();
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

internal static partial class ReleaseCommands
{
    private static int HostBehaviorParity(Options options)
    {
        var result = HostParity.Run(Root, options);
        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        return result.ExitCode;
    }
}
