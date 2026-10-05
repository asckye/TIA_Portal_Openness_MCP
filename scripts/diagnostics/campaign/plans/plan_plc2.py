# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
fc_st = {"operations": [{"op": "line", "items": [{"sym": "#OUT_Sum"}, {"token": ":="}, {"sym": "#IN_A"}, {"token": "+"}, {"sym": "#IN_B"}]}]}
fb_st = {"operations": [{"op": "assignment", "target": "#Busy", "source": "#Enable"}, {"op": "if", "condition": "#Enable"}, {"op": "line", "items": [{"sym": "#Count"}, {"token": ":="}, {"sym": "#Count"}, {"token": "+"}, {"lit": "1"}], "indent": 1}, {"op": "endif"}]}
plan = [
    S("ListPlcSoftwareUnits", softwarePath=P, unitName="", unitKind="all", includeContents=False, offset=0, limit=20, keys=["units", "systemGroup"]),
    S("GetPlcTagTableConstants", softwarePath=P, tablePath="MCP_Tags/MCP_Table", kind="all", unitName="", unitKind="unit", offset=0, limit=20, keys=["rows", "total"]),
    S("BuildAndImportPlcArtifact", softwarePath=P, kind="fc", spec={"blockName": "MCP_FC", "blockNumber": 500, "inputs": [{"name": "IN_A", "datatype": "Int"}, {"name": "IN_B", "datatype": "Int"}], "outputs": [{"name": "OUT_Sum", "datatype": "Int"}], "structuredText": fc_st}, typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False, keys=["verified"]),
    S('BuildAndImportPlcArtifact', note='', expect='ok', keys=['verified', 'compile'], softwarePath='MCP_PLC', kind='fb', spec={'blockName': 'MCP_FB', 'blockNumber': 500, 'inputs': [{'name': 'Enable', 'datatype': 'Bool'}], 'outputs': [{'name': 'Busy', 'datatype': 'Bool'}], 'statics': [{'name': 'Count', 'datatype': 'Int'}], 'structuredText': {'operations': [{'op': 'assign', 'target': '#Busy', 'source': '#Enable'}, {'op': 'if', 'condition': '#Enable'}, {'op': 'line', 'items': [{'sym': '#Count'}, {'token': ':='}, {'sym': '#Count'}, {'token': '+'}, {'lit': '1'}], 'indent': 1}, {'op': 'endif'}]}}, typeGroupPath='', tagFolderPath='', blockGroupPath='MCP_G', compileAfter=True, dryRun=False),
    S("CreatePlcInstanceDb", softwarePath=P, fbPath="MCP_G/MCP_FB", name="MCP_FB_DB", groupPath="MCP_G", autoNumber=True, number=0, dryRun=False, keys=["after"]),
    S("CompilePlcDiagnostics", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
    S("GetPlcBlockInfo", softwarePath=P, blockPath="MCP_G/MCP_FC"),
    S('GetPlcCrossReferences', note='', expect='any', keys=['items'], softwarePath='MCP_PLC', objectPath='MCP_G/MCP_FB', objectKind='Block', filter=''),
    S('GetPlcCrossReferences', note='', expect='any', keys=[], softwarePath='MCP_PLC', objectPath='MCP_T/MCP_UDT', objectKind='Type', filter=''),
    S("DescribePlcBlockLogic", softwarePath=P, blockPath="MCP_G/MCP_FC", expect="any", note="SCL block"),
    S("ExportPlcBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", exportPath=D + r"\mcp46_FC.xml", preservePath=False),
    S("ExportPlcBlockDocuments", softwarePath=P, blockPath="MCP_G/MCP_FB", exportPath=D + r"\mcp46_docs", preservePath=False),
    S("GeneratePlcSourceFromBlocks", softwarePath=P, blockPaths=["MCP_G/MCP_FC"], filePath=D + r"\mcp46_FC.scl", dryRun=False, keys=["sha256", "output"]),
    S("GeneratePlcLoadableFile", softwarePath=P, objectPaths=["MCP_G/MCP_FC"], objectKind="blocks", targetOption="Normal", filePath=D + r"\mcp46_FC.loadable", dryRun=True, expect="any", keys=["targetOptions"]),
    S('GetPlcObjectFingerprints', note='', expect='ok', keys=['fingerprints'], softwarePath='MCP_PLC', objectKind='block', objectPath='MCP_G/MCP_FC', unitName=''),
    S("DeletePlcBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", dryRun=False, keys=["verifiedAbsent"]),
    S("ImportPlcBlock", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_FC.xml", keys=["verified"]),
    S("ImportPlcBlockDocuments", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_docs", fileNameWithoutExtension="MCP_FB", importOption="Override", keys=["verified"]),
    S("MovePlcBlockToGroup", softwarePath=P, blockName="MCP_FC", targetGroupPath="MCP_G/Sub2", autoCreateGroup=False, keys=["method", "newPath"]),
    S("GetPlcBlockHierarchy", softwarePath=P),
    S("RepairAndReimportPlcBlock", softwarePath=P, importPath=D + r"\mcp46_FC.xml", groupPath="MCP_G/Sub2", compileAfter=True, expect="any", keys=["compile", "suggestions"]),
    S("ImportPlcProgramFromDirectory", softwarePath=P, sourceDir=D + r"\mcp46_blocks", typeGroupPath="MCP_T", tagFolderPath="MCP_Tags", technologyFolderPath="", blockGroupPath="MCP_G", regexName="", compileAfter=False, stopOnImportFailure=False, dryRun=True, expect="any", keys=["plan", "failed"]),
    S("ImportPlcExternalSource", softwarePath=P, groupPath="MCP_X", filePath=D + r"\mcp46_FC.scl", keys=["verified"]),
    S("ListPlcExternalSources", softwarePath=P),
    S("GenerateBlocksFromExternalSource", softwarePath=P, externalSourceName="mcp46_FC", expect="any"),
    S('ManagePlcExternalSources', note='', expect='ok', keys=['rows'], softwarePath='MCP_PLC', action='list', name='', unitName='', unitKind='', groupPath='', filePath='', libraryName='', masterCopyPath='', copyMode='', targetKind='', targetGroupPath='', newName='', confirmDelete=False, dryRun=True),
    S("DeletePlcExternalSource", softwarePath=P, externalSourceName="mcp46_FC", expect="any"),
    S("DeleteEmptyPlcBlockGroup", softwarePath=P, groupPath="MCP_G/Sub2", dryRun=True, expect="any", keys=["contents"]),
    S("ManagePlcUserGroup", softwarePath=P, family="blocks", groupPath="MCP_Empty", action="create", newName="", dryRun=False),
    S("DeleteEmptyPlcBlockGroup", softwarePath=P, groupPath="MCP_Empty", dryRun=False, keys=["verifiedAbsent"]),
    S("ManagePlcUserGroup", softwarePath=P, family="types", groupPath="MCP_T2", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="types", groupPath="MCP_T2", action="deleteEmpty", newName="", dryRun=False, keys=["verifiedAbsent"]),
    S("CompilePlcDiagnostics", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_plc2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
