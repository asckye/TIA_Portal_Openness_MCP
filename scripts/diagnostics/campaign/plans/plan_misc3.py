# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("Bootstrap", keys=["recommendedNextTool"]), S("Doctor", fix=False, keys=["ok"]), S("GetAuthoringGuide", topic="workflow"), S("ListToolCategories"), S("FindTools", query="alarm", limit=5, category="", domain=""),
    S("RunCapabilitySelfTest", connectIfNeeded=False, includeProjectTree=False, inspectPortalProcesses=True, expectedPlcSoftwarePath=P, expectedHmiSoftwarePath=H, keys=["ok"]),
    S("ValidateAutomationContext", expectedPlcSoftwarePath=P, expectedHmiSoftwarePath=H, keys=["ok"]),
    S("RunHmiActionScriptRecipeSafetySelfTest"), S("RunOnlineMonitoringSafetySelfTest"),
    S("GetProject"), S("ReadProjectSettings", folderPath="", customIdentityKey="", offset=0, limit=20, expect="any", keys=["records"]),
    S("ManageProjectLanguage", action="read", culture="", dryRun=True, expect="any", keys=["before", "languages"]),
    S("ManageProjectLanguage", action="activate", culture="en-US", dryRun=False, expect="any", keys=["after"]),
    S("ManageProjectLanguage", action="setEditing", culture="en-US", dryRun=False, expect="any", keys=["after"]),
    S("ManagePlcTagDefinition", softwarePath=P, tablePath="MCP_Tags/MCP_Table", name="MCP_Start", kind="tag", action="update", propertiesJson=J({"Comment": {"zh-CN": "启动"}}), dryRun=False, expect="any", note="2.7.45 refuses; 2.7.46 writes"),
    S("ManageProjectLanguage", action="setEditing", culture="zh-CN", dryRun=False, expect="any", keys=["after"]),
    S("ManageProjectLanguage", action="deactivate", culture="en-US", dryRun=False, expect="any", keys=["after"]),
    S("ManageProjectCompilationSettings", action="read", propertiesJson="{}", dryRun=True, expect="any", keys=["before"]),
    S("ManageProjectCompilationSettings", action="update", propertiesJson=J({"IsSimulationDuringBlockCompilationEnabled": True}), dryRun=False, expect="any", keys=["after"]),
    S("ExportProjectTexts", filePath=D + r"\mcp46_texts.xlsx", sourceCulture="zh-CN", targetCulture="en-US", dryRun=False, expect="any", keys=["file"]),
    S("ImportProjectTexts", filePath=D + r"\mcp46_texts.xlsx", updateSourceLanguage=False, dryRun=True, expect="any"),
    S("ReadObjectIdentifier", kind="plcBlock", devicePathJson="[]", itemPathJson="[]", softwarePath=P, objectPath="MCP_G/MCP_FB", identifier="", expect="any", keys=["identifier"]),
    S("ReadObjectIdentifier", kind="device", devicePathJson=J([P]), itemPathJson="[]", softwarePath="", objectPath="", identifier="", expect="any", keys=["identifier"]),
    S("ShowObjectInEditor", kind="plcBlock", devicePathJson="[]", itemPathJson="[]", softwarePath=P, objectPath="MCP_G/MCP_FB", dryRun=False, expect="any"),
    S("ManageMultiuserSession", action="read", serverName="", projectName="", protocol="", host="", port=0, commitComment="", offset=0, limit=20, confirmChange=False, dryRun=True, expect="any", keys=["servers"]),
    S("CompareProjects", kind="software", softwarePath=P, devicePathJson="[]", itemPathJson="[]", targetProjectName="", targetSoftwarePath=P, targetDevicePathJson="[]", targetItemPathJson="[]", targetLibraryName="", includeIdentical=False, maxDepth=3, offset=0, limit=20, expect="any", keys=["summary"]),
    S("CompareProjects", kind="device", softwarePath="", devicePathJson=J([P]), itemPathJson="[]", targetProjectName="", targetSoftwarePath="", targetDevicePathJson=J(["MCP_S120"]), targetItemPathJson="[]", targetLibraryName="", includeIdentical=False, maxDepth=2, offset=0, limit=20, expect="any", keys=["summary"]),
    S("RunToolsInTransaction", callsJson=J([{"name": "CreatePlcBlockGroup", "arguments": {"softwarePath": P, "groupPath": "MCP_TX"}}, {"name": "ManagePlcUserGroup", "arguments": {"softwarePath": P, "family": "blocks", "groupPath": "MCP_TX", "action": "deleteEmpty", "newName": "", "dryRun": False}}]), text="MCP transaction", confirmChange=True, dryRun=False, expect="any", keys=["results", "committed"]),
    # reflection
    S("DescribeObject", objectKind="Project", objectPath="", softwarePath="", maxMembers=20), S("DescribeObject", objectKind="Device", objectPath=P, softwarePath="", maxMembers=20),
    S("DescribeObjectProperty", objectKind="Software", objectPath="", propertyPath="BlockGroup", softwarePath=P, maxMembers=20, expect="any"),
    S("DescribeService", objectKind="Software", objectPath="", serviceTypeSuffix="PlcUnitProvider", softwarePath=P, maxMembers=20, expect="any"),
    S("GetObjectProperty", objectKind="Device", objectPath=P, propertyPath="Name", softwarePath="", expect="any"),
    S("ListObjectChildren", objectKind="Project", objectPath="", collectionProperty="Devices", softwarePath="", limit=20, expect="any"),
    S("InvokeService", objectKind="Software", objectPath="", serviceTypeSuffix="PlcChecksumProvider", methodName="GetAttributeInfos", args="[]", softwarePath=P, allowWrite=False, expect="any"),
    S("InvokeObject", objectKind="Device", objectPath=P, methodName="GetAttributeInfos", args="[]", softwarePath="", allowWrite=False, expect="any"),
    # exports
    {"tool": "ListExports", "args": {"tool": "", "limit": 10}, "expect": "ok", "note": "", "keys": []}, S("ClearExports", olderThanHours=0), {"tool": "ListExports", "args": {"tool": "", "limit": 10}, "expect": "ok", "note": "", "keys": []},
    # offline validation / reports (server-side files on the VM)
    S("ExportBlocksAsDocuments", softwarePath=P, exportPath=D + r"\mcp46_docs_all", regexName="^MCP_", preservePath=False),
    S("ExtractPlcBlockMetrics", path=D + r"\mcp46_docs_all", recursive=True, extensionsJson="[]", offset=0, limit=20, expect="any", keys=["records"]),
    S("GeneratePlcDocumentation", directory=D + r"\mcp46_docs_all", outputPath=D + r"\mcp46_docs_all\handbook.md", title="MCP", recursive=True, extensionsJson="[]", mermaidDirection="TD", expect="any", keys=["file"]),
    S("RenderPlcBlockDocument", filePath=D + r"\mcp46_docs_all\MCP_FC.s7dcl", softwarePath="", blockPath="", mermaidDirection="TD", outputPath="", maxChars=4000, expect="any"),
    S("ScanPlcSourceAnnotations", directory=D + r"\mcp46_docs_all", recursive=True, extensionsJson="[]", markersJson="[]", offset=0, limit=20, csvPath="", expect="any"),
    S("ComparePlcBlockDocuments", leftFilePath=D + r"\mcp46_docs_all\MCP_FC.s7dcl", rightFilePath=D + r"\mcp46_vci\MCP_FC.s7dcl", softwarePath="", leftBlockPath="", rightBlockPath="", offset=0, limit=20, contextLines=2, expect="any", keys=["summary"]),
    S("LintPlcSclSource", sourceText="FUNCTION \"F\" : Void\nBEGIN\n IF #a THEN\n END_IF;\nEND_FUNCTION", filePath="", rulesJson="[]", limit=20, keys=["findings"]),
    S("RunOfflineReleaseValidationSuite", workspaceRoot=D + r"\mcp46_release", reportDirectory=D + r"\mcp46_release\reports", expect="any", keys=["ok", "reportPath"]),
    S("RunHmiTemplatePlcSyncPrecheckSuite", templateDirectory=D + r"\mcp46_release", plcXmlPath=D + r"\mcp46_blocks", reportDirectory=D + r"\mcp46_release\reports", mappingFilePath="", expect="any"),
    S("GenerateErrorReport", errorCode="MCP-TEST", summary="test", detail="detail", recommendedNextActions="none", severity="info", outputDirectory=D + r"\mcp46_release\reports", expect="any", keys=["files"]),
    S("GenerateAcceptanceReport", outputDirectory=D + r"\mcp46_release\reports", connectIfNeeded=False, includeProjectTree=True, inspectPortalProcesses=True, title="MCP acceptance", expect="any", keys=["files"]),
    # simulation availability
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", expect="any", keys=["records", "apiAvailable"]),
    S("SaveProject"),
    S("ArchiveSavedProject", archivePath=D + r"\mcp46_project.zap21", dryRun=False, expect="any", keys=["file"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_misc3.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
