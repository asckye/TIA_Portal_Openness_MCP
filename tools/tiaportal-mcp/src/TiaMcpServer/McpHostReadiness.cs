using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer
{
    internal static class McpHostReadiness
    {
        private static readonly object Gate = new object();
        private static string Phase = "NotStarted";
        private static string? LastError;
        internal static void Set(string phase, string? error = null)
        { lock(Gate) { Phase=phase; LastError=error; } }
        internal static JsonObject Snapshot()
        {
            lock(Gate) return new JsonObject {
                ["httpAlive"]=true, ["mcpHostReady"]=Phase=="Ready", ["phase"]=Phase,
                ["lastError"]=LastError, ["tiaAvailability"]="NotProbed",
                ["tiaCheck"]="Use GetState for TIA/project state; HTTP readiness never launches or attaches TIA." };
        }
    }
}
