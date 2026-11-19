using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Libraries;

internal static class LibraryArchive
{
    internal static string Write(Dictionary<string, object?> meta, DirectoryInfo archiveDirectory, Action archive)
    {
        var before = archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Select(f => f.Name).ToArray() : Array.Empty<string>();
        meta["mayHaveWrittenFiles"] = true;
        archive();
        meta["writeReturned"] = true;
        if (!ReadbackStep.TryRead(meta, "archiveDirectory", () => {
            archiveDirectory.Refresh();
            return archiveDirectory.Exists ? archiveDirectory.GetFileSystemInfos().Where(f => !before.Contains(f.Name))
                .Select(f => (object)new Dictionary<string, object?> { ["name"] = f.Name, ["bytes"] = f is FileInfo fi ? fi.Length : (long?)null })
                .ToArray() : Array.Empty<object>();
        }, out var created)) return ReadbackStep.Message;
        meta["createdEntries"] = new List<object?>(created); meta["apiCallSuccess"] = true;
        if (created.Length == 0) {
            meta["operationSuccess"] = false;
            return "Archive returned but no new entry appeared in the destination directory; inspect the directory before retrying.";
        }
        return "User global library archived; new directory entries listed. The library stays open at its original location.";
    }
}
