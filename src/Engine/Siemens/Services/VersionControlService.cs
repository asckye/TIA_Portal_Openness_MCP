using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
#if TIA_SHARED_ADAPTER_PATHS
using Vci = TiaOpenness.Openness.VersionControlPrimitives;
#else
using Vci = TiaMcpServer.Siemens.LocalVci.VersionControlPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    // TIA Portal V21's Version Control Interface (VCI), reached from Openness rather than the UI.
    //
    // A TIA project is a binary blob that Git cannot diff. VCI fixes that: a *workspace* is a
    // plain folder holding one text file per mapped object (.s7dcl / .xml), which IS diffable and
    // commitable. Openness can create workspaces and synchronize in both directions, so the whole
    // "export → commit → review → restore" loop can run unattended.
    //
    // Mapping is available through Workspace.ConnectObject.
    // MappedObjectComposition indeed exposes only Find, but the create path does not live on the
    // composition: it is Workspace.ConnectObject(obj, relativeDir, fileName, fileFormat), with
    // Workspace.GetSupportedFileFormats(obj) telling you whether an object can be mapped at all.
    // ConnectProjectToWorkspace below uses both to put a WHOLE project under version control with
    // no UI interaction; creation is on the workspace, not the mapped-object composition.
    //
    // The generic reflection tools cannot reach any of this: they navigate properties from an
    // object, and VersionControlInterface is a *service*, so the traversal dead-ends immediately.
    // Hence a purpose-built toolset.
    internal sealed partial class VersionControlService
    {
        private readonly IEngineeringSession _session;

#if TIA_SHARED_ADAPTER_PATHS
        private TiaMcp.Adapters.PlcServices.VersionControlSurface? _versionControl;
        private ProjectBase? VciProject => (_versionControl ?? (_versionControl =
            TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).VersionControl)).CurrentProject;
#else
        private ProjectBase? VciProject => _session.CurrentProject;
#endif

        public VersionControlService(IEngineeringSession session) => _session = session;

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

            var vci = Vci.Service(project as IEngineeringServiceProvider);
            if (vci == null)
                throw new InvalidOperationException(
                    "This project exposes no VersionControlInterface. VCI requires TIA Portal V21 or later.");
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
            WorkspaceSystemGroup root = Keep(Vci.Group(vci));
            pending.Push(root);
            while (pending.Count > 0)
            {
                var g = pending.Pop();
                foreach (var w in Vci.Enumerate(Keep(Vci.Workspaces(g)))) found.Add(Keep(w));
                foreach (WorkspaceUserGroup sub in Vci.Enumerate(Keep(Vci.Groups(g)))) pending.Push(Keep(sub));
            }
            return found;
        }

        private Workspace FindWorkspace(VersionControlInterface vci, string name)
        {
            var all = AllWorkspaces(vci).ToList();
            if (all.Count == 0)
                throw new InvalidOperationException(
                    "This project has no version control workspace yet. Create one with " +
                    "CreateVersionControlWorkspace, then map objects into it with " +
                    "ConnectProjectToWorkspace (no UI interaction needed).");
            if (string.IsNullOrWhiteSpace(name)) return all[0];
            var hit = all.FirstOrDefault(w => string.Equals(Vci.Name(w), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                throw new InvalidOperationException(
                    "No workspace named '" + name + "'. Available: " +
                    string.Join(", ", all.Select(w => Vci.Name(w))));
            return hit;
        }

        private static JsonObject FailureEvidence(Exception error, bool issued = false)
        {
            var meta = ResponseMeta.Basic(DateTime.Now, false);
            meta["mayHaveChanged"] = issued;
            if (!issued)
                meta["v4Rejection"] = error is PortalException portal && portal.Code == PortalErrorCode.InvalidState ? "PROJECT_NOT_BOUND"
                    : error is ArgumentException ? "INVALID_ARGUMENT"
                    : error is DirectoryNotFoundException ? "NOT_FOUND" : null;
            return meta;
        }

        public ResponseStringList GetVersionControlWorkspaces()
        {
            try
            {
                var vci = RequireVci();
                var lines = new List<string>();
                var entries = new JsonArray();
                int n = 0;
                foreach (var w in AllWorkspaces(vci))
                {
                    n++;
                    int mapped = 0;
                    try { mapped = Vci.Count(Vci.MappedObjects(w)); } catch { /* swallow(probe-optional): An unavailable mapped-object count leaves the existing zero fallback while other workspace fields remain readable. */ }
                    string root = "";
                    try { root = Vci.Root(w)?.FullName ?? ""; } catch { /* swallow(probe-optional): An unavailable workspace root leaves the existing empty-path fallback in the workspace listing. */ }
                    lines.Add(string.Format(
                        "{0} | folder={1} | mappedObjects={2} | language={3}",
                        Vci.Name(w), root, mapped, SafeLanguage(w)));
                    entries.Add(new JsonObject { ["name"] = Vci.Name(w), ["rootPath"] = root, ["mappedObjectCount"] = mapped, ["language"] = SafeLanguage(w) });
                }
                return new ResponseStringList
                {
                    Message = n == 0
                        ? "No version control workspace exists in this project yet. Create one with " +
                          "CreateVersionControlWorkspace, then map objects into it in the TIA UI."
                        : n + " version control workspace(s).",
                    Items = lines,
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("workspaces", entries)),
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "ListVersionControlWorkspaces failed: " + ex.Message,
                    Meta = FailureEvidence(ex),
                };
            }
        }

        private static string SafeLanguage(Workspace w)
        {
            try { return Vci.Language(w)?.ToString() ?? "-"; }
            catch { /* swallow(probe-optional): An unavailable workspace language is represented by the existing dash placeholder. */ return "-"; }
        }

        public ResponseMessage CreateVersionControlWorkspace(
            string workspaceName,
            string folderPath)
        {
            bool issued = false;
            try
            {
                if (string.IsNullOrWhiteSpace(workspaceName))
                    throw new ArgumentException("workspaceName is required.");
                if (string.IsNullOrWhiteSpace(folderPath))
                    throw new ArgumentException("folderPath is required.");
                var dir = new DirectoryInfo(folderPath.Trim());
                if (!dir.Exists)
                    throw new DirectoryNotFoundException(
                        "folderPath does not exist: " + dir.FullName + ". Create the folder (or clone the repo) first.");

                var vci = RequireVci();
                var existing = AllWorkspaces(vci)
                    .FirstOrDefault(w => string.Equals(Vci.Name(w), workspaceName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    return new ResponseMessage
                    {
                        Message = "A workspace named '" + workspaceName + "' already exists. " +
                                  "Use ListVersionControlWorkspaces to inspect it.",
                        Meta = ResponseMeta.Basic(DateTime.Now, false, ("v4Rejection", "ALREADY_EXISTS")),
                    };

                var group = Keep(Vci.Group(vci));
                issued = true;
                var ws = Keep(Vci.Create(Keep(Vci.Workspaces(group)), workspaceName.Trim(), dir));
                return new ResponseMessage
                {
                    Message = "Created workspace '" + Vci.Name(ws) + "' at " + dir.FullName +
                              ". Next: ConnectProjectToWorkspace to map the project's objects into it, " +
                              "then SynchronizeVersionControlWorkspace to write them out.",
                    Meta = ResponseMeta.Basic(DateTime.Now, true),
                };
            }
            catch (Exception ex)
            {
                return new ResponseMessage
                {
                    Message = "CreateVersionControlWorkspace failed: " + ex.Message,
                    Meta = FailureEvidence(ex, issued),
                };
            }
        }

        public ResponseStringList GetVersionControlStatus(
            string workspaceName = "",
            bool changedOnly = true)
        {
            try
            {
                var vci = RequireVci();
                var ws = FindWorkspace(vci, workspaceName);

                var lines = new List<string>();
                var entries = new JsonArray();
                int total = 0, differing = 0;
                foreach (var mo in Vci.Enumerate(Keep(Vci.MappedObjects(ws))))
                {
                    total++;
                    string status;
                    // GetStatus() returns an IndividualObjectCompareResult; ToString() on it yields the type
                    // name, not the verdict. The verdict is CompareState (Equal / Unequal / WorkspaceFileMissing).
                    try { status = Vci.State(Vci.ReadStatus(mo)).ToString(); }
                    catch (Exception ex) { status = "Unknown(" + ex.Message + ")"; }
                    bool inSync = string.Equals(status, "Equal", StringComparison.OrdinalIgnoreCase);
                    if (!inSync) differing++;
                    if (changedOnly && inSync) continue;
                    lines.Add(string.Format("{0} | {1} | file={2}{3}",
                        SafeName(mo), status, SafeFile(mo), SafeFormat(mo)));
                    entries.Add(new JsonObject { ["name"] = SafeName(mo), ["compareState"] = status.StartsWith("Unknown") ? "Unknown" : status,
                        ["error"] = status.StartsWith("Unknown(") ? status : null, ["filePath"] = SafeFile(mo), ["fileFormat"] = SafeFormat(mo).Replace(" | format=", "") });
                }

                return new ResponseStringList
                {
                    Message = string.Format(
                        "Workspace '{0}': {1} mapped object(s), {2} differ from the workspace files.{3}",
                        Vci.Name(ws), total, differing,
                        differing == 0
                            ? " Project and workspace are in sync — nothing to commit."
                            : " Call SynchronizeVersionControlWorkspace(direction='ProjectToWorkspace') to write the changes out, then commit."),
                    Items = lines,
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["workspaceName"] = Vci.Name(ws), ["rootPath"] = SafeRoot(ws), ["total"] = total, ["differing"] = differing, ["objects"] = entries },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "GetVersionControlStatus failed: " + ex.Message,
                    Meta = FailureEvidence(ex),
                };
            }
        }

        private static string SafeName(MappedObject mo)
        {
            try { return Vci.FileName(mo) ?? "?"; } catch { /* swallow(probe-optional): An unavailable mapped-object name is represented by the existing question-mark placeholder. */ return "?"; }
        }

        private static string SafeFile(MappedObject mo)
        {
            try
            {
                string d = "";
                try { d = Vci.Directory(mo)?.FullName ?? ""; } catch { /* swallow(probe-optional): An unavailable directory leaves the filename-only fallback intact. */ }
                string f = Vci.FileName(mo) ?? "";
                return string.IsNullOrEmpty(d) ? f : d.TrimEnd('\\', '/') + "\\" + f;
            }
            catch { /* swallow(probe-optional): An unreadable mapped-object path is represented by the existing question-mark placeholder. */ return "?"; }
        }

        private static string SafeFormat(MappedObject mo)
        {
            try { return " | format=" + Vci.Format(mo); } catch { /* swallow(probe-optional): An unavailable mapped-object format omits the optional format suffix. */ return ""; }
        }

        public ResponseStringList SyncVersionControlWorkspace(
            string direction = "ProjectToWorkspace",
            string workspaceName = "",
            bool dryRun = true,
            bool changedOnly = true)
        {
            bool issued = false;
            var lines = new List<string>();
            int ok = 0, failed = 0;
            try
            {
                SynchronizationMode mode;
                string d = (direction ?? "").Trim();
                if (d.Equals("ProjectToWorkspace", StringComparison.OrdinalIgnoreCase)) mode = SynchronizationMode.ProjectToWorkspace;
                else if (d.Equals("WorkspaceToProject", StringComparison.OrdinalIgnoreCase)) mode = SynchronizationMode.WorkspaceToProject;
                else
                    return new ResponseStringList
                    {
                        Message = "direction must be 'ProjectToWorkspace' (export for commit) or " +
                                  "'WorkspaceToProject' (import to restore); got '" + direction + "'.",
                        Meta = ResponseMeta.Basic(DateTime.Now, false, ("v4Rejection", "INVALID_ARGUMENT")),
                    };

                // Exporting (project -> text files) is allowed; importing (text files -> project)
                // overwrites blocks in the engineer's project and is rejected here.
                if (mode == SynchronizationMode.WorkspaceToProject)
                    return new ResponseStringList
                    {
                        Message = "direction='WorkspaceToProject' (restoring a Git version back INTO the project) " +
                                  "overwrites blocks in the open project and is a commercial-tier operation. " +
                                  "Everything else is free — CreateVersionControlWorkspace, ConnectProjectToWorkspace, " +
                                  "GetVersionControlStatus and SynchronizeVersionControlWorkspace(direction='ProjectToWorkspace') " +
                                  "— so you can put the project under version control, see exactly what changed, " +
                                  "export it as text and commit it.",
                        Meta = ResponseMeta.Basic(DateTime.Now, false, ("v4Rejection", "UNSUPPORTED_CAPABILITY")),
                    };

                var vci = RequireVci();
                var ws = FindWorkspace(vci, workspaceName);

                // Openness REFUSES to synchronize a mapping whose compare status is Equal
                // ("Synchronize cannot be called on a workspace mapping that has a compare status of
                // equal"), so "force every object" is not a thing that exists — asking for it just
                // produced one failure per object. Equal is always skipped; changedOnly only decides
                // whether objects whose status could not be determined are attempted anyway.
                var targets = new List<MappedObject>();
                int skippedEqual = 0;
                foreach (var mo in Vci.Enumerate(Keep(Vci.MappedObjects(ws))))
                {
                    string st;
                    try { st = Vci.State(Vci.ReadStatus(mo)).ToString(); } catch { /* swallow(probe-optional): A failed status probe retains Unknown so the existing synchronization selection rules can decide whether to proceed. */ st = "Unknown"; }
                    if (string.Equals(st, "Equal", StringComparison.OrdinalIgnoreCase)) { skippedEqual++; continue; }
                    if (changedOnly && string.Equals(st, "Unknown", StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(mo);
                }

                if (targets.Count == 0)
                    return new ResponseStringList
                    {
                        Message = "Workspace '" + Vci.Name(ws) + "': nothing to synchronize — every mapped object is already in sync.",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["workspaceName"] = Vci.Name(ws), ["rootPath"] = SafeRoot(ws), ["dryRun"] = dryRun, ["synchronized"] = 0, ["failed"] = 0, ["skippedEqual"] = skippedEqual },
                    };

                if (dryRun)
                {
                    foreach (var mo in targets) lines.Add(SafeName(mo) + " | would sync " + mode);
                    return new ResponseStringList
                    {
                        Message = string.Format(
                            "DRY RUN — nothing was written. {0} object(s) would be synchronized {1} in workspace '{2}' " +
                            "(folder {3}). Call again with dryRun=false to do it.",
                            targets.Count, mode, Vci.Name(ws), SafeRoot(ws)),
                        Items = lines,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["workspaceName"] = Vci.Name(ws), ["rootPath"] = SafeRoot(ws), ["dryRun"] = true, ["synchronized"] = 0, ["wouldSynchronize"] = targets.Count, ["failed"] = 0, ["skippedEqual"] = skippedEqual },
                    };
                }

                foreach (var mo in targets)
                {
                    try { issued = true; Vci.Synchronize(mo, mode); ok++; lines.Add(SafeName(mo) + " | synchronized"); }
                    catch (Exception ex) { failed++; lines.Add(SafeName(mo) + " | FAILED: " + ex.Message); }
                }

                return new ResponseStringList
                {
                    Message = string.Format(
                        "Workspace '{0}' ({1}): {2} synchronized, {3} failed, {6} already equal (skipped). Folder: {4}.{5}",
                        Vci.Name(ws), mode, ok, failed, SafeRoot(ws),
                        mode == SynchronizationMode.ProjectToWorkspace
                            ? " The text files are updated — `git add -A && git commit` from that folder."
                            : " The project now holds the workspace's version — compile and save to persist it.",
                        skippedEqual),
                    Items = lines,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = failed == 0, ["workspaceName"] = Vci.Name(ws), ["rootPath"] = SafeRoot(ws), ["dryRun"] = false, ["synchronized"] = ok, ["failed"] = failed, ["skippedEqual"] = skippedEqual },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "SynchronizeVersionControlWorkspace failed: " + ex.Message,
                    Items = lines,
                    Meta = FailureEvidence(ex, issued),
                };
            }
        }

        // ----------------------------------------------------------- whole-project auto mapping

        private sealed class VcNode
        {
            public IEngineeringObject Obj = null!;
            public string Label = "";    // human path in the project tree
            public string RelDir = "";   // folder inside the workspace ("" = workspace root)
            public bool Descendable;     // may we walk into it when VCI cannot map it as a unit?
        }

        private static string Flatten(string s)
            => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string ObjName(IEngineeringObject o)
        {
            try
            {
                var v = Vci.Attribute(o, "Name");
                var text = v?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text!;
            }
            catch { /* swallow(probe-optional): Objects without a readable Name attribute fall back to their runtime type name. */ }
            return o.GetType().Name;
        }

        private static string SanitizePathPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "_";
            var bad = Path.GetInvalidFileNameChars();
            return new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        /// <summary>Pick the most Git-friendly of the formats VCI offers for an object.</summary>
        private static string? PreferredFormat(IList<string> formats)
        {
            return formats.FirstOrDefault(f => f.IndexOf("s7dcl", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault(f => f.IndexOf("simatic", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault(f => f.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault();
        }

        /// <summary>
        /// Typed children of a node. The generic reflection bridge (GetComposition) is deliberately NOT used:
        /// it hands back transient proxies that Openness disposes immediately, so every object reached that way
        /// throws "Access to a disposed object" on first use. Typed compositions stay valid.
        /// </summary>
        private static List<VcNode> TypedChildren(VcNode node)
        {
            var kids = new List<VcNode>();
            string dir = string.IsNullOrEmpty(node.RelDir) ? "" : node.RelDir;

            void Add(IEngineeringObject o, string subDir, bool descendable)
            {
                string nm = ObjName(o);
                kids.Add(new VcNode
                {
                    Obj = o,
                    Label = node.Label + "/" + nm,
                    RelDir = subDir,
                    Descendable = descendable,
                });
            }

            string Under(string name)
            {
                string part = SanitizePathPart(name);
                if (string.IsNullOrEmpty(dir)) return part;
                // Device, its DeviceItem and its PlcSoftware are usually all called "PLC_1" — one level is enough.
                if (string.Equals(Path.GetFileName(dir), part, StringComparison.OrdinalIgnoreCase)) return dir;
                return Path.Combine(dir, part);
            }

            switch (node.Obj)
            {
                case Project proj:
                    foreach (var d in Vci.Enumerate(Vci.Devices(proj))) Add(d, dir, true);
                    foreach (var g in Vci.Enumerate(Vci.DeviceGroups(proj))) Add(g, dir, true);
                    break;

                case DeviceUserGroup dg:
                    foreach (var d in Vci.Enumerate(Vci.Devices(dg))) Add(d, Under(ObjName(dg)), true);
                    foreach (var g in Vci.Enumerate(Vci.DeviceGroups(dg))) Add(g, Under(ObjName(dg)), true);
                    break;

                case Device dev:
                    foreach (var di in Vci.Enumerate(Vci.DeviceItems(dev))) Add(di, Under(ObjName(dev)), true);
                    break;

                case DeviceItem di2:
                    foreach (var sub in Vci.Enumerate(Vci.DeviceItems(di2))) Add(sub, dir, true);
                    var sc = Vci.SoftwareContainer(di2);
                    var sw = Vci.Software(sc) as IEngineeringObject;
                    if (sw != null) Add(sw, dir, true);
                    break;

                case PlcSoftware plc:
                    Add(Vci.BlockGroup(plc), Under(ObjName(plc)), true);
                    Add(Vci.TagTableGroup(plc), Under(ObjName(plc)), true);
                    Add(Vci.TypeGroup(plc), Under(ObjName(plc)), true);
                    break;

                case PlcBlockGroup bg:
                    foreach (var b in Vci.Enumerate(Vci.Blocks(bg))) Add(b, dir, false);
                    foreach (var g in Vci.Enumerate(Vci.BlockGroups(bg))) Add(g, Under(ObjName(bg)), true);
                    break;

                case PlcTagTableGroup tg:
                    foreach (var t in Vci.Enumerate(Vci.TagTables(tg))) Add(t, dir, false);
                    foreach (var g in Vci.Enumerate(Vci.TagTableGroups(tg))) Add(g, Under(ObjName(tg)), true);
                    break;

                case PlcTypeGroup ty:
                    foreach (var t in Vci.Enumerate(Vci.Types(ty))) Add(t, dir, false);
                    foreach (var g in Vci.Enumerate(Vci.TypeGroups(ty))) Add(g, Under(ObjName(ty)), true);
                    break;
            }
            return kids;
        }

        public ResponseStringList ConnectProjectToWorkspace(
            string workspaceName = "",
            bool dryRun = true,
            string deviceFilter = "",
            int maxObjects = 3000,
            bool walkTrace = false)
        {
            bool issued = false;
            var lines = new List<string>();
            try
            {
                var project = VciProject;
                if (project == null) throw new PortalException(PortalErrorCode.InvalidState, "No project is open.");

                var ws = FindWorkspace(RequireVci(), workspaceName);
                string wsName = Vci.Name(ws);
                string wsRootPath = SafeRoot(ws);

                // An Openness call that throws DISPOSES the objects involved: after one
                // "The Object is not supported" the Workspace handle itself is dead. Since asking about an
                // unsupported object is a normal part of the sweep, re-acquire the handle after every failure.
                Workspace ReAcquire()
                {
                    _vciCached = null;
                    _vciOwnerProject = null;
                    _vciKeepAlive.Clear();
                    return FindWorkspace(RequireVci(), wsName);
                }

                int mapped = 0, already = 0, failed = 0, unsupported = 0, visited = 0;
                bool truncated = false;

                var stack = new Stack<VcNode>();
                stack.Push(new VcNode
                {
                    Obj = (IEngineeringObject)project,
                    Label = ObjName((IEngineeringObject)project),
                    RelDir = "",
                    Descendable = true,
                });

                while (stack.Count > 0)
                {
                    if (visited >= maxObjects) { truncated = true; break; }
                    var node = stack.Pop();
                    visited++;

                    if (walkTrace)
                        Console.Error.WriteLine("[VCI-walk] #" + visited + " " + node.Obj.GetType().Name + " :: " + node.Label);

                    if (!string.IsNullOrWhiteSpace(deviceFilter)
                        && node.Obj is Device
                        && !string.Equals(ObjName(node.Obj), deviceFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    IList<string> formats;
                    try
                    {
                        var f = Vci.SupportedFormats(ws, node.Obj);
                        formats = f == null ? new List<string>() : f.ToList();
                    }
                    catch (Exception ex)
                    {
                        formats = new List<string>();
                        if (walkTrace) Console.Error.WriteLine("[VCI-walk]     query threw: " + ex.Message.Split('\n')[0]);
                        ws = ReAcquire();   // the throw killed the handle
                    }

                    if (walkTrace)
                        Console.Error.WriteLine("[VCI-walk]     formats=[" + string.Join(",", formats) + "] descendable=" + node.Descendable);

                    if (formats.Count > 0)
                    {
                        string fmt = PreferredFormat(formats) ?? formats[0];
                        string rel = node.RelDir ?? "";
                        string flatName = SanitizePathPart(
                            (string.IsNullOrEmpty(rel) ? "" : rel.Replace(Path.DirectorySeparatorChar, '_') + "_") + ObjName(node.Obj));

                        MappedObject? existing = null;
                        try { existing = Vci.Find(Vci.MappedObjects(ws), node.Obj); }
                        catch { /* swallow(native-fallback): A failed mapped-object lookup can invalidate its workspace proxy; reacquire it before continuing the existing mapping flow. */ ws = ReAcquire(); }

                        if (existing != null)
                        {
                            already++;
                            lines.Add(node.Label + " | already mapped");
                            continue;
                        }

                        if (dryRun)
                        {
                            mapped++;
                            lines.Add(node.Label + " | would map | format=" + fmt +
                                      " | dir=<root> | file=" + flatName);
                        }
                        else
                        {
                            try
                            {
                                // Use the established root layout once. An exception can follow a partial
                                // export, so never retry the same object with a different target directory.
                                issued = true;
                                Vci.Export(ws, node.Obj, new DirectoryInfo(wsRootPath), flatName, fmt);
                                mapped++;
                                lines.Add(node.Label + " | mapped | format=" + fmt +
                                          " | dir=<root> | file=" + flatName);
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                lines.Add(node.Label + " | FAILED: " + Flatten(ex.Message) +
                                          (ex.InnerException != null ? " || inner: " + Flatten(ex.InnerException.Message) : ""));
                                ws = ReAcquire();
                            }
                        }
                        continue;   // coarse-first: a mapped object owns its children
                    }

                    if (!node.Descendable)
                    {
                        unsupported++;
                        lines.Add(node.Label + " | not supported by VCI (" + node.Obj.GetType().Name + ")");
                        continue;
                    }

                    List<VcNode> kids;
                    try { kids = TypedChildren(node); }
                    catch (Exception ex)
                    {
                        kids = new List<VcNode>();
                        lines.Add(node.Label + " | could not enumerate children: " + ex.Message.Split('\n')[0]);
                    }
                    if (walkTrace) Console.Error.WriteLine("[VCI-walk]     children=" + kids.Count);
                    foreach (var kid in kids) stack.Push(kid);
                }

                string head = dryRun
                    ? string.Format("DRY RUN - nothing was mapped. {0} object(s) would be mapped into workspace '{1}' ({2}); " +
                                    "{3} already mapped, {4} not supported by VCI, {5} tree nodes visited.{6} " +
                                    "Call again with dryRun=false to map them.",
                                    mapped, wsName, wsRootPath, already, unsupported, visited,
                                    truncated ? " ** stopped at maxObjects - raise maxObjects for full coverage **" : "")
                    : string.Format("Workspace '{0}' ({1}): {2} newly mapped, {3} already mapped, {4} failed, " +
                                    "{5} not supported by VCI, {6} tree nodes visited.{7} " +
                                    "Next: SynchronizeVersionControlWorkspace(direction='ProjectToWorkspace', dryRun=false), then git commit. " +
                                    "Save the project to persist the mappings.",
                                    wsName, wsRootPath, mapped, already, failed, unsupported, visited,
                                    truncated ? " ** stopped at maxObjects - raise maxObjects for full coverage **" : "");

                return new ResponseStringList
                {
                    Message = head,
                    Items = lines,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = failed == 0, ["workspaceName"] = wsName, ["rootPath"] = wsRootPath, ["dryRun"] = dryRun, ["visited"] = visited, ["mapped"] = mapped, ["alreadyMapped"] = already, ["unsupported"] = unsupported, ["failed"] = failed, ["truncated"] = truncated },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "ConnectProjectToWorkspace failed: " + ex.Message,
                    Items = lines,
                    Meta = FailureEvidence(ex, issued),
                };
            }
        }

        private static string SafeRoot(Workspace ws)
        {
            try { return Vci.Root(ws)?.FullName ?? "?"; } catch { /* swallow(probe-optional): An unreadable workspace root is represented by the existing question-mark placeholder. */ return "?"; }
        }
    }
}
