# -*- coding: utf-8 -*-
import json, io, os
H = "HMI_RT_1"; D = r"C:\Users\SIEMENS\Desktop"; P = "MCP_PLC"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
pkg = {"tagTable": {"name": "MCP_HmiTags", "tags": [{"name": "MCP_Run", "dataType": "Bool"}, {"name": "MCP_Speed", "dataType": "Int"}]},
       "screen": {"name": "MCP_Screen", "items": [{"type": "Text", "name": "Title", "text": "MCP", "left": 10, "top": 10, "width": 200, "height": 30}, {"type": "Button", "name": "Btn", "text": "Start", "left": 10, "top": 60, "width": 100, "height": 40, "tag": "MCP_Run"}, {"type": "IOField", "name": "Io", "left": 10, "top": 120, "width": 100, "height": 30, "tag": "MCP_Speed"}]}}
plan = [
    S("GetHmiProgramInfo", softwarePath=H, keys=["hmiType", "version"]),
    S("ListHmiScreens", softwarePath=H), S("ListHmiScreenPaths", softwarePath=H, offset=0, limit=50), S("ListHmiTagTables", softwarePath=H), S("ListHmiConnections", softwarePath=H),
    S("DescribeHmiSoftware", softwarePath=H, maxMembers=30),
    S("GetClassicHmiScreenTree", softwarePath=H, kind="screens", folderPath="", maxDepth=3, expect="any", keys=["tree"]),
    S("GetClassicHmiScreenTree", softwarePath=H, kind="popups", folderPath="", maxDepth=3, expect="any"),
    S("GetClassicHmiScreenTree", softwarePath=H, kind="templates", folderPath="", maxDepth=3, expect="any"),
    # offline builders + import
    S('BuildClassicHmiMinimalPackage', note='', expect='ok', keys=['ok', 'missingTags'], package={'tagTable': {'name': 'MCP_HmiTags', 'tags': [{'name': 'MCP_Run', 'dataType': 'Bool'}, {'name': 'MCP_Speed', 'dataType': 'Int'}]}, 'name': 'Classic_HMI_Minimal_Package', 'screenDesign': {'items': [{'type': 'Text', 'name': 'Title', 'text': 'MCP', 'left': 10, 'top': 10, 'width': 200, 'height': 30}, {'type': 'Button', 'name': 'Btn', 'text': 'Start', 'left': 10, 'top': 60, 'width': 100, 'height': 40, 'processValueTag': 'MCP_Run'}, {'type': 'IOField', 'name': 'Io', 'left': 10, 'top': 120, 'width': 100, 'height': 30, 'processValueTag': 'MCP_Speed'}], 'screen': {'name': 'MCP_Screen'}}}),
    S('WriteClassicHmiMinimalPackageFiles', note='', expect='ok', keys=['files', 'tagTableFile', 'screenFile'], package={'tagTable': {'name': 'MCP_HmiTags', 'tags': [{'name': 'MCP_Run', 'dataType': 'Bool'}, {'name': 'MCP_Speed', 'dataType': 'Int'}]}, 'name': 'Classic_HMI_Minimal_Package', 'screenDesign': {'items': [{'type': 'Text', 'name': 'Title', 'text': 'MCP', 'left': 10, 'top': 10, 'width': 200, 'height': 30}, {'type': 'Button', 'name': 'Btn', 'text': 'Start', 'left': 10, 'top': 60, 'width': 100, 'height': 40, 'processValueTag': 'MCP_Run'}, {'type': 'IOField', 'name': 'Io', 'left': 10, 'top': 120, 'width': 100, 'height': 30, 'processValueTag': 'MCP_Speed'}], 'screen': {'name': 'MCP_Screen'}}}, outputDirectory='C:\\Users\\SIEMENS\\Desktop\\mcp46_hmi'),
    S("ValidateClassicHmiMinimalPackageFiles", path=D + r"\mcp46_hmi", expect="any", keys=["ok"]),
    S("ValidateClassicHmiMinimalPackagePlcSync", path=D + r"\mcp46_hmi", plcSymbols=["MCP_Start", "MCP_Speed"], expect="any", keys=["ok"]),
    S("RunClassicHmiOfflineValidationSuite", reportDirectory=D + r"\mcp46_hmi\reports", expect="any", keys=["ok"]),
    S("RunClassicHmiTemporaryImportPreflight", workspaceRoot=D + r"\mcp46_hmi", reportDirectory=D + r"\mcp46_hmi\reports", expect="any", keys=["ok"]),
    S("ManageClassicHmiFolder", softwarePath=H, folderKind="tags", folderPath="MCP_TagFolder", action="create", newName="", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageClassicHmiFolder", softwarePath=H, folderKind="screens", folderPath="MCP_ScreenFolder", action="create", newName="", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ImportHmiTagTable", softwarePath=H, folderPath="MCP_TagFolder", importPath=D + r"\mcp46_hmi\MCP_HmiTags.xml", expect="any", keys=["verified"]),
    S("ImportHmiScreen", softwarePath=H, folderPath="MCP_ScreenFolder", importPath=D + r"\mcp46_hmi\MCP_Screen.xml", expect="any", keys=["verified"]),
    S("ListHmiTagTables", softwarePath=H), S("ListHmiTags", softwarePath=H, tagTableName="MCP_HmiTags", expect="any"), S("ListHmiScreens", softwarePath=H),
    S("DescribeHmiScreen", softwarePath=H, screenName="MCP_Screen", maxMembers=20, expect="any"),
    S("DescribeHmiScreenItem", softwarePath=H, screenName="MCP_Screen", itemName="Btn", maxMembers=20, expect="any"),
    S("DescribeHmiTagTable", softwarePath=H, tagTableName="MCP_HmiTags", maxMembers=20, expect="any"),
    S("DescribeHmiTag", softwarePath=H, tagTableName="MCP_HmiTags", tagName="MCP_Run", maxMembers=20, expect="any"),
    S("GetHmiScreenSnapshot", softwarePath=H, screenPath="/MCP_ScreenFolder/MCP_Screen", maxDepth=3, maxNodes=200, expect="any", keys=["apiCallSuccess", "dataComplete"]),
    S("ExportHmiScreen", softwarePath=H, screenName="MCP_Screen", exportPath=D + r"\mcp46_hmi\MCP_Screen_export.xml", expect="any"),
    S("ExportHmiTagTable", softwarePath=H, tagTableName="MCP_HmiTags", exportPath=D + r"\mcp46_hmi\MCP_HmiTags_export.xml", expect="any"),
    S("ExportHmiProgram", softwarePath=H, exportDir=D + r"\mcp46_hmi\program", exportScreens=True, exportTagTables=True, expect="any"),
    S("ImportHmiScreensFromDirectory", softwarePath=H, folderPath="MCP_ScreenFolder", dir=D + r"\mcp46_hmi\program", regexName="MCP_Screen", overwrite=True, expect="any"),
    S("ImportHmiTagTablesFromDirectory", softwarePath=H, folderPath="MCP_TagFolder", dir=D + r"\mcp46_hmi\program", regexName="MCP_HmiTags", overwrite=True, expect="any"),
    S('ManageClassicHmiScreenObject', note='Read the requested screen-object collection', expect='any', keys=['rows'], softwarePath='HMI_RT_1', objectKind='popup', objectPath='', action='read', filePath='', importOptions='None', confirmDelete=False, dryRun=True),
    S('ManageClassicHmiScreenObject', note='Read the requested screen-object collection', expect='any', keys=['rows'], softwarePath='HMI_RT_1', objectKind='template', objectPath='', action='read', filePath='', importOptions='None', confirmDelete=False, dryRun=True),
    S('ManageClassicHmiScreenObject', note='Read the requested screen-object collection', expect='any', keys=['rows'], softwarePath='HMI_RT_1', objectKind='slidein', objectPath='', action='read', filePath='', importOptions='None', confirmDelete=False, dryRun=True),
    S("ListClassicHmiScripts", softwarePath=H, folderPath="", offset=0, limit=20, expect="any", keys=["rows"]),
    S("ManageClassicHmiScript", softwarePath=H, scriptPath="", action="read", filePath="", importOptions="None", attributes={}, confirmDelete=False, dryRun=True, expect="any"),
    S("ManageClassicHmiCycle", softwarePath=H, action="read", cycleName="", filePath="", importOptions="None", attributes={}, offset=0, limit=20, confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiCycle", softwarePath=H, action="export", cycleName="", filePath=D + r"\mcp46_hmi\cycles.xml", importOptions="None", attributes={}, offset=0, limit=20, confirmDelete=False, dryRun=False, expect="any", keys=["file"]),
    S("ManageClassicHmiTextGraphicList", softwarePath=H, listKind="text", action="read", listName="", compositionName="", entryName="", typeName="", filePath="", importOptions="None", attributes={}, offset=0, limit=20, confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiTextGraphicList", softwarePath=H, listKind="graphic", action="read", listName="", compositionName="", entryName="", typeName="", filePath="", importOptions="None", attributes={}, offset=0, limit=20, confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("GetClassicHmiGlobalization", softwarePath=H, offset=0, limit=20, expect="any", keys=["rows"]),
    S("ManageClassicHmiGraphic", softwarePath=H, action="list", name="", filePath="", importOptions="None", confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ListClassicHmiFaceplates", kind="all", libraryName="", folderPath="", offset=0, limit=20, expect="any", keys=["rows"]),
    S("ManageSivarcScreenLayout", softwarePath=H, screenName="MCP_Screen", action="export", filePath=D + r"\mcp46_hmi\layout.yml", dryRun=False, expect="any"),
    S("ExportHmiConnection", softwarePath=H, connectionName="", exportPath=D + r"\mcp46_hmi\conn.xml", expect="any"),
    S("CompileHmiDiagnostics", softwarePath=H, expect="any", keys=["errorCount", "warningCount"]),
    S('ManageClassicHmiFolder', note='V4 rejects this unadvertised action', expect='error', keys=['after'], softwarePath='HMI_RT_1', folderKind='screens', folderPath='MCP_ScreenFolder', action='rename', newName='MCP_ScreenFolder2', confirmDelete=False, dryRun=False),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_hmi1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
