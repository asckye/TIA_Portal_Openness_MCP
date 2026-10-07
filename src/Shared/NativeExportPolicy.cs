using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    internal static class NativeExportPolicy
    {
        internal static void RequireConsistent(string kind, IEnumerable<string> inconsistent, string parameter)
        {
            var paths = inconsistent.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return;
            string noun = kind == "types" ? "type" : "block";
            throw new AdapterPreconditionException("Compile the inconsistent " + noun + " before export. Inconsistent objects: "
                + string.Join(", ", paths) + ".", parameter, false);
        }

        internal static void RequireSoftwarePath(string requested, string resolved, bool batch)
        {
            if (batch && !string.Equals(requested, resolved, StringComparison.Ordinal))
                throw new AdapterPreconditionException("Batch operation requires the exact software path, not an alias.", "softwarePath");
        }

        internal static bool ResolvedIdentityMatches(string requested, string resolved)
        {
            if (requested == resolved) return true;
            var actual = resolved.Split('/').Select(Uri.UnescapeDataString).ToArray();
            var input = requested.Split('/');
            if (actual.Length < 2 || input.Length < 1 || input.Any(p => p.Length == 0 || p == "." || p == "..")) return false;
            var groups = new List<string>(); int device = actual.Length - 2;
            if(actual[0] is "devices" or "ungrouped") device=1;
            else if(actual[0]=="device-groups")
            {
                int index=1;
                while(index<actual.Length-1)
                {
                    groups.Add(actual[index++]);
                    if(actual[index]=="devices") { device=index+1; break; }
                    if(actual[index++]!="groups") return false;
                }
            }
            else groups.AddRange(actual.Take(device));
            if(device>=actual.Length-1) return false;
            string host=actual[actual.Length-1];
            if(NativePathSelection.ShortAlias(input,actual[device],host)) return true;
            if (input.Length != groups.Count + 1 && input.Length != groups.Count + 2) return false;
            for (int i = 0; i < groups.Count; i++) if (input[i] != groups[i]) return false;
            if (input.Length == groups.Count + 1) return string.Equals(input[groups.Count],actual[device],StringComparison.OrdinalIgnoreCase)
                || string.Equals(input[groups.Count],host,StringComparison.OrdinalIgnoreCase);
            return string.Equals(input[groups.Count],actual[device],StringComparison.OrdinalIgnoreCase)
                && string.Equals(input[groups.Count+1],host,StringComparison.OrdinalIgnoreCase);
        }

        internal static string? BlockedReason(string status) => status == "inconsistent"
            ? "Compile the inconsistent object before export."
            : status == "semantics-unverified" ? "Export method evidence is insufficient for apply." : null;

        internal static void RequireApply(string status)
        {
            string? reason = BlockedReason(status);
            if (reason != null) throw new AdapterPreconditionException(reason, "export-plan", false);
        }
    }
}
