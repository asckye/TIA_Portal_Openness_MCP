using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadUnifiedPlantObject"), Description("[L2][HMI-Unified][READ] Exact project PlantView/node and optional property-only JSON path to CPM interfaces/members. Empty plantPath lists views. Live pagination, scalar fields and schema only; no implicit recursive completeness claim.")]
        public static ResponseMessage ReadUnifiedPlantObject(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath="",
            string objectPathJson="[]",
            int offset=0,
            int limit=100)
            => Portal.ReadUnifiedPlantObject(plantPath,objectPathJson,offset,limit);
        [McpServerTool(Name="ManageUnifiedPlantNode"), Description("[L2][HMI-Unified][WRITE] Exact PlantView/node create/update/delete. Optional native CPM type for child creation. Leaf/empty deletion only. Default preview, no implicit save/compile/download.")]
        public static ResponseMessage ManageUnifiedPlantNode(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath,
            [Description("action: the operation to perform - create | update | delete.")] string action,
            [Description("plantObjectType: exact plant object type name.")] string plantObjectType="",
            string propertiesJson="{}",
            bool dryRun=true)
            => Portal.ManageUnifiedPlantNode(plantPath,action,plantObjectType,propertiesJson,dryRun);
        [McpServerTool(Name="UpdateUnifiedPlantObject"), Description("[L2][HMI-Unified][WRITE] Public scalar CPM interface/member properties at exact plant path and property-only JSON address. Default preview, checks readback, no implicit save/compile/download.")]
        public static ResponseMessage UpdateUnifiedPlantObject(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath,
            string objectPathJson,
            string propertiesJson,
            bool dryRun=true)
            => Portal.UpdateUnifiedPlantObject(plantPath,objectPathJson,propertiesJson,dryRun);
        [McpServerTool(Name="ReadUnifiedObjectProperties"), Description("[L2][HMI-Unified][READ] Bounded property-only JSON path from Unified software: [{property:TagTables,name:Table},{property:Tags,name:Tag}]. Exact names; no Parent/backlinks. Returns scalar values and schema, excludes complex values, live pagination. Nested RuntimeSettings, system collections, logging tags supported by actual property availability.")]
        public static ResponseMessage ReadUnifiedObjectProperties(string softwarePath, string objectPathJson="[]", int offset=0, int limit=100)
            => Portal.ReadUnifiedObjectProperties(softwarePath,objectPathJson,offset,limit);
        [McpServerTool(Name="UpdateUnifiedObjectProperties"), Description("[L2][HMI-Unified][WRITE] Edit public scalar properties on a precisely addressed Unified object or nested settings. dryRun=true; no renaming, parent traversal, root RuntimeSettings edit, save/compile/download. TimeSpan uses invariant c string. Partial changes reported; readback checked.")]
        public static ResponseMessage UpdateUnifiedObjectProperties(string softwarePath, string objectPathJson, string propertiesJson, bool dryRun=true)
            => Portal.UpdateUnifiedObjectProperties(softwarePath,objectPathJson,propertiesJson,dryRun);
        [McpServerTool(Name="UpdateUnifiedMultilingualProperty"), Description("[L2][HMI-Unified][WRITE] Edit one existing language entry of a precisely addressed Unified object property. Raw text preserved; verifies requested language and unchanged other languages. Default preview; no save/compile/download.")]
        public static ResponseMessage UpdateUnifiedMultilingualProperty(
            string softwarePath,
            string objectPathJson,
            [Description("property: exact property name.")] string property,
            string culture,
            [Description("rawText: the text to write.")] string rawText,
            bool dryRun=true)
            => Portal.UpdateUnifiedMultilingualProperty(softwarePath,objectPathJson,property,culture,rawText,dryRun);
        [McpServerTool(Name="ValidateUnifiedObject"), Description("[L2][HMI-Unified][READ] Call native parameterless Validate on one exact Unified object. Returns errors/warnings; apiCallSuccess and validationPassed differ. No compile, script SyntaxCheck or project edit.")]
        public static ResponseMessage ValidateUnifiedObject(string softwarePath, string objectPathJson)
            => Portal.ValidateUnifiedObject(softwarePath,objectPathJson);
        [McpServerTool(Name="GetUnifiedCrossReferences"), Description("[L2][HMI-Unified][READ] Native Unified cross references for exact object. Strict errors; bounded Sources/References/Locations. Does not expand runtime script names or claim all native fields complete.")]
        public static ResponseMessage GetUnifiedCrossReferences(
            string softwarePath,
            string objectPathJson,
            [Description("filter: filter text ('' = all).")] string filter="AllObjects")
            => Portal.GetUnifiedCrossReferences(softwarePath,objectPathJson,filter);
        [McpServerTool(Name="ExportUnifiedEngineeringList"), Description("[L2][HMI-Unified][FILE] Export exactly named textLists/graphicLists/systemTextLists through native Export when available. New destination directory only; output files nonempty and hashed. Default preview; no project modification; entries not independently verified.")]
        public static ResponseMessage ExportUnifiedEngineeringList(string softwarePath, string category, string name, string destinationDirectory, bool dryRun=true)
            => Portal.ExportUnifiedEngineeringList(softwarePath,category,name,destinationDirectory,dryRun);
        [McpServerTool(Name="ManageUnifiedLoggingTag"), Description("[L2][HMI-Unified][WRITE] Read/create/update/delete LoggingTags under exact ordinary Unified tag. DataLog name validated. Public scalar configuration, TimeSpan c strings, no implicit save/compile/download. Default dryRun=true.")]
        public static ResponseMessage ManageUnifiedLoggingTag(
            string softwarePath,
            [Description("tagPathJson: JSON array path of the logging tag.")] string tagPathJson,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string name="",
            string propertiesJson="{}",
            bool dryRun=true)
            => Portal.ManageUnifiedLoggingTag(softwarePath,tagPathJson,action,name,propertiesJson,dryRun);
        [McpServerTool(Name="SetUnifiedLogDuration"), Description("[L2][HMI-Unified][WRITE] Set native LogDuration or SegmentDuration at exact nested object path. Normalized components; default preview, returns native readback; does not assert independent conversion equality.")]
        public static ResponseMessage SetUnifiedLogDuration(
            string softwarePath,
            [Description("durationPathJson: JSON array path of the duration property.")] string durationPathJson,
            [Description("kind: log | segment.")] string kind,
            [Description("days: days part of the duration.")] uint days,
            [Description("hours: hours part of the duration.")] uint hours,
            [Description("minutes: minutes part of the duration.")] uint minutes,
            [Description("seconds: seconds part of the duration.")] uint seconds,
            [Description("hundredNanoseconds: 100-ns part of the duration.")] uint hundredNanoseconds,
            bool dryRun=true)
            => Portal.SetUnifiedLogDuration(softwarePath,durationPathJson,kind,days,hours,minutes,seconds,hundredNanoseconds,dryRun);
        [McpServerTool(Name="ManageUnifiedOpcUaAlarmType"), Description("[L2][HMI-Unified][WRITE] Native create/update OPC UA alarm type with NodeId and existing HMI connection. Default preview. Native readback supplied, full binding verification separate. Read/delete via engineering-object tools.")]
        public static ResponseMessage ManageUnifiedOpcUaAlarmType(
            string softwarePath,
            string name,
            [Description("nodeId: OPC UA node id.")] string nodeId,
            [Description("connection: exact HMI connection name.")] string connection,
            string action="create",
            bool dryRun=true)
            => Portal.ManageUnifiedOpcUaAlarmType(softwarePath,name,nodeId,connection,action,dryRun);
    }
}
