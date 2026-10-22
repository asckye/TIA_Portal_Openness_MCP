using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal sealed record TestPackageStamp(DateTime StartedUtc, IReadOnlyDictionary<string, string> Files);

// package-test: an installable test package from the current checkout, for people to try a build without a release. It is never
// published, tagged or written into release records; the tracked records ship as they are in the working tree and bind nothing.
// -Build first builds the product outputs incrementally with the same lower-level steps the release chain uses, without tests and
// without rewriting a tracked file. The package-mode bundle checks that need no records then run on the staged tree.
internal static partial class ReleaseCommands
{
    internal const string TestPackageMarker = "TEST-PACKAGE.txt";
    internal const string TestPackageNotice = "Test package - not a release; no release records are bound";
    internal static readonly string[] TestPackageKeys = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"];
    // Built outside runtime/ and untracked; the updater comes from ReleaseRecords.UpdaterOutputs.
    internal static readonly string[] TestPackageBuiltFiles = ["TiaOpenness.exe"];
    // A superset of what the product outputs are compiled from; bin/obj folders and Markdown are not inputs.
    internal static readonly string[] TestPackageSourceRoots =
        ["src", "third_party/SiemensOpcUaModelled", "build-tools/native-call-weaver", "Version.props", "Directory.Build.props", "global.json"];
    private const string TestPackageWork = "bin-build/test-package-work";

    private static int PackageTest(Options options)
    {
        EnsureWindows("package-test");
        var total = Stopwatch.StartNew();
        var output = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/test-packages")));
        var fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !output.StartsWith(Path.Combine(fullRoot, "bin-build") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("-OutputDirectory inside the repository must be under bin-build");
        var work = Path.Combine(Root, TestPackageWork);
        var label = ReleaseVersions.ReadVersionProps(Root).Label;
        var build = options.Has("Build");
        var buildStarted = DateTime.UtcNow;
        if (build)
        {
            var before = TrackedFileState(Root);
            BuildTestPackageProducts(options, work, buildStarted);
            var changed = ChangedTrackedFiles(before, TrackedFileState(Root));
            // The build must never rewrite a tracked file, and a tree edited while it ran would not match the package.
            if (changed.Count != 0)
                throw new ReleaseException("Tracked files changed while package-test -Build ran; no package was made. Review them (and restore any the build rewrote): " + string.Join(", ", changed));
            Console.WriteLine($"Product build finished in {total.Elapsed.TotalSeconds:F0} s");
        }
        var problems = TestPackageOutputProblems(Root, ReadTestPackageStamp(work));
        if (problems.Count != 0)
            throw new ReleaseException("package-test refused: product outputs are missing or older than their sources. Run package-test -Build (or the release build) first." +
                Environment.NewLine + string.Join(Environment.NewLine, problems.Select(problem => "  " + problem)));

        var commit = GitText(Root, "rev-parse", "HEAD");
        var dirty = GitText(Root, "status", "--porcelain").Length != 0;
        var name = TestPackageName(label, DateTime.Now, commit[..Math.Min(8, commit.Length)], dirty);
        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, name + ".zip");
        var digestPath = Path.Combine(output, name + ".sha256");
        if (File.Exists(archive) || File.Exists(digestPath)) throw new ReleaseException("Test package already exists (packages are never overwritten): " + archive);

        var rules = DeliveryRules.Load(Root);
        var contents = TestPackageContents(Root, Repository.GitFiles(Root), rules.Delivered);
        var stage = Path.Combine(work, "stage-" + Guid.NewGuid().ToString("N")[..8], name);
        var step = Stopwatch.StartNew();
        foreach (var (relative, source) in contents)
        {
            var target = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        Console.WriteLine($"Staged {contents.Count} delivery files in {step.Elapsed.TotalSeconds:F0} s: {stage}");

        step.Restart();
        var errors = TestPackageBundleErrors(stage, rules);
        if (errors.Count != 0)
        {
            foreach (var error in errors) Console.WriteLine("[FAIL] " + error);
            throw new ReleaseException($"Test package checks failed ({errors.Count} issue(s)); the staged tree is kept: {stage}");
        }
        Console.WriteLine($"[ OK ] package-mode bundle checks without records ({step.Elapsed.TotalSeconds:F0} s)");

        step.Restart();
        var marker = TestPackageMarkerText(label, commit, dirty, DateTimeOffset.UtcNow, build ? new DateTimeOffset(buildStarted) : null);
        WriteTestPackageArchive(archive, name, contents.Keys.Select(relative => (relative, Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)))), marker);
        var digest = ReleaseRecords.HashFile(archive);
        File.WriteAllText(digestPath, digest + "  " + Path.GetFileName(archive) + "\n", Encoding.ASCII);
        Directory.Delete(Path.GetDirectoryName(stage)!, true);
        Console.WriteLine($"Archive written and verified in {step.Elapsed.TotalSeconds:F0} s");
        Console.WriteLine(TestPackageNotice + ".");
        Console.WriteLine("Package: " + archive);
        Console.WriteLine($"Files:   {contents.Count + 1} (including {TestPackageMarker})");
        Console.WriteLine($"Size:    {new FileInfo(archive).Length} bytes ({new FileInfo(archive).Length / 1048576.0:F1} MiB)");
        Console.WriteLine("SHA-256: " + digest);
        Console.WriteLine($"Total:   {total.Elapsed.TotalSeconds:F0} s");
        return 0;
    }

    // The product outputs a test package needs; each must exist and be newer than every product source.
    internal static IReadOnlyList<string> TestPackageProducts() =>
    [
        .. TestPackageKeys.SelectMany(key => new[]
        {
            $"runtime/v{key}/TiaMcp.FoundationHost.exe", $"runtime/v{key}/TiaMcp.FoundationHost.dll",
            $"runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe", $"runtime/v{key}/worker/TiaMcp.Adapter.{key}.dll"
        }),
        "runtime/studio/TiaOpenness.exe", "runtime/studio/TiaOpenness.dll",
        "runtime/tools/TiaMcp.WriteGuard.exe", "runtime/tools/TiaMcp.PluginLauncher.exe",
        .. TestPackageBuiltFiles,
        .. ReleaseRecords.UpdaterOutputs.Values.Order(StringComparer.Ordinal)
    ];

    internal static IReadOnlyList<string> TestPackageOutputProblems(string root, TestPackageStamp? stamp)
    {
        var problems = new List<string>();
        var newest = NewestTestPackageSource(root);
        foreach (var relative in TestPackageProducts())
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { problems.Add("missing: " + relative); continue; }
            var built = File.GetLastWriteTimeUtc(path);
            // Incremental builds and copies keep older timestamps on unchanged outputs; the last -Build vouches for the bytes it produced.
            if (stamp is not null && stamp.StartedUtc > built && stamp.Files.TryGetValue(relative, out var sha256) && sha256 == ReleaseRecords.HashFile(path))
                built = stamp.StartedUtc;
            if (newest is { } source && source.Time > built)
                problems.Add($"stale: {relative} ({built.ToLocalTime():yyyy-MM-dd HH:mm:ss}) is older than {source.Path} ({source.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        }
        problems.AddRange(BundledRuntimeProblems(root));
        return problems;
    }

    internal static (string Path, DateTime Time)? NewestTestPackageSource(string root)
    {
        (string Path, DateTime Time)? newest = null;
        void Consider(string path)
        {
            if (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) return;
            var time = File.GetLastWriteTimeUtc(path);
            if (newest is null || time > newest.Value.Time) newest = (Path.GetRelativePath(root, path).Replace('\\', '/'), time);
        }
        void Walk(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory)) Consider(file);
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not ("bin" or "obj" or "bin-v20" or "obj-v20" or ".vs")) Walk(child);
        }
        foreach (var relative in TestPackageSourceRoots)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) Consider(path);
            else if (Directory.Exists(path)) Walk(path);
        }
        return newest;
    }

    // The pinned bundled .NET layout (get-bundled-dotnet); its files keep the archive timestamps, so only the version is compared.
    internal static IReadOnlyList<string> BundledRuntimeProblems(string root)
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "build-tools/bundled-dotnet.json")));
        var version = pin.RootElement.GetProperty("version").GetString()!;
        var target = pin.RootElement.GetProperty("target").GetString()!;
        var problems = new List<string>();
        foreach (var framework in pin.RootElement.GetProperty("frameworks").EnumerateArray().Select(value => value.GetString()!))
            if (!Directory.Exists(Path.Combine(root, target, "shared", framework, version))) problems.Add($"missing: {target}/shared/{framework}/{version} (bundled .NET)");
        if (!File.Exists(Path.Combine(root, target, "host", "fxr", version, "hostfxr.dll"))) problems.Add($"missing: {target}/host/fxr/{version}/hostfxr.dll (bundled .NET)");
        return problems;
    }

    // Tracked delivery files as in the working tree, the built runtime files on disk and the updater build output, filtered by
    // scripts/operations/delivery-files.json. The release-only IL verifier, symbols and the Siemens PublicAPI never enter a package;
    // the rules already exclude the retired Siemens.Collaboration assemblies, while the redistributable Siemens.Simatic.S7.Webserver.API
    // dependency and the Siemens licence texts ship as in a release.
    internal static SortedDictionary<string, string> TestPackageContents(string root, IEnumerable<string> tracked, Func<string, bool> delivered)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string relative, string source)
        {
            if (delivered(relative) && !TestPackageExcluded(relative) && File.Exists(source)) files[relative] = source;
        }
        foreach (var relative in tracked) Add(relative, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var runtime = Path.Combine(root, "runtime");
        if (Directory.Exists(runtime))
            foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
                Add(Path.GetRelativePath(root, file).Replace('\\', '/'), file);
        foreach (var relative in TestPackageBuiltFiles) Add(relative, Path.Combine(root, relative));
        foreach (var (relative, built) in ReleaseRecords.UpdaterOutputs) Add(relative, Path.Combine(root, built.Replace('/', Path.DirectorySeparatorChar)));
        return files;
    }

    internal static bool TestPackageExcluded(string relative) =>
        relative.StartsWith("runtime/verification/", StringComparison.Ordinal) ||
        relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        relative.Split('/')[^1].StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase);

    internal static string TestPackageName(string label, DateTime localTime, string shortCommit, bool dirty) =>
        $"TIA_MCP_TestPackage_v{label}_{localTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}_{shortCommit}{(dirty ? "-dirty" : "")}";

    internal static string TestPackageMarkerText(string label, string commit, bool dirty, DateTimeOffset packaged, DateTimeOffset? buildStarted) =>
        TestPackageNotice + ".\n" +
        "Do not publish it or attach it to a GitHub release; releases come from the release command.\n\n" +
        $"Version label: {label}\n" +
        $"Commit: {commit}\n" +
        $"Working tree: {(dirty ? "dirty (uncommitted changes are included)" : "clean")}\n" +
        $"Build time: {packaged.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\n" +
        (buildStarted is { } started
            ? $"Product outputs: built incrementally by package-test -Build, started {started.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\n"
            : "Product outputs: packaged as already built in the checkout\n") +
        "Tests: not run by package-test\n";

    // Package-mode validate-bundle needs the release records to bind the runtime binaries. A test package binds nothing, so the
    // record-free part runs: validate-bundle -PackageMode -NoBinaries (delivery set, resources, links, required JSON, Openness
    // delivery policy) plus every binary rule that needs no record: required delivery paths and entry points, bundle resources,
    // test-policy assemblies, the Openness group runtime of each entry assembly and the dependency closure.
    internal static List<string> TestPackageBundleErrors(string stage, DeliveryRules rules)
    {
        var errors = new List<string>();
        var validation = RunTestPackageValidation(stage);
        if (validation.ExitCode != 0)
            errors.AddRange((validation.StandardOutput + validation.StandardError).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("[FAIL]", StringComparison.Ordinal)).Select(line => "validate-bundle: " + line[6..].Trim())
                .DefaultIfEmpty("validate-bundle -PackageMode -NoBinaries exited " + validation.ExitCode));
        foreach (var path in TestPackageRequiredPaths(stage, rules))
            if (!File.Exists(Path.Combine(stage, path)) && !Directory.Exists(Path.Combine(stage, path))) errors.Add("Missing delivery path: " + path);
        errors.AddRange(BundleManifestRequirements.MissingBundleResources(stage, package: true, noBinaries: false).Select(path => "Missing bundle resource: " + path));
        errors.AddRange(TestPolicyAssemblyErrors(stage));
        if (Directory.Exists(Path.Combine(stage, "runtime")))
            foreach (var entry in Directory.EnumerateFiles(Path.Combine(stage, "runtime"), "*", SearchOption.AllDirectories).Where(OpennessGroupRuntime.IsEntryAssembly))
                errors.AddRange(OpennessGroupRuntime.Inspect(entry).Errors);
        errors.AddRange(PackageDependencies.Check(stage, rules.Delivered));
        var layout = RunStaticCaptured("check-bundle-layout", []);
        if (layout.ExitCode != 0) errors.Add("check-bundle-layout: " + Tail(layout.StandardOutput + layout.StandardError));
        return errors;
    }

    private static CommandResult RunTestPackageValidation(string stage)
    {
        lock (StaticOutputLock)
        {
            var stdout = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                var code = ValidateBundle(Options.Parse(["-BundleRoot", stage, "-PackageMode", "-NoBinaries"], new HashSet<string>(["PackageMode", "NoBinaries"], StringComparer.OrdinalIgnoreCase)));
                return new CommandResult(code, output.ToString(), "");
            }
            catch (ReleaseException ex) { return new CommandResult(ex.ExitCode, output.ToString(), ex.Message); }
            finally { Console.SetOut(stdout); }
        }
    }

    // Every path the delivery rules, the package manifest and the project blueprint name, binaries included.
    internal static IReadOnlyList<string> TestPackageRequiredPaths(string stage, DeliveryRules rules)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in DeliveryRules.Strings(rules.Data["include"]!["files"]).Concat(DeliveryRules.Strings(rules.Data["requiredFiles"])))
            paths.Add(path);
        foreach (var prefix in DeliveryRules.Strings(rules.Data["include"]!["prefixes"])) paths.Add(prefix.TrimEnd('/'));
        foreach (var path in BundleManifestRequirements.GuiRequiredPaths(stage).Where(rules.Delivered)) paths.Add(path);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, "manifest/package-manifest.json")))!;
        foreach (var (key, value) in manifest["entrypoints"]!.AsObject())
            if (key is not ("mcpServerArgs" or "bundleValidationScript")) paths.Add(value!.GetValue<string>());
        paths.Add(manifest["cli"]!["exe"]!.GetValue<string>());
        var blueprint = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, "templates/project-blueprints/full_plc_hmi_project.json")))!;
        foreach (var path in DeliveryRules.Strings(blueprint["requiredBundleFiles"])) paths.Add(path);
        return paths.ToArray();
    }

    // A test-only behavior policy build must never reach a bundle (validate-bundle -Strict).
    internal static IEnumerable<string> TestPolicyAssemblyErrors(string root) =>
        Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(root, "runtime")) ? Directory.EnumerateFiles(Path.Combine(root, "runtime"), "*.dll", SearchOption.AllDirectories) : [])
            .Where(assembly => Encoding.UTF8.GetString(File.ReadAllBytes(assembly)).Contains("TiaMcpTestPolicy", StringComparison.Ordinal))
            .Select(assembly => "Test-only behavior policy assembly cannot enter a release bundle: " + assembly);

    internal static void WriteTestPackageArchive(string archive, string name, IEnumerable<(string Relative, string Source)> files, string marker)
    {
        var expected = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (relative, source) in files.OrderBy(file => file.Relative, StringComparer.Ordinal))
            {
                zip.CreateEntryFromFile(source, name + "/" + relative, CompressionLevel.Optimal);
                expected[name + "/" + relative] = new FileInfo(source).Length;
            }
            var bytes = new UTF8Encoding(false).GetBytes(marker);
            using (var entry = zip.CreateEntry(name + "/" + TestPackageMarker, CompressionLevel.Optimal).Open()) entry.Write(bytes);
            expected[name + "/" + TestPackageMarker] = bytes.Length;
        }
        // Reading every entry back checks its CRC; names and lengths must equal what was written.
        using var check = ZipFile.OpenRead(archive);
        if (check.Entries.Count != expected.Count) throw new ReleaseException("Test package archive has an unexpected entry count: " + archive);
        foreach (var entry in check.Entries)
        {
            if (!expected.TryGetValue(entry.FullName, out var length) || entry.Length != length) throw new ReleaseException("Test package archive entry differs: " + entry.FullName);
            using var content = entry.Open();
            content.CopyTo(Stream.Null);
        }
    }

    internal static IReadOnlyDictionary<string, (long Length, long Ticks)> TrackedFileState(string root) =>
        Repository.GitFiles(root).Distinct(StringComparer.Ordinal).ToDictionary(relative => relative, relative =>
        {
            var info = new FileInfo(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1L, 0L);
        }, StringComparer.Ordinal);

    internal static IReadOnlyList<string> ChangedTrackedFiles(IReadOnlyDictionary<string, (long Length, long Ticks)> before, IReadOnlyDictionary<string, (long Length, long Ticks)> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var old) || !after.TryGetValue(path, out var now) || old != now)
            .Order(StringComparer.Ordinal).ToArray();

    internal static TestPackageStamp? ReadTestPackageStamp(string work)
    {
        var path = Path.Combine(work, "last-build.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TestPackageStamp>(File.ReadAllText(path)); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("Ignoring unreadable package-test build stamp " + path + ": " + ex.Message);
            return null;
        }
    }

    internal static void WriteTestPackageStamp(string work, string root, DateTime startedUtc)
    {
        var files = TestPackageProducts().Where(relative => File.Exists(Path.Combine(root, relative)))
            .ToDictionary(relative => relative, relative => ReleaseRecords.HashFile(Path.Combine(root, relative)), StringComparer.Ordinal);
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "last-build.json"), JsonSerializer.Serialize(new TestPackageStamp(startedUtc, files), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static string GitText(string root, params string[] arguments)
    {
        var result = ProcessRunner.Run("git", arguments, root);
        ProcessRunner.RequireSuccess(result, "git " + string.Join(' ', arguments));
        return result.StandardOutput.Trim();
    }

    // The product steps of build-multi-version -PrepareOnly and build-release, without their tests, records and evidence: the
    // weaver, bundled .NET, eight PLC workers, Studio with eight adapters and the root launcher (-NoRecord), the Foundation host
    // installed for eight release keys, the Workbench apphost, the write guard, the plugin launcher and the updater.
    private static void BuildTestPackageProducts(Options options, string work, DateTime started)
    {
        var api = Path.GetFullPath(options.Get("PublicApiRoot") ?? Path.Combine(Root, "sdk"));
        if (!Directory.Exists(api)) throw new ReleaseException("package-test -Build needs the eight PublicAPI copies; pass -PublicApiRoot <SDK-root> (" + api + " does not exist)");
        var dotnet = options.Get("Dotnet", Dotnet);
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (!string.IsNullOrWhiteSpace(nuget))
        {
            nuget = Path.GetFullPath(nuget);
            if (!File.Exists(nuget)) throw new ReleaseException("NuGet config does not exist: " + nuget);
            Environment.SetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG", nuget);
            Environment.SetEnvironmentVariable("RestoreConfigFile", nuget);
        }
        if (options.Get("BuildCacheDirectory") is { } cache) Environment.SetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DIRECTORY", Path.GetFullPath(cache));
        Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");
        Environment.SetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");
        Environment.SetEnvironmentVariable("NuGetAudit", "false");
        var logs = Path.Combine(work, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(logs);
        var runTemp = Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runTemp);
        var release = ReleaseVersions.ReadVersionProps(Root);
        var stopwatch = Stopwatch.StartNew();
        void Step(string name, Action action)
        {
            stopwatch.Restart();
            Console.WriteLine("BUILD " + name);
            action();
            Console.WriteLine($"  done in {stopwatch.Elapsed.TotalSeconds:F0} s");
        }
        void Self(string description, params string[] arguments)
        {
            if (RunSelfCommand(arguments, description) != 0) throw new ReleaseException(description + " failed; see the output above and " + logs);
        }
        void Build(string description, string log, List<string> arguments)
        {
            AddNugetConfig(arguments, nuget);
            ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, arguments, logs, log, nuget), description + " failed; see " + Path.Combine(logs, log));
        }
        string[] NuGetArguments() => string.IsNullOrWhiteSpace(nuget) ? [] : ["-NuGetConfig", nuget];

        Step("native call weaver", () => ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet,
            ["build", "build-tools/native-call-weaver/NativeCallWeaver.csproj", "-c", "Release", "-v:q", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false"],
            logs, "weaver.log", nuget), "Native weaver build failed; see " + Path.Combine(logs, "weaver.log")));
        Step("bundled .NET runtime", () => Self("Bundled .NET runtime layout", "get-bundled-dotnet", "-Offline", "-Cache", BundledRuntimeCache()));
        Step("eight PLC workers", () => Self("PLC adapter worker builds", ["build-plc-workers", "-PublicApiRoot", api, "-Dotnet", dotnet,
            "-EvidenceDirectory", Path.Combine(logs, "adapters"), "-UseReferenceAssemblyPackage", .. NuGetArguments()]));
        Step("Studio, eight Openness adapters and the root launcher", () => Self("Studio build", ["build-studio", "-PublicApiRoot", api, "-Dotnet", dotnet,
            "-TiaSharedAdapterPaths", "false", .. TestPackageKeys.SelectMany(key => new[] { "-ReleaseKeys", key }), "-NoRecord", .. NuGetArguments()]));
        Step("Foundation host for eight release keys", () =>
        {
            var publish = Path.Combine(work, "foundation-host");
            Build("Foundation host publication", "host.log", ["publish", Path.Combine(Root, "src/FoundationHost/TiaMcp.FoundationHost.csproj"), "-c", "Release", "-o", publish, "-v:q",
                "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", $"-p:Version={release.Label}", $"-p:FileVersion={release.FileVersion}"]);
            // The same installation as build-multi-version.
            foreach (var key in TestPackageKeys)
            {
                var runtime = Path.Combine(Root, $"runtime/v{key}");
                if (key is "20" or "21") CleanLegacyEngineLayout(key);
                Directory.CreateDirectory(Path.Combine(runtime, "worker"));
                foreach (var file in Directory.EnumerateFiles(publish).Where(file => new[] { ".exe", ".dll", ".config", ".json" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)), true);
                File.WriteAllText(Path.Combine(runtime, "release-key.txt"), key, new UTF8Encoding(false));
                InstallReleasePlcWorker(Root, key);
            }
        });
        Step("Workbench", () =>
        {
            var studioBuild = Path.Combine(Root, "src/Studio/Gui/bin/Release/net10.0-windows");
            if (!Directory.Exists(studioBuild)) throw new ReleaseException("Studio GUI build output missing: " + studioBuild);
            var studioOutput = Path.Combine(Root, "runtime/studio");
            CopyDirectoryContents(studioBuild, studioOutput);
            var apphost = Path.Combine(work, "studio-apphost");
            Build("Studio apphost publication", "studio-apphost.log", ["publish", "src/Studio/Gui/TiaOpenness.Gui.csproj", "-c", "Release", "-o", apphost, "-v:q", "-p:TiaSharedAdapterPaths=false"]);
            File.Copy(Path.Combine(apphost, "TiaOpenness.exe"), Path.Combine(studioOutput, "TiaOpenness.exe"), true);
        });
        Step("write guard, plugin launcher and updater", () =>
        {
            var tools = Path.Combine(Root, "runtime/tools");
            Build("write-guard publish", "write-guard-publish.log", ["publish", Path.Combine(Root, "src/Tools/WriteGuard/TiaMcp.WriteGuard.csproj"), "-c", "Release", "-o", tools, "-v:q", "-p:PublishAot=false", "-p:UseAppHost=true"]);
            Build("plugin launcher publish", "plugin-launcher-publish.log", ["publish", Path.Combine(Root, "src/Tools/PluginLauncher/TiaMcp.PluginLauncher.csproj"), "-c", "Release", "-o", tools, "-v:q", "-p:UseAppHost=true"]);
            Build("Updater build", "build-updater.log", ["build", Path.Combine(Root, "src/Updater/TiaMcp.Updater.csproj"), "-c", "Release", "-f", "net48", "-v:q"]);
        });
        Step("bundled runtime use and PublicAPI absence", () =>
        {
            WithIsolatedHost(runTemp, "foundation-bundled-runtime", approvalEnabled: false, _ =>
            {
                AssertBundledRuntime(Path.Combine(Root, "runtime/v14sp1/TiaMcp.FoundationHost.exe"), ["--catalog"], "foundation-host", logs);
                return 0;
            });
            WithIsolatedHost(runTemp, "studio-bundled-runtime", approvalEnabled: false, _ =>
            {
                AssertBundledRuntime(Path.Combine(Root, "runtime/studio/TiaOpenness.exe"), ["--network", "127.0.0.1", "not-a-port", "S-1-5-18"], "studio", logs);
                return 0;
            });
            if (Directory.EnumerateFiles(Path.Combine(Root, "runtime"), "Siemens.Engineering*.dll", SearchOption.AllDirectories).Any())
                throw new ReleaseException("Siemens PublicAPI redistribution is forbidden");
        });
        WriteTestPackageStamp(work, Root, started);
        try { Directory.Delete(runTemp, true); }
        catch (IOException ex) { Console.Error.WriteLine("WARNING package-test temp data still in use (kept): " + runTemp + " (" + ex.Message + ")"); }
        Console.WriteLine("Build logs: " + logs);
    }

    // get-bundled-dotnet reads the pinned archives offline from this checkout's cache, or else from the main repository's cache that
    // a linked worktree shares (the same folder the build cache defaults to); nothing is downloaded.
    private static string BundledRuntimeCache()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "build-tools/bundled-dotnet.json")));
        var folder = "dotnet-" + pin.RootElement.GetProperty("version").GetString();
        var names = pin.RootElement.GetProperty("archives").EnumerateArray().Select(archive => archive.GetProperty("name").GetString()!).ToArray();
        var local = Path.Combine(Root, "bin-build/cache", folder);
        if (names.All(name => File.Exists(Path.Combine(local, name)))) return local;
        var shared = Path.Combine(DefaultBuildCache(), folder);
        return names.All(name => File.Exists(Path.Combine(shared, name))) ? shared : local;
    }
}
