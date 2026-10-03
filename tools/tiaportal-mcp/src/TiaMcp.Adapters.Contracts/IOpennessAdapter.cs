using System;

namespace TiaMcp.Adapters.Contracts
{
    [Flags]
    public enum AdapterCapabilities
    {
        None = 0,
        PortalSession = 1,
        PlcProgram = 2,
        PlcData = 4,
        Hardware = 8,
        VersionControl = 16,
        HmiExport = 32
    }

    // Marker surfaces only. Existing adapters are not wired to this interface in step B.
    public interface IOpennessAdapter
    {
        string ReleaseKey { get; }
        string ApiIdentity { get; }
        AdapterCapabilities Capabilities { get; }
        IPortalSession? PortalSession { get; }
        IPlcProgram? PlcProgram { get; }
        IPlcData? PlcData { get; }
        IHardware? Hardware { get; }
        IVersionControl? VersionControl { get; }
        IHmiExport? HmiExport { get; }
    }

    public interface IPortalSession { }
    public interface IPlcProgram { }
    public interface IPlcData { }
    public interface IHardware { }
    public interface IVersionControl { }
    public interface IHmiExport { }
}
