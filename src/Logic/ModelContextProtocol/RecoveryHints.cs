using System;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.ModelContextProtocol
{
    // Guidance uses embedded examples only; caller arguments never enter this projection.
    public static class RecoveryHints
    {
        public const int ExampleByteLimit = 4096;
        public static string RecoveryCode(string code)
        {
            switch (code)
            {
                case "NOT_FOUND": return Tip("verify the exact target names with GetProjectTree / GetSoftwareTree / ListPlcBlocks before another call.");
                case "INVALID_ARGUMENT": return Tip("read GetToolUsage and the tool input schema, then correct the argument without issuing the operation again automatically.");
                case "UNSUPPORTED_CAPABILITY": return Tip("check this release's capabilities with GetToolUsage; do not substitute an unverified operation.");
                case "ACCESS_DENIED": return Tip("check the required permissions; do not bypass the access restriction.");
                case "OFFLINE_REQUIRED": return Tip("the affected target must be offline; verify its state and obtain explicit authorization before changing the online connection.");
                case "OUTCOME_UNKNOWN": return Tip("verify the actual target state and reset the session before continuing; do not replay the write automatically.");
                case "PRECONDITION_FAILED": return Tip("read GetToolUsage, verify the target and the stated preconditions, then review a new preview before another call.");
                case "SESSION_RESET_REQUIRED": return Tip("reset the session and verify the actual target state before continuing; do not replay the write automatically.");
                case "CONFIRMATION_REQUIRED": return Tip("read GetToolUsage, review the preview and obtain Workbench confirmation before a new apply request.");
                default: return "";
            }
        }

        public static bool Attach(JsonNode? body, Func<string, string, string, JsonObject?> example)
        {
            if (body is not JsonObject || body["ok"]?.GetValue<bool>() != false
                || body["meta"] is not JsonObject meta || meta["warnings"] is not JsonArray warnings) return false;
            string code = (string?)body["error"]?["code"] ?? "";
            if (code is not ("INVALID_ARGUMENT" or "PRECONDITION_FAILED" or "SESSION_RESET_REQUIRED" or "CONFIRMATION_REQUIRED")) return false;
            foreach (var warning in warnings) if ((string?)warning?["code"] == "RECOVERY_GUIDANCE") return false;
            string tool = (string?)meta["tool"] ?? "";
            string release = (string?)meta["releaseKey"] ?? "";
            var query = new JsonObject { ["toolName"] = tool };
            var verified = example(tool, release, "");
            var arguments = verified?["request"]?["params"]?["arguments"] as JsonObject;
            string? selector = arguments?.ContainsKey("action") == true ? "action"
                : arguments?.ContainsKey("operation") == true ? "operation" : null;
            if (selector != null && arguments![selector] is JsonValue operation && operation.TryGetValue<string>(out var text) && text.Length > 0)
            {
                query["operation"] = text;
                verified = example(tool, release, text);
                arguments = verified?["request"]?["params"]?["arguments"] as JsonObject;
            }
            var compact = arguments?.DeepClone().AsObject();
            bool bounded = compact != null && Encoding.UTF8.GetByteCount(compact.ToJsonString()) <= ExampleByteLimit;
            var details = new JsonObject { ["getToolUsage"] = query, ["releaseKey"] = release,
                ["exampleArguments"] = bounded ? compact : null, ["exampleOmitted"] = !bounded,
                ["validation"] = verified?["validation"]?.DeepClone(),
                ["bindingMeaning"] = "Example values are placeholders and sample targets. Resolve and review them against this session; this hint does not authorize a write." };
            warnings.Add(new JsonObject { ["code"] = "RECOVERY_GUIDANCE", ["message"] = RecoveryCode(code).Trim(), ["details"] = details });
            return true;
        }
        private static string Tip(string text) => "  ▶ RECOVERY: " + text;
    }
}
