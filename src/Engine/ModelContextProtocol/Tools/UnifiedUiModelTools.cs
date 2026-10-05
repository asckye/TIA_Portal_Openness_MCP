using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedUiModelTools
    {
        private readonly UnifiedUiModelService _service;

        public UnifiedUiModelTools(UnifiedUiModelService service) => _service = service;

        [McpServerTool(Name="GetUnifiedObjectEvents"), Description("[L2][HMI-Unified][READ] Enumerate every EventHandlers and PropertyEventHandlers entry (event type, property name, raw script fields) of one exact Unified screen or screen item addressed by object JSON path [{property:Screens,name:X},{property:ScreenItems,name:Y}]. Also lists the event types the native composition can create. Live offset/limit pagination; no script execution, SyntaxCheck or write.")]
        public CallToolResult ReadUnifiedObjectEventsV4(string softwarePath,PropertyStep[] objectPath,int offset=0,int limit=100)
            => UnifiedHmiContract.Run("GetUnifiedObjectEvents", false, false, () => ReadUnifiedObjectEvents(softwarePath, V4Json.Serialize(objectPath), offset, limit), offset, limit);

        public ResponseMessage ReadUnifiedObjectEvents(string softwarePath,string objectPathJson,int offset=0,int limit=100)
            => _service.ReadUnifiedObjectEvents(softwarePath,objectPathJson,offset,limit);

        [McpServerTool(Name="ManageUnifiedObjectParts"), Description("[L2][HMI-Unified][WRITE] UI.Parts sub-objects (trend areas, trends, axes, thresholds, columns, selection items, help lines, scaling entries, pressed-state tags...) of one exact object path. read without collectionProperty lists collections; read with exact collectionProperty lists parts with partIndex. create uses the native Create(string)/Create() of that composition (partName only for named parts; columns/thresholds have no Create). update writes public scalar/Color (#AARRGGBB) properties via properties; delete needs exact partName or partIndex plus confirmDelete=true. Default preview; readback verified; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageUnifiedObjectPartsV4(
            string softwarePath,
            PropertyStep[] objectPath,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("collectionProperty: name of the collection property that holds the parts.")] string collectionProperty="",
            [Description("partName: exact name of the part.")] string partName="",
            [Description("partIndex: 0-based index of the part (-1 = by name).")] int partIndex=-1,
            [Description("partKind: kind of part (see the tool description).")] string partKind="",
            AttributeMap<Scalar>? properties = null,
            bool confirmDelete=false,
            bool dryRun=true)
            => UnifiedHmiContract.Run("ManageUnifiedObjectParts", !dryRun && action != "read", true, () => ManageUnifiedObjectParts(softwarePath, V4Json.Serialize(objectPath), action, collectionProperty, partName, partIndex, partKind, V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), confirmDelete, dryRun));

        public ResponseMessage ManageUnifiedObjectParts(string softwarePath,
            string objectPathJson,
            string action="read",
            string collectionProperty="",
            string partName="",
            int partIndex=-1,
            string partKind="",
            string propertiesJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => _service.ManageUnifiedObjectParts(softwarePath,objectPathJson,action,collectionProperty,partName,partIndex,partKind,propertiesJson,confirmDelete,dryRun);

        [McpServerTool(Name="ManageUnifiedDynamization"), Description("[L2][HMI-Unified][WRITE] Read/create/update/delete the dynamization of one exact property (propertyName) on an exact Unified screen/item object path. dynamizationKind Tag/Script/ResourceList/Flashing/Expression/TagParameter maps to native Create<T>(propertyName). properties contains exact scalar property names and values, with colors as #AARRGGBB. mappingEntries appends [{kind:Simple|Range|Bitmask,properties:{...}}] entries. delete needs confirmDelete=true. Default preview; readback verified; tag/formula semantics not validated; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageUnifiedDynamizationV4(
            string softwarePath,
            PropertyStep[] objectPath,
            string propertyName,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("dynamizationKind: dynamization kind (see the tool description).")] string dynamizationKind="",
            AttributeMap<Scalar>? properties = null,
            [Description("mappingEntries: JSON array of mapping entries.")] DynamizationMapping[]? mappingEntries = null,
            bool confirmDelete=false,
            bool dryRun=true)
            => UnifiedHmiContract.Run("ManageUnifiedDynamization", !dryRun && action != "read", true, () => ManageUnifiedDynamization(softwarePath, V4Json.Serialize(objectPath), propertyName, action, dynamizationKind, V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), UnifiedHmiContract.MappingEntries(mappingEntries), confirmDelete, dryRun));

        public ResponseMessage ManageUnifiedDynamization(string softwarePath,
            string objectPathJson,
            string propertyName,
            string action="read",
            string dynamizationKind="",
            string propertiesJson="{}",
            string mappingEntriesJson="[]",
            bool confirmDelete=false,
            bool dryRun=true)
            => _service.ManageUnifiedDynamization(softwarePath,objectPathJson,propertyName,action,dynamizationKind,propertiesJson,mappingEntriesJson,confirmDelete,dryRun);

        [McpServerTool(Name="ManageUnifiedScreenLayout"), Description("[L2][HMI-Unified][WRITE] Exact Unified screen: read scalars incl. background colors and size, update public scalar/Color properties, resize (native ResizeScreen to device display), rename, create (path to a Screens composition plus name) or delete (confirmDelete=true; removes all items). No screen copy/duplicate exists in the public V21/V20 API; layout-field export/import is the SiVArc option's LayoutData screen service (ManageSivarcScreenLayout, V21). Default preview; readback verified; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageUnifiedScreenLayoutV4(
            string softwarePath,
            PropertyStep[] objectPath,
            [Description("action: the operation to perform - read | create | rename | update | resize | delete.")] string action="read",
            string name="",
            AttributeMap<Scalar>? properties = null,
            bool confirmDelete=false,
            bool dryRun=true)
            => UnifiedHmiContract.Run("ManageUnifiedScreenLayout", !dryRun && action != "read", true, () => ManageUnifiedScreenLayout(softwarePath, V4Json.Serialize(objectPath), action, name, V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), confirmDelete, dryRun));

        public ResponseMessage ManageUnifiedScreenLayout(string softwarePath,
            string objectPathJson,
            string action="read",
            string name="",
            string propertiesJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => _service.ManageUnifiedScreenLayout(softwarePath,objectPathJson,action,name,propertiesJson,confirmDelete,dryRun);

        [McpServerTool(Name="ManageUnifiedListEntries"), Description("[L2][HMI-Unified][WRITE] Locate one exact textLists/graphicLists/systemTextLists list and report its public shape and official self-description. The public V21/V20 HmiUnified.TextGraphicList API exposes no entry composition, so create/update/delete of entries return NotSupported without changing anything; use ExportUnifiedEngineeringList/ImportUnifiedEngineeringList for entry content. Default preview.")]
        public CallToolResult ManageUnifiedListEntriesV4(
            string softwarePath,
            string category,
            [Description("listName: exact list name.")] string listName,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("entryKey: key of the list entry.")] string entryKey="",
            [Description("entry: JSON object of the entry's properties.")] AttributeMap<Scalar>? entry = null,
            bool confirmDelete=false,
            bool dryRun=true)
            => UnifiedHmiContract.Run("ManageUnifiedListEntries", false, false, () => ManageUnifiedListEntries(softwarePath, category, listName, action, entryKey, V4Json.Serialize(entry ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), confirmDelete, dryRun));

        public ResponseMessage ManageUnifiedListEntries(string softwarePath,
            string category,
            string listName,
            string action="read",
            string entryKey="",
            string entryJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => _service.ManageUnifiedListEntries(softwarePath,category,listName,action,entryKey,entryJson,confirmDelete,dryRun);

        [McpServerTool(Name="GetUnifiedAlarmCommon"), Description("[L2][HMI-Unified][READ] Read-only HmiAlarmCommon dump: alarmClasses with the four AlarmStatusVisuals states (colors as #AARRGGBB, flashing), or discreteAlarms/analogAlarms with AlarmBase scalars and every EventText/InfoText language. Exact optional name; live offset/limit pagination; AlarmParameterTags excluded.")]
        public CallToolResult ReadUnifiedAlarmCommonV4(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => UnifiedHmiContract.Run("GetUnifiedAlarmCommon", false, false, () => ReadUnifiedAlarmCommon(softwarePath, category, name, offset, limit), offset, limit);

        public ResponseMessage ReadUnifiedAlarmCommon(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => _service.ReadUnifiedAlarmCommon(softwarePath,category,name,offset,limit);

        [McpServerTool(Name="GetUnifiedAuditSettings"), Description("[L2][HMI-Unified][READ] Read-only HmiAudit dump: alarmAuditClasses scalars (comment required, confirmation mode, GMP, function rights) or auditTrails with nested Backup/Segment/Settings and log durations. Exact optional name; live offset/limit pagination; no edit.")]
        public CallToolResult ReadUnifiedAuditSettingsV4(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => UnifiedHmiContract.Run("GetUnifiedAuditSettings", false, false, () => ReadUnifiedAuditSettings(softwarePath, category, name, offset, limit), offset, limit);

        public ResponseMessage ReadUnifiedAuditSettings(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => _service.ReadUnifiedAuditSettings(softwarePath,category,name,offset,limit);
    }
}
