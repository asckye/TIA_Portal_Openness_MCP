using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Native.Plc
{
    /// <summary>
    /// Decides whether a CrossReferenceService query may run at all. Pure logic, no Openness types.
    ///
    /// Real project, TIA V21, 2026-09-21: five blocks re-imported with Override (an FC stopped calling an
    /// instance DB whose structure changed completely), no compile in between, then DeletePlcBlock's dry run asked the
    /// CrossReferenceService for the old IDB's references - it answered "26 references" (the stale index) and TIA Portal
    /// V21 was gone before the next call. A second TIA exit on 2026-09-20 also followed a run of Openness writes.
    /// Every native query requires readable, consistent blocks. Compilation alone cannot guarantee stability.
    /// Native exit evidence: docs/reference/real-machine-ledger.md.
    /// </summary>
    internal static class PlcCrossReferencePolicy
    {
        public const int ListLimit = 10;

        // Use the dedicated query path for target resolution and consistency checks.
        public static string? ReflectionRefusal(string? service, string? method)
            => TiaMcp.Adapters.Contracts.OpennessReflectionPolicy.ReflectionRefusal(service, method);

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
                 + ". No native query was made; this does not mean there are no references. Uncompiled changes may leave cross-reference information " +
                     "outdated; on the maintainer's real project TIA Portal V21 exited "
                 + "right after such a query (2026-09-21: five blocks re-imported with Override, then a query on the old instance DB). "
                 + "Run CompilePlcSoftware (errorCount=0) before considering a diagnostic query; compilation does not guarantee that the native service " +
                     "cannot crash.";
        }

        /// <summary>How many blocks are known to be uncompiled (for reports that only need the number).</summary>
        public static int StaleCount(IReadOnlyList<(string Path, bool? Consistent)> blocks)
            => blocks == null ? 0 : blocks.Count(b => b.Consistent == false);
    }
}
