using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;

namespace TiaMcp.FoundationHost;

internal static class WorkerDialog
{
    internal const string Prefix = "TIA_MCP_DIALOG ";
    internal static JsonObject? Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(line) > 2048) return null;
        try
        {
            var raw = JsonNode.Parse(line[Prefix.Length..]) as JsonObject;
            if (raw == null || (string?)raw["event"] is not ("Confirmation" or "Notification")) return null;
            var result = new JsonObject();
            foreach (string key in new[] { "event", "utc", "caption", "text", "detailText", "answer", "rule" })
                result[key] = HostBehavior.SafeDiagnostic(raw[key]?.GetValue<string>());
            result["choices"] = raw["choices"] is JsonArray choices
                ? new JsonArray(choices.Select(choice => (JsonNode?)JsonValue.Create(HostBehavior.SafeDiagnostic(choice?.GetValue<string>()))).ToArray())
                : new JsonArray();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { /* swallow(parse-fallback): malformed stderr diagnostics leave the worker call running. */ return null; }
    }
}
