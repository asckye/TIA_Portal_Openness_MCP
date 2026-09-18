using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringScalarProperties
    {
        internal static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Version);
        internal static JsonNode? Json(object? value) => value is TimeSpan span ? JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture)) : value == null ? null : value.GetType().IsEnum || value is Version || value is Guid || value is DateTime
            ? JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)) : JsonSerializer.SerializeToNode(value);
        internal static object? ConvertValue(JsonNode? node, Type type)
        {
            if (node == null) { if (type.IsValueType) throw new ArgumentException("Null is invalid for " + type.Name); return null; }
            if (type == typeof(object))
            {
                using var doc = JsonDocument.Parse(node.ToJsonString());
                return doc.RootElement.ValueKind switch { JsonValueKind.String => doc.RootElement.GetString(), JsonValueKind.True => true,
                    JsonValueKind.False => false, JsonValueKind.Number => doc.RootElement.TryGetInt32(out int n) ? (object)n : doc.RootElement.GetDouble(),
                    _ => throw new ArgumentException("Only scalar values are supported.") };
            }
            if (!Scalar(type)) throw new NotSupportedException("Complex/reference property requires a dedicated adapter: " + type.FullName);
            if (type.IsEnum)
            {
                var value = Enum.Parse(type, node.GetValue<string>(), false);
                if (!Enum.IsDefined(type, value)) throw new ArgumentException("Undefined enum value.");
                return value;
            }
            if (type == typeof(Version)) return Version.Parse(node.GetValue<string>());
            if (type == typeof(Guid)) return Guid.Parse(node.GetValue<string>());
            if (type == typeof(TimeSpan)) return TimeSpan.ParseExact(node.GetValue<string>(), "c", CultureInfo.InvariantCulture);
            return JsonSerializer.Deserialize(node.ToJsonString(), type);
        }
        internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, JsonObject changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            var result = new List<(PropertyInfo, object?)>();
            foreach (var change in changes)
            {
                var property = DistinctProperties(type).FirstOrDefault(p => p.Name == change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
                result.Add((property, ConvertValue(change.Value, property.PropertyType)));
            }
            return result;
        }
        internal static JsonObject Read(object target)
        {
            var values = new JsonObject(); var failures = new JsonArray(); var excluded = new JsonArray();
            foreach (var p in DistinctProperties(target.GetType()))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod?.IsPublic != true) continue;
                if (!Scalar(p.PropertyType) && p.PropertyType != typeof(object)) { excluded.Add(p.Name); continue; }
                try {
                    var value = p.GetValue(target);
                    if (value != null && !Scalar(value.GetType())) { excluded.Add(p.Name); continue; }
                    values[p.Name] = Json(value);
                }
                catch (Exception ex) { failures.Add(new JsonObject { ["property"] = p.Name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            return new JsonObject { ["type"] = target.GetType().FullName, ["scope"] = "public scalar properties only",
                ["values"] = values, ["excludedComplexProperties"] = excluded, ["failures"] = failures,
                ["dataComplete"] = failures.Count == 0, ["fullObjectComplete"] = excluded.Count == 0 && failures.Count == 0 };
        }
        // A property redeclared with `new` on a derived type (HmiSlider.EventHandlers) is reported once, from the most derived type.
        internal static IEnumerable<PropertyInfo> DistinctProperties(Type type)
        {
            int Depth(PropertyInfo p) { int d = 0; for (var t = type; t != null && t != p.DeclaringType; t = t.BaseType) d++; return d; }
            return type.GetProperties().GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.OrderBy(Depth).First()).OrderBy(p => p.Name, StringComparer.Ordinal);
        }
        // Readback comparison. Object-typed Openness properties (tag ranges, substitute values, thresholds) store the
        // value in their own representation (JSON 0 comes back as the string "0"), so different CLR types are compared
        // by invariant text and, when both parse, by numeric value.
        internal static bool SameValue(object? actual, object? expected)
        {
            if (actual == null || expected == null) return actual == null && expected == null;
            if (Equals(actual, expected)) return true;
            if (actual.GetType() == expected.GetType()) return false;
            var a = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? ""; var e = Convert.ToString(expected, CultureInfo.InvariantCulture) ?? "";
            if (a == e) return true;
            return decimal.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da) && decimal.TryParse(e, NumberStyles.Any, CultureInfo.InvariantCulture, out var de) && da == de;
        }
        internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> changes, JsonObject meta)
        {
            var applied = new JsonArray(); meta["appliedProperties"] = applied;
            foreach (var change in changes)
            {
                meta["mayHaveChanged"] = true; meta["lastAttemptedProperty"] = change.Property.Name;
                change.Property.SetValue(target, change.Value);
                applied.Add(change.Property.Name);
                if (!SameValue(change.Property.GetValue(target), change.Value)) throw new InvalidOperationException("Property readback differs: " + change.Property.Name + ". Changes are not rolled back.");
            }
        }
    }
}
