using System.Text.Json.Nodes;

namespace TiaOpenness.Shared
{
    internal static class ApprovalResult
    {
        internal static JsonNode Decorate(JsonNode body, bool disabled, string? requestId = null)
        {
            var copy = body.DeepClone();
            if (copy["schemaVersion"]?.GetValue<int?>() != 4 || copy["meta"] is not JsonObject meta) return copy;
            if (requestId != null) meta["requestId"] = requestId;
            if (disabled && meta["warnings"] is JsonArray warnings)
            {
                foreach (var warning in warnings) if ((string?)warning?["code"] == "APPROVAL_DISABLED") return copy;
                warnings.Add(new JsonObject { ["code"] = "APPROVAL_DISABLED", ["message"] = "Workbench approval is disabled for this MCP write call.",
                    ["details"] = new JsonObject { ["enabled"] = false } });
            }
            return copy;
        }
    }
}
