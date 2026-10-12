using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedHmiGroupsTools
    {
        private readonly UnifiedHmiGroupsService _service;

        public UnifiedHmiGroupsTools(UnifiedHmiGroupsService service) => _service = service;

        [McpServerTool(Name = "ManageUnifiedHmiGroup"), Description("[L2][HMI-Unified][WRITE] Create, rename or deleteEmpty a user folder. family=screens/tags; groupPath is an exact relative nested path. newName is one segment for rename. Default dryRun=true. Missing parents created, root/nonempty deletion refused. No save/compile/download. Unified script folders are not exposed by the supported API. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageUnifiedHmiGroupV4(string softwarePath, [Description("screens | tags. Unified HMI user-folder family.")] string family, string groupPath, [Description("create | rename | deleteEmpty. ")] string action, string newName = "", bool dryRun = true)
            => UnifiedHmiContract.Run("ManageUnifiedHmiGroup", !dryRun, true, () => ManageUnifiedHmiGroup(softwarePath, family, groupPath, action, newName, dryRun));

        public ResponseMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _service.ManageUnifiedHmiGroup(softwarePath, family, groupPath, action, newName, dryRun);
    }
}
