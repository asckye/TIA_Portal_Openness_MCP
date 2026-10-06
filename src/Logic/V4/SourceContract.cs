using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class SourceContract : IBehaviorCandidateContract
    {
        public const string Description = "[PLC-Software] Exact PLC and external-source candidate. Empty softwarePath selects only a unique PLC. Query paths and source names (including extensions) are literal. Preview lists files, source identities and native calls. Apply requires confirmation, expectedPlanHash and expectedProjectFile. overwrite=false, onError=stop, missingPolicy=reject. One native call per item; stop on failure, unknown requires session reset, no replay. Generation requires a source imported by this candidate session with verified collision names; no compile or save. Test/accepted behaviorPolicy=safe-v4.";
        public static readonly string[] Entries = { "ListPlcExternalSources", "PlanPlcExternalSourceImport", "ImportPlcExternalSource", "GenerateBlocksFromExternalSource", "DeletePlcExternalSource", "ManagePlcExternalSources" };
        protected virtual string Entry => "ImportPlcExternalSource";
        public JsonElement InputSchema => Schema(Entry);
        public static JsonElement Schema(string entry)
        {
            var p = new JsonObject(); var required = new JsonArray();
            void Text(string key, bool needed = false, string value = "") { p[key] = new JsonObject { ["type"] = "string" }; if (needed) required.Add(key); else p[key]!["default"] = value; }
            void Flag(string key) => p[key] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            Text("softwarePath"); Text("groupPath");
            bool read = entry == "ListPlcExternalSources";
            if (!read)
            {
                Text("sourceName", entry == "ImportPlcExternalSource" || entry == "GenerateBlocksFromExternalSource" || entry == "DeletePlcExternalSource");
                if (entry == "ImportPlcExternalSource" || entry == "PlanPlcExternalSourceImport" || entry == "ManagePlcExternalSources") Text("filePath", entry != "ManagePlcExternalSources");
                if (entry == "PlanPlcExternalSourceImport") Text("allowedFilePath", true);
                if (entry == "ManagePlcExternalSources") { p["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("list", "read", "createFromFile", "generateBlocks", "delete") }; required.Add("action"); }
                Flag("overwrite"); p["onError"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("stop"), ["default"] = "stop" };
                p["missingPolicy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("reject"), ["default"] = "reject" };
                p["mode"] = new JsonObject { ["type"] = "string", ["enum"] = entry == "PlanPlcExternalSourceImport" ? new JsonArray("preview") : new JsonArray("preview", "apply"), ["default"] = "preview" };
                Flag("confirm"); Text("expectedPlanHash"); Text("expectedProjectFile");
            }
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = p, ["required"] = required });
        }
    }
    public sealed class SourceListContract : SourceContract { protected override string Entry => "ListPlcExternalSources"; }
    public sealed class SourcePlanContract : SourceContract { protected override string Entry => "PlanPlcExternalSourceImport"; }
    public sealed class SourceGenerateContract : SourceContract { protected override string Entry => "GenerateBlocksFromExternalSource"; }
    public sealed class SourceDeleteContract : SourceContract { protected override string Entry => "DeletePlcExternalSource"; }
    public sealed class SourceManageContract : SourceContract { protected override string Entry => "ManagePlcExternalSources"; }
}
