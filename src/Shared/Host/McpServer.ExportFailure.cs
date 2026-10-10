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
            if (export?.NativeMessage == null || !InvocationJournal.NativeCallIssued || result.StructuredContent is not JsonObject original
                || (string?)original["meta"]?["outcome"] is not ("unknown" or "failed")) return result;
            if ((bool?)original["error"]?["details"]?["evidence"]?["nativeResultReturned"] == true) return result;
            var data = original["data"]?.DeepClone() as JsonObject ?? new JsonObject();
            data["nativeMessage"] = export.NativeMessage;
            if (!export.NoFileWritten)
            {
                data["mayHaveChanged"] = true;
                return V4Result(tool, data, new Error("The export failed and its target may have changed. " + export.NativeMessage,
                    new OutcomeUnknownDetails("export-file", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true);
            }
            data["mayHaveChanged"] = false; data["mayHaveWrittenFiles"] = false;
            if (data["evidence"] is JsonObject evidence) { evidence["mayHaveChanged"] = false; evidence["mayHaveWrittenFiles"] = false; }
            return V4Result(tool, data, new Error("The native export failed without writing its target. " + export.NativeMessage,
                new NativeOperationFailedDetails(null, export.NativeMessage, new Dictionary<string, JsonElement>())), Outcome.Failed, Execution.Completed, Completeness.Complete, current: true);
        }

    }
}
