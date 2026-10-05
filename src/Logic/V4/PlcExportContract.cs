using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class PlcExportContract : IBehaviorCandidateContract
    {
        public static readonly string[] Entries = { "ExportPlcBlock", "ExportPlcType", "ExportPlcTagTable", "ExportPlcBlocks", "ExportPlcTypes", "ExportPlcBlockDocuments", "ExportPlcBlocksDocuments" };
        public static bool Documents(string tool) => tool.EndsWith("Documents", StringComparison.Ordinal);
        public static bool Batch(string tool) => tool == "ExportPlcBlocks" || tool == "ExportPlcTypes" || tool == "ExportPlcBlocksDocuments";
        protected virtual string Entry => "ExportPlcBlock";
        public JsonElement InputSchema => Schema(Entry, "21");
        public static JsonElement Schema(string tool, string release)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            void Text(string key, bool needed) { properties[key] = new JsonObject { ["type"] = "string" }; if (needed) required.Add(key); else properties[key]!["default"] = ""; }
            void Flag(string key) => properties[key] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            Text("softwarePath", true); Text("exportPath", true);
            Text(Batch(tool) ? "groupPath" : tool == "ExportPlcType" ? "typePath" : tool == "ExportPlcTagTable" ? "tagTableName" : "blockPath", true);
            if (Batch(tool))
            {
                Text("regexName", false); Flag("recursive");
                properties["maxItems"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 256, ["default"] = 128 };
            }
            if (tool != "ExportPlcTagTable") Flag("preservePath");
            Flag("overwrite"); Text("workspaceRoot", false);
            properties["onError"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("stop"), ["default"] = "stop" };
            properties["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("preview", "apply"), ["default"] = "preview" };
            Flag("confirm"); Text("expectedPlanHash", false); Text("expectedProjectFile", false);
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required });
        }
    }
    public sealed class PlcTypeExportContract : PlcExportContract { protected override string Entry => "ExportPlcType"; }
    public sealed class PlcTagExportContract : PlcExportContract { protected override string Entry => "ExportPlcTagTable"; }
    public sealed class PlcBlocksExportContract : PlcExportContract { protected override string Entry => "ExportPlcBlocks"; }
    public sealed class PlcTypesExportContract : PlcExportContract { protected override string Entry => "ExportPlcTypes"; }
    public sealed class PlcDocumentExportContract : PlcExportContract { protected override string Entry => "ExportPlcBlockDocuments"; }
    public sealed class PlcDocumentsExportContract : PlcExportContract { protected override string Entry => "ExportPlcBlocksDocuments"; }
}
