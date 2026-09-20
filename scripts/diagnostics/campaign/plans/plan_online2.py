# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; ADP = "Siemens PLCSIM Virtual Ethernet Adapter"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOn", cpuType="", timeoutMs=90000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any", keys=["stateAfter"]),
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["instances"]),
    # offline items that do not need the PLC
    S("SetWatchTableModifyValue", softwarePath=P, tableName="MCP_WT", address="%M0.0", modifyValue="TRUE", trigger="OnceOnlyAtStart", expect="any", keys=["after", "refusedAttributes", "readbackVerified", "entryCreated"]),
    S("SetWatchTableModifyValue", softwarePath=P, tableName="MCP_WT", address="\"MCP_Start\"", modifyValue="FALSE", trigger="Permanent", expect="any", keys=["after", "refusedAttributes", "readbackVerified", "entryCreated"]),
    S("ExportPlcWatchTable", softwarePath=P, tableName="MCP_WT", exportPath=D + r"\mcp49_wt.xml", expect="any"),
    S("ImportTechnologyObject", softwarePath=P, folderPath="MCP_TO", importPath=D + r"\mcp48_pid.xml", expect="any"),
    S("GetTechnologyObjects", softwarePath=P, keys=["*"]),
    S("ExportTechnologyObject", softwarePath=P, toName="MCP_TO/MCP_PID", exportPath=D + r"\mcp49_pid_folder.xml", expect="any", keys=["exportPath"]),
    S("ExportTechnologyObjectsToDirectory", softwarePath=P, exportDir=D + r"\mcp49_to", regexName="", expect="any"),
    S("ManageTechnologyObject", softwarePath=P, objectPath="MCP_TO/MCP_PID", action="delete", typeIdentifier="", version="", parameter="", valueJson="null", dryRun=False, confirmDelete=True, expect="any", keys=["verifiedAbsent"]),
    # online family: route selection on 2.7.49 (adapter still without IP - expect a TIA-side connection error, not a route refusal)
    S("ScanAccessibleDevices", pgPcInterface=ADP, softwarePath=P, offset=0, limit=20, expect="any", keys=["devices"]),
    S("DownloadToPlc", softwarePath=P, consistentBlocksOnly=True, keepActualValues=True, startAfterDownload=True, stopBeforeDownload=True, password="", pgPcInterface=ADP, targetIpAddress="192.168.0.1", userManagementMode="", promptAnswersJson="{}", moduleAccessPassword="", blockBindingPassword="", masterSecretPassword="", rhTarget="", expect="any", keys=["pgPcRoute", "targetAddress", "targetAddressSource", "downloadState"]),
    S("GoOnline", softwarePath=P, ipAddress="192.168.0.1", password="", userName="", userType="", rhTarget="", pgPcInterface=ADP, expect="any"),
    S("GetOnlineState", softwarePath=P, expect="any"),
    S("UploadStationFromPlc", targetIpAddress="02-C0-A8-00-F1-00", pgPcInterface=ADP, password="", promptAnswersJson="{}", confirmUpload=False, dryRun=True, expect="any", keys=["targetAddress", "targetAddressSource", "addressCreation", "knownTargetAddresses"]),
    S("ReadPlcBlockFingerprints", softwarePath=P, targetIpAddress="192.168.0.1", pgPcInterface=ADP, password="", offset=0, limit=20, dryRun=True, expect="any", keys=["candidateRoutes", "selectedRoute"]),
    S("GoOffline", softwarePath=P, expect="any"),
    S("WritePlcSimAdvancedTags", instanceName="MCP_SIM", valuesJson=J({"\"MCP_Start\"": True}), confirmWrite=True, dryRun=False, apiPath="", expect="any"),
    S("RunPlcSimAdvancedTestScenario", scenarioJson=J({"instance": "MCP_SIM", "mode": "default", "stopOnFailure": True, "steps": [{"write": {"\"MCP_Start\"": False}}, {"assert": {"\"MCP_Start\"": False}}]}), confirmRun=True, dryRun=False, apiPath="", expect="any"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_online2.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
