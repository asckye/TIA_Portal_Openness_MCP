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
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
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
    // ConnectObject is for existing files and is not exposed by these tools. Older releases use
    // WorkspaceMapping.Create followed by ProjectToWorkspace synchronization.
    internal sealed partial class VersionControlAdapter
    {
        // ----------------------------------------------------------- whole-project auto mapping

        private sealed class VcNode
        {
            public IEngineeringObject Obj = null!;
            public string Label = "";    // human path in the project tree
            public string RelDir = "";   // folder inside the workspace ("" = workspace root)
            public bool Descendable;     // may we walk into it when VCI cannot map it as a unit?
            public bool Structural;      // the UngroupedDevicesGroup: a folder only, never asked or mapped
            public bool UngroupedDevice; // a device of the UngroupedDevicesGroup: a VCI refusal is a failure
            public VcNode? UngroupedRoot; // the ungrouped device this node belongs to
            public bool Reported;        // on an ungrouped device: a line was written for it or below it
        }

        private static string Flatten(string s)
            => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string ObjName(IEngineeringObject o)
        {
            try
            {
                var v = PortVci.Attribute(o, "Name");
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
            return TiaMcp.Adapters.Native.Vci.VciExportPolicy.PreferredFormat(formats);
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
                    UngroupedDevice = node.Structural && o is Device,
                    UngroupedRoot = node.UngroupedDevice ? node : node.UngroupedRoot,
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
                    foreach (var d in PortVci.Enumerate(PortVci.Devices(proj))) Add(d, dir, true);
                    foreach (var g in PortVci.Enumerate(PortVci.DeviceGroups(proj))) Add(g, dir, true);
                    // UngroupedDevicesGroup.Devices go under the fixed folder VciWorkspaceLayout.UngroupedFolder, because
                    // the group's Name is localized. A root device or group that already owns that folder is refused.
                    var ungrouped = PortVci.UngroupedDevicesGroup(proj);
                    var ungroupedDevices = new List<Device>();
                    if (ungrouped != null) foreach (var d in PortVci.Enumerate(PortVci.Devices(ungrouped))) ungroupedDevices.Add(d);
                    if (ungroupedDevices.Count > 0)
                    {
                        var owner = VciWorkspaceLayout.UngroupedFolderOwner(kids.Select(k => (ObjName(k.Obj), SanitizePathPart(ObjName(k.Obj)))));
                        if (owner != null) throw VciWorkspaceLayout.UngroupedFolderTaken(owner);
                        kids.Add(new VcNode
                        {
                            Obj = ungrouped!,
                            Label = node.Label + "/" + VciWorkspaceLayout.UngroupedFolder,
                            RelDir = string.IsNullOrEmpty(dir) ? VciWorkspaceLayout.UngroupedFolder : Path.Combine(dir, VciWorkspaceLayout.UngroupedFolder),
                            Descendable = true,
                            Structural = true,
                        });
                    }
                    break;

                case DeviceSystemGroup system:
                    foreach (var d in PortVci.Enumerate(PortVci.Devices(system))) Add(d, dir, true);
                    break;

                case DeviceUserGroup dg:
                    foreach (var d in PortVci.Enumerate(PortVci.Devices(dg))) Add(d, Under(ObjName(dg)), true);
                    foreach (var g in PortVci.Enumerate(PortVci.DeviceGroups(dg))) Add(g, Under(ObjName(dg)), true);
                    break;

                case Device dev:
                    foreach (var di in PortVci.Enumerate(PortVci.DeviceItems(dev))) Add(di, Under(ObjName(dev)), true);
                    break;

                case DeviceItem di2:
                    foreach (var sub in PortVci.Enumerate(PortVci.DeviceItems(di2))) Add(sub, dir, true);
                    var sc = PortVci.SoftwareContainer(di2);
                    var sw = PortVci.Software(sc) as IEngineeringObject;
                    if (sw != null) Add(sw, dir, true);
                    break;

                case PlcSoftware plc:
                    Add(PortVci.BlockGroup(plc), Under(ObjName(plc)), true);
                    Add(PortVci.TagTableGroup(plc), Under(ObjName(plc)), true);
                    Add(PortVci.TypeGroup(plc), Under(ObjName(plc)), true);
                    break;

                case PlcBlockGroup bg:
                    foreach (var b in PortVci.Enumerate(PortVci.Blocks(bg))) Add(b, dir, false);
                    foreach (var g in PortVci.Enumerate(PortVci.BlockGroups(bg))) Add(g, Under(ObjName(bg)), true);
                    break;

                case PlcTagTableGroup tg:
                    foreach (var t in PortVci.Enumerate(PortVci.TagTables(tg))) Add(t, dir, false);
                    foreach (var g in PortVci.Enumerate(PortVci.TagTableGroups(tg))) Add(g, Under(ObjName(tg)), true);
                    break;

                case PlcTypeGroup ty:
                    foreach (var t in PortVci.Enumerate(PortVci.Types(ty))) Add(t, dir, false);
                    foreach (var g in PortVci.Enumerate(PortVci.TypeGroups(ty))) Add(g, Under(ObjName(ty)), true);
                    break;
            }
            return kids;
        }

        public VersionControlResult ConnectProjectToWorkspace(
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
                string wsName = PortVci.Name(ws);
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
                var ungroupedDevices = new List<VcNode>();
                void Note(VcNode reported) {
                    var device = reported.UngroupedDevice ? reported : reported.UngroupedRoot; if (device != null) device.Reported = true; }

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
                    if (node.UngroupedDevice) ungroupedDevices.Add(node);

                    IList<string> formats = new List<string>();
                    if (!node.Structural)   // the UngroupedDevicesGroup is a folder only: never asked, always walked
                    {
                        try
                        {
                            var f = PortVci.SupportedFormats(ws, node.Obj);
                            formats = f == null ? new List<string>() : f.ToList();
                        }
                        catch (Exception ex)
                        {
                            formats = new List<string>();
                            if (walkTrace) Console.Error.WriteLine("[VCI-walk]     query threw: " + ex.Message.Split('\n')[0]);
                            ws = ReAcquire();   // the throw killed the handle
                            if (node.UngroupedDevice)
                            {
                                // A native refusal of an ungrouped device is reported as a failure, not walked past.
                                failed++;
                                Note(node);
                                lines.Add(VciWorkspaceLayout.NativeRefusal(node.Label, Flatten(ex.Message)));
                                continue;
                            }
                        }
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
                        try { existing = PortVci.Find(PortVci.MappedObjects(ws), node.Obj); }
                        catch { /* swallow(native-fallback): A failed mapped-object lookup can invalidate its workspace proxy; reacquire it before continuing the existing mapping flow. */ ws = ReAcquire(); }

                        if (existing != null)
                        {
                            already++;
                            Note(node);
                            lines.Add(node.Label + " | already mapped");
                            continue;
                        }

                        Note(node);
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
                                PortVci.Export(ws, node.Obj, TiaMcp.Adapters.Native.Vci.VciExportPolicy.RootDirectory(), flatName, fmt);
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
                        Note(node);
                        lines.Add(node.Label + " | not supported by VCI (" + node.Obj.GetType().Name + ")");
                        continue;
                    }

                    List<VcNode> kids;
                    try { kids = TypedChildren(node); }
                    catch (Exception ex) when (!(ex is AdapterPreconditionException))   // the ungrouped-folder refusal ends the call
                    {
                        kids = new List<VcNode>();
                        Note(node);
                        lines.Add(node.Label + " | could not enumerate children: " + ex.Message.Split('\n')[0]);
                    }
                    if (walkTrace) Console.Error.WriteLine("[VCI-walk]     children=" + kids.Count);
                    foreach (var kid in kids) stack.Push(kid);
                }
                if (!truncated)
                    foreach (var device in ungroupedDevices.Where(d => !d.Reported))
                    {
                        unsupported++;
                        lines.Add(VciWorkspaceLayout.NothingMappable(device.Label));
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

                return new VersionControlResult
                {
                    Message = head,
                    Items = lines,
                    Meta = new HmiObject {
                        ["timestamp"] = TiaMcpServer.ModelContextProtocol.ResponseClock.Now, ["success"] = failed == 0, ["workspaceName"] = wsName,
                            ["rootPath"] = wsRootPath, ["dryRun"] = dryRun, ["visited"] = visited, ["mapped"] = mapped, ["alreadyMapped"] = already,
                            ["unsupported"] = unsupported, ["failed"] = failed, ["truncated"] = truncated
                    },
                };
            }
            catch (Exception ex)
            {
                return new VersionControlResult
                {
                    Message = "ConnectProjectToWorkspace failed: " + ex.Message,
                    Items = lines,
                    Meta = FailureEvidence(ex, issued),
                };
            }
        }

    }
}

#endif
