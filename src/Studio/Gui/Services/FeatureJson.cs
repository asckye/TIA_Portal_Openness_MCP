using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaOpenness.Gui.Services;

/// <summary>Both display and clipboard use this boundary; malformed payloads are never echoed.</summary>
public static class FeatureJson
{
    public static string Redact(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            Scrub(node);
            return node?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? "null";
        }
        catch (JsonException) /* swallow(privacy): invalid JSON is hidden in full so parsing failures cannot expose credentials */
        { return "••••"; }
    }

    private static void Scrub(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(item => item.Key).ToArray())
            {
                string normalized = key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                if (normalized.Contains("secret", StringComparison.Ordinal) || normalized.Contains("password", StringComparison.Ordinal)
                    || normalized.Contains("token", StringComparison.Ordinal) || normalized.Contains("apikey", StringComparison.Ordinal)
                    || normalized is "authorization" or "proxyauthorization" or "credential" or "credentials" or "sk")
                    obj[key] = normalized.EndsWith("authorization", StringComparison.Ordinal) ? "Bearer ••••" : "••••";
                else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[key] = MaskBearer(text);
                else Scrub(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = MaskBearer(text);
                else Scrub(array[i]);
            }
        }
    }

    private static string MaskBearer(string text) => Regex.Replace(text, @"(?i)\bBearer\s+[^\s""\\]+", "Bearer ••••");
}
