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

    internal static string? PreferredFormat(IEnumerable<string> formats)
    {
        var available = formats.ToArray();
        return available.FirstOrDefault(f => f.IndexOf("s7dcl", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? available.FirstOrDefault(f => f.IndexOf("simatic", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? available.FirstOrDefault(f => f.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? available.FirstOrDefault();
    }
}
