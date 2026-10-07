using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.BehaviorParity
{
    public static class BehaviorParityCases
    {
        public static IEnumerable<object[]> All => new[] {
            new object[] { "argument", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "binding-argument", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "wrapped-argument", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "alias", "", "succeeded", "completed" },
            new object[] { "exact", "", "succeeded", "completed" },
            new object[] { "missing-file", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "missing-directory", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
            new object[] { "file-access-denied", "INVALID_ARGUMENT", "rejected-before-operation", "not-started" },
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
            new object[] { "blocked-export-preview", "", "succeeded", "read-only" },
            new object[] { "blocked-export-apply", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "typed-export-refusal", "PRECONDITION_FAILED", "rejected-before-operation", "not-started" },
            new object[] { "cancellation", "CANCELLED", "rejected-before-operation", "not-started" }
        };

        public static JsonObject Arguments(string scenario)
        {
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
                ["details"] = Shape(body["error"]?["details"]), ["applyBlocked"] = (bool?)body["data"]?["applyBlocked"],
                ["applyBlockedReason"] = (string?)body["data"]?["applyBlockedReason"],
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
