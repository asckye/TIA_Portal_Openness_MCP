# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"; U = "HMI_RT_2"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    # offline builders
    S("BuildPlcUdtXml", udtJson=J({"name": "MCP_UDT2", "members": [{"name": "A", "datatype": "Bool"}]}), keys=["udtName"]),
    S("BuildPlcTagTableXml", tagTableJson=J({"tableName": "MCP_T2", "tags": [{"name": "T1", "dataTypeName": "Bool", "logicalAddress": "%I1.0"}]})),
    S("BuildPlcGlobalDbXml", globalDbJson=J({"dbName": "MCP_DB2", "dbNumber": 501, "staticMembers": [{"name": "X", "datatype": "Int"}]})),
    S("BuildStructuredTextXml", structuredTextJson=J({"operations": [{"op": "assignment", "target": "#a", "value": "1"}]}), innerOnly=True),
    S("ComposePlcFcBlockXml", fcBlockJson=J({"blockName": "MCP_FC2", "blockNumber": 502, "inputs": [{"name": "A", "datatype": "Int"}], "outputs": [{"name": "B", "datatype": "Int"}], "structuredText": {"operations": [{"op": "assignment", "target": "#B", "source": "#A"}]}})),
    S("ComposePlcFbBlockXml", fbBlockJson=J({"blockName": "MCP_FB2", "blockNumber": 502, "inputs": [{"name": "A", "datatype": "Bool"}], "statics": [{"name": "S", "datatype": "Int"}], "structuredText": {"operations": [{"op": "assignment", "target": "#S", "value": "1"}]}})),
    S("BuildFlgNetCallXml", flgNetJson=J({"callName": "MCP_FC", "parameters": [{"name": "IN_A", "section": "Input", "dataType": "Int", "value": "1"}, {"name": "IN_B", "section": "Input", "dataType": "Int", "value": "2"}, {"name": "OUT_Sum", "section": "Output", "dataType": "Int", "symbol": "MCP_DB.Counter"}]}), expect="any"),
    S("ComposePlcLadFcBlockXml", ladFcBlockJson=J({"blockName": "MCP_LAD", "blockNumber": 503, "networks": [{"callJson": {"callName": "MCP_FC", "parameters": [{"name": "IN_A", "section": "Input", "dataType": "Int", "value": "1"}, {"name": "IN_B", "section": "Input", "dataType": "Int", "value": "2"}, {"name": "OUT_Sum", "section": "Output", "dataType": "Int", "symbol": "\"MCP_DB\".Counter"}]}, "titleZhCn": "调用"}]}), expect="any"),
    S("BuildPlcSymbolManifestFromXmlPath", path=D + r"\mcp46_tags.xml", expect="any"),
    S("BuildClassicHmiTagTableXml", tableJson=J({"name": "MCP_CT", "tags": [{"name": "A", "dataType": "Bool"}]})),
    S("BuildClassicHmiScreenXml", designJson=J({"screen": {"name": "MCP_CS"}, "items": [{"type": "Text", "name": "T", "text": "x"}]})),
    # exports store
    S("DumpDeviceAttributes", devicePath=P, nameFilter="", keys=["exportId"]),
    {"tool": "ListExports", "args": {"tool": "", "limit": 5}, "expect": "ok", "note": "", "keys": ["exports"]},
    # opc ua
    S("GetOpcUaConfig", softwarePath=P, keys=["serverInterfaces", "values"]),
    S("ExportOpcUaInterface", softwarePath=P, interfaceName="MCP_Srv", exportPath=D + r"\mcp46_opcua.xml", interfaceType="server", expect="any"),
    S("ImportOpcUaInterface", softwarePath=P, importPath=D + r"\mcp46_opcua.xml", interfaceType="server", expect="any"),
    S("ManageOpcUaAccessControl", softwarePath=P, action="read", roleName="", definedInNamespace="", projectRole="", namespaceUri="", permission="", enabled=False, propertiesJson="{}", confirmChange=False, dryRun=True, expect="any"),
    # tag table delete round trip
    S("PlcBuildAndImport", softwarePath=P, kind="tagtable", json=J({"tableName": "MCP_Tmp", "tags": [{"name": "Tmp1", "dataTypeName": "Bool", "logicalAddress": "%I3.0"}]}), typeGroupPath="", tagFolderPath="MCP_Tags", blockGroupPath="", compileAfter=False, dryRun=False),
    S("DeletePlcTagTable", softwarePath=P, tagTableName="MCP_Tmp", dryRun=True, keys=["tags"]),
    S("DeletePlcTagTable", softwarePath=P, tagTableName="MCP_Tmp", dryRun=False, keys=["verifiedAbsent"]),
    # transfer areas / sivarc
    S("ManageTransferArea", devicePathJson=J([P]), itemPathJson=J([P, "PROFINET 接口_1"]), action="read", kind="", name="", type="", positionNumber=-1, extendedPositionNumber=-1, partnerDevicePathJson="[]", partnerItemPathJson="[]", senderName="", length=0, propertiesJson="{}", attributesJson="{}", ruleIndex=-1, targetDevicePathJson="[]", targetItemPathJson="[]", confirmDelete=False, dryRun=True, expect="any", keys=["records"]),
    S("ReadSiVArcRules", category="screens", objectPathJson="[]", offset=0, limit=20, expect="any", keys=["records"]),
    S("ReadSivarcRuleTree", category="screens", folderPath="", tablePath="", includeRules=True, maxDepth=3, offset=0, limit=20, expect="any"),
    S("ManageSivarcRuleContainer", category="screens", kind="folder", path="MCP_Rules", action="create", libraryName="", typePath="", typeVersion="", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageSivarcRuleContainer", category="screens", kind="table", path="MCP_Rules/MCP_Table", action="create", libraryName="", typePath="", typeVersion="", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageSivarcTableRule", category="screens", tablePath="MCP_Rules/MCP_Table", rulePath="MCP_Rule", kind="rule", action="create", propertiesJson="{}", referencesJson="{}", deviceSelectionJson="{}", deviceNamesJson="[]", libraryName="", masterCopyPath="", createOption="", confirmDelete=False, dryRun=True, expect="any"),
    S("ManageSiVArcRule", category="screens", collectionPathJson=J([{"property": "Folders", "name": "MCP_Rules"}]), name="MCP_Table", action="read", propertiesJson="{}", dryRun=True, expect="any"),
    S("ResolveSivarcExpression", softwarePath=P, blockPath="MCP_G/MCP_FB", devicePathJson=J(["MCP_UCP"]), itemPathJson=J(["HMI_RT_2"]), libraryItemKind="", libraryItemPath="", expression="Block.Name", libraryName="", maxResults=10, expect="any"),
    S("GenerateSiVArc", hmiDeviceName="MCP_UCP", plcSoftwarePathsJson=J([P]), generationOptions="", dryRun=True, additionalHmiDeviceNamesJson="[]", expect="any"),
    S("ManageSivarcRuleContainer", category="screens", kind="table", path="MCP_Rules/MCP_Table", action="delete", libraryName="", typePath="", typeVersion="", confirmDelete=True, dryRun=False, expect="any"),
    S("ManageSivarcRuleContainer", category="screens", kind="folder", path="MCP_Rules", action="delete", libraryName="", typePath="", typeVersion="", confirmDelete=True, dryRun=False, expect="any"),
    S("ImportHmiConnection", softwarePath=H, importPath=D + r"\nope.xml", expect="any"),
    S("SeedProjectFromReference", plcSoftwarePath=P, hmiSoftwarePath=U, referenceDir=D + r"\mcp46_release", placeholders="{}", expect="any"),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_off1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
