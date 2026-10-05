using System;
using System.IO;
using System.Reflection;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Versioning;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Environment;

namespace TiaOpenness.Core.Adapters
{
    internal sealed class AdapterSessionFactory : ITiaSessionFactory
    {
        private readonly string path;
        private readonly string releaseKey;
        private Type adapterType;
        private string apiIdentity;

        internal AdapterSessionFactory(string path, string releaseKey)
        {
            this.path = path;
            this.releaseKey = releaseKey;
        }

        public SessionMode Mode => SessionMode.Openness;

        public void Configure(string opennessVersion)
        {
            var compiled = TiaVersionCatalog.Get(releaseKey);
            var requested = string.IsNullOrWhiteSpace(opennessVersion) ? compiled : TiaVersionCatalog.FromApiVersion(opennessVersion);
            TiaVersionCatalog.RequireMatchingEngine(requested.Key, compiled.Key);
            var installation = OpennessAssemblyResolver.Install(compiled.ApiVersion);
            var assembly = Assembly.LoadFrom(path);
            if (assembly.GetName().Name != "TiaMcp.Adapter." + releaseKey)
                throw new InvalidOperationException("The selected Studio adapter assembly does not match release " + releaseKey + ".");
            bool matchingRelease = false;
            foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                if (attribute.Key == "TiaReleaseKey") matchingRelease = attribute.Value == releaseKey;
            if (!matchingRelease)
                throw new InvalidOperationException("The selected Studio adapter metadata does not match release " + releaseKey + ".");
            var type = assembly.GetType("TiaMcp.Adapters.StudioAdapter", true);
            if (!typeof(IOpennessAdapter).IsAssignableFrom(type))
                throw new InvalidOperationException("The selected adapter does not implement the shared Studio contract.");
            apiIdentity = AssemblyName.GetAssemblyName(installation.EngineeringDllPath).FullName;
            adapterType = type;
        }

        public ITiaSession Create()
        {
            if (adapterType == null)
                throw new InvalidOperationException("Configure must run before Create so the Openness assemblies can be resolved.");
            // Reflection delays the first native type load until after resolver installation.
            // StudioAdapter construction is managed only and stays on the bridge's STA.
            var adapter = (IOpennessAdapter)Activator.CreateInstance(adapterType);
            TiaVersionCatalog.RequireMatchingEngine(releaseKey, adapter.ReleaseKey);
            if (!string.Equals(apiIdentity, adapter.ApiIdentity, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException("The selected PublicAPI does not match the Studio adapter: " + adapter.ApiIdentity, path);
            return new AdapterTiaSession(adapter);
        }
    }
}
