using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        [McpServerTool(Name = "ManageUnifiedHmiGroup"), Description("[L2][HMI-Unified][WRITE] Create, rename or deleteEmpty a user folder. family=screens/tags; groupPath is an exact relative nested path. newName is one segment for rename. Default dryRun=true. Missing parents created, root/nonempty deletion refused. No save/compile/download. Unified script folders are not exposed by the supported API.")]
        public static ResponseMessage ManageUnifiedHmiGroup(string softwarePath, [Description("screens | tags. Unified HMI user-folder family.")] string family, string groupPath, [Description("create | rename | deleteEmpty. ")] string action, string newName = "", bool dryRun = true)
            => Portal.ManageUnifiedHmiGroup(softwarePath, family, groupPath, action, newName, dryRun);
    }
}
