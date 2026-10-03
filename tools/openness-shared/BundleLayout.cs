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
        UpdateScript,
        Templates
    }

    // Only known output layouts establish a root. Callers retain their original
    // probing and error policy when this resolver returns null (D-G7-3).
    internal static class BundleLayout
    {
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
                { BundleResource.UpdateScript, "scripts/operations/Update-Engine.ps1" },
                { BundleResource.Templates, "templates" }
            };

        public static string RelativePath(BundleResource resource)
        {
            return ResourcePaths[resource];
        }

        public static string FindResource(BundleResource resource, string baseDirectory, string explicitRoot = null)
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
            // Keep explicit spelling, separators and error handling with the caller.
            // An invalid explicit root must not silently select another installation.
            if (!string.IsNullOrWhiteSpace(explicitRoot))
                return HasMarker(explicitRoot) ? explicitRoot : null;
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathRooted(baseDirectory)) return null;

            var directory = new DirectoryInfo(baseDirectory);
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
                    var root = FromAnchor(directory, "tools/tiaportal-mcp/src/TiaMcpServer/"
                        + version.EngineOutputDirectory + "/" + configuration + "/net48");
                    if (root != null) return root;
                }
                var gui = "tools/tia-openness-studio/src/TiaOpenness.Gui/bin/" + configuration + "/net10.0-windows";
                var guiRoot = FromAnchor(directory, gui);
                if (guiRoot != null) return guiRoot;
                guiRoot = FromAnchor(directory, gui + "/bridge");
                if (guiRoot != null) return guiRoot;
                var bridgeRoot = FromAnchor(directory,
                    "tools/tia-openness-studio/src/TiaOpenness.Bridge/bin/" + configuration + "/net48");
                if (bridgeRoot != null) return bridgeRoot;
            }
            return null;
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
