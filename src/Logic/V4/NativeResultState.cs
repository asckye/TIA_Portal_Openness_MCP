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
            => (evidence["nativeState"] ?? evidence["state"] ?? evidence["effectiveState"] ?? evidence["downloadState"] ?? evidence["uploadState"] ?? evidence["transferResultState"] ?? evidence["result"]?["state"])?.ToString();

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
                if (TryUnsuccessful(obj, false, out _, out _, tool)) return obj;
                foreach (var pair in obj) { var found = FindUnsuccessful(pair.Value, tool); if (found != null) return found; }
            }
            else if (node is JsonArray array)
                foreach (var item in array) { var found = FindUnsuccessful(item, tool); if (found != null) return found; }
            return null;
        }

        public static bool TryUnsuccessful(JsonObject evidence, bool writes, out Outcome outcome, out Error? error, string? tool = null)
        {
            if (TryFailure(evidence, writes, out outcome, out error, tool)) return true;
            bool returned = evidence["nativeResultReturned"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
            if (!returned || Succeeded(evidence, tool)) return false;
            var details = Evidence(evidence, tool);
            // PartialSuccess and None cannot establish that all requested work completed.
            outcome = Outcome.Unknown;
            error = new Error("The native result state does not establish a complete outcome. Inspect the retained raw state and reset the session.",
                new OutcomeUnknownDetails("native-result-state", details));
            return true;
        }

        private static Dictionary<string, JsonElement> Evidence(JsonObject evidence, string? tool = null)
        {
            var keys = new[] { "nativeState", "nativeStateType", "nativeResultReturned", "mayHaveChanged", "logFilePath", "nativeMessages", "targetFile", "targetFiles", "recoveryDirectory" };
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
            if (type != null
                ? NativeResultStates.Classify(type, state) != NativeStateKind.Failure
                : state != "Error" && state != "Failure" && state != "Failed" && state != "ErrorRollback") return false;
            bool changesProject = evidence["mayHaveChanged"] is JsonValue changed && changed.TryGetValue<bool>(out var mayChange) && mayChange;
            if (changesProject) outcome = Outcome.Unknown;
            var details = Evidence(evidence, tool);
            error = changesProject
                ? new Error("The native operation returned " + state + "; partial project changes are possible. Inspect the evidence and reset the session.", new OutcomeUnknownDetails("native-result-state", details))
                : new Error("The native operation returned " + state + ". The project was unchanged; inspect the file and log evidence.", new NativeOperationFailedDetails(state, null, details));
            return true;
        }
    }
}
