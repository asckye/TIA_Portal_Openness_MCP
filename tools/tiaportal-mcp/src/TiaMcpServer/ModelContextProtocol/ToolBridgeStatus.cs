using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class ToolBridgeStatus
    {
        internal static JsonObject Create(bool bridgeSuccess, JsonObject? operationMeta = null)
        {
            bool? operationSuccess = null;
            if (bridgeSuccess && operationMeta?["success"] is JsonValue value && value.TryGetValue<bool>(out var success))
                operationSuccess = success;
            return new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["bridgeSuccess"] = bridgeSuccess,
                ["operationSuccess"] = operationSuccess,
                // Unknown is not proof of business success.
                ["success"] = bridgeSuccess ? operationSuccess : false,
                ["operationStatus"] = !bridgeSuccess ? "notCompleted" : operationSuccess == true ? "succeeded" : operationSuccess == false ? "failed" : "unknown"
            };
        }
    }
}
