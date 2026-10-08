using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.BehaviorParity
{
    public static class BehaviorParityCases
    {
        private static IEnumerable<object[]> Enabled => new[] {
            new object[] { "argument", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "binding-argument", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "wrapped-argument", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "alias", "", "succeeded", "completed" },
            new object[] { "exact", "", "succeeded", "completed" },
            new object[] { "missing-file", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "missing-directory", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "file-access-denied", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "file-locked", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "existing-no-overwrite", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "precondition", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "unknown", "OUTCOME_UNKNOWN", "unknown", "unknown" },
            new object[] { "native-read", "NATIVE_OPERATION_FAILED", "read-failed", "read-only" },
            new object[] { "refused-approval", "CONFIRMATION_REQUIRED", "rejected-before-operation", "not-started" },
            new object[] { "no-effect", "", "succeeded", "read-only" },
            new object[] { "inconsistent-types-preview", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "inconsistent-types-apply", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "inconsistent-blocks-preview", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "inconsistent-blocks-apply", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "batch-alias", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "ordinary-export-alias-slashes", "", "succeeded", "completed" },
            new object[] { "blocked-export-preview", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "blocked-export-apply", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "typed-export-refusal", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "cancellation", "CANCELLED", "rejected-before-operation", "not-started" },
            new object[] { "staging-name", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "staging-size", "LIMIT_EXCEEDED", "rejected-before-operation", "not-started" },
            new object[] { "staging-preview", "", "succeeded", "read-only" },
            new object[] { "staging-apply", "", "succeeded", "completed" },
            new object[] { "staging-refused", "CONFIRMATION_REQUIRED", "rejected-before-operation", "not-started" },
            new object[] { "staging-cleanup", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "batch-confirm", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "batch-order", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "batch-hash", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "batch-project", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "single-inconsistent", "", "succeeded", "completed" },
            new object[] { "single-import-failed", "OUTCOME_UNKNOWN", "unknown", "unknown" },
            new object[] { "single-native-warning", "", "succeeded", "completed" },
            new object[] { "single-legacy-group-missing", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "single-group-missing", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "single-export-refused", "", "succeeded", "completed" },
            new object[] { "batch-inconsistent", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "batch-protected", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "batch-unknown-consistency", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "compile-zero-count", "COMPILE_ERRORS", "failed", "completed" },
            new object[] { "compile-errors", "COMPILE_ERRORS", "failed", "completed" },
            new object[] { "staging-live-cleanup", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "staging-modified-cleanup", "", "succeeded", "completed" },
            new object[] { "staging-truncated-cleanup", "", "succeeded", "completed" },
            new object[] { "staging-first-delete-cleanup", "IO_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "staging-partial-cleanup", "PARTIAL_FAILURE", "partial", "partial" },
            new object[] { "staging-old-list", "", "succeeded", "read-only" },
            new object[] { "staging-old-cleanup", "", "succeeded", "completed" },
            new object[] { "staging-extra-cleanup", "", "succeeded", "completed" },
            new object[] { "batch-stale", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" }
        };

        public static IEnumerable<object[]> All => Enabled.Concat(Enabled.Where(row => new[] {
            "argument", "binding-argument", "wrapped-argument", "existing-no-overwrite", "precondition",
            "inconsistent-types-apply", "inconsistent-blocks-apply", "batch-alias", "blocked-export-apply", "typed-export-refusal",
            "single-legacy-group-missing", "single-group-missing", "staging-name", "staging-size", "staging-cleanup", "staging-live-cleanup"
        }.Contains((string)row[0])).Select(row => new object[] { row[0]+"-disabled", row[1], row[2], row[3] })).Concat(
            new[] { false,true }.SelectMany(program => new[] { "inconsistent-stale", "stale", "inconsistent", "protected", "unknown-consistency", "backup-io", "precheck-io", "inventory-io", "recheck-io", "confirm", "hash", "malformed-hash", "project", "order" }
                .Concat(program ? new[] { "compile", "continue", "technology" } : Array.Empty<string>()).Select(scenario => new object[] {
                    "disabled-"+(program ? "program-" : "batch-")+scenario,
                    new[] { "inconsistent-stale", "stale", "confirm", "hash", "malformed-hash", "project", "order" }.Contains(scenario) ? "INVALID_ARGUMENT" : "PRECONDITION_FAILED", "rejected-before-operation", "not-started" }))).Concat(new[] {
                new object[] { "single-type-group-missing-disabled","PRECONDITION_FAILED","rejected-before-operation","not-started" },
                new object[] { "single-table-group-missing-disabled","PRECONDITION_FAILED","rejected-before-operation","not-started" } });

        public static JsonObject Arguments(string scenario)
        {
            if (scenario is "single-legacy-group-missing" or "single-type-group-missing" or "single-table-group-missing")
            {
                string path = System.IO.Path.GetFullPath("bin-build/P6-68/parity-single.xml"); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, "<Document/>");
                return new JsonObject { ["softwarePath"] = "PLC_1", [scenario=="single-table-group-missing" ? "folderPath" : "groupPath"] = "Missing", ["importPath"] = path };
            }
            if (scenario.StartsWith("batch-", StringComparison.Ordinal) && scenario != "batch-alias")
            {
                string dir = System.IO.Path.GetFullPath("bin-build/P6-67r/parity-batch"); System.IO.Directory.CreateDirectory(dir);
                var batch = new JsonObject { ["softwarePath"] = "CPU/PLC_1", ["groupPath"] = "", ["dir"] = dir, ["overwrite"] = true,
                    ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap19", ["expectedPlanHash"] = new string(scenario == "batch-stale" ? 'b' : 'a', 64), ["importOrder"] = new JsonArray("A.xml") };
                string? missing = scenario == "batch-confirm" ? "confirm" : scenario == "batch-order" ? "importOrder" : scenario == "batch-hash" ? "expectedPlanHash" : scenario == "batch-project" ? "expectedProjectFile" : null;
                if (missing != null) batch.Remove(missing);
                return batch;
            }
            if (scenario.StartsWith("compile-", StringComparison.Ordinal)) return new JsonObject { ["softwarePath"] = "PLC_1", ["dryRun"] = false };
            if (scenario == "staging-cleanup") return new JsonObject { ["batchId"] = Guid.NewGuid().ToString("N"), ["dryRun"] = false };
            if (scenario.StartsWith("staging-", StringComparison.Ordinal)) return new JsonObject { ["dryRun"] = scenario == "staging-preview",
                ["files"] = new JsonArray(new JsonObject { ["fileName"] = scenario == "staging-name" ? "../bad.scl" : "Main.scl", ["kind"] = "scl", ["content"] = scenario == "staging-size" ? new string('x', 4194305) : "FUNCTION Main : Void\nBEGIN\nEND_FUNCTION" }) };
            if (scenario == "native-read") return new JsonObject { ["plc"] = "PLC_1", ["table"] = "T" };
            var args = new JsonObject { ["plc"] = scenario == "exact" ? "CPU/PLC_1" : "PLC_1", ["table"] = "T",
                ["name"] = "Ready", ["dataType"] = "Bool", ["address"] = "%M0.0", ["dryRun"] = false,
                ["confirm"] = true, ["expectedProjectFile"] = "C:/fixture.ap19" };
            if (scenario == "missing-file") return new JsonObject { ["softwarePath"] = "PLC_1", ["groupPath"] = "",
                ["filePath"] = System.IO.Path.GetFullPath("bin-build/P6-65/absent-" + Guid.NewGuid().ToString("N") + ".scl"), ["dryRun"] = true };
            if (scenario == "missing-directory") return new JsonObject { ["softwarePath"] = "PLC_1", ["groupPath"] = "",
                ["dir"] = System.IO.Path.GetFullPath("bin-build/P6-65/absent-" + Guid.NewGuid().ToString("N")), ["dryRun"] = true };
            if (scenario.EndsWith("-preview", StringComparison.Ordinal)) args["dryRun"] = true;
            if (scenario == "binding-argument") args["name"] = new JsonObject();
            return args;
        }

        public static JsonObject CompileData() => new JsonObject { ["executed"] = true, ["projectFile"] = "C:/fixture.ap19", ["state"] = "Error", ["errorCount"] = 6, ["warningCount"] = 0,
            ["errors"] = new JsonArray("State=Error; Description=The input address of the drive telegram is not set.; Path=ASIS (DB1); ErrorCount=1; WarningCount=0") };
        public static JsonObject BatchData(string scenario)
        {
            string reason = scenario == "batch-inconsistent" ? "inconsistent" : scenario == "batch-unknown-consistency" ? "unknown-consistency" : "know-how-protected";
            return new JsonObject { ["executed"] = false, ["projectFile"] = "C:/fixture.ap19", ["softwarePath"] = "CPU/PLC_1", ["release"] = "19",
                ["planHash"] = new string('a', 64), ["recursive"] = false, ["dependencyStatus"] = "unverified-caller-order-required",
                ["requiresSessionReset"] = false, ["importedCount"] = 0, ["failedCount"] = 0, ["imported"] = new JsonArray(), ["failed"] = new JsonArray(),
                ["items"] = new JsonArray(new JsonObject { ["relativePath"] = "A.xml", ["status"] = "replace-blocked", ["action"] = "replace-blocked: " + reason,
                    ["inputSha256"] = new string('a', 64), ["planned"] = new JsonObject { ["name"] = "A", ["kind"] = "FC", ["groupPath"] = "", ["number"] = 1 },
                    ["replaced"] = new JsonObject { ["name"] = "A", ["kind"] = "FC", ["groupPath"] = "", ["number"] = 1, ["backupBlocker"] = reason },
                    ["returnedObjects"] = new JsonArray(), ["failure"] = reason, ["attempted"] = false }) };
        }
        public static JsonObject SingleBackup(string scenario, bool preview)
        {
            var data = new JsonObject { ["executed"] = !preview };
            if (scenario.EndsWith("group-missing", StringComparison.Ordinal))
            {
                System.Collections.Generic.IEnumerable<TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget> Targets()
                { yield return Fail(); }
                TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget Fail() => throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact destination unavailable", "groupPath", false);
                TiaOpenness.Shared.NativeExportPolicy.SingleImportRecovery(Targets(), () => throw new Exception("Must not create directory"), "fixture");
            }
            if (preview) return data;
            var target = new TiaOpenness.Shared.NativeExportPolicy.RecoveryTarget { Object = "Group/Uncompiled", Blocker = scenario == "single-inconsistent" ? "inconsistent" : "",
                Export = _ => throw new System.IO.IOException("Native export refused.") };
            var saved = TiaOpenness.Shared.NativeExportPolicy.SingleImportRecovery(new[] { target }, () => System.IO.Path.GetFullPath("bin-build/P6-68"), "fixture");
            data["recoveryDirectory"] = saved.Directory; data["recoveryStatus"] = saved.Status; data["recoveryWarning"] = saved.Warning; data["recoverySkipped"] = JsonSerializer.SerializeToNode(saved.Skipped);
            if (scenario == "single-native-warning") data["warnings"] = new JsonArray("Separate native diagnostic");
            return data;
        }
        public static JsonObject StagingArguments(string scenario, TiaMcp.Logic.ModelContextProtocol.ImportStagingStore store, string bundle, string release)
        {
            if (scenario is not ("staging-old-list" or "staging-old-cleanup" or "staging-extra-cleanup" or "staging-live-cleanup" or "staging-modified-cleanup" or "staging-truncated-cleanup" or "staging-first-delete-cleanup" or "staging-partial-cleanup")) return Arguments(scenario);
            var old = new TiaMcp.Logic.ModelContextProtocol.ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N"));
            var batch = old.Stage(new[] { new TiaMcp.Logic.ModelContextProtocol.StagedTextFile { FileName = "F.scl", Kind = "scl", Content = "FUNCTION F : Void\nBEGIN\nEND_FUNCTION" }, new TiaMcp.Logic.ModelContextProtocol.StagedTextFile { FileName = "G.scl", Kind = "scl", Content = "FUNCTION G : Void\nBEGIN\nEND_FUNCTION" } }, false);
            if (scenario != "staging-live-cleanup") store.OwnerAliveForTests = (_, _) => false;
            if (scenario == "staging-modified-cleanup") System.IO.File.WriteAllText(System.IO.Path.Combine((string)batch["directory"]!, "F.scl"), "caller replacement");
            if (scenario == "staging-truncated-cleanup") System.IO.File.WriteAllText(System.IO.Path.Combine((string)batch["directory"]!, ".staging-batch.json"), "{");
            if (scenario == "staging-first-delete-cleanup") store.BeforeDeleteForTests = _ => throw new System.IO.IOException("Fixture deletion failure");
            if (scenario == "staging-partial-cleanup") { int deletes = 0; store.BeforeDeleteForTests = _ => { if (++deletes == 2) throw new ArgumentException("Fixture safety guard changed"); }; }
            if (scenario == "staging-extra-cleanup") System.IO.File.WriteAllText(System.IO.Path.Combine((string)batch["directory"]!, "caller.xml"), "caller export");
            return scenario == "staging-old-list" ? new JsonObject() : new JsonObject { ["batchId"] = batch["batchId"]!.DeepClone(), ["dryRun"] = false };
        }
        public static string StagingTool(string scenario) => scenario == "staging-old-list" ? "ListStagedImportFiles"
            : scenario.Contains("cleanup") ? "CleanupStagedImportFiles" : "StageImportFiles";
        public static void ExportAdmission(string scenario)
        {
            if (scenario.StartsWith("inconsistent-", StringComparison.Ordinal))
                TiaOpenness.Shared.NativeExportPolicy.RequireConsistent(scenario.Contains("types") ? "types" : "blocks", new[] { "group/Uncompiled" }, "groupPath");
            if (scenario == "batch-alias") TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath("PLC_1", "CPU/PLC_1", true);
            if (scenario == "ordinary-export-alias-slashes")
            {
                TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath("PLC_1", "CPU/PLC_1", false);
                string path = System.IO.Path.GetFullPath("bin-build/P6-65/export.xml");
                if (TiaOpenness.Shared.NativeInputPolicy.FullPath(path.Replace('\\', '/')) != path) throw new Exception("Output path differs.");
            }
            if (scenario == "typed-export-refusal") TiaOpenness.Shared.NativeExportPolicy.RequireApply("inconsistent");
        }

        public static JsonObject Project(JsonNode body)
            => new JsonObject { ["code"] = (string?)body["error"]?["code"] ?? "", ["outcome"] = (string?)body["meta"]?["outcome"],
                ["execution"] = (string?)body["meta"]?["execution"], ["reset"] = (bool?)body["meta"]?["requiresSessionReset"],
                ["compileErrorCount"] = (int?)body["error"]?["details"]?["errorCount"],
                ["details"] = Shape(body["error"]?["details"]), ["applyBlocked"] = (bool?)body["data"]?["applyBlocked"],
                ["applyBlockedReason"] = (string?)body["data"]?["applyBlockedReason"],
                ["recoveryStatus"] = (string?)body["data"]?["recoveryStatus"], ["folderRetained"] = (bool?)body["data"]?["folderRetained"],
                ["unknownEntries"] = body["data"]?["unknownEntries"]?.DeepClone(),
                ["backupWarning"] = (string?)body["meta"]?["warnings"]?.AsArray().FirstOrDefault(w => (string?)w?["code"] == "BACKUP_SKIPPED")?["message"],
                ["backupDetails"] = body["meta"]?["warnings"]?.AsArray().FirstOrDefault(w => (string?)w?["code"] == "BACKUP_SKIPPED")?["details"]?.DeepClone(),
                ["nativeWarning"] = body["meta"]?["warnings"]?.AsArray().Any(w => (string?)w?["code"] == "NATIVE_WARNING") ?? false,
                ["precheckWarning"] = body["meta"]?["warnings"]?.AsArray().FirstOrDefault(w => (string?)w?["code"] == "APPROVAL_PRECHECK_REFUSED")?.DeepClone(),
                ["stagingWarning"] = body["meta"]?["warnings"]?.AsArray().FirstOrDefault(w => (string?)w?["code"] == "STAGING_FOLDER_RETAINED")?.DeepClone(),
                ["hint"] = (string?)body["meta"]?["warnings"]?.AsArray().FirstOrDefault(w => (string?)w?["code"] == "RECOVERY_GUIDANCE")?["message"] ?? "" };

        private static JsonNode? Shape(JsonNode? node)
        {
            if (node is JsonObject obj) return new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, Shape(p.Value))));
            if (node is JsonArray rows) return new JsonArray(rows.Select(Shape).ToArray());
            if (node == null) return null;
            using (var document = JsonDocument.Parse(node.ToJsonString())) return JsonValue.Create(document.RootElement.ValueKind.ToString());
        }
    }
}
