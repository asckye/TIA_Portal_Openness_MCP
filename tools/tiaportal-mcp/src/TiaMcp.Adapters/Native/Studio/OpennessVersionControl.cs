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
#else
using TiaOpenness.Contracts.Models;
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
    internal sealed class OpennessVersionControl : IVersionControl
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
                return (project as IEngineeringServiceProvider)?.GetService<VersionControlInterface>() != null;
            }
            catch (Exception) /* swallow(probe-optional): a project without an accessible VersionControlInterface service is reported as VCI unavailable */
            {
                return false;
            }
        }

        // ---- workspaces ----------------------------------------------------

        public IReadOnlyList<WorkspaceInfo> ListWorkspaces()
        {
            return AllWorkspaces().Select(Describe).ToList();
        }

        public WorkspaceInfo CreateWorkspace(string name, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A workspace name is required.");
            if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentException("A folder path is required.");

            var directory = new DirectoryInfo(folderPath.Trim());
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

            var group = Keep(Service().WorkspaceGroup);
#if STUDIO_VCI_INITIAL
            var workspace = Keep(Keep(group.Workspaces).Create(name.Trim()));
            workspace.RootPath = directory;
#else
            var workspace = Keep(Keep(group.Workspaces).Create(name.Trim(), directory));
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
            workspace.ExportObject(node.Object, new DirectoryInfo(rootPath), flat, format);
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
                var state = ReadStatus(mapped).CompareState.ToString();
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

        // ---- tree walk -----------------------------------------------------

        private sealed class VcNode
        {
            public IEngineeringObject Object;
            /// <summary>Position in the project tree, for the report.</summary>
            public string Label;
            /// <summary>Name to give the file.</summary>
            public string Name;
            /// <summary>Folder inside the workspace; empty means the workspace root.</summary>
            public string RelativeDirectory;
            /// <summary>May the walk descend when VCI cannot map this node as a unit?</summary>
            public bool Descendable;
            public bool IsDevice;
        }

        /// <summary>
        /// Typed children only. The reflection route (GetComposition) hands back transient proxies
        /// that Openness disposes immediately, so anything reached that way throws on first use.
        /// </summary>
        private List<VcNode> Children(VcNode node)
        {
            var children = new List<VcNode>();

            var project = node.Object as Project;
            if (project != null)
            {
                foreach (var device in PlcNavigator.AllDevices(project).ToList())
                {
                    children.Add(Node(device, device.Name, device.Name, device.Name, true, isDevice: true));
                }
                return children;
            }

            var deviceNode = node.Object as Device;
            if (deviceNode != null)
            {
                var software = PlcNavigator.FindPlcSoftware(deviceNode);
                if (software != null)
                {
                    children.Add(Node(software, node.Label + "/Software", deviceNode.Name, node.RelativeDirectory, true));
                }
                return children;
            }

            var plc = node.Object as PlcSoftware;
            if (plc != null)
            {
                children.Add(Node(plc.BlockGroup, node.Label + "/Program blocks", "Blocks", node.RelativeDirectory, true));
                children.Add(Node(plc.TypeGroup, node.Label + "/PLC data types", "Types", node.RelativeDirectory, true));
                children.Add(Node(plc.TagTableGroup, node.Label + "/PLC tags", "Tags", node.RelativeDirectory, true));
                return children;
            }

            var blockGroup = node.Object as PlcBlockGroup;
            if (blockGroup != null)
            {
                foreach (var block in Keep(blockGroup.Blocks).ToList())
                {
                    children.Add(Node(Keep(block), node.Label + "/" + block.Name, block.Name, node.RelativeDirectory, false));
                }
                foreach (var sub in Keep(blockGroup.Groups).ToList())
                {
                    children.Add(Node(Keep(sub), node.Label + "/" + sub.Name, sub.Name,
                        Combine(node.RelativeDirectory, sub.Name), true));
                }
                return children;
            }

            var typeGroup = node.Object as PlcTypeGroup;
            if (typeGroup != null)
            {
                foreach (var type in Keep(typeGroup.Types).ToList())
                {
                    children.Add(Node(Keep(type), node.Label + "/" + type.Name, type.Name, node.RelativeDirectory, false));
                }
                foreach (var sub in Keep(typeGroup.Groups).ToList())
                {
                    children.Add(Node(Keep(sub), node.Label + "/" + sub.Name, sub.Name,
                        Combine(node.RelativeDirectory, sub.Name), true));
                }
                return children;
            }

            var tagGroup = node.Object as PlcTagTableGroup;
            if (tagGroup != null)
            {
                foreach (var table in Keep(tagGroup.TagTables).ToList())
                {
                    children.Add(Node(Keep(table), node.Label + "/" + table.Name, table.Name, node.RelativeDirectory, false));
                }
                foreach (var sub in Keep(tagGroup.Groups).ToList())
                {
                    children.Add(Node(Keep(sub), node.Label + "/" + sub.Name, sub.Name,
                        Combine(node.RelativeDirectory, sub.Name), true));
                }
                return children;
            }

            return children;
        }

        private static VcNode Node(IEngineeringObject o, string label, string name, string relativeDirectory,
            bool descendable, bool isDevice = false)
        {
            return new VcNode
            {
                Object = o,
                Label = label,
                Name = name,
                RelativeDirectory = relativeDirectory ?? string.Empty,
                Descendable = descendable,
                IsDevice = isDevice,
            };
        }

        private static bool SkipByFilter(VcNode node, string deviceFilter)
        {
            return !string.IsNullOrWhiteSpace(deviceFilter)
                && node.IsDevice
                && !string.Equals(node.Name, deviceFilter.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Which formats VCI can write this object as. Empty means it cannot be mapped.</summary>
        private IList<string> SupportedFormats(ref Workspace workspace, IEngineeringObject o)
        {
#if STUDIO_VCI_MODERN
            var formats = workspace.GetSupportedFileFormats(o);
#else
            // The old VCI API exposes individual PLC block/type/tag-table mappings only.
            IList<string> formats = o is PlcBlock || o is PlcType || o is PlcTagTable ? new[] { "xml" } : new string[0];
#endif
            return formats == null ? new List<string>() : formats.ToList();
        }

        private MappedObject Existing(ref Workspace workspace, IEngineeringObject o)
        {
#if STUDIO_VCI_MODERN
            return workspace.MappedObjects.Find(o);
#else
            return workspace.Mappings.Find(o);
#endif
        }

        /// <summary>s7dcl is the reviewable text format; the others are fallbacks.</summary>
        private static string PreferredFormat(IList<string> formats)
        {
            return formats.FirstOrDefault(f => f.IndexOf("s7dcl", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault(f => f.IndexOf("simatic", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault(f => f.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats[0];
        }

        // ---- service and lifetime ------------------------------------------

        private VersionControlInterface Service()
        {
            var project = _project();
            if (project == null)
            {
                Reset();
                throw new InvalidOperationException("No project is open. Call project.open first.");
            }

            if (_service != null && ReferenceEquals(_serviceOwner, project)) return _service;

            var service = (project as IEngineeringServiceProvider)?.GetService<VersionControlInterface>();
            if (service == null)
            {
                throw new NotSupportedException(
                    "This project exposes no Version Control Interface in the current installation.");
            }

            Reset();
            _serviceOwner = project;
            _service = service;
            Keep(service);
            return service;
        }

        private void Reset()
        {
            _keepAlive.Clear();
            _service = null;
            _serviceOwner = null;
        }

        /// <summary>
        /// Roots an Openness proxy for as long as the project is open. Letting an intermediate be
        /// collected disposes everything reached through it.
        /// </summary>
        private T Keep<T>(T o) where T : class
        {
            if (o != null) _keepAlive.Add(o);
            return o;
        }

        /// <summary>Nothing here is lazy: a yield-return walk lets the groups be collected mid-iteration.</summary>
        private List<Workspace> AllWorkspaces()
        {
            var found = new List<Workspace>();
            var pending = new Stack<WorkspaceGroup>();
            pending.Push(Keep(Service().WorkspaceGroup));

            while (pending.Count > 0)
            {
                var group = pending.Pop();
                foreach (var workspace in Keep(group.Workspaces).ToList()) found.Add(Keep(workspace));
                foreach (var sub in Keep(group.Groups).ToList()) pending.Push(Keep(sub));
            }
            return found;
        }

        private Workspace FindWorkspace(string name)
        {
            var all = AllWorkspaces();
            if (all.Count == 0)
            {
                throw new InvalidOperationException(
                    "This project has no version control workspace yet. Create one first, then map the " +
                    "project's objects into it.");
            }
            if (string.IsNullOrWhiteSpace(name)) return all[0];

            var match = all.FirstOrDefault(w =>
                string.Equals(SafeName(w), name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                throw new KeyNotFoundException("No workspace named '" + name + "'. Available: " +
                    string.Join(", ", all.Select(SafeName)));
            }
            return match;
        }

        // ---- defensive readers ---------------------------------------------

        private static WorkspaceInfo Describe(Workspace workspace)
        {
            var count = 0;
            try { count = Mappings(workspace).Count(); }
            catch (Exception) /* swallow(enumerate-optional): unreadable workspace mappings leave the descriptive mapping count at zero */ { count = 0; }

            return new WorkspaceInfo
            {
                Name = SafeName(workspace),
                RootPath = SafeRoot(workspace),
                Language = SafeLanguage(workspace),
                MappedObjectCount = count,
            };
        }

        private static string SafeName(Workspace workspace)
        {
            try { return workspace.Name; } catch (Exception) /* swallow(probe-optional): a stale workspace name uses the diagnostic placeholder */ { return "?"; }
        }

        private static string SafeRoot(Workspace workspace)
        {
            try { return workspace.RootPath?.FullName ?? "?"; } catch (Exception) /* swallow(probe-optional): an unavailable workspace root uses the diagnostic placeholder */ { return "?"; }
        }

        private static string SafeLanguage(Workspace workspace)
        {
#if STUDIO_VCI_MODERN
            try { return workspace.WorkspaceLanguage?.ToString() ?? "-"; } catch (Exception) /* swallow(native-fallback): unreadable workspace language uses the existing dash placeholder in the workspace description */ { return "-"; }
#else
            return "-";
#endif
        }

        private static string SafeObjectName(MappedObject mapped)
        {
#if STUDIO_VCI_MODERN
            try { return mapped.FileNameWithoutExtension ?? "?"; } catch (Exception) /* swallow(native-fallback): unreadable mapped-object names use the question-mark label in status and sync progress results */ { return "?"; }
#else
            return Path.GetFileNameWithoutExtension(mapped.RelativeWorkspacePath);
#endif
        }

#if STUDIO_VCI_MODERN
        private static string SafeFile(MappedObject mapped)
        {
            try
            {
                var directory = string.Empty;
                try { directory = mapped.DirectoryPath?.FullName ?? string.Empty; } catch (Exception) /* swallow(native-fallback): unreadable mapping directory metadata leaves the filename-only status display available */ { }
                var name = mapped.FileNameWithoutExtension ?? string.Empty;
                return directory.Length == 0 ? name : Path.Combine(directory, name);
            }
            catch (Exception) /* swallow(native-fallback): unreadable mapping file metadata uses the question-mark placeholder in the status report */
            {
                return "?";
            }
        }

        private static string SafeFormat(MappedObject mapped)
        {
            try { return mapped.FileFormat?.ToString(); } catch (Exception) /* swallow(native-fallback): unreadable mapping format is omitted from the status report */ { return null; }
        }

#else
        private static string SafeFile(MappedObject mapped)
        {
            var workspace = (Workspace)mapped.Parent;
            return Path.Combine(workspace.RootPath.FullName, mapped.RelativeWorkspacePath);
        }
        private static string SafeFormat(MappedObject mapped) { return "xml"; }
#endif
        private static IEnumerable<MappedObject> Mappings(Workspace workspace)
        {
#if STUDIO_VCI_MODERN
            return workspace.MappedObjects;
#else
            return workspace.Mappings;
#endif
        }
        private static IndividualObjectCompareResult ReadStatus(MappedObject mapped)
        {
#if STUDIO_VCI_MODERN
            return mapped.GetStatus();
#else
            var service = mapped.GetService<IndividualObjectSynchronizationStatus>();
            service.UpdateStatus();
            return service.GetStatus();
#endif
        }
        private static void Synchronize(MappedObject mapped, SynchronizationMode mode)
        {
#if STUDIO_VCI_MODERN
            mapped.Synchronize(mode);
#else
            mapped.GetService<IndividualObjectSynchronizationStatus>().Synchronize(mode);
#endif
        }

        private static string Combine(string parent, string child)
        {
            return string.IsNullOrEmpty(parent) ? child : parent + Path.DirectorySeparatorChar + child;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var buffer = name.ToCharArray();
            for (var i = 0; i < buffer.Length; i++)
            {
                if (Array.IndexOf(invalid, buffer[i]) >= 0) buffer[i] = '_';
            }
            return new string(buffer);
        }

        private static string Flatten(Exception ex)
        {
            var message = (ex.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return ex.InnerException == null
                ? message
                : message + " || " + (ex.InnerException.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}

#endif
