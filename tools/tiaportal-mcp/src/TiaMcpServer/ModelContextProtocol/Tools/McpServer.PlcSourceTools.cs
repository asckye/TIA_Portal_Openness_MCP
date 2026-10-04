using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

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
