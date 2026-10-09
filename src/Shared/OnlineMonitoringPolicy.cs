using System.Collections.Generic;
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OnlineMonitoringPolicy
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
    }
}
