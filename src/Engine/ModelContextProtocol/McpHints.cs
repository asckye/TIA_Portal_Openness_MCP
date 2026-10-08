using System;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Recovery guidance follows typed failures; native prose never selects an action.
    public static class McpHints
    {
        public static string Recovery(Exception? ex)
        {
            for (var error = ex; error != null; error = error.InnerException)
            {
                if (error is PortalException portal)
                {
                    switch (portal.Code)
                    {
                        case PortalErrorCode.NotFound: return RecoveryCode("NOT_FOUND");
                        case PortalErrorCode.InvalidParams: return RecoveryCode("INVALID_ARGUMENT");
                        case PortalErrorCode.NotSupportedOnVersion: return RecoveryCode("UNSUPPORTED_CAPABILITY");
                    }
                }
                if (error is ArgumentException) return RecoveryCode("INVALID_ARGUMENT");
                if (error is UnauthorizedAccessException) return RecoveryCode("ACCESS_DENIED");
            }
            return "";
        }

        public static string RecoveryCode(string code)
            => RecoveryHints.RecoveryCode(code);

        internal static CallToolResult WithRecovery(CallToolResult result)
        {
            var body = McpServer.ResultBody(result);
            if (!RecoveryHints.Attach(body, (tool, release, operation) =>
            {
                if (release != McpServer.ReleaseKey || !McpServer.AllToolDescriptors().ContainsKey(tool)) return null;
                var usage = McpServer.ResultBody(new ToolUsageTools().GetToolUsage(toolName: tool, operation: operation));
                return usage?["data"]?["example"] as JsonObject;
            })) return result;
            return new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body!.ToJsonString() } } };
        }
    }
}
