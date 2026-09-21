using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadUnifiedObjectEvents"), Description("[L2][HMI-Unified][READ] Enumerate every EventHandlers and PropertyEventHandlers entry (event type, property name, raw script fields) of one exact Unified screen or screen item addressed by object JSON path [{property:Screens,name:X},{property:ScreenItems,name:Y}]. Also lists the event types the native composition can create. Live offset/limit pagination; no script execution, SyntaxCheck or write.")]
        public static ResponseMessage ReadUnifiedObjectEvents(string softwarePath,string objectPathJson,int offset=0,int limit=100)
            => Portal.ReadUnifiedObjectEvents(softwarePath,objectPathJson,offset,limit);
        [McpServerTool(Name="ManageUnifiedObjectParts"), Description("[L2][HMI-Unified][WRITE] UI.Parts sub-objects (trend areas, trends, axes, thresholds, columns, selection items, help lines, scaling entries, pressed-state tags...) of one exact object path. read without collectionProperty lists collections; read with exact collectionProperty lists parts with partIndex. create uses the native Create(string)/Create() of that composition (partName only for named parts; columns/thresholds have no Create). update writes public scalar/Color (#AARRGGBB) properties via propertiesJson; delete needs exact partName or partIndex plus confirmDelete=true. Default preview; readback verified; no save/compile/download.")]
        public static ResponseMessage ManageUnifiedObjectParts(
            string softwarePath,
            string objectPathJson,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("collectionProperty: name of the collection property that holds the parts.")] string collectionProperty="",
            [Description("partName: exact name of the part.")] string partName="",
            [Description("partIndex: 0-based index of the part (-1 = by name).")] int partIndex=-1,
            [Description("partKind: kind of part (see the tool description).")] string partKind="",
            string propertiesJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageUnifiedObjectParts(softwarePath,objectPathJson,action,collectionProperty,partName,partIndex,partKind,propertiesJson,confirmDelete,dryRun);
        [McpServerTool(Name="ManageUnifiedDynamization"), Description("[L2][HMI-Unified][WRITE] Read/create/update/delete the dynamization of one exact property (propertyName) on an exact Unified screen/item object path. dynamizationKind Tag/Script/ResourceList/Flashing/Expression/TagParameter maps to native Create<T>(propertyName). propertiesJson may nest ValueConverter{Formula,IsFormulaSelected,MappingTable{ConditionType}}, Trigger{Type,CustomDuration}, Flashing colors as #AARRGGBB. mappingEntriesJson appends [{kind:Simple|Range|Bitmask,...}] entries. delete needs confirmDelete=true. Default preview; readback verified; tag/formula semantics not validated; no save/compile/download.")]
        public static ResponseMessage ManageUnifiedDynamization(
            string softwarePath,
            string objectPathJson,
            string propertyName,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("dynamizationKind: dynamization kind (see the tool description).")] string dynamizationKind="",
            string propertiesJson="{}",
            [Description("mappingEntriesJson: JSON array of mapping entries.")] string mappingEntriesJson="[]",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageUnifiedDynamization(softwarePath,objectPathJson,propertyName,action,dynamizationKind,propertiesJson,mappingEntriesJson,confirmDelete,dryRun);
        [McpServerTool(Name="ManageUnifiedScreenLayout"), Description("[L2][HMI-Unified][WRITE] Exact Unified screen: read scalars incl. background colors and size, update public scalar/Color properties, resize (native ResizeScreen to device display), rename, create (path to a Screens composition plus name) or delete (confirmDelete=true; removes all items). No screen copy/duplicate exists in the public V21/V20 API; layout-field export/import is the SiVArc option's LayoutData screen service (ManageSivarcScreenLayout, V21). Default preview; readback verified; no save/compile/download.")]
        public static ResponseMessage ManageUnifiedScreenLayout(
            string softwarePath,
            string objectPathJson,
            [Description("action: the operation to perform - read | create | rename | update | resize | delete.")] string action="read",
            string name="",
            string propertiesJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageUnifiedScreenLayout(softwarePath,objectPathJson,action,name,propertiesJson,confirmDelete,dryRun);
        [McpServerTool(Name="ManageUnifiedListEntries"), Description("[L2][HMI-Unified][WRITE] Locate one exact textLists/graphicLists/systemTextLists list and report its public shape and official self-description. The public V21/V20 HmiUnified.TextGraphicList API exposes no entry composition, so create/update/delete of entries return NotSupported without changing anything; use ExportUnifiedEngineeringList/ImportUnifiedEngineeringList for entry content. Default preview.")]
        public static ResponseMessage ManageUnifiedListEntries(
            string softwarePath,
            string category,
            [Description("listName: exact list name.")] string listName,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("entryKey: key of the list entry.")] string entryKey="",
            [Description("entryJson: JSON object of the entry's properties.")] string entryJson="{}",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageUnifiedListEntries(softwarePath,category,listName,action,entryKey,entryJson,confirmDelete,dryRun);
        [McpServerTool(Name="ReadUnifiedAlarmCommon"), Description("[L2][HMI-Unified][READ] Read-only HmiAlarmCommon dump: alarmClasses with the four AlarmStatusVisuals states (colors as #AARRGGBB, flashing), or discreteAlarms/analogAlarms with AlarmBase scalars and every EventText/InfoText language. Exact optional name; live offset/limit pagination; AlarmParameterTags excluded.")]
        public static ResponseMessage ReadUnifiedAlarmCommon(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => Portal.ReadUnifiedAlarmCommon(softwarePath,category,name,offset,limit);
        [McpServerTool(Name="ReadUnifiedAuditSettings"), Description("[L2][HMI-Unified][READ] Read-only HmiAudit dump: alarmAuditClasses scalars (comment required, confirmation mode, GMP, function rights) or auditTrails with nested Backup/Segment/Settings and log durations. Exact optional name; live offset/limit pagination; no edit.")]
        public static ResponseMessage ReadUnifiedAuditSettings(string softwarePath,string category,string name="",int offset=0,int limit=100)
            => Portal.ReadUnifiedAuditSettings(softwarePath,category,name,offset,limit);
    }
}
