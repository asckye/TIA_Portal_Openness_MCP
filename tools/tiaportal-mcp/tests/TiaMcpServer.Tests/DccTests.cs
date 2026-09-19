using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-② (2.7.39) pure logic: DCC chart / block / pin / chart interface / partition / DCB library requests, writable property
    // catalogues per DCC class, import options and the .dcc / .zip file gates.
    internal static class DccTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string dcc = Path.Combine(temp, "Charts.dcc"), zip = Path.Combine(temp, "GMCV5_1_sinamics5_1_(5.1.15).zip");

            // ---- catalogues ----
            check(DccLogic.ImportOptions.SequenceEqual(new[] { "None", "RenameOnConflict" }) && DccLogic.ChartProperties.Contains("HorizontalSheets") && DccLogic.BlockProperties.Contains("GenericInputsNumber") && DccLogic.PinProperties.SequenceEqual(new[] { "Comment", "Value", "Unit", "Invisible", "ForTest" }) && DccLogic.ParameterProperties.Contains("IsSignal"), "dcc: property catalogues");
            check(DccLogic.ChartParts("DCC_1/Sub_1").SequenceEqual(new[] { "DCC_1", "Sub_1" }), "dcc: chart path split");
            check(Fails<ArgumentException>(() => DccLogic.ChartParts("")) && Fails<ArgumentException>(() => DccLogic.ChartParts("a/../b")), "dcc: empty / dotted chart path refused");
            check(Fails<ArgumentException>(() => DccLogic.ValidateProperties("{\"Name\":\"x\"}", DccLogic.PinProperties, "pin")) && Fails<ArgumentException>(() => DccLogic.ValidateProperties("{\"Value\":[1]}", DccLogic.PinProperties, "pin")), "dcc: unknown / non-scalar property refused");

            // ---- charts ----
            DccLogic.ChartRequest C(string path, string action, string file = "", string options = "", string props = "{}", bool confirm = false, bool dryRun = true) => DccLogic.ValidateChartRequest(path, action, file, options, props, confirm, dryRun);
            check(!C("DCC_1", "read").Writes && C("DCC_1", "create", dryRun: false).Writes && C("", "create", dryRun: false).AutoName && !C("DCC_1", "create").Writes, "dcc charts: read / preview never write, create writes, empty name = auto");
            check(C("DCC_1/Sub", "update", props: "{\"Comment\":\"c\",\"HorizontalSheets\":4,\"Partition\":\"Partition_2\"}", dryRun: false).Path.Length == 2, "dcc charts: update on a subchart with typed properties");
            check(C("DCC_1", "delete", confirm: true, dryRun: false).Writes && !C("DCC_1", "delete").Writes, "dcc charts: delete writes only for real");
            var ex = C("DCC_1", "export", dcc, dryRun: false); check(!ex.Writes && ex.WritesFiles, "dcc charts: export writes a file, not the project");
            var im = C("", "import", dcc, "RenameOnConflict", dryRun: false); check(im.Writes && im.ImportOption == "RenameOnConflict" && im.Path.Length == 0, "dcc charts: import at container level with option");
            check(C("", "readSequence").Path.Length == 0 && C("DCC_1", "readSequence").Path.Length == 1 && !C("DCC_1", "optimizeSequence").Writes && C("DCC_1", "showEditor", dryRun: false).Writes, "dcc charts: sequence / optimize / editor actions");
            check(Fails<ArgumentException>(() => C("DCC_1", "delete", dryRun: false)), "dcc charts: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => C("DCC_1", "export", Path.Combine(temp, "x.xml"))), "dcc charts: non-.dcc export refused");
            check(Fails<ArgumentException>(() => C("DCC_1", "read", dcc)), "dcc charts: filePath on read refused");
            check(Fails<ArgumentException>(() => C("DCC_1", "create", props: "{\"Name\":\"Other\"}")), "dcc charts: Name on create refused");
            check(Fails<ArgumentException>(() => C("DCC_1", "update")), "dcc charts: update without properties refused");
            check(Fails<ArgumentException>(() => C("DCC_1", "import", dcc, "Merge")), "dcc charts: unknown import option refused");
            check(Fails<ArgumentException>(() => C("DCC_1", "read", options: "None")), "dcc charts: importOptions outside import refused");

            // ---- blocks ----
            DccLogic.BlockRequest B(string block, string action, string type = "", string lib = "", string props = "{}", bool confirm = false, bool dryRun = true) => DccLogic.ValidateBlockRequest("DCC_1", block, action, type, lib, props, confirm, dryRun);
            check(!B("", "read").Writes && !B("add_1", "read").Writes && B("add_1", "create", "ADD", dryRun: false).Writes && B("", "create", "ADD", dryRun: false).Name == "", "dcc blocks: read all / one, create named or auto");
            check(B("gear_1", "create", "gear", "GMC", "{\"PositionX\":110,\"Partition\":\"Partition_2\"}", dryRun: false).LibraryName == "GMC", "dcc blocks: create with library and properties");
            check(B("add_1", "update", props: "{\"Name\":\"add_3\",\"GenericInputsNumber\":4}", dryRun: false).Writes && B("add_1", "setAsPredecessor", dryRun: false).Writes, "dcc blocks: update / setAsPredecessor");
            check(Fails<ArgumentException>(() => B("add_1", "create")), "dcc blocks: create without blockType refused");
            check(Fails<ArgumentException>(() => B("add_1", "update", "ADD", props: "{\"Comment\":\"x\"}")), "dcc blocks: blockType outside create refused");
            check(Fails<ArgumentException>(() => B("add_1", "delete", dryRun: false)), "dcc blocks: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => B("", "delete", confirm: true)), "dcc blocks: delete without name refused");
            check(Fails<ArgumentException>(() => B("add_1", "update", props: "{\"Pins\":1}")), "dcc blocks: non-writable property refused");

            // ---- pins ----
            DccLogic.PinRequest P(string action, string props = "{}", string partner = "{}", bool signal = false, int number = -1, int index = -1, bool dryRun = true) => DccLogic.ValidatePinRequest("DCC_1", "add_1", "X1", action, props, partner, signal, number, index, dryRun);
            check(!P("read").Writes && P("update", "{\"Value\":42,\"Comment\":\"c\"}", dryRun: false).Writes, "dcc pins: read / update");
            var pp = P("connect", partner: "{\"block\":\"add_2\",\"pin\":\"Y\"}", dryRun: false); check(pp.Writes && pp.PartnerBlock == "add_2" && pp.PartnerPin == "Y" && pp.PartnerInterface == "", "dcc pins: pin-to-pin partner");
            var pi = P("disconnect", partner: "{\"chartInterface\":\"In_1\"}", dryRun: false); check(pi.PartnerInterface == "In_1", "dcc pins: chart interface partner");
            var pub = P("publish", signal: true, number: 21500, index: 0, dryRun: false); check(pub.Writes && pub.SetAsSignal && pub.HasParameterNumber && pub.HasArrayIndex && pub.ParameterNumber == 21500 && pub.ArrayIndex == 0, "dcc pins: indexed publish");
            check(!P("publish", dryRun: false).HasParameterNumber && P("publish", number: 21620, dryRun: false).HasParameterNumber && P("unpublish", dryRun: false).Writes, "dcc pins: publish overloads / unpublish");
            check(P("updateParameter", "{\"Number\":21600,\"ParameterText\":\"t\"}", dryRun: false).Properties.Count == 2, "dcc pins: updateParameter properties");
            check(Fails<ArgumentException>(() => P("connect")), "dcc pins: connect without partner refused");
            check(Fails<ArgumentException>(() => P("connect", partner: "{\"block\":\"a\",\"pin\":\"b\",\"chartInterface\":\"c\"}")), "dcc pins: mixed partner refused");
            check(Fails<ArgumentException>(() => P("publish", index: 2)), "dcc pins: arrayIndex without number refused");
            check(Fails<ArgumentException>(() => P("read", number: 1)), "dcc pins: parameterNumber outside publish refused");
            check(Fails<ArgumentException>(() => P("update", "{\"IsInput\":true}")), "dcc pins: read-only property refused");
            check(Fails<ArgumentException>(() => P("updateParameter", "{\"Value\":1}")), "dcc pins: pin property on updateParameter refused");

            // ---- chart interfaces / partitions ----
            DccLogic.InterfaceRequest I(string name, string action, string block = "", string pin = "", string props = "{}", bool confirm = false, bool dryRun = true) => DccLogic.ValidateInterfaceRequest("DCC_1", name, action, block, pin, props, confirm, dryRun);
            check(!I("", "read").Writes && I("", "create", "add_1", "X1", dryRun: false).SourcePin == "X1" && I("X1", "update", props: "{\"Invisible\":true}", dryRun: false).Writes && I("X1", "delete", confirm: true, dryRun: false).Writes, "dcc interfaces: gates");
            check(Fails<ArgumentException>(() => I("In_1", "create", "add_1", "X1")) && Fails<ArgumentException>(() => I("", "create", "add_1", "")) && Fails<ArgumentException>(() => I("X1", "delete", dryRun: false)), "dcc interfaces: name on create / missing pin / unconfirmed delete refused");
            DccLogic.PartitionRequest Q(string name, string action, string props = "{}", bool confirm = false, bool dryRun = true) => DccLogic.ValidatePartitionRequest("DCC_1", name, action, props, confirm, dryRun);
            check(!Q("", "read").Writes && Q("Partition_2", "create", dryRun: false).Writes && Q("Partition_2", "update", "{\"Name\":\"P2\",\"Comment\":\"c\"}", dryRun: false).Properties.Count == 2 && Q("P2", "delete", confirm: true, dryRun: false).Writes, "dcc partitions: gates");
            check(Fails<ArgumentException>(() => Q("", "create")) && Fails<ArgumentException>(() => Q("P2", "update", "{\"PositionX\":1}")), "dcc partitions: missing name / unknown property refused");

            // ---- DCB libraries ----
            check(!DccLogic.ValidateLibraryRequest("read", "", true) && DccLogic.ValidateLibraryRequest("import", zip, false) && !DccLogic.ValidateLibraryRequest("import", zip, true), "dcc libraries: gates");
            check(Fails<ArgumentException>(() => DccLogic.ValidateLibraryRequest("import", Path.Combine(temp, "lib.tec"), false)) && Fails<ArgumentException>(() => DccLogic.ValidateLibraryRequest("read", zip, true)), "dcc libraries: non-.zip / filePath on read refused");
        }
    }
}
