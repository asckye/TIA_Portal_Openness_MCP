using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        public static ResponseObjectDescribe DescribeObjectProperty(
            string objectKind,
            string objectPath,
            string propertyPath,
            string softwarePath = "",
            int maxMembers = 200)
            => ((ReflectionTools)EngineServices.Get(typeof(ReflectionTools))).DescribeObjectProperty(objectKind, objectPath, propertyPath, softwarePath, maxMembers);

        public static ResponseObjectValue GetObjectProperty(
            string objectKind,
            string objectPath,
            string propertyPath,
            string softwarePath = "")
            => ((ReflectionTools)EngineServices.Get(typeof(ReflectionTools))).GetObjectProperty(objectKind, objectPath, propertyPath, softwarePath);

        public static ResponseObjectChildren ListObjectChildren(
            string objectKind,
            string objectPath,
            string collectionProperty,
            string softwarePath = "",
            int limit = 200)
            => ((ReflectionTools)EngineServices.Get(typeof(ReflectionTools))).ListObjectChildren(objectKind, objectPath, collectionProperty, softwarePath, limit);

        public static ResponseObjectValue InvokeObject(
            string objectKind,
            string objectPath,
            string methodName,
            System.Text.Json.JsonElement[]? args = null,
            string softwarePath = "",
            bool allowWrite = false)
            => ((ReflectionTools)EngineServices.Get(typeof(ReflectionTools))).InvokeObject(objectKind, objectPath, methodName, args, softwarePath, allowWrite);

        public static ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args, string softwarePath = "", bool allowWrite = false)
            => ((ReflectionTools)EngineServices.Get(typeof(ReflectionTools))).InvokeObject(objectKind, objectPath, methodName, args, softwarePath, allowWrite);

        public static ResponsePlcProgramImport PlcBuildAndImport(string softwarePath, string kind, string json,
            string typeGroupPath = "", string tagFolderPath = "", string blockGroupPath = "", bool compileAfter = true, bool dryRun = true)
            => ((PlcBuildTools)EngineServices.Get(typeof(PlcBuildTools))).PlcBuildAndImport(softwarePath, kind, json,
                typeGroupPath, tagFolderPath, blockGroupPath, compileAfter, dryRun);

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
