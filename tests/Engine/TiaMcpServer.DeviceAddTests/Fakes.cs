namespace Siemens.Engineering
{
    public class ProjectBase {public HW.DeviceComposition Devices { get; }=new();}
}
namespace Siemens.Engineering.HW
{
    public class Device { public string Name="";public object? Parent; }
    public class DeviceComposition : List<Device>
    {
        public object? Owner;public int Calls;public bool ThrowAfterCreate;
        public Device CreateWithItem(string typeIdentifier,string name,string deviceName)
        {
            Calls++;var d=new Device{Name=deviceName,Parent=Owner};Add(d);
            if(ThrowAfterCreate)throw new InvalidOperationException("native failure after addition");return d;
        }
    }
    public class DeviceUserGroup {public string Name="Group";public object? Parent;public DeviceComposition Devices=new();public List<DeviceUserGroup> Groups=new();public DeviceUserGroup(){Devices.Owner=this;} }
    public class DeviceSystemGroup {public object? Parent;public DeviceComposition Devices=new();public DeviceSystemGroup(){Devices.Owner=this;} }
}
namespace TiaMcp.PlcFoundation
{
    public class FakeProject : Siemens.Engineering.ProjectBase
    {
        public FakePath Path=new();
        public List<Siemens.Engineering.HW.DeviceUserGroup> DeviceGroups=new();
        public Siemens.Engineering.HW.DeviceSystemGroup UngroupedDevicesGroup=new();
        public FakeProject(){Devices.Owner=this;UngroupedDevicesGroup.Parent=this;}
    }
    public class FakePath {public string FullName=@"C:\Test\Test.ap19";}
    public class FakePortal {public FakeCatalog HardwareCatalog=new();}
    public class FakeCatalog {public List<PlcHardwareCatalogCandidate> Rows=new();public int Calls;public IEnumerable<Siemens.Engineering.HW.HardwareCatalog.CatalogEntry> Find(string q){Calls++;return Rows.Select(row=>new Siemens.Engineering.HW.HardwareCatalog.CatalogEntry(row));}}
    public class FakeLifecycle {public bool IsLocalSession=true;public int? ProcessId=42;}
    internal static class PlcLifecyclePolicy {internal static void RequireLocalSessionExecution(bool local,bool unused){if(!local)throw new InvalidOperationException("local required");}}
    public sealed partial class PlcFoundationEngine
    {
        public string ReleaseKey="19";public FakeProject BoundProject=new();public FakePortal AttachedPortal=new();public FakeLifecycle lifecycle=new();public bool Bound=true;
        private FakeProject Project()=>Bound?BoundProject:throw new InvalidOperationException("explicit project required");
        private FakePortal Portal()=>AttachedPortal;
        private void RequireHardwareCatalogBinding() { if(!Bound) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Hardware catalog tools require ConnectPortal followed by AttachOpenProject first.","session",false); }
        private void RequireProjectIdentity(string path)=>MutationIdentityPolicy.RequireSameProject(path,Project().Path.FullName);
    }
}
namespace Siemens.Engineering.HW.HardwareCatalog
{
    public sealed class CatalogEntry
    {
        private readonly TiaMcp.PlcFoundation.PlcHardwareCatalogCandidate row;
        public CatalogEntry(TiaMcp.PlcFoundation.PlcHardwareCatalogCandidate row) { this.row=row; }
        public string ArticleNumber => row.ArticleNumber!;
        public string CatalogPath => row.CatalogPath!;
        public string Description => row.Description!;
        public string TypeIdentifier => row.TypeIdentifier!;
        public string TypeIdentifierNormalized => row.TypeIdentifierNormalized!;
        public string TypeName => row.TypeName!;
        public string Version => row.Version!;
    }
}
