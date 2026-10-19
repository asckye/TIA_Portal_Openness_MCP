using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OptionalPackageContract
    {
        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static bool Issued(JsonObject data) => Flag(data, "mayHaveChanged") == true
            || Flag(data, "mayHaveWrittenFiles") == true || Flag(data, "mayHaveExternalEffects") == true
            || Flag(data, "operationStarted") == true;

        // Marks set by the host's worker client: the request was sent and its acknowledgement
        // was lost (unknown native outcome), or it exceeded the worker frame before sending.
        private static bool WorkerMark(Exception exception, string mark, bool sent)
        {
            for (Exception? cause = exception; cause != null; cause = cause.InnerException)
                if (cause.Data["foundationRequestSent"] is bool value && value == sent && cause.Data[mark] != null) return true;
            return false;
        }
        private static bool WorkerOutcomeLost(Exception exception) => WorkerMark(exception, "workerOutcomeUnknown", true);

        internal static void RecordFailure(JsonObject data, Exception exception)
        {
            var cause = exception.GetBaseException();
            data["failureType"] = cause.GetType().Name;
            if (Issued(data) || WorkerOutcomeLost(exception)) return;
            data["operationNotStarted"] = true;
            string? code = WorkerMark(exception, "workerLimitBytes", false) ? "LIMIT_EXCEEDED" : cause is PortalException portal ? portal.Code switch
            {
                PortalErrorCode.InvalidParams => "INVALID_ARGUMENT",
                PortalErrorCode.NotFound => "NOT_FOUND",
                PortalErrorCode.NotSupportedOnVersion => "UNSUPPORTED_CAPABILITY",
                PortalErrorCode.InvalidState => "PRECONDITION_FAILED",
                _ => null
            } : cause is ArgumentException ? "INVALID_ARGUMENT"
                : cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                : cause is IOException ? "IO_FAILED" : cause is InvalidOperationException ? "PRECONDITION_FAILED" : null;
            if (code != null) data["rejectionCode"] = code;
        }

        internal static CallToolResult Run(string tool, bool writes, Func<ResponseMessage> operation, int? offset = null, int? limit = null)
        {
            try { return Map(tool, operation(), writes, offset, limit); }
            catch (Exception ex)
            {
                var evidence = new JsonObject();
                RecordFailure(evidence, ex);
                return MapOutcome(tool, new ResponseMessage { Meta = evidence }, writes, offset, limit, WorkerOutcomeLost(ex));
            }
        }

        internal static CallToolResult Map(string tool, ResponseMessage response, bool writes, int? offset = null, int? limit = null)
            => MapOutcome(tool, response, writes, offset, limit, false);

        private static CallToolResult MapOutcome(string tool, ResponseMessage response, bool writes, int? offset, int? limit, bool outcomeLost)
        {
            var data = response.Meta?.DeepClone().AsObject() ?? new JsonObject();
            bool issued = Issued(data);
            bool reset = Flag(data, "requiresExplicitRebind") == true;
            bool? success = Flag(data, "operationSuccess") ?? Flag(data, "success");
            string status = data["status"]?.ToString() ?? "";
            string? rejection = data["rejectionCode"]?.ToString();
            if (!issued && status == "InvalidState" && !data.ContainsKey("apiCallSuccess")) rejection = "PROJECT_NOT_BOUND";
            if (status == "HmiReadSessionBlocked") rejection = "SESSION_RESET_REQUIRED";
            bool nativeKnown = Flag(data, "nativeResult").HasValue;
            if (Flag(data, "nativeResult") == false) success = false;
            bool stateRequired = (tool == "RunTestSuiteCase" || tool == "ExchangePlcSupervisions") && writes && rejection == null;
            string? state = data["nativeState"]?.ToString();
            bool stateKnown = NativeResultState.Classify(data, tool) != TiaMcp.Adapters.Contracts.NativeStateKind.Unexpected;
            if (stateRequired || data.ContainsKey("nativeState"))
            {
                nativeKnown |= stateKnown;
                success = NativeResultState.Succeeded(data, tool) && success != false;
            }
            bool verificationFailed = Flag(data, "expectedPresenceVerified") == false || Flag(data, "verifiedAbsent") == false;
            if (verificationFailed) success = false;
            bool notStarted = Flag(data, "operationNotStarted") == true;
            // A lost worker acknowledgement is unknown for reads too, as in HostFailurePolicy.
            bool unknown = outcomeLost || rejection == null && writes && !notStarted && (stateRequired && !stateKnown
                || reset && issued || success != true && !nativeKnown && !verificationFailed);
            int imported = (data["imported"] as JsonArray)?.Count ?? 0;
            bool knownPartial = verificationFailed && imported > 0
                || success == false && nativeKnown && (data["file"] is JsonObject || data["output"] is JsonObject);
            Outcome outcome = unknown ? Outcome.Unknown : rejection != null ? Outcome.RejectedBeforeOperation
                : success == true ? Outcome.Succeeded : knownPartial ? Outcome.Partial
                : writes && nativeKnown || verificationFailed ? Outcome.Failed : writes && !notStarted ? Outcome.Unknown : Outcome.ReadFailed;
            string failureMessage = outcome == Outcome.RejectedBeforeOperation && rejection == "UNSUPPORTED_CAPABILITY"
                && (tool == "ExchangeCfcCharts" || tool == "ManageCfcChartProtection") && data["unsupportedReason"] is JsonValue reason
                && reason.TryGetValue<string>(out var guardReason) ? guardReason : "The optional-package operation did not establish success.";
            Error? error = outcome == Outcome.Succeeded ? null : new Error(failureMessage,
                outcome == Outcome.Unknown ? new OutcomeUnknownDetails("optional-package-operation", new Dictionary<string, JsonElement>())
                : outcome == Outcome.Partial ? new PartialFailureDetails(imported, imported > 0 ? 1 : 0, 0)
                : outcome == Outcome.RejectedBeforeOperation ? Rejection(rejection!, tool)
                : new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
            bool incomplete = Incomplete(data) || tool == "ExchangeCfcCharts" && (data["action"]?.ToString() == "import" || data["action"]?.ToString() == "export")
                || data.ContainsKey("contentVerified") && Flag(data, "contentVerified") != true;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (offset.HasValue && limit.HasValue && data["expectedCount"] is JsonValue count && count.TryGetValue<int>(out var total)
                && offset >= 0 && limit > 0) paging = McpServer.OffsetPage(offset.Value, limit.Value, total);
            Clean(data);
            if (NativeResultState.TryUnsuccessful(data, writes, out var nativeOutcome, out var nativeError, tool)) { outcome = nativeOutcome; error = nativeError; completeness = outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete; }
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            if (data["records"] is JsonArray records) { data.Remove("records"); data["items"] = records; }
            var warnings = new List<Warning> { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()) };
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData,
                "Only the reported fields and native evidence were observed; content semantics are not asserted.", new Dictionary<string, JsonElement>()));
            warnings.AddRange(NativeResultState.Warnings(data, tool));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, BehaviorPolicy.Current, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }

        private static ErrorDetails Rejection(string code, string tool) => code switch
        {
            "PROJECT_NOT_BOUND" => new ProjectNotBoundDetails(),
            "SESSION_RESET_REQUIRED" => new SessionResetRequiredDetails("optional-package-session"),
            "INVALID_ARGUMENT" => new InvalidArgumentDetails("arguments", Array.Empty<string>()),
            "UNSUPPORTED_CAPABILITY" => new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null),
            "NOT_FOUND" => new NotFoundDetails(null),
            "IO_FAILED" => new IoFailedDetails("optional-package-file", null),
            "LIMIT_EXCEEDED" => new LimitExceededDetails("arguments", null, null),
            _ => new PreconditionFailedDetails("optional-package-operation", null)
        };

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) || Incomplete(p.Value));
        }

        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "timestamp" || pair.Key == "tool" || pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0) obj.Remove(pair.Key);
                else if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }
    }

    // Typed CFC option package (PlcSoftware.GetService<ChartProviderS7>; identical on V20 / V21).
}
