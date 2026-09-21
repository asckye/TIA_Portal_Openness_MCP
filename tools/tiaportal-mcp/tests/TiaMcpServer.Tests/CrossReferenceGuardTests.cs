using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 2.9.1: the cross-reference guard. Real project 2026-09-21: Override re-import of five blocks, no compile, then a
    /// CrossReferenceService query on the old instance DB - TIA Portal V21 terminated itself (Process Exit Monitor 3000,
    /// EVENT_PROCESSTERMINATION_SELF, exit code -1) right after the query returned. The guard refuses the query while any
    /// block of the PLC is not compiled and names the blocks, so the caller compiles first instead of losing TIA.
    /// </summary>
    internal static class CrossReferenceGuardTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var compiled = new List<(string Path, bool? Consistent)> { ("Main", true), ("MOTOR_FB", true), ("MOTOR_FB_IDB", true) };
            check(CrossReferenceGuardLogic.Refusal(compiled, "PLC_1") == null && CrossReferenceGuardLogic.StaleCount(compiled) == 0,
                "cross-reference guard: a fully compiled PLC is queried");

            var unknown = new List<(string Path, bool? Consistent)> { ("Main", true), ("KnowHow_FB", null) };
            check(CrossReferenceGuardLogic.Refusal(unknown, "PLC_1") == null,
                "cross-reference guard: an unreadable IsConsistent does not block the query (only a known false does)");

            var stale = new List<(string Path, bool? Consistent)> { ("Main", true), ("INITIALIZATION_FC", false), ("INITIALIZATION_DB", false), ("SYSTEM_INFO_FB_IDB", true) };
            var refusal = CrossReferenceGuardLogic.Refusal(stale, "PLC_1");
            check(refusal != null && refusal.StartsWith("refused: 2 block(s) of 'PLC_1' are not compiled")
                  && refusal.Contains("INITIALIZATION_FC, INITIALIZATION_DB") && refusal.Contains("CompileSoftware") && refusal.Contains("2026-09-21")
                  && CrossReferenceGuardLogic.StaleCount(stale) == 2,
                "cross-reference guard: uncompiled blocks refuse the query, are named, and the fix (CompileSoftware) plus the real-project reason are stated");

            var many = new List<(string Path, bool? Consistent)>();
            for (int i = 1; i <= 14; i++) many.Add(("FB_" + i, false));
            var capped = CrossReferenceGuardLogic.Refusal(many, "PLC_2")!;
            check(capped.Contains("FB_10") && !capped.Contains("FB_11,") && capped.Contains("(+4 more)") && capped.StartsWith("refused: 14 block(s)"),
                "cross-reference guard: the list is capped at 10 names with the remainder counted");

            bool threw = false;
            try { CrossReferenceGuardLogic.Refusal(null!, "PLC_1"); } catch (ArgumentNullException) { threw = true; }
            check(threw, "cross-reference guard: a null block list is a programming error, not a silent pass");
        }
    }
}
