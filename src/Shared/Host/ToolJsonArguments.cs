using System;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class ToolJsonArguments
    {
        internal static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName)
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
    }
}
