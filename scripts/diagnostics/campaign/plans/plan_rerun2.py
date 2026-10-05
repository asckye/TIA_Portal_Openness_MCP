# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"; DRV = "MCP_S120"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
AX = dict(devicePath=[DRV], itemPath=["驱动轴_1"])
pkg = {"packageName": "MCP800", "tagTable": {"name": "MCP_HmiTags2", "tags": [{"name": "MCP_Run2", "dataType": "Bool"}]},
       "screen": {"name": "MCP_Screen", "width": 800, "height": 480, "items": [{"type": "Text", "name": "Title", "text": "MCP", "left": 10, "top": 10, "width": 200, "height": 30}, {"type": "Button", "name": "Btn", "text": "Start", "left": 10, "top": 60, "width": 100, "height": 40, "tag": "MCP_Run2"}]}}
plan = [
    S('WriteClassicHmiMinimalPackageFiles', note='', expect='ok', keys=[], package={'tagTable': {'name': 'MCP_HmiTags2', 'tags': [{'name': 'MCP_Run2', 'dataType': 'Bool'}]}, 'name': 'MCP800', 'screenDesign': {'items': [{'type': 'Text', 'name': 'Title', 'text': 'MCP', 'left': 10, 'top': 10, 'width': 200, 'height': 30}, {'type': 'Button', 'name': 'Btn', 'text': 'Start', 'left': 10, 'top': 60, 'width': 100, 'height': 40, 'processValueTag': 'MCP_Run2'}], 'screen': {'name': 'MCP_Screen', 'width': 800, 'height': 480}}}, outputDirectory='C:\\Users\\SIEMENS\\Desktop\\mcp47_hmi'),
    S("ImportHmiScreen", softwarePath=H, folderPath="MCP_ScreenFolder", importPath=D + r"\mcp47_hmi\MCP800_Screen.xml", expect="any", keys=["verified"], note="800x480 matches the TP700"),
    S("ListHmiScreens", softwarePath=H), S("DescribeHmiScreen", softwarePath=H, screenName="MCP_Screen", maxMembers=10, expect="any"), S("DescribeHmiScreenItem", softwarePath=H, screenName="MCP_Screen", itemName="Btn", maxMembers=10, expect="any"),
    S("ExportHmiScreen", softwarePath=H, screenName="MCP_Screen", exportPath=D + r"\mcp47_screen.xml", expect="any"),
    S("GetHmiScreenSnapshot", softwarePath=H, screenPath="/MCP_ScreenFolder/MCP_Screen", maxDepth=3, maxNodes=200, expect="any", keys=["apiCallSuccess"]),
    S("ImportHmiScreensFromDirectory", softwarePath=H, folderPath="MCP_ScreenFolder", dir=D + r"\mcp47_hmi", regexName="MCP800_Screen", overwrite=True, expect="any"),
    S("ManageSivarcScreenLayout", softwarePath=H, screenName="MCP_Screen", action="export", filePath=D + r"\mcp47_layout.yml", dryRun=False, expect="any", keys=["file"]),
    S("ExportHmiProgram", softwarePath=H, exportDir=D + r"\mcp47_hmi\program", exportScreens=True, exportTagTables=True, expect="any"),
    S("CompileHmiDiagnostics", softwarePath=H, expect="any", keys=["errorCount"]),
    S("SaveProject"),
    # remaining rerun rows
    S('ManageGlobalLibrary', note='', expect='any', keys=['after'], action='open', libraryName='MCP_GL', filePath='C:\\Users\\SIEMENS\\Desktop\\mcp46_gl\\MCP_GL\\MCP_GL.al21', destinationDirectory='', openMode='', upgrade=False, archiveName='', dryRun=False),
    S("ProbeGlobalLibrary", libraryPath=D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", maxItems=20, keys=["warnings"]),
    S('ManageGlobalLibrary', note='', expect='ok', keys=['actualCount'], action='list', libraryName='', filePath='', destinationDirectory='', openMode='', upgrade=False, archiveName='', dryRun=True),
    S('ManageGlobalLibrary', note='', expect='any', keys=[], action='close', libraryName='MCP_GL', filePath='', destinationDirectory='', openMode='', upgrade=False, archiveName='', dryRun=False),
    S("ImportOpcUaInterface", softwarePath=P, importPath=D + r"\nope_opcua.xml", interfaceType="server", expect="error", note="missing file"),
    S("RunOnlineMonitoringSafetySelfTest", keys=["ok"]),
    S('InvokeObject', note='', expect='ok', keys=[], objectKind='Device', objectPath='MCP_PLC', methodName='GetAttributeInfos', args=[], softwarePath='', allowWrite=False),
    S('InvokeService', note='', expect='any', keys=[], objectKind='Software', objectPath='', serviceTypeSuffix='PlcChecksumProvider', methodName='GetAttributeInfos', args=[], softwarePath='MCP_PLC', allowWrite=False),
    S("ManageDccChart", **AX, chartName="MCP_C47", action="create", dryRun=False, keys=["createdName"]),
    S("GetDccObject", **AX, objectPath=[{"property": "Charts", "name": "MCP_C47"}], offset=0, limit=10),
    S("ManageDccChart", **AX, chartName="MCP_C47", action="delete", confirmDelete=True, dryRun=False, keys=["verifiedAbsent"]),
    S("CreateDevice", orderNumber="OrderNumber:6AV2 128-3GB06-0AXx/20.0.0.0", version="", deviceName="MCP_UCP_OLD", expect="error", note="crash 8 guard"),
    S("CreateHardwareCatalogDevice", keyword="MTP700 Unified", deviceName="MCP_UCP2", preferredText="21.0.0.0", expect="any", keys=["attempts"]),
    S("ManageHardwareObject", devicePath=["MCP_UCP2"], action="deleteDevice", itemPath=[], destinationDevicePath=[], destinationItemPath=[], position=0, dryRun=False, expect="any", keys=["verifiedAbsent"]),
    S("ManageProjectLanguage", action="read", culture="", dryRun=True, keys=["before"]),
    S("SaveProject"),
    # project-level tools that were skipped
    S("CreateProject", directoryPath=D + r"\mcp47_projects", projectName="MCP_New", closeForeignProject=False, expect="any", note="a project is already open in the UI instance"),
    S("BuildProjectScaffold", spec=J({"projectName": "MCP_Scaffold", "directoryPath": D + r"\mcp47_projects", "plcName": "PLC_1", "plcMlfb": "6ES7 515-2FM02-0AB0", "udt": [{"name": "U1", "members": [{"name": "A", "datatype": "Bool"}]}], "compile": True, "save": True}), dryRun=True, expect="any", keys=["steps"]),
    S("SaveProjectCopy", newProjectPath=D + r"\mcp47_projects\项目1_copy\项目1_copy.ap21", expect="any", keys=["projectPath"]),
    S("GetProjectInfo"),
    S("CloseProject", expect="any"),
    S("RetrieveProjectArchive", archivePath=D + r"\mcp46_project.zap21", destinationDirectory=D + r"\mcp47_projects\retrieved", upgrade=False, dryRun=False, expect="any", keys=["projectPath"]),
    S("GetProjectInfo"),
    S("CloseProject", expect="any"),
    S("OpenProject", path="C:/Users/SIEMENS/Documents/Automation/项目1/项目1.ap21"),
    S("ListDevices"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_rerun2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
