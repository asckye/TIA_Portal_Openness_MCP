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
    /// The common factor is a cross-reference query while the project holds uncompiled changes, so the engine refuses
    /// the query while any block of the PLC reports IsConsistent = false and names them; CompileSoftware clears it.
    /// </summary>
    public static class CrossReferenceGuardLogic
    {
        public const int ListLimit = 10;

        /// <summary>Null when the query may run; otherwise the refusal text (the blocks that are not compiled).</summary>
        public static string? Refusal(IReadOnlyList<(string Path, bool? Consistent)> blocks, string softwarePath)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            var stale = blocks.Where(b => b.Consistent == false).Select(b => b.Path).ToList();
            if (stale.Count == 0) return null;
            string shown = string.Join(", ", stale.Take(ListLimit));
            if (stale.Count > ListLimit) shown += ", ... (+" + (stale.Count - ListLimit) + " more)";
            return "refused: " + stale.Count + " block(s) of '" + softwarePath + "' are not compiled (IsConsistent=false): " + shown
                 + ". The cross-reference index is stale until the PLC compiles, and on the maintainer's real project TIA Portal V21 exited "
                 + "right after such a query (2026-09-21: five blocks re-imported with Override, then a query on the old instance DB). "
                 + "Run CompileSoftware (errorCount=0) first, then query again.";
        }

        /// <summary>How many blocks are known to be uncompiled (for reports that only need the number).</summary>
        public static int StaleCount(IReadOnlyList<(string Path, bool? Consistent)> blocks)
            => blocks == null ? 0 : blocks.Count(b => b.Consistent == false);
    }
}
