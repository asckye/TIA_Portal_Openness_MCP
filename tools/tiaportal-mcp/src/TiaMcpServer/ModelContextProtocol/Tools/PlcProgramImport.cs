using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class PlcProgramImport
    {
        internal static string ClassifyPlcXml(string file, out string subKind, out string objectName)
        {
            subKind = "";
            objectName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = XDocument.Load(file);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase));

                var local = obj?.Name.LocalName ?? "";
                subKind = local;
                var name = obj?.Element("AttributeList")?.Element("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    objectName = name!.Trim();
                }
                if (local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase)) return "type";
                if (string.Equals(local, "SW.Tags.PlcTagTable", StringComparison.OrdinalIgnoreCase)) return "tagtable";
                if (local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)) return "block";
                if (local.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase)) return "technology";
            }
            catch
            {
 /* swallow(parse-fallback): Unreadable XML remains unknown so the batch import reports or skips it using its existing classification policy. */                subKind = "";
            }

            return "unknown";
        }

        internal static ResponsePlcProgramImport BuildPlcProgramImportResponse(
            string sourceDir,
            bool dryRun,
            List<string> discoveredTypes,
            List<string> discoveredTagTables,
            List<string> discoveredTechnologyObjects,
            List<string> discoveredBlocks,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedTechnologyObjects,
            List<string> importedBlocks,
            List<ImportFailure> failed,
            ResponseCompile? compile)
        {
            var compileOk = compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false);
            var success = failed.Count == 0 && compileOk;
            return new ResponsePlcProgramImport
            {
                Message = dryRun
                    ? $"PLC program dry-run from '{sourceDir}': types={discoveredTypes.Count}, tagTables={discoveredTagTables.Count}, technologyObjects={discoveredTechnologyObjects.Count}, blocks={discoveredBlocks.Count}, failed={failed.Count}"
                    : $"PLC program import from '{sourceDir}': types={importedTypes.Count}, tagTables={importedTagTables.Count}, technologyObjects={importedTechnologyObjects.Count}, blocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}",
                DryRun = dryRun,
                DiscoveredTypes = discoveredTypes,
                DiscoveredTagTables = discoveredTagTables,
                DiscoveredTechnologyObjects = discoveredTechnologyObjects,
                DiscoveredBlocks = discoveredBlocks,
                ImportedTypes = importedTypes,
                ImportedTagTables = importedTagTables,
                ImportedTechnologyObjects = importedTechnologyObjects,
                ImportedBlocks = importedBlocks,
                Failed = failed,
                Compile = compile,
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = success, ["importOrdering"] = ImportSelectionPolicy.OrderingDescription, ["dependencyResolution"] = false }
            };
        }
    }
}
