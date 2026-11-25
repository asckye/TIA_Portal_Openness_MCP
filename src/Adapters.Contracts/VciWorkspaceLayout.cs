using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

// Workspace layout of ConnectProjectToWorkspace for the UngroupedDevicesGroup (decided 2026-10-19). The ungrouped devices
// are walked under one fixed, language-independent workspace folder, because the system group's own Name is localized.
// When a root-level device or device group already owns that folder, the call is refused before anything is mapped; the
// two are never merged. Workspace folders compare case-insensitively, as Windows folders do.
public static class VciWorkspaceLayout
{
    public const string UngroupedFolder = "UngroupedDevices";

    internal static DirectoryInfo LegacyMappingDirectory(string root, string relativeWorkspacePath) =>
        new DirectoryInfo(Path.Combine(root, Path.GetDirectoryName(LegacyRelativePath(relativeWorkspacePath)) ?? ""));

    private static string LegacyRelativePath(string relativeWorkspacePath)
    {
        // V16 can prefix a mapping with a single separator to denote the workspace root.
        string path = relativeWorkspacePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (path.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) path = path.Substring(1);
        if (Path.IsPathRooted(path)) throw new ArgumentException("Expected a workspace-relative mapping path.");
        return path;
    }

    public static FileInfo LegacyMappingFile(string root, string relativeWorkspacePath)
    {
        if (!Path.IsPathRooted(root)) throw new IOException("The workspace root is unavailable.");
        var file = new FileInfo(Path.Combine(root, LegacyRelativePath(relativeWorkspacePath)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!file.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(file.Extension, ".xml", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Expected an XML file inside the workspace.");
        return file;
    }

    public static DirectoryInfo CreationDirectory(string workspaceName, string folderPath, Func<string, string> fullPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceName)) throw new ArgumentException("workspaceName is required.");
        if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentException("folderPath is required.");
        var directory = new DirectoryInfo(fullPath(folderPath.Trim()));
        if (!directory.Exists)
            throw new DirectoryNotFoundException(
                "The workspace folder does not exist. Create it first or pass an existing folder. Folder: " + directory.FullName);
        return directory;
    }

    /// <summary>The first root entry whose workspace folder is <see cref="UngroupedFolder"/>, or null.</summary>
    public static string? UngroupedFolderOwner(IEnumerable<(string Name, string Folder)> rootEntries)
        => (rootEntries ?? Enumerable.Empty<(string Name, string Folder)>())
            .Where(entry => string.Equals(entry.Folder, UngroupedFolder, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name).FirstOrDefault();

    public static AdapterPreconditionException UngroupedFolderTaken(string owner)
        => new AdapterPreconditionException("The ungrouped devices are mapped under the workspace folder '" + UngroupedFolder
            + "', which the root-level device or device group '" + owner + "' already uses. Rename that device or group, or move the"
            + " ungrouped devices into a device group, and call again. Nothing was mapped.", null, false);

    public static string NativeRefusal(string label, string message)
        => label + " | FAILED: VCI refused this ungrouped device (workspace folder " + UngroupedFolder + "): " + message;

    public static string NothingMappable(string label)
        => label + " | not supported by VCI: no object of this ungrouped device can be mapped (workspace folder " + UngroupedFolder + ")";
}
