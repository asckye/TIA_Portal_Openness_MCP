using System;
using System.Linq;
using System.Collections.Generic;
using System.Security;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;

namespace TiaMcp.Adapters.Native.Online;

internal static class OnlineDataPolicy
{
    private static readonly string[] SnapshotActions = { "read", "createSnapshot", "loadSnapshotAsActualValues", "loadStartValuesAsActualValues", "exportSnapshot" };
    internal static SecureString ToSecureString(string password) => EngineeringCredentialRules.ToSecureString(password);
        internal static bool ValidateSnapshotRequest(string action, string filePath, bool confirmValueChange, bool dryRun)
        {
            if (!SnapshotActions.Contains(action)) throw new ArgumentException("action must be read/createSnapshot/loadSnapshotAsActualValues/loadStartValuesAsActualValues/exportSnapshot.");
            if (action == "read") return false;
            if (action == "exportSnapshot" && string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("exportSnapshot requires an absolute new filePath.");
            if (action != "exportSnapshot" && !string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath is only used by exportSnapshot.");
            if (dryRun) return false;
            if (action.StartsWith("load", StringComparison.Ordinal) && !confirmValueChange) throw new ArgumentException("Loading values requires confirmValueChange=true besides dryRun=false.");
            return true;
        }

        internal static (int Skip, int Take) Paginate(HmiObject meta, int total, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
            int actual = Math.Max(0, Math.Min(limit, total - offset));
            meta["expectedCount"] = total; meta["actualCount"] = actual; meta["offset"] = offset; meta["limit"] = limit;
            meta["nextOffset"] = offset + limit < total ? offset + limit : (int?)null;
            meta["truncated"] = offset + limit < total;
            meta["dataComplete"] = offset == 0 && !(offset + limit < total);
            return (offset, limit);
        }

        internal static RouteCandidate SelectFingerprintRoute(IReadOnlyList<RouteCandidate> candidates, string targetIpAddress, string pgPcInterface)
        {
            if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("Exact targetIpAddress is required; no first-route fallback.");
            var byAddress = candidates.Where(c => string.Equals(c.Address, targetIpAddress.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byAddress.Length == 0) throw new PortalException(PortalErrorCode.NotFound, "No configured PLC interface address equals " + targetIpAddress + ".");
            var selected = string.IsNullOrWhiteSpace(pgPcInterface) ? byAddress : byAddress.Where(c => string.Equals(c.PcInterfaceName, pgPcInterface.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selected.Length == 0) throw new PortalException(PortalErrorCode.NotFound, "No route to " + targetIpAddress + " uses PG/PC interface " + pgPcInterface + ".");
            if (selected.Length > 1) throw new PortalException(PortalErrorCode.InvalidParams, "Ambiguous route to " + targetIpAddress + " (" + selected.Length + " PG/PC interfaces); pass the exact pgPcInterface name.");
            return selected[0];
        }

        internal sealed class RouteCandidate
        {
            internal string ModeName = "";
            internal string PcInterfaceName = "";
            internal string TargetName = "";
            internal string Address = "";
            internal object? NativeAddress = null;   // ConfigurationAddress; untyped so the logic stays free of Siemens.Engineering
            internal string Describe() => ModeName + " / " + PcInterfaceName + " -> " + TargetName + " [" + Address + "]";
        }
}
