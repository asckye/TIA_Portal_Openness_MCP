using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class ToolBridgeStatus
    {
        internal static JsonObject Create(bool bridgeSuccess, JsonObject? operationMeta = null)
        {
            // Unknown is not proof of business success.
            return ResponseMeta.Bridge(bridgeSuccess, operationMeta);
        }
    }
}
