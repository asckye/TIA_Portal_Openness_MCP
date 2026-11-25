#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcp.Adapters.Native.Vci;

internal static class VciExportPolicy
{
    // Siemens' ExportObject example uses this path relative to Workspace.RootPath.
    internal static DirectoryInfo RootDirectory() => new DirectoryInfo(@"\");

    internal static bool IsKnownNonSd(string? format) => Is(format, "SimaticML") || Is(format, "xml") || Is(format, "liblink");

    private static bool Is(string? value, string name) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

    internal static string? PreferredFormat(IEnumerable<string> formats)
    {
        var available = formats.ToArray();
        return available.FirstOrDefault(f => Is(f, "SimaticSd") || Is(f, "s7dcl"))
            ?? available.FirstOrDefault(f => Is(f, "SimaticML"))
            ?? available.FirstOrDefault(f => Is(f, "xml"))
            ?? available.FirstOrDefault();
    }
}
