using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcp.Adapters.Native.Vci;

// Only value data survives an export. Synchronize can dispose both the mapping and its workspace when it throws.
internal sealed class VciSyncTarget
{
    private const int MaxSnapshotEntries = 10000;
    internal string Stem { get; }
    private readonly string? directory;
    private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private VciSyncTarget(string stem, string? directory) { Stem = stem; this.directory = directory; }

    private static string? AbsoluteDirectory(string path)
    {
        if (!Path.IsPathRooted(path)) return null;
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(full), PathComparison)) return null;
        // Keep a trailing separator so roots, nested folders and sibling names have unambiguous boundaries.
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    internal static VciSyncTarget Capture(string stem, Func<string> readDirectory)
    {
        string? directory = null;
        try { directory = AbsoluteDirectory(readDirectory()); }
        catch (Exception) { /* swallow(probe-optional): A failed mapping path probe leaves the export unconfirmed. */ }
        return new VciSyncTarget(stem, directory);
    }

    internal T Find<T>(IEnumerable<T> freshMappings, Func<T, string> readStem, Func<T, string> readDirectory)
    {
        if (directory == null || string.IsNullOrEmpty(Stem)) throw new InvalidOperationException("The mapping path is unavailable.");
        return freshMappings.Single(mapping => string.Equals(Stem, readStem(mapping), PathComparison)
            && string.Equals(directory, AbsoluteDirectory(readDirectory(mapping)), PathComparison));
    }

    internal (string Name, long Length, DateTime Modified)[]? Snapshot(string workspaceRoot, bool checkWorkspaceWhenEmpty = false)
    {
        try
        {
            string? root = AbsoluteDirectory(workspaceRoot);
            if (root == null || directory == null || !directory.StartsWith(root, PathComparison)
                || string.IsNullOrEmpty(Stem) || Stem == "?" || Stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var folder = new DirectoryInfo(directory);
            for (var parent = folder; parent != null; parent = parent.Parent)
            {
                if (string.Equals(AbsoluteDirectory(parent.FullName), root, PathComparison)) break;
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            }
            // FileNameWithoutExtension can contain dots and the export can have multiple companion files.
            var files = folder.EnumerateFiles(Stem + "*").Where(file => file.Name.StartsWith(Stem, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => (file.Name, file.Length, file.LastWriteTimeUtc)).ToArray();
            return checkWorkspaceWhenEmpty && files.Length == 0 ? WorkspaceSnapshot(root) : files;
        }
        catch (Exception) /* swallow(probe-optional): An unreadable file snapshot leaves the export unconfirmed. */
        { return null; }
    }

    internal static (string Name, long Length, DateTime Modified)[]? ConnectSnapshot(string workspaceRoot, string stem)
    {
        try
        {
            string? root = AbsoluteDirectory(workspaceRoot);
            if (root == null || string.IsNullOrEmpty(stem) || stem == "?" || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var folder = new DirectoryInfo(root);
            var ancestors = new Stack<DirectoryInfo>();
            for (var parent = folder; parent != null; parent = parent.Parent) ancestors.Push(parent);
            if (ancestors.Any(parent => (parent.Attributes & FileAttributes.ReparsePoint) != 0)) return null;

            return WorkspaceSnapshot(root, stem);
        }
        catch (Exception) /* swallow(probe-optional): An incomplete directory observation cannot settle a refused export. */
        { return null; }
    }

    // A write deferred past the post-throw snapshot or a same-length rewrite within timestamp resolution can escape this comparison.
    private static (string Name, long Length, DateTime Modified)[]? WorkspaceSnapshot(string root, string? connectStem = null)
    {
        var entries = new List<(string Name, long Length, DateTime Modified)>();
        var pending = new Stack<DirectoryInfo>();
        var workspace = new DirectoryInfo(root);
        pending.Push(workspace);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            folder.Refresh();
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entries.Count >= MaxSnapshotEntries) return null;
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                if (entry is DirectoryInfo directory)
                {
                    if (folder == workspace && connectStem != null && entry.Name.StartsWith(connectStem, StringComparison.OrdinalIgnoreCase)) return null;
                    pending.Push(directory);
                }
                entries.Add((entry.FullName, entry is FileInfo file ? file.Length : -1, entry.LastWriteTimeUtc));
            }
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }
}
