# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; H = "HMI_RT_1"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    # safety on the F-CPU
    S("ManageSafetyGlobalSettings", action="read", propertiesJson="{}", dryRun=True, expect="any", keys=["before"]),
    S("ManagePlcSafety", softwarePath=P, action="read", runtimeGroup="", propertiesJson="{}", dryRun=True, password="", confirmSafetyChange=False, mainSafetyBlockPath="", mainSafetyInstanceDbPath="", expect="any", keys=["before", "settings"]),
    S("ReadSafetyBlockSignatures", softwarePath=P, blockPath="", includeProgramSignatures=True, offset=0, limit=20, expect="any", keys=["records", "programSignatures"]),
    S("ExportSafetyPrintout", softwarePath=P, filePath=D + r"\mcp46_safety.pdf", printer="MicrosoftPrintToPdf", option="Compact", documentLayout="", dryRun=True, expect="any"),
    S("ReadSafetyActivationTests", groupPathJson="[]", name="", includeSafetyFunctions=True, includeConditions=False, offset=0, limit=20, expect="any", keys=["records"]),
    # library
    S("ReadLibraryOverview", libraryName="", includeTypes=True, includeMasterCopies=True, maxDepth=3, maxItems=50, expect="any", keys=["header"]),
    S("ManageLibraryFolder", folderKind="masterCopies", folderPath="MCP_MC", action="create", libraryName="", newName="", dryRun=False, expect="any", keys=["after"]),
    S("ManageLibraryFolder", folderKind="types", folderPath="MCP_Types", action="create", libraryName="", newName="", dryRun=False, expect="any", keys=["after"]),
    S("CreateLibraryMasterCopy", sourceKind="block", sourcePath="MCP_G/MCP_FB", softwarePath=P, folderPath="MCP_MC", libraryName="", dryRun=False, expect="any", keys=["after"]),
    S("CreateLibraryMasterCopy", sourceKind="type", sourcePath="MCP_T/MCP_UDT", softwarePath=P, folderPath="MCP_MC", libraryName="", dryRun=False, expect="any", keys=["after"]),
    S("CreateLibraryMasterCopy", sourceKind="device", sourcePath=J(["MCP_TP700"]), softwarePath="", folderPath="MCP_MC", libraryName="", dryRun=False, expect="any", keys=["after"]),
    S("ManageLibraryMasterCopy", sourcePath="MCP_MC/MCP_FB", action="read", libraryName="", destinationLibraryName="", destinationPath="", dryRun=True, expect="any", keys=["before"]),
    S("ManageLibraryMasterCopy", sourcePath="MCP_MC/MCP_FB", action="copy", libraryName="", destinationLibraryName="", destinationPath="MCP_MC", dryRun=False, expect="any", note="copy into the same folder -> conflict expected"),
    S("ManagePlcSoftwareUnit", softwarePath=P, action="createFromMasterCopy", name="MCP_Unit2", relatedUnit="", relationType="", propertiesJson="{}", dryRun=True, unitKind="unit", commentsJson="{}", libraryName="", masterCopyPath="MCP_MC/MCP_FB", copyMode="", expect="any", note="a block master copy is not a unit"),
    S("ManagePlcAlarmTextList", softwarePath=P, action="createFromMasterCopy", name="", libraryName="", masterCopyPath="MCP_MC/MCP_FB", copyMode="", confirmDelete=False, offset=0, limit=20, dryRun=True, expect="any"),
    S("ReadLibraryOverview", libraryName="", includeTypes=True, includeMasterCopies=True, maxDepth=3, maxItems=50, expect="any", keys=["masterCopies"]),
    S("ReadLibraryType", libraryName="", typePath="", guid="", offset=0, limit=20, expect="any"),
    S("ImportLibraryTypeDocuments", filePath=D + r"\mcp46_blockdocs", folderPath="MCP_Types", libraryName="", importOptions="None", typePath="", createOptions="", targetSoftwarePath="", targetGroupKind="", targetGroupPath="", dryRun=True, expect="any"),
    S("CheckLibraryUpdates", libraryName="", updateCheckMode="ReportOutOfDateAndUpToDate", maxItems=50, expect="any", keys=["messages"]),
    S("CompareLibraries", leftLibraryName="", rightLibraryName="", includeIdentical=False, maxDepth=3, offset=0, limit=20, expect="any", note="project library with itself"),
    S("ManageGlobalLibrary", action="list", libraryName="", filePath="", destinationDirectory="", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=True, expect="any", keys=["records"]),
    S("ManageGlobalLibrary", action="infos", libraryName="", filePath="", destinationDirectory="", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=True, expect="any", keys=["records"]),
    S("ManageGlobalLibrary", action="create", libraryName="MCP_GL", filePath="", destinationDirectory=D + r"\mcp46_gl", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=False, expect="any", keys=["after"]),
    S("ManageGlobalLibrary", action="list", libraryName="", filePath="", destinationDirectory="", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=True, expect="any", keys=["records"]),
    S("ReadLibraryOverview", libraryName="MCP_GL", includeTypes=True, includeMasterCopies=True, maxDepth=2, maxItems=20, expect="any", keys=["header"]),
    S("ManageLibraryFolder", folderKind="masterCopies", folderPath="GL_MC", action="create", libraryName="MCP_GL", newName="", dryRun=False, expect="any"),
    S("ManageLibraryMasterCopy", sourcePath="MCP_MC/MCP_FB", action="copy", libraryName="", destinationLibraryName="MCP_GL", destinationPath="GL_MC", dryRun=False, expect="any", keys=["after"]),
    S("CompareLibraries", leftLibraryName="", rightLibraryName="MCP_GL", includeIdentical=False, maxDepth=3, offset=0, limit=20, expect="any", keys=["summary"]),
    S("CompareLibraryObjects", kind="masterCopy", leftPath="MCP_MC/MCP_FB", rightPath="GL_MC/MCP_FB", leftLibraryName="", rightLibraryName="MCP_GL", leftVersion="", rightVersion="", includeIdentical=False, offset=0, limit=20, expect="any"),
    S("ProbeGlobalLibrary", libraryPath=D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", maxItems=50, expect="any"),
    S("AnalyzeGlobalLibraryPackage", libraryPath=D + r"\mcp46_gl\MCP_GL", expect="any"),
    S("ImportMasterCopyFromGlobalLibrary", libraryPath=D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", masterCopyName="MCP_FB", hmiSoftwarePath=H, screenName="", importedItemName="", left=0, top=0, expect="any", note="HMI-oriented helper; block master copy"),
    S("ManageGlobalLibrary", action="save", libraryName="MCP_GL", filePath="", destinationDirectory="", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=False, expect="any"),
    S("ManageGlobalLibrary", action="archive", libraryName="MCP_GL", filePath="", destinationDirectory=D + r"\mcp46_gl", openMode="", upgrade=False, archiveName="MCP_GL_archive", archiveMode="", dryRun=False, expect="any", keys=["file"]),
    S("ManageGlobalLibrary", action="close", libraryName="MCP_GL", filePath="", destinationDirectory="", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=False, expect="any"),
    S("ManageGlobalLibrary", action="open", libraryName="", filePath=D + r"\mcp46_gl\MCP_GL\MCP_GL.al21", destinationDirectory="", openMode="ReadOnly", upgrade=False, archiveName="", archiveMode="", dryRun=False, expect="any", keys=["after"]),
    S("ManageGlobalLibrary", action="close", libraryName="MCP_GL", filePath="", destinationDirectory="", openMode="", upgrade=False, archiveName="", archiveMode="", dryRun=False, expect="any"),
    S("SynchronizeLibrary", action="updateCheck", selectionJson="[]", libraryName="", targetLibraryName="", scopeSoftwarePathsJson="[]", forceUpdateMode="", deleteUnusedVersionsMode="", structureConflictResolutionMode="", harmonizeOptionsJson="{}", cleanUpMode="", confirmChange=False, dryRun=True, expect="any"),
    S("ManageLibraryType", typePath="Nope", action="read", libraryName="", propertiesJson="{}", targetLibraryName="", scopeSoftwarePathsJson="[]", deleteUnusedVersionsMode="", structureConflictResolutionMode="", forceUpdateMode="", confirmDelete=False, dryRun=True, expect="any"),
    S("ManageLibraryTypeVersion", typePath="Nope", version="1.0.0", action="read", libraryName="", newVersion="", dependenciesMode="", author="", comment="", targetSoftwarePath="", dryRun=True, expect="any"),
    S("ManageLibraryMasterCopy", sourcePath="MCP_MC/MCP_FB", action="delete", libraryName="", destinationLibraryName="", destinationPath="", dryRun=False, expect="any", keys=["verifiedAbsent"]),
    S("ManageLibraryFolder", folderKind="types", folderPath="MCP_Types", action="delete", libraryName="", newName="", dryRun=False, expect="any"),
    # security / protection reads
    S("ReadProjectProtection", expect="any", keys=["umacAvailable"]),
    S("ReadProjectUserManagement", category="users", name="", devicePathJson="[]", itemPathJson="[]", offset=0, limit=20, expect="any"),
    S("ManagePasswordPolicy", action="read", target="", propertiesJson="{}", confirmChange=False, dryRun=True, expect="any", keys=["before"]),
    S("ManageSyslogServers", scope="project", action="read", name="", propertiesJson="{}", attributesJson="{}", devicePathJson="[]", itemPathJson="[]", serverAddress="", serverPort=0, confirmDelete=False, dryRun=True, expect="any", keys=["records"]),
    S("ManageSyslogServers", scope="device", action="read", name="", propertiesJson="{}", attributesJson="{}", devicePathJson=J([P]), itemPathJson=J([P]), serverAddress="", serverPort=0, confirmDelete=False, dryRun=True, expect="any", keys=["records"]),
    S("ManagePlcCertificate", devicePathJson=J([P]), itemPathJson=J([P]), action="list", certificateId=0, filePath="", usage="", propertiesJson="{}", assignment="", dryRun=True, assignmentItemPathJson="[]", subjectAlternativeNamesJson="[]", password="", expect="any", keys=["records"]),
    S("ManageUmcUsers", kind="user", action="read", name="", newName="", roleName="", serverUserName="", serverPassword="", confirmChange=False, dryRun=True, offset=0, limit=20, expect="any"),
    S("ManageProjectUserManagement", action="createUser", name="mcp", password="Mcp!2345678", roleName="", rightName="", comment="", group="", devicePathJson="[]", itemPathJson="[]", confirmChange=False, dryRun=True, expect="any", note="unprotected project -> refusal expected"),
    # version control
    S("GetVersionControlWorkspaces", expect="any", keys=["records"]),
    S("CreateVersionControlWorkspace", workspaceName="MCP_WS", folderPath=D + r"\mcp46_vci", expect="any", keys=["after"]),
    S("ConnectProjectToWorkspace", workspaceName="MCP_WS", dryRun=True, deviceFilter="", maxObjects=200, walkTrace=False, expect="any", keys=["summary"]),
    S("ConnectProjectToWorkspace", workspaceName="MCP_WS", dryRun=False, deviceFilter="MCP_PLC", maxObjects=200, walkTrace=False, expect="any", keys=["summary"]),
    S("SyncVersionControlWorkspace", direction="ProjectToWorkspace", workspaceName="MCP_WS", dryRun=False, changedOnly=False, expect="any", keys=["summary"]),
    S("GetVersionControlStatus", workspaceName="MCP_WS", changedOnly=False, expect="any", keys=["summary"]),
    S("SyncVersionControlWorkspace", direction="WorkspaceToProject", workspaceName="MCP_WS", dryRun=True, changedOnly=True, expect="any"),
    # portal
    S("ReadPortalInfo", includeProcesses=True, includeSessions=True, includeProducts=True, keys=["processes"]),
    S("ListPortalProcessProjects"), S("EnsureOpennessUserGroup", expect="any"),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_misc1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
