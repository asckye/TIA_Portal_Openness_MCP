using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 4 sub-batch 1 (2.7.34) pure logic: software unit requests (standard / safety), document requests (named value
    // type / UDT), fingerprint requests, block write protection state machine, project compilation settings and the
    // directory snapshot helpers.
    internal static class SoftwareUnitDeepTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        private static string Message(Action a) { try { a(); return ""; } catch (Exception ex) { return ex.Message; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            var dir = Path.Combine(temp, "mcp-units-" + Guid.NewGuid().ToString("N"));

            // ---- software units ----
            var list = SoftwareUnitDeepLogic.ValidateUnitRequest("list", "unit", "", "", "", "{}", "{}", "", "", "", true);
            check(!list.Writing && list.Properties.Count == 0, "units: list is read-only");
            var read = SoftwareUnitDeepLogic.ValidateUnitRequest("read", "safety", "SafetyUnit", "", "", "{}", "{}", "", "", "", false);
            check(!read.Writing, "units: read never writes even with dryRun=false");
            var create = SoftwareUnitDeepLogic.ValidateUnitRequest("create", "unit", "Unit_1", "", "", "{}", "{}", "", "", "", false);
            check(create.Writing, "units: create with dryRun=false writes");
            check(!SoftwareUnitDeepLogic.ValidateUnitRequest("create", "unit", "Unit_1", "", "", "{}", "{}", "", "", "", true).Writing, "units: create preview does not write");
            var fromCopy = SoftwareUnitDeepLogic.ValidateUnitRequest("createFromMasterCopy", "unit", "Unit_2", "", "", "{}", "{}", "Lib", "Folder/Unit_2", "Rename", false);
            check(fromCopy.Writing, "units: createFromMasterCopy accepts libraryName / masterCopyPath / copyMode");
            var update = SoftwareUnitDeepLogic.ValidateUnitRequest("update", "unit", "Unit_1", "", "", "{\"Author\":\"me\",\"NamespacePreset\":\"Plant.Line\"}", "{\"en-US\":\"hello\",\"de-DE\":\"hallo\"}", "", "", "", false);
            check(update.Writing && update.Properties.Count == 2 && update.Comments.Length == 2 && update.Comments[1].Culture == "de-DE" && update.Comments[1].Text == "hallo", "units: update carries scalars and per-culture comments");
            var relation = SoftwareUnitDeepLogic.ValidateUnitRequest("createRelation", "safety", "SafetyUnit", "Unit_1", "SoftwareUnit", "{}", "{}", "", "", "", false);
            check(relation.Writing, "units: safety unit relations are allowed");
            check(SoftwareUnitDeepLogic.ValidateUnitRequest("deleteRelation", "unit", "Unit_1", "DB_1", "", "{}", "{}", "", "", "", false).Writing, "units: deleteRelation needs relatedUnit only");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("rename", "unit", "U", "", "", "{}", "{}", "", "", "", true)), "units: unknown action refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "system", "U", "", "", "{}", "{}", "", "", "", true)), "units: unknown unitKind refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("create", "safety", "SafetyUnit", "", "", "{}", "{}", "", "", "", true)), "units: safety unit create refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("delete", "safety", "SafetyUnit", "", "", "{}", "{}", "", "", "", false)), "units: safety unit delete refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("createFromMasterCopy", "safety", "S", "", "", "{}", "{}", "", "F/S", "", false)), "units: safety unit createFromMasterCopy refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "unit", "", "", "", "{}", "{}", "", "", "", true)), "units: read without name refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("list", "unit", "Unit_1", "", "", "{}", "{}", "", "", "", true)), "units: name on list refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "unit", " Unit_1", "", "", "{}", "{}", "", "", "", true)), "units: surrounding whitespace in name refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("createRelation", "unit", "U", "", "SoftwareUnit", "{}", "{}", "", "", "", true)), "units: createRelation without relatedUnit refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("createRelation", "unit", "U", "V", "GlobalDB", "{}", "{}", "", "", "", true)), "units: unknown relationType refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("deleteRelation", "unit", "U", "V", "SoftwareUnit", "{}", "{}", "", "", "", true)), "units: relationType on deleteRelation refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "unit", "U", "V", "", "{}", "{}", "", "", "", true)), "units: relatedUnit outside relation actions refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("createFromMasterCopy", "unit", "U", "", "", "{}", "{}", "", "", "", true)), "units: createFromMasterCopy without masterCopyPath refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("createFromMasterCopy", "unit", "U", "", "", "{}", "{}", "", "F/U", "Overwrite", true)), "units: unknown copyMode refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("create", "unit", "U", "", "", "{}", "{}", "", "F/U", "", true)), "units: masterCopyPath on create refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("create", "unit", "U", "", "", "{}", "{}", "Lib", "", "", true)), "units: libraryName outside createFromMasterCopy refused");
            check(Message(() => SoftwareUnitDeepLogic.ValidateUnitRequest("update", "unit", "U", "", "", "{\"Name\":\"X\"}", "{}", "", "", "", true)).Contains("reference-impact"), "units: Name update refused with the review hint");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("update", "unit", "U", "", "", "{\"Comment\":\"x\"}", "{}", "", "", "", true)), "units: Comment scalar refused (use commentsJson)");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("update", "unit", "U", "", "", "{\"Author\":5}", "{}", "", "", "", true)), "units: non-string scalar refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("update", "unit", "U", "", "", "{}", "{\"en-US\":true}", "", "", "", true)), "units: non-string comment refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("update", "unit", "U", "", "", "{}", "{}", "", "", "", true)), "units: empty update refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "unit", "U", "", "", "{\"Author\":\"a\"}", "{}", "", "", "", true)), "units: propertiesJson outside update refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "unit", "U", "", "", "{}", "{\"en-US\":\"x\"}", "", "", "", true)), "units: commentsJson outside update refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateUnitRequest("read", "unit", "U", "", "", "[]", "{}", "", "", "", true)), "units: propertiesJson must be an object");
            check(SoftwareUnitDeepLogic.UnitActions.Length == 8 && SoftwareUnitDeepLogic.UnitRelationTypes.SequenceEqual(new[] { "SoftwareUnit", "NonUnitDB", "TODB" }) && SoftwareUnitDeepLogic.MasterCopyModes.Length == 3 && SoftwareUnitDeepLogic.UpdatePathsModes.Length == 3, "units: official enum catalogs");

            // ---- documents ----
            check(!SoftwareUnitDeepLogic.ValidateDocumentRequest("list", "document", "", "", "unit", "", "Override", "", "", "", "", "", "", true), "documents: list is read-only");
            check(!SoftwareUnitDeepLogic.ValidateDocumentRequest("read", "type", "UDT_1", "Unit_1", "unit", "", "Override", "", "", "", "", "", "", false), "documents: read never writes");
            check(!SoftwareUnitDeepLogic.ValidateDocumentRequest("export", "document", "NVT_1", "", "unit", dir, "Override", "", "", "", "", "", "", false), "documents: export is not a project write");
            check(SoftwareUnitDeepLogic.ValidateDocumentRequest("import", "type", "UDT_1", "", "unit", dir, "ActivateInactiveCultures", "", "", "", "", "", "", false), "documents: import with dryRun=false writes");
            check(!SoftwareUnitDeepLogic.ValidateDocumentRequest("import", "type", "UDT_1", "", "unit", dir, "None", "", "", "", "", "", "", true), "documents: import preview does not write");
            check(SoftwareUnitDeepLogic.ValidateDocumentRequest("createFromMasterCopy", "document", "NVT_1", "SafetyUnit", "safety", "", "Override", "", "Folder/NVT_1", "Replace", "", "", "", false), "documents: createFromMasterCopy accepted");
            check(SoftwareUnitDeepLogic.ValidateDocumentRequest("createFromLibraryType", "type", "UDT_1", "", "unit", "", "Override", "Lib", "", "", "Types/UDT_1", "1.0.0", "ThrowIfPathsConflict", false), "documents: createFromLibraryType accepted");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("delete", "document", "N", "", "unit", "", "Override", "", "", "", "", "", "", true)), "documents: unknown action refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("list", "block", "", "", "unit", "", "Override", "", "", "", "", "", "", true)), "documents: unknown objectKind refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("list", "document", "", "Unit_1", "system", "", "Override", "", "", "", "", "", "", true)), "documents: unknown unitKind refused when a unit is named");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("list", "document", "N", "", "unit", "", "Override", "", "", "", "", "", "", true)), "documents: name on list refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("export", "document", "", "", "unit", dir, "Override", "", "", "", "", "", "", true)), "documents: export without name refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("export", "document", "N", "", "unit", "", "Override", "", "", "", "", "", "", true)), "documents: export without directoryPath refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("export", "document", "N", "", "unit", "relative/dir", "Override", "", "", "", "", "", "", true)), "documents: relative directoryPath refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("read", "document", "N", "", "unit", dir, "Override", "", "", "", "", "", "", true)), "documents: directoryPath outside export / import refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("import", "document", "N", "", "unit", dir, "Overwrite", "", "", "", "", "", "", true)), "documents: unknown importOption refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("export", "document", "N", "", "unit", dir, "None", "", "", "", "", "", "", true)), "documents: importOption outside import refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("createFromMasterCopy", "document", "N", "", "unit", "", "Override", "", "", "", "", "", "", true)), "documents: createFromMasterCopy without masterCopyPath refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("createFromLibraryType", "document", "N", "", "unit", "", "Override", "", "", "", "T/N", "", "", true)), "documents: createFromLibraryType without version refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("createFromLibraryType", "document", "N", "", "unit", "", "Override", "", "", "", "T/N", "1.0.0", "Merge", true)), "documents: unknown updatePathsMode refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("read", "document", "N", "", "unit", "", "Override", "", "", "", "T/N", "", "", true)), "documents: typePath outside createFromLibraryType refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateDocumentRequest("import", "document", "N", "", "unit", dir, "Override", "Lib", "", "", "", "", "", true)), "documents: libraryName outside the create actions refused");
            check(SoftwareUnitDeepLogic.ImportDocumentOptionNames.SequenceEqual(new[] { "None", "Override", "SkipInactiveCultures", "ActivateInactiveCultures" }) && SoftwareUnitDeepLogic.DocumentActions.Length == 6, "documents: option catalog / actions");

            // ---- directory snapshot / file rows ----
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "old.txt"), "old");
                var before = SoftwareUnitDeepLogic.SnapshotDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "new.nvt"), "content");
                var created = SoftwareUnitDeepLogic.NewFilesSince(dir, before);
                check(created.Count == 1 && created[0]!["path"]!.ToString().EndsWith("new.nvt") && created[0]!["bytes"]!.GetValue<long>() == 7 && created[0]!["sha256"]!.ToString().Length == 64, "documents: NewFilesSince reports only the new file with size and SHA-256");
                var missing = SoftwareUnitDeepLogic.FileRow(new FileInfo(Path.Combine(dir, "absent.nvt")));
                check(!missing["exists"]!.GetValue<bool>() && missing["sha256"] == null, "documents: FileRow tolerates a missing file");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // ---- fingerprints ----
            SoftwareUnitDeepLogic.ValidateFingerprintRequest("block", "Group/FB_1", "", "unit");
            SoftwareUnitDeepLogic.ValidateFingerprintRequest("type", "UDT_1", "Unit_1", "safety");
            check(true, "fingerprints: block / type requests accepted");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateFingerprintRequest("tagTable", "T", "", "unit")), "fingerprints: tag tables refused (no FingerprintProvider natively)");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateFingerprintRequest("block", "", "", "unit")), "fingerprints: empty objectPath refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateFingerprintRequest("block", "FB_1", "Unit_1", "other")), "fingerprints: unknown unitKind refused");
            check(SoftwareUnitDeepLogic.FingerprintIds.Length == 12 && SoftwareUnitDeepLogic.FingerprintIds.Contains("ProgramCode"), "fingerprints: FingerprintId catalog (12 official values)");

            // ---- block write protection ----
            check(!SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("read", "", "", false, true), "writeprot: read takes no password");
            check(!SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("define", "pw", "", false, true), "writeprot: define preview does not write");
            check(SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("define", "pw", "", true, false), "writeprot: define with confirmation writes");
            check(SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("protect", "pw", "", true, false) && SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("unprotect", "pw", "", true, false), "writeprot: protect / unprotect write");
            check(SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("change", "pw", "pw2", true, false), "writeprot: change needs both passwords");
            check(SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("remove", "pw", "", true, false), "writeprot: remove needs the current password only");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("read", "pw", "", false, true)), "writeprot: password on read refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("protect", "", "", true, false)), "writeprot: protect without password refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("change", "pw", "", true, false)), "writeprot: change without newPassword refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("define", "pw", "pw2", true, false)), "writeprot: newPassword outside change refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("define", "pw", "", false, false)), "writeprot: real change without confirmProtectionChange refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateWriteProtectionRequest("lock", "pw", "", true, false)), "writeprot: unknown action refused");
            check(SoftwareUnitDeepLogic.WriteProtectionStateRefusal("define", false, false) == null && SoftwareUnitDeepLogic.WriteProtectionStateRefusal("define", true, false) != null, "writeprot: define only when no password is defined");
            check(SoftwareUnitDeepLogic.WriteProtectionStateRefusal("protect", true, false) == null && SoftwareUnitDeepLogic.WriteProtectionStateRefusal("protect", false, false) != null && SoftwareUnitDeepLogic.WriteProtectionStateRefusal("protect", true, true) != null, "writeprot: protect needs a defined password and an unprotected block");
            check(SoftwareUnitDeepLogic.WriteProtectionStateRefusal("unprotect", true, true) == null && SoftwareUnitDeepLogic.WriteProtectionStateRefusal("unprotect", true, false) != null, "writeprot: unprotect only when protected");
            check(SoftwareUnitDeepLogic.WriteProtectionStateRefusal("change", true, true) == null && SoftwareUnitDeepLogic.WriteProtectionStateRefusal("remove", false, false) != null && SoftwareUnitDeepLogic.WriteProtectionStateRefusal("read", false, false) == null, "writeprot: change / remove need a defined password; read never refused");

            // ---- project compilation settings ----
            var readSettings = SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("read", "{}", true);
            check(!readSettings.Writing && readSettings.Values.Count == 0, "compile settings: read");
            var updateSettings = SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("update", "{\"IsSimulationDuringBlockCompilationEnabled\":true,\"IsVirtualPlcDuringBlockCompilationEnabled\":false}", false);
            check(updateSettings.Writing && updateSettings.Values.Count == 2 && updateSettings.Values["IsSimulationDuringBlockCompilationEnabled"] && !updateSettings.Values["IsVirtualPlcDuringBlockCompilationEnabled"], "compile settings: update carries both booleans");
            check(!SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("update", "{\"IsSimulationDuringBlockCompilationEnabled\":true}", true).Writing, "compile settings: update preview does not write");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("update", "{}", false)), "compile settings: empty update refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("update", "{\"IsSimulationDuringBlockCompilationEnabled\":\"yes\"}", false)), "compile settings: non-boolean refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("update", "{\"SimulationSupport\":true}", false)), "compile settings: unknown key refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("read", "{\"IsSimulationDuringBlockCompilationEnabled\":true}", true)), "compile settings: propertiesJson on read refused");
            check(Fails<ArgumentException>(() => SoftwareUnitDeepLogic.ValidateCompilationSettingsRequest("toggle", "{}", true)), "compile settings: unknown action refused");
        }
    }
}
