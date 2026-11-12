using TiaOpenness.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class OnlineMonitoringPolicy
    {
        private static readonly HashSet<string> ReviewedForceWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ManageWatchForceTableWebAccess" };

        internal static IReadOnlyList<string> ForbiddenForceTools(IEnumerable<string> names, Func<string, ToolMetadata.Classification?> find)
            => names.Where(name => name.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0).Where(name =>
            {
                var row = find(name);
                return !(row?.Operation == "READ" && row.ReadOnlyHint == true
                    || (row?.Operation is "WRITE" or "ONLINE-WRITE") && ReviewedForceWrites.Contains(name));
            }).ToArray();

        internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy()
        {
            return new[]
            {
                "Online monitoring may only read the current state/value of variables.",
                "Online mode must not modify monitoring tables, watch tables or objects within those tables.",
                "MCP must not start, stop or change forcing on a running PLC. Force-table tools are offline engineering: listing is read-only, "
                    + "and any change must be classified WRITE or ONLINE-WRITE so Workbench approval gates it.",
                "Generic reflection entry points must block forcing services and all writes, creation, deletion, downloads, start/stop and online/offline " +
                    "transitions on online, watch and monitoring surfaces.",
                "New monitoring capabilities must first probe the API shape, then verify readback with a minimal instance; until verified, they must be " +
                    "labelled as probe capabilities only."
            };
        }
    }
}
