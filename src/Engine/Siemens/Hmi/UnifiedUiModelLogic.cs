using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // No Siemens dependency: the offline suite exercises the same reflection path as the live server.
    // Unified UI model helpers shared by events, parts, dynamizations, screens, lists, alarm and audit readers.
    internal static class UnifiedUiModelLogic
    {
        private static readonly string[] BackLinks = { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        internal const string DynamizationNamespace = "Siemens.Engineering.HmiUnified.UI.Dynamization.";
        internal static readonly IReadOnlyDictionary<string, string> DynamizationKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tag"] = DynamizationNamespace + "TagDynamization",
            ["Script"] = DynamizationNamespace + "Script.ScriptDynamization",
            ["ResourceList"] = DynamizationNamespace + "ResourceListDynamization",
            ["Flashing"] = DynamizationNamespace + "Flashing.FlashingDynamization",
            ["Expression"] = DynamizationNamespace + "ExpressionDynamization",
            ["TagParameter"] = DynamizationNamespace + "TagParameterDynamization",
        };
        internal static readonly IReadOnlyDictionary<string, string> MappingEntryKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Simple"] = DynamizationNamespace + "Tag.MappingTableEntrySimple",
            ["Range"] = DynamizationNamespace + "Tag.MappingTableEntryRange",
            ["Bitmask"] = DynamizationNamespace + "Tag.MappingTableEntryBitmask",
        };

        // Property lookup that tolerates hiding (HmiSlider/HmiToggleSwitch/HmiCircleSegment/HmiEllipseSegment redeclare
        // EventHandlers with a narrower composition): the most derived declaration wins instead of AmbiguousMatchException.
        internal static PropertyInfo? FindProperty(Type type, string name)
        {
            PropertyInfo? best = null; int bestDepth = -1;
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.Name != name || p.GetIndexParameters().Length != 0) continue;
                int depth = 0; for (var t = type; t != null && t != p.DeclaringType; t = t.BaseType) depth++;
                if (best == null || depth < bestDepth) { best = p; bestDepth = depth; }
            }
            return best;
        }
        // Public readable properties with hidden base declarations removed (most derived kept), stable order by name.
        internal static IEnumerable<PropertyInfo> PublicProperties(Type type)
            => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true)
                .GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => FindProperty(type, g.Key)!).OrderBy(p => p.Name, StringComparer.Ordinal);

        internal static bool IsColor(Type type) => type == typeof(Color);
        internal static string ColorText(Color color) => "#" + color.ToArgb().ToString("X8", CultureInfo.InvariantCulture);
        internal static Color ParseColor(JsonNode? node)
        {
            var text = node is JsonValue value && value.TryGetValue<string>(out var s) ? s.Trim() : throw new ArgumentException("Color must be a \"#AARRGGBB\" or \"#RRGGBB\" string.");
            if (!text.StartsWith("#") || (text.Length != 7 && text.Length != 9) || !int.TryParse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                throw new ArgumentException("Color must be a \"#AARRGGBB\" or \"#RRGGBB\" hex string: " + text);
            if (text.Length == 7) argb = unchecked((int)0xFF000000) | argb;
            return Color.FromArgb(argb);
        }
        internal static object? ConvertValue(JsonNode? node, Type type) => IsColor(type) ? ParseColor(node) : EngineeringScalarProperties.ConvertValue(node, type);
        internal static bool SameValue(object? actual, object? expected) => actual is Color a && expected is Color e ? a.ToArgb() == e.ToArgb() : EngineeringScalarProperties.SameValue(actual, expected);
        internal static JsonNode? Json(object? value) => value is Color c ? JsonValue.Create(ColorText(c)) : EngineeringScalarProperties.Json(value);

        // Public scalar read plus System.Drawing.Color rendered as #AARRGGBB; Color leaves excludedComplexProperties.
        internal static JsonObject Scalars(object target)
        {
            var result = EngineeringScalarProperties.Read(target);
            var values = (JsonObject)result["values"]!; var excluded = (JsonArray)result["excludedComplexProperties"]!; var failures = (JsonArray)result["failures"]!;
            foreach (var p in target.GetType().GetProperties().Where(p => IsColor(p.PropertyType) && p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true))
            {
                var index = excluded.Select((n, i) => (n, i)).Where(x => x.n?.ToString() == p.Name).Select(x => x.i).DefaultIfEmpty(-1).First();
                try { values[p.Name] = ColorText((Color)p.GetValue(target)!); if (index >= 0) excluded.RemoveAt(index); }
                catch (Exception ex) { failures.Add(new JsonObject { ["property"] = p.Name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            result["dataComplete"] = failures.Count == 0; result["fullObjectComplete"] = excluded.Count == 0 && failures.Count == 0;
            return result;
        }

        // Bounded nested read: scalars and colors at every level, engineering sub-objects and
        // collections descended up to depth, backlinks never followed. Collections capped at 500 items.
        internal static JsonObject Tree(object target, int depth)
        {
            var row = Scalars(target);
            if (depth <= 0) return row;
            var children = new JsonObject();
            foreach (var name in ((JsonArray)row["excludedComplexProperties"]!).Select(n => n!.ToString()).ToArray())
            {
                if (BackLinks.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var property = FindProperty(target.GetType(), name);
                object? value;
                try { value = property?.GetValue(target); }
                catch (Exception ex) { ((JsonArray)row["failures"]!).Add(new JsonObject { ["property"] = name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; continue; }
                if (value == null) { children[name] = null; continue; }
                if (value is IEnumerable sequence && value is not string)
                {
                    var items = new JsonArray(); int count = 0;
                    foreach (var item in sequence) { if (item == null) continue; if (++count > 500) { row["dataComplete"] = false; items.Add(new JsonObject { ["truncated"] = true }); break; } items.Add(Tree(item, depth - 1)); }
                    children[name] = items;
                }
                else if (value.GetType().IsClass && value.GetType().Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
                    children[name] = Tree(value, depth - 1);
                else children[name] = new JsonObject { ["type"] = value.GetType().FullName, ["unread"] = true };
            }
            row["children"] = children;
            return row;
        }

        internal sealed class Edit
        {
            public string[] Path = Array.Empty<string>(); public PropertyInfo Property = null!; public object? Value;
            public string Name => string.Join(".", Path.Concat(new[] { Property.Name }));
        }
        // Validates a nested propertiesJson against CLR shape only: scalar and Color leaves need a public setter,
        // JSON objects descend into readable engineering sub-objects. No native call is made here.
        internal static List<Edit> PrepareNested(Type type, JsonObject changes, string[]? path = null, int budget = 50)
        {
            path ??= Array.Empty<string>();
            if (path.Length > 6) throw new ArgumentException("Nested property depth exceeds 6.");
            var result = new List<Edit>();
            foreach (var change in changes)
            {
                if (change.Key == "Name" || change.Key == "Parent" || BackLinks.Contains(change.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Renaming/backlink edits excluded: " + change.Key);
                var property = FindProperty(type, change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.GetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public property unavailable: " + type.FullName + "." + change.Key);
                bool leaf = EngineeringScalarProperties.Scalar(property.PropertyType) || IsColor(property.PropertyType) || property.PropertyType == typeof(object);
                if (!leaf && change.Value is JsonObject nested)
                {
                    if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string)) throw new NotSupportedException("Collections need a dedicated action, not nested properties: " + change.Key);
                    result.AddRange(PrepareNested(property.PropertyType, nested, path.Concat(new[] { change.Key }).ToArray(), budget));
                    continue;
                }
                if (!leaf) throw new NotSupportedException("Complex/reference property requires a JSON object of nested scalars or a dedicated adapter: " + property.PropertyType.FullName);
                if (property.SetMethod?.IsPublic != true) throw new NotSupportedException("Public writable property unavailable (V20 may expose it only as a dynamic attribute): " + type.FullName + "." + change.Key);
                result.Add(new Edit { Path = path, Property = property, Value = ConvertValue(change.Value, property.PropertyType) });
                if (result.Count > budget) throw new ArgumentException("At most " + budget + " leaf properties per request.");
            }
            return result;
        }
        internal static void ApplyNested(object target, List<Edit> edits, JsonObject meta)
        {
            var applied = new JsonArray(); meta["appliedProperties"] = applied;
            foreach (var edit in edits)
            {
                object owner = target;
                foreach (var step in edit.Path) owner = FindProperty(owner.GetType(), step)!.GetValue(owner) ?? throw new InvalidOperationException("Nested object is null: " + step);
                meta["mayHaveChanged"] = true; meta["lastAttemptedProperty"] = edit.Name;
                edit.Property.SetValue(owner, edit.Value);
                applied.Add(edit.Name);
                if (!SameValue(edit.Property.GetValue(owner), edit.Value)) throw new InvalidOperationException("Property readback differs: " + edit.Name + ". Changes are not rolled back.");
            }
        }

        internal static JsonObject Page(JsonArray all, int offset, int limit, JsonObject meta)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required.");
            var rows = new JsonArray(all.Skip(offset).Take(limit).Select(n => n!.DeepClone()).ToArray());
            meta["records"] = rows; meta["expectedCount"] = all.Count; meta["actualCount"] = rows.Count;
            meta["nextOffset"] = offset + rows.Count < all.Count ? offset + rows.Count : (int?)null;
            meta["truncated"] = offset + rows.Count < all.Count;
            meta["dataComplete"] = offset == 0 && rows.Count == all.Count && rows.All(r => r is not JsonObject o || o["dataComplete"] == null || o["dataComplete"]!.GetValue<bool>());
            return meta;
        }

        // Events: every EventHandlers and PropertyEventHandlers entry of one object, with script fields.
        internal static JsonArray EventRows(object target)
        {
            var rows = new JsonArray();
            foreach (var source in new[] { "EventHandlers", "PropertyEventHandlers" })
            {
                var property = FindProperty(target.GetType(), source);
                if (property?.GetMethod?.IsPublic != true) continue;
                var handlers = property.GetValue(target) ?? throw new InvalidOperationException(source + " is null.");
                foreach (var handler in EngineeringGroupOperations.Items(handlers))
                {
                    var row = HmiExactAccess.EventDto(handler);
                    row["source"] = source; row["handlerType"] = handler.GetType().FullName;
                    var name = handler.GetType().GetProperty("PropertyName");
                    row["propertyName"] = name == null ? null : name.GetValue(handler)?.ToString();
                    row["dataComplete"] = true;
                    rows.Add(row);
                }
            }
            return rows;
        }
        internal static JsonObject EventCapabilities(object target)
        {
            var result = new JsonObject();
            foreach (var source in new[] { "EventHandlers", "PropertyEventHandlers" })
            {
                var property = FindProperty(target.GetType(), source);
                if (property?.GetMethod?.IsPublic != true) { result[source] = null; continue; }
                var create = property.PropertyType.GetMethods().FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length > 0 && m.GetParameters().Last().ParameterType.IsEnum);
                result[source] = new JsonObject { ["compositionType"] = property.PropertyType.FullName,
                    ["eventTypeEnum"] = create?.GetParameters().Last().ParameterType.FullName,
                    ["availableEventTypes"] = create == null ? null : new JsonArray(Enum.GetNames(create.GetParameters().Last().ParameterType).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()) };
            }
            return result;
        }

        // Parts: a composition is identified by its exact property name; members by exact Name or, when the
        // part type has no Name, by zero-based index. Exactly one selector is accepted.
        internal static object Composition(object owner, string collectionProperty)
        {
            if (string.IsNullOrWhiteSpace(collectionProperty) || !collectionProperty.All(c => char.IsLetterOrDigit(c) || c == '_')) throw new ArgumentException("Exact collection property name required.");
            var property = FindProperty(owner.GetType(), collectionProperty);
            if (property?.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0) throw new NotSupportedException("Public collection property unavailable: " + owner.GetType().FullName + "." + collectionProperty);
            var value = property.GetValue(owner) ?? throw new InvalidOperationException("Collection is null: " + collectionProperty);
            if (value is not IEnumerable || value is string) throw new NotSupportedException(collectionProperty + " is not a collection.");
            return value;
        }
        internal static Type? ElementType(object composition) => composition.GetType().GetProperty("Item")?.PropertyType;
        internal static bool Named(object composition) => ElementType(composition)?.GetProperty("Name") != null;
        internal static object? FindPart(object composition, string partName, int partIndex)
        {
            bool byName = !string.IsNullOrEmpty(partName), byIndex = partIndex >= 0;
            if (byName == byIndex) throw new ArgumentException("Select a part by exactly one of partName (named parts) or partIndex (unnamed parts).");
            if (byName)
            {
                if (!Named(composition)) throw new ArgumentException("Parts in this collection have no Name; use partIndex.");
                var matches = EngineeringGroupOperations.Items(composition).Where(x => string.Equals(x.GetType().GetProperty("Name")?.GetValue(x)?.ToString(), partName, StringComparison.Ordinal)).Take(2).ToArray();
                if (matches.Length > 1) throw new InvalidOperationException("Ambiguous exact part name: " + partName);
                return matches.SingleOrDefault();
            }
            var items = EngineeringGroupOperations.Items(composition).ToArray();
            return partIndex < items.Length ? items[partIndex] : null;
        }
        internal static (MethodInfo Method, object[] Args) PartCreation(object composition, string partName, string partKind)
        {
            var element = ElementType(composition);
            if (!string.IsNullOrEmpty(partKind) && !string.Equals(element?.Name, partKind, StringComparison.Ordinal) && !string.Equals(element?.FullName, partKind, StringComparison.Ordinal))
                throw new ArgumentException("partKind must match the collection element type " + element?.Name + ".");
            var methods = composition.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == "Create" && !m.IsGenericMethodDefinition).ToArray();
            var named = methods.FirstOrDefault(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var plain = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
            if (named != null) { if (string.IsNullOrEmpty(partName)) throw new ArgumentException("This composition creates named parts; partName required."); return (named, new object[] { partName }); }
            if (plain != null) { if (!string.IsNullOrEmpty(partName)) throw new ArgumentException("This composition creates unnamed parts via Create(); omit partName and address the result by partIndex."); return (plain, Array.Empty<object>()); }
            throw new NotSupportedException("Native Create() / Create(string) is not exposed on " + composition.GetType().FullName + "; parts of this kind are created by TIA, not the public API.");
        }
        internal static JsonArray PartCollections(object owner)
        {
            var rows = new JsonArray();
            foreach (var p in owner.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!typeof(IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string)) continue;
                var element = p.PropertyType.GetProperty("Item")?.PropertyType;
                var creates = p.PropertyType.GetMethods().Where(m => m.Name == "Create").Select(m => "Create" + (m.IsGenericMethodDefinition ? "<T>" : "") + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")").ToArray();
                rows.Add(new JsonObject { ["property"] = p.Name, ["compositionType"] = p.PropertyType.FullName, ["elementType"] = element?.FullName,
                    ["isPart"] = element?.Namespace == "Siemens.Engineering.HmiUnified.UI.Parts", ["named"] = element?.GetProperty("Name") != null,
                    ["deletable"] = element?.GetMethod("Delete", Type.EmptyTypes) != null,
                    ["create"] = new JsonArray(creates.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()) });
            }
            return rows;
        }

        // Dynamizations
        internal static Type DynamizationType(object composition, string kind)
        {
            if (!DynamizationKinds.TryGetValue(kind, out var fullName)) throw new ArgumentException("dynamizationKind must be one of " + string.Join("/", DynamizationKinds.Keys) + ".");
            return composition.GetType().Assembly.GetType(fullName, false) ?? throw new NotSupportedException("Native type unavailable in this API version: " + fullName);
        }
        internal static MethodInfo GenericCreate(object composition, Type argument, int parameterCount)
        {
            var open = composition.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == parameterCount)
                ?? throw new NotSupportedException("Native generic Create<T> with " + parameterCount + " parameter(s) unavailable on " + composition.GetType().FullName);
            return open.MakeGenericMethod(argument);
        }
        internal static object? FindDynamization(object composition, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException("Exact dynamized propertyName required.");
            var matches = EngineeringGroupOperations.Items(composition).Where(x => string.Equals(x.GetType().GetProperty("PropertyName")?.GetValue(x)?.ToString(), propertyName, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous dynamization for " + propertyName + "; no mutation performed.");
            return matches.SingleOrDefault();
        }
        internal static string KindOf(object dynamization) => KindOf(dynamization.GetType());
        internal static string KindOf(Type type) => DynamizationKinds.FirstOrDefault(k => k.Value == type.FullName).Key ?? type.Name;
        internal sealed class MappingEntry { public Type Type = null!; public JsonObject Properties = new JsonObject(); public string Kind = ""; }
        internal static List<MappingEntry> ParseMappingEntries(string json, Func<string, Type> resolve)
        {
            var array = JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("mappingEntriesJson must be a JSON array.");
            if (array.Count > 100) throw new ArgumentException("At most 100 mapping entries per request.");
            var result = new List<MappingEntry>();
            foreach (var node in array)
            {
                var entry = node as JsonObject ?? throw new ArgumentException("Each mapping entry must be an object with kind and properties.");
                var kind = entry["kind"]?.GetValue<string>() ?? throw new ArgumentException("Mapping entry kind required: Simple/Range/Bitmask.");
                if (!MappingEntryKinds.TryGetValue(kind, out var fullName)) throw new ArgumentException("Mapping entry kind must be Simple, Range or Bitmask.");
                var properties = new JsonObject();
                foreach (var pair in entry.Where(p => p.Key != "kind")) properties[pair.Key] = pair.Value?.DeepClone();
                var type = resolve(fullName);
                PrepareNested(type, properties);
                result.Add(new MappingEntry { Type = type, Properties = properties, Kind = kind });
            }
            return result;
        }
        internal static string[] MappingTablePath(Type dynamization)
            => dynamization.GetProperty("ValueConverter") != null && dynamization.GetProperty("ValueConverter")!.PropertyType.GetProperty("MappingTable") != null ? new[] { "ValueConverter", "MappingTable", "Entries" } : Array.Empty<string>();

        // Screens
        internal static bool IsScreen(object target) => target.GetType().FullName == "Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen";
        internal static bool IsScreenComposition(object target) => target.GetType().FullName == "Siemens.Engineering.HmiUnified.UI.Screens.HmiScreenComposition";
        internal static string ValidateScreenName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact screen name of 1..128 characters without path separators required.");
            return name;
        }

        // Official dynamic self-description: names only, no composition getter is invoked (see UnifiedGraphicSelection).
        internal static JsonObject SelfDescription(object target)
        {
            var api = target.GetType().GetInterface("Siemens.Engineering.IEngineeringObject");
            var result = new JsonObject { ["engineeringObject"] = api != null, ["attributes"] = new JsonArray(), ["compositions"] = new JsonArray(), ["contentsRead"] = false };
            if (api == null) return result;
            try
            {
                if (api.GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(target, null) is IEnumerable attributes)
                    foreach (var info in attributes) { if (((JsonArray)result["attributes"]!).Count >= 512) break; ((JsonArray)result["attributes"]!).Add(new JsonObject { ["name"] = info?.GetType().GetProperty("Name")?.GetValue(info)?.ToString(), ["accessMode"] = info?.GetType().GetProperty("AccessMode")?.GetValue(info)?.ToString() }); }
                if (api.GetMethod("GetCompositionInfos", Type.EmptyTypes)?.Invoke(target, null) is IEnumerable compositions)
                    foreach (var info in compositions) { if (((JsonArray)result["compositions"]!).Count >= 128) break; ((JsonArray)result["compositions"]!).Add(info?.GetType().GetProperty("Name")?.GetValue(info)?.ToString()); }
            }
            catch (Exception ex) { result["error"] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            return result;
        }
        internal static string ListCollection(string category) => category switch
        {
            "textLists" => "HmiTextLists", "graphicLists" => "HmiGraphicLists", "systemTextLists" => "HmiSystemTextLists",
            _ => throw new ArgumentException("category must be textLists, graphicLists or systemTextLists.")
        };
        internal static string AlarmCommonCollection(string category) => category switch
        {
            "alarmClasses" => "AlarmClasses", "discreteAlarms" => "DiscreteAlarms", "analogAlarms" => "AnalogAlarms",
            _ => throw new ArgumentException("category must be alarmClasses, discreteAlarms or analogAlarms.")
        };
        internal static string AuditCollection(string category) => category switch
        {
            "alarmAuditClasses" => "HmiAlarmAuditClass", "auditTrails" => "AuditTrails",
            _ => throw new ArgumentException("category must be alarmAuditClasses or auditTrails.")
        };
        internal static JsonObject MultilingualRows(object target)
        {
            var result = new JsonObject();
            foreach (var p in target.GetType().GetProperties().Where(p => p.PropertyType.Name == "MultilingualText" && p.GetMethod?.IsPublic == true).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                try { var value = p.GetValue(target); result[p.Name] = value == null ? null : UnifiedMultilingualText.Read(value); }
                catch (Exception ex) { result[p.Name] = new JsonObject { ["error"] = ex.GetBaseException().Message }; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            return result;
        }
    }
}
