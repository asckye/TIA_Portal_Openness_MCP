using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens;

internal static class EngineeringObjectPath
{
        private static readonly string[] BackLinks = { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        internal static JsonArray Parse(string json)
        {
            if (json == null || json.Length > 32768) throw new ArgumentException("Object path exceeds 32 KiB.");
            var path = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("Path must be a JSON array.");
            if (path.Count > 24) throw new ArgumentException("At most 24 property steps.");
            foreach (var node in path)
            {
                var step = node as JsonObject ?? throw new ArgumentException("Each step must be {property,name?}.");
                if (step.Any(p => p.Key != "property" && p.Key != "name")) throw new ArgumentException("Unknown object-path key.");
                var property = step["property"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(property) || !property!.All(c => char.IsLetterOrDigit(c) || c == '_') || BackLinks.Contains(property, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid or backward property step: " + property);
                if (step.ContainsKey("name") && string.IsNullOrWhiteSpace(step["name"]?.GetValue<string>())) throw new ArgumentException("Exact nonempty name required.");
            }
            return path;
        }
}
