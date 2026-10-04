using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        internal static class PlcSoftwareToolSupport
        {
            internal static string ResolveCompareSide(string side, string filePath, string blockPath, string softwarePath, JsonObject meta, out string? tempDir)
                => McpServer.ResolveCompareSide(side, filePath, blockPath, softwarePath, meta, out tempDir);
            internal static void DeleteAnalysisTempDir(string? tempDir) => McpServer.DeleteAnalysisTempDir(tempDir);
            internal static string MakeSafeFileName(string name) => McpServer.MakeSafeFileName(name);
            internal static ResponseCompile BuildCompileResponse(string softwarePath, object result) => McpServer.BuildCompileResponse(softwarePath, result);
            internal static string ClassifyPlcXml(string file, out string subKind, out string objectName) => McpServer.ClassifyPlcXml(file, out subKind, out objectName);
            internal static ResponsePlcProgramImport BuildPlcProgramImportResponse(string sourceDir, bool dryRun,
                List<string> discoveredTypes, List<string> discoveredTagTables, List<string> discoveredTechnologyObjects, List<string> discoveredBlocks,
                List<string> importedTypes, List<string> importedTagTables, List<string> importedTechnologyObjects, List<string> importedBlocks,
                List<ImportFailure> failed, ResponseCompile? compile)
                => McpServer.BuildPlcProgramImportResponse(sourceDir, dryRun, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks,
                    importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
        }
    }
}
