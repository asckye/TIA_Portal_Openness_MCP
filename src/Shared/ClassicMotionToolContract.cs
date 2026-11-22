using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    // F3 adapter for this group's RunHmiStepTool results. Structural admission
    // belongs to the shared typed boundary; the services retain native validation.
    internal static class ClassicMotionToolContract
    {
        internal static string Json<T>(T value) => value == null ? "{}" : V4Json.Serialize(value);

        internal static string Capture(JsonObject meta, Func<JsonObject, string> action)
        {
            meta["mayHaveChanged"] = false;
            meta["mayHaveWrittenFiles"] = false;
            try { return action(meta); }
            catch (Exception ex)
            {
                var cause = ex.GetBaseException();
                meta["v4FailureCode"] = cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                    : cause is ArgumentException ? "INVALID_ARGUMENT"
                    : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                    : cause is TiaMcp.Adapters.Contracts.HardwareAddressingException hardware
                        ? hardware.Status == "InvalidState" ? "PRECONDITION_FAILED" : hardware.Status
                    : cause is TiaMcp.Adapters.Contracts.PlcSoftwareException plc ? plc.Code == "InvalidState" ? "PRECONDITION_FAILED" : plc.Code
                    : cause is PortalException portal ? portal.Code == PortalErrorCode.InvalidState ? "PRECONDITION_FAILED" : portal.Code.ToString()
                    : cause is InvalidOperationException ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED";
                throw;
            }
        }

        internal static CallToolResult Invoke(string tool, bool readOnly, bool current, Func<ResponseMessage> operation)
        {
            try { return Map(tool, operation(), readOnly, current); }
            catch (Exception error) /* swallow(privacy): escaped writes retain their unknown outcome */
            {
                return Result(tool, null, readOnly ? NativeFailureDiagnostics.FromException(Failure(), error) : Unknown(),
                    readOnly ? Outcome.ReadFailed : Outcome.Unknown,
                    readOnly ? Execution.ReadOnly : Execution.Unknown, Completeness.Unknown, current);
            }
        }

        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static CallToolResult Map(string tool, ResponseMessage response, bool readOnly, bool current)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            var success = data.ContainsKey("operationSuccess") ? Flag(data, "operationSuccess") : Flag(data, "success");
            bool issued = Flag(data, "mayHaveChanged") == true || Flag(data, "mayHaveWrittenFiles") == true;
            bool unknown = Flag(data, "mappingVerified") == false || !readOnly && Flag(data, "writeOutcomeUnknown") == true;
            string status = data["v4FailureCode"]?.ToString() ?? data["status"]?.ToString() ?? "";
            var refusal = HostBehavior.DocumentedRefusal(data)
                ?? HostBehavior.KnownRefusal(data, success, HardwareContract.ReleaseKey, tool, field => McpServer.MissingTargetDetails(tool, field))
                    ?? Refusal(tool, status, data);
            if (tool == "ManageClassicHmiScript" && (string?)data["unsupportedBy"] == "target" && refusal != null)
            {
                data["status"] = "UnsupportedOnTarget";
                data["nativeMessage"] = "The selected object must be a classic WinCC HmiTarget.";
                refusal = UnsupportedCapabilityDetails.ErrorFor(HardwareContract.ReleaseKey, tool, null, (string?)data["nativeMessage"], "target");
            }
            Outcome outcome; Execution execution; Error? error;
            if (status == "HmiReadSessionBlocked")
            {
                outcome = readOnly ? Outcome.ReadFailed : Outcome.Unknown;
                execution = readOnly ? Execution.ReadOnly : Execution.Unknown;
                error = HostBehavior.UnavailableCall(!readOnly);
            }
            else if (!readOnly && issued && (success != true || unknown))
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = Unknown(); }
            else if (readOnly && issued && success != true)
            { outcome = Outcome.ReadFailed; execution = Execution.ReadOnly; error = Failure(); }
            else if (!readOnly && Flag(data, "foundationKnownNoMutation") == true && HostBehavior.UnchangedNativeFailure(data, success))
            {
                outcome = Outcome.Failed; execution = Execution.Completed; error = HostBehavior.UnchangedNativeError(data);
            }
            else if (refusal != null)
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = refusal; }
            else if (success == true && !unknown)
            { outcome = Outcome.Succeeded; execution = readOnly ? Execution.ReadOnly : Execution.Completed; error = null; }
            else if (readOnly)
            { outcome = Outcome.ReadFailed; execution = Execution.ReadOnly; error = Failure(); }
            else if (Flag(data, "mayHaveChanged") == false && Flag(data, "mayHaveWrittenFiles") == false)
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error =
                    new Error("The operation stopped before a write was issued.", new PreconditionFailedDetails(Family(tool) + "-before-write", null));
            }
            else
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = Unknown(); }

            bool incomplete = Incomplete(data);
            Paging? paging = null;
            if (data["offset"] is JsonValue offset && offset.TryGetValue<int>(out int start)
                && data["limit"] is JsonValue limit && limit.TryGetValue<int>(out int size)
                && data["total"] is JsonValue total && total.TryGetValue<int>(out int count) && size > 0 && start >= 0)
                paging = McpServer.OffsetPage(start, size, count);
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            foreach (string key in new[] { "timestamp", "tool", "success", "v4FailureCode", "offset", "limit", "nextOffset" }) data.Remove(key);
            Sanitize(data);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete ? Completeness.Partial : Completeness.Complete;
            return Result(tool, data, error, outcome, execution, completeness, current, paging);
        }

        private static Error? Refusal(string tool, string status, JsonObject evidence)
        {
            switch (status)
            {
                case "InvalidState": return new Error("No project is bound.", new ProjectNotBoundDetails());
                case "NotFound": case "NOT_FOUND"
                    : return new Error("The requested target was not found.", McpServer.MissingTargetDetails(tool, evidence: evidence));
                case "NotSupportedOnVersion": case "UNSUPPORTED_CAPABILITY":
                    return UnsupportedCapabilityDetails.ErrorFor(HardwareContract.ReleaseKey, tool, null,
                        HostBehavior.SafeDiagnostic((string?)evidence["nativeMessage"]), (string?)evidence["unsupportedBy"]);
                case "NativeCrashRiskBlocked"
                    : return new Error("The target or version does not support this capability.",
                        new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null));
                case "InvalidParams": case "INVALID_ARGUMENT":
                    return new Error(McpServer.InvalidInput("").Message, McpServer.ArgumentDetails(tool, null, evidence));
                case "PRECONDITION_FAILED":
                    return new Error(HostBehavior.SafeDiagnostic(evidence["nativeMessage"]?.ToString()) ?? "The operation's preconditions were not satisfied.",
                        new PreconditionFailedDetails(Family(tool), null));
                default: return null;
            }
        }

        private static readonly IReadOnlyDictionary<string, string> ToolFamilies = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["ExportPlcProDiagInfo"] = "prodiag",
            ["ManagePlcSupervision"] = "prodiag",
            ["GetClassicHmiScreenTree"] = "classic-hmi",
            ["ManageClassicHmiScreenObject"] = "classic-hmi",
            ["ManageClassicHmiFolder"] = "classic-hmi",
            ["ManageClassicHmiGraphic"] = "classic-hmi",
            ["ListClassicHmiScripts"] = "classic-hmi",
            ["ManageClassicHmiScript"] = "classic-hmi",
            ["ManageClassicHmiCycle"] = "classic-hmi",
            ["ManageClassicHmiTextGraphicList"] = "classic-hmi",
            ["GetClassicHmiGlobalization"] = "classic-hmi",
            ["ListClassicHmiFaceplates"] = "classic-hmi",
            ["GetMotionAxisConfiguration"] = "motion",
            ["ManageMotionAxis"] = "motion",
            ["ExchangeMotionCamData"] = "motion",
            ["ConfigureMotionHardwareConnection"] = "motion"
        };

        private static string Family(string tool) => ToolFamilies[tool];

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            foreach (var pair in new[] { ("screenCount", "screens"), ("popupCount", "screenPopups"), ("templateCount", "screenTemplates"),
                ("tagTableCount", "tagTables"), ("scriptCount", "vbScripts"), ("count", "records") })
                if (obj[pair.Item1] is JsonValue count && count.TryGetValue<int>(out int total) && obj[pair.Item2] is JsonArray items && items.Count < total)
                    return true;
            return Flag(obj, "dataComplete") == false || obj.Any(p =>
                p.Key.EndsWith("Truncated", StringComparison.OrdinalIgnoreCase) && p.Value?.ToString() == "true"
                || p.Key.EndsWith("Error", StringComparison.OrdinalIgnoreCase) && p.Value != null
                || (p.Key == "failures" || p.Key == "attributeErrors" || p.Key == "excludedComplexProperties") && p.Value is JsonArray failures && failures
                    .Count > 0
                || Incomplete(p.Value));
        }

        private static void Sanitize(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Sanitize(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal))
                    obj[pair.Key] = "Native observation failed; details are available in the local diagnostic log.";
                else if (pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure") obj.Remove(pair.Key);
                else Sanitize(pair.Value);
            }
        }

        private static Error Failure() =>
            new Error("The operation did not establish a successful result.",
                new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() =>
            new Error("The write outcome is unknown. Inspect the target before repeating this call. Never replay it automatically. GetSessionState lists " +
                "unsettled request IDs.",
                new OutcomeUnknownDetails("classic-motion-operation", new Dictionary<string, JsonElement>()));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Execution execution,
            Completeness completeness, bool current, Paging? paging = null)
        {
            var warnings = new List<Warning>();
            if (current) warnings
                .Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.",
                        new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings
                .Add(new Warning(WarningCode.IncompleteData, "Only the reported scope and available fields were observed.", new Dictionary<string,
                        JsonElement>()));
            var meta =
                new Meta(DateTimeOffset.UtcNow, HardwareContract.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution, current
                    ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable,
                    completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }

}
