#nullable disable
#if STUDIO_VCI && !STUDIO_VCI_MODERN
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Contracts;
using NativeVci = TiaOpenness.Openness.VersionControlPrimitives;
using MappedObject = Siemens.Engineering.VersionControl.WorkspaceMapping;

namespace TiaMcp.Adapters.Native.Vci;

internal sealed class VciStoredEqual
{
    internal readonly VciContentProof Proof;
    internal readonly VciSyncTarget Path;
    internal readonly string RefreshMessage;
    internal string Outcome { get; private set; } = "verify-before-export";
    internal string Message { get; private set; }
    internal bool? FileChanged { get; private set; }
    internal bool Inconsistent { get; private set; }
    internal bool Issued { get; private set; }
    internal bool Unconfirmed { get; private set; }
    internal bool Interrupted { get; private set; }
    internal bool ComparisonInterrupted { get; private set; }
    private readonly string root;
    private MappedObject source;
    internal string InitialHash { get; private set; }
    internal bool NotAttempted { get; private set; }
    private bool comparisonFailed;
    private bool invalidated;
    internal string Verification => invalidated ? "not-comparable" : Proof.Verification;

    internal VciStoredEqual(string root, MappedObject mapping, string name, string refresh)
    {
        this.root = root;
        RefreshMessage = refresh;
        Proof = new VciContentProof(root, NativeVci.TryMappingFile(root, mapping));
        Path = VciSyncTarget.Capture(name, () => Proof.File?.DirectoryName ?? "");
        source = mapping;
    }

    // Capture push comparisons before synchronization can invalidate the selected mapping handles.
    internal static bool PrepareAll(IEnumerable<VciStoredEqual> rows, bool preview)
    {
        bool stopped = false;
        foreach (var row in rows)
        {
            var mapping = row.source;
            row.source = null;
            if (stopped)
            {
                row.NotAttempted = true;
                row.Outcome = "not-verified";
                row.Message = "Comparison stopped before this mapping was checked.";
                continue;
            }
            row.Prepare(mapping, preview);
            stopped = row.Interrupted;
        }
        return stopped;
    }

    private void Prepare(MappedObject mapping, bool preview)
    {
        Inconsistent = false;
        Message = null;
        if (preview) Proof.Preview();
        else
        {
            Outcome = "not-verified";
            try
            {
                global::Siemens.Engineering.IEngineeringObject compared = null;
                Proof.Compare(() => NativeVci.Exporter(compared = mapping.LinkedProjectObject), NativeVci.Ordinary);
                if (Proof.Matches) Outcome = "verified-equal";
                else if (Proof.Verification == "not-comparable" && Proof.Message != null && compared is global::Siemens.Engineering.SW.Blocks.PlcBlock block)
                {
                    Message = NativeVci.ComparisonDiagnostic(block, Proof.Message, Proof.ExportSucceeded, out bool inconsistent);
                    Inconsistent = inconsistent;
                }
            }
            catch (Exception error) when (!NativeVci.Ordinary(error))
            {
                Message = NativeFailureText.BoundedDiagnostic(error);
                comparisonFailed = true;
                Interrupted = !LocalDisposal(error);
            }
        }
        InitialHash = Proof.Sha256;
        Message ??= Proof.Message;
    }

    private static bool LocalDisposal(Exception error) =>
        (HmiReadPolicy.DisposedObjectOnly(error) || NativeVci.DisposedStatus(error) || error is ObjectDisposedException)
        && (error.InnerException == null || !NativeRefusalProbe.UncertainConnection(error.InnerException));

    internal void CompareAfterImports(Func<MappedObject> acquire, bool stopped)
    {
        source = null;
        Inconsistent = false;
        Outcome = "not-verified";
        if (stopped)
        {
            NotAttempted = true;
            Message = "The batch stopped before the project content could be verified.";
            return;
        }
        try { Prepare(acquire(), false); }
        catch (Exception error)
        {
            Message = NativeFailureText.BoundedDiagnostic(error);
            Interrupted = !NativeVci.Ordinary(error) && !LocalDisposal(error);
        }
    }

    internal void Finish(Func<MappedObject> acquire, SynchronizationMode mode, bool stopped)
    {
        if (Interrupted || comparisonFailed || NotAttempted) return;
        string beforeHash = InitialHash;
        if (beforeHash == null) return;
        string currentHash = Proof.CurrentHash();
        if (beforeHash != currentHash)
        {
            invalidated = true;
            FileChanged = currentHash == null ? (bool?)null : true;
            Outcome = "not-verified";
            Message = "The workspace file changed or became unreadable during comparison.";
            return;
        }
        if (Proof.Matches)
        {
            FileChanged = false;
            return;
        }
        if (mode == SynchronizationMode.WorkspaceToProject)
        {
            FileChanged = false;
            Message ??= "The workspace file did not match the project export.";
            return;
        }
        if (stopped)
        {
            NotAttempted = true;
            Message ??= "The batch stopped before this mapping could be synchronized.";
            return;
        }
        try
        {
            var mapping = acquire();
            if (!string.Equals(NativeVci.TryMappingFile(root, mapping)?.FullName, Proof.File?.FullName, StringComparison.OrdinalIgnoreCase))
            {
                Message = "The original XML mapping is unavailable.";
                return;
            }
            var before = Path.Snapshot(root);
            currentHash = Proof.CurrentHash();
            if (beforeHash != currentHash)
            {
                invalidated = true;
                FileChanged = currentHash == null ? (bool?)null : true;
                Message = "The workspace file changed before synchronization.";
                return;
            }
            Issued = true;
            try { NativeVci.Synchronize(mapping, mode); }
            catch (Exception error)
            {
                string refusal = NativeFailureText.BoundedDiagnostic(error);
                Message = Proof.Verification == "not-comparable" && Message != null ? "Comparison: " + Message + " Synchronize: " + refusal : refusal;
                Unconfirmed = true;
                if (!NativeVci.Ordinary(error)) { Interrupted = true; return; }
                var after = Path.Snapshot(root);
                var hash = Proof.CurrentHash();
                FileChanged = hash == null ? (bool?)null : beforeHash != hash;
                try { NativeVci.ReadStatus(acquire(), out _); }
                catch (Exception readError) when (NativeVci.Ordinary(readError))
                { /* swallow(probe-optional): The unchanged readable file and snapshots independently settle an ordinary stored-Equal refusal. */ }
                Unconfirmed = FileChanged != false || before == null || after == null || !before.SequenceEqual(after);
                return;
            }
            string written = Proof.CurrentHash();
            FileChanged = written == null ? (bool?)null : written != beforeHash;
            if (written != null)
            {
                Message = null;
                Inconsistent = false;
                try { Proof.Compare(() => NativeVci.Exporter(acquire().LinkedProjectObject), NativeVci.Ordinary); }
                catch (Exception error)
                {
                    ComparisonInterrupted = !NativeVci.Ordinary(error) && !LocalDisposal(error);
                    if (FileChanged != true) throw;
                    invalidated = true;
                    Message = NativeFailureText.BoundedDiagnostic(error);
                    Interrupted = ComparisonInterrupted;
                }
                string finalHash = Proof.CurrentHash();
                if (Proof.Sha256 != written || finalHash != written)
                {
                    invalidated = true;
                    Unconfirmed = true;
                    FileChanged = finalHash == null ? (bool?)null : finalHash != beforeHash;
                    Message = "The workspace file changed or became unreadable during the final comparison.";
                }
                else if (FileChanged == true)
                {
                    // TIA can synchronize to a file form different from either comparison export.
                    Outcome = "synchronized";
                    Message ??= Proof.Message;
                }
                else if (Proof.Matches)
                {
                    Outcome = "verified-equal";
                    Message = null;
                }
                else Message = Proof.Message ?? "Synchronize returned without writing matching content.";
            }
            else { Interrupted = true; Unconfirmed = true; Message = "Cannot verify the workspace file after synchronization."; }
        }
        catch (Exception error)
        {
            Message = NativeFailureText.BoundedDiagnostic(error);
            Unconfirmed |= Issued && FileChanged != false;
            Interrupted = !NativeVci.Ordinary(error) && !LocalDisposal(error);
        }
    }
}
#endif
