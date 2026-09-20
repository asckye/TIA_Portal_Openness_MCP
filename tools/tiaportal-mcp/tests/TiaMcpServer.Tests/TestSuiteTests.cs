using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-③ (2.7.42) pure logic: Test Suite categories / kinds, load-option grammar, exchange, execution and management requests
    // including the style-guide scope entries.
    internal static class TestSuiteTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string file = Path.Combine(temp, "TSRuleSets1.xml");

            // ---- catalogues (pinned member by member by TestSuiteShapeChecks) ----
            check(TestSuiteLogic.Categories.SequenceEqual(new[] { "styleGuide", "application", "system" }) && TestSuiteLogic.Kinds.SequenceEqual(new[] { "case", "testSet" })
                && TestSuiteLogic.RSLoadOptions.Length == 4 && TestSuiteLogic.TCLoadOptions.SequenceEqual(new[] { "None", "IgnoreInvalidObject" }) && TestSuiteLogic.TSLoadOptions.Length == 2
                && TestSuiteLogic.ExecutionModes.Length == 2 && TestSuiteLogic.ServerInterfaces.Length == 3 && TestSuiteLogic.UpdateOptions.SequenceEqual(new[] { "Add", "Override" }) && TestSuiteLogic.TestResultsStates.Length == 4 && TestSuiteLogic.ScopeKinds.Length == 7, "test suite: catalogues");
            check(TestSuiteLogic.CategoryLabel("styleGuide") == "rule set" && TestSuiteLogic.CategoryLabel("system") == "system test case" && TestSuiteLogic.RequireKind("application", "") == "case" && TestSuiteLogic.RequireKind("application", "testSet") == "testSet", "test suite: labels / kinds");
            check(Fails<ArgumentException>(() => TestSuiteLogic.RequireKind("styleGuide", "testSet")) && Fails<ArgumentException>(() => TestSuiteLogic.CategoryLabel("unit")), "test suite: testSet outside application refused");

            // ---- load options ----
            check(TestSuiteLogic.ParseLoadOptions("styleGuide", "import", "IgnoreMissingAttributes|SkipInvalidObjects").Length == 2 && TestSuiteLogic.ParseLoadOptions("application", "import", "").SequenceEqual(new[] { "None" }) && TestSuiteLogic.ParseLoadOptions("application", "importTestSets", "IgnoreInvalidObject").Length == 1, "test suite: load option grammar");
            check(Fails<ArgumentException>(() => TestSuiteLogic.ParseLoadOptions("application", "import", "None|IgnoreInvalidObject")) && Fails<ArgumentException>(() => TestSuiteLogic.ParseLoadOptions("system", "import", "IgnorePropertyErrors")) && Fails<ArgumentException>(() => TestSuiteLogic.ParseLoadOptions("styleGuide", "import", "SkipInvalidObjects|SkipInvalidObjects")), "test suite: load option gates");

            // ---- read ----
            check(TestSuiteLogic.ValidateReadRequest("application", "testSet", 0, 100) == "testSet" && Fails<ArgumentException>(() => TestSuiteLogic.ValidateReadRequest("system", "case", 0, 0)), "test suite read: kind and pagination");

            // ---- exchange ----
            TestSuiteLogic.ExchangeRequest X(string category, string action, string name = "RS_1", string path = "", string import = "None", string load = "", string kind = "case", bool dryRun = true)
                => TestSuiteLogic.ValidateExchangeRequest(category, action, name, path, import, load, kind, dryRun);
            check(!X("styleGuide", "export", path: file).Writes && X("styleGuide", "export", path: file, dryRun: false).WritesFile && !X("styleGuide", "export", path: file, dryRun: false).Writes, "test suite exchange: export writes a file only");
            var import = X("styleGuide", "import", name: "", path: file, import: "Override", load: "IgnoreMissingAttributes|SkipInvalidObjects", dryRun: false);
            check(import.Writes && import.ImportOption == "Override" && import.LoadFlags.Length == 2, "test suite exchange: import with combined RSLoadOptions");
            check(X("application", "importTestSets", name: "", path: file, load: "IgnoreInvalidObject", dryRun: false).LoadFlags[0] == "IgnoreInvalidObject" && X("application", "delete", name: "Set_1", kind: "testSet", dryRun: false).Writes && X("system", "import", name: "TC_1", path: file, dryRun: false).Kind == "case", "test suite exchange: test sets / delete / system import");
            check(Fails<ArgumentException>(() => X("styleGuide", "importTestSets", name: "", path: file)) && Fails<ArgumentException>(() => X("application", "export", name: "Set_1", path: file, kind: "testSet")) && Fails<ArgumentException>(() => X("application", "import", name: "", path: file, kind: "testSet")), "test suite exchange: kind gates");
            check(Fails<ArgumentException>(() => X("styleGuide", "delete", name: "")) && Fails<ArgumentException>(() => X("styleGuide", "export")) && Fails<ArgumentException>(() => X("styleGuide", "delete", path: file)) && Fails<ArgumentException>(() => X("styleGuide", "export", path: file, load: "None")) && Fails<ArgumentException>(() => X("styleGuide", "import", name: "", path: file, import: "Merge")), "test suite exchange: missing / stray arguments refused");

            // ---- run ----
            TestSuiteLogic.RunRequest R(string category, string name = "", string names = "[]", bool all = false, string kind = "case", bool confirm = false, bool dryRun = true)
                => TestSuiteLogic.ValidateRunRequest(category, name, names, all, kind, confirm, dryRun);
            check(R("styleGuide", "RS_1").Names.SequenceEqual(new[] { "RS_1" }) && !R("styleGuide", "RS_1").External && R("styleGuide", "RS_1", "[\"RS_2\",\"RS_1\"]").Names.SequenceEqual(new[] { "RS_1", "RS_2" }) && R("styleGuide", all: true).RunAll, "test suite run: selection");
            check(R("application", "TC_1", dryRun: false, confirm: true).External && R("system", "TC_1", dryRun: false, confirm: true).External && R("application", "Set_1", kind: "testSet").Kind == "testSet" && R("application", "TC_1", dryRun: false, confirm: true).Names.Length == 1, "test suite run: application / system external");
            check(Fails<ArgumentException>(() => R("application", "TC_1", dryRun: false)) && Fails<ArgumentException>(() => R("styleGuide")) && Fails<ArgumentException>(() => R("styleGuide", "RS_1", all: true)) && Fails<ArgumentException>(() => R("application", all: true, kind: "testSet")) && Fails<ArgumentException>(() => R("styleGuide", names: "[\"a\",\"a\"]")), "test suite run: gates");
            check(!Fails<ArgumentException>(() => R("styleGuide", "RS_1", dryRun: false)), "test suite run: style guide needs no external confirmation");

            // ---- scope entries ----
            var scope = TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"project\"},{\"kind\":\"plc\",\"softwarePath\":\"PLC_1\"},{\"kind\":\"blocks\",\"softwarePath\":\"PLC_1\",\"groupPath\":\"Motors/Fans\"},{\"kind\":\"units\",\"softwarePath\":\"PLC_1\"},{\"kind\":\"deviceGroup\",\"name\":\"Line 1\"}]");
            check(scope.Length == 5 && scope[0].Kind == "project" && scope[2].GroupPath == "Motors/Fans" && scope[4].Name == "Line 1" && TestSuiteLogic.ParseScopeEntries("").Length == 0, "test suite scope: official scope objects");
            check(Fails<ArgumentException>(() => TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"blocks\"}]")) && Fails<ArgumentException>(() => TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"project\",\"softwarePath\":\"PLC_1\"}]")) && Fails<ArgumentException>(() => TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"units\",\"softwarePath\":\"PLC_1\",\"groupPath\":\"x\"}]"))
                && Fails<ArgumentException>(() => TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"deviceGroup\"}]")) && Fails<ArgumentException>(() => TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"hmi\",\"softwarePath\":\"HMI_1\"}]")) && Fails<ArgumentException>(() => TestSuiteLogic.ParseScopeEntries("[{\"kind\":\"project\",\"extra\":1}]")), "test suite scope: entry gates");

            // ---- manage ----
            TestSuiteLogic.ManageRequest M(string category, string action, string name = "RS_1", string kind = "case", string newName = "", string sw = "", string instance = "", string mode = "", string opc = "", string iface = "", string folder = "", string update = "", string scopeJson = "[]", string target = "", string master = "", bool dryRun = true)
                => TestSuiteLogic.ValidateManageRequest(category, name, action, kind, newName, sw, instance, mode, opc, iface, folder, update, scopeJson, target, master, dryRun);
            check(!M("styleGuide", "read").Writes && !M("styleGuide", "showInEditor", dryRun: false).Writes && M("styleGuide", "rename", newName: "RS_2", dryRun: false).Writes && M("application", "rename", name: "Set_1", kind: "testSet", newName: "Set_2", dryRun: false).Writes, "test suite manage: read / show / rename");
            var style = M("styleGuide", "setScope", update: "Add", scopeJson: "[{\"kind\":\"project\"}]", dryRun: false);
            check(style.Writes && style.UpdateOption == "Add" && style.Scope.Length == 1 && M("styleGuide", "setScope", scopeJson: "[{\"kind\":\"project\"}]").UpdateOption == "Override", "test suite manage: style-guide scope (Override default)");
            check(M("application", "setScope", name: "TC_1", sw: "PLC_1", dryRun: false).ExecutionMode == "" && M("application", "setScope", name: "TC_1", sw: "PLC_1", instance: "Inst", mode: "ExternallyManagedPLCSIMInstance", dryRun: false).ExecutionMode == "ExternallyManagedPLCSIMInstance", "test suite manage: application scope overloads");
            var system = M("system", "setScope", name: "TC_1", opc: "opc.tcp://plc:4840/UA", iface: "StandardSIMATIC", folder: temp, dryRun: false);
            check(system.Writes && system.ServerInterface == "StandardSIMATIC" && M("system", "setScope", name: "TC_1", opc: "opc.tcp://plc:4840", iface: "UserDefined").ServerInterface == "UserDefined", "test suite manage: system scope");
            check(M("styleGuide", "copyScope", target: "RS_2", dryRun: false).Writes && M("application", "createFromMasterCopy", name: "", master: "Copies/TC", dryRun: false).Writes && M("system", "createFromMasterCopy", name: "TC_9", master: "Copies/TC", dryRun: false).Writes, "test suite manage: copyScope / master copies");
            check(Fails<ArgumentException>(() => M("styleGuide", "setScope")) && Fails<ArgumentException>(() => M("application", "setScope", name: "TC_1")) && Fails<ArgumentException>(() => M("application", "setScope", name: "TC_1", sw: "PLC_1", instance: "Inst")) && Fails<ArgumentException>(() => M("system", "setScope", name: "TC_1", opc: "http://x", iface: "UserDefined")) && Fails<ArgumentException>(() => M("system", "setScope", name: "TC_1", opc: "opc.tcp://x", iface: "UserDefined", folder: "relative/dir")), "test suite manage: scope gates");
            check(Fails<ArgumentException>(() => M("application", "copyScope", name: "TC_1", target: "TC_2")) && Fails<ArgumentException>(() => M("system", "showInEditor", name: "TC_1")) && Fails<ArgumentException>(() => M("application", "setScope", name: "Set_1", kind: "testSet", sw: "PLC_1")) && Fails<ArgumentException>(() => M("application", "createFromMasterCopy", name: "Set_1", kind: "testSet", master: "Copies/S")) && Fails<ArgumentException>(() => M("styleGuide", "rename")) && Fails<ArgumentException>(() => M("styleGuide", "read", scopeJson: "[{\"kind\":\"project\"}]")) && Fails<ArgumentException>(() => M("styleGuide", "read", sw: "PLC_1")), "test suite manage: category / kind / stray gates");
        }
    }
}
