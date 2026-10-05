# -*- coding: utf-8 -*-
import json, io, os
P = "MCP_PLC"; D = r"C:\Users\SIEMENS\Desktop"
def S(tool, note="", expect="ok", keys=None, **args): return {"tool": tool, "args": args, "note": note, "expect": expect, "keys": keys or []}
J = json.dumps
plan = [
    S("ListPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["records", "instances"]),
    S("ProbePlcMonitorOnlineCapabilities", softwarePath=P, expect="any"),
    S("PlanOnlineReadOnlyMonitoring", softwarePath=P, tagPaths=["MCP_Start"], mode="watch", expect="any"),
    S("PlanOnlineReadOnlyDataProvider", provider="s7-readonly", endpoint="192.168.0.1", tagPaths=["M0.0"], options={}, expect="any"),
    S("GetPlcWatchTableCurrentValuesReadOnly", softwarePath=P, watchTableName="MCP_WT", maxEntries=10, expect="any"),
    S("TraceTagCause", softwarePath=P, tag="MCP_Start", blockScope="", expect="any", keys=["writers"]),
    S("CheckDownloadReadiness", softwarePath=P, expect="any", keys=["ready", "checks"]),
    S("ListTransferRoutes", softwarePath=P, maxItems=50, expect="any", keys=["modes"]),
    S("GetOnlineState", softwarePath=P, expect="any", keys=["state"]),
    S("ScanAccessibleDevices", pgPcInterface="PLCSIM", softwarePath=P, offset=0, limit=20, expect="any", keys=["records"]),
    S('ListPlcSimAdvancedInstances', note='', expect='any', keys=['instance'], apiPath=''),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="register", cpuType="CPU1500_Unspecified", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any", keys=["instance"]),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOn", cpuType="", timeoutMs=60000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any", keys=["instance"]),
    S("ListPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["records", "instances"]),
    S("GetPlcSimAdvancedTags", instanceName="MCP_SIM", names=[], areaFilter="", nameContains="", offset=0, limit=10, apiPath="", expect="any"),
    S('DownloadPlcToFolder', note='', expect='any', keys=[], softwarePath='MCP_PLC', destinationDirectory='C:\\Users\\SIEMENS\\Desktop\\mcp46_card', targetForSoftware='', overwriteOnMemoryCard=False, keepActualValues=False, promptAnswers={}, confirmDownload=False, dryRun=True),
    S("ManagePlcDataBlockSnapshot", softwarePath=P, blockPath="MCP_G/MCP_DB", action="read", filePath="", confirmValueChange=False, dryRun=True, expect="any"),
    S('SetPlcWatchTableModifyValue', note='', expect='any', keys=[], softwarePath='MCP_PLC', tableName='MCP_WT', address='%I0.0', modifyValue='TRUE', trigger='OnceOnlyAtStart'),
    S("DisconnectOnlinePlcs", expect="any"),
    S("DisconnectOnlinePlc", softwarePath=P, expect="any"),
    S("GetPlcWebDiagnostics", host="192.168.0.1", username="", password="", ignoreCertificateErrors=True, timeoutMs=3000, expect="any"),
    S("ProbeS7CpuIdentity", ip="192.168.0.1", rack=0, slot=1, expect="any"),
    S("GetPlcRunStateS7", ip="192.168.0.1", rack=0, slot=1, maxDiagEntries=5, expectModuleContains="", expect="any"),
    S("GetPlcLiveValuesS7", ip="192.168.0.1", items=["M0.0"], rack=0, slot=1, expectModuleContains="", devicePath="", expect="any"),
    S("GetPlcLiveValuesOpcUa", endpointUrl="opc.tcp://192.168.0.1:4840", nodeIds=["ns=3;s=\"MCP_DB\".\"Counter\""], timeoutMs=3000, expect="any"),
    S("GetUnifiedRuntimeTags", tags=["MCP_Run"], pipeName="", timeoutMs=3000, expect="any"),
    S("GetUnifiedRuntimeAlarms", systemNames=[], filter="", languageId=1033, pipeName="", timeoutMs=3000, maxAlarms=10, expect="any"),
    S('InvokeUnifiedOpenPipe', note='', expect='any', keys=[], request={'message': 'BrowseTags', 'params': {}}, pipeName='', timeoutMs=3000, confirmWrite=False, dryRun=True),
    S('WriteUnifiedRuntimeTags', note='', expect='any', keys=[], writes=[{'name': 'MCP_Run', 'value': True}], pipeName='', timeoutMs=3000, confirmWrite=False, dryRun=True),
    S('WritePlcWebVars', note='', expect='any', keys=[], host='192.168.0.1', username='', password='', writes=[{'name': '"MCP_DB".Counter', 'value': 1}], ignoreCertificateErrors=True, timeoutMs=3000, confirmWrite=False, dryRun=True),
    S("SetPlcWebOperatingMode", host="192.168.0.1", username="", password="", mode="RUN", ignoreCertificateErrors=True, timeoutMs=3000, confirmModeChange=False, dryRun=True, expect="any"),
    S("GetPlcWebVars", host="192.168.0.1", username="", password="", vars=["\"MCP_DB\".Counter"], ignoreCertificateErrors=True, timeoutMs=3000, expect="any"),
    S("SamplePlcLiveValuesS7", ip="192.168.0.1", items=["M0.0"], intervalMs=500, durationMs=1000, maxSamples=3, rack=0, slot=1, expectModuleContains="", expect="any"),
    S("MonitorPlcWatchTableS7", softwarePath=P, watchTableName="MCP_WT", ip="192.168.0.1", rack=0, slot=1, expectModuleContains="", expect="any"),
    S("TraceTagCauseLive", softwarePath=P, tag="MCP_Start", ip="192.168.0.1", rack=0, slot=1, blockScope="", expectModuleContains="", expect="any"),
    S('WritePlcSimAdvancedTags', note='', expect='any', keys=[], instanceName='MCP_SIM', values=[{'name': '"MCP_Start"', 'value': True}], confirmWrite=False, dryRun=True, apiPath=''),
    S('RunPlcSimAdvancedTestScenario', note='', expect='any', keys=[], scenario={'instance': 'MCP_SIM', 'mode': 'default', 'stopOnFailure': True, 'steps': [{'write': {'"MCP_Start"': True}}, {'assert': {'"MCP_Start"': True}}]}, confirmRun=False, dryRun=True, apiPath=''),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="powerOff", cpuType="", timeoutMs=60000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("ManagePlcSimAdvancedInstance", instanceName="MCP_SIM", action="unregister", cpuType="", timeoutMs=30000, confirmInstanceChange=True, dryRun=False, apiPath="", expect="any"),
    S("ListPlcSimAdvancedInstances", includeState=True, apiPath="", keys=["records", "instances"]),
]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "plan_sim1.json")
io.open(out, "w", encoding="utf-8").write(json.dumps(plan, ensure_ascii=False, indent=0)); print(len(plan), "steps ->", out)
