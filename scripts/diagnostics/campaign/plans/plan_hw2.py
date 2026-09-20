# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; PN = [P, "PROFINET 接口_1"]
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("GetDeviceItemNetworkInfo", deviceItemPath=P + "/" + P + "/PROFINET 接口_1", expect="any"),
    S("GetDeviceItemTree", deviceItemPath=P + "/导轨_0", maxDepth=2, expect="any"),
    S("GetDevicePlugLocations", deviceItemPath=P + "/导轨_0", plugOnDevice=False, expect="any", keys=["free", "occupied"]),
    S("PlugDeviceItem", deviceItemPath=P + "/导轨_0", orderNumber="6ES7 521-1BH00-0AB0", version="V2.2", positionNumber=2, name="MCP_DI", dryRun=False, plugOnDevice=False, expect="any", keys=["result"]),
    S("GetDeviceItemTree", deviceItemPath=P + "/导轨_0", maxDepth=2, expect="any"),
    S("GetDeviceItemIoAddresses", deviceItemPath=P + "/MCP_DI", expect="any", keys=["addresses"]),
    S("SetDeviceItemIoAddress", deviceItemPath=P + "/MCP_DI", ioType="Input", startAddress=20, dryRun=False, expect="any", keys=["after"]),
    S("UpdateDeviceAddress", devicePathJson=J([P]), itemPathJson=J(["MCP_DI"]), ioType="Input", startAddress=20, propertiesJson=J({"StartAddress": 30}), attributesJson="{}", softwarePath="", processImageObName="", dryRun=False, expect="any", keys=["after"]),
    S("ReadDeviceItemChannels", devicePathJson=J([P]), itemPathJson=J(["MCP_DI"]), channelType="", channelIoType="", channelNumber=-1, attributeNamesJson="[]", offset=0, limit=20, includeLinkedTags=True, expect="any", keys=["rows"]),
    S("UpdateDeviceItemChannel", devicePathJson=J([P]), itemPathJson=J(["MCP_DI"]), channelType="Digital", channelIoType="Input", channelNumber=0, attributesJson=J({"InputDelay": "3.2"}), dryRun=True, expect="any"),
    S("SetDeviceItemAttribute", deviceItemPath=P + "/MCP_DI", attributeName="Comment", value="MCP DI", expect="any"),
    S("ReadDeviceAddressing", devicePathJson=J([P]), itemPathJson=J(["MCP_DI"]), offset=0, limit=20, expect="any", keys=["rows"]),
    S("ManageHardwareObject", devicePathJson=J([P]), action="copyItem", itemPathJson=J(["MCP_DI"]), destinationDevicePathJson=J([P]), destinationItemPathJson=J([]), position=3, dryRun=True, expect="any"),
    S("ManageHardwareObject", devicePathJson=J([P]), action="moveItem", itemPathJson=J(["MCP_DI"]), destinationDevicePathJson=J([P]), destinationItemPathJson=J([]), position=4, dryRun=True, expect="any"),
    # network: subnet, connect, io system, domains, ports, transfer areas, connections
    S("EnsureSubnet", anchorDeviceItemPath=P + "/" + P + "/PROFINET 接口_1", subnetType="PROFINET", subnetName="MCP_PN", expect="any", keys=["subnetName", "nodePath"]),
    S("ConnectDeviceNodesToProfinetSubnet", firstRootPath=P, secondRootPath="MCP_TP700", subnetName="MCP_PN", expect="any", keys=["subnetName", "detail"]),
    S("AttachDeviceNodeToSubnet", deviceItemPath="MCP_S120", interfaceIndex=0, subnetName="MCP_PN", anchorDeviceItemPath=P + "/" + P + "/PROFINET 接口_1", expect="any"),
    S("GetProjectTopology"),
    S("ReadIoSystems", devicePathJson=J([P]), itemPathJson=J(PN), expect="any", keys=["records"]),
    S("ManageIoSystem", devicePathJson=J([P]), itemPathJson=J(PN), action="create", name="MCP_IO", subnetName="MCP_PN", ioSystemName="", propertiesJson="{}", attributesJson="{}", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ReadIoSystems", devicePathJson=J([P]), itemPathJson=J(PN), expect="any", keys=["records"]),
    S("ReadNetworkDomains", subnetName="MCP_PN", offset=0, limit=20, expect="any", keys=["records"]),
    S("ManageNetworkDomain", subnetName="MCP_PN", kind="sync", action="create", name="MCP_Sync", propertiesJson="{}", attributesJson="{}", participantDevicePathJson="[]", participantItemPathJson="[]", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ReadNetworkDomains", subnetName="MCP_PN", offset=0, limit=20, expect="any", keys=["records"]),
    S("ManageNetworkDomain", subnetName="MCP_PN", kind="sync", action="delete", name="MCP_Sync", propertiesJson="{}", attributesJson="{}", participantDevicePathJson="[]", participantItemPathJson="[]", confirmDelete=True, dryRun=False, expect="any"),
    S("ReadTransferAreas", devicePathJson=J([P]), itemPathJson=J(PN), expect="any"),
    S("ManagePortInterconnection", devicePathJson=J([P]), itemPathJson=J([P, "PROFINET 接口_1", "端口_1"]), action="read", partnerDevicePathJson="[]", partnerItemPathJson="[]", dryRun=True, expect="any"),
    S("ManagePortInterconnection", devicePathJson=J([P]), itemPathJson=J([P, "PROFINET 接口_1", "端口_1"]), action="connect", partnerDevicePathJson=J(["MCP_TP700"]), partnerItemPathJson=J(["MCP_TP700", "MCP_TP700.IE_CP_1", "端口_1"]), dryRun=False, expect="any", keys=["after"]),
    S("ManagePortInterconnection", devicePathJson=J([P]), itemPathJson=J([P, "PROFINET 接口_1", "端口_1"]), action="disconnect", partnerDevicePathJson="[]", partnerItemPathJson="[]", dryRun=False, expect="any"),
    S("ReadCommunicationConnections", devicePathJson=J([P]), itemPathJson=J([P]), offset=0, limit=20, expect="any", keys=["records"]),
    S("ManageDeviceServiceObjects", devicePathJson=J([P]), itemPathJson=J([P]), family="webApplications", action="read", name="", propertiesJson="{}", filePath="", confirmChange=False, dryRun=True, expect="any", keys=["records"]),
    S("ManageDeviceServiceObjects", devicePathJson=J([P]), itemPathJson=J([P]), family="syslog", action="read", name="", propertiesJson="{}", filePath="", confirmChange=False, dryRun=True, expect="any", keys=["records"]),
    S("ExchangeSystemDiagnosticsSettings", action="export", filePath=D + r"\mcp46_sysdiag.xml", devicePathJson=J([P]), itemPathJson=J([P]), confirmImport=False, dryRun=False, expect="any", keys=["file"]),
    S("BuildDeviceAmlDocument", specJson=J({"projectName": "P", "devices": [{"name": "MCP_AML_PLC", "typeIdentifier": "OrderNumber:6ES7 515-2FM02-0AB0/V2.9", "deviceItems": [{"name": "Rail_0", "typeIdentifier": "OrderNumber:6ES7 590-1AB60-0AA0", "positionNumber": 0, "deviceItems": [{"name": "PLC_1", "typeIdentifier": "OrderNumber:6ES7 515-2FM02-0AB0/V2.9", "positionNumber": 1}]}]}]}), outputPath=D + r"\mcp46_built.aml", referenceAmlPath="", expect="any"),
    S("ManageHardwareObject", devicePathJson=J([P]), action="deleteItem", itemPathJson=J(["MCP_DI"]), destinationDevicePathJson="[]", destinationItemPathJson="[]", position=0, dryRun=False, expect="any", keys=["verifiedAbsent"]),
    S("ManageIoSystem", devicePathJson=J([P]), itemPathJson=J(PN), action="delete", name="", subnetName="", ioSystemName="MCP_IO", propertiesJson="{}", attributesJson="{}", confirmDelete=True, dryRun=False, expect="any"),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_hw2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
