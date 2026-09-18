using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // No Siemens dependency: item types are resolved by reflection against whatever assembly the caller
    // hands over, so the offline suite runs the same code on fakes shaped like the official UI model.
    // Covers Siemens.Engineering.HmiUnified.UI.Shapes / Widgets / Controls / Screens (screen items),
    // their UI.Base classes and the IHmi*Feature interfaces they implement.
    internal static class UnifiedScreenItemLogic
    {
        internal const string UiNamespace = "Siemens.Engineering.HmiUnified.UI.";
        internal const string ScreenItemBase = UiNamespace + "Base.HmiScreenItemBase";
        internal static readonly string[] ItemNamespaces = { UiNamespace + "Shapes", UiNamespace + "Widgets", UiNamespace + "Controls", UiNamespace + "Screens" };
        internal static readonly string[] Actions = { "read", "list", "create", "update", "delete" };
        private static readonly string[] BackLinks = { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };

        internal static bool ValidateRequest(string action, string itemName, string itemType, JsonObject changes, bool confirmDelete, bool dryRun, string containedType = "")
        {
            if (!Actions.Contains(action)) throw new ArgumentException("action must be read/list/create/update/delete.");
            if (action != "create" && !string.IsNullOrEmpty(containedType)) throw new ArgumentException("containedType is only used by create (faceplate / custom widget containers).");
            if (action != "list" && string.IsNullOrWhiteSpace(itemName)) throw new ArgumentException("Exact itemName required for " + action + ".");
            if (!string.IsNullOrEmpty(itemName) && (itemName.Length > 128 || itemName.IndexOfAny(new[] { '/', '\\' }) >= 0)) throw new ArgumentException("itemName must be 1..128 characters without path separators.");
            if (action == "create" && string.IsNullOrWhiteSpace(itemType)) throw new ArgumentException("create requires itemType (e.g. HmiCircle, Widgets.HmiSlider or a full CLR name).");
            if (action != "create" && !string.IsNullOrEmpty(itemType)) throw new ArgumentException("itemType is only used by create.");
            if ((action == "read" || action == "list" || action == "delete") && changes.Count != 0) throw new ArgumentException("propertiesJson is only accepted by create/update.");
            if (action == "update" && changes.Count == 0) throw new ArgumentException("update requires nonempty propertiesJson.");
            if (action == "read" || action == "list" || dryRun) return false;
            if (action == "delete" && !confirmDelete) throw new ArgumentException("Real delete requires confirmDelete=true besides dryRun=false.");
            return true;
        }

        internal static JsonObject ParseProperties(string propertiesJson)
        {
            if (string.IsNullOrWhiteSpace(propertiesJson) || propertiesJson.Length > 65536) throw new ArgumentException("propertiesJson must be a JSON object (<= 64 KB).");
            return JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be a JSON object.");
        }

        // Concrete screen item types: public classes under the four UI namespaces deriving from HmiScreenItemBase.
        // The API leaves its *Base classes (HmiWidgetBase, HmiShapeBase, ...) non-abstract; they are not placeable items.
        internal static List<Type> ItemTypes(Assembly assembly)
        {
            var root = assembly.GetType(ScreenItemBase, false);
            return LoadableTypes(assembly)
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && !t.Name.EndsWith("Base", StringComparison.Ordinal)
                    && t.Namespace != null && ItemNamespaces.Contains(t.Namespace) && (root == null || root.IsAssignableFrom(t)))
                .OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        }
        internal static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }

        // Accepts "HmiCircle", "Circle", "Shapes.HmiCircle" or the full CLR name; ambiguity and unknown names are refused with candidates.
        internal static Type ResolveItemType(Assembly assembly, string itemType)
        {
            var key = (itemType ?? "").Trim();
            if (key.Length == 0) throw new ArgumentException("itemType required.");
            var types = ItemTypes(assembly);
            var exact = types.Where(t => t.FullName == key).ToList();
            if (exact.Count == 1) return exact[0];
            var matches = types.Where(t =>
                    string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.Name, "Hmi" + key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.Namespace!.Substring(UiNamespace.Length) + "." + t.Name, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.FullName, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) throw new ArgumentException("Ambiguous itemType '" + key + "': " + string.Join(", ", matches.Select(t => t.FullName)) + ". Use the namespace-qualified name.");
            var similar = types.Where(t => t.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).Select(t => t.Name).Take(8).ToList();
            throw new ArgumentException("Unknown screen item type '" + key + "'." + (similar.Count > 0 ? " Similar: " + string.Join(", ", similar) + "." : "") + " Call DescribeUnifiedScreenItemType with an empty itemType for the catalog.");
        }

        internal static string Group(Type type) => type.Namespace != null && type.Namespace.StartsWith(UiNamespace, StringComparison.Ordinal) ? type.Namespace.Substring(UiNamespace.Length) : type.Namespace ?? "";
        internal static JsonArray Features(Type type)
            => new JsonArray(type.GetInterfaces().Where(i => i.Namespace == UiNamespace + "Features").Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        internal static JsonArray BaseChain(Type type)
        {
            var chain = new JsonArray();
            for (var t = type.BaseType; t != null && t != typeof(object) && t.Namespace?.StartsWith("Siemens.Engineering", StringComparison.Ordinal) == true; t = t.BaseType) chain.Add(t.Name);
            return chain;
        }

        // Property classification used by describeType and by the read snapshot.
        internal static string Kind(PropertyInfo p)
        {
            var t = p.PropertyType;
            if (UnifiedUiModelLogic.IsColor(t)) return "color";
            if (EngineeringScalarProperties.Scalar(t) || t == typeof(object)) return "scalar";
            if (t.Name == "MultilingualText") return "multilingual";
            if (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string)) return t.Name.EndsWith("Composition", StringComparison.Ordinal) || t.Name.EndsWith("Association", StringComparison.Ordinal) ? "collection" : "enumerable";
            if (t.Namespace == UiNamespace + "Parts" || t.Name.EndsWith("Part", StringComparison.Ordinal)) return "part";
            return "reference";
        }

        internal static JsonObject PropertySchema(PropertyInfo p, int depth)
        {
            var kind = Kind(p);
            var row = new JsonObject { ["name"] = p.Name, ["kind"] = kind, ["type"] = p.PropertyType.Name, ["writable"] = p.SetMethod?.IsPublic == true };
            var t = p.PropertyType;
            if (t.IsEnum) row["enumValues"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
            if (kind == "collection" || kind == "enumerable")
            {
                var element = t.GetProperty("Item")?.PropertyType;
                row["elementType"] = element?.Name;
                var creates = t.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == "Create")
                    .Select(m => "Create" + (m.IsGenericMethodDefinition ? "<T>" : "") + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")");
                row["create"] = new JsonArray(creates.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
            }
            if (kind == "part" && depth > 0) row["properties"] = Schema(t, depth - 1);
            if (kind == "multilingual") row["edit"] = "UpdateUnifiedMultilingualProperty or propertiesJson {\"" + p.Name + "\": {\"<culture>\": \"text\"}}";
            return row;
        }
        internal static JsonArray Schema(Type type, int depth = 2)
        {
            var rows = new JsonArray();
            foreach (var p in UnifiedUiModelLogic.PublicProperties(type))
            {
                if (BackLinks.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                rows.Add(PropertySchema(p, depth));
            }
            return rows;
        }
        internal static JsonObject TypeDescription(Type type, int depth = 2)
        {
            var events = UnifiedUiModelLogic.FindProperty(type, "EventHandlers")?.PropertyType.GetMethods().FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)?.GetParameters()[0].ParameterType;
            return new JsonObject
            {
                ["type"] = type.FullName, ["name"] = type.Name, ["group"] = Group(type), ["baseTypes"] = BaseChain(type), ["features"] = Features(type),
                ["hasDynamizations"] = UnifiedUiModelLogic.FindProperty(type, "Dynamizations") != null, ["hasEventHandlers"] = UnifiedUiModelLogic.FindProperty(type, "EventHandlers") != null,
                ["eventTypes"] = events == null ? null : new JsonArray(Enum.GetNames(events).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                ["deletable"] = type.GetMethod("Delete", Type.EmptyTypes) != null,
                ["properties"] = Schema(type, depth)
            };
        }
        internal static JsonArray Catalog(Assembly assembly)
            => new JsonArray(ItemTypes(assembly).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["group"] = Group(t), ["type"] = t.FullName, ["features"] = Features(t) }).ToArray());

        internal sealed class TextEdit
        {
            public string[] Path = Array.Empty<string>(); public string Property = ""; public string Culture = ""; public string Text = "";
            public string Name => string.Join(".", Path.Concat(new[] { Property }));
        }

        // Splits propertiesJson into nested scalar/color edits (UnifiedUiModelLogic.PrepareNested) and multilingual
        // text edits {"Text": {"en-US": "Start"}} at any depth ({"Title": {"Text": {...}}} for text parts); a plain
        // string on a MultilingualText property is refused so a caller cannot write one language believing all were set.
        internal static (JsonObject Plain, List<TextEdit> Texts) SplitProperties(Type type, JsonObject changes)
        {
            var texts = new List<TextEdit>();
            var plain = SplitTexts(type, changes, Array.Empty<string>(), texts);
            if (texts.Count > 50) throw new ArgumentException("At most 50 multilingual entries per request.");
            return (plain, texts);
        }
        private static JsonObject SplitTexts(Type type, JsonObject changes, string[] path, List<TextEdit> texts)
        {
            if (path.Length > 6) throw new ArgumentException("Nested property depth exceeds 6.");
            var plain = new JsonObject();
            foreach (var change in changes)
            {
                var property = UnifiedUiModelLogic.FindProperty(type, change.Key);
                var name = string.Join(".", path.Concat(new[] { change.Key }));
                if (property?.PropertyType.Name == "MultilingualText")
                {
                    var languages = change.Value as JsonObject ?? throw new ArgumentException(name + " is a MultilingualText: pass {\"<culture>\": \"text\"} per language.");
                    if (languages.Count == 0) throw new ArgumentException(name + " needs at least one culture entry.");
                    foreach (var language in languages)
                    {
                        if (string.IsNullOrWhiteSpace(language.Key) || language.Key.Length > 16) throw new ArgumentException("Culture name required for " + name + ".");
                        var text = language.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException(name + "." + language.Key + " must be a string (empty clears).");
                        texts.Add(new TextEdit { Path = path, Property = change.Key, Culture = language.Key, Text = text });
                    }
                    continue;
                }
                // Descend into parts so texts nested in Title/Label/Caption parts are split out too; the rest stays a nested edit.
                if (property != null && change.Value is JsonObject nested && Kind(property) == "part")
                {
                    var rest = SplitTexts(property.PropertyType, nested, path.Concat(new[] { change.Key }).ToArray(), texts);
                    if (rest.Count > 0) plain[change.Key] = rest;
                    continue;
                }
                plain[change.Key] = change.Value?.DeepClone();
            }
            return plain;
        }
        // Walks the part path of a text edit down from the item; null parts are refused before any setter runs.
        internal static object ResolveTextOwner(object item, string[] path)
        {
            object owner = item;
            foreach (var step in path)
                owner = UnifiedUiModelLogic.FindProperty(owner.GetType(), step)?.GetValue(owner) ?? throw new InvalidOperationException("Nested part is null: " + step);
            return owner;
        }

        // Compact list row: identity plus the geometry every visible item shares.
        internal static JsonObject ListRow(object item, int index)
        {
            var row = new JsonObject { ["index"] = index, ["name"] = UnifiedUiModelLogic.FindProperty(item.GetType(), "Name")?.GetValue(item)?.ToString(), ["type"] = item.GetType().Name, ["group"] = Group(item.GetType()) };
            foreach (var name in new[] { "Left", "Top", "Width", "Height", "Visible", "Enabled", "TabIndex" })
            {
                var p = UnifiedUiModelLogic.FindProperty(item.GetType(), name);
                if (p?.GetMethod?.IsPublic != true) continue;
                try { row[char.ToLowerInvariant(name[0]) + name.Substring(1)] = UnifiedUiModelLogic.Json(p.GetValue(item)); } catch (Exception ex) { row[name + "Error"] = ex.GetBaseException().Message; }
            }
            return row;
        }
    }
}
