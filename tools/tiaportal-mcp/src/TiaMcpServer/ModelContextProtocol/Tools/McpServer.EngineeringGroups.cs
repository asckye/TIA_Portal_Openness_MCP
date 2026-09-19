using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ManagePlcUserGroup"), Description("[L2][PLC-Software][WRITE] Create, rename or deleteEmpty an exact nested PLC user group. family: blocks/types/tags/technology/watchTables/externalSources (PlcBlockUserGroup / PlcTypeUserGroup / PlcTagTableUserGroup / TechnologicalInstanceDBUserGroup / PlcWatchAndForceTableUserGroup / PlcExternalSourceUserGroup; the resulting group is read back as a typed row). groupPath is relative to the family's root. newName is one segment for rename. Default dryRun=true; actual edits require Offline and exclusive access. Missing parents are created; root and nonempty deletion refused. No save/compile/download. Partial failures may leave created parents; inspect errors.")]
        public static ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => Portal.ManagePlcUserGroup(softwarePath, family, groupPath, action, newName, dryRun);

        [McpServerTool(Name = "ManageUnifiedHmiGroup"), Description("[L2][HMI-Unified][WRITE] Create, rename or deleteEmpty a user folder. family=screens/tags; groupPath is an exact relative nested path. newName is one segment for rename. Default dryRun=true. Missing parents created, root/nonempty deletion refused. No save/compile/download. Unified script folders are not exposed by the supported API.")]
        public static ResponseMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => Portal.ManageUnifiedHmiGroup(softwarePath, family, groupPath, action, newName, dryRun);
    }
}
