namespace TiaMcp.ReleaseTool;

internal static class BundleManifestRequirements
{
    internal static readonly string[] BundleResourcePaths =
    [
        "manifest/package-manifest.json",
        "manifest/delivery.json",
        "reference/siemens-openness/skills",
        "reference/siemens-openness/UPSTREAM.json",
        "reference/v21-ecosystem.json",
        "scripts/ecosystem/plc_tools_bridge.py",
        "scripts/ecosystem/simaticml_decode_bridge.py",
        "runtime/tools/TiaMcp.WriteGuard.exe",
        "runtime/tools/TiaMcp.Updater.exe",
        "templates"
    ];

    private static readonly HashSet<string> GeneratedBundleResources = new(StringComparer.Ordinal)
    {
        "runtime/tools/TiaMcp.WriteGuard.exe",
        "runtime/tools/TiaMcp.Updater.exe"
    };

    internal static IEnumerable<string> GuiRequiredPaths(string root) =>
        new[] { "TiaOpenness.exe", "docs/getting-started/configuration.md" }
            .Concat(SourceRoots.Load(root).RequiredPaths("bundle"));

    internal static IReadOnlyList<string> MissingBundleResources(string root, bool package, bool noBinaries) => BundleResourcePaths
        .Where(resource => !File.Exists(Path.Combine(root, resource.Replace('/', Path.DirectorySeparatorChar))) &&
                           !Directory.Exists(Path.Combine(root, resource.Replace('/', Path.DirectorySeparatorChar))) &&
                           !HasGeneratedBundleResource(root, resource, package, noBinaries))
        .ToArray();

    private static bool HasGeneratedBundleResource(string root, string resource, bool package, bool noBinaries)
    {
        if (!GeneratedBundleResources.Contains(resource) || package) return false;
        if (noBinaries) return true;
        var buildOutput = resource switch
        {
            "runtime/tools/TiaMcp.WriteGuard.exe" => "src/Tools/WriteGuard/bin/Release/net10.0/TiaMcp.WriteGuard.exe",
            "runtime/tools/TiaMcp.Updater.exe" => "bin-build/updater/TiaMcp.Updater.exe",
            _ => ""
        };
        return buildOutput.Length != 0 && File.Exists(Path.Combine(root, buildOutput.Replace('/', Path.DirectorySeparatorChar)));
    }
}
