using ModelContextProtocol.Server;
using System.ComponentModel;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class GuideTools
    {
        private readonly ToolUsageTools _usage;

        public GuideTools(ToolUsageTools usage) => _usage = usage;

        [McpServerTool(Name = "GetAuthoringGuide"), Description("[L0][Guide][READ] Compatibility entry into the unified GetToolUsage example library. A language topic returns programming examples; workflow returns the connection sequence. No separate rules or native calls.")]
        public ResponseMessage GetAuthoringGuide(
            [Description("Language/format such as scl, scl-sd, lad, fbd, db, udt, s7res, stl, graph, hmi-javascript, hmi-vbscript; legacy workflow/hmi/errors topics remain accepted.")] string topic)
        {
            switch (topic.Trim().ToLowerInvariant())
            {
                case "workflow": return _usage.GetToolUsage(exampleId: "sequence/connect-project");
                case "openness-workflow": return _usage.GetToolUsage(query: "openness-base");
                case "startdrive-bico": return _usage.GetToolUsage(toolName: "ManageStartdriveParameter", operation: "read");
                case "hmi": return _usage.GetToolUsage(language: "hmi-javascript");
                case "errors": return _usage.GetToolUsage();
                default: return _usage.GetToolUsage(language: topic);
            }
        }
    }
}
