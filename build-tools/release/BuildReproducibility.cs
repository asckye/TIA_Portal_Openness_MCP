using System.Diagnostics;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static class BuildReproducibility
{
    // Check outputs have a stable informational-version suffix. Release builds
    // keep their own commit stamp and never share this cache identity.
    internal const string CheckRevision = "check";
    internal static string[] CheckArguments => ["-p:TiaCheckBuild=true", "-p:SourceRevisionId=" + CheckRevision];

    internal static string SourceRevision(string root)
    {
        var stamp = Path.Combine(root, "bin-build/source-revision.txt");
        if (File.Exists(stamp) && !File.Exists(Path.Combine(root, ".git")) && !Directory.Exists(Path.Combine(root, ".git"))) return File.ReadAllText(stamp).Trim();
        if (!File.Exists(Path.Combine(root, ".git")) && !Directory.Exists(Path.Combine(root, ".git"))) return "";
        var result = ProcessRunner.Run("git", ["rev-parse", "--verify", "--quiet", "HEAD"], root);
        // Synthetic build repositories can legitimately have an unborn HEAD.
        if (result.ExitCode == 1) return "";
        ProcessRunner.RequireSuccess(result, "Identify build source revision");
        return result.StandardOutput.Trim();
    }
    private static IEnumerable<string> Files(string directory, Action? checkDeadline = null)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new ReleaseException("Linked build output: " + directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            checkDeadline?.Invoke();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ReleaseException("Linked build output: " + file);
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
            foreach (var file in Files(child, checkDeadline)) yield return file;
    }

    internal static ReleaseArtifact[] Inventory(string directory, Action? checkDeadline = null) => Files(directory, checkDeadline)
        .Select(file => new ReleaseArtifact(Path.GetRelativePath(directory, file).Replace('\\', '/'), ReleaseRecords.HashFile(file, checkDeadline)))
        .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

    internal static string[] Differences(ReleaseArtifact[] first, ReleaseArtifact[] second) => first.Concat(second)
        .GroupBy(file => file.Path, StringComparer.Ordinal).Where(group => group.Count() != 2 || group.Select(file => file.Sha256).Distinct().Count() != 1)
        .Select(group => group.Key).Order(StringComparer.Ordinal).ToArray();

    internal static CheckBuild[] Representatives() => [
        new("src/Logic/TiaMcp.Logic.csproj", "", []),
        new("src/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj", "", []),
        new("src/WorkerChannel/TiaMcp.WorkerChannel.csproj", "", []),
        new("src/FoundationHost/TiaMcp.FoundationHost.csproj", "v20", ["-p:TiaReleaseKey=20"]),
        new("src/FoundationHost/TiaMcp.FoundationHost.csproj", "v14sp1", ["-p:TiaReleaseKey=14sp1"]),
        new("src/PlcWorker/TiaMcp.PlcWorker.csproj", "v21", ["-p:TiaReleaseKey=21"]),
        .. new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key =>
            new CheckBuild($"src/Adapters/{(key == "14sp1" ? "V14Sp1" : key == "15.1" ? "V15_1" : "V" + key)}/Adapter.{key}.csproj", "adapter-" + key, ["-p:TiaReleaseKey=" + key])),
        new("src/Studio/Core/TiaOpenness.Core.csproj", "", [])
    ];

    internal static int Run(string root, Options options)
    {
        var output = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(root, "bin-build/repro-" + Guid.NewGuid().ToString("N")[..8])));
        ReleaseCommands.RequireCheckAllOwnedPath(root, output);
        if (Directory.Exists(output)) throw new ReleaseException("Reproducibility output must be fresh: " + output, 64);
        Directory.CreateDirectory(output);
        var dotnet = options.Get("Dotnet", "dotnet");
        if (options.Has("SelfTest")) return Fixture(root, output, dotnet);
        if (options.Has("SharedLibrariesOnly")) return SharedLibraries(root, output, dotnet, options);
        var api = Path.GetFullPath(options.Get("PublicApiRoot") ?? throw new ReleaseException("build-reproducibility requires -PublicApiRoot", 64));
        var nuget = Path.GetFullPath(options.Get("NuGetConfig") ?? throw new ReleaseException("build-reproducibility requires offline -NuGetConfig", 64));
        if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error, 64);
        var inventory = CheckAllLayout.SourceInventory(root);
        var rows = new List<object>();
        var snapshots = new List<Dictionary<string, ReleaseArtifact[]>>();
        foreach (var side in new[] { "first", "different-second" })
        {
            var copy = Path.Combine(output, side);
            Copy(root, copy, inventory);
            Directory.CreateDirectory(Path.Combine(copy, "bin-build")); File.WriteAllText(Path.Combine(copy, "bin-build/source-revision.txt"), SourceRevision(root));
            var units = Representatives();
            var clock = Stopwatch.StartNew();
            var result = CheckAllBuild.Run(copy, units, Path.Combine(copy, "bin-build/repro"), dotnet, api, nuget,
                FastSuites.Positive(options, "BuildParallelism", 1), cold: true);
            File.WriteAllText(Path.Combine(output, side + ".log"), result.StandardOutput + result.StandardError);
            ProcessRunner.RequireSuccess(result, "Reproducibility " + side);
            var files = new Dictionary<string, ReleaseArtifact[]>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(Path.Combine(copy, "bin-build/repro/runtime-paths.tsv")))
            {
                var fields = line.Split('\t');
                files[Path.GetRelativePath(copy, fields[0]).Replace('\\', '/') + "|" + fields[1] + "|" +
                    Path.GetFileName(fields[2].TrimEnd('\\', '/')) + "|" + Path.GetFileName(fields[4])] = Inventory(fields[2]);
            }
            snapshots.Add(files); rows.Add(new { side, seconds = clock.Elapsed.TotalSeconds, outputs = files });
        }
        var differences = snapshots[0].Keys.Union(snapshots[1].Keys).SelectMany(unit => Differences(snapshots[0].GetValueOrDefault(unit) ?? [], snapshots[1].GetValueOrDefault(unit) ?? [])
            .Select(file => unit + "/" + file)).ToArray();
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new { rows, differences }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Build reproducibility: {snapshots[0].Count} outputs, {differences.Length} differences. " + string.Join("\n", differences));
        return differences.Length == 0 ? 0 : 1;
    }

    private static int SharedLibraries(string root, string output, string dotnet, Options options)
    {
        var nuget = Path.GetFullPath(options.Get("NuGetConfig") ?? throw new ReleaseException("Shared library proof requires offline -NuGetConfig", 64));
        if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error, 64);
        var copy = Path.Combine(output, "source"); Copy(root, copy, CheckAllLayout.SourceInventory(root));
        Directory.CreateDirectory(Path.Combine(copy, "bin-build")); File.WriteAllText(Path.Combine(copy, "bin-build/source-revision.txt"), SourceRevision(root));
        string[] libraries = ["src/Logic/TiaMcp.Logic.csproj", "src/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj", "src/WorkerChannel/TiaMcp.WorkerChannel.csproj",
            "third_party/TiaGitAddIn.Core/TiaGitAddIn.Core.csproj", "build-tools/native-call-weaver/NativeCallWeaver.csproj", "src/OnlineChannels/Simulation/TiaMcp.PlcSimAdvanced.csproj"];
        var rows = new List<object>(); var differences = new List<string>();
        foreach (var project in libraries)
        {
            ReleaseArtifact[]? first = null;
            foreach (var key in CatalogJson.Keys)
            {
                var clock = Stopwatch.StartNew();
                var result = ProcessRunner.Run(dotnet, ["build", Path.Combine(copy, project), "-c", "Release", "--no-incremental", "-m:1", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", "-p:RestoreConfigFile=" + nuget, "-p:TiaReleaseKey=" + key,
                    "-p:SourceRevisionId=" + SourceRevision(root),
                    "-p:TiaSharedAdapterPaths=" + (key is "20" or "21" ? "true" : "false")], copy,
                    logPath: Path.Combine(output, Path.GetFileNameWithoutExtension(project) + "-" + key + ".log"));
                ProcessRunner.RequireSuccess(result, "Shared library identity " + project + " " + key);
                var files = Inventory(Path.Combine(copy, Path.GetDirectoryName(project)!, "bin/Release"));
                var changed = first == null ? [] : Differences(first, files);
                differences.AddRange(changed.Select(file => project + " " + key + ": " + file));
                first ??= files;
                rows.Add(new { project, key, seconds = clock.Elapsed.TotalSeconds, files, differences = changed });
                Console.WriteLine($"Library identity {project} {key}: {files.Length} files, {changed.Length} differences; {clock.Elapsed.TotalSeconds:F3}s");
            }
        }
        var builds = libraries.SelectMany(project => CatalogJson.Keys.Select(key => new CheckBuild(project, "key-" + key, ["-p:TiaReleaseKey=" + key]))).ToArray();
        var times = new List<object>();
        var prior = Environment.GetEnvironmentVariable("TIA_MCP_CHECK_SHARE_LIBRARIES");
        try
        {
            foreach (var mode in new[] { "before", "after" })
            {
                Environment.SetEnvironmentVariable("TIA_MCP_CHECK_SHARE_LIBRARIES", mode == "before" ? "0" : "1");
                var directory = Path.Combine(copy, "bin-build/" + mode); var clock = Stopwatch.StartNew();
                var result = CheckAllBuild.Run(copy, builds, directory, dotnet, null, nuget, FastSuites.Positive(options, "BuildParallelism", 1), true);
                ProcessRunner.RequireSuccess(result, "Shared library graph " + mode);
                var nodes = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "build-nodes.props")).Descendants("CheckGraphProject").Count();
                var log = File.ReadAllLines(Path.Combine(directory, "build.log"));
                times.Add(new { mode, seconds = clock.Elapsed.TotalSeconds, nodes, projectOutputs = log.Count(line => line.Contains(" -> ", StringComparison.Ordinal)) });
            }
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_CHECK_SHARE_LIBRARIES", prior); }
        File.WriteAllText(Path.Combine(output, "identity.json"), JsonSerializer.Serialize(new { rows, times, differences }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return differences.Count == 0 ? 0 : 1;
    }

    private static void Copy(string root, string destination, IEnumerable<string> inventory)
    {
        foreach (var relative in inventory)
        {
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            ReleaseCommands.RequireCheckAllOwnedPath(destination, target);
            if (!File.Exists(Path.Combine(root, relative))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(root, relative), target);
        }
    }

    private static int Fixture(string root, string output, string dotnet)
    {
        var snapshots = new List<ReleaseArtifact[]>();
        foreach (var side in new[] { "first", "different-second" })
        {
            var copy = Path.Combine(output, side); Directory.CreateDirectory(Path.Combine(copy, "src/Shared"));
            File.Copy(Path.Combine(root, "src/Shared/ReproducibleBuild.props"), Path.Combine(copy, "src/Shared/ReproducibleBuild.props"));
            File.WriteAllText(Path.Combine(copy, "Directory.Build.props"), "<Project><Import Project=\"src/Shared/ReproducibleBuild.props\" /></Project>");
            File.WriteAllText(Path.Combine(copy, "fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks><SourceRevisionId>same-commit</SourceRevisionId></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(copy, "Fixture.cs"), "using System.Runtime.CompilerServices; public class Fixture { public static string Source([CallerFilePath] string file = \"\") => file; public static string Recorded => Source(); }\n");
            File.WriteAllText(Path.Combine(copy, "nuget.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            var environment = new Dictionary<string, string?> { ["DOTNET_CLI_HOME"] = Path.Combine(copy, "cli"), ["TEMP"] = Path.Combine(copy, "temp"), ["TMP"] = Path.Combine(copy, "temp") };
            Directory.CreateDirectory(environment["TEMP"]!);
            var result = ProcessRunner.Run(dotnet, ["build", Path.Combine(copy, "fixture.csproj"), "-c", "Release", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                "-p:NuGetAudit=false", "-p:RestoreConfigFile=" + Path.Combine(copy, "nuget.config")], copy, environment, Path.Combine(output, side + ".log"));
            ProcessRunner.RequireSuccess(result, "Reproducibility fixture " + side);
            snapshots.Add(Inventory(Path.Combine(copy, "bin/Release")));
        }
        var differences = Differences(snapshots[0], snapshots[1]);
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new { outputs = snapshots, differences }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Reproducibility self-test: {snapshots[0].Length} files, {differences.Length} differences.");
        return differences.Length == 0 ? 0 : 1;
    }
}
