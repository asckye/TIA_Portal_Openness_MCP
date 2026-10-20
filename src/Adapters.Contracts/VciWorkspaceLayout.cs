using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

// Workspace layout of ConnectProjectToWorkspace for the UngroupedDevicesGroup (decided 2026-10-19). The ungrouped devices
// are walked under one fixed, language-independent workspace folder, because the system group's own Name is localized.
// When a root-level device or device group already owns that folder, the call is refused before anything is mapped; the
// two are never merged. Workspace folders compare case-insensitively, as Windows folders do.
public static class VciWorkspaceLayout
{
    public const string UngroupedFolder = "UngroupedDevices";

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
