using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareServicesTools
    {
        private readonly HardwareServicesService _hardware;

        public HardwareServicesTools(HardwareServicesService hardware) => _hardware = hardware;

        [McpServerTool(Name="ReadCommunicationConnections"), Description("[L2][Hardware][READ] List Siemens.Engineering.HW.CommunicationConnections (S7/ISO/ISO-on-TCP/TCP/UDP/FDL/PtP/HMI) owned by one exact hardware object (devicePathJson/itemPathJson JSON name arrays, e.g. the CPU). Scalar properties (LocalConnectionName, TSAPs, addresses, ports) plus local/partner target and interface names; offset/limit pagination. NotSupported on TIA V20. Nothing modified.")]
        public ResponseMessage ReadCommunicationConnections(
            [Description("devicePathJson: JSON array naming the station, e.g. [\"PLC_1\"].")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names down to the item with the service; [] = the station.")] string itemPathJson="[]",
            [Description("offset: first connection to return (paging).")] int offset=0,
            [Description("limit: maximum connections to return.")] int limit=100)
            => _hardware.ReadCommunicationConnections(devicePathJson,itemPathJson,offset,limit);
        [McpServerTool(Name="ManageCommunicationConnection"), Description("[L2][Hardware][WRITE] Create or delete one communication connection on an exact owner DeviceItem. create: connectionType is one exact kind (S7Connection/IsoConnection/IsoOnTcpConnection/TcpConnection/UdpConnection/FdlConnection/PtpConnection/HmiConnection), localInterfaceItemPathJson + localNodeName (required when the interface has several nodes), partnerDevicePathJson/partnerItemPathJson (partner DeviceItem) and partnerInterfaceItemPathJson/partnerNodeName; optional connectionName is written to LocalConnectionName and verified. delete: exact LocalConnectionName (ambiguous names refused) and confirmDelete=true. Default preview; real run needs exclusive access. NotSupported on TIA V20. No save/compile/download.")]
        public ResponseMessage ManageCommunicationConnection(
            [Description("devicePathJson: JSON array naming the station that owns the connections, e.g. [\"PLC_1\"].")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names down to the item carrying the CommunicationConnections service (usually the CPU); [] = the station.")] string itemPathJson,
            [Description("action: create | delete.")] string action,
            [Description("connectionType: for create - S7Connection | IsoConnection | IsoOnTcpConnection | TcpConnection | UdpConnection | FdlConnection | PtpConnection | HmiConnection.")] string connectionType="",
            [Description("connectionName: name of the connection to create or delete.")] string connectionName="",
            [Description("localInterfaceItemPathJson: JSON array path of the local interface item, e.g. [\"PROFINET interface_1\"].")] string localInterfaceItemPathJson="[]",
            [Description("localNodeName: name of the local node on that interface.")] string localNodeName="",
            [Description("partnerDevicePathJson: JSON array naming the partner station.")] string partnerDevicePathJson="[]",
            [Description("partnerItemPathJson: JSON array of device-item names on the partner.")] string partnerItemPathJson="[]",
            [Description("partnerInterfaceItemPathJson: JSON array path of the partner's interface item.")] string partnerInterfaceItemPathJson="[]",
            [Description("partnerNodeName: name of the partner node.")] string partnerNodeName="",
            [Description("confirmDelete: must be true together with dryRun=false to delete.")] bool confirmDelete=false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => _hardware.ManageCommunicationConnection(devicePathJson,itemPathJson,action,connectionType,connectionName,localInterfaceItemPathJson,localNodeName,partnerDevicePathJson,partnerItemPathJson,partnerInterfaceItemPathJson,partnerNodeName,confirmDelete,dryRun);
        [McpServerTool(Name="ManageWatchForceTableWebAccess"), Description("[L2][Hardware][WRITE] Read/assign/unassign web-server access rules for PLC watch/force tables (WatchAndForceTableAccessManager on the exact CPU DeviceItem). read lists both rule sets. assign/unassign need softwarePath (exact PLC, must be Offline for a real run), tableKind watch|force, exact tablePath (group/table), access None|Read|Write and confirmChange=true. Existing rule with read-only Access is refused (unassign first). Default preview; readback verified; no save/compile/download.")]
        public ResponseMessage ManageWatchForceTableWebAccess(
            string devicePathJson,
            string itemPathJson,
            [Description("action: the operation to perform - read | assign | unassign.")] string action="read",
            string softwarePath="",
            [Description("tableKind: watch | force.")] string tableKind="watch",
            string tablePath="",
            [Description("access: None | Read | Write.")] string access="Read",
            bool confirmChange=false,
            bool dryRun=true)
            => _hardware.ManageWatchForceTableWebAccess(devicePathJson,itemPathJson,action,softwarePath,tableKind,tablePath,access,confirmChange,dryRun);
        [McpServerTool(Name="ExchangeSystemDiagnosticsSettings"), Description("[L2][Hardware][FILE] Export/import system diagnostics settings via SystemdiagnosticsSettingsDataProvider (project-level by default; optional exact devicePathJson/itemPathJson owner). export writes a NEW absolute file and returns its sha256; import needs an existing absolute file and confirmImport=true. Native result state attached; Error state fails the tool. Default preview; no save/compile/download.")]
        public ResponseMessage ExchangeSystemDiagnosticsSettings(
            [Description("action: the operation to perform - export | import.")] string action,
            string filePath,
            string devicePathJson="[]",
            string itemPathJson="[]",
            [Description("confirmImport: must be true together with dryRun=false to import (imports replace project data).")] bool confirmImport=false,
            bool dryRun=true)
            => _hardware.ExchangeSystemDiagnosticsSettings(action,filePath,devicePathJson,itemPathJson,confirmImport,dryRun);
        [McpServerTool(Name="ReadHardwareFeatures"), Description("[L2][Hardware][READ] Probe every Siemens.Engineering.HW.Features.* service from the V21 API catalog on one exact hardware object (devicePathJson/itemPathJson): reports advertised services, present/absent per feature and public scalar values (credential-related features report presence only). offset/limit pagination. Read-only.")]
        public ResponseMessage ReadHardwareFeatures(string devicePathJson,string itemPathJson="[]",int offset=0,int limit=100)
            => _hardware.ReadHardwareFeatures(devicePathJson,itemPathJson,offset,limit);
        [McpServerTool(Name="ManageDeviceServiceObjects"), Description("[L2][Hardware][WRITE] Typed objects behind three PLC services of the exact hardware object. family=webApplications (DefaultWebPagesFeature.WebApplicationConfigurations: read Name/ApplicationType/IsDefault, setDefault name). family=telecontrolDataPoints (TelecontrolManagement.TelecontrolDataPoints: read Name/DataPointType and DNP3/IEC DataPointIndex/MasterFunction or WDC DataPointIndex, update name + propertiesJson, delete, export/import filePath via ExportDataPoints/ImportDataPoints). family=certificateServices (CertificateManagementConfiguration, PLC families V3.0+: read Usage TIAPortal/Runtime, CertificateExpirationEventActivated, RemainingCertificateLifetime 10..90 % and CertificateSupportedServices Id/ServiceType/ServiceGroupName/ApplicationUri/Guid; update propertiesJson; setServiceGroupName name=<Id or Guid> propertiesJson {ServiceGroupName<=64}; createService; deleteService name=<Id or Guid>). Real changes need confirmChange with dryRun=false; no save/compile/download.")]
        public ResponseMessage ManageDeviceServiceObjects(
            string devicePathJson,
            string itemPathJson,
            [Description("family: webApplications | telecontrolDataPoints | certificateServices.")] string family,
            [Description("read | setDefault | update | delete | export | import | setServiceGroupName | createService | deleteService. ")] string action="read",
            string name="",
            string propertiesJson="{}",
            string filePath="",
            bool confirmChange=false,
            bool dryRun=true)
            => _hardware.ManageDeviceServiceObjects(devicePathJson,itemPathJson,family,action,name,propertiesJson,filePath,confirmChange,dryRun);

    }
}
