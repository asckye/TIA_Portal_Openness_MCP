using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ReadUnifiedObjectEvents(string softwarePath, string objectPathJson, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadUnifiedObjectEvents", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                var capabilities = UnifiedUiModelLogic.EventCapabilities(target);
                if (capabilities["EventHandlers"] == null && capabilities["PropertyEventHandlers"] == null) throw new NotSupportedException("Selected object exposes neither EventHandlers nor PropertyEventHandlers: " + target.GetType().FullName);
                var rows = UnifiedUiModelLogic.EventRows(target);
                meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson); meta["objectType"] = target.GetType().FullName;
                meta["capabilities"] = capabilities; meta["apiCallSuccess"] = true;
                UnifiedUiModelLogic.Page(rows, offset, limit, meta);
                meta["scope"] = "Every existing EventHandlers and PropertyEventHandlers entry with raw script fields; availableEventTypes lists what the native composition can create. Live pagination, no script execution or SyntaxCheck.";
                return "Unified object events read; use ManageUnifiedEvent for create/update/delete.";
            });

        public ResponseMessage ManageUnifiedObjectParts(string softwarePath, string objectPathJson, string action = "read", string collectionProperty = "", string partName = "", int partIndex = -1, string partKind = "", string propertiesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedObjectParts", meta => {
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (action != "update" && changes.Count != 0) throw new ArgumentException("propertiesJson is only accepted by update.");
                if (action == "update" && changes.Count == 0) throw new ArgumentException("update requires nonempty propertiesJson.");
                bool write = action != "read" && !dryRun; using var access = write ? AcquireHmiEditAccess() : null;
                var owner = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson); meta["objectType"] = owner.GetType().FullName;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read" && collectionProperty == "")
                {
                    meta["owner"] = UnifiedUiModelLogic.Scalars(owner); meta["collections"] = UnifiedUiModelLogic.PartCollections(owner); meta["apiCallSuccess"] = true;
                    meta["dataComplete"] = meta["owner"]!["dataComplete"]!.GetValue<bool>();
                    meta["scope"] = "Owner scalars plus every public collection property with element type, Name/Delete/Create availability. Supply collectionProperty to list members.";
                    return "Unified object part collections listed; members not enumerated.";
                }
                var composition = UnifiedUiModelLogic.Composition(owner, collectionProperty);
                meta["collectionProperty"] = collectionProperty; meta["compositionType"] = composition.GetType().FullName;
                meta["elementType"] = UnifiedUiModelLogic.ElementType(composition)?.FullName; meta["named"] = UnifiedUiModelLogic.Named(composition);
                if (action == "read")
                {
                    var items = EngineeringGroupOperations.Items(composition).ToArray();
                    var rows = new JsonArray(items.Select((x, i) => { var row = UnifiedUiModelLogic.Scalars(x); row["partIndex"] = i; return (JsonNode)row; }).ToArray());
                    meta["apiCallSuccess"] = true; UnifiedUiModelLogic.Page(rows, 0, 500, meta);
                    meta["scope"] = "Public scalar and Color values of each part with its live partIndex; nested parts/collections excluded and addressable via the object path.";
                    return "Unified parts read.";
                }
                var part = action == "create" ? null : UnifiedUiModelLogic.FindPart(composition, partName, partIndex) ?? throw new InvalidOperationException("Exact part not found in " + collectionProperty + ".");
                var creation = action == "create" ? UnifiedUiModelLogic.PartCreation(composition, partName, partKind) : default;
                if (action == "create" && !string.IsNullOrEmpty(partName) && UnifiedUiModelLogic.FindPart(composition, partName, -1) != null) throw new InvalidOperationException("Part already exists: " + partName);
                var prepared = action == "update" ? UnifiedUiModelLogic.PrepareNested(part!.GetType(), changes) : null;
                if (action == "delete" && part!.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete() is not exposed on " + part.GetType().FullName);
                if (part != null) meta["before"] = UnifiedUiModelLogic.Scalars(part);
                if (action == "update") meta["requestedProperties"] = changes.DeepClone();
                int countBefore = EngineeringGroupOperations.Items(composition).Count(); meta["countBefore"] = countBefore;
                if (dryRun) return "Unified part " + action + " preview; public API shape and value conversions checked, no native write.";
                if (action == "delete" && !confirmDelete) throw new ArgumentException("delete requires confirmDelete=true.");
                meta["mayHaveChanged"] = true;
                if (action == "create")
                {
                    var created = EngineeringGroupOperations.Call(composition, "Create", creation.Method.GetParameters().Select(p => p.ParameterType).ToArray(), creation.Args);
                    meta["apiCallSuccess"] = true;
                    var items = EngineeringGroupOperations.Items(composition).ToArray();
                    var index = Array.IndexOf(items, created);
                    if (index < 0 || items.Length != countBefore + 1) throw new InvalidOperationException("Create returned but the part is not present exactly once in the composition.");
                    meta["partIndex"] = index; meta["after"] = UnifiedUiModelLogic.Scalars(created);
                }
                else if (action == "delete")
                {
                    EngineeringGroupOperations.Call(part!, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                    var items = EngineeringGroupOperations.Items(composition).ToArray();
                    if (items.Length != countBefore - 1 || items.Contains(part)) throw new InvalidOperationException("Delete returned but the part remains.");
                    meta["verifiedAbsent"] = true;
                }
                else { UnifiedUiModelLogic.ApplyNested(part!, prepared!, meta); meta["apiCallSuccess"] = true; meta["after"] = UnifiedUiModelLogic.Scalars(part!); }
                meta["countAfter"] = EngineeringGroupOperations.Items(composition).Count();
                return "Native part " + action + " completed and read back. No save/compile/download; partial edits are not rolled back.";
            });

        public ResponseMessage ManageUnifiedDynamization(string softwarePath, string objectPathJson, string propertyName, string action = "read", string dynamizationKind = "", string propertiesJson = "{}", string mappingEntriesJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedDynamization", meta => {
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if ((action == "read" || action == "delete") && (changes.Count != 0 || mappingEntriesJson.Trim() != "[]")) throw new ArgumentException("Properties and mapping entries are only accepted by create/update.");
                bool write = action != "read" && !dryRun; using var access = write ? AcquireHmiEditAccess() : null;
                var target = EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath), objectPathJson);
                var dynamizations = EngineeringGroupOperations.Get(target, "Dynamizations");
                var existing = UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName);
                meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson); meta["propertyName"] = propertyName; meta["action"] = action;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["exists"] = existing != null;
                meta["supportedKinds"] = new JsonArray(UnifiedUiModelLogic.DynamizationKinds.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
                if (existing != null) { meta["before"] = UnifiedUiModelLogic.Tree(existing, 4); meta["dynamizationKind"] = UnifiedUiModelLogic.KindOf(existing); }
                if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = existing == null || meta["before"]!["dataComplete"]!.GetValue<bool>(); return existing == null ? "No dynamization on this property." : "Dynamization read including value converter, mapping table entries and trigger where present."; }
                if ((action == "create") == (existing != null)) throw new InvalidOperationException(action == "create" ? "Dynamization exists; use update or delete." : "Exact dynamization not found.");
                if (action == "update" && dynamizationKind != "" && dynamizationKind != UnifiedUiModelLogic.KindOf(existing!)) throw new ArgumentException("dynamizationKind differs from the existing " + UnifiedUiModelLogic.KindOf(existing!) + " dynamization.");
                var type = action == "create" ? UnifiedUiModelLogic.DynamizationType(dynamizations, dynamizationKind) : existing!.GetType();
                var create = action == "create" ? UnifiedUiModelLogic.GenericCreate(dynamizations, type, 1) : null;
                var prepared = UnifiedUiModelLogic.PrepareNested(type, changes);
                var entryPath = UnifiedUiModelLogic.MappingTablePath(type);
                var entries = UnifiedUiModelLogic.ParseMappingEntries(mappingEntriesJson, n => dynamizations.GetType().Assembly.GetType(n, false) ?? throw new NotSupportedException("Native mapping entry type unavailable: " + n));
                if (entries.Count != 0 && entryPath.Length == 0) throw new NotSupportedException("Mapping entries only apply to Tag/Expression dynamizations with ValueConverter.MappingTable.");
                if (action == "delete" && type.GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete() unavailable on " + type.FullName);
                if (action == "update" && prepared.Count == 0 && entries.Count == 0) throw new ArgumentException("update requires propertiesJson and/or mappingEntriesJson.");
                meta["dynamizationKind"] = UnifiedUiModelLogic.KindOf(type); meta["requestedProperties"] = changes.DeepClone(); meta["requestedEntryCount"] = entries.Count;
                if (dryRun) return "Dynamization " + action + " preview; native Create<T>/setter shape and conversions checked, no native write.";
                if (action == "delete" && !confirmDelete) throw new ArgumentException("delete requires confirmDelete=true.");
                meta["mayHaveChanged"] = true;
                if (action == "delete")
                {
                    EngineeringGroupOperations.Call(existing!, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                    if (UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName) != null) throw new InvalidOperationException("Delete returned but the dynamization remains.");
                    meta["exists"] = false; meta["verifiedAbsent"] = true;
                    return "Dynamization deleted and absence verified. No save/compile/download.";
                }
                var dyn = existing ?? create!.Invoke(dynamizations, new object[] { propertyName }) ?? throw new InvalidOperationException("Native Create<T> returned null.");
                meta["apiCallSuccess"] = true;
                UnifiedUiModelLogic.ApplyNested(dyn, prepared, meta);
                if (entries.Count != 0)
                {
                    object owner = dyn;
                    foreach (var step in entryPath) owner = EngineeringGroupOperations.Get(owner, step);
                    var added = new JsonArray(); meta["appliedEntries"] = added;
                    foreach (var entry in entries)
                    {
                        var created = UnifiedUiModelLogic.GenericCreate(owner, entry.Type, 0).Invoke(owner, Array.Empty<object>()) ?? throw new InvalidOperationException("Native mapping entry Create<T> returned null.");
                        var entryMeta = new JsonObject(); UnifiedUiModelLogic.ApplyNested(created, UnifiedUiModelLogic.PrepareNested(entry.Type, entry.Properties), entryMeta);
                        added.Add(new JsonObject { ["kind"] = entry.Kind, ["applied"] = entryMeta["appliedProperties"]?.DeepClone() });
                    }
                }
                var after = UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName) ?? throw new InvalidOperationException("Dynamization missing after write.");
                if (after.GetType() != type) throw new InvalidOperationException("Readback dynamization type differs: " + after.GetType().FullName);
                meta["exists"] = true; meta["after"] = UnifiedUiModelLogic.Tree(after, 4);
                return "Dynamization " + action + " completed; scalar writes read back. Native semantic validity (tag existence, formula) is not asserted; no save/compile/download.";
            });

        private static object ScreenComposition(object root, string objectPathJson)
        {
            var path = EngineeringObjectAddress.Parse(objectPathJson);
            if (path.Count == 0 || !((JsonObject)path[path.Count - 1]!).ContainsKey("name")) throw new ArgumentException("Screen path must end with a named Screens step.");
            var parent = new JsonArray(path.Take(path.Count - 1).Select(n => n!.DeepClone()).ToArray());
            parent.Add(new JsonObject { ["property"] = path[path.Count - 1]!["property"]!.DeepClone() });
            return EngineeringObjectAddress.Resolve(root, parent.ToJsonString());
        }
        public ResponseMessage ManageUnifiedScreenLayout(string softwarePath, string objectPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedScreenLayout", meta => {
                if (!new[] { "read", "create", "rename", "update", "resize", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/rename/update/resize/delete.");
                var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (action != "update" && changes.Count != 0) throw new ArgumentException("propertiesJson is only accepted by update.");
                if (action == "update" && changes.Count == 0) throw new ArgumentException("update requires nonempty propertiesJson.");
                if ((action == "create" || action == "rename") != (name != "")) throw new ArgumentException("name is required by create/rename only.");
                if (name != "") UnifiedUiModelLogic.ValidateScreenName(name);
                bool write = action != "read" && !dryRun; using var access = write ? AcquireHmiEditAccess() : null;
                var root = ExactUnifiedRoot(softwarePath);
                meta["objectPath"] = EngineeringObjectAddress.Parse(objectPathJson); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["layoutFields"] = "ExportLayoutFields/ImportLayoutFields are not part of the V21/V20 public HmiScreen API; screen copy/duplicate is not exposed either.";
                if (action == "create")
                {
                    var composition = EngineeringObjectAddress.Resolve(root, objectPathJson);
                    if (!UnifiedUiModelLogic.IsScreenComposition(composition)) throw new ArgumentException("create needs a path to a Screens composition (last step without name).");
                    if (composition.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native HmiScreenComposition.Create(string) unavailable.");
                    if (EngineeringGroupOperations.Find(composition, name) != null) throw new InvalidOperationException("Screen already exists: " + name);
                    meta["name"] = name;
                    if (dryRun) return "Screen create preview; no native write.";
                    meta["mayHaveChanged"] = true;
                    EngineeringGroupOperations.Call(composition, "Create", new[] { typeof(string) }, name); meta["apiCallSuccess"] = true;
                    // Create<T>/Create return a proxy distinct from the one Find() returns; verify by exact name, not by reference.
                    var created = EngineeringGroupOperations.Find(composition, name) ?? throw new InvalidOperationException("Create returned but the screen is not found by name.");
                    meta["after"] = UnifiedUiModelLogic.Scalars(created);
                    return "Screen created and read back. No save/compile/download.";
                }
                var screen = EngineeringObjectAddress.Resolve(root, objectPathJson);
                if (!UnifiedUiModelLogic.IsScreen(screen)) throw new ArgumentException("Path must identify an HmiScreen: " + screen.GetType().FullName);
                var before = UnifiedUiModelLogic.Scalars(screen); meta["before"] = before;
                meta["displayName"] = UnifiedUiModelLogic.MultilingualRows(screen)["DisplayName"]?.DeepClone();
                meta["screenItemCount"] = EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(screen, "ScreenItems")).Count();
                meta["resizeAvailable"] = screen.GetType().GetMethod("ResizeScreen", Type.EmptyTypes) != null;
                if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = before["dataComplete"]!.GetValue<bool>(); meta["scope"] = "Screen scalars incl. colors, DisplayName languages, item count. Items/events/dynamizations via their own tools."; return "Screen layout read."; }
                var prepared = action == "update" ? UnifiedUiModelLogic.PrepareNested(screen.GetType(), changes) : null;
                if (action == "resize" && !meta["resizeAvailable"]!.GetValue<bool>()) throw new NotSupportedException("Native HmiScreen.ResizeScreen() unavailable.");
                if (action == "delete" && screen.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native HmiScreenBase.Delete() unavailable.");
                var composition2 = action == "rename" || action == "delete" ? ScreenComposition(root, objectPathJson) : null;
                if (action == "rename")
                {
                    if (screen.GetType().GetProperty("Name")?.SetMethod?.IsPublic != true) throw new NotSupportedException("HmiScreen.Name has no public setter on this version.");
                    if (EngineeringGroupOperations.Find(composition2!, name) != null) throw new InvalidOperationException("Destination screen name exists: " + name);
                    meta["newName"] = name;
                }
                if (action == "update") meta["requestedProperties"] = changes.DeepClone();
                if (dryRun) return "Screen " + action + " preview; no native write.";
                if (action == "delete" && !confirmDelete) throw new ArgumentException("delete requires confirmDelete=true; the screen and all its items would be removed.");
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "update": UnifiedUiModelLogic.ApplyNested(screen, prepared!, meta); break;
                    case "resize": EngineeringGroupOperations.Call(screen, "ResizeScreen", Type.EmptyTypes); break;
                    case "rename":
                        screen.GetType().GetProperty("Name")!.SetValue(screen, name);
                        if (!string.Equals(EngineeringGroupOperations.Get(screen, "Name").ToString(), name, StringComparison.Ordinal)) throw new InvalidOperationException("Rename returned but readback differs.");
                        break;
                    case "delete":
                        EngineeringGroupOperations.Call(screen, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                        if (EngineeringGroupOperations.Find(composition2!, before["values"]!["Name"]!.ToString()) != null) throw new InvalidOperationException("Delete returned but the screen remains.");
                        meta["verifiedAbsent"] = true;
                        return "Screen deleted and absence verified. No save/compile/download.";
                }
                meta["apiCallSuccess"] = true; meta["after"] = UnifiedUiModelLogic.Scalars(screen);
                if (action == "resize") meta["sizeChanged"] = meta["after"]!["values"]!["Width"]?.ToJsonString() != before["values"]!["Width"]?.ToJsonString() || meta["after"]!["values"]!["Height"]?.ToJsonString() != before["values"]!["Height"]?.ToJsonString();
                return "Screen " + action + " completed and read back. No save/compile/download; partial edits are not rolled back.";
            });

        public ResponseMessage ManageUnifiedListEntries(string softwarePath, string category, string listName, string action = "read", string entryKey = "", string entryJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => RunHmiStepTool("ManageUnifiedListEntries", meta => {
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                if (string.IsNullOrWhiteSpace(listName)) throw new ArgumentException("Exact list name required.");
                var root = ExactUnifiedRoot(softwarePath);
                var property = root.GetType().GetProperty(UnifiedUiModelLogic.ListCollection(category)) ?? throw new NotSupportedException(category + " collection is not exposed by this API version (V20 has no HmiGraphicLists).");
                var collection = property.GetValue(root) ?? throw new InvalidOperationException("List collection is null.");
                var list = EngineeringGroupOperations.Find(collection, listName) ?? throw new InvalidOperationException("Exact list not found: " + listName);
                meta["category"] = category; meta["listName"] = listName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["list"] = UnifiedUiModelLogic.Scalars(list); meta["selfDescription"] = UnifiedUiModelLogic.SelfDescription(list);
                var typed = list.GetType().GetProperties().Where(p => typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string)).Select(p => p.Name).ToArray();
                meta["typedEntryCollections"] = new JsonArray(typed.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                meta["entriesSupported"] = false; meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "The V21/V20 public HmiUnified.TextGraphicList API exposes only Name, Delete and composition Export/Import; list entries have no typed public composition. Entry content is only reachable through ExportUnifiedEngineeringList/ImportUnifiedEngineeringList native files.";
                if (action == "read") return "List located; entry rows are not exposed by the public API (see scope and selfDescription). Export the list for its entries.";
                throw new NotSupportedException("Entry " + action + " is not exposed by the public HmiUnified.TextGraphicList API on V21/V20; edit the native export and re-import it instead. No change was made.");
            });

        public ResponseMessage ReadUnifiedAlarmCommon(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadUnifiedAlarmCommon", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var collection = EngineeringGroupOperations.Get(ExactUnifiedRoot(softwarePath), UnifiedUiModelLogic.AlarmCommonCollection(category));
                var items = string.IsNullOrEmpty(name) ? EngineeringGroupOperations.Items(collection).ToArray()
                    : new[] { EngineeringGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Exact object not found: " + name) };
                var rows = new JsonArray();
                foreach (var item in items.Skip(offset).Take(limit))
                {
                    var row = UnifiedUiModelLogic.Scalars(item);
                    if (category == "alarmClasses")
                    {
                        var states = new JsonObject();
                        foreach (var state in new[] { "RaisedState", "AcknowledgedState", "ClearedState", "AcknowledgedClearedState" })
                        {
                            var p = item.GetType().GetProperty(state);
                            var value = p?.GetValue(item);
                            states[state] = value == null ? null : UnifiedUiModelLogic.Scalars(value);
                            if (value != null && !states[state]!["dataComplete"]!.GetValue<bool>()) row["dataComplete"] = false;
                        }
                        row["stateVisuals"] = states;
                    }
                    else row["texts"] = UnifiedUiModelLogic.MultilingualRows(item);
                    rows.Add(row);
                }
                meta["category"] = category; meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null; meta["truncated"] = offset + rows.Count < items.Length;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                meta["scope"] = "HmiAlarmCommon scalars with colors: alarm classes include the four AlarmStatusVisuals states; discrete/analog alarms include AlarmBase scalars and every MultilingualText language. AlarmParameterTags remains excluded. Live pagination.";
                return "Unified alarm common data read.";
            });

        public ResponseMessage ReadUnifiedAuditSettings(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadUnifiedAuditSettings", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var root = ExactUnifiedRoot(softwarePath);
                var propertyName = UnifiedUiModelLogic.AuditCollection(category);
                var property = root.GetType().GetProperty(propertyName) ?? throw new NotSupportedException("HmiSoftware." + propertyName + " is not exposed by this API version.");
                var collection = property.GetValue(root) ?? throw new InvalidOperationException("Audit collection is null.");
                var items = string.IsNullOrEmpty(name) ? EngineeringGroupOperations.Items(collection).ToArray()
                    : new[] { EngineeringGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Exact object not found: " + name) };
                var rows = new JsonArray(items.Skip(offset).Take(limit).Select(x => (JsonNode)UnifiedUiModelLogic.Tree(x, category == "auditTrails" ? 3 : 0)).ToArray());
                meta["category"] = category; meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null; meta["truncated"] = offset + rows.Count < items.Length;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!["dataComplete"]!.GetValue<bool>());
                meta["scope"] = "Read-only: HmiAudit alarm audit classes (scalars) or audit trails with nested Backup/Segment/Settings and log durations. No creation, deletion or edit.";
                return "Unified audit settings read.";
            });
    }
}
