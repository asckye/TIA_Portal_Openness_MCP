using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        public static ResponseExportAsDocuments ExportAsDocuments(
            string softwarePath,
            string blockPath,
            string exportPath,
            bool preservePath = false)
            => ((DocumentsTools)EngineServices.Get(typeof(DocumentsTools))).ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath);

        public static ResponseImportFromDocuments ImportFromDocuments(
            string softwarePath,
            string groupPath,
            string importPath,
            string fileNameWithoutExtension,
            string importOption = "Override")
            => ((DocumentsTools)EngineServices.Get(typeof(DocumentsTools))).ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, importOption);

        public static ResponseMessage ImportPlcExternalSource(
            string softwarePath,
            string groupPath,
            string filePath)
            => ((PlcExternalSourcesTools)EngineServices.Get(typeof(PlcExternalSourcesTools))).ImportPlcExternalSource(softwarePath, groupPath, filePath);

        public static ResponseMessage GenerateBlocksFromExternalSource(
            string softwarePath,
            string externalSourceName)
            => ((PlcExternalSourcesTools)EngineServices.Get(typeof(PlcExternalSourcesTools))).GenerateBlocksFromExternalSource(softwarePath, externalSourceName);

        internal static class PlcSourceToolSupport
        {
            internal static string BuildTypeDidYouMean(string softwarePath, string typePath)
                => McpServer.BuildTypeDidYouMean(softwarePath, typePath);
            internal static string MakeSafeFileName(string name) => McpServer.MakeSafeFileName(name);
            internal static ResponseMessage RunOfflineAnalysisTool(string toolName, Func<JsonObject, string> action)
                => McpServer.RunOfflineAnalysisTool(toolName, action);
            internal static void DeleteAnalysisTempDir(string? tempDir) => McpServer.DeleteAnalysisTempDir(tempDir);
        }
    }
}
