#if STUDIO_VCI
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using System;
using TiaMcpServer.Siemens;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using NativeVci = TiaOpenness.Openness.VersionControlPrimitives;
#if !STUDIO_VCI_MODERN
using MappedObject = Siemens.Engineering.VersionControl.WorkspaceMapping;
#endif
#if STUDIO_VCI_MODERN
using PortVci = TiaOpenness.Openness.VersionControlPrimitives;
#else
using PortVci = TiaMcp.Adapters.Native.Vci.LegacyVciPortPrimitives;
#endif

namespace TiaMcp.Adapters.Native.Vci
{
    // TIA Portal V21's Version Control Interface (VCI), reached from Openness rather than the UI.
    //
    // A TIA project is a binary blob that Git cannot diff. VCI fixes that: a *workspace* is a
    // plain folder holding one text file per mapped object (.s7dcl / .xml), which IS diffable and
    // commitable. Native Openness supports synchronization in both directions; this adapter exposes
    // ProjectToWorkspace only and refuses WorkspaceToProject, including previews.
    //
    // ExportObject writes new workspace files and creates their mappings (V20+).
    // V16/V17 export or verify the XML before creating the mapping. V18/V19 create the mapping then synchronize.
    internal sealed partial class VersionControlAdapter
    {
        private readonly Func<IEngineeringServiceProvider?> project;

        private IEngineeringServiceProvider? VciProject => project();

        public VersionControlAdapter(Func<IEngineeringServiceProvider?> project) => this.project = project;

        // The VCI service must be kept alive for the whole session. Openness disposes the objects
        // reached through a service once that service instance is collected, so re-acquiring it on
        // every call makes workspaces obtained in an earlier call throw
        // "Access to a disposed object of type Workspace" — observed, not theoretical.
        private object? _vciOwnerProject;
        private VersionControlInterface? _vciCached;

        private VersionControlInterface RequireVci()
        {
            var project = VciProject;
            if (project == null)
            {
                _vciOwnerProject = null;
                _vciCached = null;
                _vciKeepAlive.Clear();
                throw new PortalException(PortalErrorCode.InvalidState,
                    "No project is open. Call ConnectPortal, then AttachOpenProject / OpenProject first.");
            }
            if (_vciCached != null && ReferenceEquals(_vciOwnerProject, project))
                return _vciCached;

            var vci = PortVci.Service(project as IEngineeringServiceProvider);
            if (vci == null)
                throw new InvalidOperationException(
                    "This project exposes no VersionControlInterface. VCI requires TIA Portal V16 or later and a project supporting this service.");
            _vciKeepAlive.Clear();
            _vciOwnerProject = project;
            _vciCached = vci;
            Keep(vci);
            return vci;
        }

        // Openness hands out COM-backed proxies whose lifetime is tied to the parent they came from.
        // Let an intermediate (WorkspaceGroup, or the service itself) get collected and every object
        // reached through it dies with it — "Access to a disposed object of type Workspace". So every
        // intermediate stays rooted here for as long as the project is open. Observed, not theoretical.
        private readonly List<object> _vciKeepAlive = new List<object>();

        private T Keep<T>(T o) where T : class
        {
            if (o != null) _vciKeepAlive.Add(o);
            return o!;
        }

        /// <summary>All workspaces, walking the system group and any nested user groups. Nothing here is lazy:
        /// a yield-return iterator would let the groups be collected between MoveNext calls.</summary>
        private List<Workspace> AllWorkspaces(VersionControlInterface vci)
        {
            var found = new List<Workspace>();
            var pending = new Stack<WorkspaceGroup>();
            // The root is the typed WorkspaceSystemGroup, its descendants are WorkspaceUserGroups (Name / Groups / Workspaces).
            WorkspaceSystemGroup root = Keep(PortVci.Group(vci));
            pending.Push(root);
            while (pending.Count > 0)
            {
                var g = pending.Pop();
                foreach (var w in PortVci.Enumerate(Keep(PortVci.Workspaces(g)))) found.Add(Keep(w));
                foreach (WorkspaceUserGroup sub in PortVci.Enumerate(Keep(PortVci.Groups(g)))) pending.Push(Keep(sub));
            }
            return found;
        }

        private Workspace FindWorkspace(VersionControlInterface vci, string name)
            => Select(AllWorkspaces(vci), name, PortVci.Name);

        // A throwing Openness call can dispose the workspace and its children.
        private Workspace ReAcquireWorkspace(string name)
        {
            _vciCached = null;
            _vciOwnerProject = null;
            _vciKeepAlive.Clear();
            return FindWorkspace(RequireVci(), name);
        }

        private IEnumerable<MappedObject> WorkspaceMappings(Workspace workspace)
        {
            foreach (var mapping in PortVci.Enumerate(Keep(PortVci.MappedObjects(workspace)))) yield return mapping;
        }

        public VersionControlResult GetVersionControlWorkspaces()
        {
            try
            {
                var vci = RequireVci();
                var lines = new List<string>();
                var entries = new HmiArray();
                int n = 0;
                foreach (var w in AllWorkspaces(vci))
                {
                    n++;
                    int mapped = 0;
                    try { mapped = PortVci.Count(PortVci.MappedObjects(w)); } catch { /* swallow(probe-optional): An unavailable mapped-object count leaves the existing zero fallback while other workspace fields remain readable. */ }
                    string root = "";
                    try { root = PortVci.Root(w)?.FullName ?? ""; } catch { /* swallow(probe-optional): An unavailable workspace root leaves the existing empty-path fallback in the workspace listing. */ }
                    lines.Add(string.Format(
                        "{0} | folder={1} | mappedObjects={2} | language={3}",
                        PortVci.Name(w), root, mapped, SafeLanguage(w)));
                    entries
                        .Add(new HmiObject { ["name"] = PortVci.Name(w), ["rootPath"] = root, ["mappedObjectCount"] = mapped, ["language"] = SafeLanguage(w) });
                }
                return new VersionControlResult
                {
                    Message = n == 0
                        ? "No version control workspace exists in this project yet. Create one with " +
                          "CreateVersionControlWorkspace, then use ConnectProjectToWorkspace to map project objects."
                        : n + " version control workspace(s).",
                    Items = lines,
                    Meta = HmiMeta.Basic(true, ("workspaces", entries)),
                };
            }
            catch (Exception ex)
            {
                return new VersionControlResult
                {
                    Message = "ListVersionControlWorkspaces failed: " + ex.Message,
                    Meta = FailureEvidence(ex),
                };
            }
        }

        private static string SafeLanguage(Workspace w)
        {
            try { return PortVci.Language(w)?.ToString() ?? "-"; }
            catch { /* swallow(probe-optional): An unavailable workspace language is represented by the existing dash placeholder. */ return "-"; }
        }

        public VersionControlResult GetVersionControlStatus(
            string workspaceName = "",
            bool changedOnly = true)
        {
            try
            {
                var vci = RequireVci();
                var ws = FindWorkspace(vci, workspaceName);

                var lines = new List<string>();
                var entries = new HmiArray();
                int total = 0, differing = 0, unknown = 0, unverifiedEqual = 0;
                foreach (var mo in PortVci.Enumerate(Keep(PortVci.MappedObjects(ws))))
                {
                    total++;
                    CompareState state;
                    string? error = null;
                    string? refreshMessage = null;
                    // GetStatus() returns an IndividualObjectCompareResult; ToString() on it yields the type
                    // name, not the verdict. The verdict is CompareState (Equal / Unequal / WorkspaceFileMissing).
                    try { state = PortVci.State(PortVci.ReadStatus(mo, out refreshMessage)); }
                    catch (Exception ex)
                    {
                        state = CompareState.Unknown;
                        error = NativeFailureText.BoundedDiagnostic(ex);
#if !STUDIO_VCI_MODERN
                        if (NativeVci.DisposedStatus(ex)) error = NativeVci.DisconnectedMapping + ": " + error;
#endif
                    }
                    if (state == CompareState.Unknown) unknown++;
                    else if (state != CompareState.Equal) differing++;
                    else if (refreshMessage != null) unverifiedEqual++;
                    if (changedOnly && state == CompareState.Equal && refreshMessage == null) continue;
                    var status = error == null ? state.ToString() : "Unknown(" + error + ")";
                    var line = $"{SafeName(mo)} | {status} | file={SafeFile(mo)}{SafeFormat(mo)}";
                    var row = new HmiObject { ["name"] = SafeName(mo), ["compareState"] = state.ToString(),
                        ["error"] = error, ["filePath"] = SafeFile(mo), ["fileFormat"] = SafeFormat(mo).Replace(" | format=", "") };
                    if (state == CompareState.Equal && refreshMessage != null) row["verified"] = false;
                    lines.Add(line + RefreshNote(row, refreshMessage));
                    entries.Add(row);
                }

                return new VersionControlResult
                {
                    Message = string.Format(
                        "Workspace '{0}': {1} mapped object(s), {2} differ from the workspace files.{3}",
                        PortVci.Name(ws), total, differing,
                        unknown > 0 ? " " + unknown + " comparison(s) are unknown. Inspect the individual status before export."
                        : total == 0 ? " Use ConnectProjectToWorkspace to map objects."
                        : differing == 0 && unverifiedEqual > 0 ? ""
                        : differing == 0
                            ? " Project and workspace are in sync — nothing to commit."
                            : " Call SynchronizeVersionControlWorkspace(direction='ProjectToWorkspace') to write the changes out, then commit.")
                        + VersionControlResult.StoredEqualSummary(unverifiedEqual),
                    Items = lines,
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new HmiObject {
                        ["timestamp"] = TiaMcpServer.ModelContextProtocol.ResponseClock.Now, ["success"] = true, ["workspaceName"] = PortVci.Name(ws),
                            ["rootPath"] = SafeRoot(ws), ["total"] = total, ["differing"] = differing, ["unknown"] = unknown,
                            ["dataComplete"] = unknown == 0 && unverifiedEqual == 0, ["unverifiedEqual"] = unverifiedEqual, ["objects"] = entries
                    },
                };
            }
            catch (Exception ex)
            {
                return new VersionControlResult
                {
                    Message = "GetVersionControlStatus failed: " + ex.Message,
                    Meta = FailureEvidence(ex),
                };
            }
        }

        private static string RefreshNote(HmiObject row, string? refreshMessage)
        {
            if (refreshMessage == null) return "";
            row["refreshMessage"] = refreshMessage;
            return VersionControlResult.RefreshSeparator + refreshMessage;
        }

        private static string SafeName(MappedObject mo)
        {
            try { return PortVci.FileName(mo) ?? "?"; } catch { /* swallow(probe-optional): An unavailable mapped-object name is represented by the existing question-mark placeholder. */ return "?"; }
        }

        private static string SafeFile(MappedObject mo)
        {
            try
            {
                string d = "";
                try { d = PortVci.Directory(mo)?.FullName ?? ""; } catch { /* swallow(probe-optional): An unavailable directory leaves the filename-only fallback intact. */ }
                string f = PortVci.FileName(mo) ?? "";
                return string.IsNullOrEmpty(d) ? f : d.TrimEnd('\\', '/') + "\\" + f;
            }
            catch { /* swallow(probe-optional): An unreadable mapped-object path is represented by the existing question-mark placeholder. */ return "?"; }
        }

        private static string SafeFormat(MappedObject mo)
        {
            try { return " | format=" + PortVci.Format(mo); } catch { /* swallow(probe-optional): An unavailable mapped-object format omits the optional format suffix. */ return ""; }
        }

        public VersionControlResult SyncVersionControlWorkspace(
            string direction = "ProjectToWorkspace",
            string workspaceName = "",
            bool dryRun = true,
            bool changedOnly = true)
        {
            var attempts = new VciSyncAttempts(LegacyXmlExport);
            var selection = new VciSyncSelection();
            var lines = new List<string>();
            var rows = new HmiArray();
            var failures = new HmiArray();
#if STUDIO_VCI_INITIAL
            var plannedFiles = new HmiArray();
#endif
            var unprovenNames = new List<string>();
            bool reportRows = false;
            var notAttempted = new List<string>();
            int failed = 0;
            try
            {
                SynchronizationMode mode;
                string d = (direction ?? "").Trim();
                if (d.Equals("ProjectToWorkspace", StringComparison.OrdinalIgnoreCase)) mode = SynchronizationMode.ProjectToWorkspace;
                else if (d.Equals("WorkspaceToProject", StringComparison.OrdinalIgnoreCase)) mode = SynchronizationMode.WorkspaceToProject;
                else
                    return new VersionControlResult
                    {
                        Message = "direction must be 'ProjectToWorkspace' (export for commit) or " +
                                  "'WorkspaceToProject' (import to restore); got '" + direction + "'.",
                        Meta = HmiMeta.Basic(false, ("v4Rejection", "INVALID_ARGUMENT")),
                    };

                // Exporting (project -> text files) is allowed; importing (text files -> project)
                // overwrites blocks in the engineer's project and is rejected here.
                if (mode == SynchronizationMode.WorkspaceToProject)
                    return new VersionControlResult
                    {
                        Message = "direction='WorkspaceToProject' (restoring a Git version back INTO the project) " +
                                  "overwrites blocks in the open project and is a commercial-tier operation. " +
                                  "Everything else is free — CreateVersionControlWorkspace, ConnectProjectToWorkspace, " +
                                  "GetVersionControlStatus and SynchronizeVersionControlWorkspace(direction='ProjectToWorkspace') " +
                                  "— so you can put the project under version control, see exactly what changed, " +
                                  "export it as text and commit it.",
                        Meta = HmiMeta.Basic(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY")),
                    };

                var vci = RequireVci();
                var ws = FindWorkspace(vci, workspaceName);

                string ReadStatus(MappedObject mo, out string? refreshMessage) => PortVci.State(PortVci.ReadStatus(mo, out refreshMessage)).ToString();
                string ReadDirectory(MappedObject mo) => PortVci.Directory(mo)?.ToString() ?? "";
                string wsName = PortVci.Name(ws);
                string wsRoot = SafeRoot(ws);
                var targets = new List<(MappedObject Object, bool ProbeFailed, string State, string? RefreshMessage)>();
#if STUDIO_VCI_MODERN
                Func<Exception, bool>? isDisconnected = null;
#else
                var stored = new List<VciStoredEqual>();
                Func<Exception, bool> isDisconnected = NativeVci.DisposedStatus;
#endif
                foreach (var mo in WorkspaceMappings(ws))
                {
                    string state = "Unknown";
                    string? refreshMessage = null;
#if STUDIO_VCI_INITIAL
                    Func<bool> fileMissing = () => VciExportPolicy.FileExists(NativeVci.TryMappingFile(wsRoot, mo)) == false;
#else
                    Func<bool>? fileMissing = null;
#endif
                    bool include = selection.Include(() => state = ReadStatus(mo, out refreshMessage), changedOnly, out bool probeFailed,
                        () => SafeName(mo), isDisconnected, out bool disconnected, fileMissing);
                    reportRows |= refreshMessage != null || disconnected;
#if !STUDIO_VCI_MODERN
                    if (disconnected)
                    {
                        var mappingName = SafeName(mo);
                        string file = NativeVci.TryMappingFile(wsRoot, mo)?.FullName ?? mappingName;
                        string reason = NativeVci.DisconnectedMapping + ": " + file + ". " + TiaMcp.Adapters.Contracts.VersionControlResult.Reconnect;
                        lines.Add(mappingName + " | " + reason);
                        rows.Add(new HmiObject { ["name"] = mappingName, ["stateBefore"] = state, ["outcome"] = "skipped-disconnected",
                            ["reason"] = reason, ["filePath"] = file });
                        continue;
                    }
#endif
                    if (include)
                        targets.Add((mo, probeFailed, state, refreshMessage));
#if !STUDIO_VCI_MODERN
                    else if (state == "Equal" && refreshMessage != null)
                    {
                        selection.SelectStored(dryRun);
                        stored.Add(new VciStoredEqual(wsRoot, mo, SafeName(mo), refreshMessage));
                    }
#endif
                }

                HmiObject Evidence()
                {
                    var meta = selection.Evidence(dryRun, attempts.Synchronized, failed);
                    meta["workspaceName"] = wsName;
                    meta["rootPath"] = wsRoot;
#if STUDIO_VCI_INITIAL
                    if (dryRun) meta["plannedFiles"] = plannedFiles;
#endif
                    if (reportRows) meta["items"] = rows;
                    AddNotAttempted(meta);
                    attempts.AddEvidence(meta);
                    return meta;
                }

#if !STUDIO_VCI_MODERN
                void ReportProof(VciStoredEqual proof)
                {
                    string name = proof.Path.Stem;
                    var row = new HmiObject { ["name"] = name, ["filePath"] = proof.Proof.File?.FullName, ["stateBefore"] = "Equal",
                        ["workspaceFileSha256"] = proof.InitialHash, ["outcome"] = proof.Outcome, ["verification"] = proof.Verification,
                        ["fileChanged"] = proof.FileChanged, ["nativeMessage"] = proof.Message };
                    string description = dryRun ? "verify-before-export (TIA content is written if they differ)" : proof.Outcome;
                    if (proof.Outcome == "not-verified" && proof.Message != null) description += " | FAILED: " + proof.Message;
                    if (proof.NotAttempted) { description += " | not-attempted"; notAttempted.Add(name); row["notAttempted"] = true; }
                    lines.Add(name + " | " + description + " | file=" + row["filePath"] + RefreshNote(row, proof.RefreshMessage));
                    rows.Add(row);
                    if (dryRun) return;
                    selection.FinishStored(proof.Outcome);
                    attempts.RecordProof(proof.Issued, proof.Outcome, proof.Unconfirmed, proof.Message);
                    if (proof.Outcome == "not-verified")
                    {
                        failed++;
                        unprovenNames.Add(name);
                        failures.Add(row);
                    }
                }
                if (dryRun)
                {
                    VciStoredEqual.PrepareAll(stored, true);
                    foreach (var proof in stored) ReportProof(proof);
                }
#endif
                if (targets.Count == 0 && selection.UnverifiedEqual == 0)
                    return new VersionControlResult
                    {
                        Message = selection.Summary("Workspace '" + wsName + "': nothing selected to synchronize.", noTargets: true) + " Folder: " + wsRoot,
                        Items = lines,
                        Meta = Evidence(),
                    };

                if (dryRun)
                {
                    foreach (var target in targets)
                    {
                        var name = SafeName(target.Object);
                        var outcome = "would sync " + mode;
                        var row = new HmiObject { ["name"] = name, ["stateBefore"] = target.State, ["outcome"] = outcome };
#if STUDIO_VCI_INITIAL
                        var file = NativeVci.TryMappingFile(wsRoot, target.Object);
                        row["filePath"] = file?.FullName;
                        row["fileExists"] = VciExportPolicy.FileExists(file);
                        plannedFiles.Add(new HmiObject { ["filePath"] = row["filePath"], ["fileExists"] = row["fileExists"] });
                        reportRows = true;
#endif
#if !STUDIO_VCI_INITIAL && !STUDIO_VCI_MODERN
                        if (target.RefreshMessage != null) row["filePath"] = NativeVci.TryMappingFile(wsRoot, target.Object)?.FullName;
#endif
                        string fileNote = target.RefreshMessage != null && row.TryGetValue("filePath", out var path) ? " | file=" + path : "";
                        lines.Add(name + " | " + outcome + fileNote + RefreshNote(row, target.RefreshMessage));
                        rows.Add(row);
                    }
                    string message = string.Format(
                        "DRY RUN — nothing was written. {0} object(s) would be synchronized {1} in workspace '{2}'. Call again with dryRun=false to do it.",
                        targets.Count, mode, wsName);
                    message = selection.Summary(message) + " Folder: " + wsRoot;
                    var meta = Evidence();
                    meta["wouldSynchronize"] = targets.Count;
                    return new VersionControlResult { Message = message, Items = lines, Meta = meta };
                }

                // If one export disposes the workspace, the other selected mappings may also need fresh handles.
                var prepared = targets.Select(t => (t.Object, t.ProbeFailed, t.State, t.RefreshMessage,
#if !STUDIO_VCI_MODERN
                    File: t.RefreshMessage == null ? null : NativeVci.TryMappingFile(wsRoot, t.Object)?.FullName,
#else
                    File: (string?)null,
#endif
                    Path: VciSyncTarget.Capture(SafeName(t.Object), () => ReadDirectory(t.Object)))).ToArray();
#if !STUDIO_VCI_MODERN
                bool stopped = VciStoredEqual.PrepareAll(stored, false);
#else
                bool stopped = false;
#endif
                Workspace? current = ws;
                MappedObject FindCurrent(VciSyncTarget path) => path.Find(
                    WorkspaceMappings(current ??= ReAcquireWorkspace(wsName)), PortVci.FileName, ReadDirectory);
                Exception? lookupFailure = null;
                bool reacquireNext = false;
                foreach (var target in prepared)
                {
                    if (stopped)
                    {
                        RecordNotAttempted(target.Path.Stem, target.State);
                        continue;
                    }
                    var mo = target.Object;
                    if (reacquireNext)
                    {
                        try { mo = attempts.FindNext(() => FindCurrent(target.Path)); }
                        catch (Exception error)
                        {
                            lookupFailure = error;
                            stopped = true;
                            RecordNotAttempted(target.Path.Stem, target.State);
                            continue;
                        }
                    }
                    string? readbackMessage = null;
#if STUDIO_VCI_INITIAL
                    Func<Action, bool> repair = issued => NativeVci.RepairMissingFile(wsRoot, mo, attempts.RecordFile, issued);
#else
                    Func<Action, bool>? repair = null;
#endif
                    string? failure = attempts.Synchronize(wsRoot, target.Path, () => PortVci.Synchronize(mo, mode),
                        () => { current = null; return FindCurrent(target.Path); }, fresh => ReadStatus(fresh, out readbackMessage),
                        target.ProbeFailed, out var statusAfter, mode.ToString(), repair);
                    string name = target.Path.Stem;
                    var row = new HmiObject { ["name"] = name, ["stateBefore"] = target.State, ["outcome"] = failure == null ? "synchronized" : "failed" };
                    if (attempts.WrittenFile != null)
                    {
                        row["fileWritten"] = true;
                        row["filePath"] = attempts.WrittenFile;
                        reportRows = true;
                    }
                    string fileNote = "";
                    if (target.File is string path)
                    {
                        row["filePath"] = path;
                        fileNote = " | file=" + path;
                    }
                    string note = RefreshNote(row, target.RefreshMessage);
                    rows.Add(row);
                    if (failure == null)
                    {
                        lines.Add(name + " | synchronized" + fileNote + note);
                    }
                    else
                    {
                        reacquireNext = true;
                        if (statusAfter == null) current = null;
                        failed++;
                        var failedRow = new HmiObject { ["name"] = name, ["statusAfter"] = statusAfter ?? "Unknown" };
                        failedRow["nativeMessage"] = failure;
                        if (row.TryGetValue("filePath", out var filePath)) failedRow["filePath"] = filePath;
                        string readbackNote = RefreshNote(failedRow, readbackMessage);
                        if (readbackMessage != null)
                        {
                            reportRows = true;
                            row["statusAfter"] = statusAfter;
                            if (target.RefreshMessage == null) row["refreshMessage"] = readbackMessage;
                            if (statusAfter == "Equal") selection.CountStoredEqual();
                        }
                        lines.Add(name + " | FAILED: " + failure + fileNote + note + (readbackMessage == target.RefreshMessage ? "" : readbackNote));
                        failures.Add(failedRow);
                        row["nativeMessage"] = failure;
                        stopped = attempts.InterruptedBatch;
                    }
                }

#if !STUDIO_VCI_MODERN
                foreach (var proof in stored)
                {
                    proof.Finish(() => { current = null; return FindCurrent(proof.Path); }, mode, stopped);
                    ReportProof(proof);
                    stopped |= proof.Interrupted;
                }
#endif
                if (lookupFailure != null) return Interrupted(lookupFailure);
                string summary = string.Format(
                    selection.UnverifiedEqual == 0 ? "Workspace '{0}' ({1}): {2} synchronized, {3} failed, {4} already equal (skipped)."
                        : "Workspace '{0}' ({1}): {2} synchronized, {3} failed, {4} skipped with status Equal.",
                    wsName, mode, attempts.Synchronized, failed, selection.SkippedEqual);
                if (failed == 0 && selection.Complete && !stopped) summary += " The text files are updated — `git add -A && git commit` from that folder.";
                summary = selection.Summary(summary) + " Folder: " + wsRoot;
                summary += VersionControlResult.NotVerifiedSummary(unprovenNames);
                var resultMeta = Evidence();
                resultMeta["failures"] = failures;
                return new VersionControlResult { Message = summary, Items = lines, Meta = resultMeta };
            }
            catch (Exception ex) { return Interrupted(ex); }

            void AddNotAttempted(HmiObject meta)
            {
                if (notAttempted.Count == 0) return;
                meta["notAttempted"] = notAttempted.Count;
                meta["notAttemptedNames"] = notAttempted.ToArray();
            }

            void RecordNotAttempted(string name, string state)
            {
                notAttempted.Add(name);
                reportRows = true;
                lines.Add(name + " | not-attempted");
                rows.Add(new HmiObject { ["name"] = name, ["stateBefore"] = state, ["outcome"] = "not-attempted" });
            }

            VersionControlResult Interrupted(Exception ex)
            {
                var meta = attempts.Interrupted(ex);
                AddNotAttempted(meta);
                if (unprovenNames.Count > 0 && meta.TryGetValue("failed", out var previous) && previous is int count)
                    meta["failed"] = count + unprovenNames.Count;
                selection.AddProbeEvidence(meta);
                if (reportRows)
                {
                    meta["items"] = rows;
                    if (failures.Count > 0) meta["failures"] = failures;
                }
                return new VersionControlResult
                {
                    Message = selection.Summary("SynchronizeVersionControlWorkspace failed: " + ex.Message)
                        + VersionControlResult.NotVerifiedSummary(unprovenNames),
                    Items = lines,
                    Meta = meta,
                };
            }
        }

        private static string SafeRoot(Workspace ws)
        {
            try { return PortVci.Root(ws)?.FullName ?? "?"; } catch { /* swallow(probe-optional): An unreadable workspace root is represented by the existing question-mark placeholder. */ return "?"; }
        }
    }
}

#endif
