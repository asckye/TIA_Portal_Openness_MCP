using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    // One installed worker layout for every release.
    internal static string PlcReleaseInstallDirectory(string root, string key) =>
        Path.Combine(root, "runtime/v" + key, "worker");

    internal static string ReleasePlcFixture(string logs, string key) =>
        new PlcWorkerFixtureBuild(key, "sdk").Worker(Path.Combine(logs, "plc-fixtures"));

    internal static string PreparePlcReleaseFilePrograms(string root, string dotnet, string? nuget, string logs,
        string systemTemp, SuiteProcess? process = null)
    {
        systemTemp = Path.GetFullPath(systemTemp);
        Directory.CreateDirectory(systemTemp);
        Directory.CreateDirectory(logs);
        // Test-CampaignInputs.py starts its file app with dotnet run --file --no-build, which
        // finds the program below TEMP. Compile into the caller-owned cache and export the same path to jobs;
        // their private TEMP directories may be removed before a later stage starts.
        // Same arguments as build-file-programs: a file-based build rejects -m and -nodeReuse.
        foreach (var program in PlcReleaseFilePrograms)
        {
            var arguments = new List<string> { "build", Path.Combine(root, program), "-c", "Release", "--nologo", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false" };
            if (!string.IsNullOrWhiteSpace(nuget)) arguments.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
            var built = (process ?? DotnetSuites.RunProcess)(dotnet, arguments, root, new Dictionary<string, string?>
            {
                ["TEMP"] = systemTemp, ["TMP"] = systemTemp, ["TMPDIR"] = systemTemp,
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1"
            });
            WriteLog(Path.Combine(logs, Path.GetFileNameWithoutExtension(program) + "-plc-build.log"), built);
            ProcessRunner.RequireSuccess(built, "PLC file app build: " + program);
        }
        return systemTemp;
    }

    internal static readonly string[] PlcReleaseFilePrograms = ["scripts/checks/Test-CampaignInputs.cs"];
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

    // Preparation and its tests share this command inventory.
    private static readonly IReadOnlyDictionary<string, (int Count, bool Exact)> BuildReleaseCountRules =
        new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
        {
            ["nativeMcpSafety"] = (8, false), ["nativeSupervisor"] = (1, false), ["approvalSafety"] = (8, true)
        };

    private static int RequireCount(string name, string text, string pattern, string? rule = null)
    {
        var match = Regex.Match(text, pattern);
        if (!match.Success) throw new ReleaseException(name + " did not report a complete check count");
        var actual = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (rule is not null) CheckReleaseCount(rule, actual, name + " checks incomplete");
        return actual;
    }

    private static void CheckReleaseCount(string key, int actual, string message)
    {
        if (!BuildReleaseCountRules.TryGetValue(key, out var expected)) throw new ReleaseException("Unknown check-count gate: " + key);
        if ((expected.Exact && actual != expected.Count) || (!expected.Exact && actual < expected.Count))
            throw new ReleaseException($"{message}; expected {(expected.Exact ? "exactly" : "at least")} {expected.Count}, actual {actual}");
    }

    private static void ValidateApproval(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var evidence = doc.RootElement;
        CheckReleaseCount("approvalSafety", GetJsonInt(evidence, "checksPassed"), "Default approval checks incomplete");
        var results = evidence.GetProperty("results");
        if (GetJsonString(evidence, "status") != "passed" || GetJsonInt(evidence, "checksExpected") != 8 || GetJsonBool(evidence, "workbenchConnected") ||
            GetJsonString(results, "CreateDevice") != "native-admission; no approval card; not-started" ||
            GetJsonBool(evidence, "tiaConnected") || GetJsonString(results, "direct") != "refused-before-dispatch; read-succeeded" ||
            GetJsonString(results, "CallTool") != "refused-before-dispatch") throw new ReleaseException("Default approval evidence mismatch");
    }

}
