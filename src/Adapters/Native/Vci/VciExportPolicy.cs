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

    internal static bool TargetExists(FileInfo file)
    {
        try
        {
            if ((File.GetAttributes(file.FullName) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The workspace export target must be a regular file.");
            return true;
        }
        catch (FileNotFoundException) /* swallow(env-probe): only a missing leaf is eligible for an initial XML export. */
        {
            for (var parent = file.Directory; parent != null; parent = parent.Parent)
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The workspace export path contains a reparse point.");
            return false;
        }
    }

    internal static bool? FileExists(FileInfo? file)
    {
        try { return file == null ? null : TargetExists(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }

    internal static string ExistingFile(string name) => "Target file already exists: " + name + ". " + TiaMcp.Adapters.Contracts.VersionControlResult.Reconnect;

    internal static string? PreferredFormat(IEnumerable<string> formats)
    {
        var available = formats.ToArray();
        return available.FirstOrDefault(f => f.IndexOf("s7dcl", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? available.FirstOrDefault(f => f.IndexOf("simatic", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? available.FirstOrDefault(f => f.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? available.FirstOrDefault();
    }
}
