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
    internal sealed class UnifiedObjectServicesTools
    {
        private readonly UnifiedObjectServicesService _service;

        public UnifiedObjectServicesTools(UnifiedObjectServicesService service) => _service = service;

        [McpServerTool(Name="GetUnifiedPlantObject"), Description("[L2][HMI-Unified][READ] Exact project PlantView/node and optional property-only JSON path to CPM interfaces/members. Empty plantPath lists views. Live pagination, scalar fields and schema only; no implicit recursive completeness claim.")]
        public CallToolResult ReadUnifiedPlantObjectV4(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath="",
            PropertyStep[] objectPath = null!,
            int offset=0,
            int limit=100)
            => HmiInspectionContract.Run("GetUnifiedPlantObject", false, false, () => ReadUnifiedPlantObject(plantPath, V4Json.Serialize(objectPath ?? Array.Empty<PropertyStep>()), offset, limit), offset: offset, pageSize: limit);

        public ResponseMessage ReadUnifiedPlantObject(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath="",
            string objectPathJson="[]",
            int offset=0,
            int limit=100)
        => _service.ReadUnifiedPlantObject(plantPath,objectPathJson,offset,limit);

        [McpServerTool(Name="ManageUnifiedPlantNode"), Description("[L2][HMI-Unified][WRITE] Exact PlantView/node create/update/delete. Optional native CPM type for child creation. Leaf/empty deletion only. Default preview, no implicit save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageUnifiedPlantNodeV4(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath,
            [Description("action: the operation to perform - create | update | delete.")] string action,
            [Description("plantObjectType: exact plant object type name.")] string plantObjectType="",
            AttributeMap<Scalar> properties = null!,
            bool dryRun=true)
            => HmiInspectionContract.Run("ManageUnifiedPlantNode", !dryRun, true, () => ManageUnifiedPlantNode(plantPath, action, plantObjectType, V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), dryRun));

        public ResponseMessage ManageUnifiedPlantNode(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath,
            [Description("action: the operation to perform - create | update | delete.")] string action,
            [Description("plantObjectType: exact plant object type name.")] string plantObjectType="",
            string propertiesJson="{}",
            bool dryRun=true)
        => _service.ManageUnifiedPlantNode(plantPath,action,plantObjectType,propertiesJson,dryRun);

        [McpServerTool(Name="SetUnifiedPlantObject"), Description("[L2][HMI-Unified][WRITE] Public scalar CPM interface/member properties at exact plant path and property-only JSON address. Default preview, checks readback, no implicit save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedPlantObjectV4(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath,
            PropertyStep[] objectPath,
            AttributeMap<Scalar> properties,
            bool dryRun=true)
            => HmiInspectionContract.Run("SetUnifiedPlantObject", !dryRun, true, () => UpdateUnifiedPlantObject(plantPath, V4Json.Serialize(objectPath), V4Json.Serialize(properties), dryRun));

        public ResponseMessage UpdateUnifiedPlantObject(
            [Description("plantPath: plant-object path inside the HMI plant hierarchy, e.g. 'Plant/Line1/Pump'.")] string plantPath,
            string objectPathJson,
            string propertiesJson,
            bool dryRun=true)
        => _service.UpdateUnifiedPlantObject(plantPath,objectPathJson,propertiesJson,dryRun);

        [McpServerTool(Name="GetUnifiedObjectProperties"), Description("[L2][HMI-Unified][READ] Bounded property-only JSON path from Unified software: [{property:TagTables,name:Table},{property:Tags,name:Tag}]. Exact names; no Parent/backlinks. Returns scalar values and schema, excludes complex values, live pagination. Nested RuntimeSettings, system collections, logging tags supported by actual property availability.")]
        public CallToolResult ReadUnifiedObjectPropertiesV4(string softwarePath, PropertyStep[] objectPath = null!, int offset=0, int limit=100)
            => HmiInspectionContract.Run("GetUnifiedObjectProperties", false, false, () => ReadUnifiedObjectProperties(softwarePath, V4Json.Serialize(objectPath ?? Array.Empty<PropertyStep>()), offset, limit), offset: offset, pageSize: limit);

        public ResponseMessage ReadUnifiedObjectProperties(string softwarePath, string objectPathJson="[]", int offset=0, int limit=100)
        => _service.ReadUnifiedObjectProperties(softwarePath,objectPathJson,offset,limit);

        [McpServerTool(Name="SetUnifiedObjectProperties"), Description("[L2][HMI-Unified][WRITE] Edit public scalar properties on a precisely addressed Unified object or nested settings. dryRun=true; no renaming, parent traversal, root RuntimeSettings edit, save/compile/download. TimeSpan uses invariant c string. Partial changes reported; readback checked. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedObjectPropertiesV4(string softwarePath, PropertyStep[] objectPath, AttributeMap<Scalar> properties, bool dryRun=true)
            => HmiInspectionContract.Run("SetUnifiedObjectProperties", !dryRun, true, () => UpdateUnifiedObjectProperties(softwarePath, V4Json.Serialize(objectPath), V4Json.Serialize(properties), dryRun));

        public ResponseMessage UpdateUnifiedObjectProperties(string softwarePath, string objectPathJson, string propertiesJson, bool dryRun=true)
        => _service.UpdateUnifiedObjectProperties(softwarePath,objectPathJson,propertiesJson,dryRun);

        [McpServerTool(Name="SetUnifiedMultilingualProperty"), Description("[L2][HMI-Unified][WRITE] Edit one existing language entry of a precisely addressed Unified object property. Raw text preserved; verifies requested language and unchanged other languages. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UpdateUnifiedMultilingualPropertyV4(
            string softwarePath,
            PropertyStep[] objectPath,
            [Description("property: exact property name.")] string property,
            string culture,
            [Description("rawText: the text to write.")] string rawText,
            bool dryRun=true)
            => HmiInspectionContract.Run("SetUnifiedMultilingualProperty", !dryRun, true, () => UpdateUnifiedMultilingualProperty(softwarePath, V4Json.Serialize(objectPath), property, culture, rawText, dryRun));

        public ResponseMessage UpdateUnifiedMultilingualProperty(
            string softwarePath,
            string objectPathJson,
            [Description("property: exact property name.")] string property,
            string culture,
            [Description("rawText: the text to write.")] string rawText,
            bool dryRun=true)
        => _service.UpdateUnifiedMultilingualProperty(softwarePath,objectPathJson,property,culture,rawText,dryRun);

        [McpServerTool(Name="ValidateUnifiedObject"), Description("[L2][HMI-Unified][READ] Call native parameterless Validate on one exact Unified object. Returns errors/warnings; apiCallSuccess and validationPassed differ. No compile, script SyntaxCheck or project edit.")]
        public CallToolResult ValidateUnifiedObjectV4(string softwarePath, PropertyStep[] objectPath)
            => HmiInspectionContract.Run("ValidateUnifiedObject", false, false, () => ValidateUnifiedObject(softwarePath, V4Json.Serialize(objectPath)));

        public ResponseMessage ValidateUnifiedObject(string softwarePath, string objectPathJson)
        => _service.ValidateUnifiedObject(softwarePath,objectPathJson);

        [McpServerTool(Name="GetUnifiedCrossReferences"), Description("[L2][HMI-Unified][READ] Native Unified cross references for exact object. Strict errors; bounded Sources/References/Locations. Does not expand runtime script names or claim all native fields complete.")]
        public CallToolResult GetUnifiedCrossReferencesV4(
            string softwarePath,
            PropertyStep[] objectPath,
            [Description("filter: filter text ('' = all).")] string filter="AllObjects")
            => HmiInspectionContract.Run("GetUnifiedCrossReferences", false, false, () => GetUnifiedCrossReferences(softwarePath, V4Json.Serialize(objectPath), filter));

        public ResponseMessage GetUnifiedCrossReferences(
            string softwarePath,
            string objectPathJson,
            [Description("filter: filter text ('' = all).")] string filter="AllObjects")
        => _service.GetUnifiedCrossReferences(softwarePath,objectPathJson,filter);

        [McpServerTool(Name="ExportUnifiedEngineeringList"), Description("[L2][HMI-Unified][FILE] Export exactly named textLists/graphicLists/systemTextLists through native Export when available. New destination directory only; output files nonempty and hashed. Default preview; no project modification; entries not independently verified. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExportUnifiedEngineeringListV4(string softwarePath, string category, string name, string destinationDirectory, bool dryRun=true)
            => HmiInspectionContract.Run("ExportUnifiedEngineeringList", !dryRun, true, () => ExportUnifiedEngineeringList(softwarePath, category, name, destinationDirectory, dryRun));

        public ResponseMessage ExportUnifiedEngineeringList(string softwarePath, string category, string name, string destinationDirectory, bool dryRun=true)
        => _service.ExportUnifiedEngineeringList(softwarePath,category,name,destinationDirectory,dryRun);

        [McpServerTool(Name="ManageUnifiedLoggingTag"), Description("[L2][HMI-Unified][WRITE] Read/create/update/delete LoggingTags under exact ordinary Unified tag. DataLog name validated. Public scalar configuration, TimeSpan c strings, no implicit save/compile/download. Default dryRun=true. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageUnifiedLoggingTagV4(
            string softwarePath,
            [Description("tagPath: up to 24 {property,name?} steps identifying the logging tag.")] PropertyStep[] tagPath,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string name="",
            AttributeMap<Scalar> properties = null!,
            bool dryRun=true)
            => HmiInspectionContract.Run("ManageUnifiedLoggingTag", action != "read" && !dryRun, true,
                () => ManageUnifiedLoggingTag(softwarePath, V4Json.Serialize(tagPath), action, name,
                    V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>())), dryRun));

        public ResponseMessage ManageUnifiedLoggingTag(
            string softwarePath,
            [Description("tagPathJson: JSON array path of the logging tag.")] string tagPathJson,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string name="",
            string propertiesJson="{}",
            bool dryRun=true)
            => _service.ManageUnifiedLoggingTag(softwarePath,tagPathJson,action,name,propertiesJson,dryRun);

        [McpServerTool(Name="SetUnifiedLogDuration"), Description("[L2][HMI-Unified][WRITE] Set native LogDuration or SegmentDuration at exact nested object path. Normalized components; default preview, returns native readback; does not assert independent conversion equality. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SetUnifiedLogDurationV4(
            string softwarePath,
            [Description("durationPath: up to 24 {property,name?} steps identifying the duration property.")] PropertyStep[] durationPath,
            [Description("kind: log | segment.")] string kind,
            [Description("days: days part of the duration.")] uint days,
            [Description("hours: hours part of the duration.")] uint hours,
            [Description("minutes: minutes part of the duration.")] uint minutes,
            [Description("seconds: seconds part of the duration.")] uint seconds,
            [Description("hundredNanoseconds: 100-ns part of the duration.")] uint hundredNanoseconds,
            bool dryRun=true)
            => HmiInspectionContract.Run("SetUnifiedLogDuration", !dryRun, true,
                () => SetUnifiedLogDuration(softwarePath, V4Json.Serialize(durationPath), kind, days, hours, minutes, seconds, hundredNanoseconds, dryRun));

        public ResponseMessage SetUnifiedLogDuration(
            string softwarePath,
            [Description("durationPathJson: JSON array path of the duration property.")] string durationPathJson,
            [Description("kind: log | segment.")] string kind,
            [Description("days: days part of the duration.")] uint days,
            [Description("hours: hours part of the duration.")] uint hours,
            [Description("minutes: minutes part of the duration.")] uint minutes,
            [Description("seconds: seconds part of the duration.")] uint seconds,
            [Description("hundredNanoseconds: 100-ns part of the duration.")] uint hundredNanoseconds,
            bool dryRun=true)
        => _service.SetUnifiedLogDuration(softwarePath,durationPathJson,kind,days,hours,minutes,seconds,hundredNanoseconds,dryRun);

        [McpServerTool(Name="ManageUnifiedOpcUaAlarmType"), Description("[L2][HMI-Unified][WRITE] Native create/update OPC UA alarm type with NodeId and existing HMI connection. Default preview. Native readback supplied, full binding verification separate. Read/delete via engineering-object tools. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageUnifiedOpcUaAlarmTypeV4(
            string softwarePath,
            string name,
            [Description("nodeId: OPC UA node id.")] string nodeId,
            [Description("connection: exact HMI connection name.")] string connection,
            [Description("create | update. ")] string action="create",
            bool dryRun=true)
            => HmiInspectionContract.Run("ManageUnifiedOpcUaAlarmType", !dryRun, true, () => ManageUnifiedOpcUaAlarmType(softwarePath, name, nodeId, connection, action, dryRun));

        public ResponseMessage ManageUnifiedOpcUaAlarmType(
            string softwarePath,
            string name,
            [Description("nodeId: OPC UA node id.")] string nodeId,
            [Description("connection: exact HMI connection name.")] string connection,
            [Description("create | update. ")] string action="create",
            bool dryRun=true)
        => _service.ManageUnifiedOpcUaAlarmType(softwarePath,name,nodeId,connection,action,dryRun);
    }
}
