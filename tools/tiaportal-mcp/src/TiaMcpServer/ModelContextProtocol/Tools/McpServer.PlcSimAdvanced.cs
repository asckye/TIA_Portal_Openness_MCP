using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.ModelContextProtocol
{
    // S7-PLCSIM Advanced channel (official .NET API, late-bound at run time; see PlcSimAdvancedChannel).
    // Closes the loop "download the memory-card image (DownloadPlcToFolder) -> boot a virtual CPU ->
    // write inputs, step cycles, assert outputs". Lifecycle changes, writes and scenarios are real
    // operations on the simulation: they default to preview and need an explicit confirm flag.
    // Nothing here saves, compiles or downloads a TIA project, and no physical PLC is involved.
    public static partial class McpServer
    {
        private static JsonObject PlcSimSafety(bool changesInstance, bool writesValues) => new JsonObject
        {
            ["target"] = "S7-PLCSIM Advanced virtual controller (no physical PLC)",
            ["changesInstanceState"] = changesInstance,
            ["writesValues"] = writesValues,
            ["touchesTiaProject"] = false
        };

        private static ResponseJsonReport RunPlcSimTool(string tool, bool? dryRun, Func<JsonObject, JsonObject, string> body)
        {
            var meta = RuntimeMeta(false, dryRun);
            meta["tool"] = tool;
            var data = new JsonObject();
            var sw = Stopwatch.StartNew();
            try
            {
                var message = body(data, meta);
                // 2.7.49: a body may downgrade the verdict itself (tags not written, scenario assertions failed) - keep it.
                meta["success"] = true; meta["apiCallSuccess"] = true;
                if (meta["operationSuccess"] == null) meta["operationSuccess"] = true;
                return new ResponseJsonReport { Ok = meta["operationSuccess"]!.GetValue<bool>(), Message = message, Data = data, Meta = meta };
            }
            catch (Exception ex)
            {
                var cause = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                meta["success"] = false; meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                meta["status"] = cause is ArgumentException ? "InvalidParams" : cause is NotSupportedException ? "NotSupported" : cause is InvalidOperationException && cause.Message.StartsWith("PLCSIM Advanced API (", StringComparison.Ordinal) ? "ApiNotFound" : "ReadOrWriteFailed";
                meta["error"] = cause.Message;
                return new ResponseJsonReport { Ok = false, Message = tool + " failed: " + cause.Message, Data = data, Meta = meta };
            }
            finally { meta["elapsedMs"] = sw.ElapsedMilliseconds; }
        }

        [McpServerTool(Name = "ReadPlcSimAdvancedInstances"), Description("[L2][Simulation][ONLINE] List the S7-PLCSIM Advanced instances registered on this machine through the official PLCSIM Advanced .NET API (Siemens.Simatic.Simulation.Runtime, located at run time: apiPath > env PLCSIMADV_API_PATH > newest folder under %ProgramFiles(x86)%\\Common Files\\Siemens\\PLCSIMADV\\API). Returns API path/version, the global network mode (SimulationRuntimeManager.NetworkMode on PLCSIM Advanced 6+: TCPIPMultipleAdapter / TCPIPSingleAdapter / Softbus) and per instance name, id and — when includeState=true — operating state (Off/Stop/Run/...), CPU type, communication interface and storage path. Read-only: registers nothing, changes no state. Fails with status ApiNotFound when PLCSIM Advanced is not installed; the API DLL is never shipped with this server.")]
        public static ResponseJsonReport ReadPlcSimAdvancedInstances(
            [Description("includeState: true (default) opens an interface to each instance to read its operating state; false lists names/ids only.")] bool includeState = true,
            [Description("apiPath: optional absolute path of Siemens.Simatic.Simulation.Runtime.Api.x64.dll or its folder; empty = auto-detect.")] string apiPath = "",
            [Description("memberFilter: optional substring; when given (with includeState) every instance row also lists the API members of the instance object and its interfaces whose name contains it (e.g. 'CommunicationInterface'), and managerMembers lists the matching static members of SimulationRuntimeManager - a diagnostic for API differences between PLCSIM Advanced versions.")] string memberFilter = "")
            => RunPlcSimTool("ReadPlcSimAdvancedInstances", null, (data, meta) =>
            {
                var api = PlcSimAdvancedChannel.Load(apiPath);
                data["api"] = PlcSimAdvancedChannel.Describe(api);
                var items = new JsonArray();
                foreach (var (name, id) in PlcSimAdvancedChannel.RegisteredInstances(api))
                {
                    var o = new JsonObject { ["name"] = name, ["id"] = id };
                    if (includeState)
                    {
                        object? instance = null;
                        try
                        {
                            instance = PlcSimAdvancedChannel.Acquire(api, name); foreach (var kv in PlcSimAdvancedChannel.InstanceState(instance)) o[kv.Key] = kv.Value?.DeepClone();
                            if (!string.IsNullOrWhiteSpace(memberFilter)) o["members"] = PlcSimAdvancedChannel.DescribeMembers(instance, memberFilter.Trim());
                        }
                        catch (Exception ex) { o["stateError"] = ex.Message; PlcSimAdvancedChannel.Forget(name); }
                    }
                    items.Add(o);
                }
                data["instances"] = items;
                if (!string.IsNullOrWhiteSpace(memberFilter)) data["managerMembers"] = PlcSimAdvancedChannel.DescribeManagerMembers(api, memberFilter.Trim());
                data["safety"] = PlcSimSafety(false, false);
                meta["dataComplete"] = items.All(i => i?["stateError"] == null);
                return items.Count + " PLCSIM Advanced instance(s) registered (API " + api.Version + ").";
            });

        [McpServerTool(Name = "ManagePlcSimAdvancedInstance"), Description("[L2][Simulation][ONLINE-WRITE] Lifecycle of ONE S7-PLCSIM Advanced instance: action register (create/registers a new instance; optional cpuType e.g. CPU1500_Unspecified, CPU1516, CPU1518F), powerOn, run, stop, powerOff, memoryReset or unregister. communicationInterface=TCPIP on register / powerOn puts the instance on the 'Siemens PLCSIM Virtual Ethernet Adapter' (API default IP 192.168.0.1), so a TIA project whose PLC has that IP can DownloadToPlc / GoOnline to it through that PG/PC interface - the safe target for the whole online family; Softbus selects the local PLCSIM access instead. On PLCSIM Advanced 6+ the per-instance property is read-only and the choice is the global SimulationRuntimeManager.NetworkMode (also accepted literally: TCPIPMultipleAdapter / TCPIPSingleAdapter / Softbus), which the API refuses while any instance is running - data.communicationInterfaceRoute shows the route and the mode before / after. Default dryRun=true only reports the current state and the planned action; the action runs only with dryRun=false AND confirmInstanceChange=true. powerOn boots the virtual CPU from its storage path (put the memory-card image there via DownloadPlcToFolder or download from TIA to the running instance); memoryReset wipes the loaded program. Returns state before/after. No physical PLC and no TIA project is touched.")]
        public static ResponseJsonReport ManagePlcSimAdvancedInstance(
            [Description("instanceName: PLCSIM Advanced instance name, e.g. 'PLC_1'.")] string instanceName,
            [Description("action: register | powerOn | run | stop | powerOff | memoryReset | unregister.")] string action,
            [Description("cpuType: only for register; ECPUType name such as CPU1500_Unspecified, CPU1511, CPU1516, CPU1518F; empty = API default.")] string cpuType = "",
            [Description("timeoutMs: wait budget for powerOn/run/stop/powerOff/memoryReset (1000..600000).")] int timeoutMs = 60000,
            [Description("confirmInstanceChange: must be true together with dryRun=false to execute the action.")] bool confirmInstanceChange = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "",
            [Description("communicationInterface: for register / powerOn only - TCPIP (reachable through the 'Siemens PLCSIM Virtual Ethernet Adapter' PG/PC interface at the instance IP, API default 192.168.0.1 - what DownloadToPlc / GoOnline need), Softbus, or an ENetworkMode name (TCPIPMultipleAdapter / TCPIPSingleAdapter / Softbus) for PLCSIM Advanced 6+ where the choice is the global network mode; empty = leave the current setting.")] string communicationInterface = "")
            => RunPlcSimTool("ManagePlcSimAdvancedInstance", dryRun, (data, meta) =>
            {
                var name = PlcSimAdvancedLogic.RequireInstanceName(instanceName);
                var act = PlcSimAdvancedLogic.NormalizeAction(action);
                if (!string.IsNullOrWhiteSpace(communicationInterface) && act != "register" && act != "powerOn") throw new ArgumentException("communicationInterface applies to register / powerOn only.");
                if (timeoutMs < 1000 || timeoutMs > 600000) throw new ArgumentException("timeoutMs must be between 1000 and 600000.");
                var api = PlcSimAdvancedChannel.Load(apiPath);
                data["api"] = PlcSimAdvancedChannel.Describe(api);
                data["instance"] = name; data["action"] = act;
                data["safety"] = PlcSimSafety(true, false);
                var registered = PlcSimAdvancedChannel.RegisteredInstances(api).Any(r => r.name.Equals(name, StringComparison.OrdinalIgnoreCase));
                data["registeredBefore"] = registered;
                object? instance = null;
                try
                {
                    if (registered)
                    {
                        instance = PlcSimAdvancedChannel.Acquire(api, name);
                        data["stateBefore"] = PlcSimAdvancedChannel.InstanceState(instance);
                    }
                    if (act == "register" && registered) throw new ArgumentException("Instance '" + name + "' is already registered; use powerOn/run instead.");
                    if (act != "register" && !registered) throw new ArgumentException("Instance '" + name + "' is not registered; register it first (or check ReadPlcSimAdvancedInstances).");
                    if (dryRun || !confirmInstanceChange)
                    {
                        meta["mayHaveChanged"] = false;
                        data["executed"] = false;
                        if (!dryRun && !confirmInstanceChange) data["refusal"] = "confirmInstanceChange=true is required to execute.";
                        return "Preview: would " + act + " PLCSIM Advanced instance '" + name + "'" + (registered ? " (current state " + data["stateBefore"]?["operatingState"] + ")" : " (not registered yet)") + ". Set dryRun=false and confirmInstanceChange=true to execute.";
                    }
                    if (act == "register")
                    {
                        PlcSimAdvancedChannel.Dispose(PlcSimAdvancedChannel.Register(api, name, cpuType));   // the registration handle; the cached interface is opened below
                        instance = PlcSimAdvancedChannel.Acquire(api, name);
                        if (!string.IsNullOrWhiteSpace(communicationInterface))
                        {
                            // 2.7.49: the instance exists from here on - a setter failure must say so instead of looking like a failed registration.
                            var detail = new JsonObject(); data["communicationInterfaceRoute"] = detail;
                            try { data["communicationInterface"] = PlcSimAdvancedChannel.SetCommunicationInterface(api, instance, communicationInterface, detail); }
                            catch (Exception ex) { throw new InvalidOperationException("Instance '" + name + "' was registered, but communicationInterface '" + communicationInterface + "' could not be applied: " + ex.Message + " (powerOff + unregister it, or keep its current setting).", ex); }
                        }
                    }
                    else
                    {
                        if (act == "powerOn" && !string.IsNullOrWhiteSpace(communicationInterface))
                        {
                            var detail = new JsonObject(); data["communicationInterfaceRoute"] = detail;
                            data["communicationInterface"] = PlcSimAdvancedChannel.SetCommunicationInterface(api, instance!, communicationInterface, detail);
                        }
                        PlcSimAdvancedChannel.Lifecycle(instance!, act, timeoutMs);
                        if (act == "unregister") { PlcSimAdvancedChannel.Forget(name); instance = null; }
                        else if (act == "powerOff" || act == "memoryReset") PlcSimAdvancedChannel.Forget(name);   // the tag list is stale after these; the next call reopens
                    }
                    meta["mayHaveChanged"] = true;
                    data["executed"] = true;
                    data["api"] = PlcSimAdvancedChannel.Describe(api);   // 2.7.52: networkMode after the action, not the call-start snapshot
                    if (instance != null) data["stateAfter"] = PlcSimAdvancedChannel.InstanceState(instance);
                    data["registeredAfter"] = PlcSimAdvancedChannel.RegisteredInstances(api).Any(r => r.name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var stateNow = instance == null ? "" : Convert.ToString(data["stateAfter"]?["operatingState"]) ?? "";
                    return act + " executed on PLCSIM Advanced instance '" + name + "'" + (instance != null ? "; state now " + (stateNow.Length == 0 ? "(powered off - no operating state until the next powerOn)" : stateNow) : "") + ".";
                }
                catch (Exception) { PlcSimAdvancedChannel.Forget(name); throw; }
            });

        [McpServerTool(Name = "ReadPlcSimAdvancedTags"), Description("[L2][Simulation][ONLINE] Read from ONE S7-PLCSIM Advanced instance: with namesJson (JSON array or comma list of PLCSIM tag names, e.g. [\"\\\"Start\\\"\", \"\\\"DB_Motor\\\".Speed\", \"%I0.0\" is NOT supported — use symbolic names as PLCSIM lists them]) the current value and primitive type of each tag; without namesJson the tag list of the instance (UpdateTagList + TagInfos) filtered by areaFilter (Input/Output/Marker/DataBlock/... or empty) and nameContains, paginated with offset/limit (max 500). Read-only; requires the instance to be powered on with a loaded program. Per-tag errors are reported in items[].error.")]
        public static ResponseJsonReport ReadPlcSimAdvancedTags(
            [Description("instanceName: registered PLCSIM Advanced instance.")] string instanceName,
            [Description("namesJson: tag names to read (JSON array or comma-separated). Empty = list tags instead.")] string namesJson = "",
            [Description("areaFilter: for listing; EArea name such as Input, Output, Marker, DataBlock; empty = all.")] string areaFilter = "",
            [Description("nameContains: for listing; case-insensitive substring filter on the tag name.")] string nameContains = "",
            int offset = 0, int limit = 200,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "")
            => RunPlcSimTool("ReadPlcSimAdvancedTags", null, (data, meta) =>
            {
                var name = PlcSimAdvancedLogic.RequireInstanceName(instanceName);
                if (offset < 0 || limit < 1 || limit > PlcSimAdvancedLogic.MaxTagsPerCall) throw new ArgumentException("offset must be >= 0 and limit between 1 and " + PlcSimAdvancedLogic.MaxTagsPerCall + ".");
                var names = PlcSimAdvancedLogic.ParseNameList(namesJson, "namesJson");
                var api = PlcSimAdvancedChannel.Load(apiPath);
                data["api"] = PlcSimAdvancedChannel.Describe(api);
                data["instance"] = name;
                data["safety"] = PlcSimSafety(false, false);
                object? instance = null;
                try
                {
                    instance = PlcSimAdvancedChannel.Acquire(api, name);
                    data["state"] = PlcSimAdvancedChannel.OperatingState(instance);
                    PlcSimAdvancedChannel.EnsureTagList(api, name, instance, names.Count == 0);   // listing always refreshes; reads reuse the cached list
                    data["interface"] = PlcSimAdvancedChannel.CacheState(name);
                    var items = new JsonArray();
                    if (names.Count > 0)
                    {
                        var failures = 0;
                        foreach (var tag in names)
                        {
                            var o = new JsonObject { ["name"] = tag };
                            try
                            {
                                var (type, value) = PlcSimAdvancedChannel.ReadWithRefresh(api, name, instance, tag);
                                o["type"] = type; o["value"] = PlcSimAdvancedLogic.ToJson(value);
                                if (value == null) o["note"] = "non-primitive (struct/array): read elements individually";
                            }
                            catch (Exception ex) { o["error"] = ex.Message; failures++; }
                            items.Add(o);
                        }
                        data["items"] = items;
                        meta["dataComplete"] = failures == 0;
                        return "Read " + (names.Count - failures) + "/" + names.Count + " tag(s) from PLCSIM Advanced instance '" + name + "' (state " + data["state"] + ").";
                    }
                    var tags = PlcSimAdvancedChannel.Tags(instance);
                    if (!string.IsNullOrWhiteSpace(areaFilter)) tags = tags.Where(t => t.Area.Equals(areaFilter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!string.IsNullOrWhiteSpace(nameContains)) tags = tags.Where(t => t.Name.IndexOf(nameContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    foreach (var t in tags.Skip(offset).Take(limit))
                        items.Add(new JsonObject { ["name"] = t.Name, ["area"] = t.Area, ["dataType"] = t.DataType, ["primitiveType"] = t.PrimitiveType, ["offset"] = t.Offset, ["bit"] = t.Bit, ["size"] = t.Size });
                    data["items"] = items; data["totalCount"] = tags.Count; data["offset"] = offset; data["limit"] = limit;
                    meta["dataComplete"] = offset + items.Count >= tags.Count;
                    return items.Count + " of " + tags.Count + " tag(s) listed for PLCSIM Advanced instance '" + name + "' (state " + data["state"] + ").";
                }
                catch (Exception) { PlcSimAdvancedChannel.Forget(name); throw; }
            });

        [McpServerTool(Name = "WritePlcSimAdvancedTags"), Description("[L2][Simulation][ONLINE-WRITE] Write values into ONE S7-PLCSIM Advanced instance (inputs, markers, DB elements) by symbolic name: valuesJson is {\"\\\"Start\\\"\":true,\"\\\"DB_Motor\\\".Speed\":50} or [{name,value}]. Each tag is read first to learn its primitive type; values are converted (Bool/Int8..Int64/UInt8..UInt64/Float/Double/Char/WChar; 16#hex accepted; structs must be written per element). Default dryRun=true reports current values and the conversion plan; writing needs dryRun=false AND confirmWrite=true. Per-tag errors in items[].error; readBack shows the value after the write. Virtual CPU only, no physical PLC, no TIA project.")]
        public static ResponseJsonReport WritePlcSimAdvancedTags(
            [Description("instanceName: registered, powered-on PLCSIM Advanced instance.")] string instanceName,
            [Description("valuesJson: JSON object tag name -> value, or array of {name, value}. Max 500.")] string valuesJson,
            [Description("confirmWrite: must be true together with dryRun=false to write.")] bool confirmWrite = false,
            [Description("dryRun: true (default) previews; false writes.")] bool dryRun = true,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "")
            => RunPlcSimTool("WritePlcSimAdvancedTags", dryRun, (data, meta) =>
            {
                var name = PlcSimAdvancedLogic.RequireInstanceName(instanceName);
                var values = PlcSimAdvancedLogic.ParseValueMap(valuesJson, "valuesJson");
                var api = PlcSimAdvancedChannel.Load(apiPath);
                data["api"] = PlcSimAdvancedChannel.Describe(api);
                data["instance"] = name;
                data["safety"] = PlcSimSafety(false, !dryRun && confirmWrite);
                var execute = !dryRun && confirmWrite;
                if (!dryRun && !confirmWrite) data["refusal"] = "confirmWrite=true is required to write.";
                object? instance = null;
                try
                {
                    instance = PlcSimAdvancedChannel.Acquire(api, name);
                    data["state"] = PlcSimAdvancedChannel.OperatingState(instance);
                    PlcSimAdvancedChannel.EnsureTagList(api, name, instance);
                    data["interface"] = PlcSimAdvancedChannel.CacheState(name);
                    var items = new JsonArray();
                    var failures = 0;
                    foreach (var kv in values)
                    {
                        var o = new JsonObject { ["name"] = kv.Key, ["requested"] = kv.Value?.DeepClone() };
                        try
                        {
                            var (type, current) = PlcSimAdvancedChannel.ReadWithRefresh(api, name, instance, kv.Key);
                            o["type"] = type; o["before"] = PlcSimAdvancedLogic.ToJson(current);
                            var converted = PlcSimAdvancedLogic.ConvertValue(kv.Value, type);
                            o["converted"] = PlcSimAdvancedLogic.ToJson(converted);
                            if (execute)
                            {
                                PlcSimAdvancedChannel.WriteWithRefresh(api, name, instance, kv.Key, kv.Value);
                                o["written"] = true;
                                o["readBack"] = PlcSimAdvancedLogic.ToJson(PlcSimAdvancedChannel.Read(api, instance, kv.Key).value);
                            }
                            else o["written"] = false;
                        }
                        catch (Exception ex) { o["error"] = ex.Message; failures++; }
                        items.Add(o);
                    }
                    data["items"] = items; data["executed"] = execute;
                    meta["mayHaveChanged"] = execute && failures < values.Count;
                    meta["dataComplete"] = failures == 0;
                    meta["operationSuccess"] = failures == 0;   // 2.7.49: "Wrote 0/1" was reported as success (real machine)
                    return (execute ? "Wrote " : "Preview: would write ") + (values.Count - failures) + "/" + values.Count + " tag(s) on PLCSIM Advanced instance '" + name + "'" + (execute ? "." : ". Set dryRun=false and confirmWrite=true to write.");
                }
                catch (Exception) { PlcSimAdvancedChannel.Forget(name); throw; }
            });

        [McpServerTool(Name = "RunPlcSimAdvancedTestScenario"), Description("[L2][Simulation][EXECUTE] Run a closed-loop test scenario against ONE S7-PLCSIM Advanced instance (the PLCSIM.UnitTest idea without a separate runner): scenarioJson = {\"instance\":\"PLC_1\",\"mode\":\"singleStep\"|\"default\",\"stopOnFailure\":true,\"steps\":[{\"write\":{\"\\\"Start\\\"\":true}},{\"cycles\":5},{\"waitMs\":200},{\"assert\":{\"\\\"Running\\\"\":true},\"tolerance\":0.001,\"note\":\"motor starts\"},{\"run\":true},{\"stop\":true},{\"powerOn\":true}]}. mode singleStep sets the instance to SingleStep and advances exactly N cycles per {cycles} step via RunToNextSyncPoint (deterministic); mode default keeps free running and {cycles} becomes a wait of N*10 ms. Default dryRun=true validates and returns the plan; execution needs dryRun=false AND confirmRun=true. Result: per-step outcome, failed assertions with expected/actual, passed/failed counts; the instance is left in the operating mode it had before. Virtual CPU only — no physical PLC and no TIA project is touched.")]
        public static ResponseJsonReport RunPlcSimAdvancedTestScenario(
            [Description("scenarioJson: scenario object (see description). Max 500 steps.")] string scenarioJson,
            [Description("confirmRun: must be true together with dryRun=false to execute the steps.")] bool confirmRun = false,
            [Description("dryRun: true (default) validates and plans only; false executes.")] bool dryRun = true,
            [Description("apiPath: optional path of the PLCSIM Advanced API DLL or folder; empty = auto-detect.")] string apiPath = "")
            => RunPlcSimTool("RunPlcSimAdvancedTestScenario", dryRun, (data, meta) =>
            {
                var scenario = PlcSimAdvancedLogic.ParseScenario(scenarioJson);
                data["plan"] = PlcSimAdvancedLogic.ScenarioPlan(scenario);
                data["safety"] = PlcSimSafety(scenario.Steps.Any(s => s.Kind == "powerOn" || s.Kind == "run" || s.Kind == "stop" || s.Kind == "cycles"), scenario.Steps.Any(s => s.Kind == "write"));
                var execute = !dryRun && confirmRun;
                if (!execute)
                {
                    meta["mayHaveChanged"] = false; data["executed"] = false;
                    if (!dryRun && !confirmRun) data["refusal"] = "confirmRun=true is required to execute.";
                    return "Preview: scenario with " + scenario.Steps.Count + " step(s) on PLCSIM Advanced instance '" + scenario.Instance + "' validated. Set dryRun=false and confirmRun=true to execute.";
                }
                var api = PlcSimAdvancedChannel.Load(apiPath);
                data["api"] = PlcSimAdvancedChannel.Describe(api);
                object? instance = null;
                string? originalMode = null;
                var results = new JsonArray();
                int passed = 0, failed = 0, executedSteps = 0;
                try
                {
                    instance = PlcSimAdvancedChannel.Acquire(api, scenario.Instance);
                    data["stateBefore"] = PlcSimAdvancedChannel.InstanceState(instance);
                    PlcSimAdvancedChannel.EnsureTagList(api, scenario.Instance, instance);
                    data["interface"] = PlcSimAdvancedChannel.CacheState(scenario.Instance);
                    if (scenario.Mode == "singleStep")
                    {
                        originalMode = Convert.ToString(data["stateBefore"]?["operatingMode"]);
                        data["operatingModeApplied"] = PlcSimAdvancedChannel.SetOperatingMode(api, instance, "singleStep");   // 2.7.54: SingleStep_CP on API 4+
                    }
                    foreach (var step in scenario.Steps)
                    {
                        var r = new JsonObject { ["index"] = step.Index, ["kind"] = step.Kind };
                        if (step.Note.Length > 0) r["note"] = step.Note;
                        var stepOk = true;
                        try
                        {
                            switch (step.Kind)
                            {
                                case "write":
                                    var written = new JsonArray();
                                    foreach (var kv in step.Values) { var type = PlcSimAdvancedChannel.WriteWithRefresh(api, scenario.Instance, instance, kv.Key, kv.Value); written.Add(new JsonObject { ["name"] = kv.Key, ["type"] = type, ["value"] = kv.Value?.DeepClone() }); }
                                    r["written"] = written;
                                    break;
                                case "cycles":
                                    if (scenario.Mode == "singleStep")
                                    {
                                        // 2.7.55: step-and-wait - each RunToNextSyncPoint is followed by a wait for Freeze (see StepCycles).
                                        var (done, waitedMs, finalState, failure) = PlcSimAdvancedChannel.StepCycles(instance, step.Count, PlcSimAdvancedLogic.SyncPointWaitMs, PlcSimAdvancedLogic.SyncPointPollMs);
                                        r["cyclesStepped"] = done; r["waitedMs"] = waitedMs; r["stateAfterSteps"] = finalState;
                                        if (failure != null) { r["error"] = failure; stepOk = false; }
                                    }
                                    else Thread.Sleep(Math.Min(60000, step.Count * 10));
                                    r["cycles"] = step.Count;
                                    break;
                                case "wait":
                                    Thread.Sleep(step.Count); r["waitMs"] = step.Count;
                                    break;
                                case "assert":
                                    var checks = new JsonArray();
                                    foreach (var kv in step.Values)
                                    {
                                        var (type, actual) = PlcSimAdvancedChannel.ReadWithRefresh(api, scenario.Instance, instance, kv.Key);
                                        var ok = PlcSimAdvancedLogic.ValuesMatch(actual, kv.Value, step.Tolerance);
                                        if (!ok) stepOk = false;
                                        checks.Add(new JsonObject { ["name"] = kv.Key, ["type"] = type, ["expected"] = kv.Value?.DeepClone(), ["actual"] = PlcSimAdvancedLogic.ToJson(actual), ["ok"] = ok });
                                    }
                                    r["checks"] = checks;
                                    break;
                                case "powerOn": PlcSimAdvancedChannel.Lifecycle(instance, "powerOn", 120000); break;
                                case "run": PlcSimAdvancedChannel.Lifecycle(instance, "run", 60000); break;
                                case "stop": PlcSimAdvancedChannel.Lifecycle(instance, "stop", 60000); break;
                            }
                            r["state"] = PlcSimAdvancedChannel.OperatingState(instance);
                        }
                        catch (Exception ex) { stepOk = false; r["error"] = ex.Message; }
                        executedSteps++;
                        r["ok"] = stepOk;
                        if (step.Kind == "assert") { if (stepOk) passed++; else failed++; }
                        else if (!stepOk) failed++;
                        results.Add(r);
                        if (!stepOk && scenario.StopOnFailure) break;
                    }
                }
                finally
                {
                    if (instance != null && originalMode != null)
                    {
                        try { PlcSimAdvancedChannel.SetOperatingMode(api, instance, originalMode); data["operatingModeRestored"] = originalMode; }
                        catch (Exception ex) { data["operatingModeRestoreError"] = ex.Message; }
                    }
                    if (instance != null) { try { data["stateAfter"] = PlcSimAdvancedChannel.InstanceState(instance); } catch (Exception) { PlcSimAdvancedChannel.Forget(scenario.Instance); } }
                }
                data["steps"] = results; data["executed"] = true;
                data["executedSteps"] = executedSteps; data["assertionsPassed"] = passed; data["failedSteps"] = failed;
                data["verdict"] = failed == 0 ? "passed" : "failed";
                meta["operationSuccess"] = failed == 0;   // 2.7.49: a FAILED scenario was reported as success (real machine)
                meta["mayHaveChanged"] = true;
                meta["dataComplete"] = executedSteps == scenario.Steps.Count;
                return "Scenario " + (failed == 0 ? "PASSED" : "FAILED") + ": " + executedSteps + "/" + scenario.Steps.Count + " step(s) executed, " + passed + " assertion(s) passed, " + failed + " failed on PLCSIM Advanced instance '" + scenario.Instance + "'.";
            });
    }
}
