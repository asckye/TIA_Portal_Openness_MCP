using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Vci;

// One native export can affect both the mapping and workspace files before it throws.
internal sealed class VciExportAttempts
{
    internal int Issued { get; private set; }
    internal int Unconfirmed { get; private set; }
    internal bool MappingProbeFailed { get; private set; }
    internal int MappedAfterFailure { get; private set; }
    internal int FilesWritten { get; private set; }
    private readonly HmiArray writtenAfterFailure = new();
    private int writtenFailureCount;
    private void RecordFailureFile(FileInfo file, string state)
    {
        if (++writtenFailureCount <= 40)
            writtenAfterFailure.Add(new HmiObject { ["fileName"] = file.Name, ["mappingState"] = state });
    }

    internal void AddFileEvidence(IDictionary<string, object?> meta)
    {
        if (FilesWritten > 0) meta["filesWritten"] = FilesWritten;
        if (writtenFailureCount > 0)
        {
            meta["writtenFilesAfterFailure"] = writtenAfterFailure;
            meta["writtenFilesAfterFailureTotal"] = writtenFailureCount;
        }
    }
    internal int UnsupportedContainers { get; private set; }
    internal int QueryRefusals { get; private set; }
    internal string? NativeMessage { get; private set; }

    internal void QueryRefused(Exception error)
    {
        QueryRefusals++;
        NativeMessage ??= Diagnostic(error);
    }

    // No direct format on a container still permits a walk of its children. A null result stops this branch.
    internal IList<string>? ReadFormats(Func<IList<string>?> read, bool descendable, bool ungroupedDevice, Action<Exception> onRefusal)
    {
        try { return read() ?? Array.Empty<string>(); }
        catch (Exception error)
        {
            bool walkChildren = descendable && !ungroupedDevice;
            if (walkChildren) UnsupportedContainers++;
            else QueryRefused(error);
            onRefusal(error);
            return walkChildren ? Array.Empty<string>() : null;
        }
    }

    internal bool ExportFileFirst(FileInfo file, Action seed, Action connect, Func<bool> freshMapping, Action<string> report,
        Func<bool>? matchesExisting = null)
    {
        bool issuedSeed = false;
        try
        {
            if (VciExportPolicy.TargetExists(file))
            {
                if (matchesExisting?.Invoke() != true) throw new IOException(VciExportPolicy.ExistingFile(file.Name));
            }
            else
            {
                issuedSeed = true;
                Issued++;
                seed();
                FilesWritten++;
            }
        }
        catch (Exception error)
        {
            if (!NativeRefusalProbe.AllowsRefusalProof(error))
            {
                if (issuedSeed) Unconfirmed++;
                MappingProbeFailed = true;
            }
            NativeMessage = NativeFailureText.BoundedDiagnostic(error);
            report("FAILED: " + NativeMessage);
            if (issuedSeed && File.Exists(file.FullName))
            {
                FilesWritten++;
                report("file present after refused export: " + file.Name);
                RecordFailureFile(file, "absent");
            }
            report("no mapping created");
            return false;
        }
        string mappingState = "unknown";
        bool Lookup()
        {
            bool present = freshMapping();
            mappingState = present ? "present" : "absent";
            return present;
        }
        bool mapped = Export(connect, Lookup, report,
            () => VciSyncTarget.ConnectSnapshot(file.DirectoryName!, Path.GetFileNameWithoutExtension(file.Name)), stopOnUncertainWrite: true);
        if (!mapped)
        {
            if (issuedSeed) RecordFailureFile(file, mappingState);
            if (mappingState == "absent") report("mapping absent after failed export");
            if (issuedSeed) report("file written: " + file.Name);
        }
        return mapped;
    }

    internal bool ExportToWorkspace(string root, string stem, Action write, Func<bool> freshMapping, Action<string> report)
        => Export(write, freshMapping, report, () => VciSyncTarget.ConnectSnapshot(root, stem));

    internal bool Export(Action write, Func<bool> freshMapping, Action<string> report,
        Func<(string Name, long Length, DateTime Modified)[]?>? snapshot = null, bool stopOnUncertainWrite = false)
    {
        var before = snapshot?.Invoke();
        Issued++;
        Unconfirmed++;
        try { write(); }
        catch (Exception error)
        {
            NativeMessage = Diagnostic(error);
            report("FAILED: " + NativeMessage);
            if (stopOnUncertainWrite && !NativeRefusalProbe.AllowsRefusalProof(error))
            {
                MappingProbeFailed = true;
                return false;
            }
            bool mapped;
            try { mapped = freshMapping(); }
            catch (Exception probeError)
            {
                MappingProbeFailed = true;
                report("mapping lookup failed after export: " + Diagnostic(probeError));
                return false;
            }
            if (mapped)
            {
                MappedAfterFailure++; Unconfirmed--;
                report("mapping present after failed export; inspect workspace files");
            }
            else if (before != null && snapshot!() is { } after && before.SequenceEqual(after)) Unconfirmed--;
            return false;
        }
        Unconfirmed--;
        return true;
    }

    private static string Diagnostic(Exception error) =>
        NativeFailureText.Compose(error.GetBaseException().Message, null, Array.Empty<string>());
}
