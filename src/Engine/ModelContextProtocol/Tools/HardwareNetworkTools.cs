using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
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
    internal sealed class HardwareNetworkTools
    {
        private readonly HardwareNetworkService _hardware;

        public HardwareNetworkTools(HardwareNetworkService hardware) => _hardware = hardware;

        [McpServerTool(Name="ReadIoSystems"), Description("[L2][Hardware][READ] Official IoSystem rows (Name, Number, Subnet, ConnectedIoDevices owner paths, HwIdentifiers, dynamic MultipleUseIoSystem/UseIoSystemNameAsDeviceNameExtension/MaxNumberIWlanLinksPerSegment/IsochronousTiTo*) scoped by exact subnetName (Subnet.IoSystems) or by the interface device item (devicePathJson + itemPathJson: NetworkInterface.IoControllers[].IoSystem and IoConnectors[].ConnectedToIoSystem, with IoController SyncRole/PnDeviceNumber/Addresses and IoConnector PnUpdateTime*/PnWatchdog*/RtClass/SyncRole rows). Attribute failures are listed per name. Paginated, no modification.")]
        public ResponseMessage ReadIoSystems(string subnetName="", string devicePathJson="[]", string itemPathJson="[]", int offset=0, int limit=100)
            => _hardware.ReadIoSystems(subnetName,devicePathJson,itemPathJson,offset,limit);
        [McpServerTool(Name="ManageIoSystem"), Description("[L2][Hardware][WRITE] Official IO system actions on the exact interface device item: create (IoController.CreateIoSystem(name), interface must be on a subnet and own no IO system; empty name = TIA default), delete (IoSystem.Delete, needs confirmDelete), update (propertiesJson Name/Number + attributesJson dynamic attributes, each read back), connect (IoConnector.ConnectToIoSystem of the IO system named ioSystemName on subnetName; also DP master systems) and disconnect (IoConnector.DisconnectFromIoSystem). Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManageIoSystem(
            string devicePathJson,
            string itemPathJson,
            [Description("action: the operation to perform - create | delete | update | connect | disconnect.")] string action,
            string name="",
            string subnetName="",
            [Description("ioSystemName: exact IO system name.")] string ioSystemName="",
            string propertiesJson="{}",
            string attributesJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => _hardware.ManageIoSystem(devicePathJson,itemPathJson,action,name,subnetName,ioSystemName,propertiesJson,attributesJson,confirmDelete,dryRun);
        [McpServerTool(Name="ReadNetworkDomains"), Description("[L2][Hardware][READ] Domain management of one exact subnet: SyncDomainOwner.SyncDomains (Name, ConvertedName, IsDefault, participants, dynamic HighPerformanceActive/FastForwardingActive), MrpDomainOwner.MrpDomains (Name, participants, dynamic IsDefault/ManagerOutsideOfProjectActive) and the MrpInstances (ConnectedMrpDomain, RingPort1/2) of the subnet's interface items. Owner availability is reported; no modification.")]
        public ResponseMessage ReadNetworkDomains(string subnetName, int offset=0, int limit=100)
            => _hardware.ReadNetworkDomains(subnetName,offset,limit);
        [McpServerTool(Name="ManageNetworkDomain"), Description("[L2][Hardware][WRITE] kind=sync/mrp on one exact subnet: create (SyncDomainComposition/MrpDomainComposition.Create(name)), delete (needs confirmDelete), update (propertiesJson Name/IsDefault + attributesJson dynamic attributes, read back) and addParticipant (DomainParticipants.Add of the NetworkInterface at participantDevicePathJson/participantItemPathJson; the API exposes no removal). Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManageNetworkDomain(
            string subnetName,
            [Description("kind: sync | mrp.")] string kind,
            [Description("action: the operation to perform - create | delete | update | addParticipant.")] string action,
            string name,
            string propertiesJson="{}",
            string attributesJson="{}",
            [Description("participantDevicePathJson: JSON array naming the station to add to the domain.")] string participantDevicePathJson="[]",
            [Description("participantItemPathJson: JSON array of device-item names of the participant's interface.")] string participantItemPathJson="[]",
            bool confirmDelete=false,
            bool dryRun=true)
            => _hardware.ManageNetworkDomain(subnetName,kind,action,name,propertiesJson,attributesJson,participantDevicePathJson,participantItemPathJson,confirmDelete,dryRun);
        [McpServerTool(Name="ReadTransferAreas"), Description("[L2][Hardware][READ] NetworkInterface.TransferAreas (I-device / DP slave / CP 16xx / PN/PN coupler: Name, Type, Direction, PositionNumber, ExtendedPositionNumber, LocalToPartnerLength, PartnerToLocalLength, LocalAddresses/PartnerAddresses rows, TransferAreaMappingRules, dynamic Comment/TransferUpdateTime/SharedDeviceAccessConfigured/UpdateAlarm/RecordIndex) and MulticastableTransferAreas (CCDX: DataLength, Comment, PartnerTransferAreas) of the exact interface device item; positionNumber[/extendedPositionNumber] uses native Find. Paginated, no modification.")]
        public ResponseMessage ReadTransferAreas(
            string devicePathJson,
            string itemPathJson,
            [Description("positionNumber: slot / position number of the module or item (-1 = not given).")] int positionNumber=-1,
            [Description("extendedPositionNumber: extended position number within the slot (-1 = not given).")] int extendedPositionNumber=-1,
            int offset=0,
            int limit=100)
            => _hardware.ReadTransferAreas(devicePathJson,itemPathJson,positionNumber,extendedPositionNumber,offset,limit);
        [McpServerTool(Name="ManageTransferArea"), Description("[L2][Hardware][WRITE] Transfer areas of the exact interface device item. kind=standard: create (TransferAreaComposition.Create(name, type[, positionNumber]); type is an official TransferAreaType such as IN/OUT/MS/CD/TM/F_PS and cannot be changed later), delete, update (propertiesJson Name/Direction/LocalToPartnerLength/PartnerToLocalLength + attributesJson), createMappingRule/updateMappingRule/deleteMappingRule (TransferAreaMappingRules: Begin/End/IoType/Offset via propertiesJson, Target via targetDevicePathJson/targetItemPathJson, ruleIndex for update/delete). kind=multicast (CCDX, DDX): create on the sender interface toward partnerDevicePathJson/partnerItemPathJson (optional name, length), createReceiver for senderName, delete (sender deletes all receivers), update (Name/Comment/DataLength). Target by exact name or positionNumber. Deletes need confirmDelete. Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManageTransferArea(
            string devicePathJson,
            string itemPathJson,
            [Description("create | delete | update | createMappingRule | updateMappingRule | deleteMappingRule | createReceiver. ")] string action,
            [Description("kind: standard | multicast.")] string kind="standard",
            string name="",
            [Description("type: None | MS | CD | F_PS | TM | IN | OUT | MSI | MSO | MSO_LOCAL | RECORD_WRITE_STO | RECORD_WRITE_PUB | RECORD_READ_STO | RECORD_READ_PUB | MSI_MSO | IN_OUT | LOCAL_RECORD_STO | LOCAL_RECORD_PUB | SUB_MSI | SUB_MSO | SUB_LOCAL_RECORD_STO_READ | SUB_LOCAL_RECORD_PUB_READ | PROFISAFE_IN12_OUT6 | PROFISAFE_IN6_OUT12 | F_Proxy_CD | DDX | ISOC_STATUS_CONTROL | ISOCHRON_IN | ISOCHRON_OUT | F_CD.")] string type="",
            [Description("positionNumber: slot / position number of the module or item (-1 = not given).")] int positionNumber=-1,
            [Description("extendedPositionNumber: extended position number within the slot (-1 = not given).")] int extendedPositionNumber=-1,
            string partnerDevicePathJson="[]",
            string partnerItemPathJson="[]",
            [Description("senderName: exact name of the sending transfer area.")] string senderName="",
            [Description("length: length in bytes.")] int length=-1,
            string propertiesJson="{}",
            string attributesJson="{}",
            [Description("ruleIndex: 0-based index of the mapping rule.")] int ruleIndex=-1,
            [Description("targetDevicePathJson: JSON array naming the target station.")] string targetDevicePathJson="[]",
            [Description("targetItemPathJson: JSON array of device-item names on the target.")] string targetItemPathJson="[]",
            bool confirmDelete=false,
            bool dryRun=true)
            => _hardware.ManageTransferArea(devicePathJson,itemPathJson,action,kind,name,type,positionNumber,extendedPositionNumber,partnerDevicePathJson,partnerItemPathJson,senderName,length,propertiesJson,attributesJson,ruleIndex,targetDevicePathJson,targetItemPathJson,confirmDelete,dryRun);
        [McpServerTool(Name="ReadDeviceItemChannels"), Description("[L2][Hardware][READ] DeviceItem.Channels of the exact module: Number, Type (Analog/Digital/Technology), IoType (Input/Output/Complex), the attribute names from GetAttributeInfos and the values of ChannelAddress/ChannelWidth plus any attributeNamesJson (failures listed per name). channelType+channelIoType+channelNumber together select one channel through native ChannelComposition.Find. Paginated, no modification. includeLinkedTags=true adds linkedTags per channel (PlcTagProvider.GetLinkedTags, V21: tag name, data type, logical address, tag table; V20 answers null with a note).")]
        public ResponseMessage ReadDeviceItemChannels(
            string devicePathJson,
            string itemPathJson,
            [Description("channelType: None | Analog | Digital | Technology.")] string channelType="",
            [Description("channelIoType: None | Input | Output | Complex.")] string channelIoType="",
            [Description("channelNumber: 0-based channel number.")] int channelNumber=-1,
            [Description("attributeNamesJson: JSON array of attribute names to read ('[]' = the documented set).")] string attributeNamesJson="[]",
            int offset=0,
            int limit=100,
            [Description("includeLinkedTags: true also returns the PLC tags linked to each channel.")] bool includeLinkedTags=false)
            => _hardware.ReadDeviceItemChannels(devicePathJson,itemPathJson,channelType,channelIoType,channelNumber,attributeNamesJson,offset,limit,includeLinkedTags);
        [McpServerTool(Name="UpdateDeviceItemChannel"), Description("[L2][Hardware][WRITE] SetAttribute on one exact channel (ChannelComposition.Find(channelType, channelIoType, channelNumber)) for the dynamic attributes in attributesJson; the CLR type is taken from the current value and every write is read back. Default dryRun=true; no save/compile/download.")]
        public ResponseMessage UpdateDeviceItemChannel(
            string devicePathJson,
            string itemPathJson,
            [Description("channelType: None | Analog | Digital | Technology.")] string channelType,
            [Description("channelIoType: None | Input | Output | Complex.")] string channelIoType,
            [Description("channelNumber: 0-based channel number.")] int channelNumber,
            string attributesJson,
            bool dryRun=true)
            => _hardware.UpdateDeviceItemChannel(devicePathJson,itemPathJson,channelType,channelIoType,channelNumber,attributesJson,dryRun);
        [McpServerTool(Name="ManageDeviceUserGroup"), Description("[L2][Hardware][WRITE] Project device user groups (Project.DeviceGroups, DeviceUserGroup.Groups/Devices, UngroupedDevicesGroup): read (empty groupPath lists root devices, top-level groups and the ungrouped system group; a path lists that group), create (DeviceUserGroupComposition.Create, missing parents created), rename (one segment) and deleteEmpty. groupPath is an exact relative nested path. CreateFrom(MasterCopy) is not exposed. Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManageDeviceUserGroup(
            string groupPath="",
            [Description("action: the operation to perform - read | create | rename | deleteEmpty.")] string action="read",
            string newName="",
            bool dryRun=true)
            => _hardware.ManageDeviceUserGroup(groupPath,action,newName,dryRun);
        [McpServerTool(Name="ManageDeviceUsers"), Description("[L2][Hardware][WRITE] Users of the exact hardware object's official services: family=webserver (CPU WebserverUserManagement.WebserverUsers: read, create with permissionsJson flag names such as [\"ReadTag\",\"DoDiagnosis\"] and password, delete, setPermissions, setPassword), family=simpleWebserver (SIWAREX SimpleWebserverUserManagement: fixed user slots; setActive, rename via newName, setPermissions None/ReadOnly/ReadWrite, setPassword) and family=opcUa (OpcUaUserManagement.OpcUaUsers: read, create with password, delete, setPassword). Passwords are converted to SecureString and never echoed; TIA refuses writes while the web server / OPC UA authentication is disabled. Deletes need confirmDelete. Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManageDeviceUsers(
            string devicePathJson,
            string itemPathJson,
            [Description("family: webserver | simpleWebserver | opcUa.")] string family,
            [Description("action: the operation to perform - read | create | delete | setPassword | setPermissions | setActive | rename.")] string action="read",
            [Description("userName: user name.")] string userName="",
            string password="",
            [Description("permissionsJson: JSON array of permission names (see the tool description).")] string permissionsJson="[]",
            [Description("active: true activates, false deactivates.")] bool active=true,
            string newName="",
            bool confirmDelete=false,
            bool dryRun=true)
            => _hardware.ManageDeviceUsers(devicePathJson,itemPathJson,family,action,userName,password,permissionsJson,active,newName,confirmDelete,dryRun);
        [McpServerTool(Name="ManagePortInterconnection"), Description("[L2][Hardware][WRITE] Topology of one exact port device item (NetworkPort service): read lists ConnectedPorts with owner paths; connect/disconnect call NetworkPort.ConnectToPort/DisconnectFromPort with the partner port at partnerDevicePathJson/partnerItemPathJson and verify the ConnectedPorts count. TIA refuses ports of the same interface and second partners on ports without alternative partners. Default dryRun=true; no save/compile/download.")]
        public ResponseMessage ManagePortInterconnection(
            string devicePathJson,
            string itemPathJson,
            [Description("action: the operation to perform - read | connect | disconnect.")] string action="read",
            string partnerDevicePathJson="[]",
            string partnerItemPathJson="[]",
            bool dryRun=true)
            => _hardware.ManagePortInterconnection(devicePathJson,itemPathJson,action,partnerDevicePathJson,partnerItemPathJson,dryRun);

        [McpServerTool(Name = "GetDeviceItemNetworkInfo"), Description("[L2][Hardware]Get network-related attributes for a device item (best-effort heuristic filter)")]
        public ResponseNetworkInfo GetDeviceItemNetworkInfo(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var attrs = _hardware.GetDeviceItemNetworkInfo(deviceItemPath);
                if (attrs != null)
                {
                    return new ResponseNetworkInfo
                    {
                        Message = $"Network info retrieved from '{deviceItemPath}'",
                        DeviceItemName = deviceItemPath.Split('/').LastOrDefault(),
                        Attributes = attrs,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving network info from '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ConnectDeviceNodesToProfinetSubnet"), Description("[L1][Hardware] PREFERRED for PROFINET network setup. Finds the first IE/PROFINET node under two devices, creates/reuses a subnet on the first, connects the second, and returns readback evidence. Requires: Connect + OpenProject + both devices added. Typical: firstRootPath='PLC_1', secondRootPath='HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'. Verified for S7-1200 + KTP700 Basic PN.")]
        public ResponseMessage ConnectDeviceNodesToProfinetSubnet(
            [Description("firstRootPath: first device/device-item root, usually PLC root, e.g. 'PLC_1'")] string firstRootPath,
            [Description("secondRootPath: second device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string secondRootPath,
            [Description("subnetName: subnet name to create when the first node is not already connected, e.g. 'PN_IE_1'")] string subnetName = "PN_IE_1")
        {
            try
            {
                var report = _hardware.ProbeConnectDeviceNodesToSubnet(firstRootPath, secondRootPath, subnetName);
                var success =
                    report.IndexOf("ConnectToSubnet: OK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (report.IndexOf("already connected to subnet", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     report.IndexOf("connectedSubnet=<none>", StringComparison.OrdinalIgnoreCase) < 0);

                return new ResponseMessage
                {
                    Message = success
                        ? "Device nodes connected to PROFINET subnet"
                        : "Device node PROFINET subnet connection did not complete",
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = success,
                        ["firstRootPath"] = firstRootPath,
                        ["secondRootPath"] = secondRootPath,
                        ["subnetName"] = subnetName,
                        ["report"] = report
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error connecting device nodes to PROFINET subnet: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanHardwareNetworkConfiguration"), Description("[L2][Hardware][OFFLINE] Validate a hardware network operation plan without connecting to TIA Portal or modifying a project. Use this before EnsureSubnet/AttachDeviceNodeToSubnet/SetPlcCpuSettings; rejects guessed paths, unsafe subnet types, invalid IP/mask/gateway, and CPU settings without exactAttributes.")]
        public ResponseJsonReport PlanHardwareNetworkConfiguration(
            [Description("planJson: JSON with operations[]. Supported operation types: EnsureSubnet, AttachDeviceNodeToSubnet, SetPlcCpuSettings. This is offline-only and performs validation only.")] string planJson)
        {
            try
            {
                var report = HardwareNetworkPlanValidator.Validate(planJson);
                return new ResponseJsonReport
                {
                    Ok = report["ok"]?.GetValue<bool>() == true,
                    Data = report,
                    Message = report["ok"]?.GetValue<bool>() == true
                        ? "Hardware network plan is valid"
                        : "Hardware network plan has validation errors",
                    Meta = ResponseMeta.Basic(DateTime.Now, report["ok"]?.GetValue<bool>() == true, ("offlineOnly", true))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating hardware network plan: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureSubnet"), Description("[L2][Hardware] Ensure an Industrial Ethernet/PROFINET subnet by anchoring on a real deviceItemPath from GetProjectTree/GetDeviceItemTree. Applies only through TIA Openness, then returns readback evidence (node path, subnet name, interface path). Does not guess paths.")]
        public ResponseMessage EnsureSubnet(
            [Description("anchorDeviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree; used to create/reuse the subnet from its first PROFINET node.")] string anchorDeviceItemPath,
            [Description("subnetType: IndustrialEthernet/PROFINET/PN/IE only.")] string subnetType,
            [Description("subnetName: exact subnet name to create or read back, e.g. PN_IE_1.")] string subnetName)
        {
            try
            {
                return _hardware.EnsureSubnet(anchorDeviceItemPath, subnetType, subnetName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring subnet '{subnetName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AttachDeviceNodeToSubnet"), Description("[L2][Hardware] Attach one real device network node to an existing PROFINET subnet and return readback evidence. deviceItemPath must come from GetProjectTree/GetDeviceItemTree, interfaceIndex selects a discovered Industrial Ethernet/PROFINET node, and online/force operations are never used.")]
        public ResponseMessage AttachDeviceNodeToSubnet(
            [Description("deviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string deviceItemPath,
            [Description("interfaceIndex: zero-based index among discovered Industrial Ethernet/PROFINET nodes under deviceItemPath.")] int interfaceIndex,
            [Description("subnetName: existing subnet name to attach to.")] string subnetName,
            [Description("anchorDeviceItemPath: optional real device-item path used to EnsureSubnet first when subnetName is not found.")] string anchorDeviceItemPath = "")
        {
            try
            {
                return _hardware.AttachDeviceNodeToSubnet(deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching device node to subnet '{subnetName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbeHardwareHmiConnectionOwnerCandidates"), Description("[L2][Hardware]Enumerate candidate owner objects for hardware HMI connection creation without calling GetService on each high-level object.")]
        public ResponseStringList ProbeHardwareHmiConnectionOwnerCandidates(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include ancestor/project/device-level candidates; when false only direct node/interface/device-item candidates")] bool deepScan = true)
        {
            try
            {
                var items = _hardware.ProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection owner candidates enumerated",
                    Items = items,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing hardware HMI connection owner candidates: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbeHardwareHmiConnectionWhitelistedServices"), Description("[L2][Hardware]Read-only scan of whitelisted services on safe hardware HMI connection owner candidates. Does not create connections and skips high-level project/composition objects to avoid hangs.")]
        public ResponseStringList ProbeHardwareHmiConnectionWhitelistedServices(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include safe ancestors; when false only direct node/interface/device-item candidates")] bool deepScan = true)
        {
            try
            {
                var items = _hardware.ProbeHardwareHmiConnectionWhitelistedServices(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection whitelisted services scanned",
                    Items = items,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error scanning hardware HMI connection whitelisted services: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetProjectTopology"), Description(
            "[L1][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " One-shot, read-only project topology from Openness: every device with its network nodes (IP, subnet, node type)." +
            " Call this early to understand the project's devices and subnets at a glance, instead of probing S7 or parsing AML.")]
        public ResponseJsonReport GetProjectTopology()
        {
            try
            {
                var data = _hardware.GetProjectTopology();
                int count = data["deviceCount"]?.GetValue<int>() ?? 0;
                return new ResponseJsonReport
                {
                    Ok = count > 0,
                    Message = $"Project topology: {count} device(s).",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, count > 0)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetProjectTopology failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
