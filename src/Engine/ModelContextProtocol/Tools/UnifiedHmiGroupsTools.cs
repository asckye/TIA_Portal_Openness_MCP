using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedHmiGroupsTools
    {
        private readonly UnifiedHmiGroupsService _service;

        public UnifiedHmiGroupsTools(UnifiedHmiGroupsService service) => _service = service;

        [McpServerTool(Name = "ManageUnifiedHmiGroup"), Description("[L2][HMI-Unified][WRITE] Create, rename or deleteEmpty a user folder. family=screens/tags; groupPath is an exact relative nested path. newName is one segment for rename. Default dryRun=true. Missing parents created, root/nonempty deletion refused. No save/compile/download. Unified script folders are not exposed by the supported API.")]
        public ResponseMessage ManageUnifiedHmiGroup(string softwarePath, [Description("screens | tags. Unified HMI user-folder family.")] string family, string groupPath, [Description("create | rename | deleteEmpty. ")] string action, string newName = "", bool dryRun = true)
            => _service.ManageUnifiedHmiGroup(softwarePath, family, groupPath, action, newName, dryRun);
    }
}
