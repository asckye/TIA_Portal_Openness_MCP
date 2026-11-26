using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        internal static CallToolResult ExportFailureResult(string tool, CallToolResult result, CallerInputFiles.ExportObservation? export)
        {
            if (export == null || result.StructuredContent is not JsonObject original
                || (string?)original["meta"]?["outcome"] is not ("unknown" or "failed")) return result;
            if ((bool?)original["error"]?["details"]?["evidence"]?["nativeResultReturned"] == true) return result;
            string? nativeMessage = export.NativeMessage;
            bool missingTextList = tool == "ExportAlarmTextLists" && (string?)original["error"]?["details"]?["condition"] == "plc-text-lists";
            if (missingTextList)
            {
                // The worker can return a diagnosed refusal after writing part of the target.
                if (export.NoFileWritten && (bool?)original["data"]?["evidence"]?["mayHaveWrittenFiles"] != true) return result;
                nativeMessage ??= (string?)original["data"]?["evidence"]?["nativeMessage"] ?? (string?)original["error"]?["message"];
            }
            else if (nativeMessage == null || !InvocationJournal.NativeCallIssued) return result;
            var data = original["data"]?.DeepClone() as JsonObject ?? new JsonObject();
            data["nativeMessage"] = nativeMessage;
            if (missingTextList || !export.NoFileWritten)
            {
                data["mayHaveChanged"] = true;
                return V4Result(tool, data, new Error("The export failed and its target may have changed. " + nativeMessage,
                    new OutcomeUnknownDetails("export-file", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown,
                        current: true);
            }
            data["mayHaveChanged"] = false; data["mayHaveWrittenFiles"] = false;
            if (data["evidence"] is JsonObject evidence) { evidence["mayHaveChanged"] = false; evidence["mayHaveWrittenFiles"] = false; }
            return V4Result(tool, data, new Error("The native export failed without writing its target. " + nativeMessage,
                new NativeOperationFailedDetails(null, nativeMessage, new Dictionary<string, JsonElement>())), Outcome.Failed, Execution.Completed,
                    Completeness.Complete, current: true);
        }

    }
}
