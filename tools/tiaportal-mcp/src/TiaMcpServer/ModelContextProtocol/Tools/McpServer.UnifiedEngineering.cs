using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ImportUnifiedEngineeringList"), Description("[L2][HMI-Unified][WRITE] Native import of textLists/graphicLists from a server-side file using Import(DirectoryInfo,string). expectedNamesJson is an array of exact list names checked after import. dryRun=true default. Entire file is imported and may affect additional lists; readback verifies names, not all entries. No explicit save/compile/download. API availability differs by version.")]
        public static ResponseMessage ImportUnifiedEngineeringList(
            string softwarePath,
            string category,
            string filePath,
            [Description("expectedNamesJson: JSON array of names expected after the import (verified).")] string expectedNamesJson,
            bool dryRun=true)
            => Portal.ImportUnifiedEngineeringList(softwarePath,category,filePath,expectedNamesJson,dryRun);
        [McpServerTool(Name="ReadUnifiedEngineeringObjects"), Description("[L2][HMI-Unified][READ] Read paginated public scalar properties for alarmClasses/discreteAlarms/analogAlarms/alarmLogs/dataLogs/textLists/graphicLists/systemTags/systemTextLists/auditTrails/opcUaAlarmTypes. Optional exact name. Returns counts, nextOffset, field failures and excluded complex properties. Not a complete export of entries or bindings; live pagination requires unchanged collection.")]
        public static ResponseMessage ReadUnifiedEngineeringObjects(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => Portal.ReadUnifiedEngineeringObjects(softwarePath,category,name,offset,limit);
        [McpServerTool(Name="ManageUnifiedEngineeringObject"), Description("[L2][HMI-Unified][WRITE] Native create/update/delete for alarmClasses/discreteAlarms/analogAlarms/alarmLogs/dataLogs/textLists/graphicLists when supported by installed API. Exact name. propertiesJson contains public writable scalar properties only, not references/complex text. Default dryRun=true. Preview validates API shape, not TIA semantics. No save/compile/download. Delete impact on references is not analyzed. Partial writes reported, no rollback.")]
        public static ResponseMessage ManageUnifiedEngineeringObject(string softwarePath,string category,string name,string action,string propertiesJson="{}",bool dryRun=true)
            => Portal.ManageUnifiedEngineeringObject(softwarePath,category,name,action,propertiesJson,dryRun);
    }
}
