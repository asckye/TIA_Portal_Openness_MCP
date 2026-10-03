using System;
using System.Runtime.CompilerServices;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Environment;

namespace TiaOpenness.Openness
{
    /// <summary>
    /// Entry point the bridge discovers by reflection. Nothing here may touch a
    /// Siemens.Engineering type until <see cref="Configure"/> has installed the assembly
    /// resolver &#8212; the JIT resolves an assembly when it compiles a method, not when the
    /// method runs, so every first touch sits behind a NoInlining boundary.
    /// </summary>
    public sealed class OpennessSessionFactory : ITiaSessionFactory
    {
        private OpennessInstallation _installation;

        public SessionMode Mode { get { return SessionMode.Openness; } }

        public void Configure(string opennessVersion)
        {
#if TIA_V20
            const int compiledMajor = 20;
#else
            const int compiledMajor = 21;
#endif
            int requestedMajor = string.IsNullOrWhiteSpace(opennessVersion) ? compiledMajor : Version.Parse(opennessVersion).Major;
            TiaMcp.Versioning.TiaVersionCatalog.RequireMatchingEngine(requestedMajor, compiledMajor);
            _installation = OpennessAssemblyResolver.Install(compiledMajor + ".0");
        }

        public ITiaSession Create()
        {
            if (_installation == null)
            {
                throw new InvalidOperationException(
                    "Configure must run before Create so the Openness assemblies can be resolved.");
            }
            return CreateCore(_installation.Version);
        }

        /// <summary>
        /// Separated and never inlined so that jitting <see cref="Create"/> does not force a load
        /// of Siemens.Engineering before the resolver is in place.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ITiaSession CreateCore(string version)
        {
            return new OpennessSession(version);
        }
    }
}
