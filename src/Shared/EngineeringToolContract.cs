using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    // Contract boundary for alarms, OPC UA, technology objects and software units.
    // The services retain native execution; only their observed evidence is mapped here.
    internal static class EngineeringToolContract
    {
        internal static string Json<T>(T value) => V4Json.Serialize(value);
        internal static string Json<T>(AttributeMap<T>? value)
            => V4Json.Serialize(value ?? new AttributeMap<T>(new Dictionary<string, T>()));
        internal static string Value(NativeValue value, string action)
        {
            if (value.Kind == JsonValueKind.Undefined) return "null";
            if (action == "setParameter" && (value.Kind == JsonValueKind.Object || value.Kind == JsonValueKind.Array))
                throw new InputFailureException(McpServer.InvalidInput("value"));
            return V4Json.Serialize(value);
        }
        private sealed class InputFailureException : Exception
        {
            internal Error Error { get; }
            internal InputFailureException(Error error) => Error = error;
        }
        internal static CallToolResult Run(string tool, bool writes, Func<object> operation)
        {
            try { return Map(tool, operation(), writes); }
            catch (InputFailureException ex) { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, Completeness.None); }
            catch (Exception ex)
            {
                CallerInputFiles.RecordExportFailure(ex);
                var cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                if (cause is PortalException portal && (portal.Code == PortalErrorCode.NotFound || portal.Code == PortalErrorCode.InvalidParams
                    || portal.Code == PortalErrorCode.InvalidState || portal.Code == PortalErrorCode.NotSupportedOnVersion))
                {
                    ErrorDetails details = portal.Code == PortalErrorCode.NotFound ? new NotFoundDetails(null)
                        : portal.Code == PortalErrorCode.InvalidParams ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                        : portal.Code == PortalErrorCode.NotSupportedOnVersion ? new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null)
                        : new PreconditionFailedDetails("engineering-session", null);
                    return Result(tool, null, new Error("The engineering prerequisites were not satisfied.", details), Outcome.RejectedBeforeOperation, Completeness.None);
                }
                return PlcToolContract.Failure(tool, ex, writes, true);
            }
        }
        internal static CallToolResult Export(string tool, string path, Func<object> operation)
        {
            var arguments = new JsonObject { ["exportPath"] = path };
            try { CallerInputFiles.ValidateNativeFile(tool, arguments); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            { return Result(tool, null, new Error(error.Message, new InvalidArgumentDetails("exportPath", Array.Empty<string>())), Outcome.RejectedBeforeOperation, Completeness.None); }
            using var export = CallerInputFiles.ObserveExport(tool, arguments);
            return McpServer.ExportFailureResult(tool, Run(tool, true, operation), export);
        }
        private static bool? Flag(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        internal static CallToolResult Map(string tool, object response, bool writes)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            if (data["data"] is JsonObject report)
            {
                data.Remove("data");
                foreach (var pair in report) data[pair.Key] = pair.Value?.DeepClone();
            }
            data["summary"] = data["message"]?.DeepClone(); data.Remove("message");
            data["evidence"] = evidence;
            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success") ?? Flag(data, "ok");
            if (Flag(data, "ok") == false) success = false;
            string? state = (evidence["nativeState"] ?? evidence["state"] ?? evidence["result"]?["state"])?.ToString();
            var nativeKind = NativeResultState.Classify(evidence, tool);
            bool nativeKnown = nativeKind != TiaMcp.Adapters.Contracts.NativeStateKind.Unexpected;
            if (state != null || evidence.ContainsKey("nativeState"))
                success = NativeResultState.Succeeded(evidence, tool) && success != false;
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool before = !issued && (writes && Flag(evidence, "mayHaveChanged") == false || evidence["v4Rejection"] != null);
            string status = evidence["v4Rejection"]?.ToString() ?? evidence["status"]?.ToString() ?? "";
            bool projectMissing = status == "PROJECT_NOT_BOUND" || status == "InvalidState" && evidence.ContainsKey("tool")
                && !evidence.ContainsKey("apiCallSuccess") && !issued;
            bool reset = Flag(evidence, "requiresExplicitRebind") == true || Flag(evidence, "requiresSessionReset") == true;
            int succeeded = (data["imported"] as JsonArray)?.Count ?? 0;
            int failed = (data["failed"] as JsonArray)?.Count ?? 0;
            var nativeResult = evidence["result"] as JsonObject;
            succeeded += (nativeResult?["imported"] as JsonArray)?.Count ?? (nativeResult?["exportedDocuments"] as JsonArray)?.Count ?? 0;
            bool unknown = writes && (Flag(evidence, "verified") == false || Flag(evidence, "verifiedAbsent") == false
                || state != null && !nativeKnown || evidence.ContainsKey("nativeState") && state == null || reset && issued);
            if (failed > 0) { success = false; unknown |= writes && !before && !nativeKnown; }
            Outcome outcome; Error? error = null;
            if (unknown || writes && success != true && !before && !nativeKnown && !projectMissing && status != "HmiReadSessionBlocked")
            { outcome = Outcome.Unknown; error = Unknown(); }
            else if (projectMissing)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (status == "HmiReadSessionBlocked")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("engineering-session")); }
            else if (success == true) outcome = Outcome.Succeeded;
            else if (before)
            {
                outcome = Outcome.RejectedBeforeOperation;
                ErrorDetails details = status == "NOT_FOUND" || status == "NotFound" ? new NotFoundDetails(null)
                    : status == "UNSUPPORTED_CAPABILITY" || status == "NotSupported" ? new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null)
                    : status == "INVALID_ARGUMENT" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                    : new PreconditionFailedDetails("engineering-before-operation", null);
                error = new Error("The engineering request was rejected before execution.", details);
            }
            else if (!writes) { outcome = Outcome.ReadFailed; error = NativeFailure(); }
            else if (succeeded > 0)
            { outcome = Outcome.Partial; error = new Error("Some engineering operations did not complete.", new PartialFailureDetails(succeeded, Math.Max(1, failed), 0)); }
            else { outcome = Outcome.Failed; error = NativeFailure(); }
            bool incomplete = Incomplete(data) || Flag(evidence, "supported") == false;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (evidence["offset"] is JsonValue offset && offset.TryGetValue<int>(out var start)
                && evidence["limit"] is JsonValue limit && limit.TryGetValue<int>(out var size)
                && (evidence["total"] ?? evidence["totalCount"]) is JsonValue total && total.TryGetValue<int>(out var count)
                && start >= 0 && size > 0 && count >= start) paging = McpServer.OffsetPage(start, size, count);
            Clean(data);
            if (NativeResultState.TryUnsuccessful(evidence, writes, out var nativeOutcome, out var nativeError, tool)) { outcome = nativeOutcome; error = nativeError; completeness = outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete; }
            if (outcome != Outcome.Succeeded) data.Remove("summary");
            return Result(tool, data, error, outcome, completeness, writes, paging, reset);
        }
        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            bool MissingRows(string countKey, string rowsKey) => obj[countKey] is JsonValue count && count.TryGetValue<int>(out var total)
                && obj[rowsKey] is JsonArray rows && rows.Count < total;
            return Flag(obj, "dataComplete") == false || Flag(obj, "incomplete") == true || Flag(obj, "truncated") == true
                || Flag(obj, "groupsTruncated") == true || obj.ContainsKey("nameNote")
                || MissingRows("objectCount", "technologicalObjects") || MissingRows("parameterCount", "parameters")
                || obj["motionViews"] is JsonObject views && obj["tree"]?["objectCount"] is JsonValue objects && objects.TryGetValue<int>(out var objectCount) && views.Count < objectCount
                // Unit content lists have a fixed 200-name observation budget and no total.
                || obj.Any(p => p.Value is JsonArray names && names.Count == 200 && new[] { "blocks", "types", "groups", "documents", "tagTables", "externalSources", "system", "user", "systemBlockGroups" }.Contains(p.Key))
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null || Incomplete(p.Value));
        }
        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "timestamp" || pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }
        private static Error NativeFailure() => new Error("The engineering operation did not establish success.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() => new Error("The write outcome is unknown. Inspect the retained evidence and reset the session before further writes.", new OutcomeUnknownDetails("engineering-operation", new Dictionary<string, JsonElement>()));
        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes = false, Paging? paging = null, bool reset = false)
        {
            var warnings = new List<Warning> { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()) };
            if (data?["evidence"] is JsonObject nativeEvidence) warnings.AddRange(NativeResultState.Warnings(nativeEvidence, tool));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation covers only the reported scope and available fields.", new Dictionary<string, JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, HardwareContract.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, BehaviorPolicy.Current, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

}
