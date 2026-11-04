#nullable disable
#if STUDIO_VCI
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
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
    /// <b>Mapping is <c>ExportObject</c>, not <c>ConnectObject</c>.</b> Despite the names,
    /// <c>ExportObject</c> is what creates a mapping: it writes the text file and registers the
    /// object. <c>ConnectObject</c> only binds an object to files that already exist.
    /// </item>
    /// <item>
    /// <b>Equal objects cannot be synchronized.</b> TIA refuses with "Synchronize cannot be
    /// called on a workspace mapping that has a compare status of equal", so "force everything"
    /// is not an option that exists; equal objects are skipped, never attempted.
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
                MapOne(ref workspace, node, formats, rootPath, dryRun, result);
                if (result.Failed > 0) break;
            }

            return result;
        }

        private void MapOne(ref Workspace workspace, VcNode node, IList<string> formats, string rootPath,
            bool dryRun, MappingResult result)
        {
            if (Existing(ref workspace, node.Object) != null)
            {
                result.AlreadyMapped++;
                result.Items.Add(new MappingItem { Target = node.Label, Outcome = "already mapped" });
                return;
            }

            var format = PreferredFormat(formats);
            if (dryRun)
            {
                result.Mapped++;
                result.Items.Add(new MappingItem
                {
                    Target = node.Label,
                    Outcome = "would map",
                    FileFormat = format,
                    Directory = string.Empty,
                });
                return;
            }

            try
            {
                var directory = WriteMapping(ref workspace, node, format, rootPath);
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
                result.Failed++;
                result.Items.Add(new MappingItem
                {
                    Target = node.Label,
                    Outcome = "failed",
                    Error = Flatten(ex),
                });
                // The caller sees the partial result; stop the map batch after this failure.
            }
        }

        /// <summary>Map once at the workspace root; an API failure is never replayed with another path.</summary>
        private string WriteMapping(ref Workspace workspace, VcNode node, string format, string rootPath)
        {
            var flat = Sanitize((node.RelativeDirectory ?? "").Replace(Path.DirectorySeparatorChar, '_') + "_" + node.Name);
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                var digest = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(node.RelativeDirectory + "\n" + node.Name));
                flat += "_" + BitConverter.ToString(digest, 0, 4).Replace("-", "").ToLowerInvariant();
            }
#if STUDIO_VCI_MODERN
            VersionControlPrimitives.Export(workspace, node.Object, TiaMcp.Adapters.Native.Vci.VciExportPolicy.RootDirectory(), flat, format);
#else
            var mapping = Keep(Keep(workspace.Mappings).Create(flat + ".xml", node.Object));
            var service = Keep(mapping.GetService<IndividualObjectSynchronizationStatus>());
            service.Synchronize(SynchronizationMode.ProjectToWorkspace);
#endif
            return string.Empty;
        }

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
                var state = StateOf(mapped, out error);
                if (state != VcCompareState.Equal) report.Differing++;
                if (changedOnly && state == VcCompareState.Equal) continue;

                report.Items.Add(new MappedObjectInfo
                {
                    Name = SafeObjectName(mapped),
                    FilePath = SafeFile(mapped),
                    FileFormat = SafeFormat(mapped),
                    CompareState = state,
                    Error = error,
                });
            }

            return report;
        }

        public SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress)
        {
            var workspace = FindWorkspace(workspaceName);
            var mode = direction == SyncDirection.WorkspaceToProject
                ? SynchronizationMode.WorkspaceToProject
                : SynchronizationMode.ProjectToWorkspace;

            var result = new SyncResult
            {
                WorkspaceName = SafeName(workspace),
                RootPath = SafeRoot(workspace),
                Direction = direction,
                DryRun = dryRun,
            };

            var targets = new List<MappedObject>();
            foreach (var mapped in Mappings(workspace).ToList())
            {
                Keep(mapped);
                string ignored;
                if (StateOf(mapped, out ignored) == VcCompareState.Equal) { result.SkippedEqual++; continue; }
                targets.Add(mapped);
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var mapped = targets[i];
                var name = SafeObjectName(mapped);
                if (progress != null) progress("vc-sync", i + 1, targets.Count, name);

                if (dryRun)
                {
                    result.Synchronized++;
                    result.Items.Add(new SyncItem { Name = name, Outcome = "would sync " + direction });
                    continue;
                }

                try
                {
                    Synchronize(mapped, mode);
                    result.Synchronized++;
                    result.Items.Add(new SyncItem { Name = name, Outcome = "synchronized" });
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Items.Add(new SyncItem { Name = name, Outcome = "failed", Error = Flatten(ex) });
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// <c>GetStatus()</c> returns a result object whose <c>ToString()</c> is the type name,
        /// not the verdict; the verdict is its <c>CompareState</c>.
        /// </summary>
        private static VcCompareState StateOf(MappedObject mapped, out string error)
        {
            error = null;
            try
            {
                var state = VersionControlPrimitives.State(ReadStatus(mapped)).ToString();
                VcCompareState parsed;
                if (Enum.TryParse(state, true, out parsed)) return parsed;

                error = "unrecognised compare state '" + state + "'";
                return VcCompareState.Unknown;
            }
            catch (Exception ex)
            {
                error = Flatten(ex);
                return VcCompareState.Unknown;
            }
        }

    }
}
#endif
