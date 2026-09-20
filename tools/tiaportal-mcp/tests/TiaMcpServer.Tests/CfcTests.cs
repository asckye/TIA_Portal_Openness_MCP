using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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

            // ---- export inventory (2.7.43 preflight: TIA V21 crashed on GetChartProtection / ExportInstructionData on a PLC without charts) ----
            string inventoryZip = Path.Combine(temp, "mcp-cfc-inventory-" + Guid.NewGuid().ToString("N") + ".xml.zip");
            try
            {
                using (var archive = ZipFile.Open(inventoryZip, ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(archive.CreateEntry("Data.xml").Open(), new UTF8Encoding(false)))
                        w.Write("<?xml version=\"1.0\"?><Document><DocumentInfo/><FunctionChartsFolder Name=\"Charts\"><ObjectList><FunctionChart Name=\"CFC_1\"><Block Name=\"B1\"/></FunctionChart><FunctionChart Name=\"CFC_3\"/><FunctionChart Name=\"CFC_1\"/><ChartList Name=\"L\"/></ObjectList></FunctionChartsFolder><Tasks><Task Name=\"OB1\"/></Tasks></Document>");
                    using (var w = new StreamWriter(archive.CreateEntry("readme.txt").Open())) w.Write("not xml");
                }
                var inventory = CfcLogic.InspectExport(inventoryZip);
                check(inventory.Charts.SequenceEqual(new[] { "CFC_1", "CFC_3" }) && inventory.Entries.SequenceEqual(new[] { "Data.xml", "readme.txt" }) && inventory.Elements.Contains("FunctionChart") && inventory.Elements.Contains("Task") && !inventory.Charts.Contains("OB1") && !inventory.Charts.Contains("Charts") && !inventory.Charts.Contains("L"), "cfc inventory: chart names from chart elements only (folder / list containers excluded - 2.7.43 real export), distinct, entries and elements reported");
                check(CfcLogic.IsChartElement("FunctionChart") && CfcLogic.IsChartElement("CFCChart") && !CfcLogic.IsChartElement("FunctionChartsFolder") && !CfcLogic.IsChartElement("Charts") && !CfcLogic.IsChartElement("ChartList") && !CfcLogic.IsChartElement("Block"), "cfc inventory: chart element rule");
                File.Delete(inventoryZip);
                using (var archive = ZipFile.Open(inventoryZip, ZipArchiveMode.Create))
                    using (var w = new StreamWriter(archive.CreateEntry("Data.xml").Open(), new UTF8Encoding(false))) w.Write("<?xml version=\"1.0\"?><Document><DocumentInfo/><FunctionChartsFolder Name=\"Charts\"><ObjectList/></FunctionChartsFolder><UsedAlarmClasses/></Document>");
                var empty = CfcLogic.InspectExport(inventoryZip);
                check(empty.Charts.Length == 0 && empty.Elements.SequenceEqual(new[] { "Document", "DocumentInfo", "FunctionChartsFolder", "ObjectList", "UsedAlarmClasses" }), "cfc inventory: the real empty export (2.7.43 real project) has no charts - refused by the preflight");
            }
            finally { try { File.Delete(inventoryZip); } catch { } }

            // ---- protection ----
            CfcLogic.ProtectionRequest P(string action, string chart = "CFC_1", string current = "", string hashed = "", bool dryRun = true)
                => CfcLogic.ValidateProtectionRequest(action, chart, current, hashed, dryRun);
            check(!P("read").Writes && !P("add", hashed: "AgGUWq...92M=").Writes && P("add", hashed: "AgGUWq...92M=", dryRun: false).Writes && P("change", current: "test", hashed: "AgGUWq...92M=", dryRun: false).Writes && P("remove", current: "test", dryRun: false).Writes, "cfc protection: actions");
            check(Fails<ArgumentException>(() => P("add")) && Fails<ArgumentException>(() => P("change", hashed: "x")) && Fails<ArgumentException>(() => P("remove")) && Fails<ArgumentException>(() => P("read", chart: "")) && Fails<ArgumentException>(() => P("read", current: "test")) && Fails<ArgumentException>(() => P("remove", current: "test", hashed: "x")) && Fails<ArgumentException>(() => P("lock", current: "test")), "cfc protection: gates");
        }
    }
}
