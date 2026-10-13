using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    internal const int CheckAllCleanRootMargin = 24;

    internal static string CheckAllSnapshotPath(string original, string id)
    {
        var snapshot = Path.GetFullPath(Path.Combine(original, "bin-build/c", id));
        RequireCheckAllCleanRootHeadroom(original, snapshot);
        return snapshot;
    }

    internal static void RequireCheckAllCleanRootHeadroom(string original, string snapshot)
    {
        if (Path.GetFullPath(snapshot).TrimEnd('\\', '/').Length > Path.GetFullPath(original).TrimEnd('\\', '/').Length + CheckAllCleanRootMargin)
            throw new ReleaseException("Clean checkout root exceeds the normal root by more than " + CheckAllCleanRootMargin + " characters: " + snapshot);
    }

    private static int CheckAllSnapshot(Options options)
    {
        var original = Root;
        var snapshot = CheckAllSnapshotPath(original, Guid.NewGuid().ToString("N")[..8]);
        var output = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(original, "bin-build/check-all/clean-" + Guid.NewGuid().ToString("N"))));
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        // Git for Windows' clone transport invokes sh even for a local worktree,
        // which cannot create its signal pipe in the sandbox. Initialize the same
        // shared-object detached baseline using only native Git index operations.
        ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["init", "--quiet", snapshot], original), "initialize local checkout");
        var common = ProcessRunner.Run("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], original);
        var head = ProcessRunner.Run("git", ["rev-parse", "HEAD"], original);
        ProcessRunner.RequireSuccess(common, "shared object directory"); ProcessRunner.RequireSuccess(head, "detached baseline");
        File.WriteAllText(Path.Combine(snapshot, ".git/objects/info/alternates"), Path.Combine(common.StandardOutput.Trim(), "objects").Replace('\\', '/') + "\n");
        File.WriteAllText(Path.Combine(snapshot, ".git/HEAD"), head.StandardOutput.Trim() + "\n");
        var refs = ProcessRunner.Run("git", ["for-each-ref", "--format=%(objectname) %(refname)"], original);
        ProcessRunner.RequireSuccess(refs, "detached checkout refs");
        // Historical-source checks use tags just as they do in a detached worktree.
        File.WriteAllText(Path.Combine(snapshot, ".git/packed-refs"), refs.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        ProcessRunner.RequireSuccess(ProcessRunner.Run("git", ["read-tree", "HEAD"], snapshot), "initialize detached baseline index");
        // Review the current worktree, including new source files, rather than
        // silently testing only HEAD. The new index remains the detached baseline.
        var inventory = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], original);
        ProcessRunner.RequireSuccess(inventory, "clean source inventory");
        foreach (var relative in inventory.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var destination = Path.GetFullPath(Path.Combine(snapshot, relative));
            if (!destination.StartsWith(snapshot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Unsafe checkout source path: " + relative);
            var source = Path.Combine(original, relative);
            if (!File.Exists(source)) { if (File.Exists(destination)) File.Delete(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true);
        }
        var result = ProcessRunner.Run(Dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", .. options.Arguments(), "-Worktree=" + snapshot,
            "-OutputDirectory=" + output, "-CacheDirectory=" + Path.GetFullPath(options.Get("CacheDirectory", Path.Combine(original, "bin-build/cache/check-all")))], snapshot,
            new Dictionary<string, string?> { ["TIA_MCP_CLEAN_CHECKOUT"] = snapshot, ["TIA_MCP_CHECK_RUNNER"] = "1", ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null,
                ["TIA_MCP_CHECK_ISOLATION_ROOT"] = null }, forwardOutput: true);
        Console.WriteLine("Retained detached source checkout: " + snapshot);
        return result.ExitCode;
    }

    private static CommandResult CheckAllCachedBuild(Options options, CheckBuild[] builds, string output, string dotnet, string api, string nuget, int parallelism)
    {
        if (!options.Has("Clean") && !options.Has("Cold"))
        {
            File.WriteAllText(Path.Combine(output, "build-cache.json"), "{\"mode\":\"in-place-incremental\",\"outputsRestored\":0}\n");
            return CheckAllInPlaceBuild(options, builds, output, dotnet, api, nuget, parallelism);
        }
        var directories = builds.Select(unit => Path.Combine(Path.GetDirectoryName(unit.Project)!, "bin").Replace('\\', '/'))
            .Append("src/Engine/bin-v20").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], Root);
        ProcessRunner.RequireSuccess(files, "build cache source inventory");
        var inputs = files.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => File.Exists(Path.Combine(Root, path)))
            .Select(path => new ReleaseArtifact(path, ReleaseRecords.HashFile(Path.Combine(Root, path)))).ToList();
        foreach (var path in Directory.EnumerateFiles(api, "Siemens.Engineering*.dll", SearchOption.AllDirectories))
            inputs.Add(new("sdk/" + Path.GetRelativePath(api, path).Replace('\\', '/'), ReleaseRecords.HashFile(path)));
        inputs.Add(new("offline-nuget.config", ReleaseRecords.HashFile(nuget)));
        var key = BuildOutputCache.Key(inputs, [Environment.OSVersion.ToString(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessRunner.Run(dotnet, ["--version"], Root).StandardOutput.Trim(), .. builds.Select(unit => unit.Project + "|" + unit.Variant + "|" + string.Join(";", unit.Properties))]);
        var cacheRoot = Path.GetFullPath(options.Get("CacheDirectory", Path.Combine(Root, "bin-build/cache/check-all")));
        var cache = new BuildOutputCache(cacheRoot, "check-all-complete");
        var plan = Path.Combine(cacheRoot, "outputs-" + key + ".json");
        var eligible = new List<string>(); var hits = new List<string>();
        if (!options.Has("Cold") && !options.Has("NoBuildCache") && File.Exists(plan))
        {
            try
            {
                if (JsonSerializer.Deserialize<string[]>(File.ReadAllText(plan)) is { Length: > 0 } paths &&
                    paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length && paths.All(path => directories.Contains(path, StringComparer.OrdinalIgnoreCase)) && cache.Restore(key, Root, paths)) hits.AddRange(paths);
            }
            catch (JsonException) { /* A bad cache plan is a miss; compilation remains authoritative. */ }
        }
        var result = CheckAllInPlaceBuild(options, builds, output, dotnet, api, nuget, parallelism);
        if (result.ExitCode == 0 && !options.Has("NoBuildCache"))
            foreach (var directory in directories.Where(path => Directory.Exists(Path.Combine(Root, path))))
            {
                if (BuildOutputCache.EmbeddedPath(Path.Combine(Root, directory), Root) != null) continue;
                eligible.Add(directory);
            }
        if (result.ExitCode == 0 && !options.Has("NoBuildCache") && eligible.Count > 0 && hits.Count == 0)
        {
            // One transaction avoids repeatedly scanning/pruning the entire cache
            // after each of the many project output directories.
            cache.Populate(key, Root, eligible);
            File.WriteAllText(plan, JsonSerializer.Serialize(eligible) + "\n");
        }
        File.WriteAllText(Path.Combine(output, "build-cache.json"), JsonSerializer.Serialize(new { key, hits, eligible, outputsRestored = hits.Count,
            note = "Restored outputs are still validated by the incremental MSBuild traversal; source-path-bearing binaries rebuild in the new worktree." }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return result;
    }
    private static CommandResult CheckAllInPlaceBuild(Options options, CheckBuild[] builds, string output, string dotnet, string api, string nuget, int parallelism)
    {
        var result = CheckAllBuild.Run(Root, builds, Path.Combine(output, "build"), dotnet, api, nuget, parallelism, options.Has("Cold"));
        if (result.ExitCode == 0) PrepareCheckAllRuntime(Root, builds, Path.Combine(output, "build/runtime-paths.tsv"));
        if (result.ExitCode == 0)
        {
            var scripts = FileBasedPrograms.Build(Root, Path.Combine(output, "build/analyzer-warnings"), dotnet, nuget);
            result = new(scripts.ExitCode, result.StandardOutput + scripts.StandardOutput, result.StandardError + scripts.StandardError);
        }
        if (result.ExitCode == 0)
            foreach (var name in new[] { "Test-StaticCheckParity", "Test-MatchPlcName" })
            {
                var script = CheckAllScriptBuild(name, output, dotnet, nuget);
                result = new(result.ExitCode == 0 ? script.ExitCode : result.ExitCode, result.StandardOutput + script.StandardOutput, result.StandardError + script.StandardError);
            }
        return result;
    }

    internal static string CheckAllScriptInputKey(string root, string name, string nuget, string sdkVersion)
    {
        var inputs = Directory.EnumerateFiles(Path.Combine(root, "build-tools/common"), "*", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Concat(new[] { Path.Combine(root, "scripts/checks/" + name + ".cs"), nuget, Path.Combine(root, "Version.props") })
            .Concat(new[] { "Directory.Build.props", "Directory.Build.targets", "global.json", ".editorconfig", "src/Shared/CodeAnalysis.props" }.Select(file => Path.Combine(root, file)).Where(File.Exists));
        return CheckAllLayout.InputKey(inputs.Select(file => new KeyValuePair<string, string>(file, ReleaseRecords.HashFile(file))),
            [name, sdkVersion, "Release;UseAppHost=false;UseSharedCompilation=false;NuGetAudit=false"]);
    }

    private static CommandResult CheckAllScriptBuild(string name, string output, string dotnet, string nuget)
    {
        var destination = Path.Combine(Root, "bin-build/check-scripts", name);
        var sdk = ProcessRunner.Run(dotnet, ["--version"], Root); ProcessRunner.RequireSuccess(sdk, "File app SDK version");
        var key = CheckAllScriptInputKey(Root, name, nuget, sdk.StandardOutput);
        var cache = new BuildOutputCache(Path.Combine(Root, "bin-build/check-script-cache"), name);
        if (cache.Restore(key, destination))
        {
            File.WriteAllText(Path.Combine(output, "build", name + ".log"), "Identical file-app inputs and verified outputs: cache hit " + key + "\n");
            return new(0, name + ": verified file-app cache hit\n", "");
        }
        var result = ProcessRunner.Run(dotnet, ["build", Path.Combine(Root, "scripts/checks/" + name + ".cs"), "-c", "Release",
            "-o", destination, "-p:RestoreConfigFile=" + nuget, "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:UseAppHost=false"], Root,
            logPath: Path.Combine(output, "build", name + ".log"));
        if (result.ExitCode == 0) cache.Populate(key, destination);
        return result;
    }

    internal static void PrepareCheckAllRuntime(string root, IEnumerable<CheckBuild> builds, string? manifest = null)
    {
        if (manifest != null)
        {
            var paths = File.ReadAllLines(manifest).Where(line => line.Length != 0).Select(line => line.Split('\t')).ToArray();
            if (paths.Any(row => row.Length != 5)) throw new ReleaseException("Invalid build runtime path manifest: " + manifest);
            foreach (var row in paths.OrderBy(row => row[0].Contains("/Bridge/", StringComparison.OrdinalIgnoreCase) || row[0].Contains("\\Bridge\\", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var project = Path.GetRelativePath(root, row[0]).Replace('\\', '/');
                // The outer CLI still holds its original DLL while waiting for
                // the private driver. Checks use that driver or exact variant
                // assemblies, so its ordinary output must not be overwritten.
                if (project == "build-tools/release/TiaMcp.ReleaseTool.csproj") continue;
                var studio = project is "src/Studio/Gui/TiaOpenness.Gui.csproj" or "tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj" or "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj";
                var destination = row[1].StartsWith("sdk-", StringComparison.Ordinal) || row[1].StartsWith("candidate-", StringComparison.Ordinal)
                    ? Path.Combine(root, "src/Engine/bin/check-all", Path.GetFileNameWithoutExtension(project), row[1], "Release/net48") : row[3];
                CopyRuntime(row[2], destination, studio);
                if (studio)
                {
                    var unit = builds.First(unit => unit.Project == project && unit.Variant == row[1]);
                    var shared = unit.Properties.Any(p => p == "-p:TiaSharedAdapterPaths=true");
                    var bridge = Path.Combine(root, "src/Studio/Bridge/bin/Release", shared ? "shared-adapter" : "", "net48");
                    CopyRuntime(bridge, Path.Combine(destination, "bridge"));
                    CopyRuntime(bridge, Path.Combine(row[2], "bridge"));
                    if (!File.Exists(Path.Combine(bridge, "Newtonsoft.Json.dll"))) File.Delete(Path.Combine(destination, "bridge/Newtonsoft.Json.dll"));
                }
            }
            return;
        }
        // Compile each variant independently; runtime discovery and source-checkout
        // tests retain their established layout. Identical files keep their timestamps.
        foreach (var worker in builds.Where(unit => unit.Project == "src/PlcWorker/TiaMcp.PlcWorker.csproj" && unit.Variant.Length != 0))
        {
            var key = worker.Properties.Single(p => p.StartsWith("-p:TiaReleaseKey=", StringComparison.Ordinal))[17..];
            CopyRuntime(Path.Combine(root, "src/PlcWorker/bin/check-all/TiaMcp.PlcWorker", worker.Variant, "Release/net48"),
                Path.Combine(root, "src/PlcWorker/bin", key, "Release/net48"));
        }
        foreach (var studio in builds.Where(unit => unit.Project is "src/Studio/Gui/TiaOpenness.Gui.csproj" or
            "tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj" or "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj"))
        {
            var mode = studio.Properties.SingleOrDefault(p => p.StartsWith("-p:TiaSharedAdapterPaths=", StringComparison.Ordinal));
            if (mode == null) continue;
            var suffix = mode.EndsWith("=true", StringComparison.Ordinal) ? "Release/shared-adapter" : "Release";
            var directory = Path.Combine(root, Path.GetDirectoryName(studio.Project)!);
            var destination = Path.Combine(directory, "bin", suffix, "net10.0-windows");
            CopyRuntime(Path.Combine(directory, "bin/check-all", Path.GetFileNameWithoutExtension(studio.Project), studio.Variant, "Release/net10.0-windows"), destination, excludeBridge: true);
            var bridge = Path.Combine(root, "src/Studio/Bridge/bin/check-all/TiaOpenness.Bridge", studio.Variant, "Release/net48");
            var legacyBridge = Path.Combine(root, "src/Studio/Bridge/bin", suffix, "net48");
            CopyRuntime(bridge, legacyBridge);
            // The ordinary copy targets read the legacy bridge directory. Replace
            // that payload with this variant's output, including on a fresh checkout.
            CopyRuntime(bridge, Path.Combine(destination, "bridge"));
            if (!File.Exists(Path.Combine(bridge, "Newtonsoft.Json.dll")))
            {
                File.Delete(Path.Combine(destination, "bridge/Newtonsoft.Json.dll")); File.Delete(Path.Combine(legacyBridge, "Newtonsoft.Json.dll"));
            }
        }
    }

    private static void CopyRuntime(string source, string destination, bool excludeBridge = false)
    {
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, path);
            if (excludeBridge && relative.StartsWith("bridge" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative);
            if (File.Exists(target) && ReleaseRecords.HashFile(path) == ReleaseRecords.HashFile(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target, true);
        }
    }

}
