using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcp.Logic.V4
{
    // Host wrappers supply transport/native evidence; this code owns admission decisions.
    internal static class HostBehavior
    {
        internal sealed class UncertainWorkerFailure : InvalidOperationException, TiaMcp.Adapters.Contracts.IWorkerFailureEvidence
        {
            public string EvidenceJson { get; }
            internal UncertainWorkerFailure(string message, JsonObject reply) : base(message)
            {
                EvidenceJson = reply.ToJsonString();
                Data["workerOutcomeUnknown"] = true;
                Data["foundationRequestSent"] = true;
            }
        }

        internal static bool KnownMissingTarget(Exception error)
            => HostFailurePolicy.KnownNoMutation(error) && !HostFailurePolicy.PriorMutation(error) && !HostFailurePolicy.WorkerOutcomeUnknown(error)
                && TiaMcp.Adapters.Contracts.MutationEvidence.FailureCode(error) == "NOT_FOUND";

        internal static Error? KnownAdapterRefusal(Exception error)
        {
            if (!HostFailurePolicy.KnownNoMutation(error) || HostFailurePolicy.PriorMutation(error) || HostFailurePolicy.WorkerOutcomeUnknown(error)
                || HostFailurePolicy.Precondition(error) is not { } refusal) return null;
            return FailureError(refusal.IsArgument ? HostFailureKind.Argument : HostFailureKind.Precondition, refusal.ParamName,
                FailureEvidence(refusal.GetType().Name), SafeDiagnostic(refusal.Message), condition: refusal.Condition);
        }

        internal static Error? DevicePathRefusal(Exception error)
        {
            if (HostFailurePolicy.PriorMutation(error)) return null;
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
            {
                if (cause is TiaMcp.Adapters.Contracts.PlcSoftwareException { Code: "Ambiguous" } ambiguous
                    && ambiguous.Data["target"] is string target && ambiguous.Data["mayHaveChanged"] is not true)
                    return new Error(SafeDiagnostic(ambiguous.Message) ?? "The device path is ambiguous.",
                        new TargetAmbiguousDetails(target, ambiguous.Candidates?.ToArray() ?? Array.Empty<string>()));
                if (cause.Data["nativeResultEvidence"] is JsonObject evidence && evidence["target"] != null
                    && evidence["status"]?.ToString() is "Ambiguous" or "NotFound")
                    return KnownRefusal(evidence, false, "", "");
            }
            return null;
        }

        internal static bool IsPreview(JsonObject args, bool defaultPreview)
        {
            if (!args.TryGetPropertyValue("dryRun", out var value)) return defaultPreview;
            return value is JsonValue flag && flag.TryGetValue<bool>(out var preview) && preview;
        }

        internal static bool ApprovalWrite(string tool, JsonObject args, bool classifiedWrite,
            bool hasDryRun, bool defaultPreview)
        {
            // A present non-boolean value fails closed as a write. Only an absent value follows the schema default.
            if (tool == "ManageTestSuiteCase" && args["action"] is JsonValue editorAction
                && editorAction.TryGetValue<string>(out var editorOperation) && editorOperation == "showInEditor") return false;
            if (hasDryRun && IsPreview(args, defaultPreview)) return false;
            if (tool == "CloseProject" && args["secondaryProjectFile"] is JsonValue secondary
                && secondary.TryGetValue<string>(out var secondaryFile) && !string.IsNullOrWhiteSpace(secondaryFile)) return false;
            if (tool is "ExchangeSystemDiagnosticsSettings" or "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules")
                if (args["action"] is JsonValue action && action.TryGetValue<string>(out var operation) && operation == "import") return true;
            if (tool == "ManagePlcGitRepository" && ActorScope.Actor == ActorScope.Mcp
                && args["action"] is JsonValue gitAction && gitAction.TryGetValue<string>(out var gitOperation)
                && gitOperation is "stage" or "commit") return true;
            if (tool == "GetEnvironmentDiagnostics")
                return args["fix"] is JsonValue fix && fix.TryGetValue<bool>(out var repair) && repair;
            return classifiedWrite || tool is "EnsureOpennessUserGroup" or "SaveProject" or "SaveProjectCopy" or "CloseProject"
                || CallerInputFiles.WritesOutsideOutputRoot(tool, args);
        }

        internal static string DownloadTargetKind(string? targetIpAddress, string? pgPcInterface = null)
            => TiaMcpServer.Siemens.DownloadTargetPolicy.OnlineTargetKind(targetIpAddress, pgPcInterface);

        internal static bool ApprovalRequired(bool switchEnabled, string tool, JsonObject arguments)
        {
            if (switchEnabled) return true;
            if (TiaMcpServer.ModelContextProtocol.ToolTaxonomy.OperationOf(tool, null).Operation != "ONLINE-WRITE"
                || IsPreview(arguments, true)) return false;
            if (tool is "ManagePlcSimAdvancedInstance" or "WritePlcSimAdvancedTags" or "RunPlcSimAdvancedTestScenario") return false;
            return tool != "DownloadPlc"
                || DownloadTargetKind((string?)arguments["targetIpAddress"], (string?)arguments["pgPcInterface"]) != "plcsim";
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
            string? condition = null, string? diagnosticId = null, Func<string?, NotFoundDetails>? resolveMissingTarget = null)
            => kind switch
            {
                HostFailureKind.Argument =>
                    new Error(nativeMessage ?? "The request argument was refused before operation.",
                        new InvalidArgumentDetails(parameter ?? "", Array.Empty<string>())),
                HostFailureKind.Precondition =>
                    new Error(nativeMessage ?? "The request precondition failed before operation.",
                        condition == "not-found" ? resolveMissingTarget?.Invoke(parameter) ?? new NotFoundDetails(null)
                            : new PreconditionFailedDetails(condition ?? "native-admission", null, parameter)),
                HostFailureKind.Cancelled => new Error("Request cancelled before operation.", new CancelledDetails(stage)),
                HostFailureKind.Unknown =>
                    new Error("Only this call has an unknown outcome. Do not repeat this call. Read GetCallResult(requestId) and inspect its target. " +
                        "GetSessionState lists unsettled requests.",
                        new OutcomeUnknownDetails("tool-call", evidence, reason)),
                HostFailureKind.InternalError => new Error("The host operation could not be completed.", new InternalErrorDetails(diagnosticId)),
                _ =>
                    new Error("The native read could not be completed.",
                        new NativeOperationFailedDetails(null, nativeMessage ?? EvidenceMessage(evidence), evidence))
            };

        private static string? EvidenceMessage(IReadOnlyDictionary<string, JsonElement> evidence)
            => evidence.TryGetValue("workerMessage", out var message) && message.ValueKind == JsonValueKind.String
                ? SafeDiagnostic(message.GetString()) : null;

        internal static string? SafeDiagnostic(string? message) => NativeFailureDiagnostics.Text(message);

        internal static string
            ? AdmissionDiagnostic(Exception error, int? reportedCode = null, string? reportedOutcome = null, string? reportedExceptionType = null)
        {
            if (HostFailurePolicy.SuppressAdmissionDiagnostic(error)) return null;
            var precondition = HostFailurePolicy.Precondition(error);
            if (precondition != null) return SafeDiagnostic(precondition.Message);
            return reportedExceptionType == nameof(TiaMcp.Adapters.Contracts.AdapterPreconditionException)
                && (reportedCode == -32602 || reportedOutcome == "rejected-before-operation") ? SafeDiagnostic(error.Message) : null;
        }

        internal static void SanitizeEvidence(JsonNode? node, bool evidence = true, bool diagnostic = false)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key == "cleanupRequired" && pair.Value is JsonArray cleanup)
                        foreach (var task in cleanup.OfType<JsonObject>())
                            if (task["restoreFrom"] is JsonValue restore && restore.TryGetValue<string>(out var path)
                                && (obj.ContainsKey("evidenceDirectory") || Path.IsPathRooted(path)))
                                task["restoreFrom"] = RestorePath(obj, path);
                    bool sourceText = !evidence && (pair.Key.Equals("code", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("sourceCode", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("source", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("content", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("readable", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("scriptCode", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("globalDefinitionAreaScriptCode", StringComparison.OrdinalIgnoreCase)
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
                            || (evidence || diagnostic) && pair.Key.Equals("detailText", StringComparison.OrdinalIgnoreCase)
                            || diagnostic && pair.Key.Equals("text", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("error", StringComparison.OrdinalIgnoreCase)
                            || !sourceText && (text.Contains("\n   at ") || text.Contains(" ---> "))))
                        obj[pair.Key] = SafeDiagnostic(text);
                    else SanitizeEvidence(pair.Value, evidence || pair.Key.Equals("evidence", StringComparison.OrdinalIgnoreCase),
                        diagnostic || pair.Key.Equals("messageData", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("detailMessageData", StringComparison.OrdinalIgnoreCase));
                }
            else if (node is JsonArray rows)
                for (int index = 0; index < rows.Count; index++)
                    if (rows[index] is JsonValue value && value.TryGetValue<string>(out var text)
                        && (diagnostic || text.Contains("\n   at ") || text.Contains(" ---> "))) rows[index] = SafeDiagnostic(text);
                    else SanitizeEvidence(rows[index], evidence, diagnostic);
        }

        // Host implementation details remain private. A worker's native diagnostic is
        // explicitly identified by its caller; ordinary host errors expose only IO failures.
        internal static IReadOnlyDictionary<string, JsonElement> FailureEvidence(string? exceptionType, string? message = null,
            bool native = false, int? hResult = null)
        {
            var evidence = new Dictionary<string, JsonElement> {
                ["exceptionType"] = JsonSerializer.SerializeToElement(exceptionType),
                ["workerMessage"] = JsonSerializer.SerializeToElement(
                    native || exceptionType == nameof(System.IO.IOException) ? SafeDiagnostic(message) : null) };
            if (hResult.HasValue) evidence["hResult"] = JsonSerializer.SerializeToElement(hResult);
            return evidence;
        }

        internal static IReadOnlyDictionary<string, JsonElement> ScriptImportEvidence(JsonObject meta)
        {
            // Worker messages and nested diagnostics can contain script text.
            var retained = new JsonObject();
            foreach (var key in new[] { "phase", "importAttempted", "importReturned", "scriptCountAfterImport", "moduleExistsAfterImport",
                "nativeExceptionType", "mayHaveChanged", "writeOutcomeUnknown", "rollbackCompleted" })
                if (meta[key] is JsonValue value) retained[key] = value.DeepClone();
            if (meta["candidateDirectory"] is JsonValue) retained["candidateDirectory"] = "<path>";
            if (meta["candidateDirectoryRelativeToTemp"] is JsonValue locator && locator.TryGetValue<string>(out var relative))
                retained["candidateDirectoryRelativeToTemp"] = System.IO.Path.IsPathRooted(relative) ? "<path>" : relative;
            return NativeFailureDiagnostics.Evidence(JsonSerializer.SerializeToElement(retained)).EnumerateObject()
                .ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        }

        internal static Warning? ReadbackWarning(JsonObject? evidence, Outcome outcome)
        {
            bool Flag(string key) => evidence?[key] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
            if (outcome != Outcome.Succeeded || evidence == null || !Flag("readbackFailed") || !Flag("writeReturned") || evidence.ContainsKey("rollbackOutcome")
                || evidence.ContainsKey("connectionUnavailable") || evidence.ContainsKey("portalLost")
                || Flag("writeOutcomeUnknown") || Flag("rollbackCompleted") || Flag("unrestoredWrite")) return null;
            return new Warning(WarningCode.IncompleteData, "The write completed but its readback failed.", new Dictionary<string, JsonElement> {
                ["reason"] = JsonSerializer.SerializeToElement("readback-failed"),
                ["phase"] = JsonSerializer.SerializeToElement(evidence["phase"]?.ToString()),
                ["nativeMessage"] = JsonSerializer.SerializeToElement(SafeDiagnostic(evidence["nativeMessage"]?.ToString())) });
        }

        internal static IReadOnlyDictionary<string, JsonElement>? WorkerReplyEvidence(string tool, Exception error,
            IReadOnlyDictionary<string, JsonElement>? initial = null) => WorkerReplyEvidence(tool, error, initial, null);

        internal static IReadOnlyDictionary<string, JsonElement>? WorkerReplyEvidence(string tool, Exception error,
            IReadOnlyDictionary<string, JsonElement>? initial, Func<string, string>? redact)
        {
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
            {
                if (cause is not TiaMcp.Adapters.Contracts.IWorkerFailureEvidence { EvidenceJson: string json }) continue;
                JsonObject? reply;
                try { reply = JsonNode.Parse(json) as JsonObject; }
                catch (JsonException) /* swallow(parse-fallback): malformed optional evidence cannot replace the original failure */ { continue; }
                if (reply == null) continue;
                if (tool == "SetUnifiedGlobalScript")
                    return ScriptImportEvidence((reply["Value"] as JsonObject)?["Meta"] as JsonObject ?? reply["Meta"] as JsonObject ?? reply);
                var retained = new JsonObject();
                void Collect(JsonObject? source)
                {
                    if (source == null) return;
                    CollectWorkerEvidence(source, retained, redact);
                }
                Collect(reply);
                foreach (var owner in new[] { reply, reply["Value"] as JsonObject, reply["Step"] as JsonObject, reply["Import"] as JsonObject,
                      reply["Observation"] as JsonObject })
                {
                    Collect(owner?["Meta"] as JsonObject);
                    Collect((owner?["Failure"] as JsonObject)?["Evidence"] as JsonObject);
                }
                if (retained.Count == 0) continue;
                var result = initial?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, JsonElement>();
                foreach (var pair in retained) result[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
                return result;
            }
            return tool == "SetUnifiedGlobalScript" ? new Dictionary<string, JsonElement>() : initial;
        }

        private static void CollectWorkerEvidence(JsonObject source, JsonObject retained, Func<string, string>? redact = null)
        {
            if (source["cleanupRequired"] is JsonArray tasks)
            {
                var safe = new JsonArray();
                foreach (var task in tasks.Take(16))
                {
                    if (task is JsonValue text && text.TryGetValue<string>(out var message)) safe.Add(Diagnostic(message));
                    if (task is not JsonObject fields) continue;
                    var row = new JsonObject();
                    foreach (var key in new[] { "target", "reason", "action", "restoreFrom", "previousType" })
                        if (fields[key] is JsonValue field && field.TryGetValue<string>(out var content))
                            row[key] = Diagnostic(key == "restoreFrom" ? RestorePath(source, content) : content);
                    safe.Add(row);
                }
                retained["cleanupRequired"] = safe;
            }
            string Diagnostic(string raw)
            {
                var safe = SafeDiagnostic(redact == null ? raw : redact(raw)) ?? "";
                return safe.Length <= 1024 ? safe : safe.Substring(0, 1024);
            }
            foreach (string key in new[] { "nativeMessage", "phase", "nativeResult", "errorCount", "warningCount", "deviceCountBefore",
                "deviceCountAfter", "tagCountBefore", "tagCountAfter", "countBefore", "countAfter", "readbackFailed", "writeReturned",
                "connectionUnavailable", "portalLost", "restored", "exportsIssued", "queryRefusals", "mapped", "mappedAfterFailure", "failed",
                "status", "importReturned", "logFileMissing" })
                if (source[key] is JsonValue scalar)
                {
                    if (scalar.TryGetValue<string>(out var raw)) retained[key] = Diagnostic(raw);
                    else if (scalar.TryGetValue<bool>(out var flag)) retained[key] = flag;
                    else if (scalar.TryGetValue<long>(out var count)) retained[key] = count;
                    else if (scalar.TryGetValue<int>(out var number)) retained[key] = number;
                }
            if (source["messages"] is JsonArray messages)
                retained["messages"] = new JsonArray(messages.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _))
                    .Take(16).Select(v => (JsonNode?)JsonValue.Create(Diagnostic(v.GetValue<string>()))).ToArray());
            if (source.ContainsKey("logFile")) retained["logFile"] = source["logFile"]?.DeepClone();
            if (source["logExcerpt"] is JsonArray lines) retained["logExcerpt"] = lines.DeepClone();
            if (source.ContainsKey("logFile") || source.ContainsKey("logExcerpt")) SanitizeAmlDiagnostics(retained, redact);
            SanitizeEvidence(retained, diagnostic: true);
        }

        internal static void SanitizeAmlDiagnostics(JsonObject evidence, Func<string, string>? redact = null)
        {
            if (evidence["logFile"] is JsonObject file)
            {
                var summary = new JsonObject();
                foreach (string key in new[] { "bytes", "sha256", "contentSemanticsVerified" })
                    if (file[key] != null) summary[key] = file[key]!.DeepClone();
                if (file.ContainsKey("path")) summary["path"] = "<path>";
                evidence["logFile"] = summary;
            }
            foreach (string key in new[] { "logExcerpt", "messages" })
            {
                if (evidence[key] is not JsonArray values) continue;
                var lines = values.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>());
                evidence[key] = JsonSerializer.SerializeToNode(TiaMcp.Adapters.Contracts.NativeFailureText.BoundedLines(lines,
                    raw => SafeDiagnostic(redact == null ? raw : redact(raw)) ?? ""));
            }
        }

        private static string RestorePath(JsonObject evidence, string path)
        {
            if (evidence["evidenceDirectory"] is not JsonValue directory || !directory.TryGetValue<string>(out var root)
                || !Path.IsPathRooted(root)) return "<path>";
            try
            {
                string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(boundary, path));
                return full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                    ? full.Substring(boundary.Length) : "<path>";
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return "<path>"; }
        }

        internal static IReadOnlyDictionary<string, JsonElement> WorkerReplyEvidence(JsonObject source, Func<string, string>? redact = null)
        {
            var retained = new JsonObject();
            CollectWorkerEvidence(source, retained, redact);
            // A preflight refusal reported alongside possible mutation is not a diagnostic of the native write.
            if (source["rollbackOutcome"] == null && source["nativeResult"] == null && source["writeReturned"] == null
                && source["status"]?.ToString() is "NotFound" or "NOT_FOUND" or "InvalidParams" or "INVALID_ARGUMENT" or "AlreadyExists"
                    or "ALREADY_EXISTS" or "Ambiguous" or "NotSupported" or "NotSupportedOnVersion" or "UNSUPPORTED_CAPABILITY")
                retained.Remove("nativeMessage");
            return retained.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
        }

        internal static Error? ExpectedProjectArgument(string? expectedProject)
        {
            if (expectedProject == null || expectedProject.IndexOfAny(new[] { '/', '\\', ':' }) < 0) return null;
            return new Error("expectedProject must be the exact project NAME, for example 'ExampleProject'. Use expectedProjectFile only when " +
                "the tool requests an absolute project file path.",
                new InvalidArgumentDetails("expectedProject", Array.Empty<string>(), expected: "exact project name"));
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

        internal static Error UnavailableCall(bool mutation) => mutation
            ? new Error("Only this call has an unknown outcome. Do not repeat this call. Read GetCallResult(requestId) and inspect its target.",
                new OutcomeUnknownDetails("native-call", new Dictionary<string, JsonElement>()))
            : new Error("The native read could not be completed.",
                new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        internal static Error Confirmation(string reason, string? hash, string? id, bool outsideOutputRoot = false)
            => reason == "cancelled" ? new Error("Request cancelled before operation.", new CancelledDetails("approval-wait"))
                : new Error("Workbench confirmation is required before this write."
                    + (outsideOutputRoot ? " Write under the output root (GetSessionState data.outputRoot) to skip confirmation." : ""),
                    new ConfirmationRequiredDetails(reason, hash, id));
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
            return DocumentedRefusalError(evidence);
        }

        internal static Error DocumentedRefusalError(JsonObject evidence)
        {
            string? condition = evidence["precondition"]?.ToString();
            string? parameter = evidence["parameter"]?.ToString();
            return new Error(SafeDiagnostic(evidence["refusal"]?.ToString()) ?? "TIA Portal refused the operation.",
                new PreconditionFailedDetails(condition, null, parameter));
        }

        internal static Error UnchangedNativeError(JsonObject evidence, string? fallback = null)
        {
            string? message = SafeDiagnostic(evidence["nativeMessage"]?.ToString() ?? fallback);
            return new Error(message ?? "The native operation failed without changing the target.",
                new NativeOperationFailedDetails(null, message, new Dictionary<string, JsonElement>()));
        }

        internal static Error? KnownRefusal(JsonObject evidence, bool? success, string release, string tool,
            Func<string?, NotFoundDetails>? resolveMissingTarget = null)
        {
            bool? Flag(string key) => evidence[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
            if (success == true || Flag("mayHaveChanged") != false || Flag("mayHaveWrittenFiles") == true
                || Flag("operationStarted") == true
                || Flag("mayHaveExternalEffects") == true || Flag("writeOutcomeUnknown") == true || Flag("connectionUnavailable") == true)
                return null;
            string? parameter = evidence["parameter"]?.ToString();
            string? message = SafeDiagnostic(evidence["nativeMessage"]?.ToString());
            var refusal = evidence["status"]?.ToString() switch
            {
                "NotFound" or "NOT_FOUND" => new Error(message ?? "The requested object was not found.",
                    new NotFoundDetails(evidence["missingDeviceTarget"]?.ToString() ?? parameter)),
                "Ambiguous" => new Error(message ?? "The device path matches more than one device.",
                    new TargetAmbiguousDetails(evidence["target"]?.ToString(),
                        (evidence["candidates"] as JsonArray)?.Select(c => c!.GetValue<string>()).ToArray() ?? Array.Empty<string>())),
                "InvalidParams" or "INVALID_ARGUMENT" => new Error(message ?? "The request argument was refused before operation.",
                    new InvalidArgumentDetails(parameter ?? "arguments",
                        evidence["allowedValues"] is JsonArray allowed ? allowed.OfType<JsonValue>()
                            .Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>()).ToArray() : Array.Empty<string>())),
                "AlreadyExists" or "ALREADY_EXISTS" =>
                    new Error(message ?? "The requested object already exists.", new AlreadyExistsDetails(resolveMissingTarget?.Invoke(parameter).Target)),
                "NotSupported" or "NotSupportedOnVersion" or "UNSUPPORTED_CAPABILITY" =>
                    UnsupportedCapabilityDetails.ErrorFor(release, tool, (string?)evidence["action"], message),
                _ => null
            };
            if (refusal != null && evidence.ContainsKey("nativeMessage")) evidence["nativeMessage"] = message;
            return refusal;
        }

        internal static Error? KnownRefusalWithMessage(JsonObject evidence, bool? success, string release, string tool, string? message,
            Func<string?, NotFoundDetails>? resolveMissingTarget = null)
        {
            var refusal = KnownRefusal(evidence, success, release, tool, resolveMissingTarget);
            if (refusal == null) return null;
            string? nativeMessage = SafeDiagnostic(evidence["nativeMessage"]?.ToString()) ?? SafeDiagnostic(message);
            if (nativeMessage == null) return refusal;
            return refusal.Details is UnsupportedCapabilityDetails capability
                ? UnsupportedCapabilityDetails.ErrorFor(release, tool, capability.Action, nativeMessage)
                : new Error(nativeMessage, refusal.Details);
        }

        // A negative worker verdict with explicit unchanged evidence is known, even for a write-declared tool.
        internal static bool UnchangedNativeFailure(JsonObject evidence, bool? success, bool allowWrittenFiles = false)
        {
            bool? Flag(string name) => evidence[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
            return success == false && Flag("apiCallSuccess") == false && Flag("mayHaveChanged") == false && (allowWrittenFiles ||
                Flag("mayHaveWrittenFiles") != true)
                && Flag("mayHaveExternalEffects") != true && Flag("operationStarted") != true
                && Flag("writeOutcomeUnknown") != true && Flag("connectionUnavailable") != true
                && evidence["v4Rejection"] == null && evidence["rejectionCode"] == null
                && (evidence["status"]?.ToString() is null or "" or "ReadOrWriteFailed");
        }

        internal static Outcome OperationOutcome(bool succeeded, bool unknown, bool rejected, bool mutation, bool partial = false)
            => unknown ? Outcome.Unknown : rejected ? Outcome.RejectedBeforeOperation : succeeded ? Outcome.Succeeded
                : !mutation ? Outcome.ReadFailed : partial ? Outcome.Partial : Outcome.Failed;
    }
}
