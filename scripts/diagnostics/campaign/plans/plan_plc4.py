# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
fc_st = {"operations": [{"op": "line", "items": [{"sym": "#OUT_Sum"}, {"token": ":="}, {"sym": "#IN_A"}, {"token": "+"}, {"sym": "#IN_B"}]}]}
WT = """<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <SW.WatchAndForceTables.PlcWatchTable ID="0">
    <AttributeList>
      <Name>MCP_WT</Name>
    </AttributeList>
    <ObjectList>
      <SW.WatchAndForceTables.PlcWatchTableEntry ID="1" CompositionName="Entries">
        <AttributeList>
          <Address>%I0.0</Address>
          <DisplayFormat>Bool</DisplayFormat>
          <ModifyTrigger>Permanent</ModifyTrigger>
          <MonitorTrigger>Permanent</MonitorTrigger>
          <Name>MCP_Start</Name>
        </AttributeList>
      </SW.WatchAndForceTables.PlcWatchTableEntry>
    </ObjectList>
  </SW.WatchAndForceTables.PlcWatchTable>
</Document>
"""
plan = [
    S("PlcBuildAndImport", softwarePath=P, kind="fc", json=json.dumps({"blockName": "MCP_FC", "blockNumber": 500, "inputs": [{"name": "IN_A", "datatype": "Int"}, {"name": "IN_B", "datatype": "Int"}], "outputs": [{"name": "OUT_Sum", "datatype": "Int"}], "structuredText": fc_st}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=True, dryRun=False, keys=["verified"]),
    S("ManagePlcBlockProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="read", password="", confirmProtectionChange=False, dryRun=True, keys=["before"]),
    S("ManagePlcBlockProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="protect", password="Mcp12345", confirmProtectionChange=True, dryRun=False, keys=["after"]),
    S("ManagePlcBlockProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="unprotect", password="Mcp12345", confirmProtectionChange=True, dryRun=False, keys=["after"]),
    S("ManagePlcBlockWriteProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="read", password="", newPassword="", confirmProtectionChange=False, dryRun=True, unitName="", unitKind="unit", keys=["before"]),
    S("ManagePlcBlockWriteProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="define", password="Mcp12345", newPassword="", confirmProtectionChange=True, dryRun=False, unitName="", unitKind="unit", keys=["after"]),
    S("ManagePlcBlockWriteProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="protect", password="Mcp12345", newPassword="", confirmProtectionChange=True, dryRun=False, unitName="", unitKind="unit", keys=["after"]),
    S("ManagePlcBlockWriteProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="change", password="Mcp12345", newPassword="Mcp54321", confirmProtectionChange=True, dryRun=False, unitName="", unitKind="unit", keys=["after"]),
    S("ManagePlcBlockWriteProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="unprotect", password="Mcp54321", newPassword="", confirmProtectionChange=True, dryRun=False, unitName="", unitKind="unit", keys=["after"]),
    S("ManagePlcBlockWriteProtection", softwarePath=P, blockPath="MCP_G/MCP_FC", action="remove", password="Mcp54321", newPassword="", confirmProtectionChange=True, dryRun=False, unitName="", unitKind="unit", keys=["after"]),
    S("ExportBlock", softwarePath=P, blockPath="MCP_G/MCP_FC", exportPath=D + r"\mcp46_blocks", preservePath=False),
    S("MoveBlockToGroup", softwarePath=P, blockName="MCP_FC", targetGroupPath="MCP_G/Sub2", autoCreateGroup=False, keys=["method"]),
    S("GetBlockInfo", softwarePath=P, blockPath="MCP_G/Sub2/MCP_FC"),
    S("ImportBlock", softwarePath=P, groupPath="MCP_G", importPath=D + r"\mcp46_blocks\MCP_FC.xml", expect="any", note="same name in another group - TIA decides", keys=["verified"]),
    S("RepairAndReimportBlock", softwarePath=P, importPath=D + r"\mcp46_blocks\MCP_FC.xml", groupPath="MCP_G/Sub2", compileAfter=True, expect="any", keys=["compile"]),
    S("ImportPlcProgramFromDirectory", softwarePath=P, sourceDir=D + r"\mcp46_blocks", typeGroupPath="MCP_T", tagFolderPath="MCP_Tags", technologyFolderPath="", blockGroupPath="MCP_G/Sub2", regexName="", compileAfter=True, stopOnImportFailure=False, dryRun=False, expect="any", keys=["imported", "failed"]),
    # external sources with the real action names
    S("ManagePlcExternalSources", softwarePath=P, action="createFromFile", name="mcp46_FC.scl", unitName="", unitKind="unit", groupPath="MCP_X", filePath=D + r"\mcp46_FC.scl", libraryName="", masterCopyPath="", copyMode="", generateOption="", targetKind="", targetGroupPath="", newName="", confirmDelete=False, dryRun=False, keys=["after"]),
    S("ManagePlcExternalSources", softwarePath=P, action="read", name="mcp46_FC.scl", unitName="", unitKind="unit", groupPath="MCP_X", filePath="", libraryName="", masterCopyPath="", copyMode="", generateOption="", targetKind="", targetGroupPath="", newName="", confirmDelete=False, dryRun=True, keys=["before"]),
    S("ManagePlcExternalSources", softwarePath=P, action="generateBlocks", name="mcp46_FC.scl", unitName="", unitKind="unit", groupPath="MCP_X", filePath="", libraryName="", masterCopyPath="", copyMode="", generateOption="KeepOnError", targetKind="", targetGroupPath="", newName="", confirmDelete=False, dryRun=False, expect="any", keys=["generated"]),
    S("GenerateBlocksFromExternalSource", softwarePath=P, externalSourceName="mcp46_FC", expect="any"),
    S("ManagePlcExternalSources", softwarePath=P, action="createGroup", name="", unitName="", unitKind="unit", groupPath="MCP_X/Sub", filePath="", libraryName="", masterCopyPath="", copyMode="", generateOption="", targetKind="", targetGroupPath="", newName="", confirmDelete=False, dryRun=False, expect="any"),
    S("ManagePlcExternalSources", softwarePath=P, action="renameGroup", name="", unitName="", unitKind="unit", groupPath="MCP_X/Sub", filePath="", libraryName="", masterCopyPath="", copyMode="", generateOption="", targetKind="", targetGroupPath="", newName="Sub3", confirmDelete=False, dryRun=False, expect="any"),
    S("ManagePlcExternalSources", softwarePath=P, action="deleteGroup", name="", unitName="", unitKind="unit", groupPath="MCP_X/Sub3", filePath="", libraryName="", masterCopyPath="", copyMode="", generateOption="", targetKind="", targetGroupPath="", newName="", confirmDelete=True, dryRun=False, expect="any"),
    S("DeletePlcExternalSource", softwarePath=P, externalSourceName="mcp46_FC", expect="any"),
    # supervisions with the real providerKind
    S("ManagePlcSupervision", softwarePath=P, action="read", blockPath="", providerKind="settings", compositionName="", entryName="", typeName="", filePath="", attributesJson="{}", offset=0, limit=20, expect="any", keys=["attributes", "compositions"]),
    S("ManagePlcSupervision", softwarePath=P, action="read", blockPath="MCP_G/MCP_FB", providerKind="supervision", compositionName="", entryName="", typeName="", filePath="", attributesJson="{}", offset=0, limit=20, expect="any", keys=["attributes", "compositions"]),
    S("ExchangePlcSupervisions", softwarePath=P, action="export", filePath=D + r"\mcp46_prodiag.xlsx", importOptions="None", dryRun=False, expect="any", keys=["nativeState"]),
    # watch table via a hand-written SimaticML file
    S("WritePlcSclSourceFile", sclContent=WT, outputPath=D + r"\mcp46_wt.xml", keys=["path"]),
    S("ImportPlcWatchTableOffline", softwarePath=P, filePath=D + r"\mcp46_wt.xml", groupPath="MCP_W", dryRun=False, expect="any", keys=["after", "imported"]),
    S("GetPlcWatchTables", softwarePath=P),
    S("ManagePlcTableEntries", softwarePath=P, tableKind="watch", tablePath="MCP_W/MCP_WT", action="read", entryIndex=-1, confirmDelete=False, dryRun=True, offset=0, limit=20, expect="any", keys=["rows"]),
    S("ExportPlcWatchTable", softwarePath=P, watchTableName="MCP_WT", exportPath=D + r"\mcp46_wt_export.xml", expect="any"),
    S("ExportPlcWatchTablesToDirectory", softwarePath=P, dir=D + r"\mcp46_wt", regexName="", expect="any"),
    S("GetPlcForceTables", softwarePath=P, expect="any"),
    # technology object with a supported version
    S("ManageTechnologyObject", softwarePath=P, objectPath="MCP_Axis", action="create", typeIdentifier="TO_PositioningAxis", version="6.0", parameter="", valueJson="null", dryRun=False, expect="any", keys=["after"]),
    S("GetTechnologyObjects", softwarePath=P),
    S("ReadTechnologyObjectTree", softwarePath=P),
    S("ManageTechnologyObject", softwarePath=P, objectPath="MCP_Axis", action="read", typeIdentifier="", version="", parameter="", valueJson="null", dryRun=True, expect="any", keys=["before"]),
    S("ReadMotionAxisConfiguration", softwarePath=P, objectPath="MCP_Axis", includeParameters=True, offset=0, limit=30, expect="any", keys=["values", "parameters"]),
    S("ManageMotionAxis", softwarePath=P, objectPath="MCP_Axis", action="read", aspect="", name="", targetJson="", propertiesJson="{}", sensorIndex=-1, confirmDelete=False, dryRun=True, expect="any", keys=["before"]),
    S("ConfigureMotionHardwareConnection", softwarePath=P, objectPath="MCP_Axis", interfaceKind="actor", action="read", inputBitAddress="", outputBitAddress="", connectOption="", sensorIndex=0, dryRun=True, expect="any", keys=["before"]),
    S("ExchangeMotionCamData", softwarePath=P, objectPath="MCP_Axis", action="export", filePath=D + r"\mcp46_cam.txt", format="text", separator=";", pointCount=100, dryRun=True, expect="any", note="axis, not a cam"),
    S("ExportTechnologyObject", softwarePath=P, toName="MCP_Axis", exportPath=D + r"\mcp46_axis.xml", expect="any"),
    S("ExportTechnologyObjectsToDirectory", softwarePath=P, exportDir=D + r"\mcp46_to", regexName="", expect="any", keys=["exported"]),
    S("ManageTechnologyObject", softwarePath=P, objectPath="MCP_Axis", action="delete", typeIdentifier="", version="", parameter="", valueJson="null", dryRun=False, expect="any"),
    S("ImportTechnologyObject", softwarePath=P, folderPath="MCP_TO", importPath=D + r"\mcp46_axis.xml", expect="any", keys=["verified"]),
    S("ImportTechnologyObjectsFromDirectory", softwarePath=P, folderPath="MCP_TO", dir=D + r"\mcp46_to", regexName="", overwrite=True, expect="any"),
    S("GetTechnologyObjects", softwarePath=P),
    S("CompileAndDiagnosePlc", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_plc4.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
