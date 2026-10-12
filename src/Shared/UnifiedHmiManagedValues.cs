global using UnifiedObject = System.Text.Json.Nodes.JsonObject;
global using UnifiedArray = System.Text.Json.Nodes.JsonArray;
global using UnifiedNode = System.Text.Json.Nodes.JsonNode;
global using UnifiedScalar = TiaMcpServer.Siemens.EngineeringScalarProperties;
global using UnifiedGroups = TiaMcpServer.Siemens.EngineeringGroupOperations;
global using UnifiedValues = TiaMcpServer.Siemens.UnifiedHmiManagedValues;
global using UnifiedAccess = TiaMcpServer.Siemens.UnifiedHmiManagedValues;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Compatibility serialization for the remaining F27/F28 callers. Native reflection
    // helpers have one source under Adapters/Native/Hmi and also compile against DTO values.
    internal static class UnifiedHmiManagedValues
    {
        internal static PortalException Error(string code, string message)
            => new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), code), message);
        internal static JsonNode? Parse(string input) => JsonNode.Parse(input);
        internal static JsonNode? Value<T>(T value) => JsonSerializer.SerializeToNode(value);
        internal static object? ConvertValue(JsonNode? node, Type type) => EngineeringScalarProperties.ConvertValue(node, type);
        internal static object? ConvertField(JsonObject fields, string key, Type type)
            => UnifiedUiModelLogic.ConvertValue(fields[key], type);
        internal static void CopyScalar(JsonObject source, JsonObject target, string key) { }
        internal static bool TryString(JsonNode? node, out string text)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var result)) { text = result; return true; }
            text = string.Empty;
            return false;
        }
        internal static bool TryGetValue(this JsonObject fields, string key, out JsonNode? value)
            => fields.TryGetPropertyValue(key, out value);
    }
}
