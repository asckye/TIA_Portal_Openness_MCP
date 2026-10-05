using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Linq;
using System.Collections.Generic;
using System;
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

        [McpServerTool(Name="ManageUnifiedEvent"), Description("[L2][HMI-Unified][WRITE] Exact screen/control event or property event read/create/update/delete. Object JSON path. Updates preserve omitted script fields. Mutations need preview token. No Script SyntaxCheck, script execution, save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageUnifiedEventV4(
            string softwarePath,
            PropertyStep[] objectPath,
            string eventType,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string propertyName="",
            [Description("scriptProperties: Object of script properties to set.")] AttributeMap<Scalar> scriptProperties = null!,
            string expectedToken="",
            bool dryRun=true)
            => HmiInspectionContract.Run("ManageUnifiedEvent", action != "read" && !dryRun, true, () => ManageUnifiedEvent(softwarePath, V4Json.Serialize(objectPath), eventType, action, propertyName, V4Json.Serialize(scriptProperties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), expectedToken, dryRun));

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
