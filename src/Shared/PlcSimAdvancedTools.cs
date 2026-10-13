using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using PlcSimScenario = TiaMcp.Logic.V4.Domain.PlcSimScenario;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    // Declarations and approval stay on the host; only the net48 worker loads the Siemens API.
    [McpServerToolType]
    internal sealed class PlcSimAdvancedTools
    {
        private readonly PlcSimAdvancedPortService _service;
        public PlcSimAdvancedTools() : this(new PlcSimAdvancedPortService()) { }
        public PlcSimAdvancedTools(PlcSimAdvancedPortService service) => _service = service;
        [McpServerTool(Name = "ListPlcSimAdvancedInstances"), Description("[L2][Simulation][ONLINE] List the S7-PLCSIM Advanced instances registered on this machine through the official PLCSIM Advanced .NET API (Siemens.Simatic.Simulation.Runtime, located at run time: apiPath > env PLCSIMADV_API_PATH > newest folder under %ProgramFiles(x86)%\\Common Files\\Siemens\\PLCSIMADV\\API). Returns API path/version, the global network mode (SimulationRuntimeManager.NetworkMode on PLCSIM Advanced 6+: TCPIPMultipleAdapter / TCPIPSingleAdapter / Softbus) and per instance name, id and — when includeState=true — operating state (Off/Stop/Run/...), CPU type, communication interface and storage path. Read-only: registers nothing, changes no state. Fails with status ApiNotFound when PLCSIM Advanced is not installed; the API DLL is never shipped with this server.")]
        public CallToolResult ReadPlcSimAdvancedInstancesV4(
            [Description("includeState: true (default) opens an interface to each instance to read its operating state; false lists names/ids only.")] bool includeState = true,
            [Description("apiPath: optional absolute path of Siemens.Simatic.Simulation.Runtime.Api.x64.dll or its folder; empty = auto-detect.")] string apiPath = "",
            [Description("memberFilter: optional substring; when given (with includeState) every instance row also lists the API members of the instance object and its interfaces whose name contains it (e.g. 'CommunicationInterface'), and managerMembers lists the matching static members of SimulationRuntimeManager - a diagnostic for API differences between PLCSIM Advanced versions.")] string memberFilter = "")
            => RuntimeToolContract.Run("ListPlcSimAdvancedInstances", true, false, () =>
            {
                return _service.ReadPlcSimAdvancedInstances(includeState, apiPath, memberFilter);
            });

        [McpServerTool(Name = "ManagePlcSimAdvancedInstance"), Description("[L2][Simulation][ONLINE-WRITE] Lifecycle of ONE S7-PLCSIM Advanced instance: action register (create/registers a new instance; optional cpuType e.g. CPU1500_Unspecified, CPU1516, CPU1518F), powerOn, run, stop, powerOff, memoryReset or unregister. communicationInterface=TCPIP on register / powerOn puts the instance on the 'Siemens PLCSIM Virtual Ethernet Adapter' (API default IP 192.168.0.1), so a TIA project whose PLC has that IP can DownloadPlc / ConnectOnlinePlc to it through that PG/PC interface - the safe target for the whole online family; Softbus selects the local PLCSIM access instead. On PLCSIM Advanced 6+ the per-instance property is read-only and the choice is the global SimulationRuntimeManager.NetworkMode (also accepted literally: TCPIPMultipleAdapter / TCPIPSingleAdapter / Softbus), which the API refuses while any instance is running - data.communicationInterfaceRoute shows the route and the mode before / after. Default dryRun=true only reports the current state and the planned action; the action runs only with dryRun=false AND confirmInstanceChange=true. powerOn boots the virtual CPU from its storage path (put the memory-card image there via DownloadPlcToFolder or download from TIA to the running instance); memoryReset wipes the loaded program. Returns state before/after. No physical PLC and no TIA project is touched. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePlcSimAdvancedInstanceV4(
            [Description("instanceName: PLCSIM Advanced instance name, e.g. 'PLC_1'.")] string instanceName,
            [Description("action: register | powerOn | run | stop | powerOff | memoryReset | unregister.")] string action,
            [Description("cpuType: only for register; ECPUType name such as CPU1500_Unspecified, CPU1511, CPU1516, CPU1518F; empty = API default.")] string cpuType = "",
            [Description("timeoutMs: wait budget for powerOn/run/stop/powerOff/memoryReset (1000..600000).")] int timeoutMs = 60000,
            [Description("confirmInstanceChange: must be true together with dryRun=false to execute the action.")] bool confirmInstanceChange = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "",
            [Description("communicationInterface: for register / powerOn only - TCPIP (reachable through the 'Siemens PLCSIM Virtual Ethernet Adapter' PG/PC interface at the instance IP, API default 192.168.0.1 - what DownloadPlc / ConnectOnlinePlc need), Softbus, or an ENetworkMode name (TCPIPMultipleAdapter / TCPIPSingleAdapter / Softbus) for PLCSIM Advanced 6+ where the choice is the global network mode; empty = leave the current setting.")] string communicationInterface = "")
            => RuntimeToolContract.Run("ManagePlcSimAdvancedInstance", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmInstanceChange);
                return _service.ManagePlcSimAdvancedInstance(instanceName, action, cpuType, timeoutMs, confirmInstanceChange, dryRun, apiPath, communicationInterface);
            });

        [McpServerTool(Name = "GetPlcSimAdvancedTags"), Description("[L2][Simulation][ONLINE] Read from ONE S7-PLCSIM Advanced instance: with names (array of PLCSIM tag names, e.g. [\"\\\"Start\\\"\", \"\\\"DB_Motor\\\".Speed\", \"%I0.0\" is NOT supported — use symbolic names as PLCSIM lists them]) the current value and primitive type of each tag; without names the tag list of the instance (UpdateTagList + TagInfos) filtered by areaFilter (Input/Output/Marker/DataBlock/... or empty) and nameContains, paginated with offset/limit (max 500). Read-only; requires the instance to be powered on with a loaded program. Per-tag errors are reported in items[].error.")]
        public CallToolResult ReadPlcSimAdvancedTagsV4(
            [Description("instanceName: registered PLCSIM Advanced instance.")] string instanceName,
            [Description("names: structured string array input.")] string[] names = null!,
            [Description("areaFilter: for listing; EArea name such as Input, Output, Marker, DataBlock; empty = all.")] string areaFilter = "",
            [Description("nameContains: for listing; case-insensitive substring filter on the tag name.")] string nameContains = "",
            int offset = 0,
            int limit = 200,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "")
            => RuntimeToolContract.Run("GetPlcSimAdvancedTags", true, false, () =>
            {
                return _service.ReadPlcSimAdvancedTags(instanceName, (names == null || names.Length == 0 ? "" : RuntimeToolContract.Names(names, "names", true)), areaFilter, nameContains, offset, limit, apiPath);
            });

        [McpServerTool(Name = "WritePlcSimAdvancedTags"), Description("[L2][Simulation][ONLINE-WRITE] Write values into ONE S7-PLCSIM Advanced instance (inputs, markers, DB elements) by symbolic name: values is an array of {name,value} objects. Each tag is read first to learn its primitive type; values are converted (Bool/Int8..Int64/UInt8..UInt64/Float/Double/Char/WChar; 16#hex accepted; structs must be written per element). Default dryRun=true reports current values and the conversion plan; writing needs dryRun=false AND confirmWrite=true. Per-tag errors in items[].error; readBack shows the value after the write. Virtual CPU only, no physical PLC, no TIA project. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult WritePlcSimAdvancedTagsV4(
            [Description("instanceName: registered, powered-on PLCSIM Advanced instance.")] string instanceName,
            [Description("values: array of {name, value} writes; scalar values only, maximum 500.")] WriteValue[] values,
            [Description("confirmWrite: must be true together with dryRun=false to write.")] bool confirmWrite = false,
            [Description("dryRun: true (default) previews; false writes.")] bool dryRun = true,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "")
            => RuntimeToolContract.Run("WritePlcSimAdvancedTags", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmWrite);
                return _service.WritePlcSimAdvancedTags(instanceName, RuntimeToolContract.Writes(values, "values", dryRun, confirmWrite), confirmWrite, dryRun, apiPath);
            });

        [McpServerTool(Name = "RunPlcSimAdvancedTestScenario"), Description("[L2][Simulation][ONLINE-WRITE] Run an ordered PLCSIM Advanced scenario with instance, mode (singleStep or default), optional stopOnFailure and 1..500 write, waitMs or assert steps. Write/assert values are name-to-scalar maps; assertions may include tolerance and note. Default dryRun=true validates the plan only; execution requires dryRun=false and confirmRun=true. Returns step evidence and assertion counts. An unknown write stops execution without replay. Virtual CPU only. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult RunPlcSimAdvancedTestScenarioV4(
            [Description("scenario: object containing instance, mode and ordered write, waitMs or assert steps.")] PlcSimScenario scenario,
            [Description("confirmRun: must be true together with dryRun=false to execute the steps.")] bool confirmRun = false,
            [Description("dryRun: true (default) validates and plans only; false executes.")] bool dryRun = true,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "")
            => RuntimeToolContract.Run("RunPlcSimAdvancedTestScenario", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmRun);
                return _service.RunPlcSimAdvancedTestScenario(RuntimeToolContract.Domain(scenario, "scenario"), confirmRun, dryRun, apiPath);
            });

    }
}
