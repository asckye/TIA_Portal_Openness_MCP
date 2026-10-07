using System.Text.Json.Nodes;

namespace TiaMcp.Logic.ModelContextProtocol
{
    public static class ImportStagingSchema
    {
        public static JsonObject Files() => new JsonObject {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 128,
            ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("fileName", "kind", "content"),
                ["properties"] = new JsonObject {
                    ["fileName"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("scl", "simaticml", "tagtable", "udt", "s7dcl", "s7res") },
                    ["content"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4194304 }
                }
            }
        };
    }
}
