# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"; U = "HMI_RT_2"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    # offline builders
    S("BuildPlcUdt", udt={"name": "MCP_UDT2", "members": [{"name": "A", "datatype": "Bool"}]}, keys=["udtName"]),
    S("BuildPlcTagTable", tagTable={"tableName": "MCP_T2", "tags": [{"name": "T1", "dataTypeName": "Bool", "logicalAddress": "%I1.0"}]}),
    S("BuildPlcGlobalDb", globalDb={"dbName": "MCP_DB2", "dbNumber": 501, "staticMembers": [{"name": "X", "datatype": "Int"}]}),
    S('BuildStructuredText', note='', expect='ok', keys=[], structuredText={'operations': [{'op': 'assign', 'target': '#a', 'literalValue': '1'}]}, innerOnly=True),
    S('BuildPlcFcBlock', note='', expect='ok', keys=[], fcBlock={'blockName': 'MCP_FC2', 'blockNumber': 502, 'inputs': [{'name': 'A', 'datatype': 'Int'}], 'outputs': [{'name': 'B', 'datatype': 'Int'}], 'structuredText': {'operations': [{'op': 'assign', 'target': '#B', 'source': '#A'}]}}),
    S('BuildPlcFbBlock', note='', expect='ok', keys=[], fbBlock={'blockName': 'MCP_FB2', 'blockNumber': 502, 'inputs': [{'name': 'A', 'datatype': 'Bool'}], 'statics': [{'name': 'S', 'datatype': 'Int'}], 'structuredText': {'operations': [{'op': 'assign', 'target': '#S', 'literalValue': '1'}]}}),
    S('BuildFlgNetCall', note='', expect='any', keys=[], flgNet={'callName': 'MCP_FC', 'parameters': [{'name': 'IN_A', 'section': 'Input', 'dataType': 'Int', 'constantValue': '1', 'sourceKind': 'constant'}, {'name': 'IN_B', 'section': 'Input', 'dataType': 'Int', 'constantValue': '2', 'sourceKind': 'constant'}, {'name': 'OUT_Sum', 'section': 'Output', 'dataType': 'Int', 'symbolPath': ['MCP_DB', 'Counter']}]}),
    S('BuildPlcLadFcBlock', note='', expect='any', keys=[], ladFcBlock={'blockName': 'MCP_LAD', 'blockNumber': 503, 'networks': [{'call': {'callName': 'MCP_FC', 'parameters': [{'name': 'IN_A', 'section': 'Input', 'dataType': 'Int', 'constantValue': '1', 'sourceKind': 'constant'}, {'name': 'IN_B', 'section': 'Input', 'dataType': 'Int', 'constantValue': '2', 'sourceKind': 'constant'}, {'name': 'OUT_Sum', 'section': 'Output', 'dataType': 'Int', 'symbolPath': ['MCP_DB', 'Counter']}]}, 'titleZhCn': '调用'}]}),
    S("BuildPlcSymbolManifestFromPath", path=D + r"\mcp46_tags.xml", expect="any"),
    S("BuildClassicHmiTagTable", table={"name": "MCP_CT", "tags": [{"name": "A", "dataType": "Bool"}]}),
    S("BuildClassicHmiScreen", design={"screen": {"name": "MCP_CS"}, "items": [{"type": "Text", "name": "T", "text": "x"}]}),
    # exports store
    S("GetDeviceAttributes", devicePath=P, nameFilter="", keys=["exportId"]),
    {"tool": "ListExportHandles", "args": {"tool": "", "limit": 5}, "expect": "ok", "note": "", "keys": ["exports"]},
    # opc ua
    S("GetPlcOpcUaConfiguration", softwarePath=P, keys=["serverInterfaces", "values"]),
    S("ExportOpcUaInterface", softwarePath=P, interfaceName="MCP_Srv", exportPath=D + r"\mcp46_opcua.xml", interfaceType="server", expect="any"),
    S("ImportOpcUaInterface", softwarePath=P, importPath=D + r"\mcp46_opcua.xml", interfaceType="server", expect="any"),
    S('GetOpcUaAccessControl', note='', expect='any', keys=[], softwarePath='MCP_PLC'),
    # tag table delete round trip
    S("BuildAndImportPlcArtifact", softwarePath=P, kind="tagtable", spec={"tableName": "MCP_Tmp", "tags": [{"name": "Tmp1", "dataTypeName": "Bool", "logicalAddress": "%I3.0"}]}, typeGroupPath="", tagFolderPath="MCP_Tags", blockGroupPath="", compileAfter=False, dryRun=False),
    S("DeletePlcTagTable", softwarePath=P, tagTableName="MCP_Tmp", dryRun=True, keys=["tags"]),
    S("DeletePlcTagTable", softwarePath=P, tagTableName="MCP_Tmp", dryRun=False, keys=["verifiedAbsent"]),
    # transfer areas / sivarc
    S('ListTransferAreas', note='', expect='any', keys=['records'], devicePath=['MCP_PLC'], itemPath=['MCP_PLC', 'PROFINET 接口_1'], positionNumber=-1, extendedPositionNumber=-1),
    S("ListSivarcRules", category="screens", objectPath=[], offset=0, limit=20, expect="any", keys=["records"]),
    S("GetSivarcRuleTree", category="screens", folderPath="", tablePath="", includeRules=True, maxDepth=3, offset=0, limit=20, expect="any"),
    S("ManageSivarcRuleContainer", category="screens", kind="folder", path="MCP_Rules", action="create", libraryName="", typePath="", typeVersion="", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageSivarcRuleContainer", category="screens", kind="table", path="MCP_Rules/MCP_Table", action="create", libraryName="", typePath="", typeVersion="", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S('ManageSivarcTableRule', note='', expect='any', keys=[], category='screens', tablePath='MCP_Rules/MCP_Table', rulePath='MCP_Rule', kind='rule', action='create', properties={}, references={}, deviceSelection={}, deviceNames=[], libraryName='', masterCopyPath='', confirmDelete=False, dryRun=True),
    S('ListSivarcRules', note='', expect='any', keys=[], category='screens'),
    S('ResolveSivarcExpression', note='', expect='any', keys=[], softwarePath='MCP_PLC', blockPath='MCP_G/MCP_FB', devicePath=['MCP_UCP'], itemPath=['HMI_RT_2'], libraryItemKind='masterCopy', libraryItemPath='', expression='Block.Name', libraryName='', maxResults=10),
    S("GenerateSivarc", hmiDeviceName="MCP_UCP", plcSoftwarePaths=[P], generationOptions="", dryRun=True, additionalHmiDeviceNames=[], expect="any"),
    S("ManageSivarcRuleContainer", category="screens", kind="table", path="MCP_Rules/MCP_Table", action="delete", libraryName="", typePath="", typeVersion="", confirmDelete=True, dryRun=False, expect="any"),
    S("ManageSivarcRuleContainer", category="screens", kind="folder", path="MCP_Rules", action="delete", libraryName="", typePath="", typeVersion="", confirmDelete=True, dryRun=False, expect="any"),
    S("ImportHmiConnection", softwarePath=H, importPath=D + r"\nope.xml", expect="any"),
    S('SeedProjectFromReference', note='', expect='any', keys=[], plcSoftwarePath='MCP_PLC', hmiSoftwarePath='HMI_RT_2', referenceDir='C:\\Users\\SIEMENS\\Desktop\\mcp46_release', placeholders={}),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_off1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
