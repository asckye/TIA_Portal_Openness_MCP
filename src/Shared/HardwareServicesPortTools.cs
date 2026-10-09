using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareServicesPortTools
    {
        private readonly HardwareServicesPortService _hardware;
        public HardwareServicesPortTools(HardwareServicesPortService hardware) => _hardware = hardware;
        [McpServerTool(Name="ListCommunicationConnections"), Description("[L2][Hardware][READ] List Siemens.Engineering.HW.CommunicationConnections (S7/ISO/ISO-on-TCP/TCP/UDP/FDL/PtP/HMI) owned by one exact hardware object (devicePath/itemPath name arrays, e.g. the CPU). Scalar properties (LocalConnectionName, TSAPs, addresses, ports) plus local/partner target and interface names; offset/limit pagination. NotSupported on TIA V20. Nothing modified.")]
        public CallToolResult ListCommunicationConnections(
            [Description("devicePath: array naming the station, e.g. [\"PLC_1\"].")] string[] devicePath,
            [Description("itemPath: array of device-item names down to the item with the service; [] = the station.")] string[] itemPath = null!,
            [Description("offset: first connection to return (paging).")] int offset=0,
            [Description("limit: maximum connections to return.")] int limit=100)
            => HardwareToolContract.Invoke("ListCommunicationConnections", true, false, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                });
                return _hardware.ReadCommunicationConnections(devicePathJson,itemPathJson,offset,limit);
            });

        [McpServerTool(Name="ManageCommunicationConnection"), Description("[L2][Hardware][WRITE] Create or delete one communication connection on an exact owner DeviceItem. create: connectionType is one exact kind (S7Connection/IsoConnection/IsoOnTcpConnection/TcpConnection/UdpConnection/FdlConnection/PtpConnection/HmiConnection), localInterfaceItemPath + localNodeName (required when the interface has several nodes), partnerDevicePath/partnerItemPath (partner DeviceItem) and partnerInterfaceItemPath/partnerNodeName; optional connectionName is written to LocalConnectionName and verified. delete: exact LocalConnectionName (ambiguous names refused) and confirmDelete=true. Default preview; real run needs exclusive access. NotSupported on TIA V20. No save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageCommunicationConnection(
            [Description("devicePath: array naming the station that owns the connections, e.g. [\"PLC_1\"].")] string[] devicePath,
            [Description("itemPath: array of device-item names down to the item carrying the CommunicationConnections service (usually the CPU); [] = the station.")] string[] itemPath,
            [Description("action: create | delete.")] string action,
            [Description("connectionType: for create - S7Connection | IsoConnection | IsoOnTcpConnection | TcpConnection | UdpConnection | FdlConnection | PtpConnection | HmiConnection.")] string connectionType="",
            [Description("connectionName: name of the connection to create or delete.")] string connectionName="",
            [Description("localInterfaceItemPath: array path of the local interface item, e.g. [\"PROFINET interface_1\"].")] string[] localInterfaceItemPath = null!,
            [Description("localNodeName: name of the local node on that interface.")] string localNodeName="",
            [Description("partnerDevicePath: array naming the partner station.")] string[] partnerDevicePath = null!,
            [Description("partnerItemPath: array of device-item names on the partner.")] string[] partnerItemPath = null!,
            [Description("partnerInterfaceItemPath: array path of the partner's interface item.")] string[] partnerInterfaceItemPath = null!,
            [Description("partnerNodeName: name of the partner node.")] string partnerNodeName="",
            [Description("confirmDelete: must be true together with dryRun=false to delete.")] bool confirmDelete=false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => HardwareToolContract.Invoke("ManageCommunicationConnection", dryRun, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string localInterfaceItemPathJson = HardwareToolContract.Path(localInterfaceItemPath, "localInterfaceItemPath", rootAllowed: true, optional: true);
                string partnerDevicePathJson = HardwareToolContract.Path(partnerDevicePath, "partnerDevicePath", rootAllowed: true, optional: true);
                string partnerItemPathJson = HardwareToolContract.Path(partnerItemPath, "partnerItemPath", rootAllowed: true, optional: true);
                string partnerInterfaceItemPathJson = HardwareToolContract.Path(partnerInterfaceItemPath, "partnerInterfaceItemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.RequireOneOf(action, new[] { "create", "delete" }, "action");
                    if (action == "delete") HardwareToolContract.Confirm(dryRun, confirmDelete);
                    if (action == "create") HardwareServicesLogic.ConnectionTypeName(connectionType);
                });
                return _hardware.ManageCommunicationConnection(devicePathJson,itemPathJson,action,connectionType,connectionName,localInterfaceItemPathJson,localNodeName,partnerDevicePathJson,partnerItemPathJson,partnerInterfaceItemPathJson,partnerNodeName,confirmDelete,dryRun);
            });

        [McpServerTool(Name="ExchangeSystemDiagnosticsSettings"), Description("[L2][Hardware][FILE] Export/import system diagnostics settings via SystemdiagnosticsSettingsDataProvider (project-level by default; optional exact devicePath/itemPath owner). export writes a NEW absolute file and returns its sha256; import needs an existing absolute file and confirmImport=true. Native result state attached; Error state fails the tool. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangeSystemDiagnosticsSettings(
            [Description("action: the operation to perform - export | import.")] string action,
            string filePath,
            string[] devicePath = null!,
            string[] itemPath = null!,
            [Description("confirmImport: must be true together with dryRun=false to import (imports replace project data).")] bool confirmImport=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ExchangeSystemDiagnosticsSettings", dryRun, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: true, optional: true);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.RequireOneOf(action, new[] { "export", "import" }, "action");
                    if (action == "import") HardwareToolContract.Confirm(dryRun, confirmImport);
                });
                return _hardware.ExchangeSystemDiagnosticsSettings(action,filePath,devicePathJson,itemPathJson,confirmImport,dryRun);
            });

        [McpServerTool(Name="GetHardwareFeatures"), Description("[L2][Hardware][READ] Probe every Siemens.Engineering.HW.Features.* service from the V21 API catalog on one exact hardware object (devicePath/itemPath): reports advertised services, present/absent per feature and public scalar values (credential-related features report presence only). offset/limit pagination. Read-only.")]
        public CallToolResult GetHardwareFeatures(string[] devicePath,string[] itemPath = null!,int offset=0,int limit=100)
            => HardwareToolContract.Invoke("GetHardwareFeatures", true, false, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                });
                return _hardware.ReadHardwareFeatures(devicePathJson,itemPathJson,offset,limit);
            });

        [McpServerTool(Name="ManageDeviceServiceObjects"), Description("[L2][Hardware][WRITE] Typed objects behind three PLC services of the exact hardware object. family=webApplications (DefaultWebPagesFeature.WebApplicationConfigurations: read Name/ApplicationType/IsDefault, setDefault name). family=telecontrolDataPoints (TelecontrolManagement.TelecontrolDataPoints: read Name/DataPointType and DNP3/IEC DataPointIndex/MasterFunction or WDC DataPointIndex, update name + properties, delete, export/import filePath via ExportDataPoints/ImportDataPoints). family=certificateServices (CertificateManagementConfiguration, PLC families V3.0+: read Usage TIAPortal/Runtime, CertificateExpirationEventActivated, RemainingCertificateLifetime 10..90 % and CertificateSupportedServices Id/ServiceType/ServiceGroupName/ApplicationUri/Guid; update properties; setServiceGroupName name=<Id or Guid> properties {ServiceGroupName<=64}; createService; deleteService name=<Id or Guid>). Real changes need confirmChange with dryRun=false; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageDeviceServiceObjects(
            string[] devicePath,
            string[] itemPath,
            [Description("family: webApplications | telecontrolDataPoints | certificateServices.")] string family,
            [Description("read | setDefault | update | delete | export | import | setServiceGroupName | createService | deleteService. ")] string action="read",
            string name="",
            AttributeMap<Scalar> properties = null!,
            string filePath="",
            bool confirmChange=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageDeviceServiceObjects", dryRun || action == "read", action != "read", () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string propertiesJson = HardwareToolContract.Map(properties, "properties", optional: true);
                HardwareToolContract.Check(() =>
                {
                    DeviceServiceObjectRules.ValidateServiceObjectRequest(family, action, name, JsonNode.Parse(propertiesJson)!.AsObject(), filePath, confirmChange, dryRun);
                });
                return _hardware.ManageDeviceServiceObjects(devicePathJson,itemPathJson,family,action,name,propertiesJson,filePath,confirmChange,dryRun);
            });

        [McpServerTool(Name="ManageHardwareUtilities"), Description("[L2][Hardware][FILE] Project.HwUtilities: list (Identifier + class), findModuleTypes / findContainerTypes / normalizeTypeIdentifier (ModuleInformationProvider on a typeIdentifier), exportOpcUa (OpcUaExportProvider.Export(PLC DeviceItem, new .xml file)), exportCardReaderPsc (CardReaderPscProvider.Export(Device, new .psc file[, password] - creates the card image; f-activated devices refuse on V18 and below, encryption needs CPU V40.0+). Exports refuse to overwrite; default dryRun=true; the password is never echoed. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageHardwareUtilities(
            [Description("action: the operation to perform - list | findModuleTypes | findContainerTypes | normalizeTypeIdentifier | exportOpcUa | exportCardReaderPsc.")] string action="list",
            [Description("typeIdentifier: catalog type identifier of the form 'OrderNumber:6ES7 ...' or 'OrderNumber:.../V2.9' (SearchHardwareCatalog / ManageHardwareUtilities normalizeTypeIdentifier).")] string typeIdentifier="",
            string[] devicePath = null!,
            string[] itemPath = null!,
            string filePath="",
            string password="",
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageHardwareUtilities", dryRun || !action.StartsWith("export", StringComparison.Ordinal), action.StartsWith("export", StringComparison.Ordinal), () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: true, optional: true);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareUtilityRules.ValidateHardwareUtilityRequest(action, typeIdentifier, devicePathJson, filePath, password, dryRun);
                });
                return _hardware.ManageHardwareUtilities(action,typeIdentifier,devicePathJson,itemPathJson,filePath,password,dryRun);
            });
    }
}
