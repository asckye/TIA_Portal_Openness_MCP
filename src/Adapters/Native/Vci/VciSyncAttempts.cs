using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Vci;

internal sealed class VciSyncSelection
{
    internal int SkippedEqual { get; private set; }
    private int skippedUnknown;
    private string? statusProbeMessage;

    internal bool Include(Func<string> readStatus, bool changedOnly, out bool probeFailed)
    {
        string status;
        probeFailed = false;
        try { status = readStatus(); }
        catch (Exception error)
        {
            probeFailed = true;
            status = "Unknown";
            statusProbeMessage ??= VciSyncAttempts.Diagnostic(error);
        }
        if (string.Equals(status, "Equal", StringComparison.OrdinalIgnoreCase)) SkippedEqual++;
        else if (changedOnly && string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase)) skippedUnknown++;
        else return true;
        return false;
    }

    internal string Summary(string message, bool noTargets = false)
    {
        if (skippedUnknown > 0)
            return message + " " + skippedUnknown + " object(s) have Unknown status. Pass changedOnly=false to attempt them.";
        return noTargets ? message + " Every mapped object is already in sync." : message;
    }

    internal HmiObject Evidence(bool dryRun, int synchronized = 0, int failed = 0)
    {
        var meta = HmiMeta.Basic(failed == 0, ("dryRun", dryRun), ("synchronized", synchronized), ("failed", failed),
            ("skippedEqual", SkippedEqual), ("skippedUnknown", skippedUnknown), ("dataComplete", skippedUnknown == 0));
        if (statusProbeMessage != null) meta["statusProbe"] = new HmiObject { ["nativeMessage"] = statusProbeMessage };
        return meta;
    }
}

// Only value data survives an export. Synchronize can dispose both the mapping and its workspace when it throws.
internal sealed class VciSyncTarget
{
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

    internal (string Name, long Length, DateTime Modified)[]? Snapshot(string workspaceRoot)
    {
        try
        {
            string? root = AbsoluteDirectory(workspaceRoot);
            if (root == null || directory == null || !directory.StartsWith(root, PathComparison)
                || string.IsNullOrEmpty(Stem) || Stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var folder = new DirectoryInfo(directory);
            for (var parent = folder; parent != null; parent = parent.Parent)
            {
                if (string.Equals(AbsoluteDirectory(parent.FullName), root, PathComparison)) break;
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            }
            // FileNameWithoutExtension can contain dots and the export can have multiple companion files.
            return folder.EnumerateFiles(Stem + "*").Where(file => file.Name.StartsWith(Stem, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => (file.Name, file.Length, file.LastWriteTimeUtc)).ToArray();
        }
        catch (Exception) /* swallow(probe-optional): An unreadable file snapshot leaves the export unconfirmed. */
        { return null; }
    }
}

internal sealed class VciSyncAttempts
{
    private int issued;
    private int unconfirmed;
    private string? nativeMessage;

    internal static string Diagnostic(Exception error) =>
        NativeFailureText.Compose(error.GetBaseException().Message, null, Array.Empty<string>());

    internal string? Synchronize<T>(string workspaceRoot, VciSyncTarget target, Action write, Func<T> reacquireMapping, Func<T, string> readStatus,
        bool selectionProbeFailed, out string? statusAfter)
    {
        var before = target.Snapshot(workspaceRoot);
        statusAfter = null;
        issued++;
        try { write(); return null; }
        catch (Exception error)
        {
            string failure = Diagnostic(error);
            nativeMessage ??= failure;
            var after = target.Snapshot(workspaceRoot);
            try { statusAfter = readStatus(reacquireMapping()); }
            catch (Exception) { /* swallow(probe-optional): An unreadable status cannot confirm the failed export. */ }
            if (selectionProbeFailed || string.IsNullOrEmpty(statusAfter) || string.Equals(statusAfter, "Equal", StringComparison.OrdinalIgnoreCase)
                || before == null || after == null || !before.SequenceEqual(after)) unconfirmed++;
            return failure;
        }
    }

    internal void AddEvidence(HmiObject meta, Exception? interrupted = null)
    {
        meta["exportsIssued"] = issued;
        meta["mayHaveChanged"] = issued > 0;
        meta["unconfirmed"] = unconfirmed;
        meta["writeOutcomeUnknown"] = interrupted == null ? unconfirmed > 0 : issued > 0;
        meta["nativeMessage"] = nativeMessage ?? (interrupted == null ? null : Diagnostic(interrupted));
    }

    internal HmiObject Interrupted(Exception error)
    {
        var meta = VersionControlAdapter.FailureEvidence(error, issued > 0);
        AddEvidence(meta, error);
        return meta;
    }
}
