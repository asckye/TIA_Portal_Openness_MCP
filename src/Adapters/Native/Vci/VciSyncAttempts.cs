using System;
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
        if (!noTargets) return message;
        return message + (SkippedEqual > 0 ? " All " + SkippedEqual + " mapped object(s) reported Equal and are in sync." : " No objects are mapped.");
    }

    internal HmiObject Evidence(bool dryRun, int synchronized = 0, int failed = 0)
    {
        var meta = HmiMeta.Basic(failed == 0, ("dryRun", dryRun), ("synchronized", synchronized), ("failed", failed),
            ("skippedEqual", SkippedEqual), ("skippedUnknown", skippedUnknown), ("dataComplete", skippedUnknown == 0));
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

    internal static string Diagnostic(Exception error) =>
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
            string failure = Diagnostic(error);
            nativeMessage ??= failure;
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

    internal void AddEvidence(HmiObject meta, Exception? interrupted = null)
    {
        meta["exportsIssued"] = issued;
        meta["synchronized"] = Synchronized;
        meta["mayHaveChanged"] = Synchronized > 0 || unconfirmed > 0;
        meta["unconfirmed"] = unconfirmed;
        meta["writeOutcomeUnknown"] = unconfirmed > 0 || interrupted != null && Synchronized > 0;
        meta["nativeMessage"] = nativeMessage ?? (interrupted == null ? null : Diagnostic(interrupted));
    }

    internal HmiObject Interrupted(Exception error)
    {
        var meta = VersionControlAdapter.FailureEvidence(error, issued > 0);
        AddEvidence(meta, error);
        return meta;
    }
}
