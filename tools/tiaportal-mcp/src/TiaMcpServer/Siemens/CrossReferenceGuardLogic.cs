using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// 2.9.1: decides whether a CrossReferenceService query may run at all. Pure logic, no Openness types.
    ///
    /// Real project, 2026-09-21 (maintainer's timeline): five blocks re-imported with Override (an FC stopped calling an
    /// instance DB whose structure changed completely), no compile in between, then DeletePlcBlock's dry run asked the
    /// CrossReferenceService for the old IDB's references - it answered "26 references" (the stale index) and TIA Portal
    /// V21 was gone before the next call. A second TIA exit on 2026-09-20 also followed a run of Openness writes.
    /// Uncompiled changes are a suspected trigger, not a complete explanation of every crash. Native PLC queries
    /// are disabled by default. Explicitly enabling them still requires every block's consistency to be readable/true.
    /// </summary>
    public static class CrossReferenceGuardLogic
    {
        public const int ListLimit = 10;
        public const string NativeQueryOptInVariable = "TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES";

        // Reflection must never bypass the dedicated target / consistency checks, even after opt-in.
        public static string? ReflectionRefusal(string? service, string? method)
        {
            const string type = "Siemens.Engineering.CrossReference.CrossReferenceService";
            var suffix = (service ?? "").Trim();
            bool matches = suffix.Length > 0 && (type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || suffix.IndexOf("CrossReference", StringComparison.OrdinalIgnoreCase) >= 0);
            return matches || string.Equals(method, "GetCrossReferences", StringComparison.OrdinalIgnoreCase)
                ? "Native PLC cross references cannot be accessed through reflection. Use the guarded GetCrossReferences tool; its default-disabled policy and consistency checks also apply after explicit opt-in."
                : null;
        }

        /// <summary>Only an explicit process setting enables this known-crashing native service.</summary>
        public static string? PolicyRefusal(string? setting)
            => setting == "1" ? null
                : "refused: native PLC cross references are disabled because CrossReferenceService queries have terminated TIA Portal. "
                + "No native query was made; this does not mean there are no references. "
                + "Use exported PLC documents for partial offline analysis. For controlled diagnosis on a saved test project only, "
                + "set " + NativeQueryOptInVariable + "=1 in the MCP server process before starting it. "
                + "Compilation is not a guarantee against this crash.";

        /// <summary>Null when the query may run; otherwise the refusal text (the blocks that are not compiled).</summary>
        public static string? Refusal(IReadOnlyList<(string Path, bool? Consistent)> blocks, string softwarePath)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            var stale = blocks.Where(b => b.Consistent == false).Select(b => b.Path).ToList();
            if (stale.Count == 0)
            {
                var unknown = blocks.Where(b => b.Consistent == null).Select(b => b.Path).ToList();
                if (unknown.Count == 0) return null;
                string names = string.Join(", ", unknown.Take(ListLimit));
                if (unknown.Count > ListLimit) names += ", ... (+" + (unknown.Count - ListLimit) + " more)";
                return "refused: compile state could not be read for " + unknown.Count + " block(s) of '" + softwarePath + "': " + names
                    + ". No native query was made. Unknown IsConsistent is not evidence of a compiled PLC; resolve the read failure first.";
            }
            string shown = string.Join(", ", stale.Take(ListLimit));
            if (stale.Count > ListLimit) shown += ", ... (+" + (stale.Count - ListLimit) + " more)";
            return "refused: " + stale.Count + " block(s) of '" + softwarePath + "' are not compiled (IsConsistent=false): " + shown
                 + ". Uncompiled changes may leave cross-reference information outdated; on the maintainer's real project TIA Portal V21 exited "
                 + "right after such a query (2026-09-21: five blocks re-imported with Override, then a query on the old instance DB). "
                 + "Run CompileSoftware (errorCount=0) before considering a diagnostic query; compilation does not guarantee that the native service cannot crash.";
        }

        /// <summary>How many blocks are known to be uncompiled (for reports that only need the number).</summary>
        public static int StaleCount(IReadOnlyList<(string Path, bool? Consistent)> blocks)
            => blocks == null ? 0 : blocks.Count(b => b.Consistent == false);
    }
}
