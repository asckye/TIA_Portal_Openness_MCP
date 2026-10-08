using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Versioning;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Tests;

// Frozen pre-G7-5 bodies; only visibility changes for the differential fixture.
internal static class StudioLookupBaseline
{
    public const string UpdaterRelativePath = @"runtime/tools/TiaMcp.Updater.exe";
    internal static string FindBundleRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "manifest", "package-manifest.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("The desktop must remain inside the complete TIA MCP bundle.");
    }

    public static string Engine(string root, string versionKey)
        {
            var version = TiaVersionCatalog.RequireRunnable(versionKey);
            var candidates = new[] { Path.Combine(root, "runtime", version.RuntimeDirectory, "TiaMcp.FoundationHost.exe") };
            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null) throw new FileNotFoundException(Loc.Current.T("Config.EngineNotFound", version.DisplayName));
            return path;
        }

    internal static string DeliveryField(string root, string name)
        {
            try
            {
                string path = Path.Combine(root, "manifest", "delivery.json");
                if (!File.Exists(path)) return null;
                var json = ConfigCore.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                object value;
                return json != null && json.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : null;
            }
            catch /* swallow(parse-fallback): an unreadable or malformed delivery manifest leaves the installed release or package unknown */ { return null; }
        }

    public static string UpdaterPath(string root) { return Path.Combine(root, UpdaterRelativePath); }

    public static bool IsSourceRepository(string root) { return Directory.Exists(Path.Combine(root, ".git")); }
}
