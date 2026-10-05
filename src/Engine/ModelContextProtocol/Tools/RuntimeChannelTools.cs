using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using OpenPipeRequest = TiaMcp.Logic.V4.Domain.OpenPipeRequest;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.ModelContextProtocol
{
    // The group boundary retains domain evidence; messages never establish success.
    internal static class RuntimeToolContract
    {
        private sealed class InputFailure : Exception
        {
            internal Error Error { get; }
            internal InputFailure(Error error) => Error = error;
        }

        internal static string Json<T>(T value, string parameter, InputContract<T> contract)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new InputFailure(result.Error);
            return V4Json.Serialize(result.Value);
        }

        internal static string Names(string[] value, string parameter, bool optional, string[]? fallback = null)
        {
            var values = value ?? (optional ? fallback ?? Array.Empty<string>() : null!);
            var policy = new NameListPolicy(minimum: optional ? 0 : 1, maximum: 500,
                unique: parameter == "vars" || parameter == "tags" || parameter == "systemNames", trim: true);
            return Json(values, parameter, NameListValidator.Create(policy));
        }
        internal static string Path(string[] value, string parameter, bool rootAllowed)
            => Json(value, parameter, PathValidator.Create(rootAllowed));
        internal static string Writes(WriteValue[] value, string parameter, bool dryRun, bool confirmWrite,
            Func<string, NativeValuePolicy>? targetValuePolicy = null)
            => Json(value, parameter, WriteValueValidator.Create(NativeValueValidator.Scalar(), dryRun, confirmWrite,
                targetValuePolicy: targetValuePolicy));
        internal static string Answers(AttributeMap<string> value)
            => Json(value ?? new AttributeMap<string>(new Dictionary<string, string>()), "promptAnswers", TextMapValidator.PromptAnswers());
        internal static string Domain<T>(T value, string parameter)
            => Json(value, parameter, TiaMcp.Logic.V4.Domain.DomainValidation.Contract<T>());
        internal static string Request(OpenPipeRequest value)
        {
            Domain(value, "request");
            return value.ToWire(RuntimeChannelsLogic.NewClientCookie("request"));
        }
        internal static void Confirm(bool preview, bool confirmed)
        {
            if (!preview && !confirmed) throw new InputFailure(new Error("Explicit confirmation is required.", new ConfirmationRequiredDetails(null)));
        }

        internal static CallToolResult Run(string tool, bool readOnly, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), readOnly, current); }
            catch (InputFailure failure) { return Result(tool, null, failure.Error, Outcome.RejectedBeforeOperation, Completeness.None, current); }
            catch (Exception ex)
            {
                // A wrapped native exception can occur after a write was issued. Its
                // CLR type alone cannot prove that the operation never started.
                Error? rejection = ex is ArgumentException ? McpServer.InvalidInput("arguments")
                    : ex is NotSupportedException ? new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)) : null;
                while (ex.InnerException != null && !(ex is TiaMcpServer.Siemens.PortalException)) ex = ex.InnerException;
                if (ex is TiaMcpServer.Siemens.PortalException portal)
                {
                    if (portal.Code == TiaMcpServer.Siemens.PortalErrorCode.InvalidState) rejection = new Error("No project is bound.", new ProjectNotBoundDetails());
                    if (portal.Code == TiaMcpServer.Siemens.PortalErrorCode.NotFound) rejection = new Error("The target was not found.", new NotFoundDetails(null));
                }
                return Result(tool, null, rejection ?? Failure(!readOnly), rejection != null ? Outcome.RejectedBeforeOperation
                    : readOnly ? Outcome.ReadFailed : Outcome.Unknown, rejection != null ? Completeness.None : Completeness.Unknown, current);
            }
        }

        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        private static int Count(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;
        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true || Flag(obj, "fullObjectComplete") == false
                || obj.Any(p => (p.Key == "unavailable" || p.Key == "unresolved" || p.Key == "failures") && p.Value is JsonArray a && a.Count > 0)
                || obj.Any(p => (p.Key == "error" || p.Key.EndsWith("Error", StringComparison.Ordinal)) && p.Value != null || Incomplete(p.Value));
        }

        internal static CallToolResult Map(string tool, object response, bool readOnly, bool current)
        {
            var raw = System.Text.Json.JsonSerializer.SerializeToNode(response, response.GetType(),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!.AsObject();
            var evidence = raw["meta"] as JsonObject ?? new JsonObject();
            var data = raw["data"] is JsonObject payload ? (JsonObject)payload.DeepClone() : new JsonObject();
            foreach (var pair in raw)
                if (pair.Key != "meta" && pair.Key != "data" && pair.Key != "message" && pair.Key != "ok") data[pair.Key] = pair.Value?.DeepClone();
            foreach (var pair in evidence)
                if (!data.ContainsKey(pair.Key) && pair.Key != "timestamp" && pair.Key != "tool") data[pair.Key] = pair.Value?.DeepClone();
            bool? success = Flag(data, "operationSuccess") ?? Flag(raw, "ok") ?? Flag(data, "success");
            string status = data["v4Rejection"]?.ToString() ?? data["status"]?.ToString() ?? "";
            string state = data["downloadState"]?.ToString() ?? data["state"]?.ToString() ?? "";
            if (tool == "GetOnlineState") success = state.Length > 0 && state != "Unknown";
            if (tool == "CompareSoftwareToOnline") success = data["entries"] is JsonArray && data["summary"] is JsonObject;
            if (tool == "DownloadPlc" || tool == "DownloadPlcToFolder")
                success = state == "Success" || state == "Warning" ? true : state == "Error" ? false : success;
            if (data["refusal"] != null) status = "CONFIRMATION_REQUIRED";

            var items = data["items"] as JsonArray;
            if (tool == "DisconnectOnlinePlcs") items = data["plcs"] as JsonArray;
            if (tool == "RunPlcSimAdvancedTestScenario") items = data["steps"] as JsonArray;
            int succeeded = 0, failed = 0, notExecuted = 0;
            bool unknown = Flag(data, "outcomeUnknown") == true || Flag(data, "writeOutcomeKnown") == false
                || data["operatingModeRestoreError"] != null;
            if (items != null && !readOnly)
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (tool == "RunPlcSimAdvancedTestScenario")
                    {
                        if (Flag(item, "outcomeUnknown") == true) unknown = true;
                        if (Flag(item, "ok") == true) succeeded++; else failed++;
                        continue;
                    }
                    bool attempted = Flag(item, "writeAttempted") == true || Flag(item, "written") == true;
                    bool known = Flag(item, "writeOutcomeKnown") == true;
                    if (tool == "DisconnectOnlinePlcs")
                    {
                        attempted = Flag(item, "writeAttempted") == true;
                        known = item["after"]?.ToString() == "Offline";
                    }
                    if (Flag(item, "outcomeUnknown") == true || attempted && !known && Flag(item, "verified") != true) unknown = true;
                    if (Flag(item, "verified") == true || tool == "DisconnectOnlinePlcs" && known) succeeded++;
                    else if (attempted && known) failed++;
                    else notExecuted++;
                }
            if (tool == "DisconnectOnlinePlcs") success = items != null && items.Count == succeeded;
            bool issued = Flag(data, "mayHaveChanged") == true || Flag(data, "mayHaveWrittenFiles") == true || Flag(data, "writeAttempted") == true;
            bool knownFailure = Flag(data, "writeOutcomeKnown") == true || status == "ReadbackMismatch" || (tool == "DownloadPlc" || tool == "DownloadPlcToFolder") && state == "Error";
            if (status == "ReadbackMismatch") success = false;
            if (tool == "SetUnifiedRuntimeSettings" && status == "ReadbackMismatch" && data["readback"] is JsonObject settings && data["proposed"] is JsonObject proposed)
            {
                succeeded = settings.Count(p => p.Value is JsonObject field && JsonNode.DeepEquals(field["value"], proposed[p.Key]));
                failed = Math.Max(1, Count(data, "failureCount"));
            }
            if (!readOnly && success != true && issued && !knownFailure && succeeded + failed == 0) unknown = true;
            Error? error = null;
            Outcome outcome;
            if (!readOnly && unknown) { outcome = Outcome.Unknown; error = Failure(true); }
            else if (!readOnly && succeeded > 0 && failed + notExecuted > 0)
            { outcome = Outcome.Partial; error = new Error("Some operations did not complete successfully.", new PartialFailureDetails(succeeded, failed, notExecuted)); }
            else if (status == "PROJECT_NOT_BOUND" || status == "InvalidState")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (status == "INVALID_ARGUMENT" || status == "InvalidParams")
            { outcome = Outcome.RejectedBeforeOperation; error = McpServer.InvalidInput("arguments"); }
            else if (status == "NOT_FOUND")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The target was not found.", new NotFoundDetails(null)); }
            else if (status == "CONFIRMATION_REQUIRED")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("Explicit confirmation is required.", new ConfirmationRequiredDetails(null)); }
            else if (status == "NotSupported" || status == "UNSUPPORTED_CAPABILITY")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)); }
            else if (status == "ApiNotFound")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The runtime API is unavailable.", new ResourceUnavailableDetails("PLCSIM Advanced API")); }
            else if (success == true) outcome = Outcome.Succeeded;
            else if (readOnly) { outcome = Outcome.ReadFailed; error = Failure(false); }
            else if (knownFailure || failed > 0) { outcome = Outcome.Failed; error = Failure(false); }
            else if (Flag(data, "mayHaveChanged") == false || Flag(data, "writeAttempted") == false)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The operation did not pass its preconditions.", new PreconditionFailedDetails("runtime-before-write", null)); }
            else { outcome = Outcome.Unknown; error = Failure(true); }

            bool incomplete = Incomplete(data);
            if (outcome == Outcome.Succeeded && raw["message"] != null && !data.ContainsKey("summary")) data["summary"] = raw["message"]!.DeepClone();
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown ? Completeness.Unknown : incomplete ? Completeness.Partial
                : outcome == Outcome.ReadFailed ? Completeness.None : Completeness.Complete;
            Paging? paging = null;
            int total = Count(data, "totalCount");
            if (data["offset"] != null && Count(data, "limit") > 0) paging = McpServer.OffsetPage(Count(data, "offset"), Count(data, "limit"), total);
            Sanitize(data);
            return Result(tool, data, error, outcome, completeness, current, readOnly, paging);
        }

        private static void Sanitize(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Sanitize(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal))
                { if (pair.Value != null) obj[pair.Key] = "The native observation failed; consult the local diagnostic log."; }
                else if (pair.Key == "requestLine" || pair.Key == "responseLine")
                {
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var line))
                    {
                        try { obj[pair.Key == "requestLine" ? "request" : "response"] = JsonNode.Parse(line); }
                        catch (System.Text.Json.JsonException) /* swallow(privacy): invalid protocol text is not a structured result */ { }
                    }
                    obj.Remove(pair.Key);
                }
                else if (pair.Key == "skippedLines" || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("token", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonObject) obj.Remove(pair.Key);
                else Sanitize(pair.Value);
            }
        }
        private static Error Failure(bool unknown) => unknown
            ? new Error("The write outcome is unknown. Reset the session and inspect the target before any further write; do not retry.", new OutcomeUnknownDetails("runtime-operation", new Dictionary<string, System.Text.Json.JsonElement>()))
            : new Error("The operation did not establish a successful result.", new NativeOperationFailedDetails(null, null, new Dictionary<string, System.Text.Json.JsonElement>()));
        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool current, bool readOnly = false, Paging? paging = null)
        {
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : readOnly || outcome == Outcome.ReadFailed ? Execution.ReadOnly : Execution.Completed;
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, System.Text.Json.JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; inspect the retained per-item evidence.", new Dictionary<string, System.Text.Json.JsonElement>()));
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }

    // Runtime channels that do NOT use TIA Openness:
    //   - SIMATIC S7 Web server API on the CPU (HTTPS JSON-RPC, official Siemens client)
    //   - WinCC Unified Open Pipe (local named pipe of WinCC Unified Runtime)
    // Reads are for monitoring. Writes and CPU mode changes are real operations on a
    // running system: they default to preview and additionally need an explicit confirm
    // flag. Nothing here saves, compiles or downloads a TIA project.
    [McpServerToolType]
    internal sealed class RuntimeChannelTools
    {
        private static ResponseJsonReport RuntimeRefusal(string message, JsonObject? data = null)
        {
            var meta = ResponseMeta.Basic(false);
            meta["v4Rejection"] = "INVALID_ARGUMENT";
            return new ResponseJsonReport { Ok = false, Message = message, Data = data, Meta = meta };
        }

        internal static JsonObject RuntimeMeta(bool ok, bool? dryRun = null, bool? mayHaveChanged = null, bool? passwordProvided = null)
        {
            var m = ResponseMeta.Basic(ok);
            if (dryRun != null) m["dryRun"] = dryRun;
            if (mayHaveChanged != null) m["mayHaveChanged"] = mayHaveChanged;
            if (passwordProvided != null) m["passwordProvided"] = passwordProvided;
            return m;
        }

        private static JsonObject S7WebSafety(bool writes, bool changesMode) => new JsonObject
        {
            ["readOnly"] = !writes && !changesMode,
            ["writesValues"] = writes,
            ["usesForce"] = false,
            ["changesCpuMode"] = changesMode,
            ["certificateValidated"] = true
        };

        // ------------------------------------------------------------------ S7 Web API

        [McpServerTool(Name = "GetPlcWebVars"), Description("[L2][Online-Monitoring][ONLINE] Read live values of PLC variables by SYMBOLIC name through the SIMATIC S7 Web server API (HTTPS JSON-RPC on the CPU; S7-1500 FW>=2.9, S7-1200 G2, ET 200SP CPU, Software Controller, PLCSIM Advanced). Independent of TIA Openness and of PUT/GET; optimized DBs are fine. Names use the Web API form, e.g. \"\\\"DB_Motor\\\".\\\"Speed\\\"\" or \"\\\"Tag_1\\\"\" (quotes may be omitted for plain names; array elements as \"\\\"DB\\\".\\\"Arr\\\"[3]\"). Read-only: never writes, forces or changes CPU mode. Preconditions: web server enabled on the CPU, the user has 'read variables' permission (the default Anonymous user usually has none). The CPU certificate is validated unless ignoreCertificateErrors=true. The password is passed straight to the API and never stored or logged. Each name is read individually; per-name errors are reported in items[].error. Session is cached per host+user and reused.")]
        public CallToolResult ReadPlcWebVarsV4(
            [Description("host: CPU web server address, IP or DNS name with optional :port, e.g. '192.168.0.1'. No scheme (always https).")] string host,
            [Description("username: web server user configured in TIA (Protection & Security > User management), e.g. 'Anonymous' or 'monitor'.")] string username,
            [Description("password: that user's password (empty for Anonymous). Passed to Api.Login only.")] string password,
            [Description("vars: structured string array input.")] string[] vars,
            [Description("ignoreCertificateErrors: false (default) validates the CPU's TLS certificate against the Windows trust store; true accepts any certificate for this host (only for lab/PLCSIM).")] bool ignoreCertificateErrors = false,
            [Description("timeoutMs: per-request timeout in ms (500..120000).")] int timeoutMs = 5000)
            => RuntimeToolContract.Run("GetPlcWebVars", true, false, () =>
            {
                return ReadPlcWebVars(host, username, password, RuntimeToolContract.Names(vars, "vars", false), ignoreCertificateErrors, timeoutMs);
            });

        public ResponseJsonReport ReadPlcWebVars(string host, string username, string password, string varsJson, bool ignoreCertificateErrors = false, int timeoutMs = 5000)
        {
            try
            {
                string h; List<string> vars;
                try { h = RuntimeChannelsLogic.NormalizeS7WebHost(host); vars = RuntimeChannelsLogic.ParseNameList(varsJson, "varsJson"); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (string.IsNullOrWhiteSpace(username)) return RuntimeRefusal("username is empty; give the web server user name (e.g. 'Anonymous').");

                var r = S7WebApiChannel.ReadVars(h, username, password ?? "", vars, ignoreCertificateErrors, timeoutMs);
                var items = new JsonArray();
                foreach (var it in r.Items)
                {
                    var o = new JsonObject { ["name"] = it.Name };
                    if (it.Error != null) o["error"] = it.Error; else o["value"] = it.Value;
                    items.Add(o);
                }
                var data = new JsonObject
                {
                    ["host"] = h,
                    ["username"] = username,
                    ["channel"] = "S7 Web server API (HTTPS JSON-RPC, read-only)",
                    ["elapsedMs"] = r.ElapsedMs,
                    ["reusedSession"] = r.ReusedSession,
                    ["items"] = items,
                    ["safety"] = S7WebSafety(false, false)
                };
                data["safety"]!["certificateValidated"] = !ignoreCertificateErrors;
                if (r.Error != null) data["error"] = r.Error;
                bool ok = r.Ok && r.Error == null && r.Items.All(i => i.Error == null);
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = r.Error ?? (ok
                        ? $"Read {r.Items.Count} variable(s) from {h} in {r.ElapsedMs} ms."
                        : $"Connected to {h}, but {r.Items.Count(i => i.Error != null)} of {r.Items.Count} variable(s) failed (see items[].error; typical causes: wrong symbolic name/quoting, no read permission for this user)."),
                    Data = data,
                    Meta = RuntimeMeta(ok, passwordProvided: !string.IsNullOrEmpty(password))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPlcWebVars failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WritePlcWebVars"), Description("[L2][Online-Monitoring][ONLINE-WRITE] Write values to PLC variables by SYMBOLIC name through the SIMATIC S7 Web server API (PlcProgram.Write). This changes values in a RUNNING CPU. Defaults to PREVIEW (dryRun=true): validates the writes and reads the CURRENT values only, no write is sent. A real write needs dryRun=false AND confirmWrite=true; anything else is refused. Every variable is read before the write, written one by one, then read back and compared (items[].verified); a readback mismatch (e.g. the program overwrites the variable cyclically) is reported as an error, never as success. Values must be JSON bool/number/string scalars; structs/arrays are refused. The user needs 'write variables' permission. No force, no CPU mode change, no TIA project change. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult WritePlcWebVarsV4(
            [Description("host: CPU web server address, e.g. '192.168.0.1' (optional :port, no scheme).")] string host,
            [Description("username: web server user with write permission.")] string username,
            [Description("password: that user's password. Passed to Api.Login only.")] string password,
            [Description("writes: array of {name, value} writes; scalar values only, maximum 500.")] WriteValue[] writes,
            [Description("ignoreCertificateErrors: false (default) validates the CPU certificate; true accepts any certificate for this host.")] bool ignoreCertificateErrors = false,
            [Description("timeoutMs: per-request timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("confirmWrite: must be true (together with dryRun=false) to actually write; default false refuses.")] bool confirmWrite = false,
            [Description("dryRun: true (default) previews and reads current values only.")] bool dryRun = true)
            => RuntimeToolContract.Run("WritePlcWebVars", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmWrite);
                return WritePlcWebVars(host, username, password, RuntimeToolContract.Writes(writes, "writes", dryRun, confirmWrite), ignoreCertificateErrors, timeoutMs, confirmWrite, dryRun);
            });

        public ResponseJsonReport WritePlcWebVars(string host, string username, string password, string writesJson, bool ignoreCertificateErrors = false, int timeoutMs = 5000, bool confirmWrite = false, bool dryRun = true)
        {
            try
            {
                string h; List<RuntimeWriteItem> writes;
                try { h = RuntimeChannelsLogic.NormalizeS7WebHost(host); writes = RuntimeChannelsLogic.ParseWriteList(writesJson, "writesJson"); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (string.IsNullOrWhiteSpace(username)) return RuntimeRefusal("username is empty; give the web server user name.");
                if (!dryRun && !confirmWrite)
                    return RuntimeRefusal($"Refused: writing {writes.Count} variable(s) on a running CPU requires confirmWrite=true together with dryRun=false. Nothing was written. Re-run with dryRun=true to preview.",
                        new JsonObject { ["plannedWrites"] = PlannedWrites(writes), ["safety"] = S7WebSafety(false, false) });

                var r = S7WebApiChannel.WriteVars(h, username, password ?? "", writes, ignoreCertificateErrors, timeoutMs, preview: dryRun);
                var items = new JsonArray();
                foreach (var it in r.Items)
                {
                    var o = new JsonObject { ["name"] = it.Name, ["requested"] = it.Requested, ["before"] = it.Before };
                    if (it.BeforeError != null) o["beforeError"] = it.BeforeError;
                    if (!dryRun)
                    {
                        o["writeAttempted"] = it.WriteAttempted;
                        o["writeAccepted"] = it.WriteAccepted;
                        o["writeOutcomeKnown"] = it.WriteOutcomeKnown;
                        o["after"] = it.After;
                        o["verified"] = it.Verified;
                    }
                    if (it.Error != null) o["error"] = it.Error;
                    items.Add(o);
                }
                var data = new JsonObject
                {
                    ["host"] = h,
                    ["username"] = username,
                    ["channel"] = "S7 Web server API (HTTPS JSON-RPC, PlcProgram.Write)",
                    ["elapsedMs"] = r.ElapsedMs,
                    ["reusedSession"] = r.ReusedSession,
                    ["status"] = dryRun ? "Preview" : (r.Ok ? "WrittenAndVerified" : "Failed"),
                    ["items"] = items,
                    ["safety"] = S7WebSafety(!dryRun, false)
                };
                data["safety"]!["certificateValidated"] = !ignoreCertificateErrors;
                if (r.Error != null) data["error"] = r.Error;
                bool ok = r.Ok && r.Error == null;
                bool anyAttempt = r.Items.Any(i => i.WriteAttempted);
                string msg = r.Error ?? (dryRun
                    ? $"Preview: {writes.Count} write(s) validated and current values read from {h}; nothing written. Re-run with dryRun=false and confirmWrite=true to write."
                    : (ok ? $"Wrote and verified {r.Items.Count} variable(s) on {h} in {r.ElapsedMs} ms."
                          : $"{r.Items.Count(i => i.Error != null)} of {r.Items.Count} write(s) failed or did not verify (see items[].error)."));
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = msg,
                    Data = data,
                    Meta = RuntimeMeta(ok, dryRun, anyAttempt, !string.IsNullOrEmpty(password))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"WritePlcWebVars failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static JsonArray PlannedWrites(List<RuntimeWriteItem> writes)
        {
            var arr = new JsonArray();
            foreach (var w in writes) arr.Add(new JsonObject { ["name"] = w.Name, ["value"] = w.Value?.DeepClone(), ["valueKind"] = w.ValueKind });
            return arr;
        }

        [McpServerTool(Name = "GetPlcWebDiagnostics"), Description("[L2][Online-Monitoring][ONLINE] Read CPU diagnostics through the SIMATIC S7 Web server API: operating mode (RUN/STOP/STARTUP/HOLD/...), mode selector position, Api.Ping, API version, CPU type/order number, CPU system time, cycle-time and load figures, work/retentive memory usage. Read-only: never writes, forces or changes mode. Plc.ReadOperatingMode must succeed; the other calls are best-effort and calls this CPU firmware or user does not support are listed in data.unavailable instead of being faked. Works with PLCSIM Advanced. Certificate validated unless ignoreCertificateErrors=true; password never stored.")]
        public CallToolResult ReadPlcWebDiagnosticsV4(
            [Description("host: CPU web server address, e.g. '192.168.0.1' (optional :port, no scheme).")] string host,
            [Description("username: web server user (needs 'read diagnostics' for most values).")] string username,
            [Description("password: that user's password. Passed to Api.Login only.")] string password,
            [Description("ignoreCertificateErrors: false (default) validates the CPU certificate; true accepts any certificate for this host.")] bool ignoreCertificateErrors = false,
            [Description("timeoutMs: per-request timeout in ms (500..120000).")] int timeoutMs = 5000)
            => RuntimeToolContract.Run("GetPlcWebDiagnostics", true, false, () =>
            {
                return ReadPlcWebDiagnostics(host, username, password, ignoreCertificateErrors, timeoutMs);
            });

        public ResponseJsonReport ReadPlcWebDiagnostics(string host, string username, string password, bool ignoreCertificateErrors = false, int timeoutMs = 5000)
        {
            try
            {
                string h;
                try { h = RuntimeChannelsLogic.NormalizeS7WebHost(host); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (string.IsNullOrWhiteSpace(username)) return RuntimeRefusal("username is empty; give the web server user name.");

                var r = S7WebApiChannel.ReadDiagnostics(h, username, password ?? "", ignoreCertificateErrors, timeoutMs);
                var unavailable = new JsonArray();
                foreach (var u in r.Unavailable) unavailable.Add(u);
                var data = new JsonObject
                {
                    ["host"] = h,
                    ["username"] = username,
                    ["channel"] = "S7 Web server API (HTTPS JSON-RPC, read-only)",
                    ["elapsedMs"] = r.ElapsedMs,
                    ["reusedSession"] = r.ReusedSession,
                    ["operatingMode"] = r.OperatingMode,
                    ["modeSelector"] = r.ModeSelector,
                    ["ping"] = r.Ping,
                    ["apiVersion"] = r.ApiVersion,
                    ["cpuType"] = r.CpuType,
                    ["systemTimeUtc"] = r.SystemTimeUtc,
                    ["runtimeInformation"] = r.RuntimeInformation,
                    ["memoryInformation"] = r.MemoryInformation,
                    ["unavailable"] = unavailable,
                    ["dataComplete"] = r.Ok && r.Unavailable.Count == 0,
                    ["safety"] = S7WebSafety(false, false)
                };
                data["safety"]!["certificateValidated"] = !ignoreCertificateErrors;
                if (r.Error != null) data["error"] = r.Error;
                bool ok = r.Ok && r.Error == null;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = r.Error ?? $"CPU at {h} is {r.OperatingMode}" + (r.Unavailable.Count > 0 ? $"; {r.Unavailable.Count} diagnostic call(s) unavailable on this CPU/user (see data.unavailable)." : "."),
                    Data = data,
                    Meta = RuntimeMeta(ok, passwordProvided: !string.IsNullOrEmpty(password))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPlcWebDiagnostics failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetPlcWebOperatingMode"), Description("[L2][Online-Monitoring][ONLINE-WRITE] DANGEROUS: request a CPU operating mode change (RUN or STOP) through the SIMATIC S7 Web server API (Plc.RequestChangeOperatingMode). STOP halts the user program and all outputs go to their configured safe state; RUN starts the program. Defaults to PREVIEW (dryRun=true): reads and reports the current mode only. A real change needs dryRun=false AND confirmModeChange=true; anything else is refused. After the request the mode is polled (up to ~10 s) and the readback is reported; an unconfirmed change is an error, not success. If the CPU is already in the requested mode nothing is sent. The user needs 'change operating mode' permission and the hardware mode selector must allow it. Never writes variables, never touches the TIA project. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SetPlcWebOperatingModeV4(
            [Description("host: CPU web server address, e.g. '192.168.0.1' (optional :port, no scheme).")] string host,
            [Description("username: web server user with 'change operating mode' permission.")] string username,
            [Description("password: that user's password. Passed to Api.Login only.")] string password,
            [Description("mode: 'run' or 'stop'.")] string mode,
            [Description("ignoreCertificateErrors: false (default) validates the CPU certificate; true accepts any certificate for this host.")] bool ignoreCertificateErrors = false,
            [Description("timeoutMs: per-request timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("confirmModeChange: must be true (with dryRun=false) to actually stop/start the CPU; default false refuses.")] bool confirmModeChange = false,
            [Description("dryRun: true (default) only reads the current operating mode.")] bool dryRun = true)
            => RuntimeToolContract.Run("SetPlcWebOperatingMode", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmModeChange);
                return SetPlcWebOperatingMode(host, username, password, mode, ignoreCertificateErrors, timeoutMs, confirmModeChange, dryRun);
            });

        public ResponseJsonReport SetPlcWebOperatingMode(string host, string username, string password, string mode, bool ignoreCertificateErrors = false, int timeoutMs = 5000, bool confirmModeChange = false, bool dryRun = true)
        {
            try
            {
                string h, m;
                try { h = RuntimeChannelsLogic.NormalizeS7WebHost(host); m = RuntimeChannelsLogic.NormalizeOperatingModeRequest(mode); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (string.IsNullOrWhiteSpace(username)) return RuntimeRefusal("username is empty; give the web server user name.");
                if (!dryRun && !confirmModeChange)
                    return RuntimeRefusal($"Refused: switching the CPU at {h} to {m.ToUpperInvariant()} requires confirmModeChange=true together with dryRun=false. No request was sent.",
                        new JsonObject { ["requestedMode"] = m, ["safety"] = S7WebSafety(false, false) });

                var r = S7WebApiChannel.ChangeOperatingMode(h, username, password ?? "", m, ignoreCertificateErrors, timeoutMs, preview: dryRun);
                var data = new JsonObject
                {
                    ["host"] = h,
                    ["username"] = username,
                    ["channel"] = "S7 Web server API (HTTPS JSON-RPC, Plc.RequestChangeOperatingMode)",
                    ["elapsedMs"] = r.ElapsedMs,
                    ["reusedSession"] = r.ReusedSession,
                    ["requestedMode"] = m,
                    ["before"] = r.Before,
                    ["status"] = dryRun ? "Preview" : (r.Ok ? (r.RequestAttempted ? "ChangedAndVerified" : "AlreadyInRequestedMode") : "Failed"),
                    ["safety"] = S7WebSafety(false, !dryRun)
                };
                data["safety"]!["certificateValidated"] = !ignoreCertificateErrors;
                if (!dryRun)
                {
                    data["requestAttempted"] = r.RequestAttempted;
                    data["requestAccepted"] = r.RequestAccepted;
                    data["after"] = r.After;
                    data["verified"] = r.Verified;
                    data["pollCount"] = r.PollCount;
                }
                if (r.Error != null) data["error"] = r.Error;
                bool ok = r.Ok && r.Error == null;
                string msg = r.Error ?? (dryRun
                    ? $"Preview: CPU at {h} is currently {r.Before}; requested {m.ToUpperInvariant()}. Nothing sent. Re-run with dryRun=false and confirmModeChange=true to change the mode."
                    : (r.RequestAttempted ? $"CPU at {h} changed from {r.Before} to {r.After} (verified by readback)." : $"CPU at {h} is already {r.Before}; no request sent."));
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = msg,
                    Data = data,
                    Meta = RuntimeMeta(ok, dryRun, r.RequestAttempted, !string.IsNullOrEmpty(password))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"SetPlcWebOperatingMode failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // ------------------------------------------------------- WinCC Unified Open Pipe

        private static JsonObject OpenPipeSafety(bool writes) => new JsonObject
        {
            ["readOnly"] = !writes,
            ["writesValues"] = writes,
            ["localOnly"] = true,
            ["subscriptionsLeftOpen"] = false
        };

        private static JsonArray OpenPipeTags(OpenPipeResponse resp, bool includeValues)
        {
            var arr = new JsonArray();
            foreach (var t in resp.Tags)
            {
                var o = new JsonObject { ["name"] = t.Name };
                if (includeValues)
                {
                    o["value"] = t.Value;
                    o["quality"] = t.Quality;
                    o["qualityCode"] = t.QualityCode;
                    o["timeStamp"] = t.TimeStamp;
                }
                o["errorCode"] = t.ErrorCode;
                if (!t.Ok) o["error"] = string.IsNullOrEmpty(t.ErrorDescription) ? ("Open Pipe error code " + t.ErrorCode) : t.ErrorDescription;
                arr.Add(o);
            }
            return arr;
        }

        private static string? OpenPipeFailure(OpenPipeExchange ex, string command)
        {
            if (ex.Error != null) return ex.Error;
            if (ex.Response == null) return $"No {command} response received.";
            if (ex.Response.IsError) return $"{ex.Response.Message}: {ex.Response.ErrorDescription} (ErrorCode {ex.Response.ErrorCode}).";
            return null;
        }

        [McpServerTool(Name = "GetUnifiedRuntimeTags"), Description("[L2][Online-Monitoring][ONLINE] Read current values of WinCC Unified RUNTIME tags (HMI tags incl. their PLC-connected values) through the official WinCC Unified Open Pipe (local named pipe \\\\.\\pipe\\HmiRuntime of the running Runtime; no ODK license, no TIA Openness). Sends one ReadTag request and returns value, quality, quality code and time stamp per tag; per-tag errors (e.g. 'Tag does not exist') are in items[].error. Tag names are the runtime names, e.g. 'Tag_1' or 'HMI_RT_1::Tag_1'. Read-only, opens no subscription. Requires: this MCP server runs ON the Runtime PC and its user is in the 'SIMATIC HMI' group.")]
        public CallToolResult ReadUnifiedRuntimeTagsV4(
            [Description("tags: structured string array input.")] string[] tags,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime (bare 'HmiRuntime' also accepted). Remote machines are refused.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000)
            => RuntimeToolContract.Run("GetUnifiedRuntimeTags", true, false, () =>
            {
                return ReadUnifiedRuntimeTags(RuntimeToolContract.Names(tags, "tags", false), pipeName, timeoutMs);
            });

        public ResponseJsonReport ReadUnifiedRuntimeTags(string tagsJson, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000)
        {
            try
            {
                List<string> tags;
                try { tags = RuntimeChannelsLogic.ParseNameList(tagsJson, "tagsJson"); RuntimeChannelsLogic.ParsePipeName(pipeName); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }

                string cookie = RuntimeChannelsLogic.NewClientCookie("read");
                string line = RuntimeChannelsLogic.BuildReadTagRequest(tags, cookie);
                var ex = UnifiedOpenPipeChannel.Exchange(pipeName, line, cookie, "ReadTag", timeoutMs);
                string? failure = OpenPipeFailure(ex, "ReadTag");
                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (ReadTag, read-only)",
                    ["connected"] = ex.Connected,
                    ["elapsedMs"] = ex.ElapsedMs,
                    ["items"] = ex.Response != null ? OpenPipeTags(ex.Response, true) : new JsonArray(),
                    ["safety"] = OpenPipeSafety(false)
                };
                if (ex.SkippedLines.Count > 0) data["skippedLines"] = new JsonArray(ex.SkippedLines.Select(s => (JsonNode)s).ToArray());
                if (failure != null) data["error"] = failure;
                bool ok = failure == null && ex.Response != null && ex.Response.Tags.All(t => t.Ok);
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = failure ?? (ok
                        ? $"Read {ex.Response!.Tags.Count} runtime tag(s) via Open Pipe in {ex.ElapsedMs} ms."
                        : $"Open Pipe answered, but {ex.Response!.Tags.Count(t => !t.Ok)} of {ex.Response.Tags.Count} tag(s) failed (see items[].error)."),
                    Data = data,
                    Meta = RuntimeMeta(ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetUnifiedRuntimeTags failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WriteUnifiedRuntimeTags"), Description("[L2][Online-Monitoring][ONLINE-WRITE] Write values to WinCC Unified RUNTIME tags through WinCC Unified Open Pipe (WriteTag on \\\\.\\pipe\\HmiRuntime). PLC-connected HMI tags forward the value to the PLC, so this changes a running system. Defaults to PREVIEW (dryRun=true): validates the writes and reads the CURRENT values (ReadTag) only. A real write needs dryRun=false AND confirmWrite=true; anything else is refused. After WriteTag the tags are read back and compared (items[].verified); a mismatch or a per-tag WriteTag error is reported as failure. Values: JSON bool/number/string scalars (the Runtime converts to the tag's data type). Local only; user must be in the 'SIMATIC HMI' group. No TIA project change. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult WriteUnifiedRuntimeTagsV4(
            [Description("writes: array of {name, value} writes; scalar values only, maximum 500.")] WriteValue[] writes,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("confirmWrite: must be true (with dryRun=false) to actually write; default false refuses.")] bool confirmWrite = false,
            [Description("dryRun: true (default) previews and reads current values only.")] bool dryRun = true)
            => RuntimeToolContract.Run("WriteUnifiedRuntimeTags", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmWrite);
                return WriteUnifiedRuntimeTags(RuntimeToolContract.Writes(writes, "writes", dryRun, confirmWrite), pipeName, timeoutMs, confirmWrite, dryRun);
            });

        public ResponseJsonReport WriteUnifiedRuntimeTags(string writesJson, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000, bool confirmWrite = false, bool dryRun = true)
        {
            try
            {
                List<RuntimeWriteItem> writes;
                try { writes = RuntimeChannelsLogic.ParseWriteList(writesJson, "writesJson"); RuntimeChannelsLogic.ParsePipeName(pipeName); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (!dryRun && !confirmWrite)
                    return RuntimeRefusal($"Refused: writing {writes.Count} runtime tag(s) requires confirmWrite=true together with dryRun=false. Nothing was written.",
                        new JsonObject { ["plannedWrites"] = PlannedWrites(writes), ["safety"] = OpenPipeSafety(false) });

                var names = writes.Select(w => w.Name).ToList();
                string readCookie = RuntimeChannelsLogic.NewClientCookie("before");
                var before = UnifiedOpenPipeChannel.Exchange(pipeName, RuntimeChannelsLogic.BuildReadTagRequest(names, readCookie), readCookie, "ReadTag", timeoutMs);
                string? failure = OpenPipeFailure(before, "ReadTag");
                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (WriteTag)",
                    ["connected"] = before.Connected,
                    ["status"] = dryRun ? "Preview" : "Failed",
                    ["safety"] = OpenPipeSafety(!dryRun)
                };
                var meta = RuntimeMeta(false, dryRun, false);
                if (failure != null)
                {
                    data["error"] = failure;
                    data["requestLine"] = dryRun ? RuntimeChannelsLogic.BuildWriteTagRequest(writes, "<cookie>") : null;
                    return new ResponseJsonReport { Ok = false, Message = "Pre-write ReadTag failed, nothing written: " + failure, Data = data, Meta = meta };
                }
                var beforeTags = before.Response!.Tags.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);
                var items = new JsonArray();
                foreach (var w in writes)
                {
                    beforeTags.TryGetValue(w.Name, out var bt);
                    var o = new JsonObject { ["name"] = w.Name, ["requested"] = w.Value?.DeepClone(), ["before"] = bt?.Value, ["beforeQuality"] = bt?.Quality };
                    if (bt == null) o["beforeError"] = "not in ReadTag response";
                    else if (!bt.Ok) o["beforeError"] = bt.ErrorDescription;
                    items.Add(o);
                }
                data["items"] = items;
                if (dryRun)
                {
                    data["requestLine"] = RuntimeChannelsLogic.BuildWriteTagRequest(writes, "<cookie>");
                    bool allKnown = writes.All(w => beforeTags.TryGetValue(w.Name, out var t) && t.Ok);
                    // envelope: legacy-single-verdict
                    meta["success"] = allKnown;
                    return new ResponseJsonReport
                    {
                        Ok = allKnown,
                        Message = allKnown
                            ? $"Preview: {writes.Count} tag write(s) validated and current values read; nothing written. Re-run with dryRun=false and confirmWrite=true to write."
                            : "Preview: some tags could not be read (see items[].beforeError); fix the names before writing. Nothing written.",
                        Data = data,
                        Meta = meta
                    };
                }

                // Refuse to write tags the Runtime does not know: WriteTag would just fail per tag,
                // but a partial batch is harder to reason about than a clean refusal.
                var unknown = writes.Where(w => !beforeTags.TryGetValue(w.Name, out var t) || !t.Ok).Select(w => w.Name).ToList();
                if (unknown.Count > 0)
                {
                    data["error"] = "Not written: these tags could not be read before writing: " + string.Join(", ", unknown);
                    return new ResponseJsonReport { Ok = false, Message = data["error"]!.ToString(), Data = data, Meta = meta };
                }

                meta["mayHaveChanged"] = true;
                string writeCookie = RuntimeChannelsLogic.NewClientCookie("write");
                var write = UnifiedOpenPipeChannel.Exchange(pipeName, RuntimeChannelsLogic.BuildWriteTagRequest(writes, writeCookie), writeCookie, "WriteTag", timeoutMs);
                failure = OpenPipeFailure(write, "WriteTag");
                data["elapsedMs"] = before.ElapsedMs + write.ElapsedMs;
                if (failure != null)
                {
                    data["error"] = failure;
                    return new ResponseJsonReport { Ok = false, Message = "WriteTag failed (values may or may not have been applied): " + failure, Data = data, Meta = meta };
                }
                var writeTags = write.Response!.Tags.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);

                string afterCookie = RuntimeChannelsLogic.NewClientCookie("after");
                var after = UnifiedOpenPipeChannel.Exchange(pipeName, RuntimeChannelsLogic.BuildReadTagRequest(names, afterCookie), afterCookie, "ReadTag", timeoutMs);
                string? afterFailure = OpenPipeFailure(after, "ReadTag");
                var afterTags = after.Response?.Tags.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal) ?? new Dictionary<string, OpenPipeTagResult>(StringComparer.Ordinal);
                data["elapsedMs"] = before.ElapsedMs + write.ElapsedMs + after.ElapsedMs;

                int failed = 0;
                foreach (var w in writes)
                {
                    var o = (JsonObject)items.First(n => n!["name"]!.ToString() == w.Name)!;
                    writeTags.TryGetValue(w.Name, out var wt);
                    afterTags.TryGetValue(w.Name, out var at);
                    o["writeAttempted"] = true;
                    o["writeAccepted"] = wt != null && wt.Ok;
                    o["after"] = at?.Value;
                    o["afterQuality"] = at?.Quality;
                    o["writeOutcomeKnown"] = wt != null && (!wt.Ok || at != null && at.Ok);
                    bool verified = wt != null && wt.Ok && at != null && at.Ok && RuntimeChannelsLogic.ValuesMatch(w.Value, JsonValue.Create(at.Value));
                    o["verified"] = verified;
                    if (wt == null) o["error"] = "no entry for this tag in the NotifyWriteTag response";
                    else if (!wt.Ok) o["error"] = "WriteTag: " + wt.ErrorDescription + " (ErrorCode " + wt.ErrorCode + ")";
                    else if (afterFailure != null) o["error"] = "Readback failed: " + afterFailure;
                    else if (at == null || !at.Ok) o["error"] = "Readback failed: " + (at?.ErrorDescription ?? "tag missing in response");
                    else if (!verified) o["error"] = $"Readback '{at.Value}' differs from requested {w.Value?.ToJsonString()} (the PLC or a script may overwrite this tag).";
                    if (o["error"] != null) failed++;
                }
                bool ok = failed == 0;
                data["status"] = ok ? "WrittenAndVerified" : "Failed";
                meta["success"] = ok;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? $"Wrote and verified {writes.Count} runtime tag(s) via Open Pipe." : $"{failed} of {writes.Count} tag write(s) failed or did not verify (see items[].error).",
                    Data = data,
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"WriteUnifiedRuntimeTags failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetUnifiedRuntimeAlarms"), Description("[L2][Online-Monitoring][ONLINE] Read the currently ACTIVE alarms of WinCC Unified Runtime through WinCC Unified Open Pipe (ReadAlarm on \\\\.\\pipe\\HmiRuntime). Returns the alarm objects as the Runtime reports them (Name, State, StateText, EventText, RaiseTime, Priority, AlarmClassName, Tag, Value, ...). Optional filter uses the Runtime alarm filter syntax (e.g. \"AlarmClassName != 'Warning'\"); empty systemNames means all systems. Read-only, no acknowledgement, no subscription. Local only; user must be in the 'SIMATIC HMI' group.")]
        public CallToolResult ReadUnifiedRuntimeAlarmsV4(
            [Description("systemNames: structured string array input.")] string[] systemNames = null!,
            [Description("filter: optional alarm filter expression (Runtime syntax), e.g. \"Priority >= 10\". Empty = no filter.")] string filter = "",
            [Description("languageId: Windows LCID for alarm texts, e.g. 1033 (en-US), 1031 (de-DE), 2052 (zh-CN).")] int languageId = 1033,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("maxAlarms: cap on returned alarms (1..500); the total count is always reported.")] int maxAlarms = 200)
            => RuntimeToolContract.Run("GetUnifiedRuntimeAlarms", true, false, () =>
            {
                return ReadUnifiedRuntimeAlarms(RuntimeToolContract.Names(systemNames, "systemNames", true), filter, languageId, pipeName, timeoutMs, maxAlarms);
            });

        public ResponseJsonReport ReadUnifiedRuntimeAlarms(string systemNamesJson = "[]", string filter = "", int languageId = 1033, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000, int maxAlarms = 200)
        {
            try
            {
                List<string> systems;
                try
                {
                    systems = string.IsNullOrWhiteSpace(systemNamesJson) || systemNamesJson.Trim() == "[]" ? new List<string>() : RuntimeChannelsLogic.ParseNameList(systemNamesJson, "systemNamesJson");
                    RuntimeChannelsLogic.ParsePipeName(pipeName);
                }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                if (languageId <= 0) return RuntimeRefusal("languageId must be a positive Windows LCID, e.g. 1033.");
                int cap = Math.Min(Math.Max(maxAlarms, 1), 500);

                string cookie = RuntimeChannelsLogic.NewClientCookie("alarms");
                var ex = UnifiedOpenPipeChannel.Exchange(pipeName, RuntimeChannelsLogic.BuildReadAlarmRequest(systems, filter ?? "", languageId, cookie), cookie, "ReadAlarm", timeoutMs);
                string? failure = OpenPipeFailure(ex, "ReadAlarm");
                var alarms = new JsonArray();
                int total = 0;
                if (ex.Response?.Params is JsonObject p && p["Alarms"] is JsonArray arr)
                {
                    total = arr.Count;
                    foreach (var a in arr.Take(cap)) alarms.Add(a?.DeepClone());
                }
                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (ReadAlarm, read-only)",
                    ["connected"] = ex.Connected,
                    ["elapsedMs"] = ex.ElapsedMs,
                    ["filter"] = filter ?? "",
                    ["languageId"] = languageId,
                    ["totalCount"] = total,
                    ["returnedCount"] = alarms.Count,
                    ["dataComplete"] = failure == null && alarms.Count == total,
                    ["alarms"] = alarms,
                    ["safety"] = OpenPipeSafety(false)
                };
                if (failure != null) data["error"] = failure;
                bool ok = failure == null;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = failure ?? $"{total} active alarm(s) reported by the Runtime" + (alarms.Count < total ? $" ({alarms.Count} returned, raise maxAlarms for more)." : "."),
                    Data = data,
                    Meta = RuntimeMeta(ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetUnifiedRuntimeAlarms failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeUnifiedOpenPipe"), Description("[L2][Online-Monitoring][ONLINE-WRITE] Send ONE raw WinCC Unified Open Pipe expert-syntax request (object with 'message', message-specific 'params' and optional 'clientCookie'; protocol member casing is restored when sent) to the local Runtime and return the matching response line, e.g. BrowseTags, BrowseConfiguredAlarms, BrowseAlarmClasses, ReadConfig, ReadTag, ReadAlarm, WriteTag, WriteConfig. Defaults to PREVIEW (dryRun=true): validates the JSON and shows the exact line that would be sent, nothing is sent. Read-only messages (Read*/Browse*) are sent with dryRun=false; any other message (WriteTag, WriteConfig, ...) additionally requires confirmWrite=true. Subscribe*/Unsubscribe* are refused (streaming). No readback verification is performed here; prefer GetUnifiedRuntimeTags/WriteUnifiedRuntimeTags for tags. Local only. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UnifiedOpenPipeRequestV4(
            [Description("request: object containing message, params and optional clientCookie.")] OpenPipeRequest request,
            [Description("pipeName: named pipe of the Runtime; default \\\\.\\pipe\\HmiRuntime.")] string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName,
            [Description("timeoutMs: connect + response timeout in ms (500..120000).")] int timeoutMs = 5000,
            [Description("confirmWrite: required (with dryRun=false) for any message that is not Read*/Browse*.")] bool confirmWrite = false,
            [Description("dryRun: true (default) validates and shows the request line without sending it.")] bool dryRun = true)
            => RuntimeToolContract.Run("InvokeUnifiedOpenPipe", dryRun || request != null && request.IsReadOnly, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun || request != null && request.IsReadOnly, confirmWrite);
                return UnifiedOpenPipeRequest(RuntimeToolContract.Request(request!), pipeName, timeoutMs, confirmWrite, dryRun);
            });

        public ResponseJsonReport UnifiedOpenPipeRequest(string requestJson, string pipeName = RuntimeChannelsLogic.DefaultOpenPipeName, int timeoutMs = 5000, bool confirmWrite = false, bool dryRun = true)
        {
            try
            {
                string line, message, cookie; bool readOnly;
                try { (line, message, cookie, readOnly) = RuntimeChannelsLogic.PrepareRawRequest(requestJson); RuntimeChannelsLogic.ParsePipeName(pipeName); }
                catch (ArgumentException bad) { return RuntimeRefusal(bad.Message); }
                catch (NotSupportedException bad) { return RuntimeRefusal(bad.Message); }

                var data = new JsonObject
                {
                    ["pipeName"] = pipeName,
                    ["channel"] = "WinCC Unified Open Pipe (raw request)",
                    ["message"] = message,
                    ["readOnlyMessage"] = readOnly,
                    ["clientCookie"] = cookie,
                    ["requestLine"] = line,
                    ["safety"] = OpenPipeSafety(!readOnly)
                };
                if (dryRun)
                    return new ResponseJsonReport
                    {
                        Ok = true,
                        Message = $"Preview: '{message}' request validated; nothing sent. Re-run with dryRun=false" + (readOnly ? "" : " and confirmWrite=true") + " to send it.",
                        Data = data,
                        Meta = RuntimeMeta(true, true, false)
                    };
                if (!readOnly && !confirmWrite)
                    return RuntimeRefusal($"Refused: '{message}' is not a read-only Open Pipe message; sending it requires confirmWrite=true together with dryRun=false. Nothing sent.", data);

                var ex = UnifiedOpenPipeChannel.Exchange(pipeName, line, cookie, message, timeoutMs);
                data["connected"] = ex.Connected;
                data["elapsedMs"] = ex.ElapsedMs;
                data["responseLine"] = ex.ResponseLine;
                data["response"] = ex.Response?.Raw?.DeepClone();
                if (ex.SkippedLines.Count > 0) data["skippedLines"] = new JsonArray(ex.SkippedLines.Select(s => (JsonNode)s).ToArray());
                string? failure = OpenPipeFailure(ex, message);
                if (failure != null) data["error"] = failure;
                bool ok = failure == null;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = failure ?? $"'{message}' answered with '{ex.Response!.Message}' in {ex.ElapsedMs} ms.",
                    Data = data,
                    Meta = RuntimeMeta(ok, false, !readOnly)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"InvokeUnifiedOpenPipe failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
