using System.Collections;

namespace Siemens.Engineering
{
    public interface IEngineeringObject { }
    public interface IEngineeringServiceProvider { T? GetService<T>() where T : class; }
    public sealed class EngineeringObjectDisposedException : Exception { }
}
namespace Siemens.Engineering.SW.Blocks { public sealed class PlcBlock : Siemens.Engineering.IEngineeringObject { } }
namespace Siemens.Engineering.SW.Types { public sealed class PlcType : Siemens.Engineering.IEngineeringObject { } }
namespace Siemens.Engineering.SW.Tags { public sealed class PlcTagTable : Siemens.Engineering.IEngineeringObject { } }
namespace Siemens.Engineering.VersionControl
{
    public enum SynchronizationMode { ProjectToWorkspace, WorkspaceToProject }
    public enum CompareState { Equal, Unequal, Unknown }
    public sealed class IndividualObjectCompareResult { public CompareState CompareState { get; set; } }
    public sealed class VersionControlInterface { public WorkspaceSystemGroup WorkspaceGroup { get; set; } = new(); }
    public class WorkspaceGroup
    {
        public WorkspaceComposition Workspaces { get; set; } = new();
        public List<WorkspaceUserGroup> Groups { get; set; } = new();
    }
    public sealed class WorkspaceSystemGroup : WorkspaceGroup { }
    public sealed class WorkspaceUserGroup : WorkspaceGroup { }
    public sealed class WorkspaceComposition : List<Workspace>
    {
        internal World World = null!;
        public Workspace Create(string name)
        {
            World.Call("create"); var w = new Workspace { World = World, NameValue = name, RootValue = new DirectoryInfo("C:\\WORKSPACE") }; Add(w);
            World.After("create"); return w;
        }
        public Workspace Create(string name, DirectoryInfo root) { var w = Create(name); w.RootValue = root; return w; }
    }
    public sealed class Workspace
    {
        internal World World = null!;
        internal string NameValue = "reviewed";
        internal DirectoryInfo RootValue = new("C:\\WORKSPACE");
        public string Name { get { World.Read("name"); return NameValue; } }
        public DirectoryInfo RootPath { get { World.Read("root"); return RootValue; } set { World.Call("set-root"); RootValue = value; World.After("set-root"); } }
        public MappedObjectComposition MappedObjects { get; set; } = new();
        public MappingComposition Mappings { get; set; } = new();
    }
    public class MappedObject
    {
        internal World World = null!;
        internal string Id = "one";
        public string RelativeWorkspacePath => Id + ".xml";
        public Siemens.Engineering.IEngineeringObject? Target;
        public CompareState State = CompareState.Unequal;
    }
    public sealed class WorkspaceMapping : MappedObject { }
    public sealed class MappedObjectComposition : List<MappedObject> { }
    public sealed class MappingComposition : List<WorkspaceMapping>
    {
        internal World World = null!;
        public WorkspaceMapping? Find(Siemens.Engineering.IEngineeringObject target) => this.FirstOrDefault(x => ReferenceEquals(x.Target, target));
        public WorkspaceMapping Create(string name, Siemens.Engineering.IEngineeringObject target)
        { World.Call("map-create"); var m = new WorkspaceMapping { World = World, Id = name, Target = target }; Add(m); World.After("map-create"); return m; }
    }
    public sealed class World : Siemens.Engineering.IEngineeringObject, Siemens.Engineering.IEngineeringServiceProvider
    {
        public VersionControlInterface Service = new();
        public readonly List<string> Trace = new();
        public string Fault = "";
        public int Reads;
        public int Services;
        public bool Poisoned;
        public World()
        {
            Service.WorkspaceGroup.Workspaces.World = this;
            var w = new Workspace { World = this }; w.Mappings.World = this;
            w.MappedObjects.Add(new MappedObject { World = this, Id = "one" }); w.MappedObjects.Add(new MappedObject { World = this, Id = "two" });
            w.Mappings.Add(new WorkspaceMapping { World = this, Id = "one" }); w.Mappings.Add(new WorkspaceMapping { World = this, Id = "two" });
            Service.WorkspaceGroup.Workspaces.Add(w);
        }
        public T? GetService<T>() where T : class { Services++; Trace.Add("service"); return Service as T; }
        public void Read(string point)
        {
            if (Fault == "stale-after-write" && Trace.Any(x => x == "sync" || x == "create" || x == "map-create" || x == "export"))
                throw new Siemens.Engineering.EngineeringObjectDisposedException();
            if (Fault == "stale-" + point || Fault == "stale-always")
            { if (++Reads == 1 || Fault == "stale-always") throw new Siemens.Engineering.EngineeringObjectDisposedException(); }
        }
        public void Call(string point) { Trace.Add(point); if (Fault == point + "-before") throw new IOException(point); }
        public void After(string point) { if (Fault == point + "-after") throw new IOException(point); }
    }
}
namespace TiaOpenness.Openness
{
    using Siemens.Engineering;
    using Siemens.Engineering.VersionControl;
    public static class VersionControlPrimitives
    {
        public static VersionControlInterface? Service(IEngineeringServiceProvider p) => p.GetService<VersionControlInterface>();
        public static WorkspaceSystemGroup Group(VersionControlInterface s) => s.WorkspaceGroup;
        public static WorkspaceComposition Workspaces(WorkspaceGroup g) => g.Workspaces;
        public static List<WorkspaceUserGroup> Groups(WorkspaceGroup g) => g.Groups;
        public static IEnumerable<T> Enumerate<T>(IEnumerable<T> values) => values;
        public static string Name(Workspace w) => w.Name;
        public static DirectoryInfo Root(Workspace w) => w.RootPath;
        public static Workspace Create(WorkspaceComposition c, string name) => c.Create(name);
        public static Workspace Create(WorkspaceComposition c, string name, DirectoryInfo root) => c.Create(name, root);
        public static void SetRoot(Workspace w, DirectoryInfo root) => w.RootPath = root;
        public static MappedObjectComposition MappedObjects(Workspace w) { w.World.Read("mappings"); return w.MappedObjects; }
        public static string FileName(MappedObject m) => m.Id;
        public static DirectoryInfo Directory(MappedObject m) => new("C:\\WORKSPACE");
        public static string Format(MappedObject m) => "xml";
        public static CompareState State(IndividualObjectCompareResult result) => result.CompareState;
        public static IndividualObjectCompareResult ReadStatus(MappedObject m)
        { m.World.Call("status"); var result = new IndividualObjectCompareResult { CompareState = m.State }; m.World.After("status"); return result; }
        public static void Synchronize(MappedObject m, SynchronizationMode mode)
        { m.World.Call("sync"); m.State = CompareState.Equal; m.World.After("sync"); }
        public static string[] SupportedFormats(Workspace w, IEngineeringObject target) { w.World.Call("formats"); w.World.After("formats"); return new[] { "xml" }; }
        public static MappedObject? Find(MappedObjectComposition c, IEngineeringObject target) => c.FirstOrDefault(m => ReferenceEquals(m.Target, target));
        public static MappedObject Export(Workspace w, IEngineeringObject target, DirectoryInfo folder, string name, string format)
        { w.World.Call("export"); var m = new MappedObject { World = w.World, Id = name, Target = target }; w.MappedObjects.Add(m); w.World.After("export"); return m; }
    }
}
