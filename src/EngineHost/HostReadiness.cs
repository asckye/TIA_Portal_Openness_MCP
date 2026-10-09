using System.Text.Json.Nodes;

namespace TiaMcpServer.Runtime
{
    // The worker supplies these observations. The host never probes Openness.
    public static class OpennessReadiness
    {
        private static JsonObject Current => TiaMcp.FoundationHost.EngineHostConfiguration.Worker.Snapshot()["readiness"]!.AsObject();
        public static bool? GroupOk => (bool?)Current["groupOk"];
        public static bool Ready => (bool?)Current["ready"] == true;
        public static string? Cause => (string?)Current["cause"];
        public static string? FixEn => (string?)Current["recommendedFix"];
        public static string? FixZh => (string?)Current["recommendedFixZh"];
    }

}

namespace TiaMcpServer.Isolation
{
    // Shared wrappers retain the engine's isolation branches. This assembly uses
    // the Engine channel instead; native process ownership stays in the worker.
    internal static class IsolatedWorkerHost
    {
        internal static object? Current => null;
        internal static bool IsChild => false;
        internal static string? NativeFault => null;
        internal static System.Collections.Generic.IList<global::ModelContextProtocol.Server.McpServerTool> Wrap(
            System.Collections.Generic.IList<global::ModelContextProtocol.Server.McpServerTool> tools) => tools;
    }
}
