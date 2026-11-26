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
    // TIA Portal's Version Control Interface (VCI) is available through Openness starting with V16.
    //
    // A TIA project is a binary blob that Git cannot diff. VCI fixes that: a *workspace* is a
    // plain folder holding one text file per mapped object (.s7dcl / .xml), which IS diffable and
    // commitable. Native Openness supports synchronization in both directions; this adapter exposes
    // ProjectToWorkspace only and refuses WorkspaceToProject, including previews.
    //
    // ExportObject writes new workspace files and creates their mappings (V20+).
    // ConnectObject links a verified existing XML without exporting over it. Older releases use
    // WorkspaceMapping.Create. V16/V17 first export missing XML or verify an existing file,
    // then synchronize the new mapping if needed. V18/V19 synchronize after Create for new files.
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
            => NativeFailureText.Compose(s, null, Array.Empty<string>());

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
            var attempts = new VciExportAttempts();
            int mapped = 0, failed = 0;
            bool MayHaveChanged() => (!dryRun && mapped > 0) || attempts.FilesWritten > 0 || attempts.MappedAfterFailure > 0 || attempts.Unconfirmed > 0;
            var lines = new List<string>();
#if STUDIO_VCI_INITIAL
            int willCompare = 0;
            string? disconnectedFileReason = null;
            var plannedFiles = new HmiArray();
            var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#endif
            int wouldRefuse = 0;
            try
            {
                var project = VciProject;
                if (project == null) throw new PortalException(PortalErrorCode.InvalidState, "No project is open.");

                var ws = FindWorkspace(RequireVci(), workspaceName);
                string wsName = PortVci.Name(ws);
                string wsRootPath = PortVci.Root(ws)?.FullName ?? throw new IOException("The workspace root is unavailable.");
                try { File.GetAttributes(wsRootPath); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { throw new DirectoryNotFoundException("The workspace folder is missing. Restore it or select an existing workspace.", error); }
                var mappingFiles = new MappingFiles();
                foreach (var mapping in PortVci.Enumerate(PortVci.MappedObjects(ws)))
                    mappingFiles.Add(SafeName(mapping), SafeFile(mapping), SafeFormat(mapping));

                // A native refusal can invalidate the workspace handle. Reacquire it for the next probe.
                Workspace ReAcquire() => ReAcquireWorkspace(wsName);

                int already = 0, unsupported = 0, visited = 0;
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

                while (stack.Count > 0 && !attempts.MappingProbeFailed)
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

                    bool queryRefused = false;
                    IList<string> formats = new List<string>();
                    if (!node.Structural)   // the UngroupedDevicesGroup is a folder only: never asked, always walked
                    {
                        var supported = attempts.ReadFormats(() => PortVci.SupportedFormats(ws, node.Obj)?.ToList(),
                            node.Descendable, node.UngroupedDevice, ex =>
                            {
                                queryRefused = true;
                                if (walkTrace) Console.Error.WriteLine("[VCI-walk]     query threw: " + ex.Message.Split('\n')[0]);
                                if (node.UngroupedDevice)
                                    lines.Add(VciWorkspaceLayout.NativeRefusal(node.Label, Flatten(ex.Message)));
                                else lines.Add(node.Label + " | format query refused: " + Flatten(ex.GetBaseException().Message));
                                ws = ReAcquire();
                            });
                        if (supported == null) { Note(node); continue; }
                        formats = supported;
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
                        catch (Exception error)
                        {
                            attempts.QueryRefused(error);
                            lines.Add(node.Label + " | mapping query refused: " + Flatten(error.GetBaseException().Message));
                            Note(node); ws = ReAcquire();
                            continue;
                        }

                        if (existing != null)
                        {
                            already++;
                            Note(node);
                            lines.Add(node.Label + " | already mapped");
                            continue;
                        }

                        Note(node);
                        string targetStem = Path.Combine(wsRootPath, flatName);
#if STUDIO_VCI_INITIAL
                        FileInfo file;
                        bool collision;
                        try
                        {
                            file = VciWorkspaceLayout.LegacyMappingFile(wsRootPath, flatName + ".xml");
                            collision = dryRun && VciExportPolicy.TargetExists(file);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            failed++;
                            lines.Add(node.Label + " | FAILED: " + NativeFailureText.BoundedDiagnostic(error));
                            continue;
                        }
                        if (dryRun) plannedFiles.Add(new HmiObject { ["filePath"] = file.FullName, ["fileExists"] = collision, ["target"] = node.Label });
                        if (dryRun && !plannedPaths.Add(file.FullName))
                        {
                            wouldRefuse++;
                            lines.Add(node.Label + " | duplicate planned target | " + file.FullName);
                            continue;
                        }
                        if (collision)
                        {
                            willCompare++;
                            lines.Add(node.Label + " | existing file will be compared on apply | file=" + file.FullName);
                            if (mappingFiles.ExistingFile(targetStem, fmt) is string oldFile)
                                lines.Add(node.Label + " | reusing matching content would create a duplicate mapping | file=" + oldFile);
                            continue;
                        }
#else
                        bool reuse = false;
                        FileStream? CheckTarget()
                        {
                            var lease = NativeVci.OpenConnectTarget(wsRootPath, flatName, fmt, node.Obj);
                            reuse = lease != null;
                            return lease;
                        }
                        if (dryRun)
                        {
                            try { using var lease = CheckTarget(); }
                            catch (Exception error) when (NativeVci.Ordinary(error))
                            {
                                wouldRefuse++;
                                lines.Add(node.Label + " | would refuse | " + NativeFailureText.BoundedDiagnostic(error));
                                continue;
                            }
                        }
#endif
                        if (dryRun)
                        {
                            if (mappingFiles.ExistingFile(targetStem, fmt) is string duplicate)
                                lines.Add(node.Label + " | would create a duplicate mapping | file=" + duplicate);
                            mapped++;
                            lines.Add(node.Label + " | would map | format=" + fmt +
#if STUDIO_VCI_INITIAL
                                      " | file=" + file.FullName);
#else
                                      " | dir=<root> | file=" + flatName + (reuse ? " | existing XML content verified; would reuse" : ""));
#endif
                        }
                        else
                        {
                            void Connect() => PortVci.Export(ws, node.Obj, VciExportPolicy.RootDirectory(), flatName, fmt);
                            bool Lookup() { ws = ReAcquire(); return PortVci.Find(PortVci.MappedObjects(ws), node.Obj) != null; }
                            void Report(string detail) => lines.Add(node.Label + " | " + detail);
#if STUDIO_VCI_INITIAL
                            bool MatchesExisting()
                            {
                                if (NativeVci.MatchesExisting(wsRootPath, file, node.Obj)) return true;
                                if (mappingFiles.ExistingFile(targetStem, fmt) == null) return false;
                                disconnectedFileReason = "Target file already exists: " + file.Name
                                    + ". After an overwriting import, the old file belongs to the disconnected mapping of the same object. "
                                    + "Compare the file, back it up and remove it from the workspace, then connect again.";
                                throw new IOException(disconnectedFileReason);
                            }
                            bool exported = attempts.ExportFileFirst(file, () => NativeVci.ExportObject(node.Obj, file, ExportOptions.None), Connect,
                                Lookup, Report, MatchesExisting);
#else
                            void MapTarget()
                            {
                                if (reuse) PortVci.ConnectExisting(ws, node.Obj, VciExportPolicy.RootDirectory(), flatName, fmt);
                                else Connect();
                            }
                            bool exported = attempts.ExportToWorkspace(wsRootPath, flatName, MapTarget, Lookup, Report, CheckTarget);
#endif
                            if (exported)
                            {
                                mapped++;
                                mappingFiles.Add(flatName, targetStem, fmt);
                                lines.Add(node.Label + " | mapped | format=" + fmt + " | dir=<root> | file=" + flatName);
                            }
                            else failed++;
                        }
                        continue;   // coarse-first: a mapped object owns its children
                    }

                    if (!node.Descendable)
                    {
                        unsupported++;
                        lines.Add(node.Label + " | not supported by VCI (" + node.Obj.GetType().Name + ")");
                        Note(node);
                        continue;
                    }

                    List<VcNode> kids;
                    try { kids = TypedChildren(node); }
                    catch (Exception ex) when (!(ex is AdapterPreconditionException))   // the ungrouped-folder refusal ends the call
                    {
                        attempts.QueryRefused(ex);
                        queryRefused = true;
                        kids = new List<VcNode>();
                        Note(node);
                        lines.Add(node.Label + " | could not enumerate children: " + Flatten(ex.Message));
                    }
                    if (!node.Structural && !queryRefused && !node.UngroupedDevice)
                    {
                        if (kids.Count == 0) unsupported++;
                        lines.Add(node.Label + (kids.Count == 0 ? " | not supported by VCI" : " | no direct mapping format; inspecting children"));
                    }
                    if (walkTrace) Console.Error.WriteLine("[VCI-walk]     children=" + kids.Count);
                    foreach (var kid in kids) stack.Push(kid);
                }
                if (!truncated && !attempts.MappingProbeFailed)
                    foreach (var device in ungroupedDevices.Where(d => !d.Reported))
                    {
                        unsupported++;
                        lines.Add(VciWorkspaceLayout.NothingMappable(device.Label));
                    }

                mappingFiles.ReportDuplicates(lines);
                unsupported += attempts.UnsupportedContainers;
                int totalFailed = failed + attempts.QueryRefusals;
                bool unsupportedOnly = !dryRun && attempts.Issued == 0 && unsupported > 0 && totalFailed == 0;
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
                                    wsName, wsRootPath, mapped, already, totalFailed, unsupported, visited,
                                    truncated ? " ** stopped at maxObjects - raise maxObjects for full coverage **" : "");

                var result = new VersionControlResult
                {
                    Message = head,
                    Items = lines,
                    Meta = new HmiObject {
                        ["timestamp"] = TiaMcpServer.ModelContextProtocol.ResponseClock.Now, ["success"] = totalFailed == 0 && !unsupportedOnly,
                            ["workspaceName"] = wsName, ["rootPath"] = wsRootPath, ["dryRun"] = dryRun, ["visited"] = visited,
                            ["mapped"] = mapped, ["alreadyMapped"] = already,
                            ["unsupported"] = unsupported, ["failed"] = totalFailed, ["truncated"] = truncated,
                            ["nativeMessage"] = attempts.NativeMessage, ["exportsIssued"] = attempts.Issued,
                            ["queryRefusals"] = attempts.QueryRefusals, ["mappedAfterFailure"] = attempts.MappedAfterFailure,
                            ["unconfirmed"] = attempts.Unconfirmed,
                            ["mayHaveChanged"] = MayHaveChanged(), ["writeOutcomeUnknown"] = attempts.Unconfirmed > 0
                    },
                };
#if STUDIO_VCI_INITIAL
                if (disconnectedFileReason != null) result.Meta["disconnectedFileReason"] = disconnectedFileReason;
                if (dryRun)
                {
                    result.Meta["plannedFiles"] = plannedFiles;
                    result.Meta["willCompare"] = willCompare;
                    if (willCompare > 0) result.Message += " " + willCompare + " existing file(s) will be compared on apply before reuse or refusal.";
                    if (willCompare == 0 || wouldRefuse > 0) result.Meta["wouldRefuse"] = wouldRefuse;
                    if (wouldRefuse > 0) result.Message += " " + wouldRefuse + " duplicate planned target(s).";
                }
#else
                if (dryRun && wouldRefuse > 0)
                {
                    result.Meta["wouldRefuse"] = wouldRefuse;
                    result.Message += " " + wouldRefuse + " existing workspace target(s) would be refused. See the file names and remedies.";
                }
#endif
                if (attempts.MappingProbeFailed && stack.Count > 0)
                {
                    result.Meta["notAttempted"] = stack.Count;
                    result.Meta["notAttemptedKind"] = "walk-nodes";
                    result.Message += " " + stack.Count + " pending walk node(s) were not visited.";
                    var names = stack.Select(node => node.Label).ToArray();
                    result.Meta["notAttemptedNames"] = names;
                    lines.AddRange(names.Select(name => name + " | not-attempted | pending walk node"));
                }
                attempts.AddFileEvidence(result.Meta);
                if (attempts.MappingProbeFailed) result.Meta["dataComplete"] = false;
                return result;
            }
            catch (Exception ex)
            {
                var meta = FailureEvidence(ex, attempts.Issued > 0);
                meta["mayHaveChanged"] = MayHaveChanged();
                attempts.AddFileEvidence(meta);
                meta["unconfirmed"] = attempts.Unconfirmed;
                meta["exportsIssued"] = attempts.Issued; meta["mapped"] = mapped; meta["mappedAfterFailure"] = attempts.MappedAfterFailure;
                meta["queryRefusals"] = attempts.QueryRefusals; meta["failed"] = failed + attempts.QueryRefusals + 1;
                meta["writeOutcomeUnknown"] = attempts.Unconfirmed > 0;
                meta["nativeMessage"] = attempts.NativeMessage ?? NativeFailureText.BoundedDiagnostic(ex);
                return new VersionControlResult
                {
                    Message = "ConnectProjectToWorkspace failed: " + Flatten(ex.Message),
                    Items = lines,
                    Meta = meta,
                };
            }
        }

    }
}

#endif
