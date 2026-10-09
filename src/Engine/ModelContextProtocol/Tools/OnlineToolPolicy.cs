using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens.Services;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OnlineToolPolicy
    {
        internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy()
         => OnlineMonitoringPolicy.GetOnlineMonitoringSafetyPolicy();

        internal static bool IsOnlineModeError(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? string.Empty;
                if (m.IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.IndexOf("online", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        internal static T WithAutoOffline<T>(Func<T> op)
        {
            try { return op(); }
            catch (Exception ex) when (IsOnlineModeError(ex))
            {
                try { ((OnlineDownloadService)EngineServices.Get(typeof(OnlineDownloadService))).GoOfflineAll(); } catch { /* swallow(native-fallback): preserve the one retry so the original operation reports its own failure */ }
                return op(); // retry once, now fully offline
            }
        }

        internal static T WithAutoOffline<T>(Func<T> op, string softwarePath)
        {
            if (BehaviorCapabilities.Select(typeof(OnlineToolPolicy).Assembly, McpServer.ReleaseKey, "P6-FALLBACK") != BehaviorPolicy.SafeV4)
                return WithAutoOffline(op);
            var service = (OnlineDownloadService)EngineServices.Get(typeof(OnlineDownloadService));
            return WithOfflineRequirement(op, () => service.FallbackOfflineState(softwarePath), service.FallbackUncertain);
        }

        internal static T WithOfflineRequirement<T>(Func<T> op, Func<string> state, Action? uncertain = null)
        {
            string observed = state();
            if (observed == "online") CandidatePrimitives.Fail("offline", "selected-PLC");
            if (observed != "offline") CandidatePrimitives.Fail("precondition", "positive-offline-evidence");
            try { return op(); }
            catch { uncertain?.Invoke(); throw; }
        }
    }
}
