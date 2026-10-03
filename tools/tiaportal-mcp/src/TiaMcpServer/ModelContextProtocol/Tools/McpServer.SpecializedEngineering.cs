using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {


        [McpServerTool(Name="ManageUnifiedEvent"), Description("[L2][HMI-Unified][WRITE] Exact screen/control event or property event read/create/update/delete. Object JSON path. Updates preserve omitted script fields. Mutations need preview token. No Script SyntaxCheck, script execution, save/compile/download.")]
        public static ResponseMessage ManageUnifiedEvent(
            string softwarePath,
            string objectPathJson,
            string eventType,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string propertyName="",
            [Description("scriptPropertiesJson: JSON object of script properties to set.")] string scriptPropertiesJson="{}",
            string expectedToken="",
            bool dryRun=true)
            => Portal.ManageUnifiedEvent(softwarePath,objectPathJson,eventType,action,propertyName,scriptPropertiesJson,expectedToken,dryRun);
    }
}
