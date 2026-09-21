using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadCommunicationConnections"), Description("[L2][Hardware][READ] List Siemens.Engineering.HW.CommunicationConnections (S7/ISO/ISO-on-TCP/TCP/UDP/FDL/PtP/HMI) owned by one exact hardware object (devicePathJson/itemPathJson JSON name arrays, e.g. the CPU). Scalar properties (LocalConnectionName, TSAPs, addresses, ports) plus local/partner target and interface names; offset/limit pagination. NotSupported on TIA V20. Nothing modified.")]
        public static ResponseMessage ReadCommunicationConnections(
            [Description("devicePathJson: JSON array naming the station, e.g. [\"PLC_1\"].")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names down to the item with the service; [] = the station.")] string itemPathJson="[]",
            [Description("offset: first connection to return (paging).")] int offset=0,
            [Description("limit: maximum connections to return.")] int limit=100)
            => Portal.ReadCommunicationConnections(devicePathJson,itemPathJson,offset,limit);
        [McpServerTool(Name="ManageCommunicationConnection"), Description("[L2][Hardware][WRITE] Create or delete one communication connection on an exact owner DeviceItem. create: connectionType is one exact kind (S7Connection/IsoConnection/IsoOnTcpConnection/TcpConnection/UdpConnection/FdlConnection/PtpConnection/HmiConnection), localInterfaceItemPathJson + localNodeName (required when the interface has several nodes), partnerDevicePathJson/partnerItemPathJson (partner DeviceItem) and partnerInterfaceItemPathJson/partnerNodeName; optional connectionName is written to LocalConnectionName and verified. delete: exact LocalConnectionName (ambiguous names refused) and confirmDelete=true. Default preview; real run needs exclusive access. NotSupported on TIA V20. No save/compile/download.")]
        public static ResponseMessage ManageCommunicationConnection(
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
            => Portal.ManageCommunicationConnection(devicePathJson,itemPathJson,action,connectionType,connectionName,localInterfaceItemPathJson,localNodeName,partnerDevicePathJson,partnerItemPathJson,partnerInterfaceItemPathJson,partnerNodeName,confirmDelete,dryRun);
        [McpServerTool(Name="ManageWatchForceTableWebAccess"), Description("[L2][Hardware][WRITE] Read/assign/unassign web-server access rules for PLC watch/force tables (WatchAndForceTableAccessManager on the exact CPU DeviceItem). read lists both rule sets. assign/unassign need softwarePath (exact PLC, must be Offline for a real run), tableKind watch|force, exact tablePath (group/table), access None|Read|Write and confirmChange=true. Existing rule with read-only Access is refused (unassign first). Default preview; readback verified; no save/compile/download.")]
        public static ResponseMessage ManageWatchForceTableWebAccess(string devicePathJson,string itemPathJson,string action="read",string softwarePath="",string tableKind="watch",string tablePath="",string access="Read",bool confirmChange=false,bool dryRun=true)
            => Portal.ManageWatchForceTableWebAccess(devicePathJson,itemPathJson,action,softwarePath,tableKind,tablePath,access,confirmChange,dryRun);
        [McpServerTool(Name="ExchangeSystemDiagnosticsSettings"), Description("[L2][Hardware][FILE] Export/import system diagnostics settings via SystemdiagnosticsSettingsDataProvider (project-level by default; optional exact devicePathJson/itemPathJson owner). export writes a NEW absolute file and returns its sha256; import needs an existing absolute file and confirmImport=true. Native result state attached; Error state fails the tool. Default preview; no save/compile/download.")]
        public static ResponseMessage ExchangeSystemDiagnosticsSettings(string action,string filePath,string devicePathJson="[]",string itemPathJson="[]",bool confirmImport=false,bool dryRun=true)
            => Portal.ExchangeSystemDiagnosticsSettings(action,filePath,devicePathJson,itemPathJson,confirmImport,dryRun);
        [McpServerTool(Name="ReadOpcUaAccessControl"), Description("[L2][PLC-OpcUA][READ] Read the PLC OPC UA server access control (ServerInterfaceGroup.AccessControl): section roles (RoleMappings with per-namespace permissions) or restrictions (NamespaceAccessRestrictions). Exact softwarePath; offset/limit pagination. NotSupported on TIA V20. No secrets, nothing modified.")]
        public static ResponseMessage ReadOpcUaAccessControl(string softwarePath,string section="roles",int offset=0,int limit=100)
            => Portal.ReadOpcUaAccessControl(softwarePath,section,offset,limit);
        [McpServerTool(Name="ManageOpcUaAccessControl"), Description("[L2][PLC-OpcUA][WRITE] Modify PLC OPC UA server access control: createRole (roleName, definedInNamespace), addStandardRole (roleName), deleteRole (roleName), setProjectRole (roleName, projectRole), setPermission (roleName, exact namespaceUri, permission Browse|Read|Write|Call|ReceiveEvents|ReadRolePermissions, enabled), setRestriction (exact namespaceUri, propertiesJson of scalar properties). Exact names only; ambiguity refused. Real run needs confirmChange=true, Offline PLC and exclusive access; readback verified. NotSupported on TIA V20. Default preview; no save/compile/download.")]
        public static ResponseMessage ManageOpcUaAccessControl(string softwarePath,string action,string roleName="",string definedInNamespace="",string projectRole="",string namespaceUri="",string permission="",bool enabled=false,string propertiesJson="{}",bool confirmChange=false,bool dryRun=true)
            => Portal.ManageOpcUaAccessControl(softwarePath,action,roleName,definedInNamespace,projectRole,namespaceUri,permission,enabled,propertiesJson,confirmChange,dryRun);
        [McpServerTool(Name="ImportDeviceAml"), Description("[L2][Hardware][FILE] Import an AutomationML/CAx (.aml) file into the open project via CaxProvider.Import(file, logFile, option). filePath must exist (absolute); logFilePath is a NEW absolute file; importOption RetainTiaDevice|OverwriteTiaDevice|MoveToParkingLot. May add or replace devices; real run needs confirmImport=true and exclusive access. Returns native bool result, device counts and the log file sha256. Default preview; no save/compile/download.")]
        public static ResponseMessage ImportDeviceAml(string filePath,string logFilePath,string importOption="RetainTiaDevice",bool confirmImport=false,bool dryRun=true)
            => Portal.ImportDeviceAml(filePath,logFilePath,importOption,confirmImport,dryRun);
        [McpServerTool(Name="ManagePlcProtection"), Description("[L2][Hardware][WRITE] Access level and confidential-configuration-data password of ONE CPU (PlcAccessLevelProvider / PlcMasterSecretConfigurator on the CPU device item; itemPathJson [] resolves the station's CPU). read reports accessLevel (FullAccess / ReadAccess / HMIAccess / NoAccess / FullAccessIncludingFailsafe), masterSecret (None = 'Protect confidential PLC configuration data' unchecked, WithoutPassword = checked without password, WithPassword, WithPasswordAllDataProtection) and the access-control mode. setAccessLevel accessLevel; setAccessPassword / resetAccessPassword accessLevel [+ password] (TIA only accepts passwords for levels less strict than the selected one); protectMasterSecret password; changeMasterSecret password newPassword; unprotectMasterSecret [password]; resetMasterSecret (certificates encrypted with it are lost); protectAllConfiguration [password]; unprotectAllConfiguration. Why: TIA V21 refuses the hardware download of an S7-1500 FW >= 2.9 CPU whose level is above FullAccess without a FullAccess password or whose confidential data has no password ('硬件配置编译完成，但出现错误' - see CompileDevice). Passwords go in as SecureString and are never echoed; state read back (meta.before / after). Default preview; real change needs dryRun=false AND confirmChange=true; no compile / save / download.")]
        public static ResponseMessage ManagePlcProtection(
            [Description("devicePathJson: JSON array naming the station, e.g. [\"PLC_1\"] (or [group, ..., station]).")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names down to the CPU; [] (default) resolves the station's CPU.")] string itemPathJson="[]",
            [Description("action: read | setAccessLevel | setAccessPassword | resetAccessPassword | protectMasterSecret | changeMasterSecret | unprotectMasterSecret | resetMasterSecret | protectAllConfiguration | unprotectAllConfiguration.")] string action="read",
            [Description("accessLevel: for setAccessLevel / setAccessPassword / resetAccessPassword - FullAccess | ReadAccess | HMIAccess | NoAccess | FullAccessIncludingFailsafe.")] string accessLevel="",
            [Description("password: the password to set (setAccessPassword, protectMasterSecret) or the current one (changeMasterSecret); never logged.")] string password="",
            [Description("newPassword: the new password for changeMasterSecret.")] string newPassword="",
            [Description("confirmChange: must be true together with dryRun=false for every action except read.")] bool confirmChange=false,
            [Description("dryRun: true (default) previews; false applies the change.")] bool dryRun=true)
            => Portal.ManagePlcProtection(devicePathJson,itemPathJson,action,accessLevel,password,newPassword,confirmChange,dryRun);
        [McpServerTool(Name="CompileDevice"), Description("[L2][Hardware][EXECUTE] Hardware compile of one device (or device item) through ICompilable - what the TIA UI's 'Compile > Hardware (rebuild all)' does and what DownloadToPlc runs first; CompileSoftware / CompileAndDiagnosePlc only compile the program. Returns the compiler state, error / warning counts and the flattened diagnostics (errors[] / warnings[] / nodes) - e.g. the security errors of an S7-1500 FW >= 2.9 CPU that ManagePlcProtection fixes. No save / download.")]
        public static ResponseMessage CompileDevice(
            [Description("devicePathJson: JSON array naming the station to compile, e.g. [\"PLC_1\"].")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names when one item (e.g. the CPU) is to be compiled; [] = the whole station.")] string itemPathJson="[]")
            => Portal.CompileDevice(devicePathJson,itemPathJson);
        [McpServerTool(Name="ReadHardwareFeatures"), Description("[L2][Hardware][READ] Probe every Siemens.Engineering.HW.Features.* service from the V21 API catalog on one exact hardware object (devicePathJson/itemPathJson): reports advertised services, present/absent per feature and public scalar values (credential-related features report presence only). offset/limit pagination. Read-only.")]
        public static ResponseMessage ReadHardwareFeatures(string devicePathJson,string itemPathJson="[]",int offset=0,int limit=100)
            => Portal.ReadHardwareFeatures(devicePathJson,itemPathJson,offset,limit);
    }
}
