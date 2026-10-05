using System;
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
        {
            switch (code)
            {
                case "NOT_FOUND": return Tip("verify the exact target names with GetProjectTree / GetSoftwareTree / ListPlcBlocks before another call.");
                case "INVALID_ARGUMENT": return Tip("read GetToolUsage and the tool input schema, then correct the argument without issuing the operation again automatically.");
                case "UNSUPPORTED_CAPABILITY": return Tip("check this release's capabilities with GetToolUsage; do not substitute an unverified operation.");
                case "ACCESS_DENIED": return Tip("check the required permissions; do not bypass the access restriction.");
                case "OFFLINE_REQUIRED": return Tip("the affected target must be offline; verify its state and obtain explicit authorization before changing the online connection.");
                case "OUTCOME_UNKNOWN": return Tip("verify the actual target state and reset the session before continuing; do not replay the write automatically.");
                default: return "";
            }
        }
        private static string Tip(string text) => "  ▶ RECOVERY: " + text;
    }
}
