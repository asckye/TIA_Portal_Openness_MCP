using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol;

internal static class HmiTemplateJson
{
    internal static JsonNode? Get(JsonObject? obj, string key)
        => obj?[key] ?? obj?.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    internal static JsonObject? Screen(JsonObject? root) => Get(root, "Screen") as JsonObject;
    internal static JsonArray? Items(JsonObject? root) => Get(root, "Items") as JsonArray;
    internal static string Name(JsonObject? obj) => Get(obj, "Name")?.ToString() ?? "";
    internal static string Type(JsonObject item) => Get(item, "Type")?.ToString() ?? "";

    internal static IEnumerable<(string Tag, bool ReadOnly)> Bindings(JsonObject item)
    {
        var binding = Get(item, "Binding") as JsonObject;
        if (Get(binding, "PlcSymbol")?.ToString() is string tag && !string.IsNullOrWhiteSpace(tag))
            yield return (tag, string.Equals(Get(binding, "Direction")?.ToString(), "read", StringComparison.OrdinalIgnoreCase));
    }
}
