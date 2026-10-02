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
    public class FakeProject
    {
        public FakePath Path=new();public Siemens.Engineering.HW.DeviceComposition Devices=new();
        public List<Siemens.Engineering.HW.DeviceUserGroup> DeviceGroups=new();
        public Siemens.Engineering.HW.DeviceSystemGroup UngroupedDevicesGroup=new();
        public FakeProject(){Devices.Owner=this;UngroupedDevicesGroup.Parent=this;}
    }
    public class FakePath {public string FullName=@"C:\Test\Test.ap19";}
    public class FakePortal {public FakeCatalog HardwareCatalog=new();}
    public class FakeCatalog {public List<PlcHardwareCatalogCandidate> Rows=new();public int Calls;public IEnumerable<PlcHardwareCatalogCandidate> Find(string q){Calls++;return Rows;}}
    public class FakeLifecycle {public bool IsLocalSession=true;public int? ProcessId=42;}
    internal static class PlcLifecyclePolicy {internal static void RequireLocalSessionExecution(bool local,bool unused){if(!local)throw new InvalidOperationException("local required");}}
    public sealed partial class PlcFoundationEngine
    {
        public string ReleaseKey="19";public FakeProject BoundProject=new();public FakePortal AttachedPortal=new();public FakeLifecycle lifecycle=new();public bool Bound=true;
        private FakeProject Project()=>Bound?BoundProject:throw new InvalidOperationException("explicit project required");
        private FakePortal Portal()=>AttachedPortal;
        private void RequireProjectIdentity(string path)=>MutationIdentityPolicy.RequireSameProject(path,Project().Path.FullName);
    }
}
