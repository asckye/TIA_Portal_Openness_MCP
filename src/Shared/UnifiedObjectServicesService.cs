using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedObjectServicesService : UnifiedPortService
    {
        public UnifiedObjectServicesService() { }
        internal UnifiedObjectServicesService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadUnifiedObjectProperties(string softwarePath, string objectPathJson = "[]", int offset = 0, int limit = 100)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedObjectProperties", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, Offset = offset, Limit = limit }, true);
        public ResponseMessage UpdateUnifiedObjectProperties(string softwarePath, string objectPathJson, string propertiesJson, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "SetUnifiedObjectProperties", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, PropertiesJson = propertiesJson, DryRun = dryRun }, dryRun);
        public ResponseMessage UpdateUnifiedMultilingualProperty(string softwarePath, string objectPathJson, string property, string culture, string rawText, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "SetUnifiedMultilingualProperty", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, Property = property, Culture = culture, RawText = rawText, DryRun = dryRun }, dryRun);
        public ResponseMessage ValidateUnifiedObject(string softwarePath, string objectPathJson)
            => Invoke(new UnifiedRequest { Operation = "ValidateUnifiedObject", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson }, true);
        public ResponseMessage GetUnifiedCrossReferences(string softwarePath, string objectPathJson, string filter = "AllObjects")
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedCrossReferences", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, Filter = filter }, true);
        public ResponseMessage ExportUnifiedEngineeringList(string softwarePath, string category, string name, string destinationDirectory, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ExportUnifiedEngineeringList", SoftwarePath = softwarePath, Category = category, Name = name, DestinationDirectory = destinationDirectory, DryRun = dryRun }, dryRun);
        public ResponseMessage ManageUnifiedLoggingTag(string softwarePath, string tagPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedLoggingTag", SoftwarePath = softwarePath, TagPathJson = tagPathJson, Action = action, Name = name, PropertiesJson = propertiesJson, DryRun = dryRun }, dryRun);
        public ResponseMessage SetUnifiedLogDuration(string softwarePath, string durationPathJson, string kind, uint days, uint hours, uint minutes, uint seconds, uint hundredNanoseconds, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "SetUnifiedLogDuration", SoftwarePath = softwarePath, DurationPathJson = durationPathJson, Kind = kind, Days = days, Hours = hours, Minutes = minutes, Seconds = seconds, HundredNanoseconds = hundredNanoseconds, DryRun = dryRun }, dryRun);
        public ResponseMessage ManageUnifiedOpcUaAlarmType(string softwarePath, string name, string nodeId, string connection, string action = "create", bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedOpcUaAlarmType", SoftwarePath = softwarePath, Name = name, NodeId = nodeId, Connection = connection, Action = action, DryRun = dryRun }, dryRun);
        public ResponseMessage ReadUnifiedPlantObject(string plantPath="", string objectPathJson="[]", int offset=0, int limit=100)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedPlantObject", PlantPath = plantPath, ObjectPathJson = objectPathJson, Offset = offset, Limit = limit }, true);
        public ResponseMessage ManageUnifiedPlantNode(string plantPath, string action, string plantObjectType="", string propertiesJson="{}", bool dryRun=true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedPlantNode", PlantPath = plantPath, Action = action, PlantObjectType = plantObjectType, PropertiesJson = propertiesJson, DryRun = dryRun }, dryRun);
        public ResponseMessage UpdateUnifiedPlantObject(string plantPath, string objectPathJson, string propertiesJson, bool dryRun=true)
            => Invoke(new UnifiedRequest { Operation = "SetUnifiedPlantObject", PlantPath = plantPath, ObjectPathJson = objectPathJson, PropertiesJson = propertiesJson, DryRun = dryRun }, dryRun);
    }
}
