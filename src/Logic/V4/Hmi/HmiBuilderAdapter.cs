using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4.Hmi
{
    // Only translates input shapes. No builder, tool, project or native call is wired here.
    internal static class HmiBuilderAdapter
    {
        internal static JsonObject Convert(HmiObject value)
        {
            var result = JsonNode.Parse(V4Json.Serialize(value))!.AsObject();
            if (value is UnifiedScreenSpec || value is UnifiedLayoutSpec)
            {
                if (result["screen"] is JsonObject screen) result["screen"] = UnifiedScreen(screen);
            }
            else if (value is UnifiedScreen) return UnifiedScreen(result);
            return result;
        }

        private static JsonObject UnifiedScreen(JsonObject screen)
        {
            var result = screen["properties"]?.DeepClone() as JsonObject ?? new JsonObject();
            result["Name"] = screen["name"]!.DeepClone();
            if (screen["width"] != null) result["Width"] = screen["width"]!.DeepClone();
            if (screen["height"] != null) result["Height"] = screen["height"]!.DeepClone();
            return result;
        }
    }
}
