using TiaMcpServer.ModelContextProtocol;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using TiaMcpServer.Runtime;

namespace TiaMcp.PlcWorker.Runtime
{
    // S7-PLCSIM Advanced channel (official .NET API, late-bound at run time; see PlcSimAdvancedChannel).
    // Closes the loop "download the memory-card image (DownloadPlcToFolder) -> boot a virtual CPU ->
    // write inputs, step cycles, assert outputs". Lifecycle changes, writes and scenarios are real
    // operations on the simulation: they default to preview and need an explicit confirm flag.
    // Nothing here saves, compiles or downloads a TIA project, and no physical PLC is involved.
    public sealed class PlcSimAdvancedReport
    {
        public bool? Ok { get; set; }
        public string? Message { get; set; }
        public JsonObject? Data { get; set; }
        public JsonObject? Meta { get; set; }
    }

    public sealed class PlcSimAdvancedAdapter
    {
        // Only these reviewed operations can cross this module boundary.
        public JsonObject ExecutePlcSimAdvanced(JsonObject request)
        {
            var operation = (string?)request["operation"] ?? "";
            if (!new[] { "ReadPlcSimAdvancedInstances", "ManagePlcSimAdvancedInstance", "ReadPlcSimAdvancedTags", "WritePlcSimAdvancedTags", "RunPlcSimAdvancedTestScenario" }.Contains(operation, StringComparer.Ordinal))
                throw new ArgumentException("Unknown PLCSIM Advanced operation.");
            var method = typeof(PlcSimAdvancedAdapter).GetMethod(operation)!;
            var arguments = request["arguments"]!.AsObject();
            var values = method.GetParameters().Select(parameter => arguments.TryGetPropertyValue(parameter.Name!, out var value)
                ? value!.Deserialize(parameter.ParameterType) : parameter.DefaultValue).ToArray();
            var result = method.Invoke(this, values);
            return JsonSerializer.SerializeToNode(result)!.AsObject();
        }

        private static JsonObject PlcSimSafety(bool changesInstance, bool writesValues) => new JsonObject
        {
            ["target"] = "S7-PLCSIM Advanced virtual controller (no physical PLC)",
            ["changesInstanceState"] = changesInstance,
            ["writesValues"] = writesValues,
            ["touchesTiaProject"] = false
        };

        private static PlcSimAdvancedReport RunPlcSimTool(string tool, bool? dryRun, Func<JsonObject, JsonObject, string> body)
        {
            var meta = ResponseMeta.Basic(false);
            if (dryRun != null) meta["dryRun"] = dryRun;
            meta["tool"] = tool;
            var data = new JsonObject();
            var sw = Stopwatch.StartNew();
            try
            {
                var message = body(data, meta);
                // A body may downgrade the verdict itself (tags not written, scenario assertions failed) - keep it.
                // envelope: legacy-plcsim-complete: Complete would replace the body's operationSuccess with the API verdict.
                meta["success"] = true; meta["apiCallSuccess"] = true;
                if (meta["operationSuccess"] == null) meta["operationSuccess"] = true;
                return new PlcSimAdvancedReport { Ok = meta["operationSuccess"]!.GetValue<bool>(), Message = message, Data = data, Meta = meta };
            }
            catch (Exception ex)
            {
                var cause = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                // envelope: legacy-plcsim-failure: Failed would append dataComplete, which this executor does not set.
                meta["success"] = false; meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                meta["status"] = cause is ArgumentException ? "InvalidParams" : cause is NotSupportedException ? "NotSupported" : cause is PlcSimApiUnavailableException ? "ApiNotFound" : "ReadOrWriteFailed";
                meta["error"] = cause.Message;
                return new PlcSimAdvancedReport { Ok = false, Message = tool + " failed: " + cause.Message, Data = data, Meta = meta };
            }
            finally { meta["elapsedMs"] = sw.ElapsedMilliseconds; }
        }

        public PlcSimAdvancedReport ReadPlcSimAdvancedInstances(bool includeState = true, string apiPath = "", string memberFilter = "")
            => RunPlcSimTool("ListPlcSimAdvancedInstances", null, (data, meta) =>
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

        public PlcSimAdvancedReport ManagePlcSimAdvancedInstance(string instanceName, string action, string cpuType = "", int timeoutMs = 60000, bool confirmInstanceChange = false, bool dryRun = true, string apiPath = "", string communicationInterface = "")
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
                    if (act != "register" && !registered) throw new ArgumentException("Instance '" + name + "' is not registered; register it first (or check ListPlcSimAdvancedInstances).");
                    if (dryRun || !confirmInstanceChange)
                    {
                        meta["mayHaveChanged"] = false;
                        data["executed"] = false;
                        if (!dryRun && !confirmInstanceChange) data["refusal"] = "confirmInstanceChange=true is required to execute.";
                        return "Preview: would " + act + " PLCSIM Advanced instance '" + name + "'" + (registered ? " (current state " + data["stateBefore"]?["operatingState"] + ")" : " (not registered yet)") + ". Set dryRun=false and confirmInstanceChange=true to execute.";
                    }
                    meta["mayHaveChanged"] = true;
                    if (act == "register")
                    {
                        PlcSimAdvancedChannel.Dispose(PlcSimAdvancedChannel.Register(api, name, cpuType));   // the registration handle; the cached interface is opened below
                        instance = PlcSimAdvancedChannel.Acquire(api, name);
                        if (!string.IsNullOrWhiteSpace(communicationInterface))
                        {
                            // The instance exists from here on - a setter failure must say so instead of looking like a failed registration.
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
                    data["api"] = PlcSimAdvancedChannel.Describe(api);   // networkMode after the action, not the call-start snapshot
                    if (instance != null) data["stateAfter"] = PlcSimAdvancedChannel.InstanceState(instance);
                    data["registeredAfter"] = PlcSimAdvancedChannel.RegisteredInstances(api).Any(r => r.name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var stateNow = instance == null ? "" : Convert.ToString(data["stateAfter"]?["operatingState"]) ?? "";
                    return act + " executed on PLCSIM Advanced instance '" + name + "'" + (instance != null ? "; state now " + (stateNow.Length == 0 ? "(powered off - no operating state until the next powerOn)" : stateNow) : "") + ".";
                }
                catch (Exception) { PlcSimAdvancedChannel.Forget(name); throw; }
            });

        public PlcSimAdvancedReport ReadPlcSimAdvancedTags(string instanceName, string namesJson = "", string areaFilter = "", string nameContains = "", int offset = 0, int limit = 200, string apiPath = "")
            => RunPlcSimTool("GetPlcSimAdvancedTags", null, (data, meta) =>
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

        public PlcSimAdvancedReport WritePlcSimAdvancedTags(string instanceName, string valuesJson, bool confirmWrite = false, bool dryRun = true, string apiPath = "")
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
                            // The existing read supplies the target type; admission adds no native call.
                            var converted = RuntimeToolContract.SimulationValue(kv.Value, type, dryRun, confirmWrite);
                            o["converted"] = PlcSimAdvancedLogic.ToJson(converted);
                            if (execute)
                            {
                                o["writeAttempted"] = true;
                                meta["mayHaveChanged"] = true;
                                PlcSimAdvancedChannel.WriteWithRefresh(api, name, instance, kv.Key, kv.Value);
                                o["written"] = true;
                                var actual = PlcSimAdvancedChannel.Read(api, instance, kv.Key).value;
                                o["readBack"] = PlcSimAdvancedLogic.ToJson(actual);
                                o["writeOutcomeKnown"] = true;
                                o["verified"] = PlcSimAdvancedLogic.ValuesMatch(actual, kv.Value, 0);
                                if (o["verified"]!.GetValue<bool>() == false) { o["error"] = "Write readback differs from the requested value."; failures++; }
                            }
                            else o["written"] = false;
                        }
                        catch (Exception ex) { o["error"] = ex.Message; failures++; }
                        items.Add(o);
                        if (o["writeAttempted"]?.GetValue<bool>() == true && o["writeOutcomeKnown"]?.GetValue<bool>() != true)
                        { meta["outcomeUnknown"] = true; break; }
                    }
                    data["items"] = items; data["executed"] = execute;
                    meta["mayHaveChanged"] = items.Any(i => i?["writeAttempted"]?.GetValue<bool>() == true);
                    meta["dataComplete"] = failures == 0;
                    meta["operationSuccess"] = failures == 0;   // A failed write must fail the operation (TIA V21 / PLCSIM Advanced, 2026-09-21; docs/reference/real-machine-ledger.md).
                    return (execute ? "Wrote " : "Preview: would write ") + (values.Count - failures) + "/" + values.Count + " tag(s) on PLCSIM Advanced instance '" + name + "'" + (execute ? "." : ". Set dryRun=false and confirmWrite=true to write.");
                }
                catch (Exception) { PlcSimAdvancedChannel.Forget(name); throw; }
            });

        public PlcSimAdvancedReport RunPlcSimAdvancedTestScenario(string scenarioJson, bool confirmRun = false, bool dryRun = true, string apiPath = "")
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
                data["steps"] = results;
                int passed = 0, failed = 0, executedSteps = 0;
                try
                {
                    instance = PlcSimAdvancedChannel.Acquire(api, scenario.Instance);
                    data["stateBefore"] = PlcSimAdvancedChannel.InstanceState(instance);
                    PlcSimAdvancedChannel.EnsureTagList(api, scenario.Instance, instance);
                    data["interface"] = PlcSimAdvancedChannel.CacheState(scenario.Instance);
                    if (scenario.Mode == "singleStep")
                    {
                        meta["mayHaveChanged"] = true;
                        originalMode = Convert.ToString(data["stateBefore"]?["operatingMode"]);
                        data["operatingModeApplied"] = PlcSimAdvancedChannel.SetOperatingMode(api, instance, "singleStep");   // SingleStep_CP on API 4+
                    }
                    foreach (var step in scenario.Steps)
                    {
                        var r = new JsonObject { ["index"] = step.Index, ["kind"] = step.Kind };
                        if (step.Note.Length > 0) r["note"] = step.Note;
                        var stepOk = true;
                        try
                        {
                            if (step.Kind != "assert" && step.Kind != "wait") meta["mayHaveChanged"] = true;
                            switch (step.Kind)
                            {
                                case "write":
                                    var written = new JsonArray();
                                    r["written"] = written;
                                    foreach (var kv in step.Values) { var type = PlcSimAdvancedChannel.WriteWithRefresh(api, scenario.Instance, instance, kv.Key, kv.Value); written.Add(new JsonObject { ["name"] = kv.Key, ["type"] = type, ["value"] = kv.Value?.DeepClone() }); }
                                    break;
                                case "cycles":
                                    if (scenario.Mode == "singleStep")
                                    {
                                        // Each RunToNextSyncPoint is followed by a wait for Freeze (see StepCycles).
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
                        catch (Exception ex)
                        {
                            stepOk = false; r["error"] = ex.Message;
                            if (step.Kind != "assert" && step.Kind != "wait") { r["outcomeUnknown"] = true; meta["outcomeUnknown"] = true; }
                        }
                        executedSteps++;
                        r["ok"] = stepOk;
                        if (step.Kind == "assert") { if (stepOk) passed++; else failed++; }
                        else if (!stepOk) failed++;
                        results.Add(r);
                        if (meta["outcomeUnknown"]?.GetValue<bool>() == true || !stepOk && scenario.StopOnFailure) break;
                    }
                }
                finally
                {
                    if (instance != null && originalMode != null)
                    {
                        try { PlcSimAdvancedChannel.SetOperatingMode(api, instance, originalMode); data["operatingModeRestored"] = originalMode; }
                        catch (Exception ex) { data["operatingModeRestoreError"] = ex.Message; }
                    }
                    if (instance != null) { try { data["stateAfter"] = PlcSimAdvancedChannel.InstanceState(instance); } catch (Exception) /* swallow(probe-optional): a failed final-state probe invalidates the cached interface without replacing the scenario result. */ { PlcSimAdvancedChannel.Forget(scenario.Instance); } }
                }
                data["executed"] = true;
                if (meta["outcomeUnknown"]?.GetValue<bool>() != true && data["operatingModeRestoreError"] == null) meta["writeOutcomeKnown"] = true;
                data["executedSteps"] = executedSteps; data["assertionsPassed"] = passed; data["failedSteps"] = failed;
                data["verdict"] = failed == 0 ? "passed" : "failed";
                meta["operationSuccess"] = failed == 0;   // A failed scenario must fail the operation (TIA V21 / PLCSIM Advanced, 2026-09-21; docs/reference/real-machine-ledger.md).
                meta["mayHaveChanged"] = true;
                meta["dataComplete"] = executedSteps == scenario.Steps.Count;
                return "Scenario " + (failed == 0 ? "PASSED" : "FAILED") + ": " + executedSteps + "/" + scenario.Steps.Count + " step(s) executed, " + passed + " assertion(s) passed, " + failed + " failed on PLCSIM Advanced instance '" + scenario.Instance + "'.";
            });
    }
}
