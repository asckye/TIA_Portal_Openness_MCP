using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        // Temporary access to shared private infrastructure; the owning domains move in later steps.
        // Keeping these adapters here lets pilot method bodies move without copying their implementations.
        internal static class PilotToolSupport
        {
            internal static ResponseMessage RunOfflineAnalysisTool(string toolName, Func<JsonObject, string> action)
                => McpServer.RunOfflineAnalysisTool(toolName, action);

            internal static ResponseXmlBuild BuildOfflineXmlBuilderReport(JsonObject data, string successMessage)
                => McpServer.BuildOfflineXmlBuilderReport(data, successMessage);

            internal static Dictionary<string, MethodInfo> AllToolMethods(bool includeUnavailable = false)
                => McpServer.AllToolMethods(includeUnavailable);

            internal static McpServerTool CreateTool(string name, MethodInfo method)
                => McpServer.CreateTool(name, method);

            internal static string ToolDescription(MethodInfo method)
                => McpServer.ToolDescription(method);
        }
    }
}
