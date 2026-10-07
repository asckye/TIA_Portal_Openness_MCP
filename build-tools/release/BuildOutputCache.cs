using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record BuildCacheEvent(string Unit, string Key, string Result, string Reason);
internal sealed record BuildCacheFiles(string Unit, string Key, DateTime CreatedUtc, DateTime LastHitUtc, ReleaseArtifact[] Files);
internal sealed record BuildCacheEntry(string InputHash, DateTime CreatedUtc, DateTime LastHitUtc, long SizeBytes);
internal sealed record BuildCacheUnit(string Unit, BuildCacheEntry[] Entries);
internal sealed record BuildCacheInfo(string Directory, long MaxBytes, long SizeBytes, BuildCacheUnit[] Units);

internal sealed class BuildOutputCache(string directory, string unit, long maxBytes = BuildOutputCache.DefaultMaxBytes, Func<DateTime>? clock = null)
{
    internal const long DefaultMaxBytes = 10L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal string UnitDirectory => UnitPath(directory, unit);
    private DateTime Now => (clock ?? (() => DateTime.UtcNow))();
    private static string UnitPath(string root, string name)
    {
        var readable = Regex.Replace(name, "[^a-zA-Z0-9._-]+", "-").Trim('-');
        return Path.Combine(root, "unit-" + readable[..Math.Min(80, readable.Length)] + "-" + Key([], [name])[..12]);
    }

    internal static string Key(IEnumerable<ReleaseArtifact> inputs, IEnumerable<string> properties) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 3, inputs = inputs.OrderBy(row => row.Path, StringComparer.Ordinal), properties = properties.ToArray()
        }, JsonOptions)))).ToLowerInvariant();

    internal bool Restore(string key, string output)
    {
        try
        {
            return Locked(directory, () =>
            {
                var entry = EntryPath(key);
                var record = ReadFiles(entry);
                if (record is null || record.Unit != unit || record.Key != key || record.Files is null || record.Files.Length == 0 ||
                    record.Files.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Files.Length) return false;
                var payload = Path.Combine(entry, "payload");
                if (!Inventory(payload).SequenceEqual(record.Files)) return false;
                // Validate every file before changing any destination. Reject path traversal and links.
                foreach (var row in record.Files)
                    if (!SafeRelative(row.Path) || Linked(Path.Combine(payload, row.Path))) return false;
                if (Directory.Exists(output))
                {
                    if (Linked(output)) return false;
                    Directory.Delete(output, true);
                }
                foreach (var row in record.Files)
                {
                    var target = Path.Combine(output, row.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(payload, row.Path), target);
                    if (ReleaseRecords.HashFile(target) != row.Sha256) throw new IOException("Cache copy hash mismatch");
                }
                AtomicJson(Path.Combine(entry, "files.json"), record with { LastHitUtc = Now });
                Prune(directory, maxBytes);
                return true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return false; }
    }

    internal void Populate(string key, string output)
    {
        Locked(directory, () =>
        {
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            Directory.CreateDirectory(UnitDirectory);
            if (Linked(UnitDirectory)) throw new IOException("Linked cache directory");
            var staging = Path.Combine(UnitDirectory, ".pending-" + Guid.NewGuid().ToString("N"));
            var final = EntryPath(key);
            try
            {
                var files = Inventory(output);
                if (files.Length == 0) return 0;
                foreach (var row in files)
                {
                    var source = Path.Combine(output, row.Path);
                    if (!SafeRelative(row.Path) || Linked(source)) throw new IOException("Unsupported cache payload");
                    var target = Path.Combine(staging, "payload", row.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
                if (!Inventory(Path.Combine(staging, "payload")).SequenceEqual(files)) throw new IOException("Build outputs changed during cache population");
                AtomicJson(Path.Combine(staging, "files.json"), new BuildCacheFiles(unit, key, Now, Now, files));
                if (Directory.Exists(final)) DeleteEntry(directory, final);
                Directory.Move(staging, final);
                Prune(directory, maxBytes);
            }
            finally { if (Directory.Exists(staging)) DeleteEntry(directory, staging); }
            return 0;
        });
    }

    internal static BuildCacheInfo Info(string root, long maxBytes = DefaultMaxBytes) => Locked(root, () =>
    {
        var units = ReadUnits(root).Select(row => row.Index).ToArray();
        return new BuildCacheInfo(Path.GetFullPath(root), maxBytes, units.Sum(row => row.Entries.Sum(entry => entry.SizeBytes)), units);
    });

    internal static int Clear(string root, string? selectedUnit = null) => Locked(root, () =>
    {
        var count = 0;
        foreach (var (folder, row) in ReadUnits(root).Where(row => selectedUnit is null || row.Index.Unit == selectedUnit))
        {
            foreach (var entry in row.Entries)
            {
                DeleteEntry(root, Path.Combine(folder, entry.InputHash));
                count++;
            }
            AtomicJson(Path.Combine(folder, "index.json"), row with { Entries = [] });
        }
        return count;
    });

    private string EntryPath(string key)
    {
        if (!ValidKey(key)) throw new IOException("Invalid cache key");
        return Path.Combine(UnitDirectory, key);
    }

    private static bool ValidKey(string key) => key.Length == 64 && key.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static BuildCacheFiles? ReadFiles(string entry)
    {
        try
        {
            if (Linked(entry)) return null;
            return JsonSerializer.Deserialize<BuildCacheFiles>(File.ReadAllText(Path.Combine(entry, "files.json")), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static (string Folder, BuildCacheUnit Index)[] ReadUnits(string root)
    {
        if (!Directory.Exists(root)) return [];
        if (Linked(root)) throw new IOException("Linked cache root");
        var result = new List<(string Folder, BuildCacheUnit Index)>();
        foreach (var folder in Directory.EnumerateDirectories(root, "unit-*"))
        {
            if (!Regex.IsMatch(Path.GetFileName(folder), "^unit-[a-zA-Z0-9._-]*-[0-9a-f]{12}$")) continue;
            if (Linked(folder)) throw new IOException("Linked cache unit");
            BuildCacheUnit? index = null;
            try { index = JsonSerializer.Deserialize<BuildCacheUnit>(File.ReadAllText(Path.Combine(folder, "index.json")), JsonOptions); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            var entries = new List<BuildCacheEntry>();
            string? name = index?.Unit is { } indexedUnit && Path.GetFullPath(UnitPath(root, indexedUnit)) == Path.GetFullPath(folder) ? indexedUnit : null;
            foreach (var entry in Directory.EnumerateDirectories(folder).Where(path => ValidKey(Path.GetFileName(path))))
            {
                if (Linked(entry)) throw new IOException("Linked cache entry");
                var record = ReadFiles(entry);
                if (record?.Unit is { } storedUnit && Path.GetFullPath(UnitPath(root, storedUnit)) == Path.GetFullPath(folder)) name ??= storedUnit;
                var hash = Path.GetFileName(entry);
                var fallback = index?.Entries?.FirstOrDefault(row => row.InputHash == hash);
                var created = record?.CreatedUtc ?? fallback?.CreatedUtc ?? Directory.GetCreationTimeUtc(entry);
                var hit = record?.LastHitUtc ?? fallback?.LastHitUtc ?? created;
                entries.Add(new(hash, created, hit, Files(entry).Sum(path => new FileInfo(path).Length)));
            }
            // Even unreadable metadata must count toward the cap and remain clearable.
            result.Add((folder, new(name ?? "unreadable: " + Path.GetFileName(folder), entries.OrderBy(row => row.InputHash, StringComparer.Ordinal).ToArray())));
        }
        return result.OrderBy(row => row.Index.Unit, StringComparer.Ordinal).ToArray();
    }

    private static void Prune(string root, long limit)
    {
        var units = ReadUnits(root);
        var size = units.Sum(row => row.Index.Entries.Sum(entry => entry.SizeBytes));
        foreach (var (folder, entry) in units.SelectMany(row => row.Index.Entries.Select(entry => (row.Folder, entry))).OrderBy(row => row.entry.LastHitUtc).ThenBy(row => row.entry.CreatedUtc).ThenBy(row => row.entry.InputHash, StringComparer.Ordinal))
        {
            if (size <= limit) break;
            DeleteEntry(root, Path.Combine(folder, entry.InputHash));
            size -= entry.SizeBytes;
        }
        foreach (var (folder, row) in units)
            AtomicJson(Path.Combine(folder, "index.json"), row with { Entries = row.Entries.Where(entry => Directory.Exists(Path.Combine(folder, entry.InputHash))).ToArray() });
    }

    private static void DeleteEntry(string root, string entry)
    {
        var target = Path.GetFullPath(entry);
        if (!target.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Linked(target))
            throw new IOException("Unsafe cache deletion");
        // Check descendants before a recursive delete as well as the resolved root.
        _ = Files(target).Count();
        Directory.Delete(target, true);
    }

    private static T Locked<T>(string root, Func<T> action)
    {
        using var mutex = new Mutex(false, "Local\\TIA-BuildCache-" + Key([], [Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant()]));
        var locked = false;
        try
        {
            try { locked = mutex.WaitOne(); } catch (AbandonedMutexException) { locked = true; }
            return action();
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    private static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions) + "\n", new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool SafeRelative(string path) => !Path.IsPathRooted(path) && path.Length != 0 && !path.Replace('\\', '/').Split('/').Any(part => part is ".." or "." or "");
    private static bool Linked(string path)
    {
        for (var info = new FileInfo(path) as FileSystemInfo; info is not null; info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent)
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    private static IEnumerable<string> Files(string root)
    {
        if (Linked(root)) throw new IOException("Linked cache payload");
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (Linked(file)) throw new IOException("Linked cache file");
            yield return file;
        }
        foreach (var folder in Directory.EnumerateDirectories(root))
            foreach (var file in Files(folder)) yield return file;
    }
    private static ReleaseArtifact[] Inventory(string root) => Files(root)
        .Select(path => new ReleaseArtifact(Path.GetRelativePath(root, path).Replace('\\', '/'), ReleaseRecords.HashFile(path)))
        .OrderBy(row => row.Path, StringComparer.Ordinal).ToArray();
}

internal static partial class ReleaseCommands
{
    private static string DefaultBuildCache()
    {
        var common = ProcessRunner.Run("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], Root);
        ProcessRunner.RequireSuccess(common, "Locate main repository cache");
        return Path.Combine(Directory.GetParent(common.StandardOutput.Trim())!.FullName, "TiaMcp_Output/build-cache");
    }

    private static long CacheLimit(Options? options = null)
    {
        var value = options?.Get("BuildCacheMaxBytes") ?? Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_MAX_BYTES");
        if (value is null) return BuildOutputCache.DefaultMaxBytes;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) || limit <= 0)
            throw new ReleaseException("-BuildCacheMaxBytes must be a positive byte count.", 64);
        return limit;
    }

    private static int CacheInfo(Options options)
    {
        var info = BuildOutputCache.Info(Path.GetFullPath(options.Get("BuildCacheDirectory", DefaultBuildCache())), CacheLimit(options));
        Console.WriteLine(JsonSerializer.Serialize(info, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        return 0;
    }

    private static int CacheClear(Options options)
    {
        var directory = Path.GetFullPath(options.Get("BuildCacheDirectory", DefaultBuildCache()));
        Console.WriteLine($"Cleared {BuildOutputCache.Clear(directory, options.Get("Unit"))} build cache entries from {directory}.");
        return 0;
    }

    private static void CacheEvent(BuildCacheEvent entry)
    {
        Console.WriteLine($"CACHE {entry.Result} {entry.Unit}: {entry.Reason}");
        if (Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_RECORDS") is { } records)
        {
            Directory.CreateDirectory(records);
            WriteJson(Path.Combine(records, Guid.NewGuid().ToString("N") + ".json"), entry);
        }
    }

    private static BuildCacheEvent[] CacheEvents() => Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_RECORDS") is { } directory && Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).Select(path => JsonSerializer.Deserialize<BuildCacheEvent>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!).ToArray() : [];

    internal static CommandResult CachedBuild(string executable, IReadOnlyList<string> arguments, Func<CommandResult> build,
        IDictionary<string, string?>? environment = null)
    {
        var cacheDirectory = Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DIRECTORY");
        if (cacheDirectory is null && Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED") != "1" ||
            !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Count < 2 || arguments[0] is not ("build" or "publish") || arguments.Contains("--no-build") || !arguments[1].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return build();
        var project = Path.GetFullPath(arguments[1], Root);
        var unit = Path.GetRelativePath(Root, project).Replace('\\', '/') + " " + arguments[0] + " " + string.Join(" ", arguments.Where(arg => arg.StartsWith("-p:TiaReleaseKey=", StringComparison.OrdinalIgnoreCase)));
        CommandResult ColdBuild()
        {
            if (arguments is not List<string> mutable) throw new ReleaseException("Cached build requires mutable command arguments");
            var original = mutable.ToArray();
            CommandResult compiled;
            try
            {
                if (mutable[0] == "publish")
                {
                    mutable[0] = "build";
                    for (var i = mutable.Count - 2; i >= 2; i--)
                        if (mutable[i] is "-o" or "--output") mutable.RemoveRange(i, 2);
                }
                mutable.Add("--no-incremental");
                compiled = build();
            }
            finally { mutable.Clear(); mutable.AddRange(original); }
            if (compiled.ExitCode != 0 || original[0] != "publish") return compiled;
            var published = build();
            return new CommandResult(published.ExitCode, compiled.StandardOutput + published.StandardOutput, compiled.StandardError + published.StandardError);
        }
        if (Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED") == "1")
        {
            CacheEvent(new(unit, "", "disabled", "publication or -NoBuildCache requires a cold rebuild"));
            return ColdBuild();
        }
        string key = "", output = "";
        try
        {
            // Use the same runner/environment and effective properties as compilation. A fresh
            // worktree needs assets and generated NuGet imports before its inputs can be evaluated.
            if (arguments is not List<string> mutable) throw new IOException("Cached restore requires mutable command arguments");
            var original = mutable.ToArray();
            try
            {
                mutable.Clear();
                mutable.AddRange(CacheRestoreArguments(project, original));
                ProcessRunner.RequireSuccess(build(), "Restore build cache inputs");
            }
            finally { mutable.Clear(); mutable.AddRange(original); }
            (key, output) = BuildKey(executable, project, arguments, environment: environment);
            var cache = new BuildOutputCache(cacheDirectory!, unit, CacheLimit());
            if (cache.Restore(key, output))
            {
                CacheEvent(new(unit, key, "hit", "verified all payload hashes"));
                return new CommandResult(0, "Verified build cache hit: " + unit + Environment.NewLine, "");
            }
        }
        catch (Exception ex)
        {
            CacheEvent(new(unit, key, "miss", "input evaluation unavailable: " + ex.Message));
            // Without a pre-build fingerprint, stability of inputs cannot be proved.
            return ColdBuild();
        }
        CacheEvent(new(unit, key, "miss", "entry absent or corrupt"));
        var result = ColdBuild();
        if (result.ExitCode == 0)
        {
            try
            {
                var after = BuildKey(executable, project, arguments, environment: environment);
                if (after.Key == key) new BuildOutputCache(cacheDirectory!, unit, CacheLimit()).Populate(key, output);
                else Console.WriteLine("Cache not populated: build inputs changed during compilation: " + unit);
            }
            catch (Exception ex) { Console.WriteLine("Cache not populated: " + ex.Message); }
        }
        return result;
    }

    internal static string CacheInputPath(string path, string root)
    {
        path = Path.GetFullPath(path);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;
    }

    internal static string NormalizeCacheText(string text, string root)
    {
        var parts = Path.GetFullPath(root).TrimEnd('\\', '/').Split(['\\', '/']);
        // MSBuild uses either separator; JSON doubles backslashes. Match the root on a
        // path boundary so a similarly named external directory remains a distinct input.
        var separator = @"(?:\\\\|[\\/])";
        var pattern = string.Join(separator, parts.Select(Regex.Escape));
        return Regex.Replace(text, pattern + "(" + separator + @"|(?=$|[\""'<>;\s]))", match =>
            "<repo>" + (match.Groups[1].Value.Length == 0 ? "" : "/"), RegexOptions.IgnoreCase);
    }

    internal static string[] CacheEnvironment(string root, IEnumerable<KeyValuePair<string, string?>> values) => values
        .Where(row => Regex.IsMatch(row.Key, "^(DOTNET_.*|MSBUILD.*|NUGET_.*|Configuration|Platform|UseSharedCompilation|NuGetAudit|LIB|TIA_MCP_.*)$", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(row.Key, "^(DOTNET_CLI_HOME|TIA_MCP_(DATA_DIRECTORY|DIAGNOSTICS_DIRECTORY|RELEASE_TEMP_ROOT|RELEASE_CHECK_PLAN|TEST_PUBLIC_API_ROOT|BUILD_CACHE_.*)|TIA_MCP_OFFLINE_NUGET_CONFIG)$", RegexOptions.IgnoreCase))
        .Where(row => row.Value is not null)
        .Select(row => row.Key.ToUpperInvariant() + "=" + NormalizeCacheText(row.Value!, root)).Order(StringComparer.Ordinal).ToArray();

    private static string[] EffectiveCacheEnvironment(string root, IDictionary<string, string?>? environment = null)
    {
        var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(row => (string)row.Key, row => (string?)row.Value, StringComparer.OrdinalIgnoreCase);
        if (environment is not null) foreach (var (name, value) in environment) values[name] = value;
        return CacheEnvironment(root, values);
    }

    private static List<string> CacheProperties(IReadOnlyList<string> arguments)
    {
        var result = arguments.Where(arg => arg.StartsWith("-p:", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 2; i < arguments.Count; i++)
        {
            var property = arguments[i] switch
            {
                "-c" or "--configuration" => "Configuration", "-f" or "--framework" => "TargetFramework",
                "-r" or "--runtime" => "RuntimeIdentifier", "-o" or "--output" => "PublishDir", _ => null
            };
            if (property is not null) result.Add("-p:" + property + "=" + arguments[++i]);
        }
        if (!result.Any(prop => prop.StartsWith("-p:Configuration=", StringComparison.OrdinalIgnoreCase))) result.Add("-p:Configuration=Debug");
        if (arguments[0] == "publish") result.Add("-p:_IsPublishing=true");
        return result;
    }

    internal static string[] CacheRestoreArguments(string project, IReadOnlyList<string> arguments) =>
        ["msbuild", project, "-nologo", "-t:Restore", "-m:1", "-nodeReuse:false", .. CacheProperties(arguments)];

    internal static string[] CacheArguments(string project, IReadOnlyList<string> arguments, string? root = null)
    {
        var result = new List<string> { arguments[0], project };
        var properties = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg is "--no-restore" or "--nologo" or "--no-incremental" || arg.StartsWith("-v:", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("-m:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("-nodeReuse:", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.StartsWith("-p:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = arg.IndexOf('=');
                if (separator < 0) throw new ReleaseException("Uncertain build property: " + arg);
                properties[arg[3..separator].ToLowerInvariant()] = arg[(separator + 1)..];
            }
            else result.Add(arg);
        }
        result.AddRange(properties.Select(row => row.Key + "=" + row.Value));
        return result.Select(arg => NormalizeCacheText(arg, root ?? Root)).ToArray();
    }

    internal static (string Key, string Output) BuildKey(string dotnet, string project, IReadOnlyList<string> arguments,
        string? root = null, IDictionary<string, string?>? environment = null)
    {
        root = Path.GetFullPath(root ?? Root);
        var properties = CacheProperties(arguments);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, bool normalizeText = false)
        {
            path = Path.GetFullPath(path);
            var name = CacheInputPath(path, root);
            if (inputs.ContainsKey(name)) return;
            if (!File.Exists(path)) throw new IOException("Build input missing: " + path);
            inputs[name] = normalizeText ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCacheText(File.ReadAllText(path), root)))) : ReleaseRecords.HashFile(path);
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Visit(string file, List<string> props)
        {
            if (!visited.Add(file + string.Join('|', props))) return "";
            Add(file);
            var probe = ProcessRunner.Run(dotnet, ["msbuild", file, "-nologo", .. props,
                "-getProperty:TargetDir,TargetFramework,TargetFrameworks,ProjectAssetsFile,MSBuildAllProjects,MSBuildSDKsPath,NetCoreTargetingPackRoot",
                "-getItem:Compile,EmbeddedResource,Resource,Page,ApplicationDefinition,Content,None,Reference,ProjectReference,AdditionalFiles,Analyzer,KnownFrameworkReference,CustomAdditionalCompileInputs"], root, environment);
            ProcessRunner.RequireSuccess(probe, "Evaluate build cache inputs");
            using var doc = JsonDocument.Parse(probe.StandardOutput);
            var values = doc.RootElement.GetProperty("Properties");
            var frameworks = values.GetProperty("TargetFrameworks").GetString();
            if (values.GetProperty("TargetFramework").GetString() == "" && !string.IsNullOrWhiteSpace(frameworks))
            {
                foreach (var framework in frameworks.Split(';')) Visit(file, [.. props, "-p:TargetFramework=" + framework]);
                return "";
            }
            // MSBuildAllProjects can name only the most recently modified import. Hash the
            // complete preprocessed import closure instead; restore timestamps are not inputs.
            var pp = Path.Combine(Path.GetTempPath(), "tia-cache-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                ProcessRunner.RequireSuccess(ProcessRunner.Run(dotnet, ["msbuild", file, "-nologo", .. props, "-preprocess:" + pp], root, environment), "Evaluate imports");
                var text = NormalizeCacheText(File.ReadAllText(pp), root);
                inputs[CacheInputPath(file, root) + "#imports:" + BuildOutputCache.Key([], props.Select(prop => NormalizeCacheText(prop, root)).Order(StringComparer.Ordinal))] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            }
            finally { if (File.Exists(pp)) File.Delete(pp); }
            // Also cover target-read files and default globs, including additions and deletions.
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*", SearchOption.AllDirectories).Where(path => !IsBuildOutput(path))) Add(path);
            var assets = values.GetProperty("ProjectAssetsFile").GetString()!;
            if (!File.Exists(assets)) throw new IOException("Restore required before caching " + file);
            Add(assets, normalizeText: true);
            using (var assetDoc = JsonDocument.Parse(File.ReadAllText(assets)))
            {
                foreach (var config in assetDoc.RootElement.GetProperty("project").GetProperty("restore").GetProperty("configFilePaths").EnumerateArray()) Add(config.GetString()!);
                var folders = assetDoc.RootElement.GetProperty("packageFolders").EnumerateObject().Select(row => row.Name).ToArray();
                foreach (var library in assetDoc.RootElement.GetProperty("libraries").EnumerateObject().Where(row => row.Value.GetProperty("type").GetString() == "package"))
                {
                    var relative = library.Value.GetProperty("path").GetString()!;
                    var folder = folders.Select(root => Path.Combine(root, relative)).First(Directory.Exists);
                    foreach (var path in library.Value.GetProperty("files").EnumerateArray()) Add(Path.Combine(folder, path.GetString()!));
                }
            }
            foreach (var group in doc.RootElement.GetProperty("Items").EnumerateObject())
                foreach (var item in group.Value.EnumerateArray())
                {
                    var path = item.GetProperty("FullPath").GetString()!;
                    if (group.Name == "KnownFrameworkReference")
                    {
                        var target = values.GetProperty("TargetFramework").GetString()!;
                        if (target.StartsWith(item.GetProperty("TargetFramework").GetString()! + "-", StringComparison.Ordinal) || target == item.GetProperty("TargetFramework").GetString())
                        {
                            var folder = Path.Combine(values.GetProperty("NetCoreTargetingPackRoot").GetString()!, item.GetProperty("TargetingPackName").GetString()!, item.GetProperty("TargetingPackVersion").GetString()!);
                            foreach (var reference in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) Add(reference);
                        }
                    }
                    else if (group.Name == "ProjectReference")
                    {
                        var child = new List<string>(props);
                        foreach (var name in new[] { "GlobalPropertiesToRemove", "UndefineProperties" })
                            if (item.TryGetProperty(name, out var removes))
                                foreach (var remove in removes.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries)) child.RemoveAll(prop => prop.StartsWith("-p:" + remove + "=", StringComparison.OrdinalIgnoreCase));
                        if (item.TryGetProperty("AdditionalProperties", out var additions)) child.AddRange(additions.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(prop => "-p:" + prop));
                        Visit(path, child);
                    }
                    else if (group.Name == "Reference")
                    {
                        if (item.TryGetProperty("HintPath", out var hint) && hint.GetString() is { Length: > 0 } reference) Add(Path.GetFullPath(reference, Path.GetDirectoryName(file)!));
                    }
                    // Tools that rewrite the output (the native call weaver) are build outputs of another
                    // unit; hash their bytes, because woven assemblies embed the weaver's own fingerprint.
                    else if (group.Name == "CustomAdditionalCompileInputs") Add(path);
                    else if (File.Exists(path) && !IsBuildOutput(path)) Add(path);
                }
            return values.GetProperty("TargetDir").GetString()!;
        }
        var output = Visit(project, properties);
        for (var i = 0; i < arguments.Count - 1; i++) if (arguments[i] is "-o" or "--output") output = Path.GetFullPath(arguments[i + 1], root);
        if (output.Length == 0 || !Path.GetFullPath(output).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Uncertain build output location");
        if (properties.FirstOrDefault(prop => prop.StartsWith("-p:SiemensEngineeringDirectory=", StringComparison.OrdinalIgnoreCase)) is { } api)
            foreach (var path in Directory.EnumerateFiles(api[(api.IndexOf('=') + 1)..], "*", SearchOption.AllDirectories)) Add(path);
        foreach (var config in arguments.Where(arg => arg.StartsWith("-p:RestoreConfigFile=", StringComparison.OrdinalIgnoreCase))) Add(config[(config.IndexOf('=') + 1)..]);
        var compiler = ProcessRunner.Run(dotnet, ["msbuild", project, "-nologo", .. properties, "-getProperty:MSBuildSDKsPath"], root, environment);
        ProcessRunner.RequireSuccess(compiler, "Locate SDK compiler");
        var sdkRoot = Directory.GetParent(compiler.StandardOutput.Trim())!.FullName;
        foreach (var path in Directory.EnumerateFiles(sdkRoot, "*.dll", SearchOption.TopDirectoryOnly)) Add(path);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(sdkRoot, "Roslyn"), "*", SearchOption.AllDirectories)) Add(path);
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8");
        if (Directory.Exists(framework)) foreach (var path in Directory.EnumerateFiles(framework, "*.dll", SearchOption.AllDirectories)) Add(path);
        Add(Path.Combine(root, "scripts/build/bundled-dotnet.json"));
        // Hash the build orchestrator too: changes to arguments, weaving or payload rules invalidate outputs.
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "build-tools/release"), "*", SearchOption.TopDirectoryOnly)) Add(path);
        var sdk = ProcessRunner.Run(dotnet, ["--version"], root, environment);
        ProcessRunner.RequireSuccess(sdk, "Identify .NET SDK");
        var buildEnvironment = EffectiveCacheEnvironment(root, environment);
        var key = BuildOutputCache.Key(inputs.Select(row => new ReleaseArtifact(row.Key, row.Value)), [.. CacheArguments(project, arguments, root), sdk.StandardOutput.Trim(), .. buildEnvironment]);
        if (Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DEBUG") is { } debug)
            WriteJson(Path.Combine(debug, key + ".json"), new { project, inputs, environmentHash = BuildOutputCache.Key([], buildEnvironment), arguments = CacheArguments(project, arguments, root) });
        return (key, output);
    }
}
