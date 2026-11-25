using System;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Vci;

internal sealed class VciSyncSelection
{
    internal int SkippedEqual { get; private set; }
    internal int UnverifiedEqual { get; private set; }
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
            statusProbeMessage ??= NativeFailureText.BoundedDiagnostic(error);
        }
        if (string.Equals(status, "Equal", StringComparison.OrdinalIgnoreCase)) SkippedEqual++;
        else if (changedOnly && string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase)) skippedUnknown++;
        else return true;
        return false;
    }

    internal void CountStoredEqual() => UnverifiedEqual++;

    internal string Summary(string message, bool noTargets = false)
    {
        message += VersionControlResult.StoredEqualSummary(UnverifiedEqual);
        if (skippedUnknown > 0)
            return message + " " + skippedUnknown + " object(s) have Unknown status. Pass changedOnly=false to attempt them.";
        if (!noTargets || UnverifiedEqual > 0) return message;
        return message + (SkippedEqual > 0 ? " All " + SkippedEqual + " mapped object(s) reported Equal and are in sync." : " No objects are mapped.");
    }

    internal HmiObject Evidence(bool dryRun, int synchronized = 0, int failed = 0)
    {
        var meta = HmiMeta.Basic(failed == 0, ("dryRun", dryRun), ("synchronized", synchronized), ("failed", failed),
            ("skippedEqual", SkippedEqual), ("unverifiedEqual", UnverifiedEqual), ("skippedUnknown", skippedUnknown), ("dataComplete", skippedUnknown == 0));
        if (statusProbeMessage != null) meta["statusProbe"] = new HmiObject { ["nativeMessage"] = statusProbeMessage };
        return meta;
    }
}

internal sealed class VciSyncAttempts
{
    private readonly bool legacyXml;
    private int issued;
    private int unconfirmed;
    private string? nativeMessage;
    internal int Synchronized { get; private set; }

    internal VciSyncAttempts(bool legacyXml = false) => this.legacyXml = legacyXml;

    private static string WriteDiagnostic(Exception error) =>
        NativeFailureText.Compose(error.GetBaseException().Message, null, Array.Empty<string>());

    internal string? Synchronize<T>(string workspaceRoot, VciSyncTarget target, Action write, Func<T> reacquireMapping, Func<T, string> readStatus,
        bool selectionProbeFailed, out string? statusAfter, string mode = "ProjectToWorkspace")
    {
        bool legacyExport = legacyXml && mode == "ProjectToWorkspace";
        var before = target.Snapshot(workspaceRoot, legacyExport);
        statusAfter = null;
        issued++;
        unconfirmed++;
        try { write(); Synchronized++; unconfirmed--; return null; }
        catch (Exception error)
        {
            string failure = WriteDiagnostic(error);
            nativeMessage ??= failure;
            if (!NativeRefusalProbe.AllowsRefusalProof(error)) return failure;
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
        meta["exportsIssued"] = issued;
        meta["synchronized"] = Synchronized;
        meta["mayHaveChanged"] = Synchronized > 0 || unconfirmed > 0;
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
