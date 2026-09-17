using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/parsing for the PlcBlockServices family; no Siemens.Engineering dependency so it is testable offline.
    internal static class PlcBlockServicesLogic
    {
        internal static readonly string[] ProtectionActions = { "read", "protect", "unprotect" };
        internal static readonly string[] SnapshotActions = { "read", "createSnapshot", "loadSnapshotAsActualValues", "loadStartValuesAsActualValues", "exportSnapshot" };
        internal static readonly string[] TextListActions = { "read", "createFromMasterCopy", "delete" };

        // Returns true when the request performs a real native write (not read, not preview).
        internal static bool ValidateProtectionRequest(string action, string password, bool confirmProtectionChange, bool dryRun)
        {
            if (!ProtectionActions.Contains(action)) throw new ArgumentException("action must be read/protect/unprotect.");
            if (action == "read") return false;
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("A nonempty password is required to protect or unprotect.");
            if (password.Length > 256) throw new ArgumentException("Password exceeds 256 characters.");
            if (dryRun) return false;
            if (!confirmProtectionChange) throw new ArgumentException("Real protect/unprotect requires confirmProtectionChange=true besides dryRun=false.");
            return true;
        }

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

        internal static bool ValidateTextListRequest(string action, string name, string libraryName, string masterCopyPath, bool confirmDelete, bool dryRun)
        {
            if (!TextListActions.Contains(action)) throw new ArgumentException("action must be read/createFromMasterCopy/delete.");
            if (action == "read") return false;
            if (action == "delete" && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("delete requires the exact user text list name.");
            if (action == "createFromMasterCopy" && (string.IsNullOrWhiteSpace(libraryName) || string.IsNullOrWhiteSpace(masterCopyPath))) throw new ArgumentException("createFromMasterCopy requires libraryName and masterCopyPath.");
            if (dryRun) return false;
            if (action == "delete" && !confirmDelete) throw new ArgumentException("Real deletion requires confirmDelete=true besides dryRun=false.");
            return true;
        }

        internal static SecureString ToSecureString(string password)
        {
            var secure = new SecureString();
            foreach (var c in password) secure.AppendChar(c);
            secure.MakeReadOnly();
            return secure;
        }

        // Only reports whether the policy is violated; never which characters or where.
        internal static bool ContainsInvalidPasswordCharacter(string password, IEnumerable<char> invalid)
        {
            var set = new HashSet<char>(invalid);
            return set.Count > 0 && password.Any(set.Contains);
        }

        internal static CultureInfo[] ParseCultureNames(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) throw new ArgumentException("culturesJson must be a JSON array of 1..64 culture names.");
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("culturesJson must be a JSON array of culture names.");
            var names = array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("Every culture entry must be a string.")).ToArray();
            if (names.Length < 1 || names.Length > 64 || names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Provide 1..64 nonempty culture names.");
            var cultures = names.Select(n => {
                try { return CultureInfo.GetCultureInfo(n.Trim()); }
                catch (CultureNotFoundException) { throw new ArgumentException("Unknown culture name: " + n); }
            }).ToArray();
            if (cultures.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cultures.Length) throw new ArgumentException("Duplicate culture names.");
            return cultures;
        }

        // Live offset pagination; sets the honest counters and returns the window to read.
        internal static (int Skip, int Take) Paginate(JsonObject meta, int total, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
            int actual = Math.Max(0, Math.Min(limit, total - offset));
            meta["expectedCount"] = total; meta["actualCount"] = actual; meta["offset"] = offset; meta["limit"] = limit;
            meta["nextOffset"] = offset + limit < total ? offset + limit : (int?)null;
            meta["truncated"] = offset + limit < total;
            meta["dataComplete"] = offset == 0 && !(offset + limit < total);
            return (offset, limit);
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

        // Exact IP match only; several PG/PC adapters to the same CPU address require an exact pgPcInterface.
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
    }
}
