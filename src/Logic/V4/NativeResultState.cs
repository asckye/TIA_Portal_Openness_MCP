using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Logic.V4
{
    // A returned failure state proves native completion, not rollback of a project write.
    public static class NativeResultState
    {
        public static void Record(JsonObject evidence, object? state, bool changesProject, string? logFilePath = null,
            string? targetPath = null, JsonNode? messages = null)
        {
            evidence["nativeResultReturned"] = true;
            evidence["nativeState"] = state?.ToString();
            if (state is Enum) evidence["nativeStateType"] = state.GetType().FullName;
            evidence["mayHaveChanged"] = changesProject;
            evidence["logFilePath"] = logFilePath;
            evidence["nativeMessages"] = messages?.DeepClone();
            if (targetPath != null) evidence["targetFile"] = FileRow(targetPath);
        }

        public static JsonObject FileRow(string path)
        {
            var file = new FileInfo(path);
            return new JsonObject { ["path"] = file.FullName, ["exists"] = file.Exists, ["sizeBytes"] = file.Exists ? file.Length : (long?)null };
        }

        public static string? EnumType(JsonObject evidence, string? tool = null)
        {
            if (evidence["nativeStateType"] != null) return evidence["nativeStateType"]!.ToString();
            switch (tool)
            {
                case "ExportAlarmInstanceTexts": case "ImportPlcAlarmInstanceTexts": return NativeResultStates.AlarmTexts;
                case "ExportAlarmTextLists": case "ImportAlarmTextLists": case "ExchangePlcAlarmTextLists": return NativeResultStates.TextLists;
                case "ExportAlarmClasses": case "ImportAlarmClasses": return NativeResultStates.AlarmClasses;
                case "CompilePlcSoftware": case "CompilePlcDiagnostics": case "CompileDevice": case "CompileHmiDiagnostics": return NativeResultStates.Compiler;
                case "DownloadPlc": case "DownloadPlcToFolder": return NativeResultStates.Download;
                case "UploadStation": return NativeResultStates.Upload;
                case "ExportDeviceAml": return NativeResultStates.Cax;
                case "RunTestSuiteCase": return NativeResultStates.TestSuite;
                case "ExchangePlcSupervisions": return NativeResultStates.SupervisionXlsx;
                case "ManagePlcSupervisionProvider": return NativeResultStates.SupervisionSettings;
                case "ExchangeSystemDiagnosticsSettings": return NativeResultStates.SystemDiagnostics;
                case "ExchangeSivarcScreenLayouts": return NativeResultStates.Layout;
                case "ExportProjectTexts": case "ImportProjectTexts": return NativeResultStates.ProjectTexts;
                case "ExportPlcBlockDocuments": case "ImportPlcBlockDocuments": case "ManagePlcBlockDocuments":
                case "ExchangeSoftwareUnitDocuments": case "AuditPlcBlockDocumentRoundTrip": return NativeResultStates.Documents;
                default: return null;
            }
        }

        public static string? State(JsonObject evidence)
            =>
                (evidence["nativeState"] ?? evidence["state"] ?? evidence["effectiveState"] ?? evidence["downloadState"] ?? evidence["uploadState"]
                    ?? evidence["transferResultState"] ?? evidence["result"]?["state"])
                ?.ToString();

        public static NativeStateKind Classify(JsonObject evidence, string? tool = null)
            => NativeResultStates.Classify(EnumType(evidence, tool), State(evidence));

        public static bool Succeeded(JsonObject evidence, string? tool = null)
            => Classify(evidence, tool) is NativeStateKind.Success or NativeStateKind.Warning;

        public static IEnumerable<Warning> Warnings(JsonObject evidence, string? tool = null)
        {
            if (Classify(evidence, tool) == NativeStateKind.Warning)
                yield return new Warning(WarningCode.NativeWarning, "The native operation returned a warning state.",
                    new Dictionary<string, JsonElement> { ["nativeState"] = JsonSerializer.SerializeToElement(State(evidence)),
                        ["nativeStateType"] = JsonSerializer.SerializeToElement(EnumType(evidence, tool)) });
            foreach (var pair in evidence)
            {
                if (pair.Value is JsonObject child)
                    foreach (var warning in Warnings(child, tool)) yield return warning;
                else if (pair.Value is JsonArray children)
                    foreach (var item in children.OfType<JsonObject>())
                        foreach (var warning in Warnings(item, tool)) yield return warning;
            }
        }

        public static JsonObject? FindUnsuccessful(JsonNode? node, string? tool = null)
        {
            if (node is JsonObject obj)
            {
                if (obj["rollbackCompleted"]?.ToString() == "true" || obj["cleanupRequired"] is JsonArray cleanup && cleanup.Count > 0
                    || TryUnsuccessful(obj, false, out _, out _, tool)) return obj;
                foreach (var pair in obj) { var found = FindUnsuccessful(pair.Value, tool); if (found != null) return found; }
            }
            else if (node is JsonArray array)
                foreach (var item in array) { var found = FindUnsuccessful(item, tool); if (found != null) return found; }
            return null;
        }

        public static bool TryUnsuccessful(JsonObject evidence, bool writes, out Outcome outcome, out Error? error, string? tool = null)
        {
            if (TryRollback(evidence, writes, out outcome, out error, tool)) return true;
            if (TryFailure(evidence, writes, out outcome, out error, tool)) return true;
            bool returned = evidence["nativeResultReturned"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
            if (!returned || Succeeded(evidence, tool)) return false;
            var details = Evidence(evidence, tool);
            // PartialSuccess and None cannot establish that all requested work completed.
            outcome = Outcome.Unknown;
            error = new Error("The native result state does not establish a complete outcome. Inspect the target and retained raw state; "
                + "the call stays on GetSessionState.unsettled. Do not replay it.",
                new OutcomeUnknownDetails("native-result-state", details));
            return true;
        }

        public static bool TryRollback(JsonObject evidence, bool writes, out Outcome outcome, out Error? error, string? tool = null)
        {
            outcome = Outcome.Unknown;
            error = null;
            bool cleanup = evidence["cleanupRequired"] is JsonArray tasks && tasks.Count > 0;
            if (!writes) return false;
            bool Flag(string name) => evidence[name] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
            if (!Flag("writeOutcomeUnknown") && !Flag("unrestoredWrite") && !Flag("connectionUnavailable") && !Flag("portalLost")
                && !evidence.ContainsKey("rollbackOutcome") && (bool?)evidence["operationSuccess"] == false)
            {
                if (tool == "ImportDeviceAml" && (string?)evidence["status"] == "ImportFailed" && Flag("apiCallSuccess") && Flag("mayHaveChanged")
                    && evidence["nativeResult"] is JsonValue verdict && (verdict.TryGetValue<bool>(out var ok) && !ok
                        || verdict.TryGetValue<string>(out var state) && state == "Error"))
                {
                    outcome = Outcome.Failed;
                    error = new Error("CAx import returned an unsuccessful result. Inspect devices per log for partial changes.",
                        new NativeOperationFailedDetails(null, null, HostBehavior.WorkerReplyEvidence(evidence)));
                    return true;
                }
                if (tool == "GenerateOpcUaModelledInterface" && evidence["outputExists"] is JsonValue exists
                    && exists.TryGetValue<bool>(out var present) && (!present || cleanup))
                {
                    outcome = Outcome.Failed;
                    error = new Error(HostBehavior.SafeDiagnostic(evidence["nativeMessage"]?.ToString()) ?? "Model generation failed.",
                        new NativeOperationFailedDetails(null, null, Evidence(evidence, tool)));
                    return true;
                }
            }
            if (tool == "ImportPlcBlockVerified" && (bool?)evidence["compileRequested"] == false && (string?)evidence["phase"] == "exportReadback"
                && Flag("writeReturned") && Flag("importReturned") && Flag("readbackFailed")
                && Flag("operationSuccess") && !Flag("writeOutcomeUnknown") && !Flag("unrestoredWrite") && !Flag("rollbackCompleted")
                && !evidence.ContainsKey("rollbackOutcome") && !evidence.ContainsKey("connectionUnavailable") && !evidence.ContainsKey("portalLost"))
                return false; // The backup remains available for manual cleanup after a completed uncompiled import.
            if (tool == "ImportPlcBlockVerified" && (string?)evidence["status"] == "DocumentReadbackMismatch" && Flag("importReturned")
                && !Flag("writeOutcomeUnknown") && !Flag("connectionUnavailable") && !Flag("portalLost") && !Flag("unrestoredWrite")
                && !evidence.ContainsKey("rollbackOutcome"))
            {
                outcome = Outcome.Failed;
                error = new Error("The import returned, but the re-exported document differs from the plan. "
                    + "Compare the planned and after documents; the backup is retained.",
                    new NativeOperationFailedDetails(null, null, Evidence(evidence, tool)));
                return true;
            }
            if (Flag("restoreAttempted"))
            {
                bool groupsUnchanged = !evidence.ContainsKey("targetGroupsCreated")
                    || evidence["targetGroupsCreated"] is JsonArray groups && groups.Count == 0;
                if (!Flag("restored") || !groupsUnchanged || cleanup
                    || evidence.ContainsKey("rollbackCompleted") && !Flag("rollbackCompleted")
                    || Flag("writeOutcomeUnknown") || Flag("connectionUnavailable") || Flag("unrestoredWrite")) return false;
                outcome = Outcome.Failed;
                error = new Error("The move failed; the block was re-imported into its original group and its name, number and language verified. "
                    + "Compile before use.", new NativeOperationFailedDetails(null, null, Evidence(evidence, tool)));
                return true;
            }
            if (!evidence.ContainsKey("rollbackCompleted") && !cleanup) return false;
            string result = WriteRollbackPolicy.Outcome(Flag("rollbackCompleted"), Flag("connectionUnavailable"),
                Flag("writeOutcomeUnknown"), Flag("unrestoredWrite"), Flag("rollbackRefused"), Flag("rollbackReachable"));
            var details = Evidence(evidence, tool);
            if (tool is "ManageCommunicationConnection" or "ManageDeviceUsers" or "ManageTransferArea" or "ManageSyslogServers" or "ManageLibraryType"
                or "ExchangeUnifiedTags" or "ManageSivarcRuleContainer")
                foreach (var pair in HostBehavior.WorkerReplyEvidence(evidence)) details[pair.Key] = pair.Value;
            if (result == "restored")
            {
                outcome = Outcome.Failed;
                string message = (string?)evidence["status"] == "ImportDidNotCreateModule"
                    ? "Scripts.Import returned true, but the requested script module was not created. "
                        + "Inspect the retained candidate JS/YAML pair in candidateDirectoryRelativeToTemp under the MCP temp directory "
                        + "and the native invocation log before retrying."
                    : "The write failed; captured writes were restored and verified.";
                string? nativeMessage = details.TryGetValue("nativeMessage", out var native) && native.ValueKind == JsonValueKind.String
                    ? native.GetString() : null;
                error = new Error(nativeMessage ?? message, new NativeOperationFailedDetails(null, nativeMessage, details));
            }
            else if (result == "partial")
            {
                outcome = Outcome.Partial;
                error = new Error("The reachable target refused compensation; perform the listed cleanup.", new PartialFailureDetails(0, 0, 0));
            }
            else error = new Error("Restoration could not be established. Inspect the target and retained evidence; "
                + "the call stays on GetSessionState.unsettled. Do not replay it.",
                new OutcomeUnknownDetails("rollback", details));
            return true;
        }

        private static Dictionary<string, JsonElement> Evidence(JsonObject evidence, string? tool = null)
        {
            var keys = new[] {
                "nativeState", "nativeStateType", "nativeResultReturned", "mayHaveChanged", "logFilePath", "nativeMessages", "targetFile", "targetFiles",
                    "recoveryDirectory", "candidateDirectory", "candidateDirectoryRelativeToTemp", "scriptCountAfterImport", "moduleExistsAfterImport",
                    "rollbackCompleted", "rollbackScope", "restoredChanges", "cleanupRequired",
                    "rollbackRefused", "rollbackReachable", "unrestoredWrite", "connectionUnavailable", "writeOutcomeUnknown",
                    "status", "importReturned", "outputExists", "phase"
            };
            var details = keys.Where(evidence.ContainsKey).ToDictionary(k => k, k => JsonSerializer.SerializeToElement(evidence[k]), StringComparer.Ordinal);
            if (!details.ContainsKey("nativeStateType") && EnumType(evidence, tool) is string type)
                details["nativeStateType"] = JsonSerializer.SerializeToElement(type);
            return details;
        }

        public static JsonObject? FindFailure(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (TryFailure(obj, false, out _, out _)) return obj;
                foreach (var pair in obj) { var found = FindFailure(pair.Value); if (found != null) return found; }
            }
            else if (node is JsonArray array)
                foreach (var item in array) { var found = FindFailure(item); if (found != null) return found; }
            return null;
        }

        public static bool TryFailure(JsonObject evidence, bool writes, out Outcome outcome, out Error? error, string? tool = null)
        {
            outcome = writes ? Outcome.Failed : Outcome.ReadFailed;
            error = null;
            if (evidence["nativeResultReturned"] is not JsonValue returned || !returned.TryGetValue<bool>(out var completed) || !completed)
                return false;
            string? state = evidence["nativeState"]?.ToString();
            string? type = EnumType(evidence, tool);
            bool? testVerdict = tool == "RunTestSuiteCase" && type == NativeResultStates.TestSuite
                ? NativeResultStates.TestSuiteVerdict(completed, state) : null;
            if (testVerdict != false && (type != null
                ? NativeResultStates.Classify(type, state) != NativeStateKind.Failure
                : state != "Error" && state != "Failure" && state != "Failed" && state != "ErrorRollback")) return false;
            bool changesProject = !testVerdict.HasValue && evidence["mayHaveChanged"] is JsonValue changed
                && changed.TryGetValue<bool>(out var mayChange) && mayChange;
            if (changesProject) outcome = Outcome.Unknown;
            var details = Evidence(evidence, tool);
            error = changesProject
                ? new Error("The native operation returned " + state + "; partial project changes are possible. Inspect the target and evidence; "
                        + "the call stays on GetSessionState.unsettled. Do not replay it.",
                        new OutcomeUnknownDetails("native-result-state", details))
                : new Error(testVerdict == false ? "The native test completed with a failed verdict (" + state + ")."
                        : "The native operation returned " + state + ". The project was unchanged; inspect the file and log evidence.",
                        new NativeOperationFailedDetails(state, null, details));
            return true;
        }
    }
}
