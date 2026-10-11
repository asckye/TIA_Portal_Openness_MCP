using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class CheckAllLayout
{
    internal static string InputKey(IEnumerable<KeyValuePair<string, string>> inputs, IEnumerable<string> properties) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            inputs = inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal), properties = properties.ToArray()
        })))).ToLowerInvariant();

    private static string InputHash(string root, string file)
    {
        if (file is "manifest/version-tools.json" or "manifest/tool-usage-coverage.json")
        {
            var value = JsonNode.Parse(File.ReadAllText(Path.Combine(root, file)))!.AsObject(); value.Remove("generatedAtUtc");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
        }
        return ReleaseRecords.HashFile(Path.Combine(root, file));
    }

    internal static string[] Missing(string defaultOutput, string checkOutput, IEnumerable<string> files) => files
        .Where(file => !File.Exists(Path.Combine(defaultOutput, file)) || !File.Exists(Path.Combine(checkOutput, file)))
        .Order(StringComparer.Ordinal).ToArray();

    internal static CommandResult Verify(string root, IEnumerable<CheckBuild> builds, string dotnet, string nuget, string api, string directory)
    {
        Directory.CreateDirectory(directory);
        var runtime = File.ReadLines(Path.Combine(root, "bin-build/check-all/runtime-paths.tsv")).Select(line => line.Split('\t')).ToArray();
        var tracked = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], root); ProcessRunner.RequireSuccess(tracked, "layout input inventory");
        var inputs = tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(file => File.Exists(Path.Combine(root, file)))
            .Select(file => new KeyValuePair<string, string>(file, InputHash(root, file))).ToArray();
        inputs = inputs.Concat(Directory.EnumerateFiles(api, "*.dll", SearchOption.AllDirectories)
            .Select(file => new KeyValuePair<string, string>("sdk/" + Path.GetRelativePath(api, file), ReleaseRecords.HashFile(file)))).ToArray();
        var receipts = new List<object>();
        var failures = new List<string>();
        try
        {
            foreach (var unit in builds.Where(unit => unit.Project.StartsWith("tests/", StringComparison.Ordinal)).Distinct())
            {
                var row = runtime.Single(row => string.Equals(Path.GetFullPath(row[0]), Path.GetFullPath(Path.Combine(root, unit.Project)), StringComparison.OrdinalIgnoreCase) && row[1] == unit.Variant);
                // The baseline is produced by an ordinary registered-layout build.
                // Cache only its file inventory, never a test result. Every invocation
                // still checks every expected file in the actual isolated output.
                var key = InputKey(inputs, [unit.Project, unit.Variant, .. unit.Properties, api, nuget, ReleaseRecords.HashFile(nuget)]);
                var cache = Path.Combine(root, "bin-build/check-layout", key + ".json");
                string[] files;
                if (File.Exists(cache)) files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(cache))!;
                else
                {
                    var result = ProcessRunner.Run(dotnet, ["msbuild", Path.Combine(root, unit.Project), "-restore", "-t:Build", "-m:1", "-nodeReuse:false", "-v:quiet",
                        "-p:Configuration=Release", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaSharedAdapterPaths=false", "-p:RestoreConfigFile=" + nuget,
                        "-p:TiaPublicApiRoot=" + api, "-p:PublicApiRoot=" + api, .. unit.Properties,
                        "-getProperty:TargetDir", "-getItem:FileWrites,BridgePayload,FixtureFiles"], root,
                        new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null });
                    File.WriteAllText(Path.Combine(directory, Path.GetFileNameWithoutExtension(unit.Project) + "-" + unit.Variant + ".log"), result.StandardOutput + result.StandardError);
                    if (result.ExitCode != 0) return result;
                    var begin = result.StandardOutput.IndexOf("{\n", StringComparison.Ordinal);
                    if (begin < 0) begin = result.StandardOutput.IndexOf("{\r\n", StringComparison.Ordinal);
                    var document = JsonNode.Parse(result.StandardOutput[begin..])!;
                    var defaultOutput = Path.GetFullPath((string)document["Properties"]!["TargetDir"]!);
                    var written = document["Items"]!["FileWrites"]!.AsArray().Select(item => Path.GetFullPath((string)item!["FullPath"]!));
                    files = written.Where(file => file.StartsWith(defaultOutput, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                        .Select(file => Path.GetRelativePath(defaultOutput, file))
                        .Concat(document["Items"]!["BridgePayload"]!.AsArray().Select(item => Path.Combine("bridge", Path.GetFileName((string)item!["Identity"]!))))
                        .Concat(document["Items"]!["FixtureFiles"]!.AsArray().Select(item => Path.Combine("fixtures", Path.GetFileName((string)item!["Identity"]!))))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
                    if (files.Length == 0) return new(1, "", "Ordinary build produced no output inventory: " + unit.Project);
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!); File.WriteAllText(cache, JsonSerializer.Serialize(files) + "\n");
                }
                var missing = Missing(row[3], row[2], files);
                receipts.Add(new { unit.Project, unit.Variant, defaultOutput = row[3], checkOutput = row[2], files, missing, inputKey = key });
                if (missing.Length != 0)
                {
                    failures.Add("Missing registered output files: " + unit.Project + "/" + unit.Variant + ": " + string.Join(", ", missing));
                }
            }
            File.WriteAllText(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return new(failures.Count == 0 ? 0 : 1, $"Suite output parity: {receipts.Count} variants, {failures.Count} with missing files.\n", string.Join("\n", failures));
        }
        finally
        {
            // Ordinary builds may share their default outputs. Restore each isolated
            // production/fixture payload before any check starts.
            ReleaseCommands.PrepareCheckAllRuntime(root, builds, Path.Combine(root, "bin-build/check-all/runtime-paths.tsv"));
        }
    }
}
