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

        internal static Error RestrictArguments(string? tool, JsonElement schema, Error error, JsonElement arguments, JsonObject? data)
        {
            var input = new Inputs.InputSchema(schema);
            string? machineParameter = null;
            if (tool == "PlanProjectGeneration" && error.Details is InvalidArgumentDetails details && data?["validationErrors"] is JsonArray issues
                && arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("machine", out var machine))
            {
                foreach (var issue in issues.OfType<JsonObject>())
                {
                    if (issue["path"] is not JsonValue label || !label.TryGetValue<string>(out var path)
                        || !path.StartsWith("/machine/", StringComparison.Ordinal)) continue;
                    string safe = Generation.GenerationSchemas.PublicMachinePointer(path, machine);
                    issue["path"] = safe;
                    if (path == details.Parameter && details.Parameters.Count == 1) machineParameter = safe;
                }
            }
            if (machineParameter == null) return input.Restrict(error, arguments, data);

            // The MCP contract declares the machine root. Only the generation validator can identify its public descendants.
            var rootError = new Error(error.Message, new InvalidArgumentDetails("/machine", Array.Empty<string>()));
            var restricted = input.Restrict(rootError, arguments, data);
            return new Error(restricted.Message, new InvalidArgumentDetails(machineParameter, Array.Empty<string>()));
        }

        internal static Error? CompilePasswordRefusal(string release, string tool, JsonElement arguments)
            => tool is "CompilePlcSoftware" or "CompilePlcDiagnostics" && arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.String
                && TiaMcp.Adapters.Contracts.PlcCompileScope.PasswordRefusal(release, password.GetString()!,
                    TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName) is string reason
                ? UnsupportedCapabilityDetails.ErrorFor(release, tool, null, reason) : null;

        internal static Error? MissingRuntimeWriteConfirmation(string tool, JsonElement arguments)
            => tool == "WritePlcWebVars" && arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty("dryRun", out var dryRun) && dryRun.ValueKind == JsonValueKind.False
                && (!arguments.TryGetProperty("confirmWrite", out var confirm) || confirm.ValueKind != JsonValueKind.True)
                ? new Error("Writing PLC web variables requires confirmWrite=true.", new ConfirmationRequiredDetails(null)) : null;

        // Only the top-level credential fields that formerly resolved vault references reserve this prefix.
        internal static Error? RemovedVaultReference(string tool, JsonElement arguments, string path = "")
        {
            if (arguments.ValueKind != JsonValueKind.Object) return null;
            Error? Forwarded(JsonElement call, string prefix)
                => call.ValueKind == JsonValueKind.Object && call.TryGetProperty("name", out var target) && target.ValueKind == JsonValueKind.String
                    && call.TryGetProperty("arguments", out var nested) ? RemovedVaultReference(target.GetString()!, nested, prefix) : null;
            if (tool is "CallTool" or "PreviewToolCall") return Forwarded(arguments, path + "/arguments");
            if (tool is "PreviewToolBatch" or "RunReadOnlyToolBatch" && arguments.TryGetProperty("operations", out var operations)
                && operations.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var operation in operations.EnumerateArray())
                    if (Forwarded(operation, path + "/operations/" + i++ + "/arguments") is { } error) return error;
                return null;
            }
            foreach (var field in arguments.EnumerateObject())
            {
                bool credential = field.Name switch
                {
                    "password" => tool is "CompilePlcDiagnostics" or "CompilePlcSoftware" or "ConnectOnlinePlc" or "DownloadPlc"
                        or "GetPlcBlockFingerprints" or "UploadStationFromPlc" or "UploadDeviceParameters" or "ManagePlcBlockProtection"
                        or "ManagePlcBlockWriteProtection" or "ManagePlcCertificate" or "ManagePlcProtection" or "ManageDeviceUsers"
                        or "ManageProjectUserManagement" or "ManagePlcSafety" or "ManageHardwareUtilities" or "ManageDriveSecurity"
                        or "ManageTeamcenterConnection" or "ManageSinumerikArchive" or "GetPlcWebVars" or "WritePlcWebVars"
                        or "GetPlcWebDiagnostics" or "SetPlcWebOperatingMode",
                    "moduleAccessPassword" or "blockBindingPassword" or "masterSecretPassword" => tool == "DownloadPlc",
                    "umacPassword" => tool is "OpenProject" or "RetrieveProjectArchive",
                    "currentPassword" => tool == "ManageCfcChartProtection",
                    "newPassword" => tool is "ManagePlcBlockWriteProtection" or "ManagePlcProtection",
                    "serverPassword" => tool == "ManageUmcUsers",
                    _ => false
                };
                if (credential && field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.StartsWith("vault:", StringComparison.Ordinal))
                    return new Error("vault references were removed (ADR 0035); pass the credential directly",
                        new InvalidArgumentDetails(path + "/" + field.Name, Array.Empty<string>()));
            }
            return null;
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
            => ApprovalWriteCandidate(tool, args, classifiedWrite, hasDryRun, defaultPreview).HasValue;

        internal static WriteApprovalOperation? ApprovalWriteCandidate(string tool, JsonObject args, bool classifiedWrite,
            bool hasDryRun, bool defaultPreview)
        {
            // A present non-boolean value fails closed as a write. Only an absent value follows the schema default.
            if (tool == "ManageTestSuiteCase" && args["action"] is JsonValue editorAction
                && editorAction.TryGetValue<string>(out var editorOperation) && editorOperation == "showInEditor") return null;
            if (hasDryRun && IsPreview(args, defaultPreview)) return null;
            if (PendingApproval.HasCompilePassword(tool, args)) return WriteApprovalOperation.CompileWithSafetyPassword;
            if (tool == "CloseProject" && args["secondaryProjectFile"] is JsonValue secondary
                && secondary.TryGetValue<string>(out var secondaryFile) && !string.IsNullOrWhiteSpace(secondaryFile)) return null;
            if (tool is "ExchangeSystemDiagnosticsSettings" or "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules")
                if (args["action"] is JsonValue action && action.TryGetValue<string>(out var operation) && operation == "import")
                    return WriteApprovalOperation.OfflineWrite;
            if (tool == "ManagePlcGitRepository" && ActorScope.Actor == ActorScope.Mcp
                && args["action"] is JsonValue gitAction && gitAction.TryGetValue<string>(out var gitOperation)
                && gitOperation is "stage" or "commit") return WriteApprovalOperation.GitStageOrCommit;
            if (tool == "GetEnvironmentDiagnostics")
                return args["fix"] is JsonValue fix && fix.TryGetValue<bool>(out var repair) && repair ? WriteApprovalOperation.EnvironmentRepair : null;
            if (tool == "EnsureOpennessUserGroup") return WriteApprovalOperation.OpennessGroupChange;
            if (tool is "SaveProject" or "SaveProjectCopy" or "CloseProject") return WriteApprovalOperation.SaveOrCloseProject;
            if (classifiedWrite) return tool is "StageImportFiles" or "CleanupStagedImportFiles"
                ? WriteApprovalOperation.ImportStaging : WriteApprovalOperation.OfflineWrite;
            return CallerInputFiles.WritesOutsideOutputRoot(tool, args) ? WriteApprovalOperation.OutsideOutputFolder : null;
        }

        internal static string DownloadTargetKind(string? targetIpAddress, string? pgPcInterface = null)
            => TiaMcpServer.Siemens.DownloadTargetPolicy.OnlineTargetKind(targetIpAddress, pgPcInterface);

        internal static bool ApprovalRequired(bool switchEnabled, string tool, JsonObject arguments, WriteApprovalOperation? candidate = null)
            => switchEnabled || !IsPreview(arguments, true) && AppliedWriteOperation(tool, arguments, candidate)?.AlwaysConfirmed() == true;

        // Resolve the target and action only when deciding whether an applied write still needs approval.
        internal static WriteApprovalOperation? AppliedWriteOperation(string tool, JsonObject arguments, WriteApprovalOperation? candidate)
        {
            if (tool == "CloseProject" && arguments["discardChanges"] is JsonValue discard && discard.TryGetValue<bool>(out var requested) && requested)
                return WriteApprovalOperation.DiscardOnClose;
            if (TiaMcpServer.ModelContextProtocol.ToolTaxonomy.OperationOf(tool, null).Operation != "ONLINE-WRITE")
                return candidate;
            if (tool == "ManagePlcDataBlockSnapshot")
                return arguments["action"] is JsonValue action && action.TryGetValue<string>(out var operation)
                    && operation is "read" or "exportSnapshot" or "createSnapshot"
                    ? WriteApprovalOperation.SnapshotReadExportOrCapture : WriteApprovalOperation.OnlineWrite;
            if (tool == "InvokeUnifiedOpenPipe")
            {
                if (arguments["request"] is not JsonObject request) return WriteApprovalOperation.OnlineWrite;
                var bound = Domain.DomainValidation.Contract<Domain.OpenPipeRequest>().Read(request.ToJsonString(), "request");
                return bound.Error == null && bound.Value!.IsReadOnly ? WriteApprovalOperation.OpenPipeRead : WriteApprovalOperation.OnlineWrite;
            }
            if (tool is "ManagePlcSimAdvancedInstance" or "WritePlcSimAdvancedTags" or "RunPlcSimAdvancedTestScenario"
                || tool == "DownloadPlc" && DownloadTargetKind((string?)arguments["targetIpAddress"], (string?)arguments["pgPcInterface"]) == "plcsim")
                return WriteApprovalOperation.SimulatorWrite;
            return WriteApprovalOperation.OnlineWrite;
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
                || data["plan"] is JsonObject plan && plan["operations"] is JsonArray { Count: 0 }
                || data["inventoryComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var inventory) && inventory && data["items"] is JsonArray {
                    Count: 0 };
        }

        internal static void ExportPreview(JsonObject data)
        {
            bool documents = data["options"] is JsonValue options && options.TryGetValue<string>(out var option)
                && option == "native-default-two-argument-overload";
            bool export = documents || data["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name is "watch-table" or "technology-object";
            if (!export || data["applyBlocked"] != null) return;
            if (data["planHash"] == null || data["status"] is not JsonValue status || !status.TryGetValue<string>(out var text)) return;
            if (documents && text is "planned" or "inconsistent") data["summary"] ??= "Planned files include .s7dcl and .s7res when produced.";
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

        private static bool IsMaskedPlanArgument(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<string>(out var text) && text == ApprovalJournalPayload.Hidden
                && value.Parent is JsonObject plan && plan.Parent is JsonObject data && ReferenceEquals(data["plannedChanges"], plan);

        // Evidence historically omits the exact password field regardless of its type.
        internal static bool IsEvidenceCredential(KeyValuePair<string, JsonNode?> member)
            => member.Key.Equals("password", StringComparison.OrdinalIgnoreCase) ? !IsMaskedPlanArgument(member.Value) : IsResponseCredential(member);

        internal static bool IsResponseCredential(KeyValuePair<string, JsonNode?> member)
        {
            if (!ApprovalJournalPayload.IsResponseCredentialName(member.Key)) return false;
            if (member.Value is JsonValue value && value.TryGetValue<string>(out _)) return !IsMaskedPlanArgument(value);
            if (member.Value is not JsonArray array || !array.All(item => item is JsonValue scalar && scalar.TryGetValue<string>(out _))) return false;
            // The PLC provider returns its forbidden character alphabet as individual strings.
            return member.Key != "invalidPasswordCharacters" || array.Any(item => item!.GetValue<string>().Length != 1);
        }

        internal static void RemoveResponseCredentials(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (IsResponseCredential(pair)) obj.Remove(pair.Key);
                    else RemoveResponseCredentials(pair.Value);
                }
            else if (node is JsonArray array)
                foreach (var item in array) RemoveResponseCredentials(item);
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
                        || IsEvidenceCredential(pair))
                        obj.Remove(pair.Key);
                    else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        && (pair.Key.Equals("nativeMessage", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("workerMessage", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("message", StringComparison.OrdinalIgnoreCase)
                            || (evidence || diagnostic) && pair.Key.Equals("detailText", StringComparison.OrdinalIgnoreCase)
                            || diagnostic && pair.Key.Equals("text", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("error", StringComparison.OrdinalIgnoreCase)
                            || !sourceText && (text.Contains("\n   at ") || text.Contains(" ---> "))))
                        obj[pair.Key] = SafeDiagnostic(text);
                    else SanitizeEvidence(pair.Value, evidence || pair.Key.Equals("evidence", StringComparison.OrdinalIgnoreCase),
                        diagnostic || pair.Key.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("messageData", StringComparison.OrdinalIgnoreCase)
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
                if (tool == "ExportDeviceAml" && reply["exportPhase"]?.ToString() == "native-export"
                    && reply["outputFile"] is JsonValue output && output.TryGetValue<string>(out var filePath))
                {
                    retained["filePath"] = filePath;
                    retained["mayHaveWrittenFiles"] = true;
                }
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
            if (source["phase"]?.ToString() == TiaMcp.Adapters.Contracts.SafetyLoginFailure.Phase && source.ContainsKey("loggedOnAfterFailure"))
            {
                foreach (string field in new[] { "nativeExceptionType", "loggedOnAfterFailure", "logoffFailed", "compileStarted" })
                    if (source.ContainsKey(field)) retained[field] = source[field]?.DeepClone();
            }
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
                "connectionUnavailable", "portalLost", "restored", "exportsIssued", "queryRefusals", "mapped", "mappedAfterFailure",
                "synchronized", "unconfirmed", "failed", "status", "importReturned", "logFileMissing" })
                if (source[key] is JsonValue scalar)
                {
                    if (scalar.TryGetValue<string>(out var raw)) retained[key] = Diagnostic(raw);
                    else if (scalar.TryGetValue<bool>(out var flag)) retained[key] = flag;
                    else if (scalar.TryGetValue<long>(out var count)) retained[key] = count;
                    else if (scalar.TryGetValue<int>(out var number)) retained[key] = number;
                }
            if (source["exportsIssued"] != null)
            {
                foreach (var key in new[] { "filesWritten", "writtenFilesAfterFailureTotal", "skippedDisconnected", "verifiedEqual",
                    "notVerified", "unverifiedEqual", "notAttempted" })
                    if (source[key] is JsonValue value && value.TryGetValue<int>(out var count)) retained[key] = count;
                if (source["dataComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var flag)) retained["dataComplete"] = flag;
                if (source["notAttemptedNames"] is JsonArray pending)
                    retained["notAttemptedNames"] = JsonSerializer.SerializeToNode(TiaMcp.Adapters.Contracts.NativeFailureText.BoundedLines(
                        pending.OfType<JsonValue>().Select(name => name.ToString()), Diagnostic));
                if (source["writtenFilesAfterFailure"] is JsonArray files)
                {
                    var rows = new JsonArray();
                    foreach (var file in files.OfType<JsonObject>().Take(40))
                        if (file["mappingState"] is JsonValue state && state.TryGetValue<string>(out var mapping)
                            && mapping is "present" or "absent" or "unknown" && file["fileName"] is JsonValue name && name.TryGetValue<string>(out var text))
                            rows.Add(new JsonObject { ["fileName"] = Diagnostic(text), ["mappingState"] = mapping });
                    retained["writtenFilesAfterFailure"] = rows;
                }
                if (source["statusProbe"] is JsonObject probe && probe["objects"] is JsonArray probes)
                {
                    var rows = new JsonArray();
                    foreach (var row in probes.OfType<JsonObject>())
                        if (row["name"] is JsonValue name && name.TryGetValue<string>(out var text)
                            && row["nativeMessage"] is JsonValue message && message.TryGetValue<string>(out var reason))
                            rows.Add(new JsonObject { ["name"] = Diagnostic(text), ["nativeMessage"] = Diagnostic(reason) });
                    retained["statusProbe"] = new JsonObject { ["objects"] = rows, ["nativeMessage"] = rows.FirstOrDefault()?["nativeMessage"]?.DeepClone() };
                }
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

        internal const string PartialExportHint = "Check data.filePath and remove any partial file before retrying.";

        internal static Error UnchangedNativeError(JsonObject evidence, string? fallback = null, bool fileExport = false)
        {
            string? message = SafeDiagnostic(evidence["nativeMessage"]?.ToString() ?? fallback);
            var facts = new Dictionary<string, JsonElement>();
            if (fileExport && evidence["filePath"] is JsonValue path)
            {
                facts["filePath"] = JsonSerializer.SerializeToElement(path);
                facts["mayHaveWrittenFiles"] = JsonSerializer.SerializeToElement((bool?)evidence["mayHaveWrittenFiles"] == true);
            }
            return new Error(message ?? "The native operation failed without changing the target.", new NativeOperationFailedDetails(null, message, facts));
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
                    UnsupportedCapabilityDetails.ErrorFor(release, tool, (string?)evidence["action"], message, (string?)evidence["unsupportedBy"]),
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
                ? UnsupportedCapabilityDetails.ErrorFor(release, tool, capability.Action, nativeMessage, (string?)evidence["unsupportedBy"])
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
