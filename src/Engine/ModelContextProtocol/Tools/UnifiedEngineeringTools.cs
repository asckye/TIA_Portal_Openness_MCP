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
    internal sealed class UnifiedEngineeringTools
    {
        private readonly UnifiedEngineeringService _service;

        public UnifiedEngineeringTools(UnifiedEngineeringService service) => _service = service;

        [McpServerTool(Name="ImportUnifiedEngineeringList"), Description("[L2][HMI-Unified][WRITE] Native import of textLists/graphicLists from a server-side file using Import(DirectoryInfo,string). expectedNames is an array of exact list names checked after import. dryRun=true default. Entire file is imported and may affect additional lists; readback verifies names, not all entries. No explicit save/compile/download. API availability differs by version. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportUnifiedEngineeringListV4(
            string softwarePath,
            string category,
            string filePath,
            [Description("expectedNames: Array of names expected after the import (verified).")] string[] expectedNames,
            bool dryRun=true)
            => HmiInspectionContract.Run("ImportUnifiedEngineeringList", !dryRun, true, () => ImportUnifiedEngineeringList(softwarePath, category, filePath, V4Json.Serialize(expectedNames), dryRun));

        public ResponseMessage ImportUnifiedEngineeringList(
            string softwarePath,
            string category,
            string filePath,
            [Description("expectedNamesJson: JSON array of names expected after the import (verified).")] string expectedNamesJson,
            bool dryRun=true)
        => _service.ImportUnifiedEngineeringList(softwarePath,category,filePath,expectedNamesJson,dryRun);

        [McpServerTool(Name="ListUnifiedEngineeringObjects"), Description("[L2][HMI-Unified][READ] Read paginated public scalar properties for alarmClasses/discreteAlarms/analogAlarms/alarmLogs/dataLogs/textLists/graphicLists/systemTags/systemTextLists/auditTrails/opcUaAlarmTypes. Optional exact name. Returns counts, nextOffset, field failures and excluded complex properties. Not a complete export of entries or bindings; live pagination requires unchanged collection.")]
        public CallToolResult ReadUnifiedEngineeringObjectsV4(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => HmiInspectionContract.Run("ListUnifiedEngineeringObjects", false, false, () => ReadUnifiedEngineeringObjects(softwarePath, category, name, offset, limit), offset: offset, pageSize: limit);

        public ResponseMessage ReadUnifiedEngineeringObjects(string softwarePath,string category,string name="",int offset=0,int limit=100)
        => _service.ReadUnifiedEngineeringObjects(softwarePath,category,name,offset,limit);

        [McpServerTool(Name="ManageUnifiedEngineeringObject"), Description("[L2][HMI-Unified][WRITE] Native create/update/delete for alarmClasses/discreteAlarms/analogAlarms/alarmLogs/dataLogs/textLists/graphicLists when supported by installed API. Exact name. properties contains public writable scalar properties only, not references/complex text. Default dryRun=true. Preview validates API shape, not TIA semantics. No save/compile/download. Delete impact on references is not analyzed. Partial writes reported, no rollback. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageUnifiedEngineeringObjectV4(string softwarePath,string category,string name,[Description("create | update | delete. ")] string action,AttributeMap<Scalar> properties = null!,bool dryRun=true)
            => HmiInspectionContract.Run("ManageUnifiedEngineeringObject", !dryRun, true, () => ManageUnifiedEngineeringObject(softwarePath, category, name, action, V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), dryRun));

        public ResponseMessage ManageUnifiedEngineeringObject(string softwarePath,string category,string name,[Description("create | update | delete. ")] string action,string propertiesJson="{}",bool dryRun=true)
        => _service.ManageUnifiedEngineeringObject(softwarePath,category,name,action,propertiesJson,dryRun);
    }
}
