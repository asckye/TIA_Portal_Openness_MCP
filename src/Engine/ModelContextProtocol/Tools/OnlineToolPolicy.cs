using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OnlineToolPolicy
    {
        internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy()
        {
            return new[]
            {
                "Online monitoring may only read the current state/value of variables.",
                "Online mode must not modify monitoring tables, watch tables or objects within those tables.",
                "MCP must not expose, invoke or bypass any force-table or forcing operation.",
                "Generic reflection entry points must block forcing services and all writes, creation, deletion, downloads, start/stop and online/offline transitions on online, watch and monitoring surfaces.",
                "New monitoring capabilities must first probe the API shape, then verify readback with a minimal instance; until verified, they must be labelled as probe capabilities only."
            };
        }

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
    }
}
