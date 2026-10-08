using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.BuildCommon;

public static class McpResults
{
    public static JsonObject Envelope(JsonNode? reply)
    {
        if (reply is not JsonObject obj) throw new ArgumentException("Expected an object result");
        JsonObject? result = null;
        JsonNode? value;
        if (obj.ContainsKey("schemaVersion")) value = obj;
        else
        {
            if (obj.ContainsKey("error")) throw new ArgumentException("JSON-RPC error: " + PythonJson.Dumps(obj["error"]));
            result = (obj.ContainsKey("result") ? obj["result"] : obj) as JsonObject ?? throw new ArgumentException("Expected a CallToolResult object");
            value = result["structuredContent"];
            var blocks = result.ContainsKey("content") ? Content(result["content"]) : [];
            var texts = blocks.Select(b => b as JsonObject ?? throw new ArgumentException("Expected a content block object"))
                .Where(b => b["type"]?.ToString() == "text").Select(b => b["text"]?.GetValue<string>() ?? throw new ArgumentException("Missing TextContent text")).ToArray();
            if (value is null)
            {
                if (texts.Length != 1) throw new ArgumentException("Expected structuredContent or one V4 TextContent");
                value = JsonNode.Parse(texts[0]);
            }
            else if (texts.Length != 0 && (texts.Length != 1 || !JsonNode.DeepEquals(JsonNode.Parse(texts[0]), value)))
                throw new ArgumentException("V4 structuredContent/text mismatch");
        }
        if (value is not JsonObject envelope || !IsVersionFour(envelope["schemaVersion"])
            || envelope["ok"] is not JsonValue okValue || !okValue.TryGetValue<bool>(out var ok)
            || !new[] { "data", "error", "meta" }.All(envelope.ContainsKey) || envelope["meta"] is not JsonObject
            || ok && envelope["error"] is not null || !ok && envelope["error"] is not JsonObject)
            throw new ArgumentException("Invalid V4 envelope");
        if (result is not null && Truthy(result["isError"]) != !ok) throw new ArgumentException("V4 isError/ok mismatch");
        return envelope;
    }

    private static bool IsVersionFour(JsonNode? value)
    {
        if (value is not JsonValue scalar) return false;
        var element = JsonSerializer.SerializeToElement(scalar);
        return element.ValueKind == JsonValueKind.Number && element.GetDouble() == 4;
    }

    private static JsonArray Content(JsonNode? value) => value switch
    {
        JsonArray array => array,
        JsonObject obj when obj.Count == 0 => [],
        JsonValue scalar when scalar.TryGetValue<string>(out var text) && text.Length == 0 => [],
        _ => throw new ArgumentException("Expected iterable content blocks")
    };

    public static JsonObject Successful(JsonNode? reply)
    {
        var value = Envelope(reply);
        if (!value["ok"]!.GetValue<bool>()) throw new ArgumentException("Tool failed: " + PythonJson.Dumps(value["error"]));
        return value;
    }

    private static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonObject obj => obj.Count != 0,
        JsonArray array => array.Count != 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var boolean) => boolean,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length != 0,
        JsonValue scalar => JsonSerializer.SerializeToElement(scalar) is var element && element.ValueKind == JsonValueKind.Number && element.GetDouble() != 0,
        _ => true
    };
}
