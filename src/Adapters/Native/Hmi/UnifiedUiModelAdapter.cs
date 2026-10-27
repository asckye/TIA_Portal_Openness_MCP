using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class UnifiedUiModelAdapter
    {
        private readonly IUnifiedSession _session;

        public UnifiedUiModelAdapter(IUnifiedSession session) => _session = session;

        public HmiMessage ReadUnifiedObjectEvents(string softwarePath, string objectPathJson, int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("GetUnifiedObjectEvents", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var capabilities = UnifiedUiModelLogic.EventCapabilities(target);
                if (capabilities["EventHandlers"] == null && capabilities["PropertyEventHandlers"] == null) throw new NotSupportedException("Selected object exposes neither EventHandlers nor PropertyEventHandlers: " + target.GetType().FullName);
                var rows = UnifiedUiModelLogic.EventRows(target);
                meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson); meta["objectType"] = target.GetType().FullName;
                meta["capabilities"] = capabilities; meta["apiCallSuccess"] = true;
                UnifiedUiModelLogic.Page(rows, offset, limit, meta);
                meta["scope"] = "Every existing EventHandlers and PropertyEventHandlers entry with raw script fields; availableEventTypes lists what the native composition can create. Live pagination, no script execution or SyntaxCheck.";
                return "Unified object events read; use ManageUnifiedEvent for create/update/delete.";
            });

        public HmiMessage ManageUnifiedObjectParts(string softwarePath, string objectPathJson, string action = "read", string collectionProperty = "", string partName = "", int partIndex = -1, string partKind = "", string propertiesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedObjectParts", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                var changes = UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                if (action == "create" && partName.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("partName must be a single name. Select /Group/Sub/Table through objectPath property steps, e.g. " + EngineeringInputValidation.ObjectPathExample);
                if (action != "update" && changes.Count != 0) throw new ArgumentException("propertiesJson is only accepted by update.");
                if (action == "update" && changes.Count == 0) throw new ArgumentException("update requires nonempty propertiesJson.");
                bool write = action != "read" && !dryRun; using var access = write ? _session.AcquireHmiEditAccess() : null;
                var owner = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson); meta["objectType"] = owner.GetType().FullName;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "read" && collectionProperty == "")
                {
                    meta["owner"] = UnifiedUiModelLogic.Scalars(owner); meta["collections"] = UnifiedUiModelLogic.PartCollections(owner); meta["apiCallSuccess"] = true;
                    meta["dataComplete"] = meta["owner"]!.Field("dataComplete")!.GetValue<bool>();
                    meta["scope"] = "Owner scalars plus every public collection property with element type, Name/Delete/Create availability. Supply collectionProperty to list members.";
                    return "Unified object part collections listed; members not enumerated.";
                }
                var composition = UnifiedUiModelLogic.Composition(owner, collectionProperty);
                meta["collectionProperty"] = collectionProperty; meta["compositionType"] = composition.GetType().FullName;
                meta["elementType"] = UnifiedUiModelLogic.ElementType(composition)?.FullName; meta["named"] = UnifiedUiModelLogic.Named(composition);
                if (action == "read")
                {
                    var items = PlcGroupOperations.Items(composition).ToArray();
                    var rows = new HmiArray(items.Select((x, i) => { var row = UnifiedUiModelLogic.Scalars(x); row["partIndex"] = i; return (object)row; }).ToArray());
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
                int countBefore = PlcGroupOperations.Items(composition).Count(); meta["countBefore"] = countBefore;
                if (dryRun) return "Unified part " + action + " preview; public API shape and value conversions checked, no native write.";
                if (action == "delete" && !confirmDelete) throw new ArgumentException("delete requires confirmDelete=true.");
                meta["mayHaveChanged"] = true;
                if (action == "create")
                {
                    var created = PlcGroupOperations.Call(composition, "Create", creation.Method.GetParameters().Select(p => p.ParameterType).ToArray(), creation.Args);
                    meta["apiCallSuccess"] = true;
                    var items = PlcGroupOperations.Items(composition).ToArray();
                    var index = Array.IndexOf(items, created);
                    if (index < 0 || items.Length != countBefore + 1) throw new InvalidOperationException("Create returned but the part is not present exactly once in the composition.");
                    meta["partIndex"] = index; meta["after"] = UnifiedUiModelLogic.Scalars(created);
                }
                else if (action == "delete")
                {
                    PlcGroupOperations.Call(part!, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                    var items = PlcGroupOperations.Items(composition).ToArray();
                    if (items.Length != countBefore - 1 || items.Contains(part)) throw new InvalidOperationException("Delete returned but the part remains.");
                    meta["verifiedAbsent"] = true;
                }
                else { UnifiedUiModelLogic.ApplyNested(part!, prepared!, meta); meta["apiCallSuccess"] = true; meta["after"] = UnifiedUiModelLogic.Scalars(part!); }
                meta["countAfter"] = PlcGroupOperations.Items(composition).Count();
                return "Native part " + action + " completed and read back. No save/compile/download; partial edits are not rolled back.";
            });

        public HmiMessage ManageUnifiedDynamization(string softwarePath, string objectPathJson, string propertyName, string action = "read", string dynamizationKind = "", string propertiesJson = "{}", string mappingEntriesJson = "[]", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedDynamization", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                var changes = UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                if ((action == "read" || action == "delete") && (changes.Count != 0 || mappingEntriesJson.Trim() != "[]")) throw new ArgumentException("Properties and mapping entries are only accepted by create/update.");
                bool write = action != "read" && !dryRun; using var access = write ? _session.AcquireHmiEditAccess() : null;
                var target = UnifiedObjectAddress.Resolve(_session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var dynamizations = PlcGroupOperations.Get(target, "Dynamizations");
                var existing = UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName);
                meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson); meta["propertyName"] = propertyName; meta["action"] = action;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["exists"] = existing != null;
                meta["supportedKinds"] = new HmiArray(UnifiedUiModelLogic.DynamizationKinds.Keys.Select(k => (object)HmiValues.Value(k)!).ToArray());
                if (existing != null) { meta["before"] = UnifiedUiModelLogic.Tree(existing, 4); meta["dynamizationKind"] = UnifiedUiModelLogic.KindOf(existing); }
                if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = existing == null || meta["before"]!.Field("dataComplete")!.GetValue<bool>(); return existing == null ? "No dynamization on this property." : "Dynamization read including value converter, mapping table entries and trigger where present."; }
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
                    PlcGroupOperations.Call(existing!, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
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
                    foreach (var step in entryPath) owner = PlcGroupOperations.Get(owner, step);
                    var added = new HmiArray(); meta["appliedEntries"] = added;
                    foreach (var entry in entries)
                    {
                        var created = UnifiedUiModelLogic.GenericCreate(owner, entry.Type, 0).Invoke(owner, Array.Empty<object>()) ?? throw new InvalidOperationException("Native mapping entry Create<T> returned null.");
                        var entryMeta = new HmiObject(); UnifiedUiModelLogic.ApplyNested(created, UnifiedUiModelLogic.PrepareNested(entry.Type, entry.Properties), entryMeta);
                        added.Add(new HmiObject { ["kind"] = entry.Kind, ["applied"] = entryMeta["appliedProperties"]?.DeepClone() });
                    }
                }
                var after = UnifiedUiModelLogic.FindDynamization(dynamizations, propertyName) ?? throw new InvalidOperationException("Dynamization missing after write.");
                if (after.GetType() != type) throw new InvalidOperationException("Readback dynamization type differs: " + after.GetType().FullName);
                meta["exists"] = true; meta["after"] = UnifiedUiModelLogic.Tree(after, 4);
                return "Dynamization " + action + " completed; scalar writes read back. Native semantic validity (tag existence, formula) is not asserted; no save/compile/download.";
            });

        private static object ScreenComposition(object root, string objectPathJson)
        {
            var path = UnifiedObjectAddress.Parse(objectPathJson);
            if (path.Count == 0 || !((HmiObject)path[path.Count - 1]!).ContainsKey("name")) throw new ArgumentException("Screen path must end with a named Screens step.");
            var parent = new HmiArray(path.Take(path.Count - 1).Select(n => n!.DeepClone()).ToArray());
            parent.Add(new HmiObject { ["property"] = path[path.Count - 1]!.Field("property")!.DeepClone() });
            return UnifiedObjectAddress.Resolve(root, parent.ToJsonString());
        }

        public HmiMessage ManageUnifiedScreenLayout(string softwarePath, string objectPathJson, string action = "read", string name = "", string propertiesJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedScreenLayout", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                var changes = UnifiedRequestValues.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if (!new[] { "read", "create", "rename", "update", "resize", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/rename/update/resize/delete.");
                if (action != "update" && changes.Count != 0) throw new ArgumentException("propertiesJson is only accepted by update.");
                if (action == "update" && changes.Count == 0) throw new ArgumentException("update requires nonempty propertiesJson.");
                if ((action == "create" || action == "rename") != (name != "")) throw new ArgumentException("name is required by create/rename only.");
                if (name != "") UnifiedUiModelLogic.ValidateScreenName(name);
                bool write = action != "read" && !dryRun; using var access = write ? _session.AcquireHmiEditAccess() : null;
                var root = _session.ExactUnifiedRoot(softwarePath);
                meta["objectPath"] = UnifiedObjectAddress.Parse(objectPathJson); meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["layoutFields"] = "ExportLayoutFields/ImportLayoutFields are not part of the V21/V20 public HmiScreen API; screen copy/duplicate is not exposed either.";
                if (action == "create")
                {
                    var composition = UnifiedObjectAddress.Resolve(root, objectPathJson);
                    if (!UnifiedUiModelLogic.IsScreenComposition(composition)) throw new ArgumentException("create needs a path to a Screens composition (last step without name).");
                    if (composition.GetType().GetMethod("Create", new[] { typeof(string) }) == null) throw new NotSupportedException("Native HmiScreenComposition.Create(string) unavailable.");
                    if (PlcGroupOperations.Find(composition, name) != null) throw new InvalidOperationException("Screen already exists: " + name);
                    meta["name"] = name;
                    if (dryRun) return "Screen create preview; no native write.";
                    meta["mayHaveChanged"] = true;
                    PlcGroupOperations.Call(composition, "Create", new[] { typeof(string) }, name); meta["apiCallSuccess"] = true;
                    // Create<T>/Create return a proxy distinct from the one Find() returns; verify by exact name, not by reference.
                    var created = PlcGroupOperations.Find(composition, name) ?? throw new InvalidOperationException("Create returned but the screen is not found by name.");
                    meta["after"] = UnifiedUiModelLogic.Scalars(created);
                    return "Screen created and read back. No save/compile/download.";
                }
                var screen = UnifiedObjectAddress.Resolve(root, objectPathJson);
                if (!UnifiedUiModelLogic.IsScreen(screen)) throw new ArgumentException("Path must identify an HmiScreen: " + screen.GetType().FullName);
                var before = UnifiedUiModelLogic.Scalars(screen); meta["before"] = before;
                meta["displayName"] = UnifiedUiModelLogic.MultilingualRows(screen).Field("DisplayName")?.DeepClone();
                meta["screenItemCount"] = PlcGroupOperations.Items(PlcGroupOperations.Get(screen, "ScreenItems")).Count();
                meta["resizeAvailable"] = screen.GetType().GetMethod("ResizeScreen", Type.EmptyTypes) != null;
                if (action == "read") { meta["apiCallSuccess"] = true; meta["dataComplete"] = before["dataComplete"]!.GetValue<bool>(); meta["scope"] = "Screen scalars incl. colors, DisplayName languages, item count. Items/events/dynamizations via their own tools."; return "Screen layout read."; }
                var prepared = action == "update" ? UnifiedUiModelLogic.PrepareNested(screen.GetType(), changes) : null;
                if (action == "resize" && !meta["resizeAvailable"]!.GetValue<bool>()) throw new NotSupportedException("Native HmiScreen.ResizeScreen() unavailable.");
                if (action == "delete" && screen.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native HmiScreenBase.Delete() unavailable.");
                var composition2 = action == "rename" || action == "delete" ? ScreenComposition(root, objectPathJson) : null;
                if (action == "rename")
                {
                    if (screen.GetType().GetProperty("Name")?.SetMethod?.IsPublic != true) throw new NotSupportedException("HmiScreen.Name has no public setter on this version.");
                    if (PlcGroupOperations.Find(composition2!, name) != null) throw new InvalidOperationException("Destination screen name exists: " + name);
                    meta["newName"] = name;
                }
                if (action == "update") meta["requestedProperties"] = changes.DeepClone();
                if (dryRun) return "Screen " + action + " preview; no native write.";
                if (action == "delete" && !confirmDelete) throw new ArgumentException("delete requires confirmDelete=true; the screen and all its items would be removed.");
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "update": UnifiedUiModelLogic.ApplyNested(screen, prepared!, meta); break;
                    case "resize": PlcGroupOperations.Call(screen, "ResizeScreen", Type.EmptyTypes); break;
                    case "rename":
                        screen.GetType().GetProperty("Name")!.SetValue(screen, name);
                        if (!string.Equals(PlcGroupOperations.Get(screen, "Name").ToString(), name, StringComparison.Ordinal)) throw new InvalidOperationException("Rename returned but readback differs.");
                        break;
                    case "delete":
                        PlcGroupOperations.Call(screen, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                        if (PlcGroupOperations.Find(composition2!, before["values"]!.Field("Name")!.ToString()) != null) throw new InvalidOperationException("Delete returned but the screen remains.");
                        meta["verifiedAbsent"] = true;
                        return "Screen deleted and absence verified. No save/compile/download.";
                }
                meta["apiCallSuccess"] = true; meta["after"] = UnifiedUiModelLogic.Scalars(screen);
                if (action == "resize") meta["sizeChanged"] = meta["after"]!.Field("values")!.Field("Width")?.ToJsonString() != before["values"]!.Field("Width")?.ToJsonString() || meta["after"]!.Field("values")!.Field("Height")?.ToJsonString() != before["values"]!.Field("Height")?.ToJsonString();
                return "Screen " + action + " completed and read back. No save/compile/download; partial edits are not rolled back.";
            });

        public HmiMessage ManageUnifiedListEntries(string softwarePath, string category, string listName, string action = "read", string entryKey = "", string entryJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageUnifiedListEntries", meta => {
                if (!new[] { "read", "create", "update", "delete" }.Contains(action)) throw new ArgumentException("action must be read/create/update/delete.");
                if (string.IsNullOrWhiteSpace(listName)) throw new ArgumentException("Exact list name required.");
                var root = _session.ExactUnifiedRoot(softwarePath);
                var property = root.GetType().GetProperty(UnifiedUiModelLogic.ListCollection(category)) ?? throw new NotSupportedException(category + " collection is not exposed by this API version (V20 has no HmiGraphicLists).");
                var collection = property.GetValue(root) ?? throw new InvalidOperationException("List collection is null.");
                var list = PlcGroupOperations.Find(collection, listName) ?? throw new InvalidOperationException("Exact list not found: " + listName);
                meta["category"] = category; meta["listName"] = listName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["list"] = UnifiedUiModelLogic.Scalars(list); meta["selfDescription"] = UnifiedUiModelLogic.SelfDescription(list);
                var typed = list.GetType().GetProperties().Where(p => typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string)).Select(p => p.Name).ToArray();
                meta["typedEntryCollections"] = new HmiArray(typed.Select(n => (object)HmiValues.Value(n)!).ToArray());
                meta["entriesSupported"] = false; meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "The V21/V20 public HmiUnified.TextGraphicList API exposes only Name, Delete and composition Export/Import; list entries have no typed public composition. Entry content is only reachable through ExportUnifiedEngineeringList/ImportUnifiedEngineeringList native files.";
                if (action == "read") return "List located; entry rows are not exposed by the public API (see scope and selfDescription). Export the list for its entries.";
                throw new NotSupportedException("Entry " + action + " is not exposed by the public HmiUnified.TextGraphicList API on V21/V20; edit the native export and re-import it instead. No change was made.");
            });

        public HmiMessage ReadUnifiedAlarmCommon(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("GetUnifiedAlarmCommon", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var collection = PlcGroupOperations.Get(_session.ExactUnifiedRoot(softwarePath), UnifiedUiModelLogic.AlarmCommonCollection(category));
                var items = string.IsNullOrEmpty(name) ? PlcGroupOperations.Items(collection).ToArray()
                    : new[] { PlcGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Exact object not found: " + name) };
                var rows = new HmiArray();
                foreach (var item in items.Skip(offset).Take(limit))
                {
                    var row = UnifiedUiModelLogic.Scalars(item);
                    if (category == "alarmClasses")
                    {
                        var states = new HmiObject();
                        foreach (var state in new[] { "RaisedState", "AcknowledgedState", "ClearedState", "AcknowledgedClearedState" })
                        {
                            var p = item.GetType().GetProperty(state);
                            var value = p?.GetValue(item);
                            states[state] = value == null ? null : UnifiedUiModelLogic.Scalars(value);
                            if (value != null && !states[state]!.Field("dataComplete")!.GetValue<bool>()) row["dataComplete"] = false;
                        }
                        row["stateVisuals"] = states;
                    }
                    else row["texts"] = UnifiedUiModelLogic.MultilingualRows(item);
                    rows.Add(row);
                }
                meta["category"] = category; meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null; meta["truncated"] = offset + rows.Count < items.Length;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!.Field("dataComplete")!.GetValue<bool>());
                meta["scope"] = "HmiAlarmCommon scalars with colors: alarm classes include the four AlarmStatusVisuals states; discrete/analog alarms include AlarmBase scalars and every MultilingualText language. AlarmParameterTags remains excluded. Live pagination.";
                return "Unified alarm common data read.";
            });

        public HmiMessage ReadUnifiedAuditSettings(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("GetUnifiedAuditSettings", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
                var root = _session.ExactUnifiedRoot(softwarePath);
                var propertyName = UnifiedUiModelLogic.AuditCollection(category);
                var property = root.GetType().GetProperty(propertyName) ?? throw new NotSupportedException("HmiSoftware." + propertyName + " is not exposed by this API version.");
                var collection = property.GetValue(root) ?? throw new InvalidOperationException("Audit collection is null.");
                var items = string.IsNullOrEmpty(name) ? PlcGroupOperations.Items(collection).ToArray()
                    : new[] { PlcGroupOperations.Find(collection, name) ?? throw new InvalidOperationException("Exact object not found: " + name) };
                var rows = new HmiArray(items.Skip(offset).Take(limit).Select(x => (object)UnifiedUiModelLogic.Tree(x, category == "auditTrails" ? 3 : 0)).ToArray());
                meta["category"] = category; meta["records"] = rows; meta["expectedCount"] = items.Length; meta["actualCount"] = rows.Count;
                meta["nextOffset"] = offset + rows.Count < items.Length ? offset + rows.Count : (int?)null; meta["truncated"] = offset + rows.Count < items.Length;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == items.Length && rows.All(r => r!.Field("dataComplete")!.GetValue<bool>());
                meta["scope"] = "Read-only: HmiAudit alarm audit classes (scalars) or audit trails with nested Backup/Segment/Settings and log durations. No creation, deletion or edit.";
                return "Unified audit settings read.";
            });
    }
}
