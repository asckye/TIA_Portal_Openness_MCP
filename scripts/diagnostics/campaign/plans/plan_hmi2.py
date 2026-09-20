# -*- coding: utf-8 -*-
import json, io, os
H = "HMI_RT_1"; D = r"C:\Users\SIEMENS\Desktop"; P = "MCP_PLC"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
TT = D + r"\mcp46_hmi\Classic_HMI_Minimal_Package_TagTable.xml"; SC = D + r"\mcp46_hmi\Classic_HMI_Minimal_Package_Screen.xml"
plan = [
    S("ManageClassicHmiFolder", softwarePath=H, folderKind="tags", folderPath="", action="create", newName="MCP_TagFolder", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageClassicHmiFolder", softwarePath=H, folderKind="screens", folderPath="", action="create", newName="MCP_ScreenFolder", confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageClassicHmiFolder", softwarePath=H, folderKind="screens", folderPath="MCP_ScreenFolder", action="read", newName="", confirmDelete=False, dryRun=True, expect="any", keys=["before"]),
    S("ImportHmiTagTable", softwarePath=H, folderPath="MCP_TagFolder", importPath=TT, expect="any", keys=["verified"]),
    S("ImportHmiScreen", softwarePath=H, folderPath="MCP_ScreenFolder", importPath=SC, expect="any", keys=["verified"]),
    S("GetHmiTagTables", softwarePath=H), S("GetHmiTags", softwarePath=H, tagTableName="MCP_HmiTags", expect="any"), S("GetHmiTags", softwarePath=H, tagTableName="NoSuchTable", expect="any", note="should be NotFound"), S("GetHmiScreens", softwarePath=H),
    S("DescribeHmiScreen", softwarePath=H, screenName="MCP_Screen", maxMembers=20, expect="any"),
    S("DescribeHmiScreenItem", softwarePath=H, screenName="MCP_Screen", itemName="Btn", maxMembers=20, expect="any"),
    S("DescribeHmiTagTable", softwarePath=H, tagTableName="MCP_HmiTags", maxMembers=20, expect="any"),
    S("DescribeHmiTag", softwarePath=H, tagTableName="MCP_HmiTags", tagName="MCP_Run", maxMembers=20, expect="any"),
    S("ReadHmiScreenSnapshot", softwarePath=H, screenPath="/MCP_ScreenFolder/MCP_Screen", maxDepth=3, maxNodes=200, expect="any", keys=["apiCallSuccess", "dataComplete"]),
    S("ExportHmiScreen", softwarePath=H, screenName="MCP_Screen", exportPath=D + r"\mcp46_hmi\MCP_Screen_export.xml", expect="any"),
    S("ExportHmiTagTable", softwarePath=H, tagTableName="MCP_HmiTags", exportPath=D + r"\mcp46_hmi\MCP_HmiTags_export.xml", expect="any"),
    S("ExportHmiProgram", softwarePath=H, exportDir=D + r"\mcp46_hmi\program", exportScreens=True, exportTagTables=True, expect="any", keys=["exported", "failed"]),
    S("ImportHmiScreensFromDirectory", softwarePath=H, folderPath="MCP_ScreenFolder", dir=D + r"\mcp46_hmi\program", regexName="MCP_Screen", overwrite=True, expect="any"),
    S("ImportHmiTagTablesFromDirectory", softwarePath=H, folderPath="MCP_TagFolder", dir=D + r"\mcp46_hmi\program", regexName="MCP_HmiTags", overwrite=True, expect="any"),
    S("ManageClassicHmiScreenObject", softwarePath=H, objectKind="popup", objectPath="", action="read", filePath="", importOptions="None", confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiScreenObject", softwarePath=H, objectKind="template", objectPath="", action="read", filePath="", importOptions="None", confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiScreenObject", softwarePath=H, objectKind="slidein", objectPath="", action="read", filePath="", importOptions="None", confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiScreenObject", softwarePath=H, objectKind="overview", objectPath="", action="read", filePath="", importOptions="None", confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiScreenObject", softwarePath=H, objectKind="globalElements", objectPath="", action="export", filePath=D + r"\mcp46_hmi\global.xml", importOptions="None", confirmDelete=False, dryRun=False, expect="any", keys=["file"]),
    S("ManageClassicHmiScreenObject", softwarePath=H, objectKind="template", objectPath="", action="export", filePath=D + r"\mcp46_hmi\templates.xml", importOptions="None", confirmDelete=False, dryRun=False, expect="any", keys=["file"]),
    S("ManageClassicHmiCycle", softwarePath=H, action="export", cycleName="100 ms", filePath=D + r"\mcp46_hmi\cycle.xml", importOptions="None", attributesJson="{}", offset=0, limit=20, confirmDelete=False, dryRun=False, expect="any", keys=["file"]),
    S("ManageClassicHmiCycle", softwarePath=H, action="read", cycleName="100 ms", filePath="", importOptions="None", attributesJson="{}", offset=0, limit=20, confirmDelete=False, dryRun=True, expect="any", keys=["rows"]),
    S("ManageClassicHmiTextGraphicList", softwarePath=H, listKind="text", action="create", listName="MCP_TextList", compositionName="", entryName="", typeName="", filePath="", importOptions="None", attributesJson="{}", offset=0, limit=20, confirmDelete=False, dryRun=False, expect="any", keys=["after"]),
    S("ManageClassicHmiTextGraphicList", softwarePath=H, listKind="text", action="read", listName="MCP_TextList", compositionName="", entryName="", typeName="", filePath="", importOptions="None", attributesJson="{}", offset=0, limit=20, confirmDelete=False, dryRun=True, expect="any", keys=["rows", "compositions"]),
    S("ManageClassicHmiTextGraphicList", softwarePath=H, listKind="text", action="export", listName="MCP_TextList", compositionName="", entryName="", typeName="", filePath=D + r"\mcp46_hmi\textlist.xml", importOptions="None", attributesJson="{}", offset=0, limit=20, confirmDelete=False, dryRun=False, expect="any", keys=["file"]),
    S("ManageClassicHmiTextGraphicList", softwarePath=H, listKind="text", action="delete", listName="MCP_TextList", compositionName="", entryName="", typeName="", filePath="", importOptions="None", attributesJson="{}", offset=0, limit=20, confirmDelete=True, dryRun=False, expect="any"),
    S("ManageClassicHmiScript", softwarePath=H, scriptPath="MCP_Scripts", action="createFolder", filePath="", importOptions="None", attributesJson="{}", confirmDelete=False, dryRun=False, expect="any"),
    S("ReadClassicHmiScripts", softwarePath=H, folderPath="", offset=0, limit=20, expect="any", keys=["rows", "folders"]),
    S("ManageSivarcScreenLayout", softwarePath=H, screenName="MCP_Screen", action="export", filePath=D + r"\mcp46_hmi\layout.yml", dryRun=False, expect="any"),
    S("ManageCommunicationConnection", devicePathJson=J([P]), itemPathJson=J([P]), action="create", connectionType="HmiConnection", connectionName="MCP_HMI_Conn", localInterfaceItemPathJson=J([P, "PROFINET 接口_1"]), localNodeName="", partnerDevicePathJson=J(["MCP_TP700"]), partnerItemPathJson=J(["HMI_RT_1"]), partnerInterfaceItemPathJson=J(["MCP_TP700.IE_CP_1", "PROFINET Interface_1"]), partnerNodeName="", confirmDelete=False, dryRun=False, expect="any", keys=["after"], note="engine 2.7.45 still lacks CommunicationManagement"),
    S("GetHmiConnections", softwarePath=H),
    S("CompileAndDiagnoseHmi", softwarePath=H, expect="any", keys=["errorCount", "errors"]),
    S("ManageClassicHmiFolder", softwarePath=H, folderKind="screens", folderPath="MCP_ScreenFolder2", action="delete", newName="", confirmDelete=True, dryRun=True, expect="any"),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_hmi2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
