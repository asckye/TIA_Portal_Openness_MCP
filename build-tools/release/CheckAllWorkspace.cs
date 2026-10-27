using System.Diagnostics;
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
        WriteCheckAllSnapshotOwner(original, snapshot);
        PruneCheckAllSnapshots(original, 3, snapshot);
        var history = Path.Combine(original, "bin-build/check-timings");
        CopyCheckAllHistory(history, Path.Combine(snapshot, "bin-build/check-timings"));
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
        try
        {
            var result = ProcessRunner.Run(Dotnet, [typeof(ReleaseCommands).Assembly.Location, "check-all", .. options.Arguments(), "-Worktree=" + snapshot,
                "-OutputDirectory=" + output, "-CacheDirectory=" + Path.GetFullPath(options.Get("CacheDirectory", Path.Combine(original, "bin-build/cache/check-all")))], snapshot,
                new Dictionary<string, string?> { ["TIA_MCP_CLEAN_CHECKOUT"] = snapshot, ["TIA_MCP_CHECK_RUNNER"] = "1", ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = null,
                    ["TIA_MCP_CHECK_ISOLATION_ROOT"] = null, ["TIA_MCP_CHECK_ORIGINAL_ROOT"] = original }, forwardOutput: true);
            return result.ExitCode;
        }
        finally
        {
            CopyCheckAllHistory(Path.Combine(snapshot, "bin-build/check-timings"), history);
            File.WriteAllText(Path.Combine(snapshot, "bin-build/check-all-snapshot.json"), JsonSerializer.Serialize(new { original, completedUtc = DateTime.UtcNow }) + "\n");
            PruneCheckAllSnapshots(original, 3, snapshot);
            Console.WriteLine("Retained detached source checkout: " + snapshot);
        }
    }

    internal static void CopyCheckAllHistory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, path)));
            RequireCheckAllOwnedPath(destination, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target, true);
        }
    }

    internal static void RequireCheckAllOwnedPath(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Path outside this checkout: " + path);
        for (var info = new DirectoryInfo(Path.GetFullPath(path)); info != null && info.FullName.Length >= prefix.Length; info = info.Parent)
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new ReleaseException("Linked check-all path: " + path);
    }

    internal static void WriteCheckAllSnapshotOwner(string original, string snapshot)
    {
        var marker = Path.Combine(snapshot, "bin-build/check-all-snapshot.owner.json");
        RequireCheckAllOwnedPath(Path.Combine(original, "bin-build/c"), marker);
        RequireCheckAllOwnedPath(original, marker);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(marker, JsonSerializer.Serialize(new { original, processId = process.Id,
            processStartUtc = process.StartTime.ToUniversalTime(), startedUtc = DateTime.UtcNow }) + "\n");
    }

    internal static void PruneCheckAllSnapshots(string root, int retained, string? currentSnapshot = null, Func<int, DateTime?>? findProcessStartUtc = null)
    {
        if (retained < 1) throw new ArgumentOutOfRangeException(nameof(retained));
        var parent = Path.Combine(root, "bin-build/c");
        var lookup = findProcessStartUtc ?? CheckAllProcessStartUtc;
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var completed = new List<(string Path, DateTime CompletedUtc)>();
        try
        {
            if (!Directory.Exists(parent)) return;
            RequireCheckAllOwnedPath(root, parent);
            foreach (var path in Directory.GetDirectories(parent))
            {
                try
                {
                    RequireCheckAllOwnedPath(parent, path);
                    var marker = Path.Combine(path, "bin-build/check-all-snapshot.json");
                    var ownerMarker = Path.Combine(path, "bin-build/check-all-snapshot.owner.json");
                    RequireCheckAllOwnedPath(parent, marker); RequireCheckAllOwnedPath(parent, ownerMarker);
                    if (File.Exists(marker))
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(marker));
                        if (document.RootElement.GetProperty("original").GetString() == root)
                            completed.Add((path, File.GetLastWriteTimeUtc(marker)));
                        continue;
                    }
                    if (IsCurrent(path)) continue;
                    if (File.Exists(ownerMarker))
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(ownerMarker));
                        var owner = document.RootElement;
                        if (owner.GetProperty("original").GetString() != root) continue;
                        int processId = owner.GetProperty("processId").GetInt32();
                        var start = owner.GetProperty("processStartUtc").GetDateTime();
                        if (processId <= 0 || start.Kind != DateTimeKind.Utc) throw new ReleaseException("Invalid check-all snapshot owner: " + ownerMarker);
                        if (lookup(processId) == start) continue;
                    }
                    else if (CheckAllSnapshotNewestWriteUtc(parent, path) >= cutoff) continue;
                    Delete(path);
                }
                catch (Exception ex) { Skipped(path, ex); }
            }
            foreach (var snapshot in completed.OrderByDescending(snapshot => snapshot.CompletedUtc).Skip(retained))
            {
                try { Delete(snapshot.Path); }
                catch (Exception ex) { Skipped(snapshot.Path, ex); }
            }
        }
        catch (Exception ex) { Skipped(parent, ex); }

        bool IsCurrent(string path) => currentSnapshot != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentSnapshot), StringComparison.OrdinalIgnoreCase);
        void Delete(string path)
        {
            if (IsCurrent(path)) return;
            CheckAllSnapshotNewestWriteUtc(parent, path);
            Directory.Delete(path, true);
        }
        static void Skipped(string path, Exception ex) => Console.WriteLine("Skipped check-all snapshot pruning: " + path + " (" + ex.Message.Replace('\r', ' ').Replace('\n', ' ') + ")");
    }

    private static DateTime? CheckAllProcessStartUtc(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.StartTime.ToUniversalTime(); }
        catch (ArgumentException) { return null; } // The process no longer exists; other lookup errors retain the snapshot.
    }

    private static DateTime CheckAllSnapshotNewestWriteUtc(string parent, string path)
    {
        RequireCheckAllOwnedPath(parent, path);
        var directory = new DirectoryInfo(path);
        var newest = directory.LastWriteTimeUtc;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            RequireCheckAllOwnedPath(parent, entry.FullName);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new ReleaseException("Linked check-all path: " + entry.FullName);
            var write = entry is DirectoryInfo ? CheckAllSnapshotNewestWriteUtc(parent, entry.FullName) : entry.LastWriteTimeUtc;
            if (write > newest) newest = write;
        }
        return newest;
    }

    private static CommandResult CheckAllCachedBuild(Options options, CheckBuild[] builds, string output, string dotnet, string api, string nuget, int parallelism)
    {
        if (!options.Has("Clean") && !options.Has("Cold"))
        {
            File.WriteAllText(Path.Combine(output, "build-cache.json"), "{\"mode\":\"in-place-incremental\",\"outputsRestored\":0}\n");
            return CheckAllInPlaceBuild(options, builds, output, dotnet, api, nuget, parallelism);
        }
        // Whole-tree keys and source-path-bearing outputs cannot hit across fresh
        // snapshots. Do not spend time scanning/copying an unusable cache.
        var cacheRoot = Path.GetFullPath(options.Get("CacheDirectory", Path.Combine(Root, "bin-build/cache/check-all")));
        RequireCheckAllOwnedPath(Environment.GetEnvironmentVariable("TIA_MCP_CHECK_ORIGINAL_ROOT") ?? Root, cacheRoot);
        BuildOutputCache.PruneRetained(cacheRoot, TimeSpan.FromDays(7), BuildOutputCache.DefaultMaxBytes);
        File.WriteAllText(Path.Combine(output, "build-cache.json"), "{\"mode\":\"population-disabled\",\"outputsRestored\":0,\"reason\":\"Whole-tree cache cannot reuse path-bearing snapshot outputs; compile authoritative graph.\"}\n");
        return CheckAllInPlaceBuild(options, builds, output, dotnet, api, nuget, parallelism);
    }
    private static CommandResult CheckAllInPlaceBuild(Options options, CheckBuild[] builds, string output, string dotnet, string api, string nuget, int parallelism)
    {
        var result = CheckAllBuild.Run(Root, builds, Path.Combine(output, "build"), dotnet, api, nuget, parallelism, options.Has("Cold"));
        if (result.ExitCode == 0) PrepareCheckAllRuntime(Root, builds, Path.Combine(output, "build/runtime-paths.tsv"));
        if (result.ExitCode == 0)
        {
            var scripts = FileBasedPrograms.Build(Root, Path.Combine(output, "build/analyzer-warnings"), dotnet, nuget, parallelism);
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
        var cacheDisabled = Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED") == "1";
        if (!cacheDisabled && cache.Restore(key, destination))
        {
            File.WriteAllText(Path.Combine(output, "build", name + ".log"), "Identical file-app inputs and verified outputs: cache hit " + key + "\n");
            return new(0, name + ": verified file-app cache hit\n", "");
        }
        var result = ProcessRunner.Run(dotnet, ["build", Path.Combine(Root, "scripts/checks/" + name + ".cs"), "-c", "Release",
            "-o", destination, "-p:RestoreConfigFile=" + nuget, "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:UseAppHost=false"], Root,
            logPath: Path.Combine(output, "build", name + ".log"));
        if (result.ExitCode == 0 && !cacheDisabled) cache.Populate(key, destination);
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
                var destination = row[3];
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
