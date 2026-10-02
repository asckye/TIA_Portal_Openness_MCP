using ModelContextProtocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class V17ReadEnvelope
{
    // Worker DTOs use CLR field casing. The MCP SDK's actual serializer policy
    // determines wire casing, exactly as it does for the original response DTOs.
    private static string ManualStatus(string name) => name is "GetBlocks" or "GetTypes" or "GetBlocksWithHierarchy" or "GetPlcTagTables" or "GetBlockInfo" or "GetTypeInfo" or "GetPlcExternalSources" ? "closed-ordinary-plc" : "partial";
    private static string ManualScope(string name) => name == "GetPlcExternalSources" ? "Ordinary PLC root external-source names only; source contents, generation, software units and multiuser-specific semantics excluded. Native acceptance NOT RUN." : "Ordinary PLC root/user groups only; software units, system groups and multiuser-specific semantics are outside this closure. Native acceptance NOT RUN.";
    private static string Wire(string name) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name) ?? name;
    private static JsonNode? Fields(JsonNode? node)
    {
        if(node is JsonArray array) return new JsonArray(array.Select(Fields).ToArray());
        if(node is not JsonObject obj) return node?.DeepClone();
        var output=new JsonObject();
        foreach(var pair in obj)
        {
            if(pair.Value==null && McpJsonUtilities.DefaultOptions.DefaultIgnoreCondition!=System.Text.Json.Serialization.JsonIgnoreCondition.Never) continue;
            // Attribute values may contain user data; do not reinterpret their keys.
            output[Wire(pair.Key)]=pair.Key=="Value" ? pair.Value?.DeepClone() : Fields(pair.Value);
        }
        return output;
    }
    internal static JsonObject Wrap(string name,string member,JsonNode? result)
    {
        if(member is "BlockInfo" or "TypeInfo")
        {
            if(result is not JsonObject details || details["Name"] is not JsonValue value || !value.TryGetValue<string>(out var objectName) || string.IsNullOrWhiteSpace(objectName)
                || details["TypeName"] is not JsonValue type || !type.TryGetValue<string>(out var typeName) || string.IsNullOrWhiteSpace(typeName)
                || details["Attributes"] is not JsonArray)
                throw new InvalidDataException("Worker returned invalid object details for "+name);
            var output=(JsonObject)Fields(details)!;
            output[Wire("Message")]=name+" retrieved";
            output[Wire("Meta")]=new JsonObject { ["success"]=true,["timestamp"]=DateTimeOffset.UtcNow.ToString("O"),["nativeAcceptance"]="unverified",["manualReconciliation"]=ManualStatus(name),["manualScope"]=ManualScope(name),["pathContract"]="exact group-qualified object path" };
            return output;
        }
        if(member=="Items" && result is not JsonArray || member=="Root" && result is not JsonObject)
            throw new InvalidDataException("Worker returned an invalid V17 response payload for "+name);
        if(name is "GetPlcExternalSources" or "GetPlcTagTables")
        {
            if(result is not JsonArray names || names.Any(item=>item is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
                throw new InvalidDataException("Worker returned an invalid name list for "+name);
        }
        return new JsonObject {
            [Wire("Message")]=name+" retrieved",
            [Wire("Meta")]=new JsonObject { ["timestamp"]=DateTimeOffset.UtcNow.ToString("O"),["success"]=true,["nativeAcceptance"]="unverified",["manualReconciliation"]=ManualStatus(name),["manualScope"]=ManualScope(name),["pathContract"]="v17-read-safe-v1: unique legacy aliases or exact foundation address" },
            [Wire(member)]=Fields(result)
        };
    }
}
