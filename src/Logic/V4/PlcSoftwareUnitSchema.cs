using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public static class PlcSoftwareUnitSchema
    {
        public static bool IsImport(string tool) => tool is "ImportPlcBlock" or "ImportPlcType" or "ImportPlcTagTable" or "ImportPlcBlocksFromDirectory"
            or "ImportPlcTagTablesFromDirectory" or "ImportPlcProgramFromDirectory" or "ImportPlcBlockDocuments" or "ImportPlcBlocksDocuments";
        public static bool IsExport(string tool) => tool is "ExportPlcBlock" or "ExportPlcType" or "ExportPlcTagTable" or "ExportPlcBlocks"
            or "ExportPlcTypes" or "ExportPlcBlockDocuments" or "ExportPlcBlocksDocuments";

        public static bool Available(string release, string tool, string parameter)
        {
            bool import = IsImport(tool) && !tool.EndsWith("Documents", StringComparison.Ordinal);
            bool scoped = import || IsExport(tool) && !tool.EndsWith("Documents", StringComparison.Ordinal)
                || tool == "ListPlcTags" || tool == "ListPlcUserConstants" || tool == "ListPlcSystemConstants" || tool == "AnalyzePlcCrossReferences";
            if (!scoped) return true;
            if (parameter == "unitName" || parameter == "includeUnits")
                return new[] { "16", "17", "18", "19", "20", "21" }.Contains(release, StringComparer.Ordinal);
            if (import && parameter == "swImportOptions")
                return release != "14sp1" && tool != "ImportPlcTagTable" && tool != "ImportPlcTagTablesFromDirectory";
            if (import && parameter == "importOptions")
                return new[] { "19", "20", "21" }.Contains(release, StringComparer.Ordinal);
            return true;
        }

        public static void Apply(JsonObject schema, string tool, string release)
        {
            var properties = schema["properties"]!.AsObject();
            foreach (var key in properties.Select(p => p.Key).Where(key => !Available(release, tool, key)).ToArray())
                properties.Remove(key);
        }
    }
}
