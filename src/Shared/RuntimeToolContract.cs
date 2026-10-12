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
        private sealed class InputFailureException : Exception
        {
            internal Error Error { get; }
            internal InputFailureException(Error error) => Error = error;
        }

        internal static string Json<T>(T value, string parameter, InputContract<T> contract)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new InputFailureException(result.Error);
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
            if (!preview && !confirmed) throw new InputFailureException(new Error("Explicit confirmation is required.", new ConfirmationRequiredDetails(null)));
        }

        internal static CallToolResult Run(string tool, bool readOnly, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), readOnly, current); }
            catch (InputFailureException failure) { return Result(tool, null, failure.Error, Outcome.RejectedBeforeOperation, Completeness.None, current); }
            catch (Exception ex)
            {
                // A wrapped native exception can occur after a write was issued. Its
                // CLR type alone cannot prove that the operation never started.
                Error? rejection = ex is ArgumentException ? McpServer.InvalidInput("arguments")
                    : ex is NotSupportedException ? new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null)) : null;
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
                success = NativeResultState.Succeeded(data, tool) && success != false;
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
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("This capability is unavailable.", new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null)); }
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
            if (NativeResultState.TryUnsuccessful(data, !readOnly, out var nativeOutcome, out var nativeError, tool)) { outcome = nativeOutcome; error = nativeError; completeness = outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete; }
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
            if (data != null) warnings.AddRange(NativeResultState.Warnings(data, tool));
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, System.Text.Json.JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; inspect the retained per-item evidence.", new Dictionary<string, System.Text.Json.JsonElement>()));
            var meta = new Meta(DateTimeOffset.UtcNow, HardwareContract.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
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
}
