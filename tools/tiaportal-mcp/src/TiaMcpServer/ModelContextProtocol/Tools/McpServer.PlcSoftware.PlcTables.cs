using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // CLI compatibility entries; only PlcTablesTools carries MCP tool attributes.

        private static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                throw new McpException(parameterName + " must be a JSON object. Parse error: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        internal static class PlcTableToolSupport
        {
            internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy() => McpServer.GetOnlineMonitoringSafetyPolicy();
            internal static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName) => McpServer.ParseJsonObjectOrEmpty(json, parameterName);
        }
    }
}
