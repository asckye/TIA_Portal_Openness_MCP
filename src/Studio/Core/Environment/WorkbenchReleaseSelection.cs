using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Versioning;

namespace TiaOpenness.Core.Environment
{
    public static class WorkbenchReleaseSelection
    {
        public static string DefaultKey(string bundleRoot = null) => DefaultKey(bundleRoot, null);

        public static string DefaultKey(string bundleRoot, IEnumerable<string> installedVersions)
        {
            var root = bundleRoot ?? TiaOpenness.Shared.BundleLayout.FindWorkbenchRoot(AppContext.BaseDirectory);
            var bundled = TiaVersionCatalog.Runnable.Where(v => root != null
                && File.Exists(TiaOpenness.Shared.BundleLayout.WorkbenchEnginePath(root, v.Key, root))
                && File.Exists(TiaOpenness.Shared.BundleLayout.WorkerPath(root, v.Key))).Select(v => v.Key);
            return Select(installedVersions ?? OpennessLocator.FindAll().Select(i => i.Version), bundled);
        }

        public static string Select(IEnumerable<string> installedVersions, IEnumerable<string> bundledKeys)
        {
            var installed = new HashSet<string>(installedVersions, StringComparer.OrdinalIgnoreCase);
            var bundled = new HashSet<string>(bundledKeys, StringComparer.Ordinal);
            // With no installation or runtime, retain a catalogue selection so the environment page can explain repairs.
            return (TiaVersionCatalog.Runnable.FirstOrDefault(v => installed.Contains(v.ApiVersion)
                || installed.Contains(v.Key) || installed.Contains(v.DisplayName))
                ?? TiaVersionCatalog.Runnable.FirstOrDefault(v => bundled.Contains(v.Key))
                ?? TiaVersionCatalog.Runnable.First()).Key;
        }
    }
}
