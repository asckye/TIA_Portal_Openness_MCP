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
