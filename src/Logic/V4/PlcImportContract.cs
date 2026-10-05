using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public class PlcImportContract : IBehaviorCandidateContract
    {
        public static readonly string[] Entries = { "ImportPlcBlock", "ImportPlcType", "ImportPlcTagTable", "ImportPlcBlocksFromDirectory",
            "ImportPlcTagTablesFromDirectory", "ImportPlcProgramFromDirectory", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments" };
        public static bool IsDirectory(string entry) => entry.EndsWith("FromDirectory", StringComparison.Ordinal) || entry == "ImportPlcBlocksDocuments";
        public static bool IsDocuments(string entry) => entry == "ImportPlcBlockDocuments" || entry == "ImportPlcBlocksDocuments";
        public static int MaximumItems(string entry, string release) => entry == "ImportPlcBlocksDocuments" ? 16 : IsDirectory(entry) ? 256 : 1;
        protected virtual string Entry => "ImportPlcBlock";
        public JsonElement InputSchema => Schema(Entry, "21");

        public static JsonElement Schema(string entry, string release)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            void Text(string key, bool needed, string value = "")
            { properties[key] = new JsonObject { ["type"] = "string" }; if (needed) required.Add(key); else properties[key]!["default"] = value; }
            void Flag(string key) => properties[key] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            Text("softwarePath", true);
            if (entry == "ImportPlcProgramFromDirectory")
            { Text("sourceDir", true); foreach (var key in new[] { "typeGroupPath", "tagFolderPath", "technologyFolderPath", "blockGroupPath" }) Text(key, false); }
            else
            {
                Text(entry == "ImportPlcTagTable" || entry == "ImportPlcTagTablesFromDirectory" ? "folderPath" : "groupPath", true);
                Text(IsDirectory(entry) && !IsDocuments(entry) ? "dir" : "importPath", true);
            }
            if (IsDirectory(entry))
            {
                Text("regexName", false);
                properties["importOrder"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumItems(entry, release),
                    ["uniqueItems"] = true, ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                    ["description"] = "Every selected relative XML path or document basename, exactly once, in caller-reviewed order. Dependencies are not inferred." };
                required.Add("importOrder");
                properties["maxItems"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaximumItems(entry, release), ["default"] = IsDocuments(entry) ? 16 : 128 };
            }
            if (entry == "ImportPlcBlockDocuments") Text("fileNameWithoutExtension", true);
            Flag("overwrite");
            properties["versionPolicy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("exact"), ["default"] = "exact" };
            properties["onError"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("stop"), ["default"] = "stop" };
            Flag("compileAfter");
            properties["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("preview", "apply"), ["default"] = "preview" };
            Flag("confirm"); Text("expectedPlanHash", false); Text("expectedProjectFile", false);
            return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = required, ["properties"] = properties });
        }
    }
    public sealed class PlcTypeImportContract : PlcImportContract { protected override string Entry => "ImportPlcType"; }
    public sealed class PlcTagImportContract : PlcImportContract { protected override string Entry => "ImportPlcTagTable"; }
    public sealed class PlcBlocksImportContract : PlcImportContract { protected override string Entry => "ImportPlcBlocksFromDirectory"; }
    public sealed class PlcTagsImportContract : PlcImportContract { protected override string Entry => "ImportPlcTagTablesFromDirectory"; }
    public sealed class PlcProgramImportContract : PlcImportContract { protected override string Entry => "ImportPlcProgramFromDirectory"; }
    public sealed class PlcDocumentImportContract : PlcImportContract { protected override string Entry => "ImportPlcBlockDocuments"; }
    public sealed class PlcDocumentsImportContract : PlcImportContract { protected override string Entry => "ImportPlcBlocksDocuments"; }
}
