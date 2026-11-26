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
    internal sealed class HardwareNetworkTools
    {
        private readonly HardwareNetworkPortService _hardware;

        public HardwareNetworkTools(HardwareNetworkPortService hardware) => _hardware = hardware;

        [McpServerTool(Name="ListIoSystems"),
                Description("[L2][Hardware][READ] Official IoSystem rows (Name, Number, Subnet, ConnectedIoDevices owner paths, HwIdentifiers, dynamic MultipleUseIoSystem/UseIoSystemNameAsDeviceNameExtension/MaxNumberIWlanLinksPerSegment/IsochronousTiTo*) scoped by exact subnetName (Subnet.IoSystems) or by the interface device item (devicePath + itemPath: NetworkInterface.IoControllers[].IoSystem and IoConnectors[].ConnectedToIoSystem, with IoController SyncRole/PnDeviceNumber/Addresses and IoConnector PnUpdateTime*/PnWatchdog*/RtClass/SyncRole rows). Attribute failures are listed per name. Paginated, no modification.")]
        public CallToolResult ListIoSystems(string subnetName="", string[] devicePath = null!, string[] itemPath = null!, int offset=0, int limit=100)
            => HardwareToolContract.Invoke("ListIoSystems", true, false, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: true, optional: true);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                    if (string.IsNullOrEmpty(subnetName) == (devicePathJson == "[]"))
                        throw new ArgumentException("Give exactly one of subnetName or devicePath.", nameof(subnetName));
                }, describeArgument: true);
                return _hardware.ReadIoSystems(subnetName,devicePathJson,itemPathJson,offset,limit);
            });

        [McpServerTool(Name="ManageIoSystem"),
                Description("[L2][Hardware][WRITE] Official IO system actions on the exact interface device item: create (IoController.CreateIoSystem(name), interface must be on a subnet and own no IO system; empty name = TIA default; a name already used by an IO system of the project is refused because CreateIoSystem would attach to it silently; verified as one new IO system on the interface's subnet), delete (IoSystem.Delete, needs confirmDelete), update (properties Name/Number + attributes dynamic attributes, each read back), connect (IoConnector.ConnectToIoSystem of the IO system named ioSystemName on subnetName; also DP master systems) and disconnect (IoConnector.DisconnectFromIoSystem). Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageIoSystem(
            string[] devicePath,
            string[] itemPath,
            [Description("action: the operation to perform - create | delete | update | connect | disconnect.")] string action,
            string name="",
            string subnetName="",
            [Description("ioSystemName: exact IO system name.")] string ioSystemName="",
            AttributeMap<Scalar> properties = null!,
            AttributeMap<Scalar> attributes = null!,
            bool confirmDelete=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageIoSystem", dryRun, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string propertiesJson = HardwareToolContract.Map(properties, "properties", optional: true);
                string attributesJson = HardwareToolContract.Map(attributes, "attributes", optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareNetworkLogic.ValidateIoSystemRequest(action, name, subnetName, ioSystemName);
                    if (action == "delete") HardwareToolContract.Confirm(dryRun, confirmDelete);
                });
                return _hardware
                    .ManageIoSystem(devicePathJson,itemPathJson,action,name,subnetName,ioSystemName,propertiesJson,attributesJson,confirmDelete,dryRun);
            });

        [McpServerTool(Name="ListNetworkDomains"),
                Description("[L2][Hardware][READ] Domain management of one exact subnet: SyncDomainOwner.SyncDomains (Name, ConvertedName, IsDefault, participants, dynamic HighPerformanceActive/FastForwardingActive), MrpDomainOwner.MrpDomains (Name, participants, dynamic IsDefault/ManagerOutsideOfProjectActive) and the MrpInstances (ConnectedMrpDomain, RingPort1/2) of the subnet's interface items. Owner availability is reported; no modification.")]
        public CallToolResult ListNetworkDomains(string subnetName, int offset=0, int limit=100)
            => HardwareToolContract.Invoke("ListNetworkDomains", true, false, () =>
            {
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                    HardwareNetworkLogic.RequireExactName(subnetName, "subnetName");
                });
                return _hardware.ReadNetworkDomains(subnetName,offset,limit);
            });

        [McpServerTool(Name="ManageNetworkDomain"),
                Description("[L2][Hardware][WRITE] kind=sync/mrp on one exact subnet: create (SyncDomainComposition/MrpDomainComposition.Create(name)), delete (needs confirmDelete), update (properties Name/IsDefault + attributes dynamic attributes, read back) and addParticipant (DomainParticipants.Add of the NetworkInterface at participantDevicePath/participantItemPath; the API exposes no removal). Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageNetworkDomain(
            string subnetName,
            [Description("kind: sync | mrp.")] string kind,
            [Description("action: the operation to perform - create | delete | update | addParticipant.")] string action,
            string name,
            AttributeMap<Scalar> properties = null!,
            AttributeMap<Scalar> attributes = null!,
            [Description("participantDevicePath: array naming the station to add to the domain.")] string[] participantDevicePath = null!,
            [Description("participantItemPath: array of device-item names of the participant's interface.")] string[] participantItemPath = null!,
            bool confirmDelete=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageNetworkDomain", dryRun, true, () =>
            {
                string propertiesJson = HardwareToolContract.Map(properties, "properties", optional: true);
                string attributesJson = HardwareToolContract.Map(attributes, "attributes", optional: true);
                string participantDevicePathJson = HardwareToolContract.Path(participantDevicePath, "participantDevicePath", rootAllowed: true, optional: true);
                string participantItemPathJson = HardwareToolContract.Path(participantItemPath, "participantItemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareNetworkLogic.ValidateDomainRequest(kind, action, name);
                    if (action == "delete") HardwareToolContract.Confirm(dryRun, confirmDelete);
                    if (action == "addParticipant" && participantDevicePathJson == "[]")
                        throw new ArgumentException("addParticipant requires a nonempty participantDevicePath.", nameof(participantDevicePath));
                }, describeArgument: true);
                return _hardware
                    .ManageNetworkDomain(subnetName,kind,action,name,propertiesJson,attributesJson,participantDevicePathJson,participantItemPathJson,
                        confirmDelete,dryRun);
            });

        [McpServerTool(Name="ListTransferAreas"),
                Description("[L2][Hardware][READ] NetworkInterface.TransferAreas (I-device / DP slave / CP 16xx / PN/PN coupler: Name, Type, Direction, PositionNumber, ExtendedPositionNumber, LocalToPartnerLength, PartnerToLocalLength, LocalAddresses/PartnerAddresses rows, TransferAreaMappingRules, dynamic Comment/TransferUpdateTime/SharedDeviceAccessConfigured/UpdateAlarm/RecordIndex) and MulticastableTransferAreas (CCDX: DataLength, Comment, PartnerTransferAreas) of the exact interface device item; positionNumber[/extendedPositionNumber] uses native Find. Paginated, no modification.")]
        public CallToolResult ListTransferAreas(
            string[] devicePath,
            string[] itemPath,
            [Description("positionNumber: slot / position number of the module or item (-1 = not given).")] int positionNumber=-1,
            [Description("extendedPositionNumber: extended position number within the slot (-1 = not given).")] int extendedPositionNumber=-1,
            int offset=0,
            int limit=100)
            => HardwareToolContract.Invoke("ListTransferAreas", true, false, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                    HardwareNetworkLogic.RequirePosition(positionNumber, extendedPositionNumber);
                });
                return _hardware.ReadTransferAreas(devicePathJson,itemPathJson,positionNumber,extendedPositionNumber,offset,limit);
            });

        [McpServerTool(Name="ManageTransferArea"),
                Description("[L2][Hardware][WRITE] Transfer areas of the exact interface device item. kind=standard: create (TransferAreaComposition.Create(name, type[, positionNumber]); type is an official TransferAreaType such as IN/OUT/MS/CD/TM/F_PS and cannot be changed later), delete, update (properties Name/Direction/LocalToPartnerLength/PartnerToLocalLength + attributes), createMappingRule/updateMappingRule/deleteMappingRule (TransferAreaMappingRules: Begin/End/IoType/Offset via properties, Target via targetDevicePath/targetItemPath, ruleIndex for update/delete). kind=multicast (CCDX, DDX): create on the sender interface toward partnerDevicePath/partnerItemPath (optional name, length), createReceiver for senderName, delete (sender deletes all receivers), update (Name/Comment/DataLength). Target by exact name or positionNumber. Deletes need confirmDelete. Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageTransferArea(
            string[] devicePath,
            string[] itemPath,
            [Description("create | delete | update | createMappingRule | updateMappingRule | deleteMappingRule | createReceiver. ")] string action,
            [Description("kind: standard | multicast.")] string kind="standard",
            string name="",
            [Description("type: None | MS | CD | F_PS | TM | IN | OUT | MSI | MSO | MSO_LOCAL | RECORD_WRITE_STO | RECORD_WRITE_PUB | RECORD_READ_STO | RECORD_READ_PUB | MSI_MSO | IN_OUT | LOCAL_RECORD_STO | LOCAL_RECORD_PUB | SUB_MSI | SUB_MSO | SUB_LOCAL_RECORD_STO_READ | SUB_LOCAL_RECORD_PUB_READ | PROFISAFE_IN12_OUT6 | PROFISAFE_IN6_OUT12 | F_Proxy_CD | DDX | ISOC_STATUS_CONTROL | ISOCHRON_IN | ISOCHRON_OUT | F_CD.")] string type=
                "",
            [Description("positionNumber: slot / position number of the module or item (-1 = not given).")] int positionNumber=-1,
            [Description("extendedPositionNumber: extended position number within the slot (-1 = not given).")] int extendedPositionNumber=-1,
            string[] partnerDevicePath = null!,
            string[] partnerItemPath = null!,
            [Description("senderName: exact name of the sending transfer area.")] string senderName="",
            [Description("length: length in bytes.")] int length=-1,
            AttributeMap<Scalar> properties = null!,
            AttributeMap<Scalar> attributes = null!,
            [Description("ruleIndex: 0-based index of the mapping rule.")] int ruleIndex=-1,
            [Description("targetDevicePath: array naming the target station.")] string[] targetDevicePath = null!,
            [Description("targetItemPath: array of device-item names on the target.")] string[] targetItemPath = null!,
            bool confirmDelete=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageTransferArea", dryRun, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string partnerDevicePathJson = HardwareToolContract.Path(partnerDevicePath, "partnerDevicePath", rootAllowed: true, optional: true);
                string partnerItemPathJson = HardwareToolContract.Path(partnerItemPath, "partnerItemPath", rootAllowed: true, optional: true);
                string propertiesJson = HardwareToolContract.Map(properties, "properties", optional: true);
                string attributesJson = HardwareToolContract.Map(attributes, "attributes", optional: true);
                string targetDevicePathJson = HardwareToolContract.Path(targetDevicePath, "targetDevicePath", rootAllowed: true, optional: true);
                string targetItemPathJson = HardwareToolContract.Path(targetItemPath, "targetItemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareNetworkLogic.ValidateTransferAreaRequest(kind, action, name, type, positionNumber, extendedPositionNumber, length, ruleIndex);
                    if (action == "delete" || action == "deleteMappingRule") HardwareToolContract.Confirm(dryRun, confirmDelete);
                });
                return _hardware
                    .ManageTransferArea(devicePathJson,itemPathJson,action,kind,name,type,positionNumber,extendedPositionNumber,partnerDevicePathJson,
                        partnerItemPathJson,senderName,length,propertiesJson,attributesJson,ruleIndex,targetDevicePathJson,targetItemPathJson,confirmDelete,
                        dryRun);
            });

        [McpServerTool(Name="ListDeviceItemChannels"),
                Description("[L2][Hardware][READ] DeviceItem.Channels of the exact module: Number, Type (Analog/Digital/Technology), IoType (Input/Output/Complex), the attribute names from GetAttributeInfos and the values of ChannelAddress/ChannelWidth plus any attributeNames (failures listed per name). channelType+channelIoType+channelNumber together select one channel through native ChannelComposition.Find. Paginated, no modification. includeLinkedTags=true adds linkedTags per channel (PlcTagProvider.GetLinkedTags, V21: tag name, data type, logical address, tag table; V20 answers null with a note).")]
        public CallToolResult ListDeviceItemChannels(
            string[] devicePath,
            string[] itemPath,
            [Description("channelType: None | Analog | Digital | Technology.")] string channelType="",
            [Description("channelIoType: None | Input | Output | Complex.")] string channelIoType="",
            [Description("channelNumber: 0-based channel number.")] int channelNumber=-1,
            [Description("attributeNames: array of attribute names to read ('[]' = the documented set).")] string[] attributeNames = null!,
            int offset=0,
            int limit=100,
            [Description("includeLinkedTags: true also returns the PLC tags linked to each channel.")] bool includeLinkedTags=false)
            => HardwareToolContract.Invoke("ListDeviceItemChannels", true, false, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string attributeNamesJson = HardwareToolContract.Names(attributeNames, "attributeNames", 64, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                    HardwareNetworkLogic.ChannelIdentityGiven(channelType, channelIoType, channelNumber);
                });
                return _hardware
                    .ReadDeviceItemChannels(devicePathJson,itemPathJson,channelType,channelIoType,channelNumber,attributeNamesJson,offset,limit,
                        includeLinkedTags);
            });

        [McpServerTool(Name="SetDeviceItemChannel"),
                Description("[L2][Hardware][WRITE] SetAttribute on one exact channel (ChannelComposition.Find(channelType, channelIoType, channelNumber)) for the dynamic attributes in attributes; the CLR type is taken from the current value and every write is read back. Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SetDeviceItemChannel(
            string[] devicePath,
            string[] itemPath,
            [Description("channelType: None | Analog | Digital | Technology.")] string channelType,
            [Description("channelIoType: None | Input | Output | Complex.")] string channelIoType,
            [Description("channelNumber: 0-based channel number.")] int channelNumber,
            AttributeMap<Scalar> attributes,
            bool dryRun=true)
            => HardwareToolContract.Invoke("SetDeviceItemChannel", dryRun, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string attributesJson = HardwareToolContract.Map(attributes, "attributes", optional: false);
                HardwareToolContract.Check(() =>
                {
                    if (!HardwareNetworkLogic.ChannelIdentityGiven(channelType, channelIoType, channelNumber))
                        throw new ArgumentException("Give an exact channelType, channelIoType and nonnegative channelNumber.", nameof(channelType));
                    if (attributes.Count == 0) throw new ArgumentException("attributes must be a nonempty map of channel attributes.", nameof(attributes));
                }, describeArgument: true, parameter: nameof(channelType));
                return _hardware.UpdateDeviceItemChannel(devicePathJson,itemPathJson,channelType,channelIoType,channelNumber,attributesJson,dryRun);
            });

        [McpServerTool(Name="ManageDeviceUserGroup"),
                Description("[L2][Hardware][WRITE] Project device user groups (Project.DeviceGroups, DeviceUserGroup.Groups/Devices, UngroupedDevicesGroup): read (empty groupPath lists root devices, top-level groups and the ungrouped system group; a path lists that group), create (DeviceUserGroupComposition.Create, missing parents created), rename (one segment) and deleteEmpty. groupPath is an exact relative nested path. CreateFrom(MasterCopy) is not exposed. Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageDeviceUserGroup(
            string groupPath="",
            [Description("action: the operation to perform - read | create | rename | deleteEmpty.")] string action="read",
            string newName="",
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageDeviceUserGroup", dryRun || action == "read", action != "read", () =>
            {
                HardwareToolContract.Check(() =>
                {
                    HardwareNetworkLogic.RequireOneOf(action, HardwareNetworkLogic.DeviceGroupActions, "action");
                });
                return _hardware.ManageDeviceUserGroup(groupPath,action,newName,dryRun);
            });



        [McpServerTool(Name="ManagePortInterconnection"),
                Description("[L2][Hardware][WRITE] Topology of one exact port device item (NetworkPort service): read lists ConnectedPorts with owner paths; connect/disconnect call NetworkPort.ConnectToPort/DisconnectFromPort with the partner port at partnerDevicePath/partnerItemPath and verify the ConnectedPorts count. TIA refuses ports of the same interface and second partners on ports without alternative partners. Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePortInterconnection(
            string[] devicePath,
            string[] itemPath,
            [Description("action: the operation to perform - read | connect | disconnect.")] string action="read",
            string[] partnerDevicePath = null!,
            string[] partnerItemPath = null!,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManagePortInterconnection", dryRun || action == "read", action != "read", () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string partnerDevicePathJson = HardwareToolContract.Path(partnerDevicePath, "partnerDevicePath", rootAllowed: true, optional: true);
                string partnerItemPathJson = HardwareToolContract.Path(partnerItemPath, "partnerItemPath", rootAllowed: true, optional: true);
                HardwareToolContract.Check(() =>
                {
                    ValidatePortAction(action, partnerDevicePath);
                }, describeArgument: true);
                return _hardware.ManagePortInterconnection(devicePathJson,itemPathJson,action,partnerDevicePathJson,partnerItemPathJson,dryRun);
            });

        internal static void ValidatePortAction(string action, string[]? partnerDevicePath)
        {
            HardwareNetworkLogic.RequireOneOf(action, HardwareNetworkLogic.PortActions, "action");
            if (action != "read" && (partnerDevicePath == null || partnerDevicePath.Length == 0))
                throw new ArgumentException("connect and disconnect require a nonempty partnerDevicePath.", nameof(partnerDevicePath));
        }

        [McpServerTool(Name = "GetDeviceItemNetworkInfo"),
                Description("[L2][Hardware]Get network-related attributes for a device item (best-effort heuristic filter)")]
        public CallToolResult GetDeviceItemNetworkInfo(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
            => HardwareToolContract.Invoke("GetDeviceItemNetworkInfo", true, false, () =>
            {
                return GetDeviceItemNetworkInfoCore(deviceItemPath);
            });

        [McpServerTool(Name = "ConnectDeviceNodesToProfinetSubnet"),
                Description("[L1][Hardware] PREFERRED for PROFINET network setup. Finds the first IE/PROFINET node under two devices and connects both to subnetName: an existing subnet of that name in Project.Subnets is joined explicitly, otherwise it is created on the first node (Node.CreateAndConnectToSubnet). A node already attached to another subnet is refused before any write; success is the Node.ConnectedSubnet read-back of both nodes, and the HMI connections are read from the HMI software under the second root. Requires: ConnectPortal + OpenProject + both devices added. Typical: firstRootPath='PLC_1', secondRootPath='HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'. Verified for S7-1200 + KTP700 Basic PN. Native behaviorPolicy=current; V4 native acceptance is pending."
                        +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; " +
            "execution requires dryRun=false, expectedPlanHash from this preview, and the exact bound expectedProjectFile. " +
            "Existing approval and native checks remain.")]
        public CallToolResult ConnectDeviceNodesToProfinetSubnet(
            [Description("firstRootPath: first device/device-item root, usually PLC root, e.g. 'PLC_1'")] string firstRootPath,
            [Description("secondRootPath: second device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string secondRootPath,
            [Description("subnetName: subnet name to create when the first node is not already connected, e.g. 'PN_IE_1'")] string subnetName = "PN_IE_1",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when dryRun=false.")]
            string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("ConnectDeviceNodesToProfinetSubnet", dryRun, new { firstRootPath, secondRootPath, subnetName }, () => HardwareToolContract
                    .Invoke("ConnectDeviceNodesToProfinetSubnet", false, true, () =>
            {
                HardwareToolContract.Check(() => {
                    HardwareNetworkLogic.RequireExactName(subnetName, "subnetName");
                    if (subnetName.Any(char.IsControl)) throw new ArgumentException("subnetName must not contain control characters.", nameof(subnetName));
                });
                return ConnectDeviceNodesToProfinetSubnetCore(firstRootPath, secondRootPath, subnetName);
            }), expectedPlanHash, expectedProjectFile);

        [McpServerTool(Name = "PlanHardwareNetworkConfiguration"),
                Description("[L2][Hardware][OFFLINE] Validate a hardware network operation plan without connecting to TIA Portal or modifying a project. Use this before EnsureSubnet/AttachDeviceNodeToSubnet/SetPlcCpuSettings; rejects guessed paths, unsafe subnet types, invalid IP/mask/gateway, and CPU settings without exactAttributes.")]
        public CallToolResult PlanHardwareNetworkConfiguration(
            [Description("plan: NetworkPlan with an operations array. Supported operation types: EnsureSubnet, AttachDeviceNodeToSubnet, SetPlcCpuSettings. This is offline-only and performs validation only.")] NetworkPlan plan)
            => HardwareToolContract.Invoke("PlanHardwareNetworkConfiguration", true, false, () =>
            {
                string planJson = V4Json.Serialize(plan);
                return PlanHardwareNetworkConfigurationCore(planJson);
            });

        [McpServerTool(Name = "EnsureSubnet"),
                Description("[L2][Hardware] Ensure an Industrial Ethernet/PROFINET subnet by anchoring on a real deviceItemPath from GetProjectTree/GetDeviceItemTree. An existing subnet of that name in Project.Subnets is reported without changes; otherwise the anchor's free IE node creates it (Node.CreateAndConnectToSubnet). An anchor node attached to another subnet is refused. Returns readback evidence (Project.Subnets, Node.ConnectedSubnet, node path, interface path). Does not guess paths. Native behaviorPolicy=current; V4 native acceptance is pending."
                        +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; " +
            "execution requires dryRun=false, expectedPlanHash from this preview, and the exact bound expectedProjectFile. " +
            "Existing approval and native checks remain.")]
        public CallToolResult EnsureSubnet(
            [Description("anchorDeviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree; used to create/reuse the subnet from its first PROFINET node.")] string anchorDeviceItemPath,
            [Description("subnetType: IndustrialEthernet/PROFINET/PN/IE only.")] string subnetType,
            [Description("subnetName: exact subnet name to create or read back, e.g. PN_IE_1.")] string subnetName,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when dryRun=false.")]
            string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("EnsureSubnet", dryRun, new { anchorDeviceItemPath, subnetType, subnetName }, () => HardwareToolContract
                    .Invoke("EnsureSubnet", false, true, () =>
            {
                return EnsureSubnetCore(anchorDeviceItemPath, subnetType, subnetName);
            }), expectedPlanHash, expectedProjectFile);

        [McpServerTool(Name = "AttachDeviceNodeToSubnet"),
                Description("[L2][Hardware] Attach one real device network node to an existing PROFINET subnet (found in Project.Subnets) with Node.ConnectToSubnet and verify Node.ConnectedSubnet. A node already on that subnet is left unchanged; a node attached to another subnet is refused (no implicit disconnect). deviceItemPath must come from GetProjectTree/GetDeviceItemTree, interfaceIndex selects a discovered Industrial Ethernet/PROFINET node, and online/force operations are never used. Native behaviorPolicy=current; V4 native acceptance is pending."
                        +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; " +
            "execution requires dryRun=false, expectedPlanHash from this preview, and the exact bound expectedProjectFile. " +
            "Existing approval and native checks remain.")]
        public CallToolResult AttachDeviceNodeToSubnet(
            [Description("deviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string deviceItemPath,
            [Description("interfaceIndex: zero-based index among discovered Industrial Ethernet/PROFINET nodes under deviceItemPath.")] int interfaceIndex,
            [Description("subnetName: existing subnet name to attach to.")] string subnetName,
            [Description("anchorDeviceItemPath: optional real device-item path used to EnsureSubnet first when subnetName is not found.")] string anchorDeviceItemPath =
                "",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when dryRun=false.")]
            string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("AttachDeviceNodeToSubnet", dryRun, new { deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath }, () =>
                    HardwareToolContract.Invoke("AttachDeviceNodeToSubnet", false, true, () =>
            {
                return AttachDeviceNodeToSubnetCore(deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath);
            }), expectedPlanHash, expectedProjectFile);

        [McpServerTool(Name = "ProbeHardwareHmiConnectionOwnerCandidates"),
                Description("[L2][Hardware]Enumerate candidate owner objects for hardware HMI connection creation without calling GetService on each high-level object.")]
        public CallToolResult ProbeHardwareHmiConnectionOwnerCandidatesV4(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include ancestor/project/device-level candidates; when false only direct node/interface/device-item candidates")] bool deepScan =
                true)
            => HardwareToolContract.Invoke("ProbeHardwareHmiConnectionOwnerCandidates", true, false, () =>
            {
                return ProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);
            });

        [McpServerTool(Name = "ProbeHardwareHmiConnectionWhitelistedServices"),
                Description("[L2][Hardware]Read-only scan of whitelisted services on safe hardware HMI connection owner candidates. Does not create connections and skips high-level project/composition objects to avoid hangs.")]
        public CallToolResult ProbeHardwareHmiConnectionWhitelistedServicesV4(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include safe ancestors; when false only direct node/interface/device-item candidates")] bool deepScan = true)
            => HardwareToolContract.Invoke("ProbeHardwareHmiConnectionWhitelistedServices", true, false, () =>
            {
                return ProbeHardwareHmiConnectionWhitelistedServices(plcRootPath, hmiRootPath, deepScan);
            });

        [McpServerTool(Name = "GetProjectTopology"), Description(
            "[L1][Category:Hardware][PreCondition:ConnectPortal+OpenProject]" +
            " One-shot, read-only project topology from Openness: every device (root, device groups and ungrouped devices, with groupPath) with its network nodes (IP, subnet, node type)."
                +
            " Call this early to understand the project's devices and subnets at a glance, instead of probing S7 or parsing AML.")]
        public CallToolResult GetProjectTopology()
            => HardwareToolContract.Invoke("GetProjectTopology", true, false, () =>
            {
                return GetProjectTopologyCore();
            });

        // Unregistered adapters retain the existing domain response construction.
        private ResponseNetworkInfo GetDeviceItemNetworkInfoCore(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var reply = _hardware.GetDeviceItemNetworkInfo(deviceItemPath);
                if (reply is JsonObject refusal && (string?)refusal["failureCode"] == "PROJECT_NOT_BOUND")
                {
                    var unbound = ResponseMeta.Basic(DateTime.Now, false);
                    unbound["failureCode"] = "PROJECT_NOT_BOUND";
                    return new ResponseNetworkInfo { Message = "No project is bound.", Meta = unbound };
                }
                var attrs = reply == null ? null : JsonSerializer.Deserialize<List<NetworkAttribute>>(reply.ToJsonString());
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

                var missing = ResponseMeta.Basic(DateTime.Now, false);
                missing["failureCode"] = "NOT_FOUND";
                missing["target"] = "/deviceItemPath";
                return new ResponseNetworkInfo
                {
                    Message = "The device item was not found.",
                    Meta = missing
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving network info from '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

        private ResponseMessage ConnectDeviceNodesToProfinetSubnetCore(
            [Description("firstRootPath: first device/device-item root, usually PLC root, e.g. 'PLC_1'")] string firstRootPath,
            [Description("secondRootPath: second device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string secondRootPath,
            [Description("subnetName: subnet name to create when the first node is not already connected, e.g. 'PN_IE_1'")] string subnetName = "PN_IE_1")
        {
            try
            {
                var report = _hardware.ProbeConnectDeviceNodesToSubnet(firstRootPath, secondRootPath, subnetName);
                string resultLine = report.Split('\n').Select(line => line.TrimEnd('\r'))
                    .LastOrDefault(line => line.StartsWith("Result: ", StringComparison.Ordinal)) ?? "";
                bool success = resultLine.StartsWith("Result: ConnectToSubnet: OK", StringComparison.Ordinal);
                bool missing = resultLine == "Result: NOT CONNECTED: a root path was not found; nothing was changed.";
                bool unchanged = missing || resultLine == "Result: NOT CONNECTED: no Industrial Ethernet/PROFINET node under both roots; nothing was changed."
                    || resultLine.StartsWith("Result: NOT CONNECTED: ", StringComparison.Ordinal)
                        && resultLine.EndsWith("; disconnect it explicitly first. Nothing was changed.", StringComparison.Ordinal);
                bool invalidSubnet = resultLine.StartsWith("Result: NOT CONNECTED: invalid subnetName:", StringComparison.Ordinal);
                var meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = success,
                    ["firstRootPath"] = firstRootPath,
                    ["secondRootPath"] = secondRootPath,
                    ["subnetName"] = subnetName,
                    ["report"] = report
                };
                meta["mayHaveChanged"] = !unchanged && !invalidSubnet;
                if (unchanged)
                {
                    meta["failureCode"] = missing ? "NOT_FOUND" : "PRECONDITION_FAILED";
                    if (missing) meta["target"] = report.IndexOf("PLC root: " + firstRootPath + " -> <not found>", StringComparison.Ordinal) >=
                        0 ? firstRootPath : secondRootPath;
                }
                else if (invalidSubnet) meta["failureCode"] = "INVALID_ARGUMENT";
                const string rollbackMarker = "\nRollback evidence: ";
                int rollbackAt = report.LastIndexOf(rollbackMarker, StringComparison.Ordinal);
                if (rollbackAt >= 0 && JsonNode.Parse(report.Substring(rollbackAt + rollbackMarker.Length)) is JsonObject rollback)
                    foreach (var pair in rollback) meta[pair.Key] = pair.Value?.DeepClone();
                if (!_hardware.HasProject)
                {
                    meta["failureCode"] = "PROJECT_NOT_BOUND";
                    meta["mayHaveChanged"] = false;
                }
                return new ResponseMessage
                {
                    Message = success
                        ? "Device nodes connected to PROFINET subnet"
                        : "Device node PROFINET subnet connection did not complete",
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error connecting device nodes to PROFINET subnet: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        private ResponseJsonReport PlanHardwareNetworkConfigurationCore(
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
                throw new McpException($"Unexpected error validating hardware network plan: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        private ResponseMessage EnsureSubnetCore(
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

        private ResponseMessage AttachDeviceNodeToSubnetCore(
            [Description("deviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string deviceItemPath,
            [Description("interfaceIndex: zero-based index among discovered Industrial Ethernet/PROFINET nodes under deviceItemPath.")] int interfaceIndex,
            [Description("subnetName: existing subnet name to attach to.")] string subnetName,
            [Description("anchorDeviceItemPath: optional real device-item path used to EnsureSubnet first when subnetName is not found.")] string anchorDeviceItemPath =
                "")
        {
            try
            {
                return _hardware.AttachDeviceNodeToSubnet(deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching device node to subnet '{subnetName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        public ResponseStringList ProbeHardwareHmiConnectionOwnerCandidates(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include ancestor/project/device-level candidates; when false only direct node/interface/device-item candidates")] bool deepScan =
                true)
        {
            try
            {
                var items = _hardware.ProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection owner candidates enumerated",
                    Items = items,
                    Meta = ResponseMeta.Basic(DateTime.Now, (items as HardwareProbeEvidence)?.FailureCode == null,
                        ("failureCode", (items as HardwareProbeEvidence)?.FailureCode), ("target", (items as HardwareProbeEvidence)?.Target))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing hardware HMI connection owner candidates: {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

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
                    Meta = ResponseMeta.Basic(DateTime.Now, (items as HardwareProbeEvidence)?.FailureCode == null,
                        ("failureCode", (items as HardwareProbeEvidence)?.FailureCode), ("target", (items as HardwareProbeEvidence)?.Target))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error scanning hardware HMI connection whitelisted services: {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

        private ResponseJsonReport GetProjectTopologyCore()
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
