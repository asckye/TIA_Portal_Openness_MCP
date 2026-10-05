# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
fc_st = {"operations": [{"op": "assignment", "target": "#OUT_Sum", "expression": "#IN_A + #IN_B"}]}
plan = [
    S("GetSoftwareInfo", softwarePath=P), S("GetSoftwareTree", softwarePath=P), S("ListPlcBlocks", softwarePath=P, regexName=""), S("GetPlcBlockHierarchy", softwarePath=P),
    S("ListPlcTagTables", softwarePath=P), S("ListPlcTypes", softwarePath=P, regexName=""), S("ListPlcWatchTables", softwarePath=P), S("ListPlcExternalSources", softwarePath=P),
    S('ListPlcSystemGroups', note='', expect='ok', keys=['systemBlockGroups'], softwarePath='MCP_PLC', unitName='', includeBlocks=True, maxDepth=3),
    S("GetPlcChecksums", softwarePath=P, keys=["supported", "software", "textLists"]),
    S('ListPlcSoftwareUnits', note='', expect='ok', keys=[], softwarePath='MCP_PLC', unitName='', includeContents=False, offset=0, limit=20),
    # groups
    S("CreatePlcBlockGroup", softwarePath=P, groupPath="MCP_G/Sub"),
    S("CreatePlcTypeGroup", softwarePath=P, groupPath="MCP_T", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="tags", groupPath="MCP_Tags", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="watchTables", groupPath="MCP_W", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="externalSources", groupPath="MCP_X", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="technology", groupPath="MCP_TO", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="blocks", groupPath="MCP_G/Sub", action="rename", newName="Sub2", dryRun=False),
    # builders (offline) + import
    S("BuildAndImportPlcArtifact", softwarePath=P, kind="udt", spec={"name": "MCP_UDT", "members": [{"name": "Run", "datatype": "Bool", "commentZhCn": "运行"}, {"name": "Speed", "datatype": "Real"}]}, typeGroupPath="MCP_T", tagFolderPath="", blockGroupPath="", compileAfter=False, dryRun=False, keys=["verified", "importedPath"]),
    S("BuildAndImportPlcArtifact", softwarePath=P, kind="tagtable", spec={"tableName": "MCP_Table", "tags": [{"name": "MCP_Start", "dataTypeName": "Bool", "logicalAddress": "%I0.0"}, {"name": "MCP_Speed", "dataTypeName": "Int", "logicalAddress": "%IW10"}]}, typeGroupPath="", tagFolderPath="MCP_Tags", blockGroupPath="", compileAfter=False, dryRun=False, keys=["verified"]),
    S("BuildAndImportPlcArtifact", softwarePath=P, kind="globaldb", spec={"dbName": "MCP_DB", "dbNumber": 500, "staticMembers": [{"name": "Counter", "datatype": "Int", "startValue": "5"}, {"name": "Motor", "datatype": "\"MCP_UDT\""}]}, typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False, keys=["verified"]),
    S('BuildAndImportPlcArtifact', note='', expect='ok', keys=['verified'], softwarePath='MCP_PLC', kind='fc', spec={'blockName': 'MCP_FC', 'blockNumber': 500, 'inputs': [{'name': 'IN_A', 'datatype': 'Int'}, {'name': 'IN_B', 'datatype': 'Int'}], 'outputs': [{'name': 'OUT_Sum', 'datatype': 'Int'}], 'structuredText': {'operations': [{'op': 'assign', 'target': '#OUT_Sum', 'source': '#IN_A + #IN_B'}]}}, typeGroupPath='', tagFolderPath='', blockGroupPath='MCP_G', compileAfter=False, dryRun=False),
    S('BuildAndImportPlcArtifact', note='', expect='ok', keys=['verified', 'compile'], softwarePath='MCP_PLC', kind='fb', spec={'blockName': 'MCP_FB', 'blockNumber': 500, 'inputs': [{'name': 'Enable', 'datatype': 'Bool'}], 'outputs': [{'name': 'Busy', 'datatype': 'Bool'}], 'statics': [{'name': 'Count', 'datatype': 'Int'}], 'structuredText': {'operations': [{'op': 'assign', 'target': '#Busy', 'source': '#Enable'}]}}, typeGroupPath='', tagFolderPath='', blockGroupPath='MCP_G', compileAfter=True, dryRun=False),
    S("CreatePlcInstanceDb", softwarePath=P, fbPath="MCP_G/MCP_FB", name="MCP_FB_DB", groupPath="MCP_G", autoNumber=True, number=0, dryRun=False, keys=["after"]),
    S("CompilePlcDiagnostics", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
    S("CompilePlcSoftware", softwarePath=P, password=""),
    S("GetPlcBlockInfo", softwarePath=P, blockPath="MCP_G/MCP_FC"), S("GetPlcTypeInfo", softwarePath=P, typePath="MCP_T/MCP_UDT"),
    S('GetPlcCrossReferences', note='', expect='ok', keys=[], softwarePath='MCP_PLC', objectPath='MCP_G/MCP_DB', objectKind='Block', filter=''),
    S("DescribePlcBlockLogic", softwarePath=P, blockPath="MCP_G/MCP_FC", expect="any", note="SCL block - LAD describer may refuse"),
    # tags & constants incl. the 2.7.46 comment fix (engine 2.7.45 -> expect the adapter refusal, recorded)
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Stop", kind="tag", action="create", dataType="Bool", addressOrValue="%I0.1", properties={}, dryRun=False, keys=["after"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Stop", kind="tag", action="update", properties={"ExternalVisible": False}, dryRun=False, keys=["after"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Stop", kind="tag", action="update", properties={"Comment": "停止按钮"}, dryRun=False, expect="any", note="2.7.45 refuses (MultilingualText) - fixed in 2.7.46"),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_K", kind="constant", action="create", dataType="Int", addressOrValue="42", properties={}, dryRun=False, keys=["after"]),
    S('GetPlcTagTableConstants', note='', expect='ok', keys=['rows'], softwarePath='MCP_PLC', tablePath='MCP_Tags/MCP_Table', unitName='', offset=0, limit=20),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_K", kind="constant", action="read", properties={}, dryRun=True, keys=["before"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_K", kind="constant", action="delete", properties={}, dryRun=False, keys=["verifiedAbsent"]),
    # exports (files on the VM desktop)
    S("ExportPlcBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", exportPath=D + r"\mcp46_FC.xml", preservePath=False),
    S("ExportPlcBlockDocuments", softwarePath=P, blockPath="MCP_G/MCP_FB", exportPath=D + r"\mcp46_docs", preservePath=False, keys=["files"]),
    S("ExportPlcBlocks", softwarePath=P, exportPath=D + r"\mcp46_blocks", regexName="^MCP_", preservePath=False),
    S("ExportPlcBlocksDocuments", softwarePath=P, exportPath=D + r"\mcp46_blockdocs", regexName="^MCP_", preservePath=False),
    S("ExportPlcType", softwarePath=P, exportPath=D + r"\mcp46_UDT.xml", typePath="MCP_T/MCP_UDT", preservePath=False),
    S("ExportPlcTypes", softwarePath=P, exportPath=D + r"\mcp46_types", regexName="^MCP_", preservePath=False),
    S("ExportPlcTagTable", softwarePath=P, tagTableName="MCP_Table", exportPath=D + r"\mcp46_tags.xml"),
    S("GeneratePlcSourceFromBlocks", softwarePath=P, blockPaths=["MCP_G/MCP_FC"], filePath=D + r"\mcp46_FC.scl", dryRun=False, keys=["sha256"]),
    S('GeneratePlcLoadableFile', note='preview only; enum name may differ', expect='any', keys=[], softwarePath='MCP_PLC', objectPaths=['MCP_G/MCP_FC'], objectKind='blocks', targetOption='Normal', filePath='C:\\Users\\SIEMENS\\Desktop\\mcp46_FC.loadable', dryRun=True),
    S('GetPlcObjectFingerprints', note='', expect='ok', keys=['fingerprints'], softwarePath='MCP_PLC', objectKind='block', objectPath='MCP_G/MCP_FC', unitName=''),
    S('GetPlcObjectFingerprints', note='', expect='ok', keys=[], softwarePath='MCP_PLC', objectKind='type', objectPath='MCP_T/MCP_UDT', unitName=''),
    S("GetPlcChecksums", softwarePath=P, keys=["software"]),
    # re-import round trips
    S("DeletePlcBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", dryRun=False, keys=["verifiedAbsent"]),
    S("ImportPlcBlock", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_FC.xml", keys=["verified"]),
    S("ImportPlcBlocksFromDirectory", softwarePath=P, groupPath="MCP_G/Sub2", dir=D + r"\mcp46_blocks", regexName="MCP_FC", overwrite=True, expect="any", note="same block into another group -> TIA decides"),
    S("ImportPlcBlockDocuments", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_docs", fileNameWithoutExtension="MCP_FB", importOption="Override", keys=["verified"]),
    S("ImportPlcBlocksDocuments", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_blockdocs", regexName="", importOption="Override"),
    S("DeletePlcType", softwarePath=P, typePath="MCP_T/MCP_UDT", dryRun=True, keys=["crossReferences"]),
    S("ImportPlcType", softwarePath=P, groupPath="MCP_T", importPath=D + r"\mcp46_UDT.xml", expect="any", note="exists -> TIA decides"),
    S("ImportPlcTagTable", softwarePath=P, folderPath="MCP_Tags", importPath=D + r"\mcp46_tags.xml", expect="any"),
    S("ImportPlcTagTablesFromDirectory", softwarePath=P, folderPath="MCP_Tags", dir=D, regexName="mcp46_tags", overwrite=True, expect="any"),
    S("MovePlcBlockToGroup", softwarePath=P, blockName="MCP_FC", targetGroupPath="MCP_G/Sub2", autoCreateGroup=False, keys=["method"]),
    S("RepairAndReimportPlcBlock", softwarePath=P, importPath=D + r"\mcp46_FC.xml", groupPath="MCP_G", compileAfter=False, expect="any"),
    S("ImportPlcProgramFromDirectory", softwarePath=P, sourceDir=D + r"\mcp46_blocks", typeGroupPath="MCP_T", tagFolderPath="MCP_Tags", technologyFolderPath="", blockGroupPath="MCP_G", regexName="", compileAfter=False, stopOnImportFailure=False, dryRun=True, keys=["plan"]),
    S("CompilePlcDiagnostics", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_plc1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
