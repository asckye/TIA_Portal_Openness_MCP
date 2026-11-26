#nullable disable
#if STUDIO_VCI
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NativeRefusalProbe = TiaMcp.Adapters.Contracts.NativeRefusalProbe;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Native.Vci;
#if !STUDIO_VCI_MODERN
using MappedObject = Siemens.Engineering.VersionControl.WorkspaceMapping;
#endif
#if TIA_ADAPTER_INTERNAL_VERSIONING
using TiaMcp.Adapters.Contracts.Studio;
using TiaMcp.Adapters.Contracts.Studio.Errors;
#else
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
#endif
#if TIA_ADAPTER_INTERNAL_VERSIONING
using IVersionControl = TiaMcp.Adapters.Contracts.IVersionControl;
using ITiaSession = TiaMcp.Adapters.Contracts.IStudioSession;
#else
using TiaOpenness.Core.Abstractions;
#endif

namespace TiaOpenness.Openness
{
    /// <summary>
    /// TIA Portal's Version Control Interface over Openness.
    ///
    /// Three things about this API are not obvious and cost a lot of time if rediscovered:
    ///
    /// <list type="number">
    /// <item>
    /// <b>Lifetime.</b> Openness hands out COM-backed proxies whose lifetime is tied to the
    /// parent they came from. Let the service, or an intermediate group, be collected and every
    /// object reached through it dies with it: "Access to a disposed object of type Workspace".
    /// So the service is cached for the life of the project and every intermediate is rooted in
    /// <see cref="_keepAlive"/>. Lazy iterators are avoided for the same reason - a yield-return
    /// walk lets a group be collected between MoveNext calls.
    /// </item>
    /// <item>
    /// <b>Mapping.</b> V20/V21 use <c>ExportObject</c> to write and register the object.
    /// V16/V17 export or verify the XML before creating the mapping. V18/V19 create it and synchronize.
    /// </item>
    /// <item>
    /// <b>Stored comparison states.</b> When a legacy status refresh is refused, a stored Equal
    /// requires a content comparison. Only ProjectToWorkspace may then attempt a native write.
    /// </item>
    /// </list>
    /// </summary>
    internal sealed partial class OpennessVersionControl : IVersionControl
    {
        /// <summary>Upper bound on tree nodes visited by one mapping run, so a huge project cannot hang the bridge.</summary>
        private const int WalkBudget = 5000;

        private readonly Func<Project> _project;
        private readonly List<object> _keepAlive = new List<object>();
        private VersionControlInterface _service;
        private Project _serviceOwner;

        public OpennessVersionControl(Func<Project> project)
        {
            _project = project;
        }

        /// <summary>
        /// True when the current project exposes the Version Control Interface service.
        /// </summary>
        public static bool IsAvailable(Project project)
        {
            try
            {
                return VersionControlPrimitives.Service(project as IEngineeringServiceProvider) != null;
            }
            catch (Exception) /* swallow(probe-optional): a project without an accessible VersionControlInterface service is reported as VCI unavailable */
            {
                return false;
            }
        }

        // ---- workspaces ----------------------------------------------------

        public WorkspaceInfo CreateWorkspace(string name, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A workspace name is required.");
            if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentException("A folder path is required.");

            var directory = new DirectoryInfo(TiaOpenness.Shared.NativePathSelection.FullPath(folderPath.Trim()));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(
                    "folderPath does not exist: " + directory.FullName +
                    ". Create the folder, or clone the Git repository, first.");
            }

            if (AllWorkspaces().Any(w => string.Equals(SafeName(w), name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A workspace named '" + name + "' already exists.");
            }

            var group = Keep(VersionControlPrimitives.Group(Service()));
#if STUDIO_VCI_INITIAL
            var workspace = Keep(VersionControlPrimitives.Create(Keep(VersionControlPrimitives.Workspaces(group)), name.Trim()));
            VersionControlPrimitives.SetRoot(workspace, directory);
#else
            var workspace = Keep(VersionControlPrimitives.Create(Keep(VersionControlPrimitives.Workspaces(group)), name.Trim(), directory));
#endif
            return Describe(workspace);
        }

        // ---- mapping -------------------------------------------------------

        public MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress)
        {
            var workspace = FindWorkspace(workspaceName);
            var rootPath = SafeRoot(workspace);

            var result = new MappingResult
            {
                WorkspaceName = SafeName(workspace),
                RootPath = rootPath,
                DryRun = dryRun,
            };

            var plannedFiles = dryRun ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            var pending = new Stack<VcNode>();
            pending.Push(new VcNode { Object = _project(), Label = "<project>", RelativeDirectory = "", Descendable = true });

            while (pending.Count > 0)
            {
                if (result.Visited >= WalkBudget) { result.Truncated = true; break; }

                var node = pending.Pop();
                result.Visited++;
                if (progress != null) progress("vc-map", result.Visited, 0, node.Label);

                if (SkipByFilter(node, deviceFilter)) continue;

                var formats = SupportedFormats(ref workspace, node.Object);
                if (formats.Count == 0)
                {
                    if (node.Descendable)
                    {
                        foreach (var child in Children(node)) pending.Push(child);
                    }
                    else
                    {
                        result.Unsupported++;
                        result.Items.Add(new MappingItem
                        {
                            Target = node.Label,
                            Outcome = "unsupported",
                            Error = "VCI offers no file format for this object.",
                        });
                    }
                    continue;
                }

                // Coarse-first: an object VCI can map as a unit owns its children, so do not
                // descend into it. That is what turns "map hundreds of blocks" into one call.
                if (!MapOne(ref workspace, node, formats, rootPath, dryRun, result, plannedFiles)) break;
            }

            return result;
        }

        private bool MapOne(ref Workspace workspace, VcNode node, IList<string> formats, string rootPath,
            bool dryRun, MappingResult result, HashSet<string> plannedFiles)
        {
            if (Existing(ref workspace, node.Object) != null)
            {
                result.AlreadyMapped++;
                result.Items.Add(new MappingItem { Target = node.Label, Outcome = "already mapped" });
                return true;
            }

            var format = PreferredFormat(formats);
#if STUDIO_VCI_INITIAL
            bool reuse = false;
            FileInfo file;
            try
            {
                file = TiaMcp.Adapters.Contracts.VciWorkspaceLayout.LegacyMappingFile(rootPath, MappingName(node) + ".xml");
                if (dryRun && !plannedFiles.Add(file.FullName))
                    throw new IOException("Another selected object maps to the same file: " + file.Name);
                if (!dryRun && VciExportPolicy.TargetExists(file))
                {
                    if (!VersionControlPrimitives.MatchesExisting(rootPath, file, node.Object, out string difference))
                        throw new IOException(VciExportPolicy.ExistingFile(file.Name) + difference);
                    reuse = true;
                }
            }
            catch (Exception error) when (VersionControlPrimitives.Ordinary(error)
                && (error is IOException || error is UnauthorizedAccessException || error is ArgumentException))
            {
                result.Failed++;
                result.Items.Add(new MappingItem { Target = node.Label, Outcome = "failed",
                    Error = TiaMcp.Adapters.Contracts.NativeFailureText.BoundedDiagnostic(error) });
                return true;
            }
#else
            FileStream existingFile;
            try { existingFile = VersionControlPrimitives.OpenConnectTarget(rootPath, MappingName(node), format, node.Object); }
            catch (Exception error) when (VersionControlPrimitives.Ordinary(error))
            {
                result.Failed++;
                result.Items.Add(new MappingItem { Target = node.Label, Outcome = dryRun ? "would refuse" : "failed",
                    Error = FileDiagnostic(error.Message) });
                return true;
            }
            using var heldTarget = existingFile;
#endif
            if (dryRun)
            {
                result.Mapped++;
                result.Items.Add(new MappingItem
                {
                    Target = node.Label,
#if STUDIO_VCI_INITIAL
                    Outcome = VciExportPolicy.FileExists(file) == true ? "existing file will be compared" : "would map",
#else
                    Outcome = heldTarget == null ? "would map" : "existing XML content verified; would reuse",
#endif
                    FileFormat = format,
                    Directory = string.Empty,
                });
                return true;
            }

#if STUDIO_VCI_INITIAL
            bool fileWritten = false;
            bool createIssued = false;
#endif
            try
            {
#if STUDIO_VCI_INITIAL
                if (!reuse)
                {
                    VersionControlPrimitives.ExportObject(node.Object, file, ExportOptions.None);
                    fileWritten = true;
                }
                createIssued = true;
                var mapping = Keep(Keep(workspace.Mappings).Create(file.Name, node.Object));
                VersionControlPrimitives.SynchronizeNewMapping(mapping);
                string directory = string.Empty;
#else
                var directory = WriteMapping(ref workspace, node, format, heldTarget != null);
#endif
                result.Mapped++;
                result.Items.Add(new MappingItem
                {
                    Target = node.Label,
                    Outcome = "mapped",
                    FileFormat = format,
                    Directory = directory,
                });
            }
            catch (Exception ex)
            {
                var reason = TiaMcp.Adapters.Contracts.NativeFailureText.BoundedDiagnostic(ex);
#if STUDIO_VCI_INITIAL
                if (!VersionControlPrimitives.Ordinary(ex))
                    throw new InvalidOperationException("The mapping outcome is unknown. " + reason, ex);
                if (createIssued)
                {
                    if (fileWritten) reason += " | file written: " + file.Name;
                    try
                    {
                        Reset();
                        workspace = FindWorkspace(result.WorkspaceName);
                        reason += Existing(ref workspace, node.Object) == null ? " | mapping absent" : " | mapping present";
                    }
                    catch (Exception lookup) { reason += " | mapping lookup failed: " + TiaMcp.Adapters.Contracts.NativeFailureText.BoundedDiagnostic(lookup); }
                }
                else
                {
                    reason += " | no mapping created";
                    if (File.Exists(file.FullName)) reason += " | file present after refused export: " + file.Name;
                }
#endif
                result.Failed++;
                result.Items.Add(new MappingItem
                {
                    Target = node.Label,
                    Outcome = "failed",
                    Error = FileDiagnostic(reason),
                });
                return false;
            }
            return true;
        }

        private static string MappingName(VcNode node)
        {
            var flat = Sanitize((node.RelativeDirectory ?? "").Replace(Path.DirectorySeparatorChar, '_') + "_" + node.Name);
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                var digest = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(node.RelativeDirectory + "\n" + node.Name));
                flat += "_" + BitConverter.ToString(digest, 0, 4).Replace("-", "").ToLowerInvariant();
            }
            return flat;
        }

#if !STUDIO_VCI_INITIAL
        private string WriteMapping(ref Workspace workspace, VcNode node, string format, bool reuse)
        {
            string flat = MappingName(node);
#if STUDIO_VCI_MODERN
            var directory = VciExportPolicy.RootDirectory();
            if (reuse) VersionControlPrimitives.ConnectExisting(workspace, node.Object, directory, flat, format);
            else VersionControlPrimitives.Export(workspace, node.Object, directory, flat, format);
#else
            var mapping = Keep(Keep(workspace.Mappings).Create(flat + ".xml", node.Object));
            if (!reuse)
            {
                using var createdFile = VersionControlPrimitives.OpenConnectTarget(workspace.RootPath.FullName, flat, format, node.Object);
                if (createdFile == null)
                    Keep(mapping.GetService<IndividualObjectSynchronizationStatus>()).Synchronize(SynchronizationMode.ProjectToWorkspace);
            }
#endif
            return string.Empty;
        }

#endif

        // ---- status and sync -----------------------------------------------

        public WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly)
        {
            var workspace = FindWorkspace(workspaceName);
            var report = new WorkspaceStatusReport
            {
                WorkspaceName = SafeName(workspace),
                RootPath = SafeRoot(workspace),
            };

            foreach (var mapped in Mappings(workspace).ToList())
            {
                Keep(mapped);
                report.Total++;

                string error;
                var state = StateOf(mapped, out error, out var refreshMessage, out _);
                if (state == VcCompareState.Unknown) report.Unknown++;
                else if (state != VcCompareState.Equal) report.Differing++;
                else if (refreshMessage != null) report.UnverifiedEqual++;
                if (changedOnly && state == VcCompareState.Equal && refreshMessage == null) continue;

                report.Items.Add(new MappedObjectInfo
                {
                    Name = SafeObjectName(mapped),
                    FilePath = SafeFile(mapped, report.RootPath),
                    FileFormat = SafeFormat(mapped),
                    CompareState = state,
                    Verified = state == VcCompareState.Equal && refreshMessage != null ? (bool?)false : null,
                    RefreshMessage = refreshMessage,
                    Error = error,
                });
            }

            return report;
        }

        public SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress)
        {
            var mode = direction == SyncDirection.WorkspaceToProject
                ? SynchronizationMode.WorkspaceToProject
                : SynchronizationMode.ProjectToWorkspace;
#if STUDIO_VCI_MODERN
            bool stopOnConnectionLoss = mode == SynchronizationMode.WorkspaceToProject;
#else
            const bool stopOnConnectionLoss = false;
#endif
            var workspace = FindWorkspace(workspaceName, stopOnConnectionLoss);

            var result = new SyncResult
            {
                WorkspaceName = SafeName(workspace, stopOnConnectionLoss),
                RootPath = SafeRoot(workspace, stopOnConnectionLoss),
                Direction = direction,
                DryRun = dryRun,
            };

            try { SyncMappings(workspace, mode, result, progress, stopOnConnectionLoss); }
            catch (Exception error) when (stopOnConnectionLoss && NativeRefusalProbe.UncertainConnection(error))
            {
                // Discovery or the row label failed before dispatch. Do not probe the lost session for a name.
                result.Items.Add(new SyncItem { Name = "?", Outcome = "refused", Error = Flatten(error) });
                result.Failed++;
                result.DataComplete = false;
            }
            return result;
        }

        private void SyncMappings(Workspace workspace, SynchronizationMode mode, SyncResult result, ProgressCallback progress, bool stopOnConnectionLoss)
        {
            var direction = result.Direction;
            bool dryRun = result.DryRun;
#if !STUDIO_VCI_MODERN
            var stored = new List<VciStoredEqual>();
#endif
            var targets = new List<(MappedObject Object, string Name, string RefreshMessage)>();
            try
            {
                foreach (var mapped in Mappings(workspace).ToList())
                {
                    Keep(mapped);
                    var state = StateOf(mapped, out var error, out var refreshMessage, out var disconnected, stopOnConnectionLoss);
#if !STUDIO_VCI_MODERN
                    if (disconnected)
                    {
                        result.SkippedDisconnected++;
                        result.DataComplete = false;
                        string file = VersionControlPrimitives.TryMappingFile(result.RootPath, mapped)?.FullName;
                        result.Items.Add(new SyncItem { Name = SafeObjectName(mapped), FilePath = file, Outcome = "skipped-disconnected",
                            Error = FileDiagnostic(error + " " + Path.GetFileName(file) + ". " + TiaMcp.Adapters.Contracts.VersionControlResult.Reconnect) });
                        continue;
                    }
#endif
                    if (state == VcCompareState.Equal)
                    {
#if !STUDIO_VCI_MODERN
                        if (refreshMessage != null)
                        {
                            stored.Add(new VciStoredEqual(result.RootPath, mapped, SafeObjectName(mapped), refreshMessage));
                            result.UnverifiedEqual++;
                        }
                        else
#endif
                            result.SkippedEqual++;
                        continue;
                    }
                    targets.Add((mapped, SafeObjectName(mapped, stopOnConnectionLoss), refreshMessage));
                }
            }
            catch (Exception error) when (stopOnConnectionLoss && NativeRefusalProbe.UncertainConnection(error))
            {
                foreach (var target in targets) AddNotAttempted(result, target.Name);
                throw;
            }

#if STUDIO_VCI_MODERN
            bool stopped = false;
#else
            bool stopped = (dryRun || mode == SynchronizationMode.ProjectToWorkspace) && VciStoredEqual.PrepareAll(stored, dryRun);
#endif
            int nextTarget = 0;
            while (!stopped && nextTarget < targets.Count)
            {
                var (mapped, name, refreshMessage) = targets[nextTarget++];
                if (progress != null) progress("vc-sync", nextTarget, targets.Count, name);

#if STUDIO_VCI_MODERN
                if (mode == SynchronizationMode.WorkspaceToProject && RestoreSdMapping(mapped, result.RootPath, name, dryRun,
                    out var connectionLost) is { } restore)
                {
                    result.Items.Add(restore);
                    if (restore.Outcome is "unknown" or "failed") { result.Failed++; break; }
                    if (restore.Outcome == "refused") result.Failed++;
                    else result.Synchronized++;
                    if (connectionLost) break;
                    continue;
                }
#endif
                if (dryRun)
                {
                    result.Synchronized++;
                    result.Items.Add(new SyncItem { Name = name, Outcome = "would sync " + direction, RefreshMessage = refreshMessage });
                    continue;
                }

                string writtenFile = null;
                try
                {
#if STUDIO_VCI_INITIAL
                    bool repairedEqual = mode == SynchronizationMode.ProjectToWorkspace &&
                        VersionControlPrimitives.RepairMissingFile(result.RootPath, mapped, file => writtenFile = file.Name);
                    if (!repairedEqual)
#endif
                        Synchronize(mapped, mode);
                    result.Synchronized++;
                    result.Items.Add(new SyncItem { Name = name, Outcome = "synchronized", RefreshMessage = refreshMessage });
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    var reason = TiaMcp.Adapters.Contracts.NativeFailureText.BoundedDiagnostic(ex);
                    if (writtenFile != null) reason += " | file written: " + writtenFile;
                    result.Items.Add(new SyncItem { Name = name, Outcome = "failed", Error = FileDiagnostic(reason), RefreshMessage = refreshMessage });
                    stopped = true;
                }
            }
            foreach (var target in targets.Skip(nextTarget)) AddNotAttempted(result, target.Name);
#if !STUDIO_VCI_MODERN
            for (int i = 0; i < stored.Count; i++)
            {
                var proof = stored[i];
                MappedObject Acquire()
                {
                    Reset();
                    return proof.Path.Find(Mappings(FindWorkspace(result.WorkspaceName)), m => SafeObjectName(m),
                        m => VersionControlPrimitives.TryMappingFile(result.RootPath, m)?.DirectoryName);
                }
                if (dryRun) result.WouldVerify++;
                else
                {
                    if (mode == SynchronizationMode.WorkspaceToProject) proof.CompareAfterImports(Acquire, stopped);
                    proof.Finish(Acquire, mode, stopped);
                    if (proof.NotAttempted) result.NotAttempted++;
                    result.ComparisonInterrupted |= proof.ComparisonInterrupted;
                    if (proof.Unconfirmed) result.Failed++;
                    else if (proof.Outcome == "not-verified") { result.NotVerified++; result.Failed++; }
                    else
                    {
                        result.UnverifiedEqual--;
                        if (proof.Outcome == "verified-equal") { result.VerifiedEqual++; result.SkippedEqual++; }
                        else result.Synchronized++;
                    }
                }
                var item = new SyncItem { Name = proof.Path.Stem, Outcome = proof.Outcome, FilePath = proof.Proof.File?.FullName,
                    WorkspaceFileSha256 = proof.InitialHash, StateBefore = "Equal", RefreshMessage = proof.RefreshMessage, FileChanged = proof.FileChanged,
                    Verification = proof.Verification, NativeMessage = proof.Message, Error = proof.Outcome == "not-verified" ? proof.Message : null };
                result.Items.Add(item);
                if (proof.Unconfirmed)
                {
                    var pending = stored.Skip(i + 1).Select(row => row.Path.Stem).ToArray();
                    string remaining = pending.Length == 0 ? "" : " | not-attempted (" + pending.Length + "): " + string.Join(", ", pending);
                    item.Outcome = "unknown";
                    item.Error = "The synchronization outcome is unknown. " + proof.Message + remaining;
                    foreach (var name in pending) AddNotAttempted(result, name);
                    stopped = true;
                    break;
                }
                stopped |= proof.Interrupted;
            }
#endif
            result.DataComplete &= result.UnverifiedEqual == 0 && result.Failed == 0 && result.NotAttempted == 0 && !stopped;
        }

        private static void AddNotAttempted(SyncResult result, string name)
        {
            result.NotAttempted++;
            result.Items.Add(new SyncItem { Name = name, Outcome = "not-attempted" });
        }

        private static string FileDiagnostic(string text, string detail = null) => TiaMcp.Adapters.Contracts.NativeFailureText.BoundedLines(
            new[] { TiaMcp.Adapters.Contracts.NativeFailureText.Compose(text, detail, Array.Empty<string>()) },
            TiaMcp.Adapters.Contracts.NativeFailureText.WithoutLocalPaths)[0];

        // GetStatus returns a comparison object. Its CompareState property contains the verdict.
        private static VcCompareState StateOf(MappedObject mapped, out string error, out string refreshMessage,
            out bool disconnected, bool stopOnConnectionLoss = false)
        {
            disconnected = false;
            error = null;
            refreshMessage = null;
            try
            {
#if STUDIO_VCI_MODERN
                var state = ReadMappedState(mapped, out error).ToString();
#else
                var state = VersionControlPrimitives.State(ReadStatus(mapped, out refreshMessage)).ToString();
#endif
                VcCompareState parsed;
                if (Enum.TryParse(state, true, out parsed)) return parsed;

                error = "unrecognised compare state '" + state + "'";
                return VcCompareState.Unknown;
            }
            catch (Exception ex) when (!stopOnConnectionLoss || !NativeRefusalProbe.UncertainConnection(ex))
            {
                error = FileDiagnostic(ex.Message, ex.InnerException?.Message);
#if !STUDIO_VCI_MODERN
                disconnected = VersionControlPrimitives.DisposedStatus(ex);
                if (disconnected) error = VersionControlPrimitives.DisconnectedMapping + ": " + error;
#endif
                return VcCompareState.Unknown;
            }
        }

    }
}
#endif
