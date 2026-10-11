using System;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class HmiSnapshot
    {
        internal static JsonObject Capture(object root, int maxDepth = 6, int maxNodes = 2000, int maxString = 65536,
            Action<string, string, Exception?>? trace = null)
            => JsonSerializer.SerializeToNode(TiaMcp.Adapters.Native.Hmi.HmiAccessPrimitives.Capture(root, maxDepth, maxNodes, maxString, trace))!.AsObject();
    }
}
