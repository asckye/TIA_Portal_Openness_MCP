using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    // A returned failure state proves native completion, not rollback of a project write.
    public static class NativeResultState
    {
        public static void Record(JsonObject evidence, string? state, bool changesProject, string? logFilePath = null,
            string? targetPath = null, JsonNode? messages = null)
        {
            evidence["nativeResultReturned"] = true;
            evidence["nativeState"] = state;
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

        public static bool TryFailure(JsonObject evidence, bool writes, out Outcome outcome, out Error? error)
        {
            outcome = writes ? Outcome.Failed : Outcome.ReadFailed;
            error = null;
            if (evidence["nativeResultReturned"] is not JsonValue returned || !returned.TryGetValue<bool>(out var completed) || !completed)
                return false;
            string? state = evidence["nativeState"]?.ToString();
            if (state != "Error" && state != "Failure" && state != "Failed") return false;
            bool changesProject = evidence["mayHaveChanged"] is JsonValue changed && changed.TryGetValue<bool>(out var mayChange) && mayChange;
            if (changesProject) outcome = Outcome.Unknown;
            var keys = new[] { "nativeState", "nativeResultReturned", "mayHaveChanged", "logFilePath", "nativeMessages", "targetFile", "targetFiles" };
            var details = keys.Where(evidence.ContainsKey).ToDictionary(k => k, k => JsonSerializer.SerializeToElement(evidence[k]), StringComparer.Ordinal);
            error = changesProject
                ? new Error("The native operation returned " + state + "; partial project changes are possible. Inspect the evidence and reset the session.", new OutcomeUnknownDetails("native-result-state", details))
                : new Error("The native operation returned " + state + ". The project was unchanged; inspect the file and log evidence.", new NativeOperationFailedDetails(state, null, details));
            return true;
        }
    }
}
