#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using TiaMcp.Versioning;

namespace TiaOpenness.Shared
{
    internal enum BundleResource
    {
        PackageManifest,
        DeliveryManifest,
        OpennessGuides,
        OpennessProvenance,
        V21EcosystemCatalog,
        PlcToolsBridge,
        SimaticMlDecodeBridge,
        WriteGuardExecutable,
        Updater,
        Templates
    }

#if TIA_BUNDLE_LAYOUT_PUBLIC
    public sealed class BundleResourceUnavailableException : IOException
#else
    internal sealed class BundleResourceUnavailableException : IOException
#endif
    {
        public string Resource { get; }
        public BundleResourceUnavailableException(string resource)
            : base("RESOURCE_UNAVAILABLE: Expected bundle resource at " + resource) { Resource = resource; }
    }

    // All linked copies share the startup selection using only BCL values.
#if TIA_BUNDLE_LAYOUT_PUBLIC
    public static class BundleLayout
#else
    internal static class BundleLayout
#endif
    {
        private const string SelectionKey = "TiaOpenness.Shared.BundleLayout.v4";

        public static string ExtractRootOption(string[] args, out string[] remaining)
        {
            string root = null;
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--bundle-root", StringComparison.OrdinalIgnoreCase))
                { rest.Add(args[i]); continue; }
                if (root != null) throw new ArgumentException("Repeated option: --bundle-root.");
                if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--bundle-root requires an absolute path.");
                root = args[i];
                if (!IsAbsolute(root)) throw new ArgumentException("--bundle-root requires an absolute path: " + root);
            }
            remaining = rest.ToArray();
            return root;
        }

        public static string Initialize(string baseDirectory, string explicitRoot = null)
        {
            var root = ResolveRoot(baseDirectory, explicitRoot, Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT"));
            if (root == null) throw new BundleResourceUnavailableException(Combine(baseDirectory, RelativePath(BundleResource.PackageManifest)));
            AppDomain.CurrentDomain.SetData(SelectionKey, root);
            Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", root);
            return root;
        }

        public static string RequireRoot(string baseDirectory, string explicitRoot = null)
        {
            return FindRoot(baseDirectory, explicitRoot)
                ?? throw new BundleResourceUnavailableException(Combine(baseDirectory, RelativePath(BundleResource.PackageManifest)));
        }

        // Pure selection inputs support the complete precedence matrix without process mutations.
        public static string ResolveRoot(string baseDirectory, string explicitRoot, string environmentRoot)
        {
            string configured = explicitRoot ?? environmentRoot;
            if (configured != null)
            {
                if (!IsAbsolute(configured)) throw new ArgumentException("Bundle root must be an absolute path: " + configured);
                var marker = Combine(configured, RelativePath(BundleResource.PackageManifest));
                if (!Directory.Exists(configured) || !File.Exists(marker)) throw new BundleResourceUnavailableException(marker);
                return configured;
            }
            return FindAnchorRoot(baseDirectory);
        }

        private static bool IsAbsolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
            string prefix = Path.GetPathRoot(path);
            return Path.DirectorySeparatorChar != '\\' || prefix.Length >= 3;
        }

        // Check-BundleLayout.py checks this table against Git and Validate-Bundle.
        private static readonly Dictionary<BundleResource, string> ResourcePaths =
            new Dictionary<BundleResource, string>
            {
                { BundleResource.PackageManifest, "manifest/package-manifest.json" },
                { BundleResource.DeliveryManifest, "manifest/delivery.json" },
                { BundleResource.OpennessGuides, "reference/siemens-openness/skills" },
                { BundleResource.OpennessProvenance, "reference/siemens-openness/UPSTREAM.json" },
                { BundleResource.V21EcosystemCatalog, "reference/v21-ecosystem.json" },
                { BundleResource.PlcToolsBridge, "scripts/ecosystem/plc_tools_bridge.py" },
                { BundleResource.SimaticMlDecodeBridge, "scripts/ecosystem/simaticml_decode_bridge.py" },
            { BundleResource.WriteGuardExecutable, "runtime/tools/TiaMcp.WriteGuard.exe" },
                { BundleResource.Updater, "runtime/tools/TiaMcp.Updater.exe" },
                { BundleResource.Templates, "templates" }
            };

        internal static string RelativePath(BundleResource resource)
        {
            return ResourcePaths[resource];
        }

        internal static string FindResource(BundleResource resource, string baseDirectory, string explicitRoot = null)
        {
            var relative = RelativePath(resource);
            var root = FindRoot(baseDirectory, explicitRoot);
            if (root == null) return null;
            var path = Combine(root, relative);
            bool directory = resource == BundleResource.OpennessGuides || resource == BundleResource.Templates;
            return (directory ? Directory.Exists(path) : File.Exists(path)) ? path : null;
        }

        public static string FindRoot(string baseDirectory, string explicitRoot = null)
        {
            if (explicitRoot == null && AppDomain.CurrentDomain.GetData(SelectionKey) is string selected) return selected;
            return ResolveRoot(baseDirectory, explicitRoot, Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT"));
        }

        // Studio keeps its pre-P6-37 null/error policy until P6-38. Do not consume
        // the engine's startup selection or environment override at this entry point.
        internal static string FindRootForStudio(string baseDirectory, string explicitRoot = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot)) return HasMarker(explicitRoot) ? explicitRoot : null;
            return FindAnchorRoot(baseDirectory, true);
        }

        internal static string FindResourceForStudio(BundleResource resource, string baseDirectory, string explicitRoot = null)
        {
            var root = FindRootForStudio(baseDirectory, explicitRoot);
            if (root == null) return null;
            var path = Combine(root, RelativePath(resource));
            bool directory = resource == BundleResource.OpennessGuides || resource == BundleResource.Templates;
            return (directory ? Directory.Exists(path) : File.Exists(path)) ? path : null;
        }

        private static string FindAnchorRoot(string baseDirectory, bool studio = false)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathRooted(baseDirectory)) return null;

            var directory = new DirectoryInfo(baseDirectory);
            if (!studio && HasMarker(directory.FullName)) return directory.FullName == directory.Root.FullName ? directory.FullName
                : directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var version in TiaVersionCatalog.Runnable)
            {
                var root = FromAnchor(directory, "runtime/" + version.RuntimeDirectory);
                if (root != null) return root;
            }
            var studioRoot = FromAnchor(directory, "runtime/studio");
            if (studioRoot != null) return studioRoot;
            studioRoot = FromAnchor(directory, "runtime/studio/bridge");
            if (studioRoot != null) return studioRoot;

            foreach (var configuration in new[] { "Release", "Debug" })
            {
                foreach (var version in TiaVersionCatalog.Runnable)
                {
                    if (!version.IsFullEngine) continue;
                    var root = FromAnchor(directory, "src/Engine/"
                        + version.EngineOutputDirectory + "/" + configuration + "/net48");
                    if (root != null) return root;
                }
                var gui = "src/Studio/Gui/bin/" + configuration + "/net10.0-windows";
                var guiRoot = FromAnchor(directory, gui);
                if (guiRoot != null) return guiRoot;
                guiRoot = FromAnchor(directory, gui + "/bridge");
                if (guiRoot != null) return guiRoot;
                var bridgeRoot = FromAnchor(directory,
                    "src/Studio/Bridge/bin/" + configuration + "/net48");
                if (bridgeRoot != null) return bridgeRoot;
                if (!studio)
                {
                    var foundationRoot = FromAnchor(directory,
                        "src/FoundationHost/bin/" + configuration + "/net10.0");
                    if (foundationRoot != null) return foundationRoot;
                    foreach (var harness in new[] {
                        "TiaMcpServer.HttpTests/bin/" + configuration + "/net48",
                        "TiaMcpServer.LegacyHostTests/bin/" + configuration + "/net10.0",
                        "TiaMcpServer.Tests/bin/" + configuration + "/net10.0" })
                    {
                        var harnessRoot = FromAnchor(directory, "tests/Engine/" + harness);
                        if (harnessRoot != null) return harnessRoot;
                    }
                }
            }
            return null;
        }

        internal static string RequireResource(BundleResource resource, string baseDirectory, string explicitRoot = null)
        {
            return RequirePath(RequireRoot(baseDirectory, explicitRoot), RelativePath(resource),
                resource == BundleResource.OpennessGuides || resource == BundleResource.Templates);
        }

        public static string RequirePath(string root, string relative, bool directory = false)
        {
            var path = Combine(root, relative.Replace('\\', '/'));
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("Bundle resource must be under the selected root: " + path);
            if (!(directory ? Directory.Exists(path) : File.Exists(path))) throw new BundleResourceUnavailableException(path);
            return path;
        }

        public static string EngineExecutablePath(string root, string releaseKey, string baseDirectory)
        {
            var version = TiaVersionCatalog.RequireRunnable(releaseKey);
            string name = version.IsFullEngine ? "TiaMcp.Engine.V" + version.MajorVersion + ".exe" : "TiaMcp.FoundationHost.exe";
            var output = new DirectoryInfo(baseDirectory);
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                foreach (var sourceVersion in TiaVersionCatalog.Runnable)
                {
                    if (!sourceVersion.IsFullEngine) continue;
                    if (SameRoot(FromAnchor(output, "src/Engine/" + sourceVersion.EngineOutputDirectory + "/" + configuration + "/net48"), root))
                        return version.IsFullEngine ? Combine(root, "src/Engine/" + version.EngineOutputDirectory + "/" + configuration + "/net48/" + name)
                            : Combine(root, "src/FoundationHost/bin/" + configuration + "/net10.0/" + name);
                }
                if (SameRoot(FromAnchor(output, "src/FoundationHost/bin/" + configuration + "/net10.0"), root))
                    return version.IsFullEngine ? Combine(root, "src/Engine/" + version.EngineOutputDirectory + "/" + configuration + "/net48/" + name)
                        : Combine(root, "src/FoundationHost/bin/" + configuration + "/net10.0/" + name);
            }
            return Combine(root, "runtime/" + version.RuntimeDirectory + "/" + name);
        }

        public static string RequireEngine(string releaseKey, string baseDirectory, string explicitRoot = null)
        {
            var path = EngineExecutablePath(RequireRoot(baseDirectory, explicitRoot), releaseKey, baseDirectory);
            if (!File.Exists(path)) throw new BundleResourceUnavailableException(path);
            return path;
        }

        public sealed class Product
        {
            public string ReleaseKey { get; private set; }
            public string RuntimeDirectory { get; private set; }
            public string Executable { get; private set; }
            public string VersionOption { get; private set; }
            public string WorkerExecutable { get { return "TiaMcp.PlcWorker." + ReleaseKey + ".exe"; } }
            internal Product(string key, string executable, string option)
            {
                ReleaseKey = key;
                RuntimeDirectory = TiaVersionCatalog.RequireRunnable(key).RuntimeDirectory;
                Executable = executable;
                VersionOption = option;
            }
        }

        // Workbench commands and health checks consume this release-to-product table.
        private static readonly Dictionary<string, Product> Products = new Dictionary<string, Product>
        {
            { "14sp1", new Product("14sp1", "TiaMcp.FoundationHost.exe", "--release-key") },
            { "15.1", new Product("15.1", "TiaMcp.FoundationHost.exe", "--release-key") },
            { "16", new Product("16", "TiaMcp.FoundationHost.exe", "--release-key") },
            { "17", new Product("17", "TiaMcp.FoundationHost.exe", "--release-key") },
            { "18", new Product("18", "TiaMcp.FoundationHost.exe", "--release-key") },
            { "19", new Product("19", "TiaMcp.FoundationHost.exe", "--release-key") },
            { "20", new Product("20", "TiaMcp.Engine.V20.exe", "--tia-major-version") },
            { "21", new Product("21", "TiaMcp.Engine.V21.exe", "--tia-major-version") }
        };
        public const string StudioBridgeExecutable = "TiaOpenness.Bridge.exe";

        public static Product GetProduct(string releaseKey)
        {
            TiaVersionCatalog.RequireRunnable(releaseKey);
            return Products[releaseKey];
        }

        public static string ResolveWorkbenchRoot(string baseDirectory, string explicitRoot, string environmentRoot)
        {
            var root = ResolveRoot(baseDirectory, explicitRoot, environmentRoot);
            if (root != null || explicitRoot != null || environmentRoot != null) return root;
            foreach (var configuration in new[] { "Release", "Debug" })
            foreach (var variant in new[] { "", "shared-adapter/" })
                if ((root = WorkbenchDevelopmentRoot(baseDirectory, configuration, variant)) != null) return root;
            return null;
        }

        public static string RequireWorkbenchRoot(string baseDirectory, string explicitRoot = null)
            => FindWorkbenchRoot(baseDirectory, explicitRoot)
                ?? throw new BundleResourceUnavailableException(Combine(baseDirectory, RelativePath(BundleResource.PackageManifest)));

        public static string FindWorkbenchRoot(string baseDirectory, string explicitRoot = null)
        {
            if (explicitRoot == null && AppDomain.CurrentDomain.GetData(SelectionKey) is string selected) return selected;
            return ResolveWorkbenchRoot(baseDirectory, explicitRoot, Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT"));
        }

        public static string InitializeWorkbench(string baseDirectory, string[] args, out string[] remaining)
        {
            var explicitRoot = ExtractRootOption(args, out remaining);
            var root = ResolveWorkbenchRoot(baseDirectory, explicitRoot, Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT"));
            if (root == null) throw new BundleResourceUnavailableException(Combine(baseDirectory, RelativePath(BundleResource.PackageManifest)));
            AppDomain.CurrentDomain.SetData(SelectionKey, root);
            Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", root);
            return root;
        }

        private static string WorkbenchDevelopmentRoot(string baseDirectory, string configuration, string variant)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathRooted(baseDirectory)) return null;
            var output = new DirectoryInfo(baseDirectory);
            var gui = "src/Studio/Gui/bin/" + configuration + "/" + variant + "net10.0-windows";
            var root = FromAnchor(output, gui) ?? FromAnchor(output, gui + "/bridge")
                ?? FromAnchor(output, "src/Studio/Bridge/bin/" + configuration + "/" + variant + "net48");
            if (root != null) return root;
            foreach (var harness in new[] { "TiaOpenness.Core.Tests", "TiaOpenness.Gui.Tests", "TiaOpenness.Configuration.Tests" })
            {
                var anchor = "tests/Studio/" + harness + "/bin/" + configuration + "/" + variant + "net10.0-windows";
                root = FromAnchor(output, anchor) ?? FromAnchor(output, anchor + "/bridge");
                if (root != null) return root;
            }
            return null;
        }

        public static string WorkbenchEnginePath(string root, string releaseKey, string baseDirectory)
        {
            var product = GetProduct(releaseKey);
            var version = TiaVersionCatalog.RequireRunnable(releaseKey);
            foreach (var configuration in new[] { "Release", "Debug" })
            foreach (var variant in new[] { "", "shared-adapter/" })
                if (SameRoot(WorkbenchDevelopmentRoot(baseDirectory, configuration, variant), root))
                    return Combine(root, version.IsFullEngine
                        ? "src/Engine/" + version.EngineOutputDirectory + "/" + configuration + "/net48/" + product.Executable
                        : "src/FoundationHost/bin/" + configuration + "/net10.0/" + product.Executable);
            // Preserve the engine/CLI's formal development anchors as well.
            var enginePath = EngineExecutablePath(root, releaseKey, baseDirectory);
            return Path.Combine(Path.GetDirectoryName(enginePath), product.Executable);
        }

        public static string WorkerPath(string root, string releaseKey)
        {
            var product = GetProduct(releaseKey);
            return Combine(root, "runtime/" + product.RuntimeDirectory + "/worker/" + product.WorkerExecutable);
        }

        public static string WorkbenchBridgePath(string root, string baseDirectory)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            foreach (var variant in new[] { "", "shared-adapter/" })
            {
                if (!SameRoot(WorkbenchDevelopmentRoot(baseDirectory, configuration, variant), root)) continue;
                var bridge = "src/Studio/Bridge/bin/" + configuration + "/" + variant + "net48";
                foreach (var harness in new[] { "TiaOpenness.Core.Tests", "TiaOpenness.Gui.Tests", "TiaOpenness.Configuration.Tests" })
                {
                    var anchor = "tests/Studio/" + harness + "/bin/" + configuration + "/" + variant + "net10.0-windows";
                    if (SameRoot(FromAnchor(new DirectoryInfo(baseDirectory), anchor), root)
                        || SameRoot(FromAnchor(new DirectoryInfo(baseDirectory), anchor + "/bridge"), root))
                        return Combine(root, anchor + "/bridge/" + StudioBridgeExecutable);
                }
                return Combine(root, (SameRoot(FromAnchor(new DirectoryInfo(baseDirectory), bridge), root)
                    ? bridge : "src/Studio/Gui/bin/" + configuration + "/" + variant + "net10.0-windows/bridge")
                    + "/" + StudioBridgeExecutable);
            }
            return Combine(root, "runtime/studio/bridge/" + StudioBridgeExecutable);
        }

        public static string RequireWorkbenchBridge(string baseDirectory, string explicitRoot = null)
        {
            var path = WorkbenchBridgePath(RequireWorkbenchRoot(baseDirectory, explicitRoot), baseDirectory);
            if (!File.Exists(path)) throw new BundleResourceUnavailableException(path);
            return path;
        }

        public static string WorkbenchAdapterPath(string baseDirectory, string releaseKey, string executable, string explicitRoot = null)
        {
            GetProduct(releaseKey);
            var root = RequireWorkbenchRoot(baseDirectory, explicitRoot);
            return Combine(Path.GetDirectoryName(WorkbenchBridgePath(root, baseDirectory)),
                "adapters/v" + releaseKey + "/" + executable);
        }

        public static bool IsSourceCheckout(string root)
        {
            // Git worktrees use a .git file; source archives may have no Git metadata.
            for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))
                    || (File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))
                        && File.Exists(Path.Combine(directory.FullName, "Version.props"))
                        && Directory.Exists(Path.Combine(directory.FullName, "src")))
                    || (File.Exists(Path.Combine(directory.FullName, "Version.props"))
                        && File.Exists(Path.Combine(directory.FullName, "src", "Studio", "Gui", "TiaOpenness.Gui.csproj")))) return true;
            return false;
        }

        private static bool SameRoot(string candidate, string root)
        {
            return candidate != null && string.Equals(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static string FromAnchor(DirectoryInfo directory, string anchor)
        {
            var parts = anchor.Split('/');
            for (int index = parts.Length - 1; index >= 0; index--)
            {
                if (directory == null || !string.Equals(directory.Name, parts[index], StringComparison.OrdinalIgnoreCase))
                    return null;
                directory = directory.Parent;
            }
            return directory != null && HasMarker(directory.FullName) ? directory.FullName : null;
        }

        private static bool HasMarker(string root)
        {
            return Path.IsPathRooted(root) && File.Exists(Combine(root, RelativePath(BundleResource.PackageManifest)));
        }

        private static string Combine(string root, string relative)
        {
            // Match the callers' segment-wise Path.Combine, without another GetFullPath.
            foreach (var part in relative.Split('/')) root = Path.Combine(root, part);
            return root;
        }
    }
}
