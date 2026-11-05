using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

// Only native objects and peripheral lookups are faked. Step bodies, metadata,
// exception mapping, property conversion and failure readbacks are production code.
namespace Siemens.Engineering
{
    public interface IEngineeringService { }
    public interface IEngineeringServiceProvider { public T? GetService<T>() where T : class, IEngineeringService; }
    public enum ArchiveMode { Compressed }
    public sealed class FakeProjects : List<Project>
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
        public List<HW.Device> Devices { get; } = new();
        public List<HW.DeviceUserGroup> DeviceGroups { get; } = new();
        public HW.DeviceGroup UngroupedDevicesGroup { get; } = new();
        public List<HW.Subnet> Subnets { get; } = new();
        public Dictionary<Type, object> Services { get; } = new();
        public T? GetService<T>() where T : class, IEngineeringService => Services.TryGetValue(typeof(T), out var value) ? (T)value : null;
    }
}
namespace Siemens.Engineering.HW
{
    public class HardwareObject : IEngineeringObject, IEngineeringServiceProvider
    {
        public List<DeviceItem> DeviceItems { get; } = new();
        public List<DeviceItem> Items { get; } = new();
        public List<HwIdentifier> HwIdentifiers { get; } = new();
        public string Name { get; set; } = "";
        public object? Parent { get; set; }
        public Dictionary<Type, object> Services { get; } = new();
        public List<string> Calls { get; } = new();
        public Exception? AttributeFailure { get; set; }
        public T? GetService<T>() where T : class, IEngineeringService => Services.TryGetValue(typeof(T), out var value) ? (T)value : null;
        public virtual object GetAttribute(string name) { Calls.Add("get:" + name); return false; }
        public virtual void SetAttribute(string name, object? value) { Calls.Add("set:" + name); if (AttributeFailure != null) throw AttributeFailure; }
    }
    public sealed class NativeNamedList<T> : List<T> where T : class
    {
        public T? Find(string name) => this.FirstOrDefault(value => value.GetType().GetProperty("Name")?.GetValue(value)?.ToString() == name);
    }
    public sealed class Node : HardwareObject { public Subnet? ConnectedSubnet { get; set; } }
    public class DeviceGroup : HardwareObject { public List<Device> Devices { get; } = new(); }
    public sealed class DeviceUserGroup : DeviceGroup { public List<DeviceUserGroup> Groups { get; } = new(); }
    public sealed class Subnet : HardwareObject { public List<IoSystem> IoSystems { get; } = new(); }
    public sealed class IoSystem : HardwareObject
    {
        public int Number { get; set; }
        public Subnet? Subnet { get; set; }
        public List<object> ConnectedIoDevices { get; } = new();
        public void Delete() { }
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
    public enum AddressIoType { Input, Output }
    public enum ChannelType { Digital, Analog }
    public enum ChannelIoType { Input, Output }
    public enum AttributeAccessMode { ReadWrite }
    public sealed class AttributeInfo { public string Name { get; set; } = "ChannelAddress"; public AttributeAccessMode AccessMode =>
        AttributeAccessMode.ReadWrite; }
    public sealed class Channel : HardwareObject
    {
        public int Number { get; set; }
        public ChannelType Type { get; set; }
        public ChannelIoType IoType { get; set; }
        public IEnumerable<AttributeInfo> GetAttributeInfos() => new[] { new AttributeInfo() };
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
        public void ConnectToPort(NetworkPort port) => ConnectedPorts.Add(port);
        public void DisconnectFromPort(NetworkPort port) => ConnectedPorts.Remove(port);
    }
}
namespace Siemens.Engineering.HW.Features
{
    public sealed class NetworkInterface : IEngineeringService
    {
        public List<Siemens.Engineering.HW.IoConnector> IoConnectors { get; } = new();
        public List<Siemens.Engineering.HW.IoController> IoControllers { get; } = new();
        public List<object> Nodes { get; } = new();
        public string InterfaceType => "Ethernet";
        public string InterfaceOperatingMode => "IoController";
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
        public SysLogServerConfiguration Create(string address, ushort port) { var server = new SysLogServerConfiguration { SysLogServerAddress = address,
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
        public bool Import(System.IO.FileInfo source, System.IO.FileInfo log, CaxImportOptions option) { ImportCalls++; return false; }
        public bool Export(HW.Device device, System.IO.FileInfo destination, System.IO.FileInfo log) => false;
    }
}
namespace TiaMcp.Adapters.Native.Hardware { public static class ProjectDeviceWalk { public const string UngroupedSegment = "$ungrouped"; } }
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
        private IEnumerable<Siemens.Engineering.HW.Device> EnumerateAllDevices() => project!.Devices;
        private sealed class EngineeringAmbientAccess : IDisposable { public void Dispose() { } }
        private Siemens.Engineering.TiaPortal Portal() => portal!;
        private IEnumerable<(object Node, string Path)> FindNetworkNodes(Siemens.Engineering.HW.DeviceItem item) => Array.Empty<(object, string)>();
        private object? TryGetPropertyValue(object value, string name) => null;
        private string? TryGetName(object? value) => null;
        private string? ReadNodeAddress(object value) => null;
        private bool IsIndustrialEthernetNode(object value) => false;
        private Siemens.Engineering.SW.PlcSoftware? ResolvePlc(string name) => null;
        private (Siemens.Engineering.SW.PlcSoftware Value, string Path) ReadSelection(string name) => (FamilyPlc, name);
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
    internal sealed class PutGetAccess
    {
        internal PutGetAccess(TiaMcp.Adapters.PlcFoundationEngine engine) { }
        internal Siemens.Engineering.HW.Device? GetDevice(string path) => null;
        internal static (Siemens.Engineering.HW.DeviceItem?, string?) FindPutGetAttribute(Siemens.Engineering.HW.Device device) => (null, null);
        internal static bool AttrValueIsEnabled(object? value) => false;
        internal TiaMcp.Adapters.Native.Hmi.HmiObject GetPutGetAccess(string path) => new();
    }
}

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class HmiScalar { internal static HmiObject Read(object value) => new(); }
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
    public enum SysLogTransportProtocolType { None, TLSServerAndClientAuthentication, TLSOnlyServerAuthentication, UDP }
    public sealed class Device : HardwareObject { }
    public sealed class Address : Siemens.Engineering.IEngineeringObject
    {
        public int StartAddress { get; set; }
        public int Length { get; set; }
        public AddressIoType IoType { get; set; } = AddressIoType.Input;
        public object[] AddressControllers { get; set; } = [];
        public void AssignProcessImageToOrganizationBlock(Siemens.Engineering.SW.Blocks.OB block) { }
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
