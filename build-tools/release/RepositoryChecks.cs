using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class RepositoryChecks
{
    private static readonly HashSet<string> Skip = [".git", "bin-build", "bin", "bin-v20", "obj", "obj-v20", "__pycache__", "TiaMcp_Output", ".pytest_cache"];
    internal static string? LocalTarget(string root, string source, string target)
    {
        target = Uri.UnescapeDataString(target.Trim('<', '>').Split('#', 2)[0]);
        if (target.Length == 0 || Regex.IsMatch(target, @"\A[\w+.-]+:")) return null;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, target));
        if (!Within(root, path)) return "link leaves repository: " + target;
        return File.Exists(path) || Directory.Exists(path) ? null : "missing link: " + target;
    }
    private static bool Within(string root, string path) => path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static List<string> ForbiddenScripts(IEnumerable<string> names) => names.Distinct().Order(StringComparer.Ordinal).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".ps1" or ".psm1" or ".bat" or ".cmd").Select(p => "Forbidden tracked script file: " + p).ToList();
    internal static List<string> ProductNames(string root, IEnumerable<string> names)
    {
        string[] historical = ["manifest/contracts/", "manifest/history/", "manifest/publication-", "docs/releases/", "docs/archive/", "docs/development/evidence/", "reference/siemens-openness/", "reference/siemens-code-snippets/", "third_party/"];
        string[] evidence = ["CHANGELOG.md", "manifest/ecosystem-validation.json", "manifest/release-build.json", "manifest/multi-version-build.json", "manifest/configurator-build.json"];
        string[] extensions = [".cs", ".csproj", ".props", ".targets", ".ps1", ".psm1", ".py", ".md", ".json", ".yml", ".yaml", ".config", ".bat", ".cmd", ".sh", ".toml", ".xml", ".xaml", ".svg"];
        var oldEngine = "TiaMcp" + "Server"; var oldLauncher = "TiaMcp" + "Configurator";
        var pattern = new Regex(oldEngine + @"\.(?:exe|dll|deps\.json|runtimeconfig\.json)\b|" + oldLauncher + @"\.exe\b", RegexOptions.IgnoreCase);
        var identity = new Regex("(?:Get-Process|GetProcessesByName|InternalsVisibleTo|Assembly\\.Load|<AssemblyName>|-match).*(?:" + oldEngine + "|" + oldLauncher + ")(?:[\"'<)|])", RegexOptions.IgnoreCase);
        var errors = new List<string>();
        foreach (var name in names.Distinct().Order(StringComparer.Ordinal))
        {
            if (historical.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || evidence.Contains(name) || name.Split('/').Any(Skip.Contains)) continue;
            if (pattern.IsMatch(name)) errors.Add("Retired product filename: " + name);
            if (!extensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && name != ".gitignore") continue;
            var path = Path.Combine(root, name); if (!File.Exists(path)) continue;
            var cleanup = new HashSet<string>();
            if (name == DeliveryRules.PolicyPath)
            {
                var rules = JsonNode.Parse(File.ReadAllText(path))!;
                cleanup = DeliveryRules.Strings(rules["exclude"]!["files"]).Intersect(DeliveryRules.Strings(rules["legacyCleanup"]!["files"])).ToHashSet();
            }
            var line = 0;
            foreach (var text in Repository.ReadSource(path).Split('\n'))
            {
                line++;
                if (cleanup.Count > 0) try { if (cleanup.Contains(JsonNode.Parse(text.Trim().TrimEnd(','))?.GetValue<string>() ?? "")) continue; } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
                if (pattern.IsMatch(text) || identity.IsMatch(text)) errors.Add($"{name}:{line}: retired product reference");
            }
        }
        return errors;
    }
    internal static List<string> ChangelogErrors(string root)
    {
        var release = Regex.Match(Repository.ReadSource(Path.Combine(root, "Version.props")), @"<TiaMcpRelease>([^<]+)</TiaMcpRelease>");
        var entry = Regex.Match(Repository.ReadSource(Path.Combine(root, "CHANGELOG.md")), @"^## \[(\d+\.\d+\.\d+)\]", RegexOptions.Multiline);
        if (!release.Success || !entry.Success) return ["CHANGELOG.md or Version.props has no release version"];
        return release.Groups[1].Value.Trim() == entry.Groups[1].Value ? [] : [$"Newest CHANGELOG entry {entry.Groups[1].Value} differs from Version.props {release.Groups[1].Value.Trim()}; write the entry in the release commit (release command), keep drafts in docs/releases"];
    }
    internal static List<string> ArchiveErrors(string root)
    {
        var archive = Path.Combine(root, "manifest/history/contracts-v3");
        var errors = new List<string>();
        foreach (var category in new[] { "baseline", "responses" }) if (Directory.Exists(Path.Combine(root, "manifest/contracts", category))) errors.Add("Retired contract directory returned: manifest/contracts/" + category);
        try
        {
            var provenancePath = Path.Combine(archive, "provenance.json");
            if (ReleaseRecords.HashFile(provenancePath) != "53cd89d882e8668d5bf3fdb15d6e34b20758424552ccdbf0fbad33db5eba82ce") throw new ReleaseException("Contract archive provenance changed");
            var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;
            var expected = new[] { "baseline", "responses" }.SelectMany(c => TiaFeatures.Releases.Select(r => c + "/" + r + ".json")).ToHashSet();
            var records = provenance["files"]!.AsArray();
            if (records.Count != 16 || !expected.SetEquals(records.Select(r => r!["path"]!.GetValue<string>())) || provenance["readOnly"]?.ToJsonString() != "true") throw new ReleaseException("Contract archive provenance inventory/read-only rule changed");
            var inventory = expected.Concat(new[] { "README.md", "provenance.json" }).ToHashSet();
            var actual = Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(archive, p).Replace('\\', '/')).ToHashSet();
            if (!inventory.SetEquals(actual)) errors.Add("Contract archive inventory differs: missing=" + string.Join(",", inventory.Except(actual)) + "; extra=" + string.Join(",", actual.Except(inventory)));
            foreach (var (name, hash) in records.Select(r => (r!["path"]!.GetValue<string>(), r["sha256"]!.GetValue<string>())).Append(("README.md", provenance["readmeSha256"]!.GetValue<string>())))
            {
                var path = Path.Combine(archive, name);
                if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || ReleaseRecords.HashFile(path) != hash) errors.Add("Contract archive SHA-256 mismatch or missing file: " + name);
            }
        }
        catch (Exception ex) when (ex is IOException or ReleaseException or InvalidOperationException or System.Text.Json.JsonException) { errors.Add("Contract archive: " + ex.Message); }
        return errors;
    }
    internal static (int Count, List<string> Errors) Check(string root, bool noBinaries, bool packageMode)
    {
        var rules = DeliveryRules.Load(root);
        packageMode |= !File.Exists(Path.Combine(root, "Version.props"));
        var errors = packageMode ? new List<string>() : ArchiveErrors(root).Concat(ChangelogErrors(root)).ToList();
        string[] Enumerate() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToArray();
        var git = File.Exists(Path.Combine(root, ".git")) || Directory.Exists(Path.Combine(root, ".git"));
        var names = git ? ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], root).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries) : Enumerate();
        var tracked = git ? Repository.GitFiles(root) : names;
        // A master-side checker also validates tags predating the product rename.
        // Their bundled delivery policy and manifest are authoritative in package mode.
        var package = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest/package-manifest.json")))!;
        var oldTag = packageMode && package["entrypoints"]?["mcpServer"] is JsonValue entry && entry.TryGetValue<string>(out var entryPath) && entryPath.Replace('\\', '/').Split('/').Last().Equals("TiaMcp" + "Server.exe", StringComparison.OrdinalIgnoreCase);
        if (!oldTag)
        {
            errors.AddRange(ForbiddenScripts(tracked.Where(p => File.Exists(Path.Combine(root, p)))));
            errors.AddRange(ProductNames(root, names));
        }
        var count = 0;
        foreach (var source in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(Skip.Contains)))
        {
            count++;
            var text = Regex.Replace(Repository.ReadSource(source), @"^```[^\n]*\n.*?^```\s*$", "", RegexOptions.Multiline | RegexOptions.Singleline);
            var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
            foreach (Match match in Regex.Matches(text, "\\]\\((<[^>]+>|[^\\s)]+)(?:\\s+\"[^\"]*\")?\\)"))
            {
                var error = LocalTarget(root, source, match.Groups[1].Value);
                if (error is not null) errors.Add(relative + ": " + error);
                else if (!packageMode && rules.Delivered(relative))
                {
                    var target = Uri.UnescapeDataString(match.Groups[1].Value.Trim('<', '>').Split('#', 2)[0]);
                    if (target.Length > 0 && !Regex.IsMatch(target, @"\A[\w+.-]+:") && !rules.Resource(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, target))).Replace('\\', '/'))) errors.Add(relative + ": shipped document links outside the delivery: " + target);
                }
            }
        }
        void Required(string name, string label)
        {
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!Within(root, target)) errors.Add(label + ": external path: " + name);
            else if (noBinaries && (name.EndsWith(".exe", StringComparison.Ordinal) || name == "runtime/tools/TiaMcp.Updater.exe.config" || name.StartsWith("runtime/", StringComparison.Ordinal) && name != "runtime/README.md")) return;
            else if (!packageMode && name is "runtime/tools/TiaMcp.Updater.exe" or "runtime/tools/TiaMcp.Updater.exe.config" && File.Exists(Path.Combine(root, "bin-build/updater", Path.GetFileName(name)))) return;
            else if (!File.Exists(target) && !Directory.Exists(target)) errors.Add(label + ": missing or external path: " + name);
        }
        foreach (var (key, value) in package["entrypoints"]!.AsObject())
        {
            if (key == "mcpServerArgs") { if (!oldTag && !JsonNode.DeepEquals(value, new JsonArray("--release-key", "21"))) errors.Add("package entry mcpServerArgs: expected the default release key"); continue; }
            var name = value!.GetValue<string>();
            if (key == "bundleValidationScript" && (packageMode || !File.Exists(Path.Combine(root, name)))) continue;
            Required(name, "package entry " + key);
        }
        Required(package["cli"]!["exe"]!.GetValue<string>(), "CLI");
        foreach (var name in DeliveryRules.Strings(rules.Data["include"]!["files"]).Union(DeliveryRules.Strings(rules.Data["requiredFiles"]))) Required(name, "delivery file");
        foreach (var prefix in DeliveryRules.Strings(rules.Data["include"]!["prefixes"])) if (!noBinaries || prefix != "runtime/") Required(prefix, "delivery folder");
        var blueprint = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "templates/project-blueprints/full_plc_hmi_project.json")))!;
        foreach (var name in DeliveryRules.Strings(blueprint["requiredBundleFiles"])) Required(name, "blueprint");
        var roster = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest/tools-list.json")))!;
        var toolNames = roster["tools"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ToArray();
        if (toolNames.Length != toolNames.Distinct().Count() || toolNames.Length != roster["toolCount"]!.GetValue<int>() || toolNames.Length != package["capabilities"]!["mcpToolCount"]!.GetValue<int>()) errors.Add("Tool inventory count/uniqueness differs from package metadata");
        if (packageMode)
        {
            // Old tags carry their own required files; applying today's source-side
            // resource anchors would require assets that those releases never shipped.
            if (!oldTag) foreach (var name in BundleManifestRequirements.BundleResourcePaths) { Required(name, "bundle resource"); if (!rules.Resource(name)) errors.Add("Bundle resource excluded from delivery: " + name); }
            foreach (var name in Enumerate()) if (!rules.Delivered(name)) errors.Add("File outside delivery set: " + name);
            if (!noBinaries) foreach (var record in new[] { ("manifest/release-build.json", "runtimeFiles"), ("manifest/multi-version-build.json", "files") })
                foreach (var row in JsonNode.Parse(File.ReadAllText(Path.Combine(root, record.Item1)))![record.Item2]!.AsArray())
                { var name = row!["path"]!.GetValue<string>(); if (rules.Delivered(name)) Required(name, "recorded runtime"); }
            return (count, errors);
        }
        foreach (var row in SourceRoots.Load(root).RequiredFiles.Where(r => r.Consumers.Contains("repository"))) Required(row.Path, row.RepositoryLabel!);
        foreach (var major in new[] { "20", "21" }) foreach (var name in new[] { "TiaMcp.Runtime.dll", "TiaMcp.Adapter." + major + ".dll", "TiaMcp.Adapters.Contracts.dll" }) Required($"runtime/v{major}/worker/{name}", "engine worker dependency");
        foreach (var name in new[] { "NativeCallWeaver.dll", "NativeCallWeaver.deps.json", "NativeCallWeaver.runtimeconfig.json", "Mono.Cecil.dll" }) Required("runtime/verification/" + name, "packaged native verifier");
        string[] dependencies = ["TiaMcp.WorkerChannel.dll", "System.Text.Json.dll", "System.Text.Encodings.Web.dll", "System.IO.Pipelines.dll", "Microsoft.Bcl.AsyncInterfaces.dll", "System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll"];
        foreach (var release in TiaFeatures.Releases)
        { Required($"runtime/v{release}/TiaMcp.WorkerChannel.dll", "worker channel host"); foreach (var name in dependencies) Required($"runtime/v{release}/worker/{name}", "worker channel dependency"); }
        Required("runtime/studio/TiaMcp.WorkerChannel.dll", "Studio client channel");
        foreach (var name in dependencies) Required("runtime/studio/bridge/" + name, "Studio bridge channel dependency");
        foreach (var name in new[] { "manifest/history/contracts-v3/README.md", "manifest/history/contracts-v3/provenance.json" }.Concat(new[] { "manifest/history/contracts-v3", "manifest/contracts/v4" }.SelectMany(d => new[] { "baseline", "responses" }.SelectMany(c => TiaFeatures.Releases.Select(r => $"{d}/{c}/{r}.json"))))) Required(name, "contract snapshot");
        foreach (var kind in RatchetChecks.Baselines.Keys)
            if (RatchetChecks.Run(root, Options.Parse(["-Kind", kind], new HashSet<string>())) != 0) errors.Add(kind + " baseline check failed (see diagnostics above)");
        if (BundleLayoutChecks.Run(root) != 0) errors.Add("Bundle-layout resource check failed (see diagnostics above)");
        return (count, errors);
    }
    internal static int Run(string root, Options options)
    {
        var source = Path.Combine(root, "docs/README.md");
        if (LocalTarget(root, source, "../README.md") is not null || LocalTarget(root, source, "../__missing_repository_check__.md") is null || LocalTarget(root, source, "../../../__outside__.md") is null) throw new ReleaseException("Repository link sentinel failed", 2);
        var result = Check(root, options.Has("NoBinaries"), options.Has("PackageMode"));
        return SourceCheck.Report("Repository check", result.Errors, $"Checked {result.Count} Markdown files and repository entrypoints.");
    }
}
