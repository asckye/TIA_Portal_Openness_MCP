using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class CompileContract : IBehaviorCandidateContract
    {
        public const string Description = "[EXECUTE] Compile candidate with offlinePolicy=require. Preview reports the exact compiler target, capability, offline prerequisite and Safety permission. Apply requires confirmation, expectedPlanHash and expectedProjectFile. Never goes offline automatically. password is optional for Safety; only a login created by this invocation is ended. Full compiler diagnostic tree and root/leaf counts are retained; cleanup failures remain visible. Unknown native outcome requires session reset; no retry. Test/accepted behaviorPolicy=safe-v4.";
        public static readonly string[] Entries = { "CompilePlcSoftware", "CompilePlcDiagnostics", "CompileDevice", "CompileHmiDiagnostics" };
        protected virtual string Entry => "CompilePlcSoftware";
        public JsonElement InputSchema => Schema(Entry);
        public static JsonElement Schema(string entry)
        {
            var p = new JsonObject(); var required = new JsonArray();
            void Text(string key, string value = "") => p[key] = new JsonObject { ["type"] = "string", ["default"] = value };
            if (entry == "CompileDevice")
            {
                p["devicePath"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1 }; required.Add("devicePath");
                p["itemPath"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["default"] = new JsonArray() };
            }
            else Text("softwarePath");
            if (entry == "CompilePlcSoftware" || entry == "CompilePlcDiagnostics") Text("password");
            p["offlinePolicy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("require"), ["default"] = "require" };
            p["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("preview", "apply"), ["default"] = "preview" };
            p["confirm"] = new JsonObject { ["type"] = "boolean", ["default"] = false }; Text("expectedPlanHash"); Text("expectedProjectFile");
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = p, ["required"] = required });
        }
    }
    public sealed class CompilePlcDiagnosticsContract : CompileContract { protected override string Entry => "CompilePlcDiagnostics"; }
    public sealed class CompileHardwareContract : CompileContract { protected override string Entry => "CompileDevice"; }
    public sealed class CompileHmiContract : CompileContract { protected override string Entry => "CompileHmiDiagnostics"; }
}
