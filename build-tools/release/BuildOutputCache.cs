using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record BuildCacheEvent(string Unit, string Key, string Result, string Reason, BuildCacheOutput[]? Outputs = null,
    string? Provenance = null, string? RunId = null, string[]? ColdRunIds = null);
internal sealed record BuildCacheOutput(string Path, ReleaseArtifact[] Files);
internal sealed record BuildCacheFiles(string Unit, string Key, DateTime CreatedUtc, DateTime LastHitUtc, BuildCacheOutput[] Outputs,
    string? RunId = null, string[]? ColdRunIds = null);
internal sealed record BuildCacheEntry(string InputHash, DateTime CreatedUtc, DateTime LastHitUtc, long SizeBytes);
internal sealed record BuildCacheUnit(string Unit, BuildCacheEntry[] Entries);
internal sealed record BuildCacheInfo(string Directory, long MaxBytes, long SizeBytes, BuildCacheUnit[] Units);

// Each phase gets fresh repository hashes and MSBuild evaluations. Only raw
// immutable toolchain/package hashes outside the checkout survive compilation.
internal sealed class BuildKeyMemo(Action? checkDeadline = null, ConcurrentDictionary<string, Lazy<string>>? reusableHashes = null)
{
    internal ConcurrentDictionary<string, Lazy<string>> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal ConcurrentDictionary<string, Lazy<CommandResult>> Probes { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, Lazy<string>> Imports { get; } = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<string>> immutableHashes = new(StringComparer.OrdinalIgnoreCase);
    private int probeProcesses;
    internal int ProbeProcesses => Volatile.Read(ref probeProcesses);
    internal void CountProbe() => Interlocked.Increment(ref probeProcesses);
    internal void CheckDeadline() => checkDeadline?.Invoke();

    internal string Hash(string path, string root, bool normalizeText, bool immutable, Func<string> compute)
    {
        CheckDeadline();
        var key = path + "|" + normalizeText;
        var reusable = immutable && !normalizeText && Path.IsPathFullyQualified(ReleaseCommands.CacheInputPath(path, root));
        var value = Hashes.GetOrAdd(key, _ => reusable && reusableHashes?.TryGetValue(key, out var prior) == true ? prior : new(compute));
        var result = value.Value;
        if (reusable) immutableHashes.TryAdd(key, value);
        return result;
    }

    internal BuildKeyMemo ForCompletion() => new(checkDeadline, immutableHashes);
}

internal sealed class BuildOutputCache(string directory, string unit, long maxBytes = BuildOutputCache.DefaultMaxBytes, Func<DateTime>? clock = null,
    Action? checkDeadline = null)
{
    internal const long DefaultMaxBytes = 10L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal string UnitDirectory => UnitPath(directory, unit);
    private DateTime Now => (clock ?? (() => DateTime.UtcNow))();
    private static string UnitPath(string root, string name)
    {
        var readable = Regex.Replace(name, "[^a-zA-Z0-9._-]+", "-").Trim('-');
        return Path.Combine(root, "unit-" + readable[..Math.Min(16, readable.Length)] + "-" + Key([], [name])[..12]);
    }

    internal static string Key(IEnumerable<ReleaseArtifact> inputs, IEnumerable<string> properties) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 6, inputs = inputs.OrderBy(row => row.Path, StringComparer.Ordinal), properties = properties.ToArray()
        }, JsonOptions)))).ToLowerInvariant();

    // Keys ignore the worktree location, so an output that records that location (for example a [CallerFilePath]
    // literal in a test assembly) would point into another, possibly deleted, worktree when reused. Such units are
    // not cached. The PDB path inside a PE CodeView record is excluded: it does not change behaviour.
    internal static string? EmbeddedPath(string output, string root, Action? checkDeadline = null)
    {
        var trimmed = Path.GetFullPath(root).TrimEnd('\\', '/');
        var spellings = new[] { trimmed, trimmed.Replace('\\', '/') }.SelectMany(p => new[] { p, p.ToLowerInvariant(), p.ToUpperInvariant() })
            .Distinct(StringComparer.Ordinal).ToArray();
        var utf16 = spellings.Select(Encoding.Unicode.GetBytes).ToArray();
        var utf8 = spellings.Select(Encoding.UTF8.GetBytes).ToArray();
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            checkDeadline?.Invoke();
            var extension = Path.GetExtension(file).ToLowerInvariant();
            // Coverage source-root maps are only read when collecting coverage, which the release chain never does.
            if (extension == ".pdb" || Path.GetFileName(file).StartsWith(".msCoverageSourceRootsMapping_", StringComparison.Ordinal)) continue;
            var bytes = File.ReadAllBytes(file);
            bool pe = extension is ".dll" or ".exe";
            foreach (var needle in utf16) { checkDeadline?.Invoke(); if (IndexOf(bytes, needle, 0) >= 0) return file; }
            foreach (var needle in utf8)
                for (var at = IndexOf(bytes, needle, 0); at >= 0; at = IndexOf(bytes, needle, at + 1))
                {
                    checkDeadline?.Invoke();
                    if (!pe || !(at >= 24 && bytes[at - 24] == (byte)'R' && bytes[at - 23] == (byte)'S' && bytes[at - 22] == (byte)'D' && bytes[at - 21] == (byte)'S'))
                        return file;
                }
        }
        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (start > haystack.Length) return -1;
        var index = haystack.AsSpan(start).IndexOf(needle);
        return index < 0 ? -1 : start + index;
    }

    internal bool Restore(string key, string output) => Restore(key, new Dictionary<string, string> { [""] = output });

    internal bool Restore(string key, string root, IEnumerable<string> outputs) => Restore(key, OutputPaths(root, outputs));

    internal BuildCacheFiles? Record(string key) => Locked(directory, () => ReadFiles(EntryPath(key)), checkDeadline);

    internal bool RestoreVerified(string key, string root, IEnumerable<string> outputs) =>
        Restore(key, OutputPaths(root, outputs), requireVerified: true);

    internal bool RestoreVerified(string key, string output) =>
        Restore(key, new Dictionary<string, string> { [""] = output }, requireVerified: true);

    private static Dictionary<string, string> OutputPaths(string root, IEnumerable<string> outputs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            if (!SafeRelative(output)) throw new IOException("Unsafe cache output directory");
            result.Add(output, Path.GetFullPath(Path.Combine(root, output)));
        }
        return result;
    }

    private bool Restore(string key, Dictionary<string, string> outputs, bool requireVerified = false)
    {
        try
        {
            return Locked(directory, () =>
            {
                var entry = EntryPath(key);
                var record = ReadFiles(entry);
                if (requireVerified && (record?.ColdRunIds is not { Length: > 0 } || string.IsNullOrWhiteSpace(record?.RunId))) return false;
                if (record is null || record.Unit != unit || record.Key != key || record.Outputs is null || record.Outputs.Length == 0 ||
                    record.Outputs.Length != outputs.Count ||
                    record.Outputs.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Count) return false;
                var payload = Path.Combine(entry, "payload");
                // Validate every file before changing any destination. Reject path traversal and links.
                var inventory = new List<ReleaseArtifact>();
                foreach (var output in record.Outputs)
                {
                    checkDeadline?.Invoke();
                    if (!outputs.TryGetValue(output.Path, out var destination) || Linked(destination) || output.Files is null || output.Files.Length == 0 ||
                        output.Files.Any(row => !SafeRelative(row.Path)) ||
                        output.Files.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.Files.Length) return false;
                    if (!Inventory(Path.Combine(payload, output.Path), checkDeadline).SequenceEqual(output.Files)) return false;
                    foreach (var row in output.Files)
                    {
                        checkDeadline?.Invoke();
                        if (Linked(Path.Combine(destination, row.Path))) return false;
                        inventory.Add(row with { Path = Path.Combine(output.Path, row.Path).Replace('\\', '/') });
                    }
                }
                if (!Inventory(payload, checkDeadline).SequenceEqual(inventory.OrderBy(row => row.Path, StringComparer.Ordinal))) return false;
                foreach (var output in record.Outputs)
                {
                    // References are shared by units: replace only inventoried files, retaining files
                    // restored by another unit. Never delete the destination directory wholesale.
                    foreach (var row in output.Files)
                    {
                        checkDeadline?.Invoke();
                        var target = Path.Combine(outputs[output.Path], row.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (!File.Exists(target) || ReleaseRecords.HashFile(target, checkDeadline) != row.Sha256)
                            File.Copy(Path.Combine(payload, output.Path, row.Path), target, true);
                        if (ReleaseRecords.HashFile(target, checkDeadline) != row.Sha256) throw new IOException("Cache copy hash mismatch");
                    }
                }
                AtomicJson(Path.Combine(entry, "files.json"), record with { LastHitUtc = Now });
                Prune(directory, maxBytes, checkDeadline);
                return true;
            }, checkDeadline);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return false; }
    }

    internal void Populate(string key, string output) => Populate(key, new Dictionary<string, string> { [""] = output });

    internal void Populate(string key, string root, IEnumerable<string> outputs) => Populate(key, OutputPaths(root, outputs));

    internal void RecordBuild(string key, string root, IEnumerable<string> outputs, string runId, bool cold, bool copyOutputs = true) =>
        RecordOutputs(key, OutputPaths(root, outputs), runId, cold, copyOutputs);

    internal void RecordBuild(string key, string output, string runId, bool cold, bool copyOutputs = true) =>
        RecordOutputs(key, new Dictionary<string, string> { [""] = output }, runId, cold, copyOutputs);

    private void Populate(string key, Dictionary<string, string> outputs)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        RecordOutputs(key, outputs, null, false);
    }

    private void RecordOutputs(string key, Dictionary<string, string> outputs, string? runId, bool cold, bool copyOutputs = true)
    {
        checkDeadline?.Invoke();
        RequirePositiveLimit(maxBytes);
        Directory.CreateDirectory(UnitDirectory);
        if (Linked(UnitDirectory)) throw new IOException("Linked cache directory");
        var staging = Path.Combine(UnitDirectory, ".pending-" + Guid.NewGuid().ToString("N"));
        var final = EntryPath(key);
        try
        {
            if (outputs.Count == 0) throw new IOException("No build output directories");
            // Hash/copy outside the cache-wide metadata lock, so different units
            // really run in parallel. The final comparison/commit stays atomic.
            var inventories = outputs.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => new BuildCacheOutput(row.Key, Inventory(row.Value, checkDeadline))).ToArray();
            if (inventories.Any(output => output.Files.Length == 0)) throw new IOException("Empty build output directory");
            foreach (var output in inventories)
            {
                checkDeadline?.Invoke();
                if (copyOutputs)
                {
                    foreach (var row in output.Files)
                    {
                        checkDeadline?.Invoke();
                        var source = Path.Combine(outputs[output.Path], row.Path);
                        if (!SafeRelative(row.Path) || Linked(source)) throw new IOException("Unsupported cache payload");
                        var target = Path.Combine(staging, "payload", output.Path, row.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target);
                    }
                    if (!Inventory(Path.Combine(staging, "payload", output.Path), checkDeadline).SequenceEqual(output.Files))
                        throw new IOException("Build outputs changed during cache population");
                }
                if (!Inventory(outputs[output.Path], checkDeadline).SequenceEqual(output.Files)) throw new IOException("Build outputs changed during cache population");
            }
            Locked(directory, () =>
            {
                checkDeadline?.Invoke();
                var prior = ReadFiles(final);
                if (prior?.Outputs is { Length: > 0 } && !SameOutputs(prior.Outputs, inventories))
                {
                    DeleteEntry(directory, final);
                    throw new BuildNonDeterminismException(unit, key, prior.Outputs, inventories);
                }
                var coldRuns = (prior?.ColdRunIds ?? []).Concat(cold && !string.IsNullOrWhiteSpace(runId) ? new[] { runId } : [])
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (!copyOutputs && prior != null)
                {
                    // Keep an already useful payload; metadata-only runs must not
                    // turn a previously verified restore into a cache miss.
                    AtomicJson(Path.Combine(final, "files.json"), prior with { LastHitUtc = Now, RunId = runId ?? prior.RunId, ColdRunIds = coldRuns });
                    return 0;
                }
                Directory.CreateDirectory(staging);
                AtomicJson(Path.Combine(staging, "files.json"), new BuildCacheFiles(unit, key, Now, Now, inventories, runId ?? prior?.RunId, coldRuns));
                if (Directory.Exists(final)) DeleteEntry(directory, final);
                Directory.Move(staging, final);
                Prune(directory, maxBytes, checkDeadline);
                return 0;
            }, checkDeadline);
        }
        finally { if (Directory.Exists(staging)) DeleteEntry(directory, staging); }
    }

    internal static bool SameOutputs(BuildCacheOutput[] left, BuildCacheOutput[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair => pair.First.Path == pair.Second.Path && pair.First.Files.SequenceEqual(pair.Second.Files));

    private static void RequirePositiveLimit(long value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
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

    private static (string Folder, BuildCacheUnit Index)[] ReadUnits(string root, Action? checkDeadline = null)
    {
        if (!Directory.Exists(root)) return [];
        if (Linked(root)) throw new IOException("Linked cache root");
        var result = new List<(string Folder, BuildCacheUnit Index)>();
        foreach (var folder in Directory.EnumerateDirectories(root, "unit-*"))
        {
            checkDeadline?.Invoke();
            if (!Regex.IsMatch(Path.GetFileName(folder), "^unit-[a-zA-Z0-9._-]*-[0-9a-f]{12}$")) continue;
            if (Linked(folder)) throw new IOException("Linked cache unit");
            BuildCacheUnit? index = null;
            try { index = JsonSerializer.Deserialize<BuildCacheUnit>(File.ReadAllText(Path.Combine(folder, "index.json")), JsonOptions); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            var entries = new List<BuildCacheEntry>();
            string? name = index?.Unit is { } indexedUnit && Path.GetFullPath(UnitPath(root, indexedUnit)) == Path.GetFullPath(folder) ? indexedUnit : null;
            foreach (var entry in Directory.EnumerateDirectories(folder).Where(path => ValidKey(Path.GetFileName(path))))
            {
                checkDeadline?.Invoke();
                if (Linked(entry)) throw new IOException("Linked cache entry");
                var record = ReadFiles(entry);
                if (record?.Unit is { } storedUnit && Path.GetFullPath(UnitPath(root, storedUnit)) == Path.GetFullPath(folder)) name ??= storedUnit;
                var hash = Path.GetFileName(entry);
                var fallback = index?.Entries?.FirstOrDefault(row => row.InputHash == hash);
                var created = record?.CreatedUtc ?? fallback?.CreatedUtc ?? Directory.GetCreationTimeUtc(entry);
                var hit = record?.LastHitUtc ?? fallback?.LastHitUtc ?? created;
                entries.Add(new(hash, created, hit, Files(entry, checkDeadline).Sum(path => new FileInfo(path).Length)));
            }
            // Even unreadable metadata must count toward the cap and remain clearable.
            result.Add((folder, new(name ?? "unreadable: " + Path.GetFileName(folder), entries.OrderBy(row => row.InputHash, StringComparer.Ordinal).ToArray())));
        }
        return result.OrderBy(row => row.Index.Unit, StringComparer.Ordinal).ToArray();
    }

    private static void Prune(string root, long limit, Action? checkDeadline = null)
    {
        checkDeadline?.Invoke();
        var units = ReadUnits(root, checkDeadline);
        var size = units.Sum(row => row.Index.Entries.Sum(entry => entry.SizeBytes));
        foreach (var (folder, entry) in units.SelectMany(row => row.Index.Entries.Select(entry => (row.Folder, entry))).OrderBy(row => row.entry.LastHitUtc).ThenBy(row => row.entry.CreatedUtc).ThenBy(row => row.entry.InputHash, StringComparer.Ordinal))
        {
            checkDeadline?.Invoke();
            if (size <= limit) break;
            DeleteEntry(root, Path.Combine(folder, entry.InputHash));
            size -= entry.SizeBytes;
        }
        foreach (var (folder, row) in units)
        {
            checkDeadline?.Invoke();
            AtomicJson(Path.Combine(folder, "index.json"), row with { Entries = row.Entries.Where(entry => Directory.Exists(Path.Combine(folder, entry.InputHash))).ToArray() });
        }
    }

    internal static void PruneRetained(string root, TimeSpan age, long limit) => Locked(root, () =>
    {
        foreach (var (folder, index) in ReadUnits(root))
            foreach (var entry in index.Entries.Where(entry => DateTime.UtcNow - entry.LastHitUtc > age))
                DeleteEntry(root, Path.Combine(folder, entry.InputHash));
        Prune(root, limit);
        return true;
    });

    private static void DeleteEntry(string root, string entry)
    {
        var target = Path.GetFullPath(entry);
        if (!target.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Linked(target))
            throw new IOException("Unsafe cache deletion");
        // Check descendants before a recursive delete as well as the resolved root.
        _ = Files(target).Count();
        Directory.Delete(target, true);
    }

    private static T Locked<T>(string root, Func<T> action, Action? checkDeadline = null)
    {
        using var mutex = new Mutex(false, "Local\\TIA-BuildCache-" + Key([], [Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant()]));
        var locked = false;
        try
        {
            while (!locked)
            {
                checkDeadline?.Invoke();
                try { locked = mutex.WaitOne(100); } catch (AbandonedMutexException) { locked = true; }
            }
            checkDeadline?.Invoke();
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
            if (info.Attributes is var attributes && attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    private static IEnumerable<string> Files(string root, Action? checkDeadline = null)
    {
        if (Linked(root)) throw new IOException("Linked cache payload");
        foreach (var file in Directory.EnumerateFiles(root))
        {
            checkDeadline?.Invoke();
            if (Linked(file)) throw new IOException("Linked cache file");
            yield return file;
        }
        foreach (var folder in Directory.EnumerateDirectories(root))
            foreach (var file in Files(folder, checkDeadline)) yield return file;
    }
    private static ReleaseArtifact[] Inventory(string root, Action? checkDeadline = null) => Files(root, checkDeadline)
        .Select(path => new ReleaseArtifact(Path.GetRelativePath(root, path).Replace('\\', '/'), ReleaseRecords.HashFile(path, checkDeadline)))
        .OrderBy(row => row.Path, StringComparer.Ordinal).ToArray();
}

internal sealed class BuildNonDeterminismException(string unit, string key, BuildCacheOutput[] before, BuildCacheOutput[] after)
    : Exception("Non-deterministic build outputs; evicted " + unit + " (" + key + "): " + string.Join(", ",
        before.SelectMany(output => output.Files.Select(file => file with { Path = output.Path + "/" + file.Path }))
            .Concat(after.SelectMany(output => output.Files.Select(file => file with { Path = output.Path + "/" + file.Path })))
            .GroupBy(file => file.Path).Where(group => group.Count() != 2 || group.Select(file => file.Sha256).Distinct().Count() != 1).Select(group => group.Key)));

internal static partial class ReleaseCommands
{
    private static string DefaultBuildCache()
    {
        var common = ProcessRunner.Run("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], Root);
        ProcessRunner.RequireSuccess(common, "Locate main repository cache");
        return Path.Combine(Directory.GetParent(common.StandardOutput.Trim())!.FullName, "bin-build/cache");
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
        IDictionary<string, string?>? environment = null, string? root = null)
    {
        root = Path.GetFullPath(root ?? Root);
        var cacheDirectory = Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DIRECTORY");
        if (cacheDirectory is null && Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED") != "1" ||
            !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Count < 2 || arguments[0] is not ("build" or "publish") || arguments.Contains("--no-build") || !arguments[1].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return build();
        var project = Path.GetFullPath(arguments[1], root);
        var unit = Path.GetRelativePath(root, project).Replace('\\', '/') + " " + arguments[0] + " " + string.Join(" ", arguments.Where(arg => arg.StartsWith("-p:TiaReleaseKey=", StringComparison.OrdinalIgnoreCase)));
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
        cacheDirectory ??= Path.Combine(root, "bin-build/cache/build-units");
        var noReads = Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED") == "1";
        string key = "";
        string[] outputs = [];
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
            var evaluated = BuildKey(executable, project, arguments, root, environment);
            key = evaluated.Key;
            outputs = evaluated.Outputs;
            var cache = new BuildOutputCache(cacheDirectory!, unit, CacheLimit());
            if (!noReads && cache.RestoreVerified(key, root, outputs))
            {
                var record = cache.Record(key)!;
                CacheEvent(new(unit, key, "hit", "verified all payload hashes", record.Outputs, "restored", record.RunId, record.ColdRunIds));
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
                var after = BuildKey(executable, project, arguments, root, environment);
                if (after.Key != key) Console.WriteLine("Cache not populated: build inputs changed during compilation: " + unit);
                else if (outputs.Select(output => BuildOutputCache.EmbeddedPath(Path.Combine(root, output), root)).FirstOrDefault(path => path is not null) is { } embedded)
                    Console.WriteLine("Cache not populated: output records the worktree path (" + Path.GetRelativePath(root, embedded) + "): " + unit);
                else
                {
                    var cache = new BuildOutputCache(cacheDirectory!, unit, CacheLimit());
                    cache.RecordBuild(key, root, outputs, CacheRunId(), cold: true, copyOutputs: !noReads);
                    var record = cache.Record(key);
                    CacheEvent(new(unit, key, "built", "cold output hashes recorded", record?.Outputs, "cold", record?.RunId, record?.ColdRunIds));
                }
            }
            catch (BuildNonDeterminismException) { throw; }
            catch (Exception ex) { Console.WriteLine("Cache not populated: " + ex.Message); }
        }
        return result;
    }

    private static string CacheRunId() => Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_RUN_ID") ??
        (Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_RECORDS") is { } records ? Path.GetFileName(Path.GetDirectoryName(records)) : null) ?? Guid.NewGuid().ToString("N");

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
            // Job names and private run/output locations do not describe build inputs.
            !Regex.IsMatch(row.Key, "^(DOTNET_CLI_HOME|TIA_MCP_(DATA_DIRECTORY|DIAGNOSTICS_DIRECTORY|RELEASE_TEMP_ROOT|RELEASE_CHECK_PLAN|" +
                "BUILD_CACHE_.*|ANALYZER_REPORT_DIRECTORY|CHECK_.*|CLEAN_CHECKOUT|RUNNER_JOB|COMMAND_JOB|NIGHTLY_ROOT|" +
                "TEST_(PUBLIC_API_ROOT|PIPE_SCOPE|WORKER_FIXTURE_ROOT|REGISTERED_OUTPUT|SHARD_PLAN|CASE_TIMINGS|PRODUCER_TIMINGS|PHASE_TIMINGS|DECODER_PREPARE_TOOL))|TIA_MCP_OFFLINE_NUGET_CONFIG)$", RegexOptions.IgnoreCase))
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
        if (properties.GetValueOrDefault("tiacheckbuild") == "true")
        {
            if (properties.GetValueOrDefault("sourcerevisionid") != BuildReproducibility.CheckRevision)
                throw new IOException("Check builds require the fixed source revision marker");
            properties.Remove("sourcerevisionid");
        }
        result.AddRange(properties.Select(row => row.Key + "=" + row.Value));
        return result.Select(arg => NormalizeCacheText(arg, root ?? Root)).ToArray();
    }

    internal static (string Key, string Output, string[] Outputs) BuildKey(string dotnet, string project, IReadOnlyList<string> arguments,
        string? root = null, IDictionary<string, string?>? environment = null, BuildKeyMemo? memo = null)
    {
        root = Path.GetFullPath(root ?? Root);
        Action? checkDeadline = memo == null ? null : memo.CheckDeadline;
        var properties = CacheProperties(arguments);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, bool normalizeText = false, bool immutable = false)
        {
            memo?.CheckDeadline();
            path = Path.GetFullPath(path);
            var name = CacheInputPath(path, root);
            if (inputs.ContainsKey(name) && !immutable) return;
            if (!File.Exists(path)) throw new IOException("Build input missing: " + path);
            string Compute() => normalizeText ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCacheText(File.ReadAllText(path), root)))) : ReleaseRecords.HashFile(path, checkDeadline);
            inputs[name] = memo == null ? Compute() : memo.Hash(path, root, normalizeText, immutable, Compute);
        }
        CommandResult Probe(string[] args)
        {
            memo?.CheckDeadline();
            var key = string.Join('\n', args);
            CommandResult Run() { memo?.CheckDeadline(); memo?.CountProbe(); return ProcessRunner.Run(dotnet, args, root, environment); }
            var result = memo == null ? Run() : memo.Probes.GetOrAdd(key, _ => new(Run)).Value;
            if (memo != null && result.ExitCode != 0) memo.Probes.TryRemove(key, out _);
            return result;
        }
        string? evaluatedSdkRoot = null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Output(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Uncertain build output location");
            outputs.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
        }
        string Visit(string file, List<string> props)
        {
            memo?.CheckDeadline();
            var canonical = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in props) { var at = prop.IndexOf('='); canonical[prop[3..at]] = prop[(at + 1)..]; }
            props = canonical.Select(pair => "-p:" + pair.Key + "=" + pair.Value).ToList();
            if (!visited.Add(file + string.Join('|', props))) return "";
            Add(file);
            var probe = Probe(["msbuild", file, "-nologo", .. props,
                "-getProperty:TargetDir,TargetFramework,TargetFrameworks,ProjectAssetsFile,MSBuildAllProjects,MSBuildSDKsPath,NetCoreTargetingPackRoot",
                "-getItem:Compile,EmbeddedResource,Resource,Page,ApplicationDefinition,Content,None,Reference,ProjectReference,AdditionalFiles,Analyzer,KnownFrameworkReference,CustomAdditionalCompileInputs"]);
            ProcessRunner.RequireSuccess(probe, "Evaluate build cache inputs");
            using var doc = JsonDocument.Parse(probe.StandardOutput);
            var values = doc.RootElement.GetProperty("Properties");
            evaluatedSdkRoot ??= Directory.GetParent(values.GetProperty("MSBuildSDKsPath").GetString()!)!.FullName;
            var frameworks = values.GetProperty("TargetFrameworks").GetString();
            if (values.GetProperty("TargetFramework").GetString() == "" && !string.IsNullOrWhiteSpace(frameworks))
            {
                foreach (var framework in frameworks.Split(';')) Visit(file, [.. props, "-p:TargetFramework=" + framework]);
                return "";
            }
            Output(values.GetProperty("TargetDir").GetString()!);
            // Ask MSBuild which framework/configuration it actually builds for each reference.
            // Simply visiting all TargetFrameworks would demand outputs never built by this unit.
            JsonDocument? references = null;
            if (doc.RootElement.GetProperty("Items").GetProperty("ProjectReference").GetArrayLength() != 0)
            {
                var prepared = Probe(["msbuild", file, "-nologo", "-m:1", "-nodeReuse:false", .. props,
                    "-t:PrepareProjectReferences", "-getProperty:BuildProjectReferences,_GlobalPropertiesToRemoveFromProjectReferences",
                    "-getItem:_MSBuildProjectReferenceExistent,_MSBuildProjectReferenceNonExistent"]);
                ProcessRunner.RequireSuccess(prepared, "Evaluate referenced build outputs");
                references = JsonDocument.Parse(prepared.StandardOutput);
                if (references.RootElement.GetProperty("Properties").GetProperty("BuildProjectReferences").GetString() != "true" ||
                    references.RootElement.GetProperty("Items").GetProperty("_MSBuildProjectReferenceNonExistent").GetArrayLength() != 0)
                    throw new IOException("Uncertain referenced build outputs");
            }
            using var referenceLifetime = references;
            // MSBuildAllProjects can name only the most recently modified import. Hash the
            // complete preprocessed import closure instead; restore timestamps are not inputs.
            string Imports()
            {
                memo?.CheckDeadline();
                var pp = Path.Combine(Path.GetTempPath(), "tia-cache-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    memo?.CountProbe();
                    ProcessRunner.RequireSuccess(ProcessRunner.Run(dotnet, ["msbuild", file, "-nologo", .. props, "-preprocess:" + pp], root, environment), "Evaluate imports");
                    return NormalizeCacheText(File.ReadAllText(pp), root);
                }
                finally { if (File.Exists(pp)) File.Delete(pp); }
            }
            var importKey = file + "|" + string.Join('|', props);
            var text = memo == null ? Imports() : memo.Imports.GetOrAdd(importKey, _ => new(Imports)).Value;
            inputs[CacheInputPath(file, root) + "#imports:" + BuildOutputCache.Key([], props.Select(prop => NormalizeCacheText(prop, root)).Order(StringComparer.Ordinal))] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            // Also cover target-read files and default globs, including additions and deletions.
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*", SearchOption.AllDirectories))
            {
                memo?.CheckDeadline();
                if (!IsBuildOutput(path)) Add(path);
            }
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
                    foreach (var path in library.Value.GetProperty("files").EnumerateArray()) Add(Path.Combine(folder, path.GetString()!), immutable: true);
                }
            }
            foreach (var group in doc.RootElement.GetProperty("Items").EnumerateObject())
                foreach (var item in group.Value.EnumerateArray())
                {
                    memo?.CheckDeadline();
                    var path = item.GetProperty("FullPath").GetString()!;
                    if (group.Name == "KnownFrameworkReference")
                    {
                        var target = values.GetProperty("TargetFramework").GetString()!;
                        if (target.StartsWith(item.GetProperty("TargetFramework").GetString()! + "-", StringComparison.Ordinal) || target == item.GetProperty("TargetFramework").GetString())
                        {
                            var folder = Path.Combine(values.GetProperty("NetCoreTargetingPackRoot").GetString()!, item.GetProperty("TargetingPackName").GetString()!, item.GetProperty("TargetingPackVersion").GetString()!);
                            foreach (var reference in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) Add(reference, immutable: true);
                        }
                    }
                    else if (group.Name == "ProjectReference")
                    {
                        var resolved = references!.RootElement.GetProperty("Items").GetProperty("_MSBuildProjectReferenceExistent").EnumerateArray()
                            .Single(row => row.GetProperty("FullPath").GetString()!.Equals(path, StringComparison.OrdinalIgnoreCase));
                        if (resolved.GetProperty("BuildReference").GetString() != "true" ||
                            resolved.TryGetProperty("Targets", out var targets) && targets.GetString() is { Length: > 0 } target && target is not ("Build" or "CheckCachedBuild"))
                            throw new IOException("Uncertain project reference build targets");
                        var child = new List<string>(props);
                        foreach (var name in new[] { "GlobalPropertiesToRemove", "UndefineProperties" })
                            if (resolved.TryGetProperty(name, out var removes))
                                foreach (var remove in removes.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries)) child.RemoveAll(prop => prop.StartsWith("-p:" + remove + "=", StringComparison.OrdinalIgnoreCase));
                        foreach (var remove in references.RootElement.GetProperty("Properties").GetProperty("_GlobalPropertiesToRemoveFromProjectReferences").GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries))
                            child.RemoveAll(prop => prop.StartsWith("-p:" + remove + "=", StringComparison.OrdinalIgnoreCase));
                        foreach (var name in new[] { "AdditionalProperties", "SetConfiguration", "SetPlatform", "SetTargetFramework" })
                            if (resolved.TryGetProperty(name, out var additions)) child.AddRange(additions.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(prop => "-p:" + prop));
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
        if (output.Length != 0) Output(output);
        if (outputs.Count == 0) throw new IOException("Uncertain build output location");
        var outputPaths = outputs.Order(StringComparer.Ordinal).ToArray();
        if (properties.FirstOrDefault(prop => prop.StartsWith("-p:SiemensEngineeringDirectory=", StringComparison.OrdinalIgnoreCase)) is { } api)
            foreach (var path in Directory.EnumerateFiles(api[(api.IndexOf('=') + 1)..], "*", SearchOption.AllDirectories)) Add(path, immutable: true);
        foreach (var config in arguments.Where(arg => arg.StartsWith("-p:RestoreConfigFile=", StringComparison.OrdinalIgnoreCase))) Add(config[(config.IndexOf('=') + 1)..]);
        // The first input evaluation already reports the selected SDK. Avoid
        // a second identical MSBuild evaluation just to read that property.
        var sdkRoot = evaluatedSdkRoot ?? throw new IOException("SDK compiler location missing");
        foreach (var path in Directory.EnumerateFiles(sdkRoot, "*.dll", SearchOption.TopDirectoryOnly)) Add(path, immutable: true);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(sdkRoot, "Roslyn"), "*", SearchOption.AllDirectories)) Add(path, immutable: true);
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8");
        if (Directory.Exists(framework)) foreach (var path in Directory.EnumerateFiles(framework, "*.dll", SearchOption.AllDirectories)) Add(path, immutable: true);
        Add(Path.Combine(root, "build-tools/bundled-dotnet.json"));
        // Hash the build orchestrator too: changes to arguments, weaving or payload rules invalidate outputs.
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "build-tools/release"), "*", SearchOption.TopDirectoryOnly)) Add(path);
        var sdk = Probe(["--version"]);
        ProcessRunner.RequireSuccess(sdk, "Identify .NET SDK");
        var buildEnvironment = EffectiveCacheEnvironment(root, environment);
        var cacheArguments = CacheArguments(project, arguments, root);
        var revision = cacheArguments.Contains("tiacheckbuild=true", StringComparer.Ordinal) ? Array.Empty<string>()
            : new[] { "source-revision:" + BuildReproducibility.SourceRevision(root) };
        var key = BuildOutputCache.Key(inputs.Select(row => new ReleaseArtifact(row.Key, row.Value)), [.. cacheArguments, sdk.StandardOutput.Trim(),
            .. revision, .. buildEnvironment, "outputs:", .. outputPaths]);
        if (Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DEBUG") is { } debug)
            WriteJson(Path.Combine(debug, key + ".json"), new { project, inputs, environmentHash = BuildOutputCache.Key([], buildEnvironment), arguments = CacheArguments(project, arguments, root) });
        return (key, output, outputPaths);
    }
}
