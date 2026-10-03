using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedEventsTools
    {
        private readonly UnifiedEventsService _service;

        public UnifiedEventsTools(UnifiedEventsService service) => _service = service;

        [McpServerTool(Name="ManageUnifiedEvent"), Description("[L2][HMI-Unified][WRITE] Exact screen/control event or property event read/create/update/delete. Object JSON path. Updates preserve omitted script fields. Mutations need preview token. No Script SyntaxCheck, script execution, save/compile/download.")]
        public ResponseMessage ManageUnifiedEvent(
            string softwarePath,
            string objectPathJson,
            string eventType,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string propertyName="",
            [Description("scriptPropertiesJson: JSON object of script properties to set.")] string scriptPropertiesJson="{}",
            string expectedToken="",
            bool dryRun=true)
            => _service.ManageUnifiedEvent(softwarePath,objectPathJson,eventType,action,propertyName,scriptPropertiesJson,expectedToken,dryRun);
    }
}
