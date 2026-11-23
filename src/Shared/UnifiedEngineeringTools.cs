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

        [McpServerTool(Name="ImportUnifiedEngineeringList"),
                Description("[L2][HMI-Unified][WRITE] Native import of textLists/graphicLists from a server-side file using Import(DirectoryInfo,string). " +
                "expectedNames is an array of exact list names checked after import. dryRun=true and overwrite=false default; nonempty targets require " +
                "explicit overwrite=true because complete file identities are not verified. Entire file is imported and may affect additional lists; " +
                "readback verifies names, not all entries. No explicit save/compile/download. API availability differs by version. Native " +
                "behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportUnifiedEngineeringListV4(
            string softwarePath,
            string category,
            string filePath,
            [Description("expectedNames: Array of names expected after the import (verified).")] string[] expectedNames,
            bool dryRun=true,
            [Description("overwrite: default false; native packages with unverified identities refuse nonempty targets before Import.")] bool overwrite = false)
            => HmiInspectionContract
                .Run("ImportUnifiedEngineeringList", !dryRun, true, () =>
                    ImportUnifiedEngineeringList(softwarePath, category, filePath, V4Json.Serialize(expectedNames), dryRun, overwrite));

        public ResponseMessage ImportUnifiedEngineeringList(
            string softwarePath,
            string category,
            string filePath,
            [Description("expectedNamesJson: JSON array of names expected after the import (verified).")] string expectedNamesJson,
            bool dryRun=true, bool overwrite = false)
        => _service.ImportUnifiedEngineeringList(softwarePath,category,filePath,expectedNamesJson,dryRun,overwrite);

        [McpServerTool(Name="ListUnifiedEngineeringObjects"),
                Description("[L2][HMI-Unified][READ] Read paginated public scalar properties for " +
                "alarmClasses/discreteAlarms/analogAlarms/alarmLogs/dataLogs/textLists/graphicLists/connections/systemTags/systemTextLists/" +
                "auditTrails/opcUaAlarmTypes. " +
                "Optional exact name. Returns counts, nextOffset, field failures and excluded complex properties. Not a complete export of entries or " +
                "bindings; live pagination requires unchanged collection.")]
        public CallToolResult ReadUnifiedEngineeringObjectsV4(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => HmiInspectionContract
                .Run("ListUnifiedEngineeringObjects", false, false, () => ReadUnifiedEngineeringObjects(softwarePath, category, name, offset, limit), offset
                    : offset, pageSize: limit);

        public ResponseMessage ReadUnifiedEngineeringObjects(string softwarePath,string category,string name="",int offset=0,int limit=100)
        => _service.ReadUnifiedEngineeringObjects(softwarePath,category,name,offset,limit);

        [McpServerTool(Name="ManageUnifiedEngineeringObject"),
                Description("[L2][HMI-Unified][WRITE] Native create/update/delete for " +
                "alarmClasses/discreteAlarms/analogAlarms/alarmLogs/dataLogs/textLists/graphicLists when supported by installed API; connections/tagTables " +
                "support delete only. name is exact, or /Group/Sub/Table for tagTables (preview includes tag count). Lists have no " +
                "Create[[ImportUnifiedEngineeringList|; use " +
                "ImportUnifiedEngineeringList]]. properties contains writable scalars; TriggerBitAddress/TriggerAddress are read-only. Alarm create/update " +
                "accepts texts as {EventText|EventText1..9|InfoText:{culture:text}}; AlarmClass must exist and cultures must be active project languages. " +
                "Default dryRun=true; every execution requires expectedPlanHash and expectedProjectFile from the same preview. Preview checks API shape " +
                "and bindings, not all TIA semantics. Failed creates delete the new object; updates restore journaled values, with cleanupRequired on " +
                "incomplete rollback. For bulk alarms use ApplyToolBatch with one call per alarm; stops at first failure, earlier calls stay. " +
                "No save/compile/download. Delete impact on references is not analyzed. Native " +
                "behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageUnifiedEngineeringObjectV4(string softwarePath,string category,string name,
                [Description("create | update | delete. ")] string action,AttributeMap<Scalar> properties = null!,bool dryRun=true,
                [Description("Alarm text property to culture-to-text map. Empty text clears that culture; omitted cultures stay unchanged.")]
                AttributeMap<AttributeMap<string>> texts = null!, string expectedPlanHash = "", string expectedProjectFile = "")
            => McpServer.RunNativeWritePreview("ManageUnifiedEngineeringObject", dryRun,
                new { softwarePath, category, name, action,
                    properties = properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()),
                    texts = texts ?? new AttributeMap<AttributeMap<string>>(new Dictionary<string, AttributeMap<string>>()) },
                () => Manage(softwarePath, category, name, action, properties, texts, true),
                () => Manage(softwarePath, category, name, action, properties, texts, false), expectedPlanHash, expectedProjectFile);

        private CallToolResult Manage(string softwarePath, string category, string name, string action,
            AttributeMap<Scalar>? properties, AttributeMap<AttributeMap<string>>? texts, bool dryRun)
            => HmiInspectionContract.Run("ManageUnifiedEngineeringObject", !dryRun, true, () =>
                ManageUnifiedEngineeringObject(softwarePath, category, name, action,
                    V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), dryRun,
                    V4Json.Serialize(texts ?? new AttributeMap<AttributeMap<string>>(new Dictionary<string, AttributeMap<string>>()))));

        public ResponseMessage ManageUnifiedEngineeringObject(string softwarePath,string category,string name,
                [Description("create | update | delete. ")] string action,string propertiesJson="{}",bool dryRun=true,string textsJson="{}")
        => _service.ManageUnifiedEngineeringObject(softwarePath,category,name,action,propertiesJson,dryRun,textsJson);
    }
}
