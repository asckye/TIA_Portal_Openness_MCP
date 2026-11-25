using System;
using System.Linq;
using System.IO;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Vci;

internal sealed class VciSyncSelection
{
    internal int SkippedEqual { get; private set; }
    internal int UnverifiedEqual { get; private set; }
    private int skippedUnknown;
    private readonly HmiArray probeRows = new();
    private readonly System.Collections.Generic.List<string> disconnected = new();
    internal bool Complete => skippedUnknown == 0 && disconnected.Count == 0 && UnverifiedEqual == 0;
    private int probeCount;
    private int verifiedEqual, notVerified, wouldVerify;
    internal void SelectStored(bool preview)
    {
        SkippedEqual--;
        UnverifiedEqual++;
        if (preview) wouldVerify++;
    }
    internal void FinishStored(string outcome)
    {
        if (outcome == "not-verified") { notVerified++; return; }
        UnverifiedEqual--;
        if (outcome == "verified-equal") { verifiedEqual++; SkippedEqual++; }
    }

    internal bool Include(Func<string> readStatus, bool changedOnly, out bool probeFailed)
        => Include(readStatus, changedOnly, out probeFailed, null, null, out _);

    internal bool Include(Func<string> readStatus, bool changedOnly, out bool probeFailed, Func<string>? name,
        Func<Exception, bool>? isDisconnected, out bool skippedDisconnected, Func<bool>? fileMissing = null)
    {
        skippedDisconnected = false;
        string status;
        probeFailed = false;
        try { status = readStatus(); }
        catch (Exception error)
        {
            probeFailed = true;
            if (!NativeRefusalProbe.AllowsRefusalProof(error)) fileMissing = null;
            status = "Unknown";
            probeCount++;
            if (probeRows.Count < 40)
                probeRows.Add(new HmiObject { ["name"] = name?.Invoke(), ["nativeMessage"] = NativeFailureText.BoundedDiagnostic(error) });
            if (isDisconnected?.Invoke(error) == true)
            {
                skippedDisconnected = true;
                disconnected.Add(name?.Invoke() ?? "?");
                return false;
            }
        }
        if (string.Equals(status, "Equal", StringComparison.OrdinalIgnoreCase)) SkippedEqual++;
        else if (changedOnly && string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            // A missing legacy XML is an observable difference even when the native comparison is Unknown.
            if (fileMissing?.Invoke() == true) return true;
            skippedUnknown++;
        }
        else return true;
        return false;
    }

    internal void AddProbeEvidence(HmiObject meta)
    {
        meta["verifiedEqual"] = verifiedEqual;
        meta["notVerified"] = notVerified;
        meta["wouldVerify"] = wouldVerify;
        meta["unverifiedEqual"] = UnverifiedEqual;
        if (disconnected.Count > 0) meta["skippedDisconnected"] = disconnected.Count;
        if (disconnected.Count > 0 || UnverifiedEqual > 0) meta["dataComplete"] = false;
        if (probeRows.Count == 0) return;
        // Preserve the first diagnostic for earlier clients. Each failed probe also identifies its own mapping.
        meta["statusProbe"] = new HmiObject { ["nativeMessage"] = ((HmiObject)probeRows[0]!)["nativeMessage"],
            ["objects"] = probeRows, ["total"] = probeCount };
    }

    internal void CountStoredEqual() => UnverifiedEqual++;

    internal string Summary(string message, bool noTargets = false)
    {
        message += VersionControlResult.StoredEqualSummary(UnverifiedEqual);
        message += VersionControlResult.DisconnectedSummary(disconnected);
        if (skippedUnknown > 0)
            return message + " " + skippedUnknown + " object(s) have Unknown status. Pass changedOnly=false to attempt them.";
        if (!noTargets || UnverifiedEqual > 0 || disconnected.Count > 0) return message;
        return message + (SkippedEqual > 0 ? " All " + SkippedEqual + " mapped object(s) reported Equal and are in sync." : " No objects are mapped.");
    }

    internal HmiObject Evidence(bool dryRun, int synchronized = 0, int failed = 0)
    {
        var meta = HmiMeta.Basic(failed == 0, ("dryRun", dryRun), ("synchronized", synchronized), ("failed", failed),
            ("skippedEqual", SkippedEqual), ("unverifiedEqual", UnverifiedEqual), ("skippedUnknown", skippedUnknown), ("dataComplete", skippedUnknown == 0));
        AddProbeEvidence(meta);
        return meta;
    }
}

internal sealed class VciSyncAttempts
{
    private readonly bool legacyXml;
    private int issued;
    private int unconfirmed;
    internal bool InterruptedBatch { get; private set; }
    private int writtenFailureCount;
    internal void RecordProof(bool attempted, string outcome, bool uncertain, string? reason)
    {
        if (attempted) issued++;
        if (outcome == "synchronized") Synchronized++;
        if (uncertain) unconfirmed++;
        if (outcome == "not-verified") nativeMessage ??= reason;
    }
    private string? nativeMessage;
    private readonly HmiArray writtenAfterFailure = new();
    internal int Synchronized { get; private set; }
    internal int FilesWritten { get; private set; }
    internal string? WrittenFile { get; private set; }
    internal void RecordFile(FileInfo file) { FilesWritten++; WrittenFile = file.FullName; }
    private void ReportWrittenFile(string mappingState)
    {
        if (WrittenFile != null && ++writtenFailureCount <= 40)
            writtenAfterFailure.Add(new HmiObject { ["fileName"] = Path.GetFileName(WrittenFile), ["mappingState"] = mappingState });
    }

    internal VciSyncAttempts(bool legacyXml = false) => this.legacyXml = legacyXml;

    private static string WriteDiagnostic(Exception error) =>
        NativeFailureText.BoundedDiagnostic(error.GetBaseException());

    internal string? Synchronize<T>(string workspaceRoot, VciSyncTarget target, Action write, Func<T> reacquireMapping, Func<T, string> readStatus,
        bool selectionProbeFailed, out string? statusAfter, string mode = "ProjectToWorkspace", Func<Action, bool>? repairFile = null)
    {
        bool legacyExport = legacyXml && mode == "ProjectToWorkspace";
        statusAfter = null;
        WrittenFile = null;
        if (repairFile != null)
        {
            int beforeRepair = issued;
            try
            {
                if (repairFile(() => issued++)) { Synchronized++; return null; }
            }
            catch (Exception error)
            {
                if (issued > beforeRepair && !NativeRefusalProbe.AllowsRefusalProof(error)) { InterruptedBatch = true; unconfirmed++; }
                string reason = NativeFailureText.BoundedDiagnostic(error);
                nativeMessage ??= reason;
                ReportWrittenFile("present");
                return reason;
            }
        }
        var before = target.Snapshot(workspaceRoot, legacyExport);
        issued++;
        unconfirmed++;
        try { write(); Synchronized++; unconfirmed--; return null; }
        catch (Exception error)
        {
            string failure = WriteDiagnostic(error);
            nativeMessage ??= failure;
            if (!NativeRefusalProbe.AllowsRefusalProof(error))
            {
                InterruptedBatch = WrittenFile != null;
                ReportWrittenFile("unknown");
                return failure;
            }
            var after = target.Snapshot(workspaceRoot, legacyExport);
            bool mappingFound = false;
            bool bothStatusReadsFailed = false;
            try
            {
                var fresh = reacquireMapping();
                mappingFound = true;
                statusAfter = readStatus(fresh);
            }
            catch (Exception) /* swallow(probe-optional): Only two failed status reads on a known legacy mapping permit file-only verification. */
            { bothStatusReadsFailed = mappingFound && selectionProbeFailed; }
            ReportWrittenFile(mappingFound ? "present" : "unknown");
            bool statusConfirmsFailure = !selectionProbeFailed && !string.IsNullOrEmpty(statusAfter)
                && !string.Equals(statusAfter, "Equal", StringComparison.OrdinalIgnoreCase);
            if ((statusConfirmsFailure || legacyExport && bothStatusReadsFailed)
                && before != null && after != null && before.SequenceEqual(after)) unconfirmed--;
            return failure;
        }
    }

    private bool resolvingNext;

    internal T FindNext<T>(Func<T> lookup)
    {
        resolvingNext = true;
        var mapping = lookup();
        resolvingNext = false;
        return mapping;
    }

    internal void AddEvidence(HmiObject meta, Exception? interrupted = null)
    {
        if (unconfirmed > 0 || InterruptedBatch || interrupted != null) meta["dataComplete"] = false;
        meta["exportsIssued"] = issued;
        meta["synchronized"] = Synchronized;
        if (FilesWritten > 0) meta["filesWritten"] = FilesWritten;
        if (writtenFailureCount > 0)
        {
            meta["writtenFilesAfterFailure"] = writtenAfterFailure;
            meta["writtenFilesAfterFailureTotal"] = writtenFailureCount;
        }
        meta["mayHaveChanged"] = Synchronized > 0 || FilesWritten > 0 || unconfirmed > 0;
        meta["unconfirmed"] = unconfirmed;
        meta["writeOutcomeUnknown"] = unconfirmed > 0 || interrupted != null && issued > 0
            && (!NativeRefusalProbe.AllowsRefusalProof(interrupted) || Synchronized > 0 && !resolvingNext);
        meta["nativeMessage"] = nativeMessage ?? (interrupted == null ? null : WriteDiagnostic(interrupted));
    }

    internal HmiObject Interrupted(Exception error)
    {
        var meta = VersionControlAdapter.FailureEvidence(error, issued > 0);
        if (resolvingNext) meta["failed"] = issued - Synchronized + 1;
        AddEvidence(meta, error);
        return meta;
    }
}
