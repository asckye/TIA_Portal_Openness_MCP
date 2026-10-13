using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedUiModelService : UnifiedPortService
    {
        public UnifiedUiModelService() { }
        internal UnifiedUiModelService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadUnifiedObjectEvents(string softwarePath, string objectPathJson, int offset = 0, int limit = 100)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedObjectEvents", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, Offset = offset, Limit = limit }, true);
        public ResponseMessage ManageUnifiedObjectParts(string softwarePath, string objectPathJson, string action = "read", string collectionProperty = "", string partName = "", int partIndex = -1, string partKind = "", string propertiesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedObjectParts", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, Action = action, CollectionProperty = collectionProperty, PartName = partName, PartIndex = partIndex, PartKind = partKind, PropertiesJson = propertiesJson, ConfirmDelete = confirmDelete, DryRun = dryRun }, dryRun);
        public ResponseMessage ManageUnifiedDynamization(string softwarePath, string objectPathJson, string propertyName, string action = "read", string dynamizationKind = "", string propertiesJson = "{}", string mappingEntriesJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedDynamization", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, PropertyName = propertyName, Action = action, DynamizationKind = dynamizationKind, PropertiesJson = propertiesJson, MappingEntriesJson = mappingEntriesJson, ConfirmDelete = confirmDelete, DryRun = dryRun }, dryRun);
        public ResponseMessage ManageUnifiedScreenLayout(string softwarePath, string objectPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedScreenLayout", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, Action = action, Name = name, PropertiesJson = propertiesJson, ConfirmDelete = confirmDelete, DryRun = dryRun }, dryRun);
        public ResponseMessage ManageUnifiedListEntries(string softwarePath, string category, string listName, string action = "read", string entryKey = "", string entryJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedListEntries", SoftwarePath = softwarePath, Category = category, ListName = listName, Action = action, EntryKey = entryKey, EntryJson = entryJson, ConfirmDelete = confirmDelete, DryRun = dryRun }, dryRun);
        public ResponseMessage ReadUnifiedAlarmCommon(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedAlarmCommon", SoftwarePath = softwarePath, Category = category, Name = name, Offset = offset, Limit = limit }, true);
        public ResponseMessage ReadUnifiedAuditSettings(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedAuditSettings", SoftwarePath = softwarePath, Category = category, Name = name, Offset = offset, Limit = limit }, true);
    }
}
