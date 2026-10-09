using System.Text.Json.Nodes;
using System.Text.Json;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    // PrepareRawRequest's JSON depth, shared by typed and
    // expert messages. Byte measurement and traversal use the common input budget.
    internal static class OpenPipeLimits
    {
        internal const int MaxBytes = 10 * 1024 * 1024;
        internal const int MaxDepth = V4Json.MaximumInputDepth;
        internal const string ValueDefinition = "openPipeNativeValue";
        internal static InputBudget Budget(int depth = MaxDepth) => new InputBudget(MaxBytes, depth, utf8Bytes: MaxBytes);
        internal static InputContract<JsonElement> Wire => new InputContract<JsonElement>(
            new InputSchema(V4Json.ParseInput("{\"type\":\"object\"}")), Budget());
        internal static JsonObject ValueSchema() => new JsonObject
        {
            ["oneOf"] = new JsonArray(JsonNode.Parse(V4Json.Serialize(InputSchema.Scalar().Json)),
                new JsonObject { ["type"] = "array", ["items"] = Reference() },
                new JsonObject { ["type"] = "object", ["additionalProperties"] = Reference() })
        };
        private static JsonObject Reference() => new JsonObject { ["$ref"] = "#/$defs/" + ValueDefinition };
        internal static InputSchema Schema(JsonObject schema, int depth)
        {
            schema["$defs"] = new JsonObject { [ValueDefinition] = ValueSchema() };
            schema["x-maxUtf8Bytes"] = MaxBytes; schema["x-maxDepth"] = depth;
            return new InputSchema(V4Json.ParseInput(schema.ToJsonString()));
        }
        internal static InputContract<NativeValue> Values => NativeValueValidator.Create(new NativeValuePolicy(
            Schema(ValueSchema(), MaxDepth - 2), Budget(MaxDepth - 2)));
    }
}
