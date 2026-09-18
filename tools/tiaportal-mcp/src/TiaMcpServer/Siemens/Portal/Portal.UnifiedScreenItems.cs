using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HmiUnified;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Screen items of every UI.Shapes / UI.Widgets / UI.Controls / UI.Screens type: typed catalog and schema by
    // reflection over the loaded WinCC Unified assembly, create by exact type, full read (scalars, colors, parts,
    // multilingual texts, features), nested update with readback, delete. Events and dynamizations keep their
    // dedicated tools (ManageUnifiedEvent / ManageUnifiedDynamization).
    public partial class Portal
    {
        private static readonly System.Reflection.Assembly UnifiedAssembly = typeof(HmiSoftware).Assembly;

        private static JsonObject ScreenItemSnapshot(object item, int depth)
        {
            var row = UnifiedUiModelLogic.Tree(item, depth);
            row["texts"] = UnifiedUiModelLogic.MultilingualRows(item);
            row["group"] = UnifiedScreenItemLogic.Group(item.GetType());
            row["features"] = UnifiedScreenItemLogic.Features(item.GetType());
            foreach (var (name, key) in new[] { ("EventHandlers", "eventHandlerCount"), ("PropertyEventHandlers", "propertyEventHandlerCount"), ("Dynamizations", "dynamizationCount") })
            {
                var p = UnifiedUiModelLogic.FindProperty(item.GetType(), name);
                if (p?.GetMethod?.IsPublic != true) { row[key] = null; continue; }
                try { row[key] = EngineeringGroupOperations.Items(p.GetValue(item) ?? throw new InvalidOperationException(name + " is null.")).Count(); }
                catch (Exception ex) { row[key] = null; ((JsonArray)row["failures"]!).Add(new JsonObject { ["property"] = name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            return row;
        }

        public ResponseMessage DescribeUnifiedScreenItemType(string itemType = "", int depth = 2)
            => RunHmiStepTool("DescribeUnifiedScreenItemType", meta => {
                if (depth < 0 || depth > 4) throw new ArgumentException("depth 0..4 required.");
                meta["assembly"] = UnifiedAssembly.GetName().Name + " " + UnifiedAssembly.GetName().Version;
                if (string.IsNullOrWhiteSpace(itemType))
                {
                    var catalog = UnifiedScreenItemLogic.Catalog(UnifiedAssembly);
                    meta["itemTypes"] = catalog; meta["count"] = catalog.Count; meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                    meta["scope"] = "Every concrete screen item type of the loaded WinCC Unified API (UI.Shapes / Widgets / Controls / Screens) with the IHmi*Feature interfaces it implements; pass one name for its property schema.";
                    return "Unified screen item type catalog from the loaded API; no project access.";
                }
                var type = UnifiedScreenItemLogic.ResolveItemType(UnifiedAssembly, itemType);
                meta["description"] = UnifiedScreenItemLogic.TypeDescription(type, depth); meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "Public properties classified as scalar/color/multilingual/part/collection/reference with CLR type, enum values and writability; parts expanded to depth. From the loaded API, not from a project.";
                return "Unified screen item type described; use ManageUnifiedScreenItem to create/read/update items of this type.";
            }, requiresProject: false);

        public ResponseMessage ManageUnifiedScreenItem(string softwarePath, string screenPath, string action = "read", string itemName = "", string itemType = "",
            string propertiesJson = "{}", int depth = 2, bool confirmDelete = false, int offset = 0, int limit = 100, bool dryRun = true, string containedType = "")
            => RunHmiStepTool("ManageUnifiedScreenItem", meta => {
                var changes = UnifiedScreenItemLogic.ParseProperties(propertiesJson);
                bool writing = UnifiedScreenItemLogic.ValidateRequest(action, itemName, itemType, changes, confirmDelete, dryRun, containedType);
                if (depth < 0 || depth > 4) throw new ArgumentException("depth 0..4 required.");
                using var access = writing ? AcquireHmiEditAccess() : null;
                var root = ExactUnifiedRoot(softwarePath);
                var screen = HmiExactAccess.Screen(root, screenPath);
                if (!UnifiedUiModelLogic.IsScreen(screen)) throw new ArgumentException("screenPath must identify an HmiScreen: " + screen.GetType().FullName);
                var items = EngineeringGroupOperations.Get(screen, "ScreenItems");
                meta["softwarePath"] = softwarePath; meta["screenPath"] = screenPath; meta["screen"] = EngineeringGroupOperations.Get(screen, "Name").ToString();
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list")
                {
                    var all = EngineeringGroupOperations.Items(items).Select((x, i) => (JsonNode)UnifiedScreenItemLogic.ListRow(x, i)).ToArray();
                    UnifiedUiModelLogic.Page(new JsonArray(all), offset, limit, meta);
                    meta["apiCallSuccess"] = true;
                    return "Screen items listed (name, type, geometry); read one item for its full properties.";
                }
                var existing = EngineeringGroupOperations.Find(items, itemName);
                meta["itemName"] = itemName; meta["exists"] = existing != null;
                if (action == "read")
                {
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "Screen item not found: " + itemName);
                    meta["item"] = ScreenItemSnapshot(existing, depth); meta["itemType"] = existing.GetType().FullName; meta["apiCallSuccess"] = true;
                    meta["dataComplete"] = meta["item"]!["dataComplete"]!.GetValue<bool>();
                    meta["scope"] = "Scalars and colors at every level, parts and collections to depth, every MultilingualText language, feature interfaces, event/dynamization counts. Events: ReadUnifiedObjectEvents; dynamizations: ManageUnifiedDynamization.";
                    return "Screen item read.";
                }
                if (action == "create" && existing != null) throw new InvalidOperationException("Screen item already exists: " + itemName);
                if (action != "create" && existing == null) throw new PortalException(PortalErrorCode.NotFound, "Screen item not found: " + itemName);
                var type = action == "create" ? UnifiedScreenItemLogic.ResolveItemType(UnifiedAssembly, itemType) : existing!.GetType();
                meta["itemType"] = type.FullName;
                // Native HmiScreenItemBaseComposition: Create<T>(name) and Create<T>(name, containedType) for faceplate / custom widget containers.
                var create = action == "create" ? UnifiedUiModelLogic.GenericCreate(items, type, string.IsNullOrEmpty(containedType) ? 1 : 2) : null;
                if (action == "create") meta["containedType"] = string.IsNullOrEmpty(containedType) ? null : containedType;
                var (plain, texts) = UnifiedScreenItemLogic.SplitProperties(type, changes);
                var prepared = UnifiedUiModelLogic.PrepareNested(type, plain);
                if (action == "delete" && type.GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete() unavailable on " + type.FullName);
                if (existing != null) meta["before"] = ScreenItemSnapshot(existing, Math.Min(depth, 1));
                if (action != "delete") { meta["requestedProperties"] = plain.DeepClone(); meta["requestedTexts"] = new JsonArray(texts.Select(t => (JsonNode)new JsonObject { ["property"] = t.Name, ["culture"] = t.Culture, ["text"] = t.Text }).ToArray()); }
                int countBefore = EngineeringGroupOperations.Items(items).Count(); meta["countBefore"] = countBefore;
                if (!writing) return "Screen item " + action + " preview; type, property shape and value conversions checked, no native write.";
                meta["mayHaveChanged"] = true;
                if (action == "delete")
                {
                    EngineeringGroupOperations.Call(existing!, "Delete", Type.EmptyTypes); meta["apiCallSuccess"] = true;
                    if (EngineeringGroupOperations.Find(items, itemName) != null) throw new InvalidOperationException("Delete returned but the item remains.");
                    meta["verifiedAbsent"] = true; meta["countAfter"] = EngineeringGroupOperations.Items(items).Count();
                    return "Screen item deleted and absence verified. No save/compile/download.";
                }
                object item = existing ?? (create!.Invoke(items, string.IsNullOrEmpty(containedType) ? new object[] { itemName } : new object[] { itemName, containedType }) ?? throw new InvalidOperationException("Native Create<T> returned null."));
                if (action == "create")
                {
                    meta["apiCallSuccess"] = true;
                    // Openness returns a fresh proxy from Create<T>; Find() yields another proxy for the same object, so identity is checked by exact name and count, never by reference.
                    item = EngineeringGroupOperations.Find(items, itemName) ?? throw new InvalidOperationException("Create returned but the item is not found by name.");
                    if (EngineeringGroupOperations.Items(items).Count() != countBefore + 1) throw new InvalidOperationException("Create returned but the item count did not grow by one.");
                }
                UnifiedUiModelLogic.ApplyNested(item, prepared, meta);
                var textResults = new JsonArray(); meta["textResults"] = textResults;
                foreach (var edit in texts)
                {
                    var owner = UnifiedScreenItemLogic.ResolveTextOwner(item, edit.Path);
                    var result = UnifiedMultilingualText.WriteDetailed(owner, edit.Property, edit.Text, edit.Culture);
                    result["path"] = edit.Name;
                    textResults.Add(result);
                    if (!result["verified"]!.GetValue<bool>()) throw new InvalidOperationException(edit.Name + " culture=" + edit.Culture + ": " + result["error"] + ". Earlier edits are not rolled back.");
                }
                meta["apiCallSuccess"] = true; meta["after"] = ScreenItemSnapshot(item, Math.Min(depth, 1)); meta["countAfter"] = EngineeringGroupOperations.Items(items).Count();
                return "Screen item " + action + " completed and read back (nested properties, colors and texts verified). No save/compile/download; partial edits are not rolled back.";
            });
    }
}
