#if TIA_ENGINE_LOCAL_PRIMITIVES
#define STUDIO_VCI
#define STUDIO_VCI_MODERN
#endif
#if STUDIO_VCI
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Contracts.Candidates;
#if !STUDIO_VCI_MODERN
using MappedObject = Siemens.Engineering.VersionControl.WorkspaceMapping;
#endif
#if TIA_ENGINE_LOCAL_PRIMITIVES
using NativeVci = TiaMcpServer.Siemens.LocalVci.VersionControlPrimitives;
#else
using NativeVci = TiaOpenness.Openness.VersionControlPrimitives;
#endif

#if TIA_ENGINE_LOCAL_PRIMITIVES
namespace TiaMcpServer.Siemens.LocalVciFallback
#else
namespace TiaMcp.Adapters.Native.Vci
#endif
{
    public sealed class VciMappingTarget
    {
        public IEngineeringObject Object { get; set; } = null!;
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public IEngineeringObject[] Ancestors { get; set; } = Array.Empty<IEngineeringObject>();
    }
    // Opt-in primitive surface; the host owns plans, permission, envelopes and policy selection.
    // The same source compiles for each VCI-capable release without changing Studio's current path.
    public sealed class VciFallbackAdapter : IFallbackAdapter, IFallbackBoundary
    {
        private readonly Func<SessionState> binding;
        private readonly Func<IEngineeringServiceProvider> project;
        private readonly Action thread;
        private readonly Action uncertain;
        private readonly List<object> keepAlive = new List<object>();
        private readonly List<KeyValuePair<object, string>> ids = new List<KeyValuePair<object, string>>();
        private readonly string scope = Guid.NewGuid().ToString("N");
        private VersionControlInterface? service;
        private object? owner;
        private Dictionary<string, Workspace> workspaces = new Dictionary<string, Workspace>(StringComparer.Ordinal);
        private bool poisoned;
        public Func<VciMappingTarget[]> MappingTargets { private get; set; } = () => Array.Empty<VciMappingTarget>();
        private VciMappingTarget[] mappingTargets = Array.Empty<VciMappingTarget>();
        public FallbackRequest Request { private get; set; }
        public bool Writes => Request.Entry == "CreateVersionControlWorkspace" || Request.Entry == "ConnectProjectToWorkspace" || Request.Entry == "SynchronizeVersionControlWorkspace";
        public bool NeedsConfiguration => false;
        public bool RequiresOffline => false;
        public VciFallbackAdapter(Func<SessionState> binding, Func<IEngineeringServiceProvider> project, Action thread, Action uncertain, FallbackRequest request)
        { this.binding = binding; this.project = project; this.thread = thread; this.uncertain = uncertain; Request = request; }
        private T Keep<T>(T value) where T : class { keepAlive.Add(value); return value; }
        private string Id(object value)
        {
            foreach (var pair in ids) if (object.Equals(pair.Key, value)) return pair.Value;
            if (ids.Count >= 16384) throw new InvalidOperationException("VCI identity budget exceeded.");
            string id = "vci-" + scope + "-" + ids.Count.ToString(CultureInfo.InvariantCulture); ids.Add(new KeyValuePair<object, string>(value, id)); return id;
        }
        private VersionControlInterface Service()
        {
            var p = project();
            if (!object.Equals(owner, p)) { service = null; keepAlive.Clear(); owner = p; }
            return service ??= Keep(NativeVci.Service(p) ?? throw new NotSupportedException("VCI service unavailable."));
        }
        private List<Workspace> All()
        {
            var result = new List<Workspace>(); var pending = new Stack<WorkspaceGroup>(); pending.Push(Keep(NativeVci.Group(Service())));
            int count = 0;
            while (pending.Count > 0)
            {
                if (++count > 16384) throw new InvalidOperationException("VCI inventory budget exceeded.");
                var g = pending.Pop();
#if STUDIO_VCI_MODERN
                foreach (var w in NativeVci.Enumerate(Keep(NativeVci.Workspaces(g)))) result.Add(Keep(w));
                foreach (var child in NativeVci.Enumerate(Keep(NativeVci.Groups(g)))) pending.Push(Keep(child));
#else
                foreach (var w in Keep(NativeVci.Workspaces(g))) result.Add(Keep(w));
                foreach (var child in Keep(NativeVci.Groups(g))) pending.Push(Keep(child));
#endif
            }
            return result;
        }
        private static IEnumerable<MappedObject> Mappings(Workspace w)
        {
#if STUDIO_VCI_MODERN
            var result = new List<MappedObject>(); foreach (var m in NativeVci.Enumerate(NativeVci.MappedObjects(w))) result.Add(m); return result;
#else
            return w.Mappings.ToList();
#endif
        }
        private string MappingId(MappedObject m)
        {
#if STUDIO_VCI_MODERN
            return Id(m) + "|" + NativeVci.FileName(m) + "|" + NativeVci.Directory(m)?.FullName + "|" + NativeVci.Format(m);
#else
            return Id(m) + "|" + m.RelativeWorkspacePath;
#endif
        }
        public FallbackObservation Observe()
        {
            try { return ObserveCore(); }
            catch (EngineeringObjectDisposedException error) { throw new ReadHandleStaleException(true, Request.Entry != "ConnectProjectToWorkspace", error); }
        }
        private FallbackObservation ObserveCore()
        {
            thread(); if (poisoned) CandidatePrimitives.Fail("precondition", "session-reset-required");
            var b = binding(); var rows = All(); var routes = new List<FallbackRoute>(); var inventory = new List<string>();
            var selected = new Dictionary<string, Workspace>(StringComparer.Ordinal);
            foreach (var w in rows)
            {
                string name = NativeVci.Name(w), root = NativeVci.Root(w).FullName, id = Id(w);
                string route = "vci/workspace/" + Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(root);
                if (selected.ContainsKey(route)) throw new InvalidDataException("Ambiguous VCI route.");
                selected.Add(route, w); inventory.Add(id + "|" + name + "|" + root);
                foreach (var m in Mappings(w)) inventory.Add(MappingId(Keep(m)));
                routes.Add(new FallbackRoute { Id = route, TargetId = id, NativeCalls = new[] { Request.Entry == "GetVersionControlStatus" ? "VCI.ReadStatus" : Request.Entry == "SynchronizeVersionControlWorkspace" ? "VCI.Synchronize" : Request.Entry == "ConnectProjectToWorkspace" ? "VCI.Map" : "VCI.ReadWorkspace" } });
            }
            if (Request.Entry == "CreateVersionControlWorkspace" || Request.Entry == "ListVersionControlWorkspaces") routes = new List<FallbackRoute> { new FallbackRoute {
                Id = "vci/project/" + Uri.EscapeDataString(b.ProjectFile!), TargetId = Id(project()), NativeCalls = new[] { Request.Entry == "CreateVersionControlWorkspace" ? "VCI.CreateWorkspace" : "VCI.ReadWorkspace" } } };
            workspaces = selected;
            if (Request.Entry == "ConnectProjectToWorkspace")
            {
                mappingTargets = MappingTargets();
                if (mappingTargets.Length == 0) CandidatePrimitives.Fail("precondition", "reviewed-vci-mapping-targets");
                foreach (var target in mappingTargets) inventory.Add(Id(target.Object) + "|" + target.Name + "|" + target.RelativePath + "|" + string.Join(",", target.Ancestors.Select(Id)));
            }
            return new FallbackObservation { Binding = b, TargetId = Id(project()), Routes = routes.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
                InventoryHash = CandidatePrimitives.ByteHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", inventory.OrderBy(x => x, StringComparer.Ordinal)))) };
        }
        public void BeforeAction() { thread(); if (poisoned) CandidatePrimitives.Fail("precondition", "session-reset-required"); }
        public bool ApplyConfiguration(string route) => throw new NotSupportedException("VCI has no connection configuration step.");
        private static T Read<T>(Func<T> read, FallbackAttempt a)
        {
            try { return read(); }
            catch (EngineeringObjectDisposedException error)
            {
                // This catch surrounds property/navigation reads only, never a native status, map or sync command.
                throw new ReadHandleStaleException(true, !a.OperationIssued && !a.WriteIssued, error);
            }
        }
        public FallbackNativeResult Execute(FallbackRequest r, FallbackAttempt a)
        {
            thread(); var items = new List<Dictionary<string, string>>();
            var native = new FallbackNativeResult { State = "in-progress" }; a.NativeResult = native;
            void Retain() => native.Items = items.ToArray();
            if (r.Entry == "ListVersionControlWorkspaces")
            {
                foreach (var w in Read(All, a)) items.Add(new Dictionary<string, string> { ["name"] = Read(() => NativeVci.Name(w), a), ["rootPath"] = Read(() => NativeVci.Root(w).FullName, a) });
                a.OperationIssued = true;
            }
            else if (r.Entry == "CreateVersionControlWorkspace")
            {
                string name = r.Parameters["workspaceName"], folder = r.Parameters["folderPath"];
                if (name.Length == 0 || !Path.IsPathRooted(folder) || !Directory.Exists(folder)) CandidatePrimitives.Invalid("workspaceName/folderPath");
                if (Read(All, a).Any(w => NativeVci.Name(w) == name)) CandidatePrimitives.Fail("precondition", "workspace-exists");
                var source = Read(() => NativeVci.Workspaces(NativeVci.Group(Service())), a);
                a.OperationIssued = true; a.WriteIssued = true;
#if STUDIO_VCI_INITIAL
                var created = NativeVci.Create(source, name); NativeVci.SetRoot(created, new DirectoryInfo(TiaOpenness.Shared.NativePathSelection.FullPath(folder)));
#else
                var created = NativeVci.Create(source, name, new DirectoryInfo(TiaOpenness.Shared.NativePathSelection.FullPath(folder)));
#endif
                items.Add(new Dictionary<string, string> { ["name"] = NativeVci.Name(created), ["rootPath"] = NativeVci.Root(created).FullName });
            }
            else
            {
                var w = workspaces[r.Route]; var mappings = Read(() => Mappings(w).ToArray(), a);
                if (r.Entry == "ConnectProjectToWorkspace")
                {
                    string root = Read(() => NativeVci.Root(w).FullName, a); var mappedParents = new List<IEngineeringObject>();
                    foreach (var target in mappingTargets)
                    {
                        if (target.Ancestors.Any(p => mappedParents.Any(x => object.Equals(x, p)))) continue;
                        string id = Id(target.Object); var row = new Dictionary<string, string> { ["object"] = id, ["execution"] = "not-executed" };
                        items.Add(row); Retain();
#if STUDIO_VCI_MODERN
                        a.OperationIssued = true; var formats = NativeVci.SupportedFormats(w, target.Object).ToArray();
                        if (formats.Length == 0) { row["state"] = "unsupported"; continue; }
                        var existing = NativeVci.Find(NativeVci.MappedObjects(w), target.Object);
#else
                        if (!(target.Object is Siemens.Engineering.SW.Blocks.PlcBlock || target.Object is Siemens.Engineering.SW.Types.PlcType || target.Object is Siemens.Engineering.SW.Tags.PlcTagTable))
                        { row["state"] = "unsupported"; continue; }
                        var formats = new[] { "xml" }; a.OperationIssued = true; var existing = w.Mappings.Find(target.Object);
#endif
                        if (existing != null) { row["state"] = "already-mapped"; mappedParents.Add(target.Object); continue; }
                        string format = formats.Contains("s7dcl") ? "s7dcl" : formats.Contains("xml") ? "xml" : formats[0];
                        string name = "object_" + CandidatePrimitives.ByteHash(System.Text.Encoding.UTF8.GetBytes(target.RelativePath + "\n" + target.Name));
                        row["execution"] = "unknown"; a.WriteIssued = true;
#if STUDIO_VCI_MODERN
                        var created = NativeVci.Export(w, target.Object, new DirectoryInfo(TiaOpenness.Shared.NativePathSelection.FullPath(root)), name, format);
                        row["mappedObject"] = MappingId(created);
#else
                        var created = w.Mappings.Create(name + ".xml", target.Object);
                        NativeVci.Synchronize(created, SynchronizationMode.ProjectToWorkspace); row["mappedObject"] = MappingId(created);
#endif
                        row["execution"] = "completed"; row["format"] = format; mappedParents.Add(target.Object); Retain();
                    }
                    a.OperationIssued = true; native.Success = true; native.State = "completed"; Retain(); return native;
                }
                foreach (var m in mappings)
                {
                    string identity = Read(() => MappingId(m), a);
                    var row = new Dictionary<string, string> { ["object"] = identity, ["execution"] = "not-executed" }; items.Add(row); Retain();
                    a.OperationIssued = true; var state = NativeVci.State(NativeVci.ReadStatus(m)).ToString(); row["stateBefore"] = state;
                    if (r.Entry == "GetVersionControlStatus")
                    {
                        row["state"] = state; row["execution"] = "read-only";
                        if (r.Parameters["changedOnly"] == "true" && state == "Equal") items.Remove(row);
                    }
                    else
                    {
                        string direction = r.Parameters["direction"];
                        if (direction != "ProjectToWorkspace" && direction != "WorkspaceToProject") CandidatePrimitives.Invalid("direction");
                        if (state == "Equal") continue;
                        // A failed/unknown status never qualifies a write. Native compare states remain data.
                        if (state == "Unknown") throw new InvalidDataException("Unknown VCI compare state.");
                        a.WriteIssued = true; row["execution"] = "unknown"; Retain();
                        NativeVci.Synchronize(m, direction == "ProjectToWorkspace" ? SynchronizationMode.ProjectToWorkspace : SynchronizationMode.WorkspaceToProject);
                        row["stateAfter"] = NativeVci.State(NativeVci.ReadStatus(m)).ToString(); row["execution"] = "completed";
                    }
                    Retain();
                }
                // A known empty inventory is a completed read, never evidence of an issued write.
                a.OperationIssued = true;
            }
            native.Success = true; native.State = "completed"; Retain(); return native;
        }
        public void RefreshReadHandle() { thread(); service = null; keepAlive.Clear(); Service(); }
        public void MarkUncertain() { poisoned = true; uncertain(); }
        public FallbackAttempt Execute(FallbackCheck check) => CandidateExecution.Fallback(this, check);
    }
}
#endif
