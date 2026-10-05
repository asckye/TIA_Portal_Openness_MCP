# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; PN = [P, "PROFINET 接口_1"]
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("GetDeviceItemNetworkInfo", deviceItemPath=P + "/" + P + "/PROFINET 接口_1", expect="any"),
    S("GetDeviceItemTree", deviceItemPath=P + "/导轨_0", maxDepth=2, expect="any"),
    S('GetDevicePlugLocations', note='Typed boolean false', expect='any', keys=['free', 'occupied'], deviceItemPath='MCP_PLC/导轨_0', plugOnDevice=False),
    S("PlugDeviceItem", deviceItemPath=P + "/导轨_0", orderNumber="6ES7 521-1BH00-0AB0", version="V2.2", positionNumber=2, name="MCP_DI", dryRun=False, plugOnDevice=False, expect="any", keys=["result"]),
    S("GetDeviceItemTree", deviceItemPath=P + "/导轨_0", maxDepth=2, expect="any"),
    S("GetDeviceItemIoAddresses", deviceItemPath=P + "/MCP_DI", expect="any", keys=["addresses"]),
    S("SetDeviceItemIoAddress", deviceItemPath=P + "/MCP_DI", ioType="Input", startAddress=20, dryRun=False, expect="any", keys=["after"]),
    S("SetDeviceAddress", devicePath=[P], itemPath=["MCP_DI"], ioType="Input", startAddress=20, properties={"StartAddress": 30}, attributes={}, softwarePath="", processImageObName="", dryRun=False, expect="any", keys=["after"]),
    S("ListDeviceItemChannels", devicePath=[P], itemPath=["MCP_DI"], channelType="", channelIoType="", channelNumber=-1, attributeNames=[], offset=0, limit=20, includeLinkedTags=True, expect="any", keys=["rows"]),
    S("SetDeviceItemChannel", devicePath=[P], itemPath=["MCP_DI"], channelType="Digital", channelIoType="Input", channelNumber=0, attributes={"InputDelay": "3.2"}, dryRun=True, expect="any"),
    S("SetDeviceItemAttribute", deviceItemPath=P + "/MCP_DI", attributeName="Comment", value="MCP DI", expect="any"),
    S("GetDeviceAddressing", devicePath=[P], itemPath=["MCP_DI"], offset=0, limit=20, expect="any", keys=["rows"]),
    S("ManageHardwareObject", devicePath=[P], action="copyItem", itemPath=["MCP_DI"], destinationDevicePath=[P], destinationItemPath=[], position=3, dryRun=True, expect="any"),
    S("ManageHardwareObject", devicePath=[P], action="moveItem", itemPath=["MCP_DI"], destinationDevicePath=[P], destinationItemPath=[], position=4, dryRun=True, expect="any"),
    # network: subnet, connect, io system, domains, ports, transfer areas, connections
    S("EnsureSubnet", anchorDeviceItemPath=P + "/" + P + "/PROFINET 接口_1", subnetType="PROFINET", subnetName="MCP_PN", expect="any", keys=["subnetName", "nodePath"]),
    S("ConnectDeviceNodesToProfinetSubnet", firstRootPath=P, secondRootPath="MCP_TP700", subnetName="MCP_PN", expect="any", keys=["subnetName", "detail"]),
    S("AttachDeviceNodeToSubnet", deviceItemPath="MCP_S120", interfaceIndex=0, subnetName="MCP_PN", anchorDeviceItemPath=P + "/" + P + "/PROFINET 接口_1", expect="any"),
    S("GetProjectTopology"),
    S("ListIoSystems", devicePath=[P], itemPath=PN, expect="any", keys=["records"]),
    S("ManageIoSystem", devicePath=[P], itemPath=PN, action="create", name="MCP_IO", subnetName="MCP_PN", ioSystemName="", properties={}, attributes={}, confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ListIoSystems", devicePath=[P], itemPath=PN, expect="any", keys=["records"]),
    S('ListNetworkDomains', note='', expect='any', keys=['records'], subnetName='MCP_PN'),
    S("ManageNetworkDomain", subnetName="MCP_PN", kind="sync", action="create", name="MCP_Sync", properties={}, attributes={}, participantDevicePath=[], participantItemPath=[], confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S('ListNetworkDomains', note='', expect='any', keys=['records'], subnetName='MCP_PN'),
    S("ManageNetworkDomain", subnetName="MCP_PN", kind="sync", action="delete", name="MCP_Sync", properties={}, attributes={}, participantDevicePath=[], participantItemPath=[], confirmDelete=True, dryRun=False, expect="any"),
    S("ListTransferAreas", devicePath=[P], itemPath=PN, expect="any"),
    S("ManagePortInterconnection", devicePath=[P], itemPath=[P, "PROFINET 接口_1", "端口_1"], action="read", partnerDevicePath=[], partnerItemPath=[], dryRun=True, expect="any"),
    S("ManagePortInterconnection", devicePath=[P], itemPath=[P, "PROFINET 接口_1", "端口_1"], action="connect", partnerDevicePath=["MCP_TP700"], partnerItemPath=["MCP_TP700", "MCP_TP700.IE_CP_1", "端口_1"], dryRun=False, expect="any", keys=["after"]),
    S("ManagePortInterconnection", devicePath=[P], itemPath=[P, "PROFINET 接口_1", "端口_1"], action="disconnect", partnerDevicePath=[], partnerItemPath=[], dryRun=False, expect="any"),
    S("ListCommunicationConnections", devicePath=[P], itemPath=[P], offset=0, limit=20, expect="any", keys=["records"]),
    S("ManageDeviceServiceObjects", devicePath=[P], itemPath=[P], family="webApplications", action="read", name="", properties={}, filePath="", confirmChange=False, dryRun=True, expect="any", keys=["records"]),
    S('ManageSyslogServers', note='', expect='any', keys=['records'], devicePath=['MCP_PLC'], itemPath=['MCP_PLC'], action='read', name='', properties={}, dryRun=True, scope='plc'),
    S("ExchangeSystemDiagnosticsSettings", action="export", filePath=D + r"\mcp46_sysdiag.xml", devicePath=[P], itemPath=[P], confirmImport=False, dryRun=False, expect="any", keys=["file"]),
    S("BuildDeviceAmlDocument", spec={"projectName": "P", "devices": [{"name": "MCP_AML_PLC", "typeIdentifier": "OrderNumber:6ES7 515-2FM02-0AB0/V2.9", "deviceItems": [{"name": "Rail_0", "typeIdentifier": "OrderNumber:6ES7 590-1AB60-0AA0", "positionNumber": 0, "deviceItems": [{"name": "PLC_1", "typeIdentifier": "OrderNumber:6ES7 515-2FM02-0AB0/V2.9", "positionNumber": 1}]}]}]}, outputPath=D + r"\mcp46_built.aml", referenceAmlPath="", expect="any"),
    S("ManageHardwareObject", devicePath=[P], action="deleteItem", itemPath=["MCP_DI"], destinationDevicePath=[], destinationItemPath=[], position=0, dryRun=False, expect="any", keys=["verifiedAbsent"]),
    S("ManageIoSystem", devicePath=[P], itemPath=PN, action="delete", name="", subnetName="", ioSystemName="MCP_IO", properties={}, attributes={}, confirmDelete=True, dryRun=False, expect="any"),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_hw2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
