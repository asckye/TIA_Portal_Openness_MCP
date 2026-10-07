using System;
using System.Collections.Generic;
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
        internal static bool ApprovalWrite(string tool, JsonObject args, bool candidate, bool classifiedWrite,
            bool hasDryRun, bool defaultPreview)
        {
            if (candidate) return args["mode"] is JsonValue mode && mode.TryGetValue<string>(out var text) && text == "apply";
            if (hasDryRun)
            {
                if (args["dryRun"] is JsonValue dryRun && dryRun.TryGetValue<bool>(out var preview))
                { if (preview) return false; }
                else if (args.ContainsKey("dryRun") || defaultPreview) return false;
            }
            return classifiedWrite || tool == "SaveProject" || tool == "SaveProjectCopy" || tool == "CloseProject";
        }

        internal static bool NeedsPrecheck(bool internalPreview, bool write, bool enabled, bool hasPreview)
            => !internalPreview && write && enabled && hasPreview;

        internal static string? MissingApplyArgument(JsonObject args, bool attach)
        {
            foreach (var key in attach ? new[] { "expectedPlanHash" } : new[] { "expectedProjectFile", "expectedPlanHash" })
                if (args[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return key;
            return args["confirm"] is JsonValue confirm && confirm.TryGetValue<bool>(out var accepted) && accepted ? null : "confirm";
        }

        internal static bool PreviewHasNoEffect(JsonNode? body)
        {
            if (body?["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var succeeded) || !succeeded || body["data"] is not JsonObject data) return false;
            bool False(string key) => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;
            return (string?)data["status"] == "not-found-not-deleted" && False("attempted") && False("executed") && False("deleted") && (string?)data["targetIdentity"] == ""
                || data["plan"]?["operations"] is JsonArray { Count: 0 }
                || data["inventoryComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var inventory) && inventory && data["items"] is JsonArray { Count: 0 };
        }

        internal static void ExportPreview(JsonObject data)
        {
            bool export = (string?)data["kind"] is "watch-table" or "technology-object"
                || (string?)data["options"] == "native-default-two-argument-overload";
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
            IReadOnlyDictionary<string, JsonElement> evidence, string? nativeMessage = null, string? reason = null)
            => kind switch
            {
                HostFailureKind.Argument => new Error(nativeMessage ?? "The request argument was refused before operation.", new InvalidArgumentDetails(parameter ?? "arguments", Array.Empty<string>())),
                HostFailureKind.Precondition => new Error(nativeMessage ?? "The request precondition failed before operation.", new PreconditionFailedDetails("native-admission", null)),
                HostFailureKind.Cancelled => new Error("Request cancelled before operation.", new CancelledDetails("approval-precheck")),
                HostFailureKind.Unknown => new Error("The operation outcome is unknown; inspect the retained evidence before a new session.", new OutcomeUnknownDetails("tool-call", evidence, reason)),
                HostFailureKind.InternalError => new Error("The host operation could not be completed.", new InternalErrorDetails(null)),
                _ => new Error("The native read could not be completed.", new NativeOperationFailedDetails(null, nativeMessage, evidence))
            };

        internal static string? SafeDiagnostic(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
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

        internal static string? AdmissionDiagnostic(Exception error, int? reportedCode = null, string? reportedOutcome = null)
        {
            for (var cause = error; cause != null; cause = cause.InnerException)
                if (cause is TiaMcp.Adapters.Contracts.AdapterPreconditionException) return SafeDiagnostic(cause.Message);
            return reportedCode == -32602 || reportedOutcome == "rejected-before-operation" ? SafeDiagnostic(error.Message) : null;
        }

        // Arbitrary exception text can contain caller inputs and credentials. Retain
        // structured evidence, and disclose diagnostic text only for typed admission.
        internal static IReadOnlyDictionary<string, JsonElement> FailureEvidence(string? exceptionType)
            => new Dictionary<string, JsonElement> {
                ["exceptionType"] = JsonSerializer.SerializeToElement(exceptionType),
                ["workerMessage"] = JsonSerializer.SerializeToElement<string?>(null) };

        internal static Error SessionReset() => new Error(SessionBehavior.Recovery, new SessionResetRequiredDetails("previous-outcome-unknown"));
        internal static Error Confirmation(string reason, string? hash, string? id)
            => new Error("Workbench confirmation is required before this write.", new ConfirmationRequiredDetails(reason, hash, id));
        internal static Outcome OutcomeOf(HostFailureKind kind) => kind == HostFailureKind.Unknown ? Outcome.Unknown
            : kind is HostFailureKind.ReadFailed or HostFailureKind.InternalError ? Outcome.ReadFailed : Outcome.RejectedBeforeOperation;
        internal static Execution ExecutionOf(HostFailureKind kind) => kind == HostFailureKind.Unknown ? Execution.Unknown
            : kind is HostFailureKind.ReadFailed or HostFailureKind.InternalError ? Execution.ReadOnly : Execution.NotStarted;
        internal static Completeness CompletenessOf(HostFailureKind kind) => kind == HostFailureKind.Unknown ? Completeness.Unknown : Completeness.None;
        internal static Execution ExecutionOf(Outcome outcome, bool mutation) => outcome switch {
            Outcome.RejectedBeforeOperation => Execution.NotStarted, Outcome.ReadFailed => Execution.ReadOnly,
            Outcome.Failed => Execution.Completed, Outcome.Partial => Execution.Partial, Outcome.Unknown => Execution.Unknown,
            _ => mutation ? Execution.Completed : Execution.ReadOnly };
        internal static Outcome OperationOutcome(bool succeeded, bool unknown, bool rejected, bool mutation, bool partial = false)
            => unknown ? Outcome.Unknown : rejected ? Outcome.RejectedBeforeOperation : succeeded ? Outcome.Succeeded
                : !mutation ? Outcome.ReadFailed : partial ? Outcome.Partial : Outcome.Failed;
    }
}
