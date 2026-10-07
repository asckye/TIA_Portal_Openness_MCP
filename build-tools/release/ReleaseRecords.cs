using System.Security.Cryptography;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal sealed record ReleaseArtifact(string Path, string Sha256);

internal static class ReleaseRecords
{
    private static readonly string[] EngineSourceRoots = ["src/Engine", "src/FoundationHost", "src/Worker", "src/Logic", "src/Runtime", "src/WorkerChannel", "src/Adapters", "src/Adapters.Contracts", "src/Updater", "src/Tools/WriteGuard", "tests/Engine", "tests/Updater", "tests/Tools", "tests/test-suites.json", "build-tools/native-call-weaver", "build-tools/release", "src/Shared", "third_party/TiaGitAddIn.Core", "third_party/SiemensOpcUaModelled"];
    private static readonly string[] MultiSourceRoots = ["src/Engine", "src/FoundationHost", "src/Worker", "src/Logic", "src/Runtime", "src/WorkerChannel", "src/Adapters", "src/Adapters.Contracts", "src/Tools/WriteGuard", "tests/Engine", "tests/Tools", "tests/test-suites.json", "src/Shared", "src/Studio", "tests/Studio", "third_party/tia-openness-studio", "build-tools/native-call-weaver", "build-tools/release", "scripts/build", "scripts/checks", "scripts/diagnostics", "scripts/generate"];
    private static readonly string[] ValidationRoots = ["src/Engine", "src/FoundationHost", "src/Worker", "src/Logic", "src/Runtime", "src/WorkerChannel", "src/Adapters", "src/Adapters.Contracts", "src/Updater", "src/Tools/WriteGuard", "tests/Engine", "tests/Updater", "tests/Tools", "tests/test-suites.json", "build-tools/native-call-weaver", "build-tools/release", "src/Shared", "third_party/eido-import-planner", "third_party/siemens-plc-tools", "third_party/SiemensOpcUaModelled", "third_party/simaticml-decoder", "third_party/TiaGitAddIn.Core", "scripts/build", "scripts/checks", "scripts/diagnostics", "scripts/generate", "scripts/ecosystem", "reference", "templates"];
    private static readonly string[] BinaryExtensions = [".exe", ".dll", ".config"];

    internal static IReadOnlyList<ReleaseArtifact> GetSources(string root, string kind)
    {
        var roots = kind == "engine" ? EngineSourceRoots : kind == "multi" ? MultiSourceRoots : throw new ArgumentOutOfRangeException(nameof(kind));
        var extensions = kind == "engine"
            ? new HashSet<string>([".cs", ".csproj", ".props", ".targets", ".xml", ".json", ".config", ".manifest", ".resx"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([".cs", ".csproj", ".props", ".targets", ".xaml", ".py", ".json", ".resx"], StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kind == "engine") files.Add(Path.Combine(root, "Version.props"));
        foreach (var relative in roots)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(file) || !extensions.Contains(Path.GetExtension(file))) continue;
                    files.Add(file);
                }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).Select(file => new ReleaseArtifact(Relative(root, file), ReleaseValidation.SourceHash(file))).ToArray();
    }

    internal static IReadOnlyList<ReleaseArtifact> GetValidationInputs(string root, string kind)
    {
        var roots = kind == "multi" ? ValidationRoots.Concat(["src/Studio", "tests/Studio", "third_party/tia-openness-studio"]) : ValidationRoots;
        var extensions = new HashSet<string>([".cs", ".csproj", ".props", ".targets", ".xml", ".json", ".xaml", ".py", ".toml", ".xsd", ".ttf", ".otf"], StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>([Path.Combine(root, "Version.props")], StringComparer.OrdinalIgnoreCase);
        foreach (var relative in roots)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                if (!IsBuildOutput(path) && extensions.Contains(Path.GetExtension(path))) files.Add(path);
            }
            else if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    if (!IsBuildOutput(file) && extensions.Contains(Path.GetExtension(file))) files.Add(file);
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).Select(file => new ReleaseArtifact(Relative(root, file), ReleaseValidation.SourceHash(file))).ToArray();
    }

    internal static IReadOnlyList<ReleaseArtifact> GetRuntimeFiles(string root, bool prepared = false)
    {
        var runtime = Path.Combine(root, "runtime");
        if (!Directory.Exists(runtime)) return [];
        return Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var ext = Path.GetExtension(file);
                var bundledRuntime = file.StartsWith(Path.Combine(runtime, "dotnet") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var allowed = BinaryExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || bundledRuntime;
                return allowed && !Path.GetFileName(file).Equals("README.md", StringComparison.OrdinalIgnoreCase) &&
                       (!prepared || !System.Text.RegularExpressions.Regex.IsMatch(file, @"[\\/]runtime[\\/](v20|v21|verification|tools)[\\/]", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => new ReleaseArtifact(Relative(root, file), HashFile(file)))
            .ToArray();
    }

    internal static string ReuseReason(string root, JsonElement record, string release, IReadOnlyList<ReleaseArtifact> sources, string fileProperty)
    {
        try
        {
            if (GetString(record, "release") != release || GetString(record, "fileVersion") != release + ".0") return "release/fileVersion changed";
            if (!record.TryGetProperty("sourceFiles", out var sourceRows) || sourceRows.ValueKind != JsonValueKind.Array || sourceRows.GetArrayLength() == 0 ||
                !record.TryGetProperty(fileProperty, out var fileRows) || fileRows.ValueKind != JsonValueKind.Array || fileRows.GetArrayLength() == 0)
                return "empty source or binary inventory";
            var recordedSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in sourceRows.EnumerateArray())
            {
                var path = GetString(row, "path");
                if (path.Length == 0 || !recordedSources.TryAdd(path, GetString(row, "sha256"))) return "invalid/duplicate source inventory";
            }
            if (recordedSources.Count != sources.Count) return "source inventory changed (added/removed input)";
            foreach (var row in sources)
                if (!recordedSources.TryGetValue(row.Path, out var sha) || sha != row.Sha256) return "source changed: " + row.Path;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var row in fileRows.EnumerateArray())
            {
                var relative = GetString(row, "path");
                if (relative.Length == 0 || !seen.Add(relative)) return "invalid/duplicate binary inventory";
                var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return "invalid/duplicate binary inventory";
                if (!File.Exists(path)) return "binary missing: " + relative;
                if (!string.Equals(HashFile(path), GetString(row, "sha256"), StringComparison.OrdinalIgnoreCase)) return "binary changed: " + relative;
            }
            return "";
        }
        catch { return "record or input is unreadable"; }
    }

    internal static string AuditEvidenceReason(string root, string release, JsonElement record)
    {
        if (!record.TryGetProperty("validationArtifacts", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 4)
            return "full-engine audit evidence record missing; rebuild once";
        var prefix = $"bin-build/releases/v{release}/";
        var expected = new[] { $"{prefix}v20/native-call-coverage-v20.json", $"{prefix}v20/tool-usage-v20.json", $"{prefix}v21/native-call-coverage-v21.json", $"{prefix}v21/tool-usage-v21.json" };
        var actual = rows.EnumerateArray().Select(row => GetString(row, "path")).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return "full-engine audit evidence inventory changed";
        foreach (var row in rows.EnumerateArray())
        {
            var relative = GetString(row, "path");
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return "audit evidence missing: " + relative;
            if (!string.Equals(HashFile(path), GetString(row, "sha256"), StringComparison.OrdinalIgnoreCase)) return "audit evidence changed: " + relative;
        }
        return "";
    }

    internal static string EngineReuseReason(string root, string release, JsonElement record, IReadOnlyList<ReleaseArtifact> sources, string requiredTier = "full")
    {
        if (requiredTier == "full" && GetString(record, "tier") is "quick" or "package") return "non-full evidence cannot be reused for a full release";
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (GetString(record, "tier") is "quick" or "package")
        {
            try
            {
                var plan = JsonSerializer.Deserialize<ReleaseCheckPlan>(record.GetProperty("checkPlan").GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
                ReleaseCheckPolicy.Load(root).ValidatePlan(plan);
                selected.UnionWith(plan.SelectedChecks);
            }
            catch { return "quick check selection is missing or invalid"; }
        }
        bool includes(string check) => GetString(record, "tier") is not ("quick" or "package") || selected.Contains(check);
        var auditReason = includes("native-coverage") && includes("resource-discovery") ? AuditEvidenceReason(root, release, record) : "";
        if (auditReason.Length != 0) return auditReason;
        try
        {
            if (!record.TryGetProperty("validation", out var validation)) return "validation record missing";
            if (includes("offline-suites") && (GetInt(validation, "offlinePassed") <= 0 || GetInt(validation, "offlineV20Passed") <= 0 || GetInt(validation, "versionPolicySdkPassed") <= 0 ||
                GetInt(validation, "writeGuardPassed") < 3 || GetInt(validation, "crashEvidencePassed") < 7 || GetInt(validation, "updaterPassed") < 20))
                return "offline validation missing";
            foreach (var major in new[] { 20, 21 })
            {
                if (!validation.TryGetProperty("runtimes", out var runtimes) || !runtimes.TryGetProperty("V" + major, out var runtime)) return $"V{major} validation incomplete";
                foreach (var name in new[] { "localStability", "isolatedLocalStability" })
                {
                    if (!includes(name == "localStability" ? "engine-stability" : "engine-isolated-stability")) continue;
                    if (!runtime.TryGetProperty(name, out var stability) || GetString(stability, "status") != "passed" ||
                        !stability.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() != 4)
                        return $"V{major} validation incomplete";
                    if (name == "isolatedLocalStability" && !GetBool(stability, "isolatedWorker")) return $"V{major} validation incomplete";
                }
                if (includes("engine-approval") && (!runtime.TryGetProperty("approvalSafety", out var approval) || GetString(approval, "status") != "passed" ||
                    GetInt(approval, "checksPassed") != 7 || !GetBool(approval, "defaultEnabled") ||
                    !GetBool(approval, "directWriteRefusedBeforeDispatch") || !GetBool(approval, "callToolWriteRefusedBeforeDispatch") ||
                    !GetBool(approval, "readSucceeded") || GetBool(approval, "workbenchConnected") || GetBool(approval, "tiaConnected")))
                    return $"V{major} default-approval gate incomplete";
                if (includes("offline-suites") && (!runtime.TryGetProperty("sessionStability", out var session) || GetInt(session, "nativeMcpSafetyChecksPassed") < 8 ||
                    GetInt(session, "crashEvidenceChecksPassed") < 7 || GetBool(session, "nativeMcpExecuted")))
                    return $"V{major} session safety evidence incomplete";
            }

            var roots = new[] { "runtime/v20", "runtime/v21", "runtime/tools", "runtime/verification" };
            var expected = Directory.Exists(Path.Combine(root, "runtime"))
                ? roots.SelectMany(relative =>
                {
                    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : [];
                }).Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".config" || Path.GetFileName(path) is "NativeCallWeaver.deps.json" or "NativeCallWeaver.runtimeconfig.json")
                  .Select(path => Relative(root, path)).Order(StringComparer.Ordinal).ToArray()
                : [];
            var recorded = ReadRows(record, "runtimeFiles").Select(row => row.Path).Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(recorded, StringComparer.Ordinal)) return "runtime inventory changed (added/removed binary)";
            if (!record.TryGetProperty("validationInputs", out var inputRows) || inputRows.ValueKind != JsonValueKind.Array || inputRows.GetArrayLength() == 0)
                return "supplemental validation input record missing; rebuild once with this pipeline";
            var inputs = GetValidationInputs(root, "engine");
            using var proof = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                release = GetString(record, "release"), fileVersion = GetString(record, "fileVersion"),
                sourceFiles = ReadRows(record, "validationInputs"), files = ReadRows(record, "runtimeFiles")
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var reason = ReuseReason(root, proof.RootElement, release, inputs, "files");
            if (reason.Length != 0) return reason;
            return ReuseReason(root, record, release, sources, "runtimeFiles");
        }
        catch { return "record or input is unreadable"; }
    }

    internal static void RestoreArchivedAuditEvidence(string root, string release, string archive, JsonElement record)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(root, "bin-build/releases"));
        var fullArchive = Path.GetFullPath(archive);
        if (!string.Equals(Path.GetDirectoryName(fullArchive), expectedParent, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Audit archive escaped releases directory");
        foreach (var row in record.GetProperty("validationArtifacts").EnumerateArray())
        {
            var relative = GetString(row, "path");
            var prefix = $"bin-build/releases/v{release}/";
            if (!relative.StartsWith(prefix, StringComparison.Ordinal)) throw new ReleaseException("Audit evidence path escaped release output");
            var source = Path.GetFullPath(Path.Combine(fullArchive, relative[prefix.Length..]));
            if (!source.StartsWith(fullArchive + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Audit evidence path escaped archive");
            if (!File.Exists(source) || !string.Equals(HashFile(source), GetString(row, "sha256"), StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Archived audit evidence changed: " + relative);
            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
    }

    internal static string? MovePreviousReleaseOutput(string root, string release)
    {
        var parent = Path.GetFullPath(Path.Combine(root, "bin-build/releases"));
        var source = Path.GetFullPath(Path.Combine(parent, "v" + release));
        var destination = source + ".previous-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        if (!string.Equals(Path.GetDirectoryName(source), parent, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(destination), parent, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("Archive path escaped the releases directory");
        if (!Directory.Exists(source)) return null;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new ReleaseException("Release output must not be a reparse point");
        Directory.Move(source, destination);
        return destination;
    }

    internal static void AssertRuntimePreparation(string root)
    {
        string[] dependencies = ["TiaMcp.WorkerChannel.dll", "System.Text.Json.dll", "System.Text.Encodings.Web.dll", "System.IO.Pipelines.dll", "Microsoft.Bcl.AsyncInterfaces.dll", "System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll"];
        var required = new List<string> { "runtime/studio/TiaOpenness.exe", "runtime/studio/TiaMcp.WorkerChannel.dll" };
        required.AddRange(dependencies.Select(name => "runtime/studio/bridge/" + name));
        required.AddRange(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key => $"runtime/studio/bridge/adapters/v{key}/TiaOpenness.Openness.dll"));
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
        {
            required.Add($"runtime/v{key}/TiaMcp.FoundationHost.exe");
            required.AddRange(dependencies.Select(name => $"runtime/v{key}/worker/{name}"));
            required.Add($"runtime/v{key}/TiaMcp.WorkerChannel.dll");
        }
        var missing = required.Where(path => !File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        var dotnet = Path.Combine(root, "runtime/dotnet/shared/Microsoft.NETCore.App");
        foreach (var name in new[] { "System.Text.Json.dll", "System.Text.Encodings.Web.dll", "System.IO.Pipelines.dll" })
            if (!Directory.Exists(dotnet) || !Directory.EnumerateFiles(dotnet, name, SearchOption.AllDirectories).Any()) missing.Add($"runtime/dotnet/shared/Microsoft.NETCore.App/*/{name}");
        if (missing.Count != 0) throw new ReleaseException("Delivery prerequisites missing; run build-multi-version -PrepareOnly -Test first: " + string.Join(", ", missing));
    }

    internal static void AssertMultiVersionPreparation(string root, JsonElement record, string release, IReadOnlyList<ReleaseArtifact> sources,
        IReadOnlyList<ReleaseArtifact> validationInputs, IReadOnlyList<ReleaseArtifact> files, string api)
    {
        var reason = ReuseReason(root, record, release, sources, "files");
        if (reason.Length != 0) throw new ReleaseException("Multi-version preparation cannot be completed: " + reason);
        if (GetString(record, "publicApiRoot") != api) throw new ReleaseException("Preparation PublicApiRoot changed");
        var originalFiles = ReadRows(record, "files").Select(row => row.Path).Order(StringComparer.Ordinal).ToArray();
        if (!originalFiles.SequenceEqual(files.Select(row => row.Path).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new ReleaseException("Prepared binary inventory changed");
        var proof = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            release = GetString(record, "release"), fileVersion = GetString(record, "fileVersion"),
            sourceFiles = ReadRows(record, "validationInputs"), files = ReadRows(record, "evidence")
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        using (proof)
        {
            reason = ReuseReason(root, proof.RootElement, release, validationInputs, "files");
            if (reason.Length != 0) throw new ReleaseException("Prepared validation inputs/evidence changed: " + reason);
        }
    }

    internal static IReadOnlyList<ReleaseArtifact> ReadRows(JsonElement record, string property) =>
        record.GetProperty(property).EnumerateArray().Select(row => new ReleaseArtifact(GetString(row, "path"), GetString(row, "sha256"))).ToArray();

    internal static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static int GetInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static bool GetBool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsBuildOutput(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part is "obj" or "obj-v20" or "bin" or "bin-v20" or "__pycache__" or ".venv");
    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
