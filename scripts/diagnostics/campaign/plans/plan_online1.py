# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"; ADP = "Siemens PLCSIM Virtual Ethernet Adapter"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["instances"]),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="register", cpuType="CPU1500_Unspecified", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", communicationInterface="TCPIP", expect="any", keys=["communicationInterface", "stateAfter"]),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOn", cpuType="", timeoutMs=90000, confirmInstanceChange=True, dryRun=False, apiPath="", communicationInterface="TCPIP", expect="any", keys=["communicationInterface", "stateAfter"]),
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["instances"]),
    S("ScanAccessibleDevices", pgPcInterface=ADP, softwarePath=P, offset=0, limit=20, expect="any", keys=["records"]),
    S("CheckDownloadReadiness", softwarePath=P, keys=["ready"]),
    S("DownloadToPlc", softwarePath=P, consistentBlocksOnly=True, keepActualValues=True, startAfterDownload=True, stopBeforeDownload=True, password="", pgPcInterface=ADP, targetIpAddress="192.168.0.1", userManagementMode="", promptAnswersJson="{}", moduleAccessPassword="", blockBindingPassword="", masterSecretPassword="", rhTarget="", expect="any", keys=["state", "errors", "prompts"]),
    S("GetOnlineState", softwarePath=P, expect="any", keys=["state"]),
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["instances"]),
    S("GoOnline", softwarePath=P, ipAddress="192.168.0.1", password="", userName="", userType="", rhTarget="", expect="any", keys=["state"]),
    S("GetOnlineState", softwarePath=P, expect="any", keys=["state"]),
    S("CompareSoftwareToOnline", softwarePath=P, maxDepth=3, maxEntries=50, expect="any", keys=["summary"]),
    S("ReadPlcBlockFingerprints", softwarePath=P, targetIpAddress="192.168.0.1", pgPcInterface=ADP, password="", offset=0, limit=20, dryRun=False, expect="any", keys=["records"]),
    S("ManagePlcDataBlockSnapshot", softwarePath=P, blockPath="MCP_G/MCP_DB", action="createSnapshot", filePath="", confirmValueChange=True, dryRun=False, expect="any"),
    S("ManagePlcDataBlockSnapshot", softwarePath=P, blockPath="MCP_G/MCP_DB", action="exportSnapshot", filePath=D + r"\mcp48_snapshot.xml", confirmValueChange=False, dryRun=False, expect="any", keys=["file"]),
    S("SetWatchTableModifyValue", softwarePath=P, tableName="MCP_WT", address="%M0.0", modifyValue="TRUE", trigger="OnceOnlyAtStart", expect="any"),
    S("UploadDeviceParameters", devicePathJson=J([P]), itemPathJson=J([P]), targetIpAddress="192.168.0.1", pgPcInterface=ADP, password="", promptAnswersJson="{}", confirmUpload=False, dryRun=True, expect="any"),
    S("UploadStationFromPlc", targetIpAddress="192.168.0.1", pgPcInterface=ADP, password="", promptAnswersJson="{}", confirmUpload=False, dryRun=True, expect="any"),
    S("GoOffline", softwarePath=P, expect="any"),
    S("GoOfflineAll", expect="any"),
    S("ReadPlcSimAdvancedTags", instanceName="MCP_SIM", namesJson="[]", areaFilter="", nameContains="MCP", offset=0, limit=20, apiPath="", expect="any", keys=["items", "total"]),
    S("ReadPlcSimAdvancedTags", instanceName="MCP_SIM", namesJson=J(["\"MCP_Start\"", "\"MCP_DB\".Counter"]), areaFilter="", nameContains="", offset=0, limit=20, apiPath="", expect="any", keys=["items"]),
    S("WritePlcSimAdvancedTags", instanceName="MCP_SIM", valuesJson=J({"\"MCP_Start\"": True}), confirmWrite=True, dryRun=False, apiPath="", expect="any", keys=["items"]),
    S("RunPlcSimAdvancedTestScenario", scenarioJson=J({"instance": "MCP_SIM", "mode": "default", "stopOnFailure": True, "steps": [{"write": {"\"MCP_Start\"": False}}, {"waitMs": 200}, {"assert": {"\"MCP_Start\"": False}}]}), confirmRun=True, dryRun=False, apiPath="", expect="any", keys=["steps", "passed"]),
    S("ProbeS7CpuIdentity", ip="192.168.0.1", rack=0, slot=1, expect="any"),
    S("GetPlcRunStateS7", ip="192.168.0.1", rack=0, slot=1, maxDiagEntries=5, expectModuleContains="", expect="any"),
    S("ReadPlcLiveValuesS7", ip="192.168.0.1", itemsJson=J(["M0.0", "DB500.DBW0:INT"]), rack=0, slot=1, expectModuleContains="", devicePath="", expect="any", keys=["values"]),
    S("ReadPlcWebDiagnostics", host="192.168.0.1", username="Anonymous", password="", ignoreCertificateErrors=True, timeoutMs=5000, expect="any"),
    S("ReadPlcWebVars", host="192.168.0.1", username="Anonymous", password="", varsJson=J(["\"MCP_DB\".Counter"]), ignoreCertificateErrors=True, timeoutMs=5000, expect="any"),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="stop", cpuType="", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOff", cpuType="", timeoutMs=60000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="unregister", cpuType="", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("SaveProject"),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_online1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
