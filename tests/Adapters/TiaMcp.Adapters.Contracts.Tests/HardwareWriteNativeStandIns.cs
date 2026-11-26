using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

// Only native objects and peripheral lookups are faked. Step bodies, metadata,
// exception mapping, property conversion and failure readbacks are production code.
namespace Siemens.Engineering
{
    public sealed class EngineeringAttributeInfo
    {
        public string Name { get; set; } = "";
        public string AccessMode { get; set; } = "";
    }
    public interface IEngineeringService { }
    public sealed class EngineeringServiceInfo { public Type Type { get; set; } = typeof(object); }
    public interface IEngineeringServiceProvider
    {
        public T? GetService<T>() where T : class, IEngineeringService;
        public IEnumerable<EngineeringServiceInfo> GetServiceInfos() => Array.Empty<EngineeringServiceInfo>();
    }
    public enum ArchiveMode { Compressed }
    public sealed partial class FakeProjects : List<Project>
    {
        public Project Retrieve(System.IO.FileInfo file, System.IO.DirectoryInfo directory, object? login = null) =>
            throw new NotSupportedException("Archive retrieval is not configured in this fixture.");
        public Project RetrieveWithUpgrade(System.IO.FileInfo file, System.IO.DirectoryInfo directory, object? login = null) => Retrieve(file, directory,
            login);
    }
    public sealed partial class Project : IEngineeringServiceProvider
    {
        public bool IsModified { get; set; }
        public Exception? ArchiveFailure { get; set; }
        public int ArchiveCalls { get; private set; }
        public void SaveAs(System.IO.DirectoryInfo directory) => Path = new System.IO.FileInfo(System.IO.Path.Combine(directory.FullName, Name + ".ap21"));
        public void Archive(System.IO.DirectoryInfo directory, string name, ArchiveMode mode)
        {
            ArchiveCalls++;
            using (var zip = System.IO.Compression.ZipFile.Open(System.IO.Path.Combine(directory.FullName, name), System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new System.IO.StreamWriter(zip.CreateEntry("partial-project.txt").Open())) writer.Write("Synthetic partial archive.");
            if (ArchiveFailure != null) throw ArchiveFailure;
        }
        private readonly List<HW.Device> devices = new();
        internal Exception? DeviceReadFailure;
        public List<HW.Device> Devices => ReadDevices();
        private List<HW.Device> ReadDevices() => DeviceReadFailure == null ? devices : throw DeviceReadFailure;
        public List<HW.DeviceUserGroup> DeviceGroups { get; } = new();
        public HW.DeviceGroup UngroupedDevicesGroup { get; } = new();
        public HW.SubnetComposition Subnets { get; } = new();
        public HW.Utilities.HardwareUtilityComposition HwUtilities { get; } = new();
        public Dictionary<Type, object> Services { get; } = new();
        public T? GetService<T>() where T : class, IEngineeringService => Services.TryGetValue(typeof(T), out var value) ? (T)value : null;
    }
}
namespace Siemens.Engineering.HW
{
    public class HardwareObject : IEngineeringObject, IEngineeringInstance, IEngineeringServiceProvider
    {
        public List<DeviceItem> DeviceItems { get; } = new();
        public List<DeviceItem> Items { get; } = new();
        public List<HwIdentifier> HwIdentifiers { get; } = new();
        public string Name { get; set; } = "";
        public object? Parent { get; set; }
        public Dictionary<Type, object> Services { get; } = new();
        internal List<EngineeringServiceInfo> AdvertisedServices { get; } = new();
        public IEnumerable<EngineeringServiceInfo> GetServiceInfos() => AdvertisedServices;
        internal Dictionary<Type, Exception> ServiceFailures { get; } = new();
        public List<string> Calls { get; } = new();
        public Exception? AttributeFailure { get; set; }
        internal Action? Deleting;
        internal int DeleteCalls;
        public virtual void Delete() { DeleteCalls++; Deleting?.Invoke(); }
        public bool CanPlugMove(DeviceItem item, int position) => throw new NotSupportedException();
        public bool CanPlugCopy(DeviceItem item, int position) => throw new NotSupportedException();
        public DeviceItem PlugMove(DeviceItem item, int position) => throw new NotSupportedException();
        public DeviceItem PlugCopy(DeviceItem item, int position) => throw new NotSupportedException();
        public T? GetService<T>() where T : class, IEngineeringService => ServiceFailures.TryGetValue(typeof(T), out var failure) ? throw failure
            : Services.TryGetValue(typeof(T), out var value) ? (T)value : null;
        public List<EngineeringAttributeInfo> AttributeInfos { get; } = new();
        public virtual IEnumerable<EngineeringAttributeInfo> GetAttributeInfos() => AttributeInfos;
        public virtual object GetAttribute(string name) { Calls.Add("get:" + name); return false; }
        public virtual void SetAttribute(string name, object? value) { Calls.Add("set:" + name); if (AttributeFailure != null) throw AttributeFailure; }
    }
    public class NativeNamedList<T> : List<T> where T : class
    {
        public Func<string, T>? CreateFactory { get; set; }
        public T Create(string name) { var item = CreateFactory!(name); Add(item); return item; }
        public T? Find(string name) => this.FirstOrDefault(value => value.GetType().GetProperty("Name")?.GetValue(value)?.ToString() == name);
    }
    public sealed class Node : HardwareObject
    {
        private Subnet? connected;
        public Action<Subnet>? Attached { get; set; }
        public bool FailWrite { get; set; }
        public bool FailUndo { get; set; }
        private bool lost;
        public Subnet? ConnectedSubnet { get { if (lost) throw new ObjectDisposedException("portal"); return connected; } set => connected = value; }
        public Subnet CreateAndConnectToSubnet(string name)
        {
            var subnet = new Subnet { Name = name };
            Attached?.Invoke(subnet);
            ConnectToSubnet(subnet);
            return subnet;
        }
        public void ConnectToSubnet(Subnet subnet)
        {
            connected = subnet;
            if (FailWrite) { lost = true; throw new ObjectDisposedException("portal"); }
            if (FailUndo) throw new InvalidOperationException("Write failed after attaching.");
        }
        public void DisconnectFromSubnet()
        {
            if (FailUndo) throw new ObjectDisposedException("portal");
            connected = null;
        }
    }
    public class DeviceGroup : HardwareObject { public List<Device> Devices { get; } = new(); }
    public sealed class DeviceUserGroup : DeviceGroup { public List<DeviceUserGroup> Groups { get; } = new(); }
    public sealed class Subnet : HardwareObject
    {
        public string TypeIdentifier { get; set; } = "System:Subnet.Ethernet";
        public string NetType { get; set; } = "Ethernet";
        public List<Node> Nodes { get; } = new();
        public List<IoSystem> IoSystems { get; } = new();
    }
    public sealed class SubnetComposition : List<Subnet>
    {
        public int CreateCalls { get; private set; }
        public Action<Subnet>? Created { get; set; }
        public Subnet Create(string typeIdentifier, string name)
        {
            CreateCalls++;
            var subnet = new Subnet { Name = name, TypeIdentifier = typeIdentifier,
                NetType = typeIdentifier == "System:Subnet.Profibus" ? "Profibus" : "Ethernet" };
            subnet.Deleting = () => Remove(subnet);
            Add(subnet);
            Created?.Invoke(subnet);
            return subnet;
        }
    }
    public sealed class IoSystem : HardwareObject
    {
        public int Number { get; set; }
        public Subnet? Subnet { get; set; }
        public List<object> ConnectedIoDevices { get; } = new();
        public override void Delete() { }
    }
    public sealed class IoController : HardwareObject
    {
        public IoSystem? IoSystem { get; set; }
        public List<Address> Addresses { get; } = new();
        public IoSystem CreateIoSystem(string name) => IoSystem = new() { Name = name };
    }
    public sealed class IoConnector : HardwareObject
    {
        public IoSystem? ConnectedToIoSystem { get; set; }
        public IoController? GetIoController() => null;
        public void ConnectToIoSystem(IoSystem system) => ConnectedToIoSystem = system;
        public void DisconnectFromIoSystem() => ConnectedToIoSystem = null;
    }
    public enum AddressIoType { Input, Output, None }
    public enum ChannelType { Digital, Analog }
    public enum ChannelIoType { Input, Output }
    public enum AttributeAccessMode { ReadWrite, Read }
    public sealed class AttributeInfo
    {
        public string Name { get; set; } = "ChannelAddress";
        public AttributeAccessMode AccessMode { get; init; } = AttributeAccessMode.ReadWrite;
    }
    public sealed class Channel : HardwareObject
    {
        public int Number { get; set; }
        public ChannelType Type { get; set; }
        public ChannelIoType IoType { get; set; }
        public AttributeInfo AddressInfo { get; init; } = new();
        public IEnumerable<AttributeInfo> GetAttributeInfos() => new[] { AddressInfo };
    }
    public sealed class ChannelComposition : List<Channel>
    {
        public Channel? Find(ChannelType type, ChannelIoType ioType, int number) => this.FirstOrDefault(c => c.Type == type && c.IoType == ioType &&
            c.Number == number);
    }
    public sealed class AddressController : IEngineeringService { public List<Address> RegisteredAddresses { get; } = new(); }
    public sealed class HwIdentifierController : IEngineeringService { public List<HwIdentifier> RegisteredHwIdentifiers { get; } = new(); }
    public sealed class NetworkPort : HardwareObject, IEngineeringService
    {
        public HardwareObject? OwnedBy { get; set; }
        public Features.NetworkInterface? Interface { get; set; }
        public List<NetworkPort> ConnectedPorts { get; } = new();
        internal Action<NetworkPort>? Connecting;
        public void ConnectToPort(NetworkPort port)
        {
            if (Connecting == null) ConnectedPorts.Add(port);
            else Connecting(port);
        }
        public void DisconnectFromPort(NetworkPort port) => ConnectedPorts.Remove(port);
    }
}
namespace Siemens.Engineering.HW.Utilities
{
    public class HardwareUtility { public string Identifier { get; set; } = ""; }
    public sealed class HardwareUtilityComposition : List<HardwareUtility>
    {
        public HardwareUtility? Find(string identifier) => this.FirstOrDefault(item => item.Identifier == identifier);
    }
    public sealed class ModuleInformationProvider : HardwareUtility
    {
        public string[] FindModuleTypes(string typeIdentifier) => throw new NotSupportedException();
        public string[] FindContainerTypes(string typeIdentifier) => throw new NotSupportedException();
    }
    public sealed class OpcUaExportProvider : HardwareUtility
    {
        public Action<DeviceItem, System.IO.FileInfo> OnExport { get; set; } = (_, _) => throw new NotSupportedException();
        public void Export(DeviceItem item, System.IO.FileInfo file) => OnExport(item, file);
    }
}
namespace Siemens.Engineering.HW.Features
{
    public sealed class NetworkInterface : IEngineeringService
    {
        internal bool FailScalarRead;
        public List<Siemens.Engineering.HW.IoConnector> IoConnectors { get; } = new();
        public List<Siemens.Engineering.HW.IoController> IoControllers { get; } = new();
        public List<object> Nodes { get; } = new();
        public string InterfaceType => FailScalarRead ? throw new InvalidOperationException("Interface type read failed.") : "Ethernet";
        public string InterfaceOperatingMode => "IoController";
    }
    public sealed class WebserverUserManagement : IEngineeringService
    {
        internal Func<HW.WebserverUserComposition> ReadUsers = () => new();
        public HW.WebserverUserComposition WebserverUsers => ReadUsers();
    }
    public sealed class OpcUaUserManagement : IEngineeringService
    {
        public HW.OpcUaUserComposition OpcUaUsers { get; } = new();
    }
    public sealed class SysLogConfigurationManager : IEngineeringService
    {
        private bool enabled;
        private Siemens.Engineering.HW.SysLogTransportProtocolType protocol;
        public HW.HardwareObject OwnedBy { get; set; } = new();
        public Exception? SetterFailure { get; set; }
        public string? FailingProperty { get; set; }
        public List<string> Calls { get; } = new();
        public bool EnableSystemLogging { get => enabled; set { Calls.Add("EnableSystemLogging"); enabled = value; if (SetterFailure != null &&
            (FailingProperty == null || FailingProperty == "EnableSystemLogging")) throw SetterFailure; } }
        public HW.SysLogTransportProtocolType TransportProtocol { get => protocol; set { Calls.Add("TransportProtocol"); protocol = value;
            if (SetterFailure != null && (FailingProperty == null || FailingProperty == "TransportProtocol")) throw SetterFailure; } }
        public SysLogServerComposition SysLogServerConfiguration { get; } = new();
    }
    public sealed class SysLogServerConfiguration { public string SysLogServerAddress { get; set; } = ""; public ushort SysLogServerPort { get; set; }
        public void Delete() { } }
    public sealed class SysLogServerComposition : List<SysLogServerConfiguration>
    {
        public SysLogServerConfiguration? Find(string address) => this.FirstOrDefault(s => s.SysLogServerAddress == address);
        public Exception? CreateFailure { get; set; }
        public int CreateCalls { get; private set; }
        public SysLogServerConfiguration Create(string address, ushort port) {
            CreateCalls++;
            if (CreateFailure != null) throw CreateFailure;
            var server = new SysLogServerConfiguration { SysLogServerAddress = address,
            SysLogServerPort = port }; Add(server); return server; }
    }
}
namespace Siemens.Engineering.SW.Tags { public sealed class PlcTag { public string Name { get; set; } = ""; } }
namespace Siemens.Engineering.SW.Blocks
{
    public sealed class OB { public string Name { get; set; } = ""; }
}
namespace Siemens.Engineering.Cax
{
    public enum CaxImportOptions { RetainTiaDevice }
    public sealed class CaxProvider : IEngineeringService
    {
        public int ImportCalls { get; private set; }
        public Action<System.IO.FileInfo>? WriteLog { get; set; }
        public bool Import(System.IO.FileInfo source, System.IO.FileInfo log, CaxImportOptions option)
        { ImportCalls++; WriteLog?.Invoke(log); return false; }
        public Func<System.IO.FileInfo, bool> OnExport { get; set; } = _ => false;
        public bool Export(HW.Device device, System.IO.FileInfo destination, System.IO.FileInfo log) => OnExport(destination);
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private sealed class UmacLogin : IDisposable
        {
            internal bool Given => false;
            internal object? Delegate => null;
            internal IDisposable Authenticate(Siemens.Engineering.TiaPortal connection) => this;
            public void Dispose() { }
        }
        private UmacLogin UmacCredentialsFor(string name, string password, string type, string parameter) => new();
        private bool HardwareProjectMissing() => EngineeringProjectMissing?.Invoke() ?? project == null;
        internal Exception? DeviceInventoryFailure
        {
            get => project!.DeviceReadFailure;
            set => project!.DeviceReadFailure = value;
        }
        private IEnumerable<Siemens.Engineering.HW.Device> EnumerateAllDevices() => DeviceInventoryFailure != null
            ? throw DeviceInventoryFailure : project!.Devices;
        private sealed class EngineeringAmbientAccess : IDisposable { public void Dispose() { } }
        private Siemens.Engineering.TiaPortal Portal() => portal!;
        internal readonly List<(object Node, string Path)> NetworkNodes = new();
        private IEnumerable<(object Node, string Path)> FindNetworkNodes(Siemens.Engineering.HW.DeviceItem item) => NetworkNodes;
        private object? TryGetPropertyValue(object value, string name) => null;
        private string? TryGetName(object? value) => null;
        private static object? TryGetEngineeringAttribute(object owner, string attributeName)
            => ((Siemens.Engineering.HW.DeviceItem)owner).AttributeReader?.Invoke(attributeName);
        private string? ReadNodeAddress(object value) => null;
        private bool IsIndustrialEthernetNode(object value) => ReferenceEquals(value, RollbackNode);
        private Siemens.Engineering.SW.PlcSoftware? ResolvePlc(string name) => null;
        internal Siemens.Engineering.HW.Device ServiceDevice { get; } = new() { Name = "Station" };
        internal Siemens.Engineering.HW.DeviceItem ServiceCpu { get; } = new() { Name = "PLC_1" };
        private (Siemens.Engineering.SW.PlcSoftware Value, string ExactPath, string Device, object Context, object DeviceContext) ReadSelection(string name)
            => (FamilyPlc, name, ServiceDevice.Name, ServiceCpu, ServiceDevice);

        internal T? ResolveRequiredService<T>(string path) where T : class, Siemens.Engineering.IEngineeringService
            => new OrganisationSession(this).ResolvePlcService<T>(path, FamilyPlc, required: true);
        internal Siemens.Engineering.HW.Device? SecurityLegacyDevice(string path) => null;
        internal static IEnumerable<(Siemens.Engineering.HW.DeviceItem, string)> HardwareTraverseDeviceItems(
            Siemens.Engineering.HW.DeviceItem item, string path)
        {
            yield return (item, path);
            foreach (var child in item.DeviceItems)
                foreach (var nested in HardwareTraverseDeviceItems(child, path + "/" + child.Name)) yield return nested;
        }
        internal Siemens.Engineering.Project? SecurityProject => project;
        internal IDisposable SecurityEditAccess() => HardwareEditAccess();
        internal T SecurityHardwareService<T>(Siemens.Engineering.HW.HardwareObject owner, string parameter) where T : class,
            Siemens.Engineering.IEngineeringService => owner.GetService<T>() ?? throw new NotSupportedException("The fake native service is not configured.");
        internal object? SecurityFindOnFresh(Func<object> composition, string name, TiaMcp.Adapters.Native.Hmi.HmiObject meta, string phase) =>
            HardwareGroupOperations.Find(composition(), name);
        internal int? SecurityCountOnFresh(Func<object> composition, TiaMcp.Adapters.Native.Hmi.HmiObject meta, string key) =>
            HardwareGroupOperations.Items(composition()).Count();
        internal SecurityPlcSession PlcB5Session => new(this);
        internal sealed class SecurityPlcSession
        {
            private readonly PlcFoundationEngine engine;
            internal SecurityPlcSession(PlcFoundationEngine engine) => this.engine = engine;
            internal bool IsProjectNull() => engine.HardwareProjectMissing();
            internal Siemens.Engineering.HW.HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath) =>
                engine.ExactHardware(devicePath, itemPath);
            internal Siemens.Engineering.SW.PlcSoftware ExactPlcForEngineering(string path, bool writing) => engine.FamilyPlc;
        }
    }
}
namespace TiaMcp.Adapters.Native.Security
{
    internal static class ProjectSecurityPrimitives
    {
        internal static object? FindOrdinal(object collection, string name, string kind) => null;
    }

}

// Managed native-boundary stand-ins: the production mutation and readback code is linked above.
namespace Siemens.Engineering
{
    public interface IEngineeringObject
    {
        public object GetAttribute(string name);
        public void SetAttribute(string name, object? value);
    }
    public interface IEngineeringInstance { public object? Parent { get; } }
}
namespace Siemens.Engineering.HW
{
    [Flags] public enum WebserverUserPermissions { None = 0, ReadTag = 1, ReadFiles = 2 }
    public sealed class WebserverUser
    {
        public string UserName { get; set; } = "";
        internal WebserverUserPermissions Bits;
        internal Action<WebserverUserPermissions>? Set;
        public WebserverUserPermissions Permissions { get => Bits; set { if (Set == null) Bits = value; else Set(value); } }
    }
    public sealed class WebserverUserComposition : List<WebserverUser>
    {
        internal Action? Creating;
        public WebserverUser Create(string name, WebserverUserPermissions permissions, System.Security.SecureString password)
        {
            Creating?.Invoke();
            var user = new WebserverUser { UserName = name, Permissions = permissions };
            Add(user);
            return user;
        }
    }
    public sealed class OpcUaUserComposition : List<object>
    {
        public object Create(string name, System.Security.SecureString password) => throw new NotSupportedException();
    }
    public enum SysLogTransportProtocolType { None, TLSServerAndClientAuthentication, TLSOnlyServerAuthentication, UDP }
    public sealed class Device : HardwareObject { }
    public sealed class Address : Siemens.Engineering.IEngineeringObject
    {
        internal Action<int>? SettingStart;
        internal Func<int>? ReadingStart;
        private int start;
        public int StartAddress { get => ReadingStart?.Invoke() ?? start; set { SettingStart?.Invoke(value); start = value; } }
        public int Length { get; set; }
        public AddressIoType IoType { get; set; } = AddressIoType.Input;
        public object[] AddressControllers { get; set; } = [];
        public Action<Siemens.Engineering.SW.Blocks.OB>? AssignProcessImage { get; set; }
        public void AssignProcessImageToOrganizationBlock(Siemens.Engineering.SW.Blocks.OB block) => AssignProcessImage?.Invoke(block);
        public object GetAttribute(string name) => throw new NotSupportedException();
        public void SetAttribute(string name, object? value) => throw new NotSupportedException();
    }
    public sealed class HwIdentifier { public int Identifier { get; set; } public object[] HwIdentifierControllers { get; set; } = []; }
}
namespace TiaMcpServer.ModelContextProtocol
{
    public static class PortalFailureClassifier
    {
        public static bool IsPortalProcessLost(Exception error) => TiaOpenness.Shared.ProcessLossPolicy.Reason(error) != null;
    }
}
