# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
GL = dict(filePath="", destinationDirectory="", openMode="ReadOnly", upgrade=False, archiveName="", archiveMode="Compressed", dryRun=False)
plan = [
    S("ManageGlobalLibrary", action="create", libraryName="MCP_GL", **{**GL, "destinationDirectory": D + r"\mcp46_gl", "openMode": "ReadWrite"}, expect="any", keys=["after"]),
    S("ManageGlobalLibrary", action="list", libraryName="", **{**GL, "dryRun": True}, expect="any", keys=["records"]),
    S("GetLibraryOverview", libraryName="MCP_GL", includeTypes=True, includeMasterCopies=True, maxDepth=2, maxItems=20, expect="any", keys=["header"]),
    S("ManageLibraryFolder", folderKind="masterCopies", folderPath="GL_MC", action="create", libraryName="MCP_GL", newName="", dryRun=False, expect="any"),
    S("ManageLibraryFolder", folderKind="types", folderPath="GL_T", action="create", libraryName="MCP_GL", newName="", dryRun=False, expect="any"),
    S("CreateLibraryMasterCopy", sourceKind="block", sourcePath="MCP_G/MCP_FB", softwarePath=P, folderPath="GL_MC", libraryName="MCP_GL", dryRun=False, expect="any", keys=["after"]),
    S("ManageLibraryMasterCopy", sourcePath="MCP_MC/MCP_UDT", action="copy", libraryName="", destinationLibraryName="MCP_GL", destinationPath="GL_MC", dryRun=False, expect="any", keys=["after"]),
    S("ManageLibraryMasterCopy", sourcePath="GL_MC/MCP_FB", action="compare", libraryName="MCP_GL", destinationLibraryName="", destinationPath="MCP_MC/MCP_UDT", dryRun=True, expect="any"),
    S("CompareLibraries", leftLibraryName="", rightLibraryName="MCP_GL", includeIdentical=True, maxDepth=3, offset=0, limit=20, expect="any", keys=["summary"]),
    S('ImportLibraryTypeDocuments', note='', expect='any', keys=['after', 'created'], filePath='C:\\Users\\SIEMENS\\Desktop\\mcp46_blockdocs\\MCP_FB.s7dcl', folderPath='GL_T', libraryName='MCP_GL', importOptions='None', typePath='', targetSoftwarePath='', targetGroupKind='', targetGroupPath='', dryRun=False),
    S("GetLibraryOverview", libraryName="MCP_GL", includeTypes=True, includeMasterCopies=True, maxDepth=3, maxItems=20, expect="any", keys=["types"]),
    S("GetLibraryType", libraryName="MCP_GL", typePath="GL_T/MCP_FB", guid="", offset=0, limit=20, expect="any", keys=["type", "versions"]),
    S("ManageLibraryTypeVersion", typePath="GL_T/MCP_FB", version="", action="read", libraryName="MCP_GL", newVersion="", dependenciesMode="", author="", comment="", targetSoftwarePath="", dryRun=True, expect="any", keys=["before"]),
    S("ManageLibraryTypeVersion", typePath="GL_T/MCP_FB", version="", action="findInstances", libraryName="MCP_GL", newVersion="", dependenciesMode="", author="", comment="", targetSoftwarePath="", dryRun=True, expect="any", keys=["instances"]),
    S("CompareLibraryObjects", kind="type", leftPath="GL_T/MCP_FB", rightPath="GL_T/MCP_FB", leftLibraryName="MCP_GL", rightLibraryName="MCP_GL", leftVersion="", rightVersion="", includeIdentical=True, offset=0, limit=20, expect="any"),
    S('ManageLibraryType', note='', expect='any', keys=['after'], typePath='GL_T/MCP_FB', action='update', libraryName='MCP_GL', properties={'DoNotUse': True}, targetLibraryName='', scopeSoftwarePaths=[], confirmDelete=False, dryRun=False),
    S('CheckLibraryUpdates', note='', expect='any', keys=[], libraryName='MCP_GL'),
    S("CheckLibraryUpdates", libraryName="MCP_GL", updateCheckMode="ReportOutOfDateAndUpToDate", maxItems=50, expect="any", keys=["messages"]),
    S('ManageLibraryType', note='', expect='any', keys=['verifiedAbsent'], typePath='GL_T/MCP_FB', action='delete', libraryName='MCP_GL', properties={}, targetLibraryName='', scopeSoftwarePaths=[], confirmDelete=True, dryRun=False),
    S("ManageGlobalLibrary", action="save", libraryName="MCP_GL", **GL, expect="any"),
    S("ManageGlobalLibrary", action="archive", libraryName="MCP_GL", **{**GL, "destinationDirectory": D + r"\mcp46_gl", "archiveName": "MCP_GL_archive"}, expect="any", keys=["created"]),
    S("ManageGlobalLibrary", action="saveAs", libraryName="MCP_GL", **{**GL, "destinationDirectory": D + r"\mcp46_gl2"}, expect="any"),
    S("ManageGlobalLibrary", action="close", libraryName="MCP_GL", **GL, expect="any"),
    S("ManageGlobalLibrary", action="open", libraryName="MCP_GL", **{**GL, "filePath": D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", "openMode": "ReadOnly"}, expect="any", keys=["after"]),
    S("ProbeGlobalLibrary", libraryPath=D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", maxItems=50, expect="any"),
    S("ImportMasterCopyFromGlobalLibrary", libraryPath=D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", masterCopyName="MCP_FB", hmiSoftwarePath=H, screenName="", importedItemName="", left=0, top=0, expect="any", note="HMI-oriented helper; block master copy"),
    S("ManageGlobalLibrary", action="close", libraryName="MCP_GL", **GL, expect="any"),
    S("ManageGlobalLibrary", action="retrieve", libraryName="MCP_GL", **{**GL, "filePath": D + r"\mcp46_gl\MCP_GL_archive.zal21", "destinationDirectory": D + r"\mcp46_gl3", "openMode": "ReadWrite"}, expect="any", keys=["after"]),
    S("ManageGlobalLibrary", action="close", libraryName="MCP_GL", **GL, expect="any"),
    # VCI (the export tool creates the folder)
    S("ExportPlcBlocksDocuments", softwarePath=P, exportPath=D + r"\mcp46_vci", regexName="^MCP_FC$", preservePath=False),
    S("CreateVersionControlWorkspace", workspaceName="MCP_WS", folderPath=D + r"\mcp46_vci", expect="any", keys=["after"]),
    S("ListVersionControlWorkspaces", expect="any", keys=["records"]),
    S("ConnectProjectToWorkspace", workspaceName="MCP_WS", dryRun=True, deviceFilter="", maxObjects=200, walkTrace=False, expect="any", keys=["summary", "mapped"]),
    S("ConnectProjectToWorkspace", workspaceName="MCP_WS", dryRun=False, deviceFilter="MCP_PLC", maxObjects=200, walkTrace=False, expect="any", keys=["summary"]),
    S("SynchronizeVersionControlWorkspace", direction="ProjectToWorkspace", workspaceName="MCP_WS", dryRun=False, changedOnly=False, expect="any", keys=["summary"]),
    S("GetVersionControlStatus", workspaceName="MCP_WS", changedOnly=False, expect="any", keys=["summary"]),
    S("SynchronizeVersionControlWorkspace", direction="WorkspaceToProject", workspaceName="MCP_WS", dryRun=True, changedOnly=True, expect="any"),
    S("ManagePlcCertificate", devicePath=[P], itemPath=[P], action="list", certificateId="", filePath="", usage="", properties={}, assignment="", dryRun=True, assignmentItemPath=[], subjectAlternativeNames=[], password="", expect="any", keys=["records"]),
    S("ManageSyslogServers", scope="plc", action="read", name="", properties={}, attributes={}, devicePath=[P], itemPath=[P], serverAddress="", serverPort=0, confirmDelete=False, dryRun=True, expect="any", keys=["records"]),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_misc2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
