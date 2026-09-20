# -*- coding: utf-8 -*-
"""Rebuild the PLC content of the scratch project after a TIA restart (groups, UDT, tag table, DB, FC, FB, instance DB, watch table)."""
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
fc_st = {"operations": [{"op": "line", "items": [{"sym": "#OUT_Sum"}, {"token": ":="}, {"sym": "#IN_A"}, {"token": "+"}, {"sym": "#IN_B"}]}]}
fb_st = {"operations": [{"op": "assignment", "target": "#Busy", "source": "#Enable"}, {"op": "if", "condition": "#Enable"}, {"op": "line", "items": [{"sym": "#Count"}, {"token": ":="}, {"sym": "#Count"}, {"token": "+"}, {"lit": "1"}], "indent": 1}, {"op": "endif"}]}
WT = """<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <SW.WatchAndForceTables.PlcWatchTable ID="0">
    <AttributeList><Name>MCP_WT</Name></AttributeList>
    <ObjectList>
      <SW.WatchAndForceTables.PlcWatchTableEntry ID="1" CompositionName="Entries">
        <AttributeList><Address>%I0.0</Address><DisplayFormat>Bool</DisplayFormat><ModifyTrigger>Permanent</ModifyTrigger><MonitorTrigger>Permanent</MonitorTrigger><Name>MCP_Start</Name></AttributeList>
      </SW.WatchAndForceTables.PlcWatchTableEntry>
    </ObjectList>
  </SW.WatchAndForceTables.PlcWatchTable>
</Document>
"""
plan = [
    S("CreatePlcBlockGroup", softwarePath=P, groupPath="MCP_G/Sub2"),
    S("CreatePlcTypeGroup", softwarePath=P, groupPath="MCP_T", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="tags", groupPath="MCP_Tags", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="watchTables", groupPath="MCP_W", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="externalSources", groupPath="MCP_X", action="create", newName="", dryRun=False),
    S("ManagePlcUserGroup", softwarePath=P, family="technology", groupPath="MCP_TO", action="create", newName="", dryRun=False),
    S("PlcBuildAndImport", softwarePath=P, kind="udt", json=json.dumps({"name": "MCP_UDT", "members": [{"name": "Run", "datatype": "Bool", "commentZhCn": "运行"}, {"name": "Speed", "datatype": "Real"}]}), typeGroupPath="MCP_T", tagFolderPath="", blockGroupPath="", compileAfter=False, dryRun=False),
    S("PlcBuildAndImport", softwarePath=P, kind="tagtable", json=json.dumps({"tableName": "MCP_Table", "tags": [{"name": "MCP_Start", "dataTypeName": "Bool", "logicalAddress": "%I0.0"}, {"name": "MCP_Speed", "dataTypeName": "Int", "logicalAddress": "%IW10"}]}), typeGroupPath="", tagFolderPath="MCP_Tags", blockGroupPath="", compileAfter=False, dryRun=False),
    S("PlcBuildAndImport", softwarePath=P, kind="globaldb", json=json.dumps({"dbName": "MCP_DB", "dbNumber": 500, "staticMembers": [{"name": "Counter", "datatype": "Int", "startValue": "5"}, {"name": "Motor", "datatype": "\"MCP_UDT\""}]}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False),
    S("PlcBuildAndImport", softwarePath=P, kind="fc", json=json.dumps({"blockName": "MCP_FC", "blockNumber": 500, "inputs": [{"name": "IN_A", "datatype": "Int"}, {"name": "IN_B", "datatype": "Int"}], "outputs": [{"name": "OUT_Sum", "datatype": "Int"}], "structuredText": fc_st}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False),
    S("PlcBuildAndImport", softwarePath=P, kind="fb", json=json.dumps({"blockName": "MCP_FB", "blockNumber": 500, "inputs": [{"name": "Enable", "datatype": "Bool"}], "outputs": [{"name": "Busy", "datatype": "Bool"}], "statics": [{"name": "Count", "datatype": "Int"}], "structuredText": fb_st}), typeGroupPath="", tagFolderPath="", blockGroupPath="MCP_G", compileAfter=False, dryRun=False),
    S("CreatePlcInstanceDb", softwarePath=P, fbPath="MCP_G/MCP_FB", name="MCP_FB_DB", groupPath="MCP_G", autoNumber=True, number=1, dryRun=False),
    S("WritePlcSclSourceFile", sclContent=WT, outputPath=D + r"\mcp46_wt.xml"),
    S("ImportPlcWatchTableOffline", softwarePath=P, filePath=D + r"\mcp46_wt.xml", groupPath="MCP_W", dryRun=False),
    S("CompileAndDiagnosePlc", softwarePath=P, password="", keys=["errorCount", "warningCount"]),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_base.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
