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
            if (install == null) return Unavailable("No supported V20/V21 Openness installation was found. Run Doctor.");
            int major = Version.Parse(install.Version).Major;
            TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(major);
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adapters", "v" + major, "TiaOpenness.Openness.dll");
            if (!File.Exists(path)) return Unavailable("Build the V" + major + " native adapter with scripts/build/Build-Studio.ps1 and deploy it under bridge/adapters/v" + major + ".");
            OpennessAssemblyResolver.Install(install.Version);
            var type = Assembly.LoadFrom(path).GetType("TiaOpenness.Openness.OpennessSessionFactory", true);
            var factory = (ITiaSessionFactory)Activator.CreateInstance(type);
            factory.Configure(install.Version);
            LastDecision = "direct Openness V" + major;
            return factory;
        }
        private static ITiaSessionFactory Unavailable(string reason)
        { LastDecision = reason; return new UnavailableSessionFactory(reason); }
    }
}
