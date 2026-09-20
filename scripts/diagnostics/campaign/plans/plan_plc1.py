# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
fc_st = {"operations": [{"op": "assignment", "target": "#OUT_Sum", "expression": "#IN_A + #IN_B"}]}
plan = [
    S("GetSoftwareInfo", softwarePath=P), S("GetSoftwareTree", softwarePath=P), S("GetBlocks", softwarePath=P, regexName=""), S("GetBlocksWithHierarchy", softwarePath=P),
    S("GetPlcTagTables", softwarePath=P), S("GetTypes", softwarePath=P, regexName=""), S("GetPlcWatchTables", softwarePath=P), S("GetPlcExternalSources", softwarePath=P),
    S("ReadPlcSystemGroups", softwarePath=P, unitName="", unitKind="", includeBlocks=True, maxDepth=3, keys=["systemBlockGroups"]),
    S("ReadPlcChecksums", softwarePath=P, keys=["supported", "software", "textLists"]),
    S("ReadPlcSoftwareUnits", softwarePath=P, unitName="", unitKind="", includeContents=False, offset=0, limit=20),
    # groups
    S("CreatePlcBlockGroup", softwarePath=P, groupPath="MCP_G/Sub"),
    S("CreatePlcTypeGroup", softwarePath=P, groupPath="MCP_T", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="tags", groupPath="MCP_Tags", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="watchTables", groupPath="MCP_W", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="externalSources", groupPath="MCP_X", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="technology", groupPath="MCP_TO", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="blocks", groupPath="MCP_G/Sub", action="rename", newName="Sub2", dryRun=False),
    # builders (offline) + import
    S("PlcBuildAndImport", softwarePath=P, kind="udt", json=json.dumps({"name": "MCP_UDT", "members": [{"name": "Run", "datatype": "Bool", "commentZhCn": "运行"}, {"name": "Speed", "datatype": "Real"}]}), typeGroupPath="MCP_T", tagFolderPath="", blockGroupPath="", compileAfter=False, dryRun=False, keys=["verified", "importedPath"]),
    S("PlcBuildAndImport", softwarePath=P, kind="tagtable", json=json.dumps({"tableName": "MCP_Table", "tags": [{"name": "MCP_Start", "dataTypeName": "Bool", "logicalAddress": "%I0.0"}, {"name": "MCP_Speed", "dataTypeName": "Int", "logicalAddress": "%IW10"}]}), typeGroupPath="", tagFolderPath="MCP_Tags", blockGroupPath="", compileAfter=False, dryRun=False, keys=["verified"]),
    S("PlcBuildAndImport", softwarePath=P, kind="globaldb", json=json.dumps({"dbName": "MCP_DB", "dbNumber": 500, "staticMembers": [{"name": "Counter", "datatype": "Int", "startValue": "5"}, {"name": "Motor", "datatype": "\"MCP_UDT\""}]}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False, keys=["verified"]),
    S("PlcBuildAndImport", softwarePath=P, kind="fc", json=json.dumps({"blockName": "MCP_FC", "blockNumber": 500, "inputs": [{"name": "IN_A", "datatype": "Int"}, {"name": "IN_B", "datatype": "Int"}], "outputs": [{"name": "OUT_Sum", "datatype": "Int"}], "structuredText": fc_st}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False, keys=["verified"]),
    S("PlcBuildAndImport", softwarePath=P, kind="fb", json=json.dumps({"blockName": "MCP_FB", "blockNumber": 500, "inputs": [{"name": "Enable", "datatype": "Bool"}], "outputs": [{"name": "Busy", "datatype": "Bool"}], "statics": [{"name": "Count", "datatype": "Int"}], "structuredText": {"operations": [{"op": "assignment", "target": "#Busy", "expression": "#Enable"}]}}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=True, dryRun=False, keys=["verified", "compile"]),
    S("CreatePlcInstanceDb", softwarePath=P, fbPath="MCP_G/MCP_FB", name="MCP_FB_DB", groupPath="MCP_G", autoNumber=True, number=0, dryRun=False, keys=["after"]),
    S("CompileAndDiagnosePlc", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
    S("CompileSoftware", softwarePath=P, password=""),
    S("GetBlockInfo", softwarePath=P, blockPath="MCP_G/MCP_FC"), S("GetTypeInfo", softwarePath=P, typePath="MCP_T/MCP_UDT"),
    S("GetCrossReferences", softwarePath=P, objectPath="MCP_G/MCP_DB", objectKind="block", filter=""),
    S("DescribeBlockLogic", softwarePath=P, blockPath="MCP_G/MCP_FC", expect="any", note="SCL block - LAD describer may refuse"),
    # tags & constants incl. the 2.7.46 comment fix (engine 2.7.45 -> expect the adapter refusal, recorded)
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Stop", kind="tag", action="create", dataType="Bool", addressOrValue="%I0.1", propertiesJson="{}", dryRun=False, keys=["after"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Stop", kind="tag", action="update", propertiesJson=json.dumps({"ExternalVisible": False}), dryRun=False, keys=["after"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Stop", kind="tag", action="update", propertiesJson=json.dumps({"Comment": "停止按钮"}), dryRun=False, expect="any", note="2.7.45 refuses (MultilingualText) - fixed in 2.7.46"),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_K", kind="constant", action="create", dataType="Int", addressOrValue="42", propertiesJson="{}", dryRun=False, keys=["after"]),
    S("ReadPlcTagTableConstants", softwarePath=P, tablePath="MCP_Tags/MCP_Table", kind="", unitName="", unitKind="", offset=0, limit=20, keys=["rows"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_K", kind="constant", action="read", propertiesJson="{}", dryRun=True, keys=["before"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_K", kind="constant", action="delete", propertiesJson="{}", dryRun=False, keys=["verifiedAbsent"]),
    # exports (files on the VM desktop)
    S("ExportBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", exportPath=D + r"\mcp46_FC.xml", preservePath=False),
    S("ExportAsDocuments", softwarePath=P, blockPath="MCP_G/MCP_FB", exportPath=D + r"\mcp46_docs", preservePath=False, keys=["files"]),
    S("ExportBlocks", softwarePath=P, exportPath=D + r"\mcp46_blocks", regexName="^MCP_", preservePath=False),
    S("ExportBlocksAsDocuments", softwarePath=P, exportPath=D + r"\mcp46_blockdocs", regexName="^MCP_", preservePath=False),
    S("ExportType", softwarePath=P, exportPath=D + r"\mcp46_UDT.xml", typePath="MCP_T/MCP_UDT", preservePath=False),
    S("ExportTypes", softwarePath=P, exportPath=D + r"\mcp46_types", regexName="^MCP_", preservePath=False),
    S("ExportPlcTagTable", softwarePath=P, tagTableName="MCP_Table", exportPath=D + r"\mcp46_tags.xml"),
    S("GeneratePlcSourceFromBlocks", softwarePath=P, blockPathsJson=json.dumps(["MCP_G/MCP_FC"]), filePath=D + r"\mcp46_FC.scl", dryRun=False, keys=["sha256"]),
    S("GeneratePlcLoadableFile", softwarePath=P, objectPathsJson=json.dumps(["MCP_G/MCP_FC"]), objectKind="block", targetOption="Normal", filePath=D + r"\mcp46_FC.loadable", dryRun=True, expect="any", note="preview only; enum name may differ"),
    S("ReadPlcObjectFingerprints", softwarePath=P, objectKind="block", objectPath="MCP_G/MCP_FC", unitName="", unitKind="", keys=["fingerprints"]),
    S("ReadPlcObjectFingerprints", softwarePath=P, objectKind="type", objectPath="MCP_T/MCP_UDT", unitName="", unitKind=""),
    S("ReadPlcChecksums", softwarePath=P, keys=["software"]),
    # re-import round trips
    S("DeletePlcBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", dryRun=False, keys=["verifiedAbsent"]),
    S("ImportBlock", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_FC.xml", keys=["verified"]),
    S("ImportBlocksFromDirectory", softwarePath=P, groupPath="MCP_G/Sub2", dir=D + r"\mcp46_blocks", regexName="MCP_FC", overwrite=True, expect="any", note="same block into another group -> TIA decides"),
    S("ImportFromDocuments", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_docs", fileNameWithoutExtension="MCP_FB", importOption="Override", keys=["verified"]),
    S("ImportBlocksFromDocuments", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_blockdocs", regexName="", importOption="Override"),
    S("DeletePlcType", softwarePath=P, typePath="MCP_T/MCP_UDT", dryRun=True, keys=["crossReferences"]),
    S("ImportType", softwarePath=P, groupPath="MCP_T", importPath=D + r"\mcp46_UDT.xml", expect="any", note="exists -> TIA decides"),
    S("ImportPlcTagTable", softwarePath=P, folderPath="MCP_Tags", importPath=D + r"\mcp46_tags.xml", expect="any"),
    S("ImportPlcTagTablesFromDirectory", softwarePath=P, folderPath="MCP_Tags", dir=D, regexName="mcp46_tags", overwrite=True, expect="any"),
    S("MoveBlockToGroup", softwarePath=P, blockName="MCP_FC", targetGroupPath="MCP_G/Sub2", autoCreateGroup=False, keys=["method"]),
    S("RepairAndReimportBlock", softwarePath=P, importPath=D + r"\mcp46_FC.xml", groupPath="MCP_G", compileAfter=False, expect="any"),
    S("ImportPlcProgramFromDirectory", softwarePath=P, sourceDir=D + r"\mcp46_blocks", typeGroupPath="MCP_T", tagFolderPath="MCP_Tags", technologyFolderPath="", blockGroupPath="MCP_G", regexName="", compileAfter=False, stopOnImportFailure=False, dryRun=True, keys=["plan"]),
    S("CompileAndDiagnosePlc", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_plc1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
