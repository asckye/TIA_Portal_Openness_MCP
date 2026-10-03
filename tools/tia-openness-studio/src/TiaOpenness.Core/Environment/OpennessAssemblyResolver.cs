using System;
using System.IO;
using System.Reflection;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Environment
{
    /// <summary>
    /// Redirects <c>Siemens.*</c> assembly loads to the TIA Portal install directory.
    /// Siemens ships the Openness assemblies only inside the product folder, so a plain
    /// reference resolves at compile time and fails at run time without this hook.
    ///
    /// Install it before touching any type that mentions Siemens.Engineering &#8212; the JIT
    /// resolves references when a method is first compiled, not when it is called, so the
    /// call site must sit behind <see cref="System.Runtime.CompilerServices.MethodImplAttribute"/>
    /// with NoInlining.
    /// </summary>
    public static class OpennessAssemblyResolver
    {
        private static readonly object Gate = new object();
        private static bool _installed;
        private static string _probeDirectory;

        /// <summary>The install this resolver is bound to, or null when not installed.</summary>
        public static OpennessInstallation BoundInstallation { get; private set; }

        /// <summary>
        /// Bind to <paramref name="version"/> (or the newest installed when null).
        /// </summary>
        /// <exception cref="OpennessNotInstalledException">No matching installation.</exception>
        public static OpennessInstallation Install(string version)
        {
            lock (Gate)
            {
                var install = OpennessLocator.Resolve(version);
                if (install == null)
                {
                    throw new OpennessNotInstalledException(version);
                }

                if (_installed)
                {
                    // The CLR caches resolved assemblies per AppDomain, so a second version
                    // cannot be bound in the same process. Start another bridge instead.
                    if (!string.Equals(BoundInstallation.Version, install.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "This process is already bound to Openness " + BoundInstallation.Version +
                            " and cannot switch to " + install.Version +
                            ". Start a separate bridge process for the other version.");
                    }
                    return BoundInstallation;
                }

                _probeDirectory = install.PublicApiDirectory;
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                _installed = true;
                BoundInstallation = install;
                return install;
            }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name).Name;
            if (requested == null || !requested.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var candidate = Path.Combine(_probeDirectory, requested + ".dll");
            if (File.Exists(candidate))
            {
                return Assembly.LoadFrom(candidate);
            }

            return null;
        }
    }

    /// <summary>Thrown when no TIA Portal Openness installation matches the request.</summary>
    public class OpennessNotInstalledException : Exception
    {
        public OpennessNotInstalledException(string requestedVersion)
            : base(BuildMessage(requestedVersion))
        {
            RequestedVersion = requestedVersion;
        }

        public string RequestedVersion { get; }

        private static string BuildMessage(string requestedVersion)
        {
            return string.IsNullOrWhiteSpace(requestedVersion)
                ? "No TIA Portal Openness installation was found on this machine. Run 'doctor' for details."
                : "TIA Portal Openness " + requestedVersion + " was not found on this machine. Run 'doctor' for details.";
        }
    }
}
