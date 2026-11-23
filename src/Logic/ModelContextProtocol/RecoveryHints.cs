using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.ModelContextProtocol
{
    // Guidance uses embedded examples only; caller arguments never enter this projection.
    public static class RecoveryHints
    {
        public const int ExampleByteLimit = 4096;
        public static string RecoveryCode(string code, Func<string, bool>? registered = null)
        {
            bool Has(string name) => registered?.Invoke(name) ?? true;
            string reference = Has("GetToolUsage") ? "GetToolUsage" : "the tool input schema";
            string usage = Has("GetToolUsage") ? "GetToolUsage and the tool input schema" : "the tool input schema";
            switch (code)
            {
                case "NOT_FOUND":
                    var readers = new[] { "GetProjectTree", "GetSoftwareTree", "ListPlcBlocks" }.Where(Has).ToArray();
                    string lookup = readers.Length == 0 ? "" : " with " + string.Join(" / ", readers);
                    return Tip("verify the exact target names" + lookup + " before another call.");
                case "INVALID_ARGUMENT"
                    : return Tip("read " + usage + ", then correct the argument without issuing the operation again automatically.");
                case "UNSUPPORTED_CAPABILITY":
                    string lookupHint = Has("GetToolUsage") ? "with GetToolUsage" : "in the release documentation";
                    return Tip("check this release's capabilities " + lookupHint + "; do not substitute an unverified operation.");
                case "ACCESS_DENIED": return Tip("check the required permissions; do not bypass the access restriction.");
                case "OFFLINE_REQUIRED"
                    : return Tip("the affected target must be offline; verify its state and obtain explicit authorization before changing the online " +
                    "connection.");
                case "OUTCOME_UNKNOWN"
                    : return Tip("only this call is unknown. Do not repeat this call. " +
                        (Has("GetCallResult") ? "Read GetCallResult(requestId) and inspect the target." : "Inspect the target.") +
                        (Has("GetSessionState") ? " GetSessionState lists unsettled request IDs." : ""));
                case "PRECONDITION_FAILED"
                    : return Tip("read " + reference + ", verify the target and the stated preconditions, then review a new preview before another call.");
                case "CONFIRMATION_REQUIRED":
                    return Tip("read " + reference + ", review the preview and obtain Workbench confirmation before a new apply request.");
                default: return "";
            }
        }

        public static string? ForwardedTool(IReadOnlyDictionary<string, JsonElement>? arguments)
            => arguments != null && arguments.TryGetValue("name", out var name) && name.ValueKind == JsonValueKind.String
                && arguments.All(p => p.Key == "name" || p.Key == "arguments" && p.Value.ValueKind == JsonValueKind.Object) ? name.GetString() : null;

        public static bool Attach(JsonNode? body, Func<string, string, string, JsonObject?> example, Func<string, bool>? registered = null,
            string? forwardedTool = null)
        {
            if (body is not JsonObject || body["ok"]?.GetValue<bool>() != false
                || body["meta"] is not JsonObject meta || meta["warnings"] is not JsonArray warnings) return false;
            string code = (string?)body["error"]?["code"] ?? "";
            if (code is not ("INVALID_ARGUMENT" or "PRECONDITION_FAILED" or "CONFIRMATION_REQUIRED")) return false;
            foreach (var warning in warnings) if ((string?)warning?["code"] == "RECOVERY_GUIDANCE") return false;
            string tool = (string?)meta["tool"] ?? "";
            if (tool == "CallTool" && forwardedTool != null && registered?.Invoke(forwardedTool) == true) tool = forwardedTool;
            string release = (string?)meta["releaseKey"] ?? "";
            if (registered != null && (!registered("GetToolUsage") || !registered(tool))) return false;
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
                ["bindingMeaning"] =
                    "Example values are placeholders and sample targets. Resolve and review them against this session; this hint does not authorize a write." };
            var guidance = new TiaMcp.Logic.V4.Warning(TiaMcp.Logic.V4.WarningCode.RecoveryGuidance, RecoveryCode(code, registered).Trim(),
                System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json
                    .JsonElement>>(details.ToJsonString())!);
            if (body["schemaVersion"]?.GetValue<int?>() != 4)
            {
                warnings.Add(JsonNode.Parse(TiaMcp.Logic.V4.V4Json.Serialize(guidance)));
                return true;
            }
            var validated = TiaMcp.Logic.V4.EnvelopeMutation.AddWarning(body, guidance).AsObject();
            body.AsObject().Clear();
            foreach (var pair in validated) body.AsObject().Add(pair.Key, pair.Value?.DeepClone());
            return true;
        }
        private static string Tip(string text) => "  ▶ RECOVERY: " + text;
    }
}
