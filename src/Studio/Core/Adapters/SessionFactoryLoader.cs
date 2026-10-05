using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaOpenness.Core.Environment;
using TiaOpenness.Core.Mock;

namespace TiaOpenness.Core.Abstractions
{
    public static class SessionFactoryLoader
    {
        public static string LastDecision { get; private set; } = "not resolved yet";
        public static ITiaSessionFactory Resolve(bool forceMock, string version = null)
        {
            if (forceMock) { LastDecision = "explicit mock"; return new MockTiaSessionFactory(); }
            var install = OpennessLocator.Resolve(version);
            if (install == null) return Unavailable("No supported V14 SP1, V15.1 or V16-V21 Openness installation was found. Run Doctor.");
            var release = TiaMcp.Versioning.TiaVersionCatalog.FromApiVersion(install.Version);
            string key = release.Key;
            string path = AdapterPath(AppDomain.CurrentDomain.BaseDirectory, key);
            if (!File.Exists(path)) return Unavailable("Build the " + release.DisplayName + " native adapter with scripts/build/Build-Studio.ps1 and deploy it under bridge/adapters/v" + key + ".");
            OpennessAssemblyResolver.Install(install.Version);
#if TIA_SHARED_ADAPTER_PATHS
            ITiaSessionFactory factory = new TiaOpenness.Core.Adapters.AdapterSessionFactory(path, key);
#else
            var type = Assembly.LoadFrom(path).GetType("TiaOpenness.Openness.OpennessSessionFactory", true);
            var factory = (ITiaSessionFactory)Activator.CreateInstance(type);
#endif
            factory.Configure(install.Version);
            LastDecision = "direct Openness V" + key;
            return factory;
        }
        internal static string AdapterPath(string baseDirectory, string key)
        {
#if TIA_SHARED_ADAPTER_PATHS
            return Path.Combine(baseDirectory, "adapters", "v" + key, "TiaMcp.Adapter." + key + ".dll");
#else
            var root = TiaOpenness.Shared.BundleLayout.FindRootForStudio(baseDirectory);
            if (root != null)
            {
                string installed = Path.Combine(root, "runtime", "studio", "bridge");
                if (string.Equals(new DirectoryInfo(baseDirectory).FullName.TrimEnd(Path.DirectorySeparatorChar),
                    installed, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(baseDirectory, "adapters", "v" + key, "TiaOpenness.Openness.dll");
            }
            // Development bridges and unrecognized layouts keep their adjacent adapters (D-G7-3).
            return Path.Combine(baseDirectory, "adapters", "v" + key, "TiaOpenness.Openness.dll");
#endif
        }
        private static ITiaSessionFactory Unavailable(string reason)
        { LastDecision = reason; return new UnavailableSessionFactory(reason); }
    }
}
