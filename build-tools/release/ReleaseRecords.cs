using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record ReleaseArtifact(string Path, string Sha256);

internal sealed record UpdaterArtifact(string Path, string Sha256, long Length);

internal static class ReleaseRecords
{
    internal const string EnginePath = "manifest/release-build.json";
    internal const string MultiPath = "manifest/multi-version-build.json";

    // build-release builds and tests the net48 updater into bin-build/updater; the package carries it under runtime/tools.
    // It is in neither runtime inventory: the records bind exactly these files in updaterFiles (P8-upd-bind).
    internal static readonly IReadOnlyDictionary<string, string> UpdaterOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["runtime/tools/TiaMcp.Updater.exe"] = "bin-build/updater/TiaMcp.Updater.exe",
        ["runtime/tools/TiaMcp.Updater.exe.config"] = "bin-build/updater/TiaMcp.Updater.exe.config"
    };

    // The release-wide outputs build-release records in release-build.json; every other runtime file is in the eight-release record.
    internal static readonly string[] ReleaseBuildRuntimeRoots = ["runtime/tools", "runtime/verification"];

    internal static string SelectPath(string root) => File.Exists(Path.Combine(root, MultiPath)) ? MultiPath
        : File.Exists(Path.Combine(root, EnginePath)) ? EnginePath
        : throw new ReleaseException("Release build record missing; run build-release or build-multi-version");

    internal static string InventoryProperty(string relative) => relative == EnginePath ? "runtimeFiles" : "files";
    internal static string BindingProperty(string relative) => relative == EnginePath ? "engineBuildSha256" : "multiVersionBuildSha256";

    internal static string DeliveryBuildPath(string root, string release)
    {
        var relative = MultiPath;
        var path = Path.Combine(root, relative);
        using var build = JsonDocument.Parse(File.ReadAllText(path));
        if (GetString(build.RootElement, "release") != release || GetString(build.RootElement, "fileVersion") != ReleaseVersions.FileVersionOf(release))
            throw new ReleaseException("Release build record differs from the requested release");
        if (relative == MultiPath) AssertPlcProof(root, build.RootElement, true);
        foreach (var major in new[] { 20, 21 })
            if (!File.Exists(Path.Combine(root, $"runtime/v{major}/worker/TiaMcp.PlcWorker.{major}.exe")))
                throw new ReleaseException($"V{major} PLC worker output missing; run build-multi-version first");
        return path;
    }

    internal static void AssertBinding(string root, JsonElement delivery, string relative)
    {
        if (GetString(delivery, BindingProperty(relative)) != HashFile(Path.Combine(root, relative)))
            throw new ReleaseException("Build record changed: " + relative);
    }

    internal static string ResumeReason(string root, string release)
    {
        var relative = SelectPath(root);
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)));
        string reason;
        if (relative == EnginePath) reason = EngineReuseReason(root, release, record.RootElement, GetSources(root, "engine"));
        else
        {
            if (GetString(record.RootElement, "tier") != "full") return "full multi-version checks are required";
            AssertPlcProof(root, record.RootElement, true);
            reason = ReuseReason(root, record.RootElement, release, GetSources(root, "multi"), "files");
        }
        // The resumed package takes the updater from bin-build/updater; it must still be the recorded build.
        return reason.Length != 0 ? reason : UpdaterReason(root, record.RootElement, package: false);
    }

    // The updater build output as the records bind it (package path, SHA-256, length).
    internal static IReadOnlyList<UpdaterArtifact> HashUpdaterOutput(string root) => UpdaterOutputs.Keys.Order(StringComparer.Ordinal).Select(name =>
    {
        var path = Path.Combine(root, UpdaterOutputs[name].Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new ReleaseException("Updater build output missing: " + UpdaterOutputs[name]);
        return new UpdaterArtifact(name, HashFile(path), new FileInfo(path).Length);
    }).ToArray();

    // The updater rows a record binds; none for a record written before the field (3.x records predate the C# updater).
    // Same rules as updater_inventory in scripts/checks/Check-BundleLayout.py.
    internal static IReadOnlyList<UpdaterArtifact> UpdaterFiles(JsonElement record)
    {
        if (!record.TryGetProperty("updaterFiles", out var rows)) return [];
        ReleaseException Invalid() => new("Invalid updater build record: updaterFiles must bind " +
            string.Join(" and ", UpdaterOutputs.Keys.Order(StringComparer.Ordinal)) + " with SHA-256 and length");
        if (rows.ValueKind != JsonValueKind.Array || rows.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Object)) throw Invalid();
        var result = rows.EnumerateArray().Select(row => new UpdaterArtifact(GetString(row, "path"), GetString(row, "sha256"),
            row.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt64(out var value) ? value : 0)).ToArray();
        if (!result.Select(row => row.Path).Order(StringComparer.Ordinal).SequenceEqual(UpdaterOutputs.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            result.Any(row => !System.Text.RegularExpressions.Regex.IsMatch(row.Sha256, @"\A[0-9a-f]{64}\z") || row.Length <= 0))
            throw Invalid();
        foreach (var property in new[] { "runtimeFiles", "files" })
            if (record.TryGetProperty(property, out var inventory) && inventory.ValueKind == JsonValueKind.Array &&
                inventory.EnumerateArray().Any(row => row.ValueKind == JsonValueKind.Object && UpdaterOutputs.ContainsKey(GetString(row, "path"))))
                throw new ReleaseException("Updater files must come from the validated updater build output, not the runtime inventory");
        return result;
    }

    // "" when every recorded updater file is where this tree keeps it (bin-build/updater in a repository, runtime/tools in a package).
    internal static string UpdaterReason(string root, JsonElement record, bool package)
    {
        IReadOnlyList<UpdaterArtifact> rows;
        try { rows = UpdaterFiles(record); }
        catch (ReleaseException ex) { return ex.Message; }
        if (rows.Count == 0) return "updater build output not recorded (updaterFiles); run build-release";
        foreach (var row in rows)
        {
            var relative = package ? row.Path : UpdaterOutputs[row.Path];
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return "updater missing: " + relative;
            if (new FileInfo(path).Length != row.Length || HashFile(path) != row.Sha256) return "updater changed after validation: " + relative;
        }
        return "";
    }

    internal static IReadOnlyList<ReleaseArtifact> DeliveryFiles(string root, bool boundOnly = false)
    {
        var selected = SelectPath(root);
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, selected)));
        var files = ReadRows(record.RootElement, InventoryProperty(selected)).ToDictionary(row => row.Path, StringComparer.Ordinal);
        var multiBound = !boundOnly;
        if (boundOnly && File.Exists(Path.Combine(root, "manifest/delivery.json")))
        {
            using var delivery = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/delivery.json")));
            multiBound = GetString(delivery.RootElement, "multiVersionBuildSha256").Length != 0;
        }
        if (selected == EnginePath && multiBound && File.Exists(Path.Combine(root, MultiPath)))
        {
            using var multi = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, MultiPath)));
            foreach (var row in ReadRows(multi.RootElement, "files"))
                if (files.TryGetValue(row.Path, out var old) && old.Sha256 != row.Sha256)
                    throw new ReleaseException("Conflicting runtime inventories: " + row.Path);
                else files[row.Path] = row;
        }
        return files.Values.ToArray();
    }

    // A preparation without -Test runs only the PLC approval successors, and only when the plan selects foundation-approval;
    // the record keeps its "test" flag so the writer, AssertPlcProof and the comparison expect the same receipts.
    internal static ReleaseCheckPlan PlcProofPlan(ReleaseCheckPlan plan, bool test) =>
        test ? plan : plan with { SelectedChecks = plan.Includes("foundation-approval") ? ["plc-approval"] : [] };

    internal static bool RecordTest(JsonElement record) =>
        !record.TryGetProperty("test", out var value) || value.ValueKind != JsonValueKind.False;

    // The job logs a PLC proof binds: each receipt names one, and the preparation lists them in its evidence.
    internal static IEnumerable<string> PlcProofLogs(string root, string logs, ReleaseCheckPlan proofPlan) =>
        ReleaseCommands.PlcReleaseJobs(root, "", logs, "", "dotnet", "python")
            .Where(job => ReleaseCommands.PlcReleaseSelected(proofPlan, job)).Select(job => Path.Combine(logs, job.Name + ".log"));

    // A receipt keeps the lines that report counts; the complete log stays bound by its SHA-256 in validationArtifacts.
    private static readonly System.Text.RegularExpressions.Regex CountLine =
        new(@"\bCOMPLETE\b|^\s*(PASS|FAIL)\b|^\s*(Passed|Failed)!|^\s*已通过!|^\s*失败!|总计:|Total tests:|^Suites:", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex LocalPath =
        new(@"(?:\\\\\?\\)?\b[A-Za-z]:[\\/][^\s""'<>|;,]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static IReadOnlyList<string> CountLines(string root, string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => CountLine.IsMatch(line)).Select(line => WithoutLocalPaths(root, line)).ToArray();

    // Records ship in the package: the checkout becomes ".", the temp and profile folders become tokens, and any other
    // absolute path is replaced, so no machine path of the build host reaches a record.
    internal static string WithoutLocalPaths(string root, string text)
    {
        foreach (var (prefix, token) in new[] { (Path.GetFullPath(root), "."), (Path.GetTempPath(), "%TEMP%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%") })
        {
            var trimmed = prefix.TrimEnd('\\', '/');
            if (trimmed.Length < 3) continue;
            foreach (var form in new[] { trimmed, trimmed.Replace('\\', '/') })
                text = text.Replace(@"\\?\" + form, token, StringComparison.OrdinalIgnoreCase).Replace(form, token, StringComparison.OrdinalIgnoreCase);
        }
        return LocalPath.Replace(text, "<local path>");
    }

    internal static bool HasLocalPath(string text) => LocalPath.IsMatch(text);

    internal static JsonNode? WithoutLocalPaths(string root, JsonElement element) => WithoutLocalPaths(root, JsonNode.Parse(element.GetRawText()));

    private static JsonNode? WithoutLocalPaths(string root, JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.Select(pair => KeyValuePair.Create(pair.Key, WithoutLocalPaths(root, pair.Value)))),
        JsonArray value => new JsonArray(value.Select(item => WithoutLocalPaths(root, item)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(WithoutLocalPaths(root, text)),
        _ => node?.DeepClone()
    };

    internal static JsonObject PlcProof(string root, string logs, ReleaseCheckPlan plan)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "20", "21" })
        {
            JsonObject Artifact(string path) => new() { ["path"] = Path.GetRelativePath(root, path).Replace('\\', '/'), ["sha256"] = HashFile(path), ["length"] = new FileInfo(path).Length };
            var directory = ReleaseCommands.PlcReleaseInstallDirectory(root, key);
            var runtime = new JsonObject
            {
                ["worker"] = Artifact(Path.Combine(directory, $"TiaMcp.PlcWorker.{key}.exe")),
                ["sdkWorker"] = Artifact(new PlcWorkerFixtureBuild(key, "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"))),
                ["adapter"] = Artifact(Path.Combine(directory, $"TiaMcp.Adapter.{key}.dll")),
                ["host"] = Artifact(Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe")),
                ["catalog"] = Artifact(Path.Combine(logs, "tools-" + key + ".json")),
                ["toolsList"] = Artifact(Path.Combine(root, "manifest/tools-list.json")),
                ["checks"] = new JsonObject()
            };
            foreach (var job in ReleaseCommands.PlcReleaseJobs(root, "", logs, "", "dotnet", "python")
                .Where(job => job.Release == key && ReleaseCommands.PlcReleaseSelected(plan, job)))
            {
                var log = Path.Combine(logs, job.Name + ".log");
                if (!File.Exists(log)) throw new ReleaseException("PLC check log missing: " + job.Name);
                // The receipt: the log's path, SHA-256 and length (the log is listed in validationArtifacts) and its count
                // lines. Counts are never inferred from the engine run or reduced to a list of successful job names.
                var check = Artifact(log);
                check["status"] = "passed";
                check["counts"] = new JsonArray(CountLines(root, File.ReadAllText(log)).Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
                ((JsonObject)runtime["checks"]!)[job.Name] = check;
                if (job.Check is "plc-stability" or "plc-isolated-stability")
                {
                    if (!File.Exists(job.Evidence)) throw new ReleaseException("PLC stability evidence missing: " + job.Name);
                    // The soak's structured gates (status, rounds, identities, run matrix) without host paths; result.json is bound in validationArtifacts.
                    var stability = WithoutLocalPaths(root, JsonNode.Parse(File.ReadAllText(job.Evidence)))!;
                    // runtimeSha256 is the tested SDK PlcWorker, never Engine.V2x.
                    stability["productionWorkerSha256"] = runtime["worker"]!["sha256"]!.DeepClone();
                    runtime[job.Check == "plc-stability" ? "localStability" : "isolatedLocalStability"] = stability;
                }
            }
            result["V" + key] = runtime;
        }
        return result;
    }

    // build-release rewrites manifest/tools-list.json (generatedAt) after the preparation recorded the PLC proof;
    // the completed record binds the roster it ships, and AssertPlcProof refuses any later change.
    internal static JsonObject WithShippedToolsList(string root, JsonElement runtimes)
    {
        var result = JsonNode.Parse(runtimes.GetRawText())!.AsObject();
        var path = Path.Combine(root, "manifest/tools-list.json");
        foreach (var (_, runtime) in result)
            runtime!["toolsList"] = new JsonObject { ["path"] = "manifest/tools-list.json", ["sha256"] = HashFile(path), ["length"] = new FileInfo(path).Length };
        return result;
    }

    // The repository keeps the shipped 3.3.0 records until the first 4.0 release writes its own; 3.x records predate the PLC
    // proof. Every 4.x record (and one whose release cannot be read) must carry it (P8-04j2).
    internal static bool RequiresPlcProof(JsonElement record) =>
        !TiaMcp.Versioning.ProductReleaseVersion.TryParse(GetString(record, "release"), out var version) || version is null || version.Major >= 4;

    // P8-04l: a 4.x eight-release record no longer carries the engine record (engineCompatibility); its own PLC proof is
    // the V20/V21 evidence.
    internal static void AssertPlcProof(string root, JsonElement record, bool binaries)
    {
        var plan = PlcProofPlan(JsonSerializer.Deserialize<ReleaseCheckPlan>(record.GetProperty("checkPlan").GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? throw new ReleaseException("Multi-version check plan missing"), RecordTest(record));
        var files = ReadRows(record, "files").ToDictionary(row => row.Path, StringComparer.Ordinal);
        // Each receipt names a log that the record binds in validationArtifacts with the same SHA-256.
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        if (record.TryGetProperty("validationArtifacts", out var artifactRows) && artifactRows.ValueKind == JsonValueKind.Array)
            foreach (var row in artifactRows.EnumerateArray()) bound[GetString(row, "path")] = GetString(row, "sha256");
        if (HasLocalPath(record.GetProperty("plcRuntimes").GetRawText().Replace(@"\\", @"\", StringComparison.Ordinal)))
            throw new ReleaseException("PLC proof holds an absolute local path");
        foreach (var key in new[] { "20", "21" })
        {
            var runtime = record.GetProperty("plcRuntimes").GetProperty("V" + key);
            foreach (var field in new[] { "worker", "adapter", "host" })
            {
                var artifact = runtime.GetProperty(field);
                var path = GetString(artifact, "path");
                if (!files.TryGetValue(path, out var row) || row.Sha256 != GetString(artifact, "sha256")) throw new ReleaseException("PLC artifact not bound to inventory: " + path);
                if (binaries && HashFile(Path.Combine(root, path)) != row.Sha256) throw new ReleaseException("PLC artifact changed: " + path);
            }
            if (!GetString(runtime.GetProperty("worker"), "path").EndsWith($"/TiaMcp.PlcWorker.{key}.exe", StringComparison.Ordinal))
                throw new ReleaseException("PLC proof names a different runtime: " + key);
            var checks = runtime.GetProperty("checks");
            foreach (var field in new[] { "catalog", "toolsList", "sdkWorker" })
                if (GetString(runtime.GetProperty(field), "sha256").Length != 64) throw new ReleaseException("PLC catalog/SDK hash missing: " + field);
            var toolsPath = Path.Combine(root, "manifest/tools-list.json");
            if (File.Exists(toolsPath) && HashFile(toolsPath) != GetString(runtime.GetProperty("toolsList"), "sha256"))
                throw new ReleaseException("PLC tools-list changed after validation");
            foreach (var job in ReleaseCommands.PlcReleaseJobs(root, "", "", "", "dotnet", "python")
                .Where(job => job.Release == key && ReleaseCommands.PlcReleaseSelected(plan, job)))
                if (!checks.TryGetProperty(job.Name, out var check) || GetString(check, "status") != "passed" ||
                    !System.Text.RegularExpressions.Regex.IsMatch(GetString(check, "sha256"), "^[0-9a-f]{64}$") || GetLong(check, "length") <= 0 ||
                    !check.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Array ||
                    GetString(check, "path") != $"bin-build/multi-version/{job.Name}.log" ||
                    !bound.TryGetValue(GetString(check, "path"), out var boundSha) || boundSha != GetString(check, "sha256"))
                    throw new ReleaseException("PLC check proof missing: " + job.Name);
            foreach (var (name, selected) in new[] { ("localStability", "plc-stability"), ("isolatedLocalStability", "plc-isolated-stability") })
            {
                if (!plan.Includes(selected)) continue;
                var proof = runtime.GetProperty(name);
                var sdkHash = GetString(runtime.GetProperty("sdkWorker"), "sha256");
                if (sdkHash.Length != 64 || GetString(proof, "runtimeSha256") != sdkHash || GetString(proof, "sdkWorkerSha256") != sdkHash ||
                    GetString(proof, "productionWorkerSha256") != GetString(runtime.GetProperty("worker"), "sha256") ||
                    GetString(proof, "hostSha256") != GetString(runtime.GetProperty("host"), "sha256") ||
                    GetString(proof, "status") != "passed" || GetInt(proof, "rounds") < 10 || !GetBool(proof, "isolatedWorker") || GetString(proof, "workerModel") != "foundation-host-plc-worker")
                    throw new ReleaseException("PLC stability runtime evidence changed: " + key + "/" + name);
                var runs = proof.GetProperty("runs").EnumerateArray().ToArray();
                if (runs.Length != 4 || !runs.Select(run => GetString(run, "transport") + "/" + GetString(run, "profile")).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(new[] { "stdio/full", "stdio/lite", "http/full", "http/lite" }) ||
                    runs.Any(run => new[] { "unexpectedExits", "unexpectedFailures", "unmatchedJournalEntries", "measuredToolCalls", "tiaConnected" }.Any(field => !run.TryGetProperty(field, out _)) ||
                        GetInt(run, "unexpectedExits") != 0 || GetInt(run, "unexpectedFailures") != 0 ||
                        GetInt(run, "unmatchedJournalEntries") != 0 || GetInt(run, "measuredToolCalls") <= 0 || GetBool(run, "tiaConnected")))
                    throw new ReleaseException("PLC stability matrix incomplete: " + key + "/" + name);
            }
        }
    }

    internal sealed record FieldComparison(string EngineField, string? MultiField, string Status);

    // P8-04l: release-build.json records the release-wide build only (no engine). Its release, tier, check plan, updater and
    // runtime rows must equal the eight-release record's; its other fields (release-wide suite counts, sources, inputs) are
    // release-build.json evidence that the eight-release record does not repeat.
    internal static IReadOnlyList<FieldComparison> Compare(JsonElement releaseBuild, JsonElement multi)
    {
        var rows = new List<FieldComparison>();
        void Walk(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) Walk(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name);
                return;
            }
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() != 0)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray()) Walk(item, path + "[" + index++ + "]");
                return;
            }
            string? counterpart = null;
            var status = "release-build.json evidence; no eight-release counterpart";
            if (path is "release" or "fileVersion" or "tier" || path.StartsWith("checkPlan.", StringComparison.Ordinal) ||
                path.StartsWith("updaterFiles[", StringComparison.Ordinal)) { counterpart = path; status = "equal"; }
            var fileField = System.Text.RegularExpressions.Regex.Match(path, @"^runtimeFiles\[(\d+)\]\.(path|sha256|length)$");
            if (fileField.Success)
            {
                var index = int.Parse(fileField.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var original = GetString(releaseBuild.GetProperty("runtimeFiles")[index], "path");
                counterpart = "files[" + original + "]";
                status = "equal";
                if (multi.TryGetProperty("files", out var inventory))
                {
                    var rowIndex = 0;
                    foreach (var row in inventory.EnumerateArray())
                    {
                        if (GetString(row, "path") == original) { counterpart = $"files[{rowIndex}]." + fileField.Groups[2].Value; break; }
                        rowIndex++;
                    }
                }
            }
            if (counterpart is null) rows.Add(new(path, null, status));
            else if (!TryField(multi, counterpart, out var actual)) rows.Add(new(path, counterpart, "missing"));
            else if (value.GetRawText() != actual.GetRawText()) rows.Add(new(path, counterpart, "different"));
            else rows.Add(new(path, counterpart, status));
        }
        Walk(releaseBuild, "");
        foreach (var key in new[] { "20", "21" })
            foreach (var field in new[] { "worker.path", "worker.sha256", "adapter.sha256", "host.sha256", "catalog.sha256", "toolsList.sha256", "checks" })
            {
                var path = "plcRuntimes.V" + key + "." + field;
                rows.Add(new("V" + key + " PLC build/check evidence", path, TryField(multi, path, out _) ? "PLC counterpart" : "missing"));
            }
        return rows;
    }

    private static bool TryField(JsonElement value, string path, out JsonElement result)
    {
        result = value;
        foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(path, @"([^\.\[\]]+)|\[(\d+)\]"))
        {
            if (part.Groups[1].Success)
            {
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(part.Value, out var next)) return false;
                result = next;
            }
            else
            {
                var index = int.Parse(part.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() <= index) return false;
                result = result[index];
            }
        }
        return true;
    }

    private static readonly string[] BinaryExtensions = [".exe", ".dll", ".config"];
    // The Workbench loads its bundled fonts from runtime/studio/Fonts (P8-80d); delivery requires them.
    private static readonly string[] FontExtensions = [".ttf", ".otf"];

    internal static IReadOnlyList<ReleaseArtifact> GetSources(string root, string kind)
    {
        var roots = kind is "engine" or "multi" ? SourceRoots.Load(root).Inventory(kind) : throw new ArgumentOutOfRangeException(nameof(kind));
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
        var policy = SourceRoots.Load(root);
        var roots = kind == "multi" ? policy.Inventory("validation").Concat(policy.Inventory("validationMulti")) : policy.Inventory("validation");
        var extensions = new HashSet<string>([".cs", ".csproj", ".props", ".targets", ".xml", ".json", ".xaml", ".py", ".toml", ".xsd", ".ttf", ".otf", ".editorconfig"], StringComparer.OrdinalIgnoreCase);
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
        foreach (var entry in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).Where(OpennessGroupRuntime.IsEntryAssembly))
            OpennessGroupRuntime.Inspect(entry).RequireClean();
        return Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var ext = Path.GetExtension(file);
                var bundledRuntime = file.StartsWith(Path.Combine(runtime, "dotnet") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var allowed = BinaryExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || FontExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || bundledRuntime;
                return allowed && !Path.GetFileName(file).Equals("README.md", StringComparison.OrdinalIgnoreCase) &&
                       (!prepared || !System.Text.RegularExpressions.Regex.IsMatch(file, @"[\\/]runtime[\\/](verification|tools)[\\/]|[\\/]runtime[\\/](v20|v21)[\\/]plc-worker[\\/]", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => new ReleaseArtifact(Relative(root, file), HashFile(file)))
            .ToArray();
    }

    internal static string ReuseReason(string root, JsonElement record, string release, IReadOnlyList<ReleaseArtifact> sources, string fileProperty)
    {
        try
        {
            if (GetString(record, "release") != release || GetString(record, "fileVersion") != ReleaseVersions.FileVersionOf(release)) return "release/fileVersion changed";
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

    // P8-04l: the four engine audit inputs (engine weave inventories and engine usage receipts) retired with the engine;
    // the PLC inventories and usage receipts are bound by the eight-release record. Every artifact a release build record
    // lists must still be under its release output with the recorded SHA-256.
    internal static string AuditEvidenceReason(string root, string release, JsonElement record)
    {
        if (!record.TryGetProperty("validationArtifacts", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return "release build evidence record missing; rebuild once";
        var prefix = $"bin-build/releases/v{release}/";
        foreach (var row in rows.EnumerateArray())
        {
            var relative = GetString(row, "path");
            if (!relative.StartsWith(prefix, StringComparison.Ordinal)) return "audit evidence path escaped release output: " + relative;
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
                GetInt(validation, "keptCodePassed") <= 0 || GetInt(validation, "keptCodeV20Passed") <= 0 ||
                GetInt(validation, "writeGuardPassed") < 3 || GetInt(validation, "crashEvidencePassed") < 7 || GetInt(validation, "updaterPassed") < 20 ||
                // Formerly each engine runtime's sessionStability gate; the native MCP safety checks are release-wide.
                GetInt(validation, "nativeMcpSafetyPassed") < 8))
                return "offline validation missing";
            var roots = ReleaseBuildRuntimeRoots;
            var expected = Directory.Exists(Path.Combine(root, "runtime"))
                ? roots.SelectMany(relative =>
                {
                    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : [];
                }).Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".config" ||
                    Path.GetFileName(path) is "release-key.txt" or "NativeCallWeaver.deps.json" or "NativeCallWeaver.runtimeconfig.json")
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
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            required.Add($"runtime/v{key}/TiaMcp.FoundationHost.exe");
            required.AddRange(dependencies.Select(name => $"runtime/v{key}/worker/{name}"));
            required.Add($"runtime/v{key}/release-key.txt");
            required.Add($"runtime/v{key}/TiaMcp.FoundationHost.runtimeconfig.json");
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

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static int GetInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long GetLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static bool GetBool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsBuildOutput(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part is "obj" or "obj-v20" or "bin" or "bin-v20" or "__pycache__" or ".venv");
    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
