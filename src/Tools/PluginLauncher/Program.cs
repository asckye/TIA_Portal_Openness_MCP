using TiaMcp.Versioning;
using TiaOpenness.Core.Environment;
using TiaOpenness.Shared;
using System.Text.Json;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace TiaMcp.PluginLauncher;

public sealed record LauncherSelection(string Root, string Key, string Host)
{
    public static LauncherSelection Resolve(string baseDirectory, string? bundleRoot, string? releaseKey,
        IEnumerable<string>? installedVersions = null)
    {
        // Resolve the exact configured root; a bad override must never select the cache copy.
        string root = BundleLayout.ResolveRoot(baseDirectory, null, bundleRoot)
            ?? throw new IOException("Cannot locate the plugin bundle from " + baseDirectory);
        root = Path.GetFullPath(root);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(BundleLayout.RequirePath(root, "manifest/package-manifest.json"))))
            if (!manifest.RootElement.TryGetProperty("packageType", out var type) ||
                type.GetString() != "tia-portal-mcp-complete-offline-delivery-bundle")
                throw new IOException("Selected root is not a TIA Portal MCP delivery bundle: " + root);
        string key = releaseKey ?? WorkbenchReleaseSelection.DefaultKey(root, installedVersions);
        var version = TiaVersionCatalog.RequireRunnable(key);
        string host = BundleLayout.RequirePath(root, "runtime/" + version.RuntimeDirectory + "/TiaMcp.FoundationHost.exe");
        BundleLayout.RequirePath(root, "runtime/" + version.RuntimeDirectory + "/worker/" + BundleLayout.GetProduct(key).WorkerExecutable);
        return new LauncherSelection(root, key, host);
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var selection = LauncherSelection.Resolve(AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT"),
                Environment.GetEnvironmentVariable("TIA_MCP_RELEASE_KEY"));
            Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", selection.Root);
            return HostProcess.Run(selection.Host, ["--release-key", selection.Key, .. args]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("TIA MCP plugin launcher: " + ex.Message +
                " Check TIA_MCP_BUNDLE_ROOT and TIA_MCP_RELEASE_KEY.");
            return 1;
        }
    }
}
