using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringAssemblyIdentity
    {
        internal static int? PathVersion(string? path) => TiaOpenness.Shared.OpennessEnvironment.PathVersion(path);

        internal static void RequireMatch(AssemblyName requested, AssemblyName actual, string path)
        {
            if (!string.Equals(requested.FullName, actual.FullName, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException("TIA Portal Openness assembly identity mismatch. Requested "
                    + requested.FullName + "; found " + actual.FullName + ". Select the matching engine and PublicAPI directory.", path);
        }
    }
}
