using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 2.9.1: the cross-reference guard. Real project 2026-09-21: Override re-import of five blocks, no compile, then a
    /// CrossReferenceService query on the old instance DB - TIA Portal V21 terminated itself (Process Exit Monitor 3000,
    /// EVENT_PROCESSTERMINATION_SELF, exit code -1) right after the query returned. The guard refuses the query while any
    /// block of the PLC is not compiled or its state is unknown. Native queries also require a server-process opt-in.
    /// </summary>
    internal static class CrossReferenceGuardTests
    {
        internal static void Run(Action<bool, string> check)
        {
            check(CrossReferenceGuardLogic.PolicyRefusal(null)?.Contains("No native query was made") == true,
                "cross-reference policy: unset process setting refuses native queries and never reports zero references");
            foreach (var setting in new[] { "", "0", "true", "yes", " 1 ", "invalid" })
                check(CrossReferenceGuardLogic.PolicyRefusal(setting) != null,
                    "cross-reference policy: only the exact diagnostic opt-in enables queries (rejected '" + setting + "')");
            check(CrossReferenceGuardLogic.PolicyRefusal("1") == null,
                "cross-reference policy: exact opt-in permits the compile-state checks, not a guarantee of native safety");

            var compiled = new List<(string Path, bool? Consistent)> { ("Main", true), ("MOTOR_FB", true), ("MOTOR_FB_IDB", true) };
            check(CrossReferenceGuardLogic.Refusal(compiled, "PLC_1") == null && CrossReferenceGuardLogic.StaleCount(compiled) == 0,
                "cross-reference guard: a fully compiled PLC passes the state check (process opt-in still required)");

            var unknown = new List<(string Path, bool? Consistent)> { ("Main", true), ("KnowHow_FB", null) };
            var unknownReason = CrossReferenceGuardLogic.Refusal(unknown, "PLC_1");
            check(unknownReason?.Contains("KnowHow_FB") == true && unknownReason.Contains("No native query was made")
                  && CrossReferenceGuardLogic.StaleCount(unknown) == 0,
                "cross-reference guard: unreadable IsConsistent refuses without misreporting it as uncompiled");

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

            var mixed = new List<(string Path, bool? Consistent)> { ("Unknown_FB", null), ("Stale_FB", false) };
            check(CrossReferenceGuardLogic.Refusal(mixed, "PLC_1")?.Contains("Stale_FB") == true,
                "cross-reference guard: mixed unknown/stale blocks still refuse the query");
            var manyUnknown = new List<(string Path, bool? Consistent)>();
            for (int i = 1; i <= 12; i++) manyUnknown.Add(("Unknown_" + i, null));
            var cappedUnknown = CrossReferenceGuardLogic.Refusal(manyUnknown, "PLC_1")!;
            check(cappedUnknown.Contains("Unknown_10") && !cappedUnknown.Contains("Unknown_11") && cappedUnknown.Contains("(+2 more)"),
                "cross-reference guard: unreadable block names are bounded too");

            bool threw = false;
            try { CrossReferenceGuardLogic.Refusal(null!, "PLC_1"); } catch (ArgumentNullException) { threw = true; }
            check(threw, "cross-reference guard: a null block list is a programming error, not a silent pass");
        }
    }
}
