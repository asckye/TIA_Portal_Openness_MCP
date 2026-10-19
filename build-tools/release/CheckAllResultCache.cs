using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckAllResultSpec(string Family, string Definition, string? Directory = null,
    string? Trx = null, string? Plan = null, int MaximumSkipped = 0, string? AlwaysRun = null)
{
    internal CheckAllSuiteClosure? Closure { get; init; }
}
internal sealed record CheckAllPassEvidence(int Schema, string Check, string IdentityHash, string Machine,
    string RunId, DateTime PassedAtUtc, string Status, int ExitCode, string RunDirectory, ReleaseArtifact[] Files);

// Only check-all attaches these bindings. Standalone suites, branch-gate and the
// candidate/release pipeline never consult this cache.
internal sealed class CheckAllResultCache
{
    private readonly string root, output, manifest, api, nuget, dotnet, python;
    private readonly string? companion;
    private readonly string versions, machine, settings;
    private readonly bool enabled;
    private readonly Func<string> machineStateProbe;
    private readonly Dictionary<string, CheckAllSuiteClosure> closures = new(StringComparer.Ordinal);
    private string[]? layout;

    internal CheckAllResultCache(string root, string output, string manifest, string api, string nuget,
        string dotnet, string python, string? companion, Options options, string resources, Func<string, string?>? contextEnvironment = null,
        Func<string>? machineStateProbe = null)
    {
        this.root = root; this.output = output; this.manifest = manifest; this.api = api; this.nuget = nuget;
        this.dotnet = dotnet; this.python = python; this.companion = companion;
        this.machineStateProbe = machineStateProbe ?? (() => CheckAllMachineState.Read(root, dotnet));
        enabled = Enabled(options, contextEnvironment ?? Environment.GetEnvironmentVariable);
        try { machine = MachineIdentity(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { machine = "unavailable"; enabled = false; }
        settings = JsonSerializer.Serialize(new { resources, options = options.Arguments()
            .Where(arg => !arg.StartsWith("-OutputDirectory=", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal) });
        // Queries are identity discovery, not functional checks. A failure disables
        // reuse; the original checks still execute.
        try
        {
            var sdk = ProcessRunner.Run(dotnet, ["--version"], root);
            var runtimes = ProcessRunner.Run(dotnet, ["--list-runtimes"], root);
            ProcessRunner.RequireSuccess(sdk, "result cache SDK identity"); ProcessRunner.RequireSuccess(runtimes, "result cache runtime identity");
            versions = sdk.StandardOutput + runtimes.StandardOutput + Environment.Version + "/" +
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + "/" + Environment.OSVersion.VersionString;
        }
        catch (Exception ex) { enabled = false; versions = "unavailable: " + ex.Message; }
    }

    internal static bool Enabled(Options options, Func<string, string?> environment) =>
        !options.Has("Clean") && !options.Has("NoResultCache") && !options.Has("ProveEquivalence") &&
        !options.Has("SequentialBaselineDirectory") &&
        new[] { "CI", "GITHUB_ACTIONS", "TIA_MCP_CLEAN_CHECKOUT", "TIA_MCP_RELEASE_CHECK_PLAN", "TIA_MCP_BUILD_CACHE_DISABLED" }
            .All(name => string.IsNullOrEmpty(environment(name)) || environment(name) is "0" or "false");

    internal static string MachineIdentity()
    {
        var identity = Environment.MachineName + "/" + Environment.UserDomainName + "/" + Environment.UserName;
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            identity += "/" + (key?.GetValue("MachineGuid") as string ?? throw new IOException("MachineGuid unavailable"));
        }
        return Hash(identity);
    }

    internal CheckJob Bind(CheckJob job)
    {
        var spec = job.ResultInputs;
        if (spec == null) return job;
        string? reason = spec.AlwaysRun ?? (job.Exclusive || job.Lane != "general" || AlwaysRun(job.Name) ? "sensitive, clock, machine or stability check" : null);
        if (spec.Family == "suite")
        {
            var closure = SuiteClosure(spec);
            spec = spec with { Closure = closure };
            if (closure.LiveReasons.Length != 0) reason ??= string.Join("; ", closure.LiveReasons.Select(item => $"{item.File}:{item.Line}: {item.Detail}"));
        }
        return job with { ResultCachePolicy = reason ?? (enabled ? "eligible; verify complete identity and evidence" : "disabled by invocation/context"),
            ResultInputs = spec,
            ResultCache = new CheckAllResultBinding(root, output, job.Name, spec, machine,
            () => Identity(job, spec), enabled && reason == null) };
    }

    // Eligibility is the default, including ordinary siblings of sensitive shards.
    // Source-derived unresolved inputs and machine APIs supply explicit live reasons.
    internal static bool ReusableSuite(string definition) => JsonNode.Parse(definition)?["suite"] is JsonValue;

    private CheckAllSuiteClosure SuiteClosure(CheckAllResultSpec spec)
    {
        var definition = JsonNode.Parse(spec.Definition)!;
        var registration = definition["registration"]?.AsObject() ?? new JsonObject();
        string project = (string)definition["project"]!;
        var key = project + "|" + registration.ToJsonString() + "|" + (string?)definition["api"];
        if (!closures.TryGetValue(key, out var closure))
            closures[key] = closure = CheckAllInputClosure.Derive(root, project, registration, layout ??= CheckAllLayout.SourceInventory(root), dotnet, (string?)definition["api"]);
        return closure;
    }

    internal static bool ReusableStatic(string name) => name is "catalog-drift" or "tool-counts-drift" or "usage-drift" or "migration-drift" or
        "tools-list" or "script-calls" or "dead-references" or "adapter-boundary" or "python-script-calls" or "python-script-self-tests";

    internal static bool AlwaysRun(string name) => new[] { "stability", "soak", "timing", "budget", "network", "sensitive" }
        .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static bool HasVolatileInput(string source) => CheckAllInputClosure.MachineInputs("source", source).Length != 0;

    internal string Identity(CheckJob job, CheckAllResultSpec spec)
    {
        var closure = spec.Family == "suite" ? spec.Closure ?? SuiteClosure(spec) : null;
        if (closure != null) CheckAllInputClosure.RequireStable(root, closure);
        string? settingsForPython = null;
        var source = closure == null ? CheckAllLayout.ResultSourceInventory(root, CheckAllLayout.SourceInventory(root)) : CheckAllInputClosure.DataFiles(root, closure);
        var trees = new Dictionary<string, string>(StringComparer.Ordinal);
        if (closure == null)
        {
            trees["driver"] = Path.GetDirectoryName(typeof(CheckAllResultCache).Assembly.Location)!;
            trees["sdk"] = api;
        }
        else if (!string.IsNullOrEmpty((string?)JsonNode.Parse(spec.Definition)!["api"]) || closure.Roots.Any(path => CheckAllInputClosure.Covers("sdk", path))) trees["sdk"] = api;
        var rows = File.ReadLines(manifest).Select(line => line.Split('\t')).ToArray();
        if (rows.Any(row => row.Length != 5)) throw new IOException("Invalid runtime inventory");
        // Include ALL outputs, not just a test DLL or the build inventory's expected
        // subset: fixture/reference payloads and additional files also affect a run.
        foreach (var row in rows)
        {
            var project = Path.GetRelativePath(root, row[0]).Replace('\\', '/');
            if (closure != null && !CheckAllInputClosure.Loads(closure, root, row)) continue;
            if (closure != null && project == closure.Project && JsonNode.Parse(spec.Definition)!["variant"] is { } variant && row[1] != variant.GetValue<string>()) continue;
            trees["isolated/" + project + "/" + row[1]] = row[2];
            trees["ordinary/" + project + "/" + row[1]] = row[3];
            if (row[1].StartsWith("sdk-", StringComparison.Ordinal) || row[1].StartsWith("candidate-", StringComparison.Ordinal))
                trees["projected/" + project + "/" + row[1]] = Path.Combine(root, "src/Engine/bin/check-all", Path.GetFileNameWithoutExtension(project), row[1], "Release/net48");
        }
        if (spec.Family == "python")
        {
            var interpreter = ReleaseCommands.ResolveResultApplication(python);
            trees["python"] = Path.GetDirectoryName(interpreter)!;
            if (companion != null) trees["companion"] = Path.GetDirectoryName(companion)!;
            var runtime = ProcessRunner.Run(interpreter, ["-X", "utf8", "-c",
                "import json,sys; print(json.dumps(dict(version=sys.version,roots=[sys.prefix,sys.base_prefix],paths=sys.path)))"], root);
            ProcessRunner.RequireSuccess(runtime, "Python result cache inputs");
            var details = JsonNode.Parse(runtime.StandardOutput)!;
            var paths = details["roots"]!.AsArray().Concat(details["paths"]!.AsArray()).Select(path => path!.GetValue<string>()).Where(path => !string.IsNullOrEmpty(path));
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFullPath(path).TrimEnd('\\', '/') == root.TrimEnd('\\', '/')) continue;
                if (Directory.Exists(path)) trees["python-runtime/" + path] = path;
                else if (File.Exists(path)) source = [.. source, path];
            }
            // Version/build flags are part of the identity as well as executable,
            // stdlib, site-packages, .pth and virtual-environment bytes.
            source = [.. source, interpreter];
            settingsForPython = details.ToJsonString();
        }
        var inputs = Enumerate(root, source.Append(nuget), trees).ToList();
        if (spec.Plan != null) inputs.Add(new("shard-plan", FileHash(spec.Plan)));
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(pair => (string)pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase);
        // Fresh empty sandboxes, pipe names and result paths are allocation roles,
        // not ambient state. Their policy, parent and the runner implementing it
        // are inputs. Never normalize arbitrary caller-supplied environment values.
        var isolation = new { temporaryRoot = Path.GetTempPath(), data = "fresh/TEMP/data", local = "fresh/TEMP/local", roaming = "fresh/TEMP/roaming",
            pipeScope = "fresh-guid", sdk = spec.Family == "suite" ? (string?)JsonNode.Parse(spec.Definition)!["api"] : api,
            approval = true, approvalTimeoutSeconds = 120, noBuild = true, nuget, companion, dotnet, python };
        return Fingerprint(inputs, new { spec.Family, spec.Definition, spec.MaximumSkipped, settings, versions, machine, root,
            machineState = machineStateProbe(), closureRoots = closure?.Roots, wholeInventory = closure?.WholeInventory, productProjects = closure?.Projects,
            job.Lane, job.Affinity, job.Slots, job.Exclusive, timeoutTicks = job.Timeout.Ticks, minWorkers = CheckAffinity.BaselineWorkers,
            isolation, pythonRuntime = settingsForPython, environment = environment.OrderBy(pair => pair.Key, StringComparer.Ordinal) });
    }

    internal static ReleaseArtifact[] Enumerate(string root, IEnumerable<string> files, IReadOnlyDictionary<string, string> trees)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            result["source/" + Path.GetRelativePath(root, full).Replace('\\', '/')] = Directory.Exists(full) ? "directory" : File.Exists(full) ? FileHash(full) : "missing";
        }
        foreach (var (label, tree) in trees)
        {
            var full = Path.GetFullPath(tree);
            if (!Directory.Exists(full)) throw new IOException("Missing input output directory: " + full);
            result["tree/" + label] = "directory";
            foreach (var file in TreeFiles(full)) result["tree/" + label + "/" + Path.GetRelativePath(full, file).Replace('\\', '/')] = Directory.Exists(file) ? "directory" : FileHash(file);
        }
        return result.Select(pair => new ReleaseArtifact(pair.Key, pair.Value)).ToArray();
    }

    private static IEnumerable<string> TreeFiles(string tree)
    {
        if ((File.GetAttributes(tree) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked input: " + tree);
        foreach (var file in Directory.EnumerateFiles(tree)) yield return file;
        foreach (var directory in Directory.EnumerateDirectories(tree))
        {
            yield return directory;
            foreach (var file in TreeFiles(directory)) yield return file;
        }
    }

    internal static string Fingerprint(IEnumerable<ReleaseArtifact> inputs, object definition) => Hash(JsonSerializer.Serialize(new
        { schema = 3, inputs = inputs.OrderBy(row => row.Path, StringComparer.Ordinal), definition }));
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string FileHash(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked input: " + path);
        return ReleaseRecords.HashFile(path);
    }
}

internal static class CheckAllMachineState
{
    internal static string Read(string root, string dotnet)
    {
        try
        {
            var registry = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            object? membership = null;
            if (OperatingSystem.IsWindows())
            {
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var automation = hive.OpenSubKey(@"SOFTWARE\Siemens\Automation");
                    ReadKey(automation, view + @"\SOFTWARE\Siemens\Automation", registry);
                }
                using var token = System.Security.Principal.WindowsIdentity.GetCurrent();
                try
                {
                    // An explicitly local account lookup never queries a domain.
                    var account = new System.Security.Principal.NTAccount(Environment.MachineName, "Siemens TIA Openness");
                    var sid = (System.Security.Principal.SecurityIdentifier)account.Translate(typeof(System.Security.Principal.SecurityIdentifier));
                    membership = new { sid = sid.Value, present = token.Groups?.Contains(sid) == true,
                        enabled = new System.Security.Principal.WindowsPrincipal(token).IsInRole(sid) };
                }
                catch (System.Security.Principal.IdentityNotMappedException) { membership = "local Siemens TIA Openness group absent"; }
            }
            var runtimes = ProcessRunner.Run(dotnet, ["--list-runtimes"], root);
            ProcessRunner.RequireSuccess(runtimes, "result cache installed runtime probe");
            var drives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).Select(drive => drive.Name).Order(StringComparer.Ordinal).ToArray();
            return CheckAllResultCache.Hash(JsonSerializer.Serialize(new { registry, membership, drives,
                runtimes = runtimes.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray() }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            System.ComponentModel.Win32Exception or NotSupportedException or InvalidOperationException or ArgumentException or ReleaseException)
        { throw new IOException("Machine-state probe unavailable; execute the original check", ex); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ReadKey(RegistryKey? key, string path, SortedDictionary<string, object?> values)
    {
        values[path] = key == null ? "missing" : "key";
        if (key == null) return;
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[path + "\\value/" + name] = new { kind = key.GetValueKind(name).ToString(), value };
        }
        foreach (var name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            using var child = key.OpenSubKey(name);
            if (child == null) throw new IOException("Registry key changed during machine probe: " + path + "\\" + name);
            ReadKey(child, path + "\\" + name, values);
        }
    }
}

internal sealed class CheckAllResultBinding(string root, string output, string name, CheckAllResultSpec spec,
    string machine, Func<string> identity, bool enabled)
{
    private string? inputHash;
    private readonly string cache = Path.Combine(root, "bin-build/check-results", CheckAllResultCache.Hash(name));
    internal string EntryPath => Path.Combine(cache, "latest.json");

    internal CheckTiming? TryReuse(string check)
    {
        inputHash = null;
        try
        {
            if (!enabled) { Invalidate(); return null; }
            inputHash = identity();
            if (!File.Exists(EntryPath)) return null;
            _ = CheckAllResultCache.FileHash(EntryPath);
            var entry = JsonSerializer.Deserialize<CheckAllPassEvidence>(File.ReadAllText(EntryPath));
            if (entry == null || entry.Schema != 1 || entry.Check != check || entry.IdentityHash != inputHash || entry.Machine != machine ||
                entry.Status != "passed" || entry.ExitCode != 0 || entry.PassedAtUtc == default || !Guid.TryParseExact(entry.RunId, "N", out _) ||
                entry.Files == null || entry.Files.Length == 0 || entry.Files.Any(file => file == null || string.IsNullOrEmpty(file.Path) || string.IsNullOrEmpty(file.Sha256)) ||
                entry.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Files.Length) return Miss();
            var evidence = Path.Combine(cache, entry.RunId);
            var receipt = Path.Combine(evidence, "pass.json");
            ReleaseCommands.RequireCheckAllOwnedPath(root, receipt);
            if (File.Exists(receipt)) _ = CheckAllResultCache.FileHash(receipt);
            if (!File.Exists(receipt) || File.ReadAllText(receipt) != File.ReadAllText(EntryPath)) return Miss();
            foreach (var file in entry.Files)
            {
                if (Path.GetFileName(file.Path) != file.Path || file.Path is "." or ".." ||
                    file.Path != spec.Trx && file.Path is not ("check.log" or "case-timings.tsv" or "producer-timings.tsv" or "phase-timings.json")) return Miss();
                var path = Path.Combine(evidence, file.Path);
                ReleaseCommands.RequireCheckAllOwnedPath(root, path);
                if (!File.Exists(path) || CheckAllResultCache.FileHash(path) != file.Sha256) return Miss();
            }
            if (spec.Trx != null && (!entry.Files.Any(file => file.Path == spec.Trx) || !CleanTrx(Path.Combine(evidence, spec.Trx), spec.MaximumSkipped))) return Miss();
            if (!entry.Files.Any(file => file.Path == "check.log")) return Miss();
            if (identity() != inputHash) return Miss();
            // Verify all evidence before touching current results. The ordinary
            // Merge then rechecks identities, totals, registered minima/skip caps.
            if (spec.Directory != null)
            {
                Directory.CreateDirectory(spec.Directory);
                foreach (var file in entry.Files.Where(file => file.Path != "check.log")) File.Copy(Path.Combine(evidence, file.Path), Path.Combine(spec.Directory, file.Path), true);
                File.WriteAllText(Path.Combine(spec.Directory, "reused-pass.json"), JsonSerializer.Serialize(entry) + "\n");
            }
            return new(check, "passed (reused)", 0, 0, null) { IdentityHash = inputHash, ReusedRunId = entry.RunId,
                ReusedAtUtc = entry.PassedAtUtc, EvidencePath = Path.Combine(evidence, "pass.json") };
        }
        catch (Exception ex) when (CacheError(ex)) { inputHash = null; return Miss(); }
    }

    private CheckTiming? Miss() { Invalidate(); return null; }
    private void Invalidate()
    {
        try { ReleaseCommands.RequireCheckAllOwnedPath(root, EntryPath); File.Delete(EntryPath); }
        catch (Exception ex) when (CacheError(ex)) { inputHash = null; }
    }

    internal CheckTiming Record(CheckTiming row, string log)
    {
        if (!enabled || inputHash == null) return row;
        row = row with { IdentityHash = inputHash };
        try
        {
            if (row.Status != "passed" || row.ExitCode != 0 || row.Error != null || identity() != inputHash ||
                spec.Trx != null && (!CleanTrx(Path.Combine(spec.Directory!, spec.Trx), spec.MaximumSkipped) || SuiteHang.Errors(spec.Directory!, Path.GetFileNameWithoutExtension(spec.Trx)).Any())) return row;
            var runId = Guid.NewGuid().ToString("N");
            var evidence = Path.Combine(cache, runId); ReleaseCommands.RequireCheckAllOwnedPath(root, evidence); Directory.CreateDirectory(evidence);
            var files = new List<string>();
            if (spec.Trx != null) files.Add(spec.Trx);
            foreach (var file in new[] { "case-timings.tsv", "producer-timings.tsv", "phase-timings.json" })
                if (spec.Directory != null && File.Exists(Path.Combine(spec.Directory, file))) files.Add(file);
            foreach (var file in files) File.Copy(Path.Combine(spec.Directory!, file), Path.Combine(evidence, file));
            File.Copy(log, Path.Combine(evidence, "check.log")); files.Add("check.log");
            var entry = new CheckAllPassEvidence(1, name, inputHash, machine, runId, row.StartedAtUtc, "passed", 0, output,
                files.Select(file => new ReleaseArtifact(file, CheckAllResultCache.FileHash(Path.Combine(evidence, file)))).ToArray());
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            File.WriteAllText(Path.Combine(evidence, "pass.json"), json);
            var temporary = Path.Combine(cache, runId + ".tmp"); File.WriteAllText(temporary, json); File.Move(temporary, EntryPath, true);
            return row with { EvidencePath = Path.Combine(evidence, "pass.json") };
        }
        catch (Exception ex) when (CacheError(ex)) { return row; }
    }

    internal static bool CleanTrx(string path, int maximumSkipped)
    {
        var (counts, errors) = DotnetSuites.ReadTrx(path);
        return errors.Count == 0 && (int)counts["failed"]! == 0 && (int)counts["skipped"]! <= maximumSkipped &&
            counts["methods"]!.AsArray().All(method => !TimeSensitiveTests.Classes.ContainsKey((string)method!["testClass"]!) &&
                !TimeSensitiveTests.Contains((string)method["testClass"]!, (string)method["testMethod"]!));
    }
    private static bool CacheError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or ReleaseException or System.Xml.XmlException or FormatException or KeyNotFoundException;
}
