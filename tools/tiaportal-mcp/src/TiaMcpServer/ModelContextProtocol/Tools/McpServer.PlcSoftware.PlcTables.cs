using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // CLI compatibility entries; only PlcTablesTools carries MCP tool attributes.
        public static ResponseStringList GetPlcTagTables(string softwarePath)
            => ((PlcTablesTools)EngineServices.Get(typeof(PlcTablesTools))).GetPlcTagTables(softwarePath);

        public static ResponseExportFile ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath)
            => ((PlcTablesTools)EngineServices.Get(typeof(PlcTablesTools))).ExportPlcTagTable(softwarePath, tagTableName, exportPath);

        public static ResponseStringList GetPlcWatchTables(string softwarePath)
            => ((PlcTablesTools)EngineServices.Get(typeof(PlcTablesTools))).GetPlcWatchTables(softwarePath);

        public static ResponseImportBatch ExportPlcWatchTablesToDirectory(string softwarePath, string dir, string regexName = "")
            => ((PlcTablesTools)EngineServices.Get(typeof(PlcTablesTools))).ExportPlcWatchTablesToDirectory(softwarePath, dir, regexName);

        public static ResponseJsonReport ProbePlcMonitorOnlineCapabilities(string softwarePath)
            => ((PlcTablesTools)EngineServices.Get(typeof(PlcTablesTools))).ProbePlcMonitorOnlineCapabilities(softwarePath);

        public static ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(string softwarePath, string watchTableName, int maxEntries = 50)
            => ((PlcTablesTools)EngineServices.Get(typeof(PlcTablesTools))).ReadPlcWatchTableCurrentValuesReadOnly(softwarePath, watchTableName, maxEntries);

        private static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                throw new McpException(parameterName + " must be a JSON object. Parse error: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        internal static class PlcTableToolSupport
        {
            internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy() => McpServer.GetOnlineMonitoringSafetyPolicy();
            internal static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName) => McpServer.ParseJsonObjectOrEmpty(json, parameterName);
        }
    }
}
