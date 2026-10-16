using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;

namespace TiaMcp.FoundationHost;

internal static class WorkerSdkFixture
{
    // Read only assembly metadata. Loading a net48 worker into the net10 host
    // would also load its release-specific dependencies into the wrong process.
    internal static bool Ready(string worker, string release)
    {
        if (Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_SDK_READY") != "1") return false;
        var values = WorkerAssemblyMetadata.Read(worker);
        return values.GetValueOrDefault("TiaReleaseKey") == release
            && WorkerFaultInjection.PlcSdkReady(key => values.GetValueOrDefault(key));
    }

    internal static JsonObject Readiness(string worker, string release, string apiDirectory, Func<JsonObject> production)
        => Ready(worker, release)
            ? FoundationPassiveDiagnostics.Readiness(release, _ => apiDirectory, () => true)
            : production();
}
