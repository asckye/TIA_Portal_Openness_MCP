using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal sealed record PrepareContext(string Root, string Version, string PublicApiRoot, string? NuGetConfig,
    string Python, string CompanionPython, string OutputDirectory, DateOnly ReleaseDate, int MaxParallelism = 6, int TestParallelism = 0)
{
    internal string PackageName => $"TIA_MCP_Delivery_v{Version}_" + ReleaseDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    internal string StageDirectory => Path.Combine(OutputDirectory, PackageName);
    internal string Logs => Path.Combine(OutputDirectory, "logs");
}

internal sealed record PreparedPackage(string ZipPath, string Sha256, int FileCount, long Size, string StageDirectory);

// Local preparation owns build outputs, checks and one immutable archive. Public
// release publication remains a separate command; no check-all scheduler is used.
internal static class Prepare
{
    internal const string InventoryPath = "manifest/multi-version-build.json";
    internal static readonly string[] GeneratedPaths =
        ["manifest/package-manifest.json", "manifest/delivery.json", "manifest/tools-list.json", InventoryPath];

    internal static int RunWithTimings(Func<int> action)
    {
        var timings = new PrepareTimings();
        try { return action(); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (!timings.HasFailures) PrepareTimings.RecordFailure("prepare", error.Message);
            return error is ReleaseException release ? release.ExitCode : 1;
        }
        finally { timings.Dispose(); }
    }

    internal static int Run(string root, Options options) => RunWithTimings(() =>
    {
        var context = Timed("prerequisites", () => CreateContext(root, options));
        var tracked = Repository.GitFiles(root);
        var before = tracked.ToDictionary(path => path, path => FileHash.Sha256(Path.Combine(root, path)), StringComparer.Ordinal);
        return WithRunEnvironment(context, () => RequireUnchanged(root, before, options.Has("BuildOnly")), environment =>
        {
            RequireVulnerabilityScan(root, options, context.Logs);
            new PrepareBuild(context, environment).Run();
            RequireUnchanged(root, before, options.Has("BuildOnly"));
            Timed("stage", () => Stage(context, tracked, environment));
            Timed("delivery-check", () =>
            {
                var errors = DeliveryErrors(context.StageDirectory, DeliveryRules.Load(root));
                if (errors.Count != 0) throw new ReleaseException("Prepared delivery failed:\n  " + string.Join("\n  ", errors));
            });
            RequireUnchanged(root, before, options.Has("BuildOnly"));
            var package = Timed("zip", () => Archive(context));
            Console.WriteLine($"Package: {package.ZipPath}\nSHA-256: {package.Sha256}\nFiles: {package.FileCount}\nSize: {package.Size} bytes");
            return Complete(context, package, options, environment, () => RequireUnchanged(root, before, options.Has("BuildOnly")));
        });
    });

    internal static void RequireVulnerabilityScan(string root, Options options, string logs,
        Func<string[], string, IDictionary<string, string?>, CommandResult>? run = null)
    {
        if (options.Has("BuildOnly")) return;
        var result = Timed("vulnerable-packages", () => VulnerablePackages.Check(root,
            run ?? ((arguments, cwd, environment) => ProcessRunner.Run(ReleaseCommands.Dotnet, arguments, cwd,
                environment, timeoutMilliseconds: 300_000)), Path.Combine(logs, "vulnerable-packages.json")));
        Console.Write(result.StandardOutput);
        if (result.ExitCode != 0)
            throw new ReleaseException(result.StandardOutput.Trim() + "\n" + result.StandardError +
                (result.ExitCode == 2 ? "\nRerun prepare with network access to NuGet." : ""), result.ExitCode);
    }

    internal static int Complete(PrepareContext context, PreparedPackage package, Options options,
        IDictionary<string, string?> environment, Action requireUnchanged,
        Func<PrepareCheckResults>? checks = null, Func<string, Task>? upload = null)
    {
        if (options.Has("BuildOnly")) return 0;
        var results = Timed("checks", checks ?? (() => PrepareChecks.Run(context, package, environment)));
        Timed("clean-tree", requireUnchanged);
        var candidate = Timed("candidate", () => PrepareChecks.WriteCandidate(context, package, results));
        if (!options.Has("NoDraftUpload"))
        {
            try
            {
                Timed("upload-draft", () => (upload?.Invoke(candidate) ?? ReleasePublisher.UploadDraftAsync(context.Root,
                    candidate, ReleaseCommands.PreparedPublishOptions(options))).GetAwaiter().GetResult());
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Draft upload failed; candidate kept: " + error.Message);
                Console.Error.WriteLine($"Run: dotnet run --no-build --project build-tools/release -- upload-draft -Version {context.Version}");
                return 1;
            }
        }
        var next = options.Has("NoDraftUpload") ? "upload-draft" : "publish-prepared";
        Console.WriteLine($"dotnet run --no-build --project build-tools/release -- {next} -Version {context.Version}");
        return 0;
    }

    internal static T WithRunEnvironment<T>(PrepareContext context, Action requireUnchanged,
        Func<IDictionary<string, string?>, T> action, string? tempRoot = null)
    {
        var total = Stopwatch.StartNew();
        tempRoot = Path.GetFullPath(tempRoot ?? Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]));
        bool ownsTemp = false;
        try
        {
            RequireExternalTempRoot(context.Root, tempRoot);
            ResetOutput(context);
            if (Directory.Exists(tempRoot)) throw new ReleaseException("Prepare temp root already exists: " + tempRoot);
            Directory.CreateDirectory(tempRoot);
            ownsTemp = true;
            var result = action(CreateBuildEnvironment(context, tempRoot));
            requireUnchanged();
            return result;
        }
        catch
        {
            try { requireUnchanged(); }
            catch (Exception error) { Console.Error.WriteLine("WARNING " + error.Message); }
            throw;
        }
        finally
        {
            try { if (ownsTemp) Directory.Delete(tempRoot, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("WARNING Prepare temp directory kept: " + tempRoot + ": " + error.Message); }
            Console.WriteLine($"prepare total: {total.Elapsed.TotalSeconds:F1} s");
        }
    }

    internal static void RequireExternalTempRoot(string root, string tempRoot)
    {
        var repository = Path.GetFullPath(root).TrimEnd('\\', '/');
        var temp = Path.GetFullPath(tempRoot).TrimEnd('\\', '/');
        if (temp.Equals(repository, StringComparison.OrdinalIgnoreCase)
            || temp.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("Prepare temp root must be outside the repository: " + tempRoot);
    }

    internal static Dictionary<string, string?> CreateBuildEnvironment(PrepareContext context, string tempRoot)
    {
        RequireExternalTempRoot(context.Root, tempRoot);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = Path.Combine(context.OutputDirectory, "analyzers"),
            ["TIA_MCP_PLC_TOOLS_PYTHON"] = context.CompanionPython,
            ["TIA_MCP_DECODER_PYTHON"] = context.Python,
            ["DOTNET_CLI_HOME"] = tempRoot, ["TEMP"] = tempRoot, ["TMP"] = tempRoot, ["TMPDIR"] = tempRoot,
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false", ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false", ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["NuGetAudit"] = "false", ["UseSharedCompilation"] = "false",
            ["NUGET_PACKAGES"] = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages"),
            ["RestoreConfigFile"] = context.NuGetConfig ?? Path.Combine(context.OutputDirectory, "offline.nuget.config")
        };
    }

    internal static void ResetOutput(PrepareContext context)
    {
        PrepareBuild.RequireOwnedTree(context.Root, context.OutputDirectory);
        if (Directory.Exists(context.OutputDirectory)) Directory.Delete(context.OutputDirectory, true);
        Directory.CreateDirectory(context.OutputDirectory);
    }

    internal static T Timed<T>(string name, Func<T> action)
    {
        var clock = Stopwatch.StartNew();
        Console.WriteLine("PREPARE " + name);
        try { return action(); }
        catch (Exception error)
        {
            PrepareTimings.RecordFailure(name, error.Message);
            throw;
        }
        finally
        {
            PrepareTimings.Record(name, clock.Elapsed.TotalSeconds);
            Console.WriteLine($"PREPARE {name}: {clock.Elapsed.TotalSeconds:F1} s");
        }
    }

    internal static void Timed(string name, Action action) => Timed(name, () => { action(); return 0; });

    internal static PrepareContext CreateContext(string root, Options options, SuiteProcess? companionProbe = null)
    {
        root = Path.GetFullPath(root);
        var version = ReleaseVersions.Require(options.Get("Version"), "prepare requires -Version X.Y.Z[-beta.N|-rc.N]").Label;
        RequireRepository(Git(root, "status", "--porcelain=v1", "--untracked-files=all"), Git(root, "branch", "--show-current"), options.Has("BuildOnly"));
        GitWorkingBytes.RequireNormalizedWorkingBytes(root, "git");
        var date = RequireVersion(root, version);
        RequireNotes(root, version);
        var api = Path.GetFullPath(options.Get("PublicApiRoot") ?? Path.Combine(root, "sdk"));
        RequirePublicApi(root, api);
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG")
            ?? Environment.GetEnvironmentVariable("RestoreConfigFile");
        if (nuget != null)
        {
            nuget = Path.GetFullPath(nuget);
            if (!File.Exists(nuget)) throw new ReleaseException("NuGet config is missing: " + nuget);
            if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error);
        }
        var python = ReleaseCommands.DecoderPythonOptions(options).Get("Python")!;
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python))
            throw new ReleaseException("Decoder Python is missing; pass -Python <absolute-python.exe> or set TIA_MCP_DECODER_PYTHON");
        var companion = ReleaseCommands.ReleaseCompanionPython(options, root);
        ReleasePrerequisites.RequireEcosystemPython(companion, Path.Combine(root, "scripts/ecosystem/plc_tools_bridge.py"), python, companionProbe);
        var output = Path.Combine(root, "bin-build/prepared", version);
        PrepareBuild.RequireOwnedTree(root, output);
        var parallelism = 6;
        if (options.Get("MaxParallelism") is { } value && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parallelism) || parallelism < 1))
            throw new ReleaseException("MaxParallelism must be a positive integer");
        return new(root, version, api, nuget, python, companion, output, date, parallelism, options.Has("MaxParallelism") ? parallelism : Math.Max(1, Environment.ProcessorCount - 4));
    }

    internal static void RequireRepository(string status, string branch, bool buildOnly = false)
    {
        if (!string.IsNullOrWhiteSpace(status)) throw new ReleaseException("prepare requires a clean working tree; commit or preserve changes before preparing");
        if (!buildOnly && branch.Trim() != "master") throw new ReleaseException("prepare requires master; current branch is " + branch.Trim());
    }

    internal static DateOnly RequireVersion(string root, string version)
    {
        if (ReleaseVersions.ReadLabel(root) != version) throw new ReleaseException("Version.props does not match -Version " + version);
        var plugin = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".claude-plugin/plugin.json")))!;
        if ((string?)plugin["version"] != version) throw new ReleaseException("plugin.json does not match -Version " + version);
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".claude-plugin/marketplace.json")))!;
        var plugins = marketplace["plugins"]!.AsArray();
        if (plugins.Count == 0 || plugins.Any(item => (string?)item?["version"] != version))
            throw new ReleaseException("marketplace.json does not match -Version " + version);
        var entry = Regex.Match(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")),
            @"(?m)^## \[(?<v>" + ReleaseVersions.VersionPattern + @")\] - (?<date>[^\r\n]+)");
        if (!entry.Success || entry.Groups["v"].Value != version)
            throw new ReleaseException("Newest CHANGELOG release does not match -Version " + version);
        if (!DateOnly.TryParseExact(entry.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ReleaseException("Newest CHANGELOG release must have a real ISO date (yyyy-MM-dd)");
        return date;
    }

    internal static void RequireNotes(string root, string version)
    {
        if (!File.Exists(Path.Combine(root, PrereleaseNotes.NotesPath(version))))
            throw new ReleaseException(PrereleaseNotes.NotesPath(version) + " is missing");
        if (ReleaseVersions.Require(version, "Invalid release version").IsPrerelease && PrereleaseNotes.CurrentError(root, version) is { } error)
            throw new ReleaseException(error);
        if (!File.ReadAllText(Path.Combine(root, "docs/README.md")).Contains($"[当前版本说明](releases/v{version}.md)", StringComparison.Ordinal))
            throw new ReleaseException("docs/README.md current release link is stale");
        if (!Regex.IsMatch(File.ReadAllText(Path.Combine(root, "docs/plan/plan.md")), "(?m)^# .*" + Regex.Escape(version)))
            throw new ReleaseException("Roadmap title is stale");
    }

    internal static void RequirePublicApi(string root, string apiRoot)
    {
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            var api = Path.Combine(apiRoot, ReleaseCommands.PlcWorkerApi(key).Directory);
            var folder = PrepareBuild.AdapterFolder(key);
            var release = XDocument.Load(Path.Combine(root, $"src/Adapters/{folder}/Release.props"));
            foreach (var item in release.Descendants("AdapterApi"))
            {
                var name = ((string)item.Attribute("Include")!).Replace('\\', '/').Split('/')[^1];
                if (!File.Exists(Path.Combine(api, name))) throw new ReleaseException($"PublicAPI {key} is missing {name}: {api}");
            }
        }
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = ProcessRunner.Run("git", arguments, root);
        ProcessRunner.RequireSuccess(result, "Read local Git state");
        return result.StandardOutput.Trim();
    }

    private static void RequireUnchanged(string root, IReadOnlyDictionary<string, string> before, bool buildOnly)
    {
        var changed = before.Where(row => !File.Exists(Path.Combine(root, row.Key)) || FileHash.Sha256(Path.Combine(root, row.Key)) != row.Value)
            .Select(row => row.Key).ToArray();
        if (changed.Length != 0) throw new ReleaseException("Tracked bytes changed during prepare: " + string.Join(", ", changed));
        RequireRepository(Git(root, "status", "--porcelain=v1", "--untracked-files=all"), Git(root, "branch", "--show-current"), buildOnly);
    }

    internal static SortedDictionary<string, string> Contents(string root, IEnumerable<string> tracked, DeliveryRules rules)
    {
        var files = ReleaseCommands.Contents(root, tracked, rules.Delivered);
        foreach (var path in files.Keys.Where(path => GeneratedPaths.Contains(path) || path == "TEST-PACKAGE.txt").ToArray())
            files.Remove(path);
        return files;
    }

    internal static void Stage(PrepareContext context, IEnumerable<string> tracked, IDictionary<string, string?>? buildEnvironment = null,
        SuiteProcess? catalogProducer = null, Func<string, string, int>? matrixGenerator = null)
    {
        var stage = context.StageDirectory;
        if (Directory.Exists(stage)) throw new ReleaseException("Stage already exists: " + stage);
        Directory.CreateDirectory(stage);
        foreach (var (relative, source) in Contents(context.Root, tracked, DeliveryRules.Load(context.Root)))
        {
            var target = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        var roster = Path.Combine(stage, "manifest/tools-list.json");
        Directory.CreateDirectory(Path.GetDirectoryName(roster)!);
        // HostOptions requires the bundle marker before it can run --catalog.
        // This staging input is replaced below, never written to the checkout.
        File.Copy(Path.Combine(context.Root, "manifest/package-manifest.json"), Path.Combine(stage, "manifest/package-manifest.json"));
        var host = Path.Combine(stage, "runtime/v21/TiaMcp.FoundationHost.exe");
        string[] arguments = ["--catalog", "--offline", "--profile", "full", "--release-key", "21", "--bundle-root", stage,
            "--catalog-tools-list", roster, "--catalog-package", context.PackageName, "--catalog-generated-at", DateTimeOffset.UtcNow.ToString("o")];
        var environment = buildEnvironment == null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(buildEnvironment);
        environment["TIA_MCP_DATA_DIRECTORY"] = Path.Combine(context.OutputDirectory, "catalog-data");
        var catalog = catalogProducer == null
            ? ProcessRunner.Run(host, arguments, stage, environment, logPath: Path.Combine(context.Logs, "catalog.log"))
            : catalogProducer(host, arguments, stage, environment);
        ProcessRunner.RequireSuccess(catalog, "V21 catalog generation");
        RequireRosterDiscipline(context.Root, stage);
        var matrix = Path.Combine(stage, "docs/reference/tool-matrix.md");
        Directory.CreateDirectory(Path.GetDirectoryName(matrix)!);
        var generated = (matrixGenerator ?? ((tools, output) => ToolCapabilityMatrixGenerator.Generate(tools, output)))(roster, matrix);
        if (generated != 0) throw new ReleaseException("Tool capability matrix generation exited " + generated);
        GenerateManifests(context);
    }

    internal static void RequireRosterDiscipline(string root, string stage)
    {
        static int Undocumented(string path) => JsonNode.Parse(File.ReadAllText(path))?["callDiscipline"]?["parametersUndocumented"]?.GetValue<int>()
            ?? throw new ReleaseException("tools-list callDiscipline.parametersUndocumented missing: " + path);
        var previous = Undocumented(Path.Combine(root, "manifest/tools-list.json"));
        var current = Undocumented(Path.Combine(stage, "manifest/tools-list.json"));
        if (current > previous) throw new ReleaseException($"Parameters without a [Description] went up from {previous} to {current}");
    }

    internal static void GenerateManifests(PrepareContext context)
    {
        var stage = context.StageDirectory;
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(context.Root, "manifest/package-manifest.json")))!.AsObject();
        var roster = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, "manifest/tools-list.json")))!;
        var tools = roster["tools"]!.AsArray();
        if ((int?)roster["toolCount"] != tools.Count) throw new ReleaseException("Generated tools-list roster count is inconsistent");
        manifest["packageName"] = context.PackageName;
        manifest["bundleVersion"] = context.Version;
        manifest["fileVersion"] = ReleaseVersions.FileVersionOf(context.Version);
        manifest["refreshedAt"] = DateTimeOffset.UtcNow.ToString("o");
        manifest["validationRecords"] = new JsonObject { ["delivery"] = "manifest/delivery.json", ["inventory"] = InventoryPath };
        var capabilities = manifest["capabilities"]!.AsObject();
        capabilities["mcpToolCount"] = tools.Count;
        capabilities["mcpToolLayers"] = new JsonObject(tools.GroupBy(tool => (string?)tool?["layer"] ?? "", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => KeyValuePair.Create<string, JsonNode?>(group.Key, JsonValue.Create(group.Count()))));
        var profiles = XDocument.Load(Path.Combine(context.Root, "src/Logic/ModelContextProtocol/ToolProfiles.resx"));
        var profile = JsonNode.Parse(profiles.Descendants("data").Single(row => (string?)row.Attribute("name") == "Catalog").Element("value")!.Value)!;
        var lite = profile["releases"]!["21"]!.AsArray().Where(row => row?["profiles"]!.AsArray().Any(p => (string?)p == "lite") == true)
            .Select(row => (string)row!["currentName"]!).ToArray();
        var available = tools.Select(tool => (string?)tool?["name"]).ToHashSet(StringComparer.Ordinal);
        if (lite.Length == 0 || lite.Distinct(StringComparer.Ordinal).Count() != lite.Length || lite.Any(name => !available.Contains(name)))
            throw new ReleaseException("Generated lite tool roster is missing, duplicated or absent from the V21 catalog");
        capabilities["liteProfile"]!["toolCount"] = lite.Length;
        manifest["validationStatus"] = "Prepared locally; real TIA Portal acceptance not performed";
        WriteJson(Path.Combine(stage, GeneratedPaths[0]), manifest);
        WriteJson(Path.Combine(stage, GeneratedPaths[1]), new { release = context.Version, package = context.PackageName });
        WriteJson(Path.Combine(stage, InventoryPath), new { files = Inventory(stage).Select(row => new { path = row.Key, sha256 = row.Value }) });
    }

    internal static SortedDictionary<string, string> Inventory(string stage) => new(
        Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(path => (Path: Path.GetRelativePath(stage, path).Replace('\\', '/'), Source: path))
            .Where(row => row.Path != InventoryPath).ToDictionary(row => row.Path, row => FileHash.Sha256(row.Source), StringComparer.Ordinal), StringComparer.Ordinal);

    internal static List<string> InventoryErrors(string stage)
    {
        var actual = Inventory(stage);
        var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, InventoryPath)))!["files"]!.AsArray();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var path = (string)row!["path"]!;
            if (!DeliveryRules.PlainPath(path) || !expected.TryAdd(path, (string)row["sha256"]!))
                return ["Invalid or duplicate inventory path: " + path];
        }
        return actual.SequenceEqual(expected.OrderBy(row => row.Key, StringComparer.Ordinal)) ? [] : ["Prepared file inventory differs from stage bytes"];
    }

    internal static List<string> DeliveryErrors(string stage, DeliveryRules rules)
    {
        var errors = new List<string>();
        try { TiaMcp.Logic.ModelContextProtocol.DecoderRuntime.Require(stage); }
        catch (TiaMcp.Logic.ModelContextProtocol.DecoderRuntimeException ex) { errors.Add(ex.Message); }
        foreach (var path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(stage, path).Replace('\\', '/')))
            if (!rules.Delivered(path) || ReleaseCommands.Excluded(path))
                errors.Add("Non-delivery file in stage: " + path);
        foreach (var path in ReleaseCommands.RequiredPaths(stage, rules))
            if (!File.Exists(Path.Combine(stage, path)) && !Directory.Exists(Path.Combine(stage, path))) errors.Add("Missing delivery path: " + path);
        errors.AddRange(BundleManifestRequirements.MissingBundleResources(stage, package: true, noBinaries: false).Select(path => "Missing bundle resource: " + path));
        errors.AddRange(ReleaseCommands.TestPolicyAssemblyErrors(stage));
        errors.AddRange(OpennessGroupRuntime.DeliveryPolicyErrors(rules.Delivered));
        foreach (var entry in Directory.EnumerateFiles(Path.Combine(stage, "runtime"), "*", SearchOption.AllDirectories).Where(OpennessGroupRuntime.IsEntryAssembly))
            errors.AddRange(OpennessGroupRuntime.Inspect(entry).Errors);
        errors.AddRange(PackageDependencies.Check(stage, rules.Delivered));
        errors.AddRange(InventoryErrors(stage));
        return errors;
    }

    internal static PreparedPackage Archive(PrepareContext context)
    {
        var inventoryErrors = InventoryErrors(context.StageDirectory);
        if (inventoryErrors.Count != 0) throw new ReleaseException(string.Join("\n", inventoryErrors));
        var files = Inventory(context.StageDirectory);
        files.Add(InventoryPath, FileHash.Sha256(Path.Combine(context.StageDirectory, InventoryPath)));
        var zipPath = Path.Combine(context.OutputDirectory, context.PackageName + ".zip");
        if (File.Exists(zipPath) || File.Exists(Path.ChangeExtension(zipPath, ".sha256")))
            throw new IOException("Prepared package already exists: " + zipPath);
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var path in files.Keys)
                zip.CreateEntryFromFile(Path.Combine(context.StageDirectory, path), context.PackageName + "/" + path, CompressionLevel.Optimal);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count != files.Count) throw new ReleaseException("ZIP file count differs from stage");
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName[(context.PackageName.Length + 1)..];
                using var stream = entry.Open();
                if (!files.TryGetValue(path, out var hash) || Convert.ToHexStringLower(SHA256.HashData(stream)) != hash)
                    throw new ReleaseException("ZIP bytes differ from stage: " + path);
            }
        }
        var sha = FileHash.Sha256(zipPath);
        File.WriteAllText(Path.ChangeExtension(zipPath, ".sha256"), sha + "  " + Path.GetFileName(zipPath) + "\n", Encoding.ASCII);
        return new(zipPath, sha, files.Count, new FileInfo(zipPath).Length, context.StageDirectory);
    }

    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }
}
