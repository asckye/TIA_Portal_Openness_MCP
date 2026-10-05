using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiTagDeletion
    {
        internal static string[] Validate(string tablePath, string tagName, bool dryRun, bool confirmDelete)
        {
            if (string.IsNullOrWhiteSpace(tagName) || tagName.Any(char.IsControl) || tagName.IndexOfAny(new[] { '*', '?' }) >= 0)
                throw new ArgumentException("One exact tagName is required; patterns are refused.");
            if (!dryRun && !confirmDelete) throw new ArgumentException("Actual deletion requires confirmDelete=true.");
            if (tablePath == "") return Array.Empty<string>(); // Unified device-root Tags only.
            if (!tablePath.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("tagTablePath must be absolute within HMI, e.g. /Folder/Table.");
            string[] parts = tablePath.Substring(1).Split('/');
            if (parts.Length > 64 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".." || p.Any(char.IsControl) || p.IndexOfAny(new[] { '*', '?', '\\' }) >= 0))
                throw new ArgumentException("Invalid exact tag table path; no patterns, escaping or recursive search.");
            return parts;
        }
        internal static string Execute(JsonObject meta, bool dryRun, Func<object?> find, Action<object> delete)
        {
            object target = find() ?? throw new PortalException(PortalErrorCode.NotFound, "Exact HMI tag not found.");
            meta["exists"] = true; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
            meta["referencesChecked"] = false; meta["referencesNote"] = "No cross-reference query. Usage in screens/scripts/alarms/logging is unknown; deletion may break references or remove dependent configuration.";
            if (dryRun) return "Preview only; the exact HMI tag exists and has not been deleted.";
            meta["mayHaveChanged"] = true;
            delete(target);
            if (find() != null) throw new InvalidOperationException("Tag still exists after Delete; no retry performed.");
            meta["exists"] = false; meta["verifiedAbsent"] = true; meta["saved"] = false;
            return "HMI tag deleted and absence verified. Project has not been saved or compiled.";
        }
    }
}
