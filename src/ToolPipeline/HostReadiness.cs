using System.Text.Json.Nodes;

namespace TiaMcpServer.Runtime
{
    // Admission uses the session's installation evidence, even when a running worker has a cached status.
    public static class OpennessReadiness
    {
        private static JsonObject Current => TiaMcp.FoundationHost.EngineHostConfiguration.Worker.EnvironmentReadiness();
        public static bool? GroupOk => (bool?)Current["groupOk"];
        public static bool Ready => (bool?)Current["ready"] == true;
        public static string? Cause => (string?)Current["cause"];
        public static string? FixEn => (string?)Current["recommendedFix"];
        public static string? FixZh => (string?)Current["recommendedFixZh"];
    }

}
