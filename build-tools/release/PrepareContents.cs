using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
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

    internal static SortedDictionary<string, string> Contents(string root, IEnumerable<string> tracked, Func<string, bool> delivered)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string relative, string source)
        {
            if (delivered(relative) && !Excluded(relative) && File.Exists(source)) files[relative] = source;
        }
        foreach (var relative in tracked) Add(relative, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var runtime = Path.Combine(root, "runtime");
        if (Directory.Exists(runtime))
            foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
                Add(Path.GetRelativePath(root, file).Replace('\\', '/'), file);
        foreach (var relative in BuiltFiles) Add(relative, Path.Combine(root, relative));
        foreach (var (relative, built) in PrepareBuild.UpdaterOutputs) Add(relative, Path.Combine(root, built.Replace('/', Path.DirectorySeparatorChar)));
        return files;
    }

    internal static bool Excluded(string relative) =>
        relative.StartsWith("runtime/verification/", StringComparison.Ordinal) ||
        relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        relative.Split('/')[^1].StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase);

    internal static readonly string[] BuiltFiles = ["TiaOpenness.exe"];

    internal static IReadOnlyList<string> RequiredPaths(string stage, DeliveryRules rules)
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

    internal static IEnumerable<string> TestPolicyAssemblyErrors(string root) =>
        Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(root, "runtime")) ? Directory.EnumerateFiles(Path.Combine(root, "runtime"), "*.dll", SearchOption.AllDirectories) : [])
            .Where(assembly => Encoding.UTF8.GetString(File.ReadAllBytes(assembly)).Contains("TiaMcpTestPolicy", StringComparison.Ordinal))
            .Select(assembly => "Test-only behavior policy assembly cannot enter a release bundle: " + assembly);
}
