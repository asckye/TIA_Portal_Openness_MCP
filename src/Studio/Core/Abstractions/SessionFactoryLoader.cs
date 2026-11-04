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
            string folder = key == "14sp1" ? "V14Sp1" : key == "15.1" ? "V15_1" : "V" + key;
            string path = AdapterPath(AppDomain.CurrentDomain.BaseDirectory, key);
            if (!File.Exists(path)) return Unavailable("Build the " + release.DisplayName + " native adapter with \"dotnet build src/Studio/Openness/" + folder + "/StudioOpenness." + folder + ".csproj -p:SiemensEngineeringDirectory=<SDK>\" and deploy it under bridge/adapters/v" + key + ".");
            OpennessAssemblyResolver.Install(install.Version);
            var type = Assembly.LoadFrom(path).GetType("TiaOpenness.Openness.OpennessSessionFactory", true);
            var factory = (ITiaSessionFactory)Activator.CreateInstance(type);
            factory.Configure(install.Version);
            LastDecision = "direct Openness V" + key;
            return factory;
        }
        internal static string AdapterPath(string baseDirectory, string key)
        {
            return TiaOpenness.Shared.BundleLayout.WorkbenchAdapterPath(baseDirectory, key, "TiaOpenness.Openness.dll");
        }
        private static ITiaSessionFactory Unavailable(string reason)
        { LastDecision = reason; return new UnavailableSessionFactory(reason); }
    }
}
