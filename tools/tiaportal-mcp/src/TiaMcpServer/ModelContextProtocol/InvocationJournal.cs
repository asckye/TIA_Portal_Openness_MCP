using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Keep the engine's JSON contract at its existing boundary.
    internal static partial class InvocationJournal
    {
        internal static Func<JsonObject?>? BindingSnapshot;
        private static string? ReadBinding() => BindingSnapshot?.Invoke()?.ToJsonString();
        internal static JsonObject Health() => JsonNode.Parse(HealthRow().ToString())!.AsObject();
        internal static void Write(string id, string name, string phase, string? objectType = null, string? objectPath = null, JsonObject? details = null)
            => WriteRow(id, name, phase, objectType, objectPath, () => Details(details));
        internal static JsonLineObject? Details(JsonObject? details)
        {
            if (details == null) return null;
            var row = new JsonLineObject();
            foreach (var pair in details) row.Raw(pair.Key, pair.Value?.DeepClone().ToJsonString());
            return row;
        }
    }
}
