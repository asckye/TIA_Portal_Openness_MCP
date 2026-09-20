using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-③ (2.7.42) pure logic: CFC exchange (complete / selective export, import, instruction data) and chart protection requests.
    internal static class CfcTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string zip = Path.Combine(temp, "Chart1.xml.zip");

            check(CfcLogic.ExchangeActions.SequenceEqual(new[] { "export", "selectiveExport", "import", "exportInstructionData" }) && CfcLogic.ProtectionActions.SequenceEqual(new[] { "read", "add", "change", "remove" }), "cfc: catalogues");

            // ---- exchange ----
            CfcLogic.ExchangeRequest X(string action, string path = "", string model = "V2.0", long filter = 0, string charts = "[]", bool delete = false, bool dryRun = true)
                => CfcLogic.ValidateExchangeRequest(action, path, model, filter, charts, delete, dryRun);
            check(!X("export", zip).Writes && !X("export", zip).WritesFile && X("export", zip, dryRun: false).WritesFile && !X("export", zip, dryRun: false).Writes, "cfc exchange: export writes a file only");
            var selective = X("selectiveExport", zip, charts: "[\"CFC_1\",\"CFC_3\"]", dryRun: false);
            check(selective.WritesFile && selective.ChartNames.SequenceEqual(new[] { "CFC_1", "CFC_3" }), "cfc exchange: selective export names");
            check(X("import", zip, delete: true, dryRun: false).Writes && !X("import", zip, delete: true, dryRun: false).WritesFile && X("exportInstructionData", Path.Combine(temp, "instr.txt"), model: "", dryRun: false).WritesFile, "cfc exchange: import / instruction data");
            check(Fails<ArgumentException>(() => X("export", "relative/Chart1.xml.zip")) && Fails<ArgumentException>(() => X("export", zip, model: "")) && Fails<ArgumentException>(() => X("export", zip, filter: -1)) && Fails<ArgumentException>(() => X("selectiveExport", zip)), "cfc exchange: missing arguments refused");
            check(Fails<ArgumentException>(() => X("export", zip, charts: "[\"CFC_1\"]")) && Fails<ArgumentException>(() => X("export", zip, delete: true)) && Fails<ArgumentException>(() => X("exportInstructionData", zip)) && Fails<ArgumentException>(() => X("exportInstructionData", zip, model: "", filter: 1)) && Fails<ArgumentException>(() => X("archive", zip)), "cfc exchange: stray arguments / unknown action refused");

            // ---- protection ----
            CfcLogic.ProtectionRequest P(string action, string chart = "CFC_1", string current = "", string hashed = "", bool dryRun = true)
                => CfcLogic.ValidateProtectionRequest(action, chart, current, hashed, dryRun);
            check(!P("read").Writes && !P("add", hashed: "AgGUWq...92M=").Writes && P("add", hashed: "AgGUWq...92M=", dryRun: false).Writes && P("change", current: "test", hashed: "AgGUWq...92M=", dryRun: false).Writes && P("remove", current: "test", dryRun: false).Writes, "cfc protection: actions");
            check(Fails<ArgumentException>(() => P("add")) && Fails<ArgumentException>(() => P("change", hashed: "x")) && Fails<ArgumentException>(() => P("remove")) && Fails<ArgumentException>(() => P("read", chart: "")) && Fails<ArgumentException>(() => P("read", current: "test")) && Fails<ArgumentException>(() => P("remove", current: "test", hashed: "x")) && Fails<ArgumentException>(() => P("lock", current: "test")), "cfc protection: gates");
        }
    }
}
