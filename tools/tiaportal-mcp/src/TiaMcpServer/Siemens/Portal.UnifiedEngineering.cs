using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ImportUnifiedEngineeringList(string softwarePath, string category, string filePath, string expectedNamesJson, bool dryRun = true)
            => RunHmiStepTool("ImportUnifiedEngineeringList", meta => {
                if (category != "textLists" && category != "graphicLists") throw new ArgumentException("category must be textLists or graphicLists.");
                var names = JsonNode.Parse(expectedNamesJson)?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? throw new ArgumentException("expectedNamesJson must be an array.");
                if (names.Length == 0 || names.Length > 500 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new ArgumentException("Provide 1..500 unique exact expected list names for readback.");
                var file = new System.IO.FileInfo(filePath); if (!file.Exists) throw new System.IO.FileNotFoundException("Native list import file not found.", filePath);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Unified HMI required.");
                var collection = EngineeringGroupOperations.Get(hmi, UnifiedCollection(category));
                if (collection.GetType().GetMethod("Import", new[] { typeof(System.IO.DirectoryInfo), typeof(string) }) == null) throw new NotSupportedException("Native list Import(DirectoryInfo,string) unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["expectedCount"] = names.Length;
                meta["scope"] = "Native file import; expected list-name presence only. File can also affect other lists. Full entries/bindings are not independently verified.";
                if (dryRun) return "Native text/graphic list import preview; no changes.";
                meta["mayHaveChanged"] = true;
                var ok = EngineeringGroupOperations.Call(collection, "Import", new[] { typeof(System.IO.DirectoryInfo), typeof(string) }, file.Directory!, file.Name);
                meta["nativeSuccess"] = ok is bool result && result;
                if (ok is not bool success || !success) throw new InvalidOperationException("Native list Import did not return true; project may have changed.");
                var missing = names.Where(n => EngineeringGroupOperations.Find(collection, n) == null).ToArray();
                meta["missingNames"] = new JsonArray(missing.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                meta["actualCount"] = names.Length - missing.Length; meta["fullContentVerified"] = false;
                if (missing.Length != 0) throw new InvalidOperationException("Expected list names absent after import.");
                return "Native list import returned true and expected names are present. Full content verification remains separate; no explicit save or download.";
            });

        private static string UnifiedCollection(string category) => category switch {
            "alarmClasses" => "AlarmClasses", "discreteAlarms" => "DiscreteAlarms", "analogAlarms" => "AnalogAlarms",
            "alarmLogs" => "AlarmLogs", "dataLogs" => "DataLogs", "textLists" => "HmiTextLists", "graphicLists" => "HmiGraphicLists",
            "systemTags" => "SystemTags", "systemTextLists" => "HmiSystemTextLists", "auditTrails" => "AuditTrails", "opcUaAlarmTypes" => "OpcUaAlarmTypes",
            _ => throw new ArgumentException("Unknown Unified collection category.") };
        public ResponseMessage ReadUnifiedEngineeringObjects(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadUnifiedEngineeringObjects", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Unified HMI required.");
                var collection = EngineeringGroupOperations.Get(hmi, UnifiedCollection(category));
                var items = string.IsNullOrEmpty(name) ? EngineeringGroupOperations.Items(collection).ToArray()
                    : new[] { EngineeringGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Exact object not found: " + name) };
                var rows = new JsonArray(items.Skip(offset).Take(limit).Select(x => (JsonNode)EngineeringScalarProperties.Read(x)).ToArray());
                meta["category"] = category; meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null;
                meta["truncated"] = offset + rows.Count < items.Length;
                meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(x => x!["dataComplete"]!.GetValue<bool>());
                meta["scope"] = "Scalar properties of selected collection; complex bindings/entries require ReadUnifiedScreenBranch or dedicated readers. Pagination is live, not a frozen snapshot.";
                return "Unified engineering objects read; inspect scope and pagination, not a full object export.";
            });
        public ResponseMessage ManageUnifiedEngineeringObject(string softwarePath, string category, string name, string action,
            string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedEngineeringObject", meta => {
                if (action != "create" && action != "update" && action != "delete") throw new ArgumentException("action must be create, update or delete.");
                if (category == "systemTags" || category == "systemTextLists") throw new NotSupportedException("System collections are read-only in this adapter.");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact name required.");
                var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (changes.ContainsKey("Name")) throw new ArgumentException("Name changes are not supported here; supply the exact name parameter.");
                if (action == "delete" && changes.Count != 0) throw new ArgumentException("Delete does not accept properties.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
                if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Unified HMI required.");
                var collection = EngineeringGroupOperations.Get(hmi, UnifiedCollection(category));
                var target = EngineeringGroupOperations.Find(collection, name);
                meta["category"] = category; meta["name"] = name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "create" && target != null) throw new InvalidOperationException("Object already exists; use update explicitly.");
                if (action != "create" && target == null) throw new InvalidOperationException("Exact object not found: " + name);
                var create = collection.GetType().GetMethod("Create", new[] { typeof(string) });
                var type = target?.GetType() ?? create?.ReturnType ?? throw new NotSupportedException("Native Create(string) is not exposed for " + category);
                var prepared = EngineeringScalarProperties.Prepare(type, changes);
                if (action == "delete" && type.GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete is not exposed.");
                if (target != null) meta["before"] = EngineeringScalarProperties.Read(target);
                meta["requestedProperties"] = changes.DeepClone();
                meta["dependencyImpact"] = "Not analyzed. Deletion may invalidate references; preview does not imply absence of references.";
                if (dryRun) return "Preview only. Public API shape and scalar property conversions checked; TIA semantic validation occurs during execution.";
                meta["mayHaveChanged"] = true;
                if (action == "create") target = EngineeringGroupOperations.Call(collection, "Create", new[] { typeof(string) }, name);
                if (action == "delete")
                {
                    EngineeringGroupOperations.Call(target!, "Delete", Type.EmptyTypes);
                    if (EngineeringGroupOperations.Find(collection, name) != null) throw new InvalidOperationException("Delete returned but object remains.");
                    meta["verifiedAbsent"] = true;
                }
                else
                {
                    EngineeringScalarProperties.Apply(target!, prepared, meta);
                    meta["after"] = EngineeringScalarProperties.Read(target!);
                }
                meta["persistence"] = "Not saved, compiled or downloaded. Partial edits are not rolled back.";
                return "Native Unified object operation completed; requested scalar writes read back. Full semantic validity is not asserted.";
            });
    }
}
