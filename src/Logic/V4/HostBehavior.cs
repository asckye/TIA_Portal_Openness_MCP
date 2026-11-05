using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;
#if TIA_APPROVAL_PRIVATE_JOURNAL
using DiagnosticPayload = TiaOpenness.Shared.ApprovalJournalPayload;
#else
using DiagnosticPayload = TiaOpenness.Shared.CallJournalPayload;
#endif

namespace TiaMcp.Logic.V4
{
    // Host wrappers supply transport/native evidence; this code owns admission decisions.
    internal static class HostBehavior
    {
        internal static bool ApprovalWrite(string tool, JsonObject args, bool classifiedWrite,
            bool hasDryRun, bool defaultPreview)
        {
            if (hasDryRun)
            {
                // Only an explicit boolean true previews. A present dryRun that is not a JSON boolean (a string, number or null
                // that a later binding may convert) fails closed as a write; an absent dryRun follows the schema default.
                if (args.ContainsKey("dryRun"))
                { if (args["dryRun"] is JsonValue dryRun && dryRun.TryGetValue<bool>(out var preview) && preview) return false; }
                else if (defaultPreview) return false;
            }
            if (tool is "ExchangeSystemDiagnosticsSettings" or "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules")
                return args["action"] is JsonValue action && action.TryGetValue<string>(out var operation) && operation == "import";
            if (tool == "GetEnvironmentDiagnostics")
                return args["fix"] is JsonValue fix && fix.TryGetValue<bool>(out var repair) && repair;
            return classifiedWrite || tool is "EnsureOpennessUserGroup" or "SaveProject" or "SaveProjectCopy" or "CloseProject";
        }

        internal static bool NeedsPrecheck(bool internalPreview, bool write, bool enabled, bool hasPreview)
            => !internalPreview && write && enabled && hasPreview;

        internal static string? MissingApplyArgument(JsonObject args, bool attach)
        {
            foreach (var key in attach ? new[] { "expectedPlanHash" } : new[] { "expectedProjectFile", "expectedPlanHash" })
                if (args[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return key;
            return args["confirm"] is JsonValue confirm && confirm.TryGetValue<bool>(out var accepted) && accepted ? null : "confirm";
        }

        internal static bool IsSingleXmlImport(string tool) => tool is "ImportPlcBlock" or "ImportPlcType" or "ImportPlcTagTable";
        internal static bool IsBatchImport(string tool) => tool is "ImportPlcBlocksFromDirectory" or "ImportPlcProgramFromDirectory";
        internal static bool LegacyBatchImport(string tool, JsonObject args) => tool == "ImportPlcBlocksFromDirectory"
            && (bool?)args["overwrite"] != true && (bool?)args["dryRun"] == false && string.IsNullOrEmpty((string?)args["expectedPlanHash"]);
        internal static Error? BatchApplyRefusal(string tool, JsonObject args)
        {
            if (!IsBatchImport(tool)) return null;
            string? missing = MissingApplyArgument(args, false);
            if (missing == null && args["importOrder"] is not JsonArray { Count: > 0 }) missing = "importOrder";
            if (missing == null && !PendingApproval.IsHash((string?)args["expectedPlanHash"])) missing = "expectedPlanHash";
            return missing == null ? null
                : new Error("Apply requires " + missing + " from a reviewed preview.", new InvalidArgumentDetails(missing, Array.Empty<string>()));
        }
        internal static Error? BatchPreviewRefusal(string tool, JsonObject args, JsonNode? body)
        {
            if (!IsBatchImport(tool) || body?["ok"]?.GetValue<bool>() != true) return null;
            string? key = (string?)body["data"]?["projectFile"] != (string?)args["expectedProjectFile"] ? "expectedProjectFile"
                : (string?)body["data"]?["planHash"] != (string?)args["expectedPlanHash"] ? "expectedPlanHash" : null;
            return key == null ? null
                : new Error("Plan or project changed; review a fresh preview before applying.", new PlanStaleDetails((string?)args["expectedPlanHash"], key));
        }

        internal static bool PreviewHasNoEffect(JsonNode? body)
        {
            if (body?["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var succeeded) || !succeeded || body["data"] is not JsonObject data) return false;
            bool False(string key) => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;
            return (string?)data["status"] == "not-found-not-deleted" && False("attempted") && False("executed") && False("deleted")
                && (string?)data["targetIdentity"] == ""
                || data["plan"]?["operations"] is JsonArray { Count: 0 }
                || data["inventoryComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var inventory) && inventory && data["items"] is JsonArray {
                    Count: 0 };
        }

        internal static void ExportPreview(JsonObject data)
        {
            bool export = data["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name is "watch-table" or "technology-object"
                || data["options"] is JsonValue options && options.TryGetValue<string>(out var option) && option == "native-default-two-argument-overload";
            if (!export || data["applyBlocked"] != null) return;
            if (data["planHash"] == null || data["status"] is not JsonValue status || !status.TryGetValue<string>(out var text)) return;
            string? reason = NativeExportPolicy.BlockedReason(text);
            data["applyBlocked"] = reason != null;
            data["applyBlockedReason"] = reason;
        }

        internal static Error? PreviewApplyRefusal(JsonNode? body)
        {
            if (body?["data"]?["applyBlocked"] is not JsonValue value || !value.TryGetValue<bool>(out var blocked) || !blocked) return null;
            return FailureError(HostFailureKind.Precondition, null, new Dictionary<string, JsonElement>(),
                SafeDiagnostic((string?)body["data"]?["applyBlockedReason"]) ?? "This preview blocks apply before operation.");
        }

        internal static Error FailureError(HostFailureKind kind, string? parameter,
            IReadOnlyDictionary<string, JsonElement> evidence, string? nativeMessage = null, string? reason = null, string stage = "approval-precheck",
            string? condition = null)
            => kind switch
            {
                HostFailureKind.Argument =>
                    new Error(nativeMessage ?? "The request argument was refused before operation.",
                        new InvalidArgumentDetails(parameter ?? "arguments", Array.Empty<string>())),
                HostFailureKind.Precondition =>
                    new Error(nativeMessage ?? "The request precondition failed before operation.",
                        new PreconditionFailedDetails(condition ?? "native-admission", null, parameter)),
                HostFailureKind.Cancelled => new Error("Request cancelled before operation.", new CancelledDetails(stage)),
                HostFailureKind.Unknown =>
                    new Error("The operation outcome is unknown; inspect the retained evidence before a new session.",
                        new OutcomeUnknownDetails("tool-call", evidence, reason)),
                HostFailureKind.InternalError => new Error("The host operation could not be completed.", new InternalErrorDetails(null)),
                _ =>
                    new Error("The native read could not be completed.",
                        new NativeOperationFailedDetails(null, nativeMessage ?? EvidenceMessage(evidence), evidence))
            };

        private static string? EvidenceMessage(IReadOnlyDictionary<string, JsonElement> evidence)
            => evidence.TryGetValue("workerMessage", out var message) && message.ValueKind == JsonValueKind.String
                ? SafeDiagnostic(message.GetString()) : null;

        internal static string? SafeDiagnostic(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            // Exception.ToString() and nested native messages can contain a whole stack.
            // Only the first message line belongs on the wire; the full diagnostic stays in the log.
            int end = message.IndexOfAny(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' });
            if (end >= 0) message = message.Substring(0, end);
            int inner = message.IndexOf(" ---> ", StringComparison.Ordinal);
            if (inner >= 0) message = message.Substring(0, inner);
            string json = DiagnosticPayload.Sanitize(JsonSerializer.Serialize(message));
            try
            {
                string? safe = JsonSerializer.Deserialize<string>(json);
                if (safe == null) return null;
                safe = Regex.Replace(safe, @"(?i)(?<![\w])(?:[a-z]:\\|\\\\)[^\r\n""<>|;]*", "<path>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                return DiagnosticPayload.Bound(safe);
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException || error is RegexMatchTimeoutException)
            { return "Native operation failed."; }
        }

        internal static string
            ? AdmissionDiagnostic(Exception error, int? reportedCode = null, string? reportedOutcome = null, string? reportedExceptionType = null)
        {
            if (HostFailurePolicy.SuppressAdmissionDiagnostic(error)) return null;
            var precondition = HostFailurePolicy.Precondition(error);
            if (precondition != null) return SafeDiagnostic(precondition.Message);
            return reportedExceptionType == nameof(TiaMcp.Adapters.Contracts.AdapterPreconditionException)
                && (reportedCode == -32602 || reportedOutcome == "rejected-before-operation") ? SafeDiagnostic(error.Message) : null;
        }

        internal static void SanitizeEvidence(JsonNode? node, bool evidence = true)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    bool sourceText = !evidence && (pair.Key.Equals("code", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("sourceCode", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("source", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("content", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("text", StringComparison.OrdinalIgnoreCase));
                    if (evidence && pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0
                        && (pair.Value is JsonArray || pair.Value is JsonValue stackValue && stackValue.TryGetValue<string>(out _))
                        || pair.Key.Equals("stackTrace", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("nativeStackTrace", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
                        obj.Remove(pair.Key);
                    else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        && (pair.Key.Equals("nativeMessage", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("workerMessage", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("message", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("error", StringComparison.OrdinalIgnoreCase)
                            || !sourceText && (text.Contains("\n   at ") || text.Contains(" ---> "))))
                        obj[pair.Key] = SafeDiagnostic(text);
                    else SanitizeEvidence(pair.Value, evidence || pair.Key.Equals("evidence", StringComparison.OrdinalIgnoreCase));
                }
            else if (node is JsonArray rows)
                for (int index = 0; index < rows.Count; index++)
                    if (rows[index] is JsonValue value && value.TryGetValue<string>(out var text)
                        && (text.Contains("\n   at ") || text.Contains(" ---> "))) rows[index] = SafeDiagnostic(text);
                    else SanitizeEvidence(rows[index], evidence);
        }

        // Host implementation details remain private. A worker's native diagnostic is
        // explicitly identified by its caller; ordinary host errors expose only IO failures.
        internal static IReadOnlyDictionary<string, JsonElement> FailureEvidence(string? exceptionType, string? message = null, bool native = false)
            => new Dictionary<string, JsonElement> {
                ["exceptionType"] = JsonSerializer.SerializeToElement(exceptionType),
                ["workerMessage"] = JsonSerializer
                    .SerializeToElement(native || exceptionType == nameof(System.IO.IOException) ? SafeDiagnostic(message) : null) };

        internal static Error? ExpectedProjectArgument(string? expectedProject)
        {
            if (expectedProject == null || expectedProject.IndexOfAny(new[] { '/', '\\', ':' }) < 0) return null;
            return new Error("expectedProject must be the exact project NAME, for example 'AutomaticDipCoatingMachine'. Use expectedProjectFile only when " +
                "the tool requests an absolute project file path.",
                new InvalidArgumentDetails("expectedProject", Array.Empty<string>()));
        }
        internal static JsonObject RetainedRecoveryEvidence(Exception error)
        {
            var retained = new JsonObject();
            foreach (string key in new[] { "recoveryDirectory", "recoveryFiles", "recoveryStatus", "recoveryWarning", "recoverySkipped", "attemptedPath" })
                if (error.Data[key] != null) retained[key] = JsonSerializer.SerializeToNode(error.Data[key]);
            return retained;
        }

        internal static Warning? BackupSkipped(JsonObject? data) => (string?)data?["recoveryStatus"] != "backup-skipped" ? null
            : new Warning(WarningCode.BackupSkipped, (string?)data?["recoveryWarning"] ?? "Recovery backup was skipped.",
                new Dictionary<string, JsonElement> {
                    ["objects"] = JsonSerializer.SerializeToElement(data?["recoverySkipped"] ?? new JsonArray()),
                    ["recoveryDirectory"] = JsonSerializer.SerializeToElement(data?["recoveryDirectory"]) });

        internal static Error SessionReset() => new Error(SessionBehavior.Recovery, new SessionResetRequiredDetails("previous-outcome-unknown"));
        internal static Error Confirmation(string reason, string? hash, string? id)
            => reason == "cancelled" ? new Error("Request cancelled before operation.", new CancelledDetails("approval-wait"))
                : new Error("Workbench confirmation is required before this write.", new ConfirmationRequiredDetails(reason, hash, id));
        internal static Outcome OutcomeOf(HostFailureKind kind) => kind == HostFailureKind.Unknown ? Outcome.Unknown
            : kind is HostFailureKind.ReadFailed or HostFailureKind.InternalError ? Outcome.ReadFailed : Outcome.RejectedBeforeOperation;
        internal static Execution ExecutionOf(HostFailureKind kind) => kind == HostFailureKind.Unknown ? Execution.Unknown
            : kind is HostFailureKind.ReadFailed or HostFailureKind.InternalError ? Execution.ReadOnly : Execution.NotStarted;
        internal static Completeness CompletenessOf(HostFailureKind kind) => kind == HostFailureKind.Unknown ? Completeness.Unknown : Completeness.None;
        internal static Execution ExecutionOf(Outcome outcome, bool mutation) => outcome switch {
            Outcome.RejectedBeforeOperation => Execution.NotStarted, Outcome.ReadFailed => Execution.ReadOnly,
            Outcome.Failed => Execution.Completed, Outcome.Partial => Execution.Partial, Outcome.Unknown => Execution.Unknown,
            _ => mutation ? Execution.Completed : Execution.ReadOnly };
        internal static Error? DocumentedRefusal(JsonObject evidence)
        {
            if (evidence["status"]?.ToString() != "PreconditionFailed"
                || evidence["mayHaveChanged"] is JsonValue value && value.TryGetValue<bool>(out var changed) && changed
                || string.IsNullOrWhiteSpace(evidence["precondition"]?.ToString())) return null;
            return new Error(SafeDiagnostic(evidence["refusal"]?.ToString()) ?? "TIA Portal refused the operation.",
                new PreconditionFailedDetails(evidence["precondition"]!.ToString(), null));
        }

        internal static Error UnchangedNativeError(JsonObject evidence, string? fallback = null)
        {
            string? message = SafeDiagnostic(evidence["nativeMessage"]?.ToString() ?? fallback);
            return new Error(message ?? "The native operation failed without changing the target.",
                new NativeOperationFailedDetails(null, message, new Dictionary<string, JsonElement>()));
        }

        // A negative worker verdict with explicit unchanged evidence is known, even for a write-declared tool.
        internal static bool UnchangedNativeFailure(JsonObject evidence, bool? success, bool allowWrittenFiles = false)
        {
            bool? Flag(string name) => evidence[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
            return success == false && Flag("apiCallSuccess") == false && Flag("mayHaveChanged") == false && (allowWrittenFiles ||
                Flag("mayHaveWrittenFiles") != true)
                && Flag("mayHaveExternalEffects") != true && Flag("operationStarted") != true
                && Flag("requiresSessionReset") != true && Flag("requiresExplicitRebind") != true
                && evidence["v4Rejection"] == null && evidence["rejectionCode"] == null
                && (evidence["status"]?.ToString() is null or "" or "ReadOrWriteFailed");
        }

        internal static Outcome OperationOutcome(bool succeeded, bool unknown, bool rejected, bool mutation, bool partial = false)
            => unknown ? Outcome.Unknown : rejected ? Outcome.RejectedBeforeOperation : succeeded ? Outcome.Succeeded
                : !mutation ? Outcome.ReadFailed : partial ? Outcome.Partial : Outcome.Failed;
    }
}
