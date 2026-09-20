# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["records", "instances"]),
    S("ProbePlcMonitorOnlineCapabilities", softwarePath=P, expect="any"),
    S("PlanOnlineReadOnlyMonitoring", softwarePath=P, tagPathsJson=J(["MCP_Start"]), mode="watch", expect="any"),
    S("PlanOnlineReadOnlyDataProvider", provider="s7-readonly", endpoint="192.168.0.1", tagPathsJson=J(["M0.0"]), optionsJson="{}", expect="any"),
    S("ReadPlcWatchTableCurrentValuesReadOnly", softwarePath=P, watchTableName="MCP_WT", maxEntries=10, expect="any"),
    S("TraceTagCause", softwarePath=P, tag="MCP_Start", blockScope="", expect="any", keys=["writers"]),
    S("CheckDownloadReadiness", softwarePath=P, expect="any", keys=["ready", "checks"]),
    S("ReadTransferRoutes", softwarePath=P, maxItems=50, expect="any", keys=["modes"]),
    S("GetOnlineState", softwarePath=P, expect="any", keys=["state"]),
    S("ScanAccessibleDevices", pgPcInterface="PLCSIM", softwarePath=P, offset=0, limit=20, expect="any", keys=["records"]),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="read", cpuType="", timeoutMs=30000, confirmInstanceChange=False, dryRun=True, apiPath="", expect="any", keys=["instance"]),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="register", cpuType="CPU1500_Unspecified", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any", keys=["instance"]),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOn", cpuType="", timeoutMs=60000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any", keys=["instance"]),
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["records", "instances"]),
    S("ReadPlcSimAdvancedTags", instanceName="MCP_SIM", namesJson="[]", areaFilter="", nameContains="", offset=0, limit=10, apiPath="", expect="any"),
    S("DownloadPlcToFolder", softwarePath=P, destinationDirectory=D + r"\mcp46_card", targetForSoftware="", overwriteOnMemoryCard=False, keepActualValues=False, userManagementMode="", promptAnswersJson="{}", confirmDownload=False, dryRun=True, expect="any"),
    S("ManagePlcDataBlockSnapshot", softwarePath=P, blockPath="MCP_G/MCP_DB", action="read", filePath="", confirmValueChange=False, dryRun=True, expect="any"),
    S("SetWatchTableModifyValue", softwarePath=P, tableName="MCP_WT", address="%I0.0", modifyValue="TRUE", trigger="Once", expect="any"),
    S("GoOfflineAll", expect="any"),
    S("GoOffline", softwarePath=P, expect="any"),
    S("ReadPlcWebDiagnostics", host="192.168.0.1", username="", password="", ignoreCertificateErrors=True, timeoutMs=3000, expect="any"),
    S("ProbeS7CpuIdentity", ip="192.168.0.1", rack=0, slot=1, expect="any"),
    S("GetPlcRunStateS7", ip="192.168.0.1", rack=0, slot=1, maxDiagEntries=5, expectModuleContains="", expect="any"),
    S("ReadPlcLiveValuesS7", ip="192.168.0.1", itemsJson=J(["M0.0"]), rack=0, slot=1, expectModuleContains="", devicePath="", expect="any"),
    S("ReadPlcLiveValuesOpcUa", endpointUrl="opc.tcp://192.168.0.1:4840", nodeIdsJson=J(["ns=3;s=\"MCP_DB\".\"Counter\""]), timeoutMs=3000, expect="any"),
    S("ReadUnifiedRuntimeTags", tagsJson=J(["MCP_Run"]), pipeName="", timeoutMs=3000, expect="any"),
    S("ReadUnifiedRuntimeAlarms", systemNamesJson="[]", filter="", languageId=1033, pipeName="", timeoutMs=3000, maxAlarms=10, expect="any"),
    S("UnifiedOpenPipeRequest", requestJson=J({"Message": "BrowseTags"}), pipeName="", timeoutMs=3000, confirmWrite=False, dryRun=True, expect="any"),
    S("WriteUnifiedRuntimeTags", writesJson=J({"MCP_Run": True}), pipeName="", timeoutMs=3000, confirmWrite=False, dryRun=True, expect="any"),
    S("WritePlcWebVars", host="192.168.0.1", username="", password="", writesJson=J({"\"MCP_DB\".Counter": 1}), ignoreCertificateErrors=True, timeoutMs=3000, confirmWrite=False, dryRun=True, expect="any"),
    S("SetPlcWebOperatingMode", host="192.168.0.1", username="", password="", mode="RUN", ignoreCertificateErrors=True, timeoutMs=3000, confirmModeChange=False, dryRun=True, expect="any"),
    S("ReadPlcWebVars", host="192.168.0.1", username="", password="", varsJson=J(["\"MCP_DB\".Counter"]), ignoreCertificateErrors=True, timeoutMs=3000, expect="any"),
    S("SamplePlcLiveValuesS7", ip="192.168.0.1", itemsJson=J(["M0.0"]), intervalMs=500, durationMs=1000, maxSamples=3, rack=0, slot=1, expectModuleContains="", expect="any"),
    S("MonitorWatchTableLiveS7", softwarePath=P, watchTableName="MCP_WT", ip="192.168.0.1", rack=0, slot=1, expectModuleContains="", expect="any"),
    S("TraceTagCauseLive", softwarePath=P, tag="MCP_Start", ip="192.168.0.1", rack=0, slot=1, blockScope="", expectModuleContains="", expect="any"),
    S("WritePlcSimAdvancedTags", instanceName="MCP_SIM", valuesJson=J({"\"MCP_Start\"": True}), confirmWrite=False, dryRun=True, apiPath="", expect="any"),
    S("RunPlcSimAdvancedTestScenario", scenarioJson=J({"instance": "MCP_SIM", "mode": "default", "stopOnFailure": True, "steps": [{"write": {"\"MCP_Start\"": True}}, {"expect": {"\"MCP_Start\"": True}}]}), confirmRun=False, dryRun=True, apiPath="", expect="any"),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOff", cpuType="", timeoutMs=60000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="unregister", cpuType="", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("ReadPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["records", "instances"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_sim1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
