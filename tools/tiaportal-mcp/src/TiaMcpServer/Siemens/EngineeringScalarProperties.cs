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
        internal static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) || type == typeof(Version);
        internal static JsonNode? Json(object? value) => value == null ? null : value.GetType().IsEnum || value is Version || value is Guid || value is DateTime
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
            return JsonSerializer.Deserialize(node.ToJsonString(), type);
        }
        internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, JsonObject changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            var result = new List<(PropertyInfo, object?)>();
            foreach (var change in changes)
            {
                var property = type.GetProperty(change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
                result.Add((property, ConvertValue(change.Value, property.PropertyType)));
            }
            return result;
        }
        internal static JsonObject Read(object target)
        {
            var values = new JsonObject(); var failures = new JsonArray(); var excluded = new JsonArray();
            foreach (var p in target.GetType().GetProperties().OrderBy(p => p.Name))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod?.IsPublic != true) continue;
                if (!Scalar(p.PropertyType) && !(p.PropertyType == typeof(object) && p.Name == "Value")) { excluded.Add(p.Name); continue; }
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
        internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> changes, JsonObject meta)
        {
            var applied = new JsonArray(); meta["appliedProperties"] = applied;
            foreach (var change in changes)
            {
                meta["mayHaveChanged"] = true; meta["lastAttemptedProperty"] = change.Property.Name;
                change.Property.SetValue(target, change.Value);
                applied.Add(change.Property.Name);
                if (!Equals(change.Property.GetValue(target), change.Value)) throw new InvalidOperationException("Property readback differs: " + change.Property.Name + ". Changes are not rolled back.");
            }
        }
    }
}
