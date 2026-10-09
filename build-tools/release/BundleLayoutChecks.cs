using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal sealed class DeliveryRules
{
    internal const string PolicyPath = "scripts/operations/delivery-files.json";
    internal JsonObject Data { get; }
    internal DeliveryRules(JsonObject data)
    {
        Data = data;
        if (data["schemaVersion"]?.ToJsonString() != "1") throw new ReleaseException("Unsupported delivery-files schema");
        foreach (var group in new[] { "include", "exclude", "legacyCleanup" })
            foreach (var kind in new[] { "files", "prefixes" })
            {
                var rows = Strings(data[group]![kind]);
                if (rows.Distinct().Count() != rows.Length || rows.Any(p => !PlainPath(p.TrimEnd('/')) || p.EndsWith('/') != (kind == "prefixes"))) throw new ReleaseException("Invalid delivery rule list: " + group + "/" + kind);
            }
        var required = Strings(data["requiredFiles"]);
        if (required.Length == 0 || required.Distinct().Count() != required.Length || required.Any(p => !PlainPath(p) || !Delivered(p))) throw new ReleaseException("Invalid required delivery files");
    }
    internal static string[] Strings(JsonNode? node) => node is JsonArray rows ? rows.Select(r => r!.GetValue<string>()).ToArray() : throw new ReleaseException("Expected path list");
    internal static DeliveryRules Load(string root) => new(JsonNode.Parse(File.ReadAllText(Path.Combine(root, PolicyPath)))!.AsObject());
    internal static bool PlainPath(string path) => path.Length > 0 && !path.Any(c => "\\:*?[]".Contains(c)) && path.Split('/').All(p => p is not ("" or "." or ".."));
    private bool Matches(string path, string group) => Strings(Data[group]!["files"]).Contains(path) || Strings(Data[group]!["prefixes"]).Any(p => path.StartsWith(p, StringComparison.Ordinal));
    internal bool Delivered(string path) => PlainPath(path) && Matches(path, "include") && !Matches(path, "exclude");
    internal bool Resource(string path) => Delivered(path) || Delivered(path.TrimEnd('/') + "/__resource__");
}

internal static class BundleLayoutChecks
{
    internal const string Source = "src/Shared/BundleLayout.cs";
    internal const string Validator = "build-tools/release/BundleManifestRequirements.cs";
    internal const string Launcher = "src/Studio/Launcher/Launcher.cs";
    internal const string GuiProject = "src/Studio/Gui/TiaOpenness.Gui.csproj";
    internal static readonly string[] Generated = ["runtime/tools/TiaMcp.WriteGuard.exe", "runtime/tools/TiaMcp.Updater.exe", "runtime/tools/TiaMcp.Updater.exe.config"];
    internal static string[] ResourcePaths(string source)
    {
        var table = Regex.Match(source, @"new Dictionary<BundleResource, string>\s*\{(.*?)\};", RegexOptions.Singleline);
        var enumeration = Regex.Match(source, @"internal enum BundleResource\s*\{(.*?)\}", RegexOptions.Singleline);
        if (!table.Success || !enumeration.Success) throw new ReleaseException("Missing BundleResource enum or literal resource table");
        var pattern = new Regex("\\{ BundleResource\\.(\\w+), \"([^\"\\\\]+)\" \\}\\s*,?");
        var entries = pattern.Matches(table.Groups[1].Value).Cast<Match>().ToArray();
        if (pattern.Replace(table.Groups[1].Value, "").Trim().Length > 0) throw new ReleaseException("Resource table contains an unrecognized row");
        var ids = entries.Select(m => m.Groups[1].Value).ToArray();
        var paths = entries.Select(m => m.Groups[2].Value).ToArray();
        var enumIds = enumeration.Groups[1].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (entries.Length == 0 || ids.Distinct().Count() != ids.Length || !ids.Order().SequenceEqual(enumIds.Order())) throw new ReleaseException("Resource IDs must cover the enum exactly once");
        if (paths.Distinct().Count() != paths.Length) throw new ReleaseException("Duplicate resource path");
        if (paths.Any(p => p.StartsWith('/') || p.Contains(':') || p.Contains('\\') || p.Split('/').Any(s => s is "" or "." or ".."))) throw new ReleaseException("Resource path must be a plain root-relative path");
        return paths;
    }
    internal static string[] ValidatedPaths(string source)
    {
        var match = Regex.Match(source, @"BundleResourcePaths\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        if (!match.Success || !Regex.IsMatch(source, @"MissingBundleResources\s*\(.*?=>\s*BundleResourcePaths\s*\.Where\(resource\s*=>\s*!File\.Exists\(Path\.Combine\(root,\s*resource\.Replace", RegexOptions.Singleline)) throw new ReleaseException("C# bundle validator must enforce its BundleResourcePaths list");
        var rows = Regex.Matches(match.Groups[1].Value, "\"([^\"\\\\]+)\"").Select(m => m.Groups[1].Value).ToArray();
        if (rows.Length == 0 || Regex.Replace(match.Groups[1].Value, "\"[^\"\\\\]+\"|[\\s,]", "").Length > 0) throw new ReleaseException("Unrecognized C# bundle resource list");
        return rows;
    }
    internal static string[] LauncherPaths(string layout, string launcher, string project)
    {
        var anchor = Regex.Match(layout, "var studioRoot = FromAnchor\\(directory, \"([^\"]+)\"\\);");
        var assembly = Regex.Match(project, @"<AssemblyName>([^<]+)</AssemblyName>");
        var probes = Regex.Matches(launcher, "Path\\.Combine\\(root, ((?:\"[^\"\\\\]+\"\\s*,?\\s*)+)\\)");
        if (!anchor.Success || !assembly.Success || probes.Count != 1 || Regex.Matches(launcher, @"Path\.Combine\s*\(").Count != probes.Count || !launcher.Contains("string root = AppDomain.CurrentDomain.BaseDirectory;", StringComparison.Ordinal) || !launcher.Contains("string desktop = Path.Combine(root,", StringComparison.Ordinal) || !Regex.Matches(launcher, @"File\.Exists\(([^)]+)\)").Select(m => m.Groups[1].Value).SequenceEqual(new[] { "desktop" })) throw new ReleaseException("Unrecognized Launcher lookup; review every relative probe against BundleLayout");
        var actual = string.Join("/", Regex.Matches(probes[0].Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        var expected = anchor.Groups[1].Value + "/" + assembly.Groups[1].Value + ".exe";
        if (actual != expected) throw new ReleaseException($"Launcher path differs from BundleLayout/GUI output: {actual} != {expected}");
        return [actual];
    }
    internal static (int Count, List<string> Errors) Check(string root, ISet<string>? tracked)
    {
        var rules = DeliveryRules.Load(root);
        var layout = Repository.ReadSource(Path.Combine(root, Source));
        var paths = ResourcePaths(layout);
        LauncherPaths(layout, Repository.ReadSource(Path.Combine(root, Launcher)), Repository.ReadSource(Path.Combine(root, GuiProject)));
        var validated = ValidatedPaths(Repository.ReadSource(Path.Combine(root, Validator)));
        var errors = new List<string>();
        foreach (var name in tracked ?? new HashSet<string>())
            if ((name.StartsWith("runtime/", StringComparison.Ordinal) || name.StartsWith("hooks/", StringComparison.Ordinal)) && Path.GetExtension(name).ToLowerInvariant() is ".cs" or ".csproj") errors.Add("Shipped runtime/hook tree contains source: " + name);
        foreach (var path in paths)
        {
            var target = Path.Combine(root, path);
            if (!File.Exists(target) && !Directory.Exists(target) && !(tracked is not null && Generated.Contains(path))) errors.Add("Missing resource: " + path);
            if (tracked is not null && !(Directory.Exists(target) ? tracked.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal)) : tracked.Contains(path)) && !Generated.Contains(path)) errors.Add("Resource is not in the Git file set: " + path);
            if (!validated.Contains(path)) errors.Add("Resource is not checked by the C# bundle validator: " + path);
            if (!rules.Resource(path)) errors.Add("Resource is not in the delivery set: " + path);
            if (Directory.Exists(target))
                foreach (var child in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                    if ((tracked is null || tracked.Contains(relative)) && !rules.Delivered(relative)) errors.Add("Resource child is not in the delivery set: " + relative);
                }
        }
        return (paths.Length, errors);
    }
    internal static int Run(string root)
    {
        var top = ProcessRunner.Run("git", ["rev-parse", "--show-toplevel"], root);
        var tracked = top.ExitCode == 0 && Path.GetFullPath(top.StandardOutput.Trim()).Equals(root, StringComparison.OrdinalIgnoreCase) ? Repository.GitFiles(root).ToHashSet(StringComparer.Ordinal) : null;
        var (count, errors) = Check(root, tracked);
        return SourceCheck.Report("Bundle layout", errors, $"{count} resources checked.");
    }
}
