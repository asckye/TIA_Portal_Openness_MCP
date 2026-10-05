# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("InitializeEnvironment", keys=["recommendedNextTool"]), S("GetEnvironmentDiagnostics", fix=False, keys=["ok"]), S('GetToolUsage', note='', expect='ok', keys=[], query='workflow'), S("ListToolCategories"), S("FindTools", query="alarm", limit=5, category="", domain=""),
    S("RunCapabilitySelfTest", connectIfNeeded=False, includeProjectTree=False, inspectPortalProcesses=True, expectedPlcSoftwarePath=P, expectedHmiSoftwarePath=H, keys=["ok"]),
    S("ValidateAutomationContext", expectedPlcSoftwarePath=P, expectedHmiSoftwarePath=H, keys=["ok"]),
    S("RunHmiActionScriptRecipeSafetySelfTest"), S("RunOnlineMonitoringSafetySelfTest"),
    S("GetProjectInfo"), S("GetProjectSettings", folderPath="", customIdentityKey="", offset=0, limit=20, expect="any", keys=["records"]),
    S("ManageProjectLanguage", action="read", culture="", dryRun=True, expect="any", keys=["before", "languages"]),
    S("ManageProjectLanguage", action="activate", culture="en-US", dryRun=False, expect="any", keys=["after"]),
    S("ManageProjectLanguage", action="setEditing", culture="en-US", dryRun=False, expect="any", keys=["after"]),
    S('ManagePlcTagDefinition', note='2.7.45 refuses; 2.7.46 writes', expect='any', keys=[], softwarePath='MCP_PLC', tablePath='MCP_Tags/MCP_Table', name='MCP_Start', kind='tag', action='update', properties={'Comment': '启动'}, dryRun=False),
    S("ManageProjectLanguage", action="setEditing", culture="zh-CN", dryRun=False, expect="any", keys=["after"]),
    S("ManageProjectLanguage", action="deactivate", culture="en-US", dryRun=False, expect="any", keys=["after"]),
    S("ManageProjectCompilationSettings", action="read", properties={}, dryRun=True, expect="any", keys=["before"]),
    S("ManageProjectCompilationSettings", action="update", properties={"IsSimulationDuringBlockCompilationEnabled": True}, dryRun=False, expect="any", keys=["after"]),
    S("ExportProjectTexts", filePath=D + r"\mcp46_texts.xlsx", sourceCulture="zh-CN", targetCulture="en-US", dryRun=False, expect="any", keys=["file"]),
    S("ImportProjectTexts", filePath=D + r"\mcp46_texts.xlsx", updateSourceLanguage=False, dryRun=True, expect="any"),
    S("GetObjectIdentifier", kind="plcBlock", devicePath=[], itemPath=[], softwarePath=P, objectPath="MCP_G/MCP_FB", identifier="", expect="any", keys=["identifier"]),
    S("GetObjectIdentifier", kind="device", devicePath=[P], itemPath=[], softwarePath="", objectPath="", identifier="", expect="any", keys=["identifier"]),
    S("ShowObjectInEditor", kind="plcBlock", devicePath=[], itemPath=[], softwarePath=P, objectPath="MCP_G/MCP_FB", dryRun=False, expect="any"),
    S("ManageMultiuserSession", action="read", serverName="", projectName="", protocol="", host="", port=0, commitComment="", offset=0, limit=20, confirmChange=False, dryRun=True, expect="any", keys=["servers"]),
    S("CompareProjects", kind="software", softwarePath=P, devicePath=[], itemPath=[], targetProjectName="", targetSoftwarePath=P, targetDevicePath=[], targetItemPath=[], targetLibraryName="", includeIdentical=False, maxDepth=3, offset=0, limit=20, expect="any", keys=["summary"]),
    S("CompareProjects", kind="device", softwarePath="", devicePath=[P], itemPath=[], targetProjectName="", targetSoftwarePath="", targetDevicePath=["MCP_S120"], targetItemPath=[], targetLibraryName="", includeIdentical=False, maxDepth=2, offset=0, limit=20, expect="any", keys=["summary"]),
    S("RunToolTransaction", calls=[{"name": "CreatePlcBlockGroup", "arguments": {"softwarePath": P, "groupPath": "MCP_TX"}}, {"name": "ManagePlcUserGroup", "arguments": {"softwarePath": P, "family": "blocks", "groupPath": "MCP_TX", "action": "deleteEmpty", "newName": "", "dryRun": False}}], text="MCP transaction", confirmChange=True, dryRun=False, expect="any", keys=["results", "committed"]),
    # reflection
    S("DescribeObject", objectKind="Project", objectPath="", softwarePath="", maxMembers=20), S("DescribeObject", objectKind="Device", objectPath=P, softwarePath="", maxMembers=20),
    S("DescribeObjectProperty", objectKind="Software", objectPath="", propertyPath="BlockGroup", softwarePath=P, maxMembers=20, expect="any"),
    S("DescribeService", objectKind="Software", objectPath="", serviceTypeSuffix="PlcUnitProvider", softwarePath=P, maxMembers=20, expect="any"),
    S("GetObjectProperty", objectKind="Device", objectPath=P, propertyPath="Name", softwarePath="", expect="any"),
    S("ListObjectChildren", objectKind="Project", objectPath="", collectionProperty="Devices", softwarePath="", limit=20, expect="any"),
    S('InvokeService', note='', expect='any', keys=[], objectKind='Software', objectPath='', serviceTypeSuffix='PlcChecksumProvider', methodName='GetAttributeInfos', args=[], softwarePath='MCP_PLC', allowWrite=False),
    S('InvokeObject', note='', expect='any', keys=[], objectKind='Device', objectPath='MCP_PLC', methodName='GetAttributeInfos', args=[], softwarePath='', allowWrite=False),
    # exports
    {"tool": "ListExportHandles", "args": {"tool": "", "limit": 10}, "expect": "ok", "note": "", "keys": []}, S("ClearExportHandles", olderThanHours=0), {"tool": "ListExportHandles", "args": {"tool": "", "limit": 10}, "expect": "ok", "note": "", "keys": []},
    # offline validation / reports (server-side files on the VM)
    S("ExportPlcBlocksDocuments", softwarePath=P, exportPath=D + r"\mcp46_docs_all", regexName="^MCP_", preservePath=False),
    S("ExtractPlcBlockMetrics", path=D + r"\mcp46_docs_all", recursive=True, extensions=[], offset=0, limit=20, expect="any", keys=["records"]),
    S("GeneratePlcDocumentation", directory=D + r"\mcp46_docs_all", outputPath=D + r"\mcp46_docs_all\handbook.md", title="MCP", recursive=True, extensions=[], mermaidDirection="TD", expect="any", keys=["file"]),
    S("RenderPlcBlockDocument", filePath=D + r"\mcp46_docs_all\MCP_FC.s7dcl", softwarePath="", blockPath="", mermaidDirection="TD", outputPath="", maxChars=4000, expect="any"),
    S("ScanPlcSourceAnnotations", directory=D + r"\mcp46_docs_all", recursive=True, extensions=[], markers=[], offset=0, limit=20, csvPath="", expect="any"),
    S("ComparePlcBlockDocuments", leftFilePath=D + r"\mcp46_docs_all\MCP_FC.s7dcl", rightFilePath=D + r"\mcp46_vci\MCP_FC.s7dcl", softwarePath="", leftBlockPath="", rightBlockPath="", offset=0, limit=20, contextLines=2, expect="any", keys=["summary"]),
    S('AnalyzePlcSclSource', note='', expect='ok', keys=['findings'], sourceText='FUNCTION "F" : Void\nBEGIN\n IF #a THEN\n END_IF;\nEND_FUNCTION', filePath='', rules={}, limit=20),
    S("RunOfflineReleaseValidationSuite", workspaceRoot=D + r"\mcp46_release", reportDirectory=D + r"\mcp46_release\reports", expect="any", keys=["ok", "reportPath"]),
    S("RunHmiTemplatePlcSyncPrecheckSuite", templateDirectory=D + r"\mcp46_release", plcXmlPath=D + r"\mcp46_blocks", reportDirectory=D + r"\mcp46_release\reports", mappingFilePath="", expect="any"),
    S("GenerateErrorReport", errorCode="MCP-TEST", summary="test", detail="detail", recommendedNextActions="none", severity="info", outputDirectory=D + r"\mcp46_release\reports", expect="any", keys=["files"]),
    S("GenerateAcceptanceReport", outputDirectory=D + r"\mcp46_release\reports", connectIfNeeded=False, includeProjectTree=True, inspectPortalProcesses=True, title="MCP acceptance", expect="any", keys=["files"]),
    # simulation availability
    S("ListPlcSimAdvancedInstances", includeState=True, apiPath="", expect="any", keys=["records", "apiAvailable"]),
    S("SaveProject"),
    S("ArchiveSavedProject", archivePath=D + r"\mcp46_project.zap21", dryRun=False, expect="any", keys=["file"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_misc3.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
