using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManageUnifiedLoggingTag(string softwarePath, string tagPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedLoggingTag", meta => {
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be one of: read/create/update/delete (case-sensitive).");
                bool write = action != "read" && !dryRun;
                using var access = write ? AcquireHmiEditAccess() : null;
                var root = ExactUnifiedRoot(softwarePath); var tag = EngineeringObjectAddress.Resolve(root, tagPathJson);
                if (tag.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiTags.HmiTag") throw new ArgumentException("tagPathJson must identify one ordinary Unified tag.");
                var collection = EngineeringGroupOperations.Get(tag, "LoggingTags");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["tagPath"] = EngineeringObjectAddress.Parse(tagPathJson);
                if (action == "read")
                {
                    var items = string.IsNullOrEmpty(name) ? EngineeringGroupOperations.Items(collection).ToArray() : new[] { EngineeringGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Logging tag not found.") };
                    meta["records"] = new JsonArray(items.Select(x => (JsonNode)EngineeringObjectAddress.Read(x)).ToArray());
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = false; meta["scope"] = "Scalar fields only; complex definitions excluded.";
                    meta["actualCount"] = items.Length; meta["expectedCount"] = items.Length; meta["truncated"] = false;
                    return "Logging tags read in scalar scope; inspect exclusions.";
                }
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact logging tag name required.");
                var target = EngineeringGroupOperations.Find(collection, name);
                if ((action == "create") == (target != null)) throw new InvalidOperationException(action == "create" ? "Logging tag exists." : "Logging tag not found.");
                var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (changes.ContainsKey("Name") || (action == "delete" && changes.Count != 0)) throw new ArgumentException("Renaming or properties on delete are not supported.");
                if (changes.ContainsKey("DataLog") && EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(root, "DataLogs"), changes["DataLog"]!.GetValue<string>()) == null) throw new InvalidOperationException("Requested DataLog does not exist.");
                var type = target?.GetType() ?? collection.GetType().GetMethod("Create", new[] { typeof(string) })?.ReturnType ?? throw new NotSupportedException("Native LoggingTag.Create unavailable.");
                var prepared = EngineeringScalarProperties.Prepare(type, changes);
                meta["requestedProperties"] = changes.DeepClone(); if (target != null) meta["before"] = EngineeringObjectAddress.Read(target);
                if (!write) return "Logging tag preview. TimeSpan properties use invariant c format; TIA semantics checked only on execution.";
                meta["mayHaveChanged"] = true;
                if (action == "create") target = EngineeringGroupOperations.Call(collection, "Create", new[] { typeof(string) }, name);
                if (action == "delete")
                {
                    EngineeringGroupOperations.Call(target!, "Delete", Type.EmptyTypes);
                    if (EngineeringGroupOperations.Find(collection, name) != null) throw new InvalidOperationException("Logging tag remains after deletion.");
                    meta["verifiedAbsent"] = true;
                }
                else { EngineeringScalarProperties.Apply(target!, prepared, meta); meta["after"] = EngineeringObjectAddress.Read(target!); }
                return "Logging tag operation completed and read back; no save/compile/download.";
            });
        public ResponseMessage SetUnifiedLogDuration(string softwarePath, string durationPathJson, string kind, uint days, uint hours, uint minutes, uint seconds, uint hundredNanoseconds, bool dryRun = true)
            => RunHmiStepTool("SetUnifiedLogDuration", meta => {
                if (kind != "log" && kind != "segment") throw new ArgumentException("kind must be log/segment.");
                if (hours > 23 || minutes > 59 || seconds > 59 || hundredNanoseconds > 9999999) throw new ArgumentException("Use normalized time components.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), durationPathJson);
                var expected = "Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon." + (kind == "log" ? "LogDuration" : "SegmentDuration");
                if (target.GetType().FullName != expected) throw new NotSupportedException("Path must identify " + expected);
                string suffix = kind == "log" ? "LogDuration" : "SegmentDuration";
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["before"] = EngineeringScalarProperties.Json(EngineeringGroupOperations.Call(target, "GetString" + suffix, Type.EmptyTypes));
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    EngineeringGroupOperations.Call(target, "Set" + suffix, Enumerable.Repeat(typeof(uint), 5).ToArray(), days, hours, minutes, seconds, hundredNanoseconds);
                    meta["after"] = EngineeringScalarProperties.Json(EngineeringGroupOperations.Call(target, "GetString" + suffix, Type.EmptyTypes));
                    meta["numericReadback"] = EngineeringScalarProperties.Json(EngineeringGroupOperations.Call(target, "GetDouble" + suffix, Type.EmptyTypes));
                    meta["exactValueVerified"] = false;
                }
                return dryRun ? "Duration edit preview." : "Native duration setter completed; string/numeric readback supplied, independent unit conversion not asserted. No save/compile/download.";
            });
        public ResponseMessage ManageUnifiedOpcUaAlarmType(string softwarePath, string name, string nodeId, string connection, string action = "create", bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedOpcUaAlarmType", meta => {
                if (action != "create" && action != "update") throw new ArgumentException("create/update only; read/delete use existing engineering-object tools.");
                if (new[] { name, nodeId, connection }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Exact name, nodeId and connection required.");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var root = ExactUnifiedRoot(softwarePath);
                if (EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(root, "Connections"), connection) == null) throw new InvalidOperationException("Exact HMI connection not found.");
                var collection = EngineeringGroupOperations.Get(root, "OpcUaAlarmTypes"); var target = EngineeringGroupOperations.Find(collection, name);
                if ((action == "create") == (target != null)) throw new InvalidOperationException(action == "create" ? "Alarm type exists." : "Alarm type not found.");
                var nativeMethod = action == "create" ? collection.GetType().GetMethod("Create", new[] { typeof(string), typeof(string), typeof(string) }) : target!.GetType().GetMethod("SetNodeIdAndConnection", new[] { typeof(string), typeof(string) });
                if (nativeMethod == null) throw new NotSupportedException("Native OPC UA alarm binding method unavailable.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["name"] = name; meta["nodeId"] = nodeId; meta["connection"] = connection;
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    if (action == "create") target = EngineeringGroupOperations.Call(collection, "Create", new[] { typeof(string), typeof(string), typeof(string) }, nodeId, connection, name);
                    else EngineeringGroupOperations.Call(target!, "SetNodeIdAndConnection", new[] { typeof(string), typeof(string) }, nodeId, connection);
                    meta["after"] = EngineeringObjectAddress.Read(target!); meta["exactBindingVerified"] = false;
                }
                return dryRun ? "OPC UA alarm type edit preview." : "Native binding operation completed; readback supplied, inspect binding fields. No save/compile/download.";
            });
    }
}
