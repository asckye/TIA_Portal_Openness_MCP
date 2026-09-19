using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 4 sub-batch 2 (2.7.35) pure logic: external source requests (files, master copies, user groups, block generation),
    // system group / constant requests, alarm text list XLSX requests, watch / force table entry requests and the ProDiag gate.
    internal static class Step7LeftoversTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        private static string Message(Action a) { try { a(); return ""; } catch (Exception ex) { return ex.Message; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string scl = Path.Combine(temp, "Block_1.scl"), xlsx = Path.Combine(temp, "lists.xlsx"), csvDir = Path.Combine(temp, "prodiag");

            // ---- external sources ----
            bool E(string action, string name = "", string unitName = "", string unitKind = "unit", string filePath = "", string libraryName = "", string masterCopyPath = "", string copyMode = "",
                string generateOption = "None", string targetKind = "", string targetGroupPath = "", string newName = "", bool confirmDelete = false, bool dryRun = true)
                => Step7LeftoversLogic.ValidateExternalSourceRequest(action, name, unitName, unitKind, filePath, libraryName, masterCopyPath, copyMode, generateOption, targetKind, targetGroupPath, newName, confirmDelete, dryRun);
            check(!E("list") && !E("read", "Block_1.scl", dryRun: false), "extsrc: list / read never write");
            check(E("createFromFile", "Block_1.scl", filePath: scl, dryRun: false), "extsrc: createFromFile with dryRun=false writes");
            check(!E("createFromFile", "Block_1.scl", filePath: scl), "extsrc: createFromFile preview does not write");
            check(E("createFromMasterCopy", "Block_1.scl", libraryName: "Lib", masterCopyPath: "Sources/Block_1.scl", copyMode: "Rename", dryRun: false), "extsrc: createFromMasterCopy accepted");
            check(E("delete", "Block_1.scl", confirmDelete: true, dryRun: false), "extsrc: delete with confirmation writes");
            check(E("generateBlocks", "Block_1.scl", generateOption: "KeepOnError", dryRun: false), "extsrc: generateBlocks without target");
            check(E("generateBlocks", "Block_1.scl", targetKind: "type", targetGroupPath: "Types/Sub", dryRun: false), "extsrc: generateBlocks into a type user group");
            check(E("createGroup", newName: "Sources", dryRun: false) && E("renameGroup", "Sources", newName: "Sources2", dryRun: false) && E("deleteGroup", "Sources2", confirmDelete: true, dryRun: false), "extsrc: group actions accepted");
            check(E("createGroup", unitName: "Unit_1", unitKind: "safety", newName: "G", dryRun: false), "extsrc: unit scope accepted");
            check(Fails<ArgumentException>(() => E("import")), "extsrc: unknown action refused");
            check(Fails<ArgumentException>(() => E("list", "x")), "extsrc: name on list refused");
            check(Fails<ArgumentException>(() => E("read")), "extsrc: read without name refused");
            check(Fails<ArgumentException>(() => E("createGroup", "x", newName: "G")), "extsrc: name on createGroup refused");
            check(Fails<ArgumentException>(() => E("createGroup")), "extsrc: createGroup without newName refused");
            check(Fails<ArgumentException>(() => E("read", "x", newName: "y")), "extsrc: newName outside group actions refused");
            check(Fails<ArgumentException>(() => E("createFromFile", "Block_1.scl")), "extsrc: createFromFile without filePath refused");
            check(Fails<ArgumentException>(() => E("createFromFile", "Block_1.scl", filePath: "relative/Block_1.scl")), "extsrc: relative filePath refused");
            check(Fails<ArgumentException>(() => E("createFromFile", "Block_1.xml", filePath: Path.Combine(temp, "Block_1.xml"))), "extsrc: non-ASCII-source extension refused");
            check(Fails<ArgumentException>(() => E("read", "x", filePath: scl)), "extsrc: filePath outside createFromFile refused");
            check(Fails<ArgumentException>(() => E("createFromMasterCopy", "x")), "extsrc: createFromMasterCopy without masterCopyPath refused");
            check(Fails<ArgumentException>(() => E("createFromMasterCopy", "x", masterCopyPath: "F/x", copyMode: "Overwrite")), "extsrc: unknown copyMode refused");
            check(Fails<ArgumentException>(() => E("read", "x", libraryName: "Lib")), "extsrc: libraryName outside createFromMasterCopy refused");
            check(Fails<ArgumentException>(() => E("generateBlocks", "x", generateOption: "Force")), "extsrc: unknown generateOption refused");
            check(Fails<ArgumentException>(() => E("generateBlocks", "x", targetKind: "tag", targetGroupPath: "G")), "extsrc: unknown targetKind refused");
            check(Fails<ArgumentException>(() => E("generateBlocks", "x", targetKind: "block")), "extsrc: targetKind without targetGroupPath refused");
            check(Fails<ArgumentException>(() => E("read", "x", generateOption: "KeepOnError")), "extsrc: generateOption outside generateBlocks refused");
            check(Fails<ArgumentException>(() => E("read", "x", targetGroupPath: "G")), "extsrc: targetGroupPath outside generateBlocks refused");
            check(Fails<ArgumentException>(() => E("delete", "x", dryRun: false)), "extsrc: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => E("deleteGroup", "x", dryRun: false)), "extsrc: real deleteGroup without confirmDelete refused");
            check(Fails<ArgumentException>(() => E("list", unitName: "U", unitKind: "system")), "extsrc: unknown unitKind refused");
            check(Step7LeftoversLogic.ExternalSourceActions.Length == 9 && Step7LeftoversLogic.GenerateBlockOptions.SequenceEqual(new[] { "None", "KeepOnError" }) && Step7LeftoversLogic.ExternalSourceExtensions.Contains(".udt"), "extsrc: catalogs");

            // ---- system groups / constants ----
            Step7LeftoversLogic.ValidateSystemGroupRequest("", "unit", 4); Step7LeftoversLogic.ValidateSystemGroupRequest("Unit_1", "safety", 16);
            check(true, "sysgroups: requests accepted");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateSystemGroupRequest("", "unit", 0)), "sysgroups: maxDepth 0 refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateSystemGroupRequest("", "unit", 17)), "sysgroups: maxDepth 17 refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateSystemGroupRequest("U", "other", 4)), "sysgroups: unknown unitKind refused");
            Step7LeftoversLogic.ValidateConstantRequest("Default tag table", "all", "", "unit", 0, 200); Step7LeftoversLogic.ValidateConstantRequest("Group/Table", "system", "Unit_1", "unit", 10, 500);
            check(true, "constants: requests accepted");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateConstantRequest("", "all", "", "unit", 0, 200)), "constants: empty tablePath refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateConstantRequest("T", "constants", "", "unit", 0, 200)), "constants: unknown kind refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateConstantRequest("T", "all", "", "unit", 0, 501)), "constants: limit above 500 refused");

            // ---- alarm text lists XLSX ----
            var exportAll = Step7LeftoversLogic.ValidateXlsxRequest("export", xlsx, "", "unit", "[]", "[]", "None", false, false);
            check(!exportAll.Writing && exportAll.TextLists.Length == 0 && exportAll.Cultures.Length == 0, "xlsx: unfiltered export is not a project write");
            var filtered = Step7LeftoversLogic.ValidateXlsxRequest("export", xlsx, "Unit_1", "unit", "[\"List_1\",\"List_2\"]", "[\"en-US\"]", "", false, true);
            check(filtered.TextLists.Length == 2 && filtered.Cultures.SequenceEqual(new[] { "en-US" }), "xlsx: filtered export carries text lists and cultures");
            check(Step7LeftoversLogic.ValidateXlsxRequest("import", xlsx, "", "unit", "[]", "[]", "Override", true, false).Writing, "xlsx: import with confirmation writes");
            check(!Step7LeftoversLogic.ValidateXlsxRequest("import", xlsx, "", "unit", "[]", "[]", "None", false, true).Writing, "xlsx: import preview does not write");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("export", Path.Combine(temp, "lists.csv"), "", "unit", "[]", "[]", "None", false, true)), "xlsx: non-.xlsx path refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("export", "lists.xlsx", "", "unit", "[]", "[]", "None", false, true)), "xlsx: relative path refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("export", xlsx, "", "unit", "[\"List_1\"]", "[]", "None", false, true)), "xlsx: text lists without cultures refused (native overload needs both)");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("export", xlsx, "", "unit", "[\"A\",\"A\"]", "[\"en-US\"]", "None", false, true)), "xlsx: duplicate text list names refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("export", xlsx, "", "unit", "[1]", "[\"en-US\"]", "None", false, true)), "xlsx: non-string list entries refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("export", xlsx, "", "unit", "[]", "[]", "Override", false, true)), "xlsx: importOption on export refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("import", xlsx, "", "unit", "[\"A\"]", "[\"en-US\"]", "None", false, true)), "xlsx: filters on import refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("import", xlsx, "", "unit", "[]", "[]", "Merge", false, true)), "xlsx: unknown importOption refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("import", xlsx, "", "unit", "[]", "[]", "None", false, false)), "xlsx: real import without confirmImport refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateXlsxRequest("sync", xlsx, "", "unit", "[]", "[]", "None", false, true)), "xlsx: unknown action refused");

            // ---- watch / force table entries ----
            bool T(string tableKind, string action, int entryIndex = -1, bool confirmDelete = false, bool dryRun = true) => Step7LeftoversLogic.ValidateTableEntryRequest(tableKind, "Group/Table_1", action, entryIndex, confirmDelete, dryRun, 0, 200);
            check(!T("watch", "read") && !T("force", "read", dryRun: false), "entries: read never writes");
            check(!T("watch", "createComment") && T("watch", "createComment", dryRun: false), "entries: createComment preview / write");
            check(T("watch", "deleteEntry", 3, true, false) && !T("watch", "deleteEntry", 3), "entries: deleteEntry with confirmation writes, preview does not");
            check(Fails<ArgumentException>(() => T("force", "createComment", dryRun: false)), "entries: force tables are read-only (deliberate)");
            check(Fails<ArgumentException>(() => T("force", "deleteEntry", 0, true, false)), "entries: force table entry deletion refused");
            check(Fails<ArgumentException>(() => T("watch", "deleteEntry")), "entries: deleteEntry without entryIndex refused");
            check(Fails<ArgumentException>(() => T("watch", "read", 2)), "entries: entryIndex outside deleteEntry refused");
            check(Fails<ArgumentException>(() => T("watch", "deleteEntry", 1, false, false)), "entries: real deletion without confirmDelete refused");
            check(Fails<ArgumentException>(() => T("trace", "read")), "entries: unknown tableKind refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateTableEntryRequest("watch", "", "read", -1, false, true, 0, 200)), "entries: empty tablePath refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateTableEntryRequest("watch", "T", "read", -1, false, true, -1, 200)), "entries: negative offset refused");

            // ---- ProDiag ----
            Step7LeftoversLogic.ValidateProDiagRequest("Diag/ProDiag_FB", csvDir, "", "unit");
            check(true, "prodiag: request accepted");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateProDiagRequest("", csvDir, "", "unit")), "prodiag: empty blockPath refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateProDiagRequest("FB", "relative/dir", "", "unit")), "prodiag: relative directory refused");
            check(Fails<ArgumentException>(() => Step7LeftoversLogic.ValidateProDiagRequest("FB", csvDir, "U", "x")), "prodiag: unknown unitKind refused");
            check(Step7LeftoversLogic.ProDiagRefusal("ProDiag", true) == null, "prodiag: consistent ProDiag FB accepted");
            check(Message(() => { var r = Step7LeftoversLogic.ProDiagRefusal("SCL", true); if (r != null) throw new InvalidOperationException(r); }).Contains("SCL"), "prodiag: non-ProDiag language refused with the language named");
            check(Step7LeftoversLogic.ProDiagRefusal("ProDiag", false) != null, "prodiag: inconsistent block refused");
        }
    }
}
