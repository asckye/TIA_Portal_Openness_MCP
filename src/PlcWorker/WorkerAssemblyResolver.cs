using System;
using System.IO;
using System.Reflection;

namespace TiaMcp.PlcWorker;

internal static class WorkerAssemblyResolver
{
    internal static string? SelectFile(string api, string requestedName, Func<string, AssemblyName>? identityOf = null)
    {
        var wanted = new AssemblyName(requestedName);
        if (wanted.Name == null || !wanted.Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
        var file = Path.Combine(api, wanted.Name + ".dll");
        // Siemens.Engineering.Contract and Siemens.Engineering.ClientAdapter.Interfaces are not in the
        // PublicAPI folder; TIA's own resolution loads them. Throwing here blocked it on every release.
        if (!File.Exists(file)) return null;
        var actual = (identityOf ?? AssemblyName.GetAssemblyName)(file);
        if (!string.Equals(wanted.FullName, actual.FullName, StringComparison.OrdinalIgnoreCase))
            throw new FileLoadException("Selected SDK identity mismatch: " + wanted.FullName + " / " + actual.FullName, file);
        return file;
    }
}
