using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Session-independent helpers stay in the engine so reflected native calls remain woven.
    internal static class EngineeringSessionHelpers
    {
        internal static JsonArray Names(object composition, int max = 200)
            => new JsonArray(EngineeringGroupOperations.Items(composition).Take(max).Select(x => (JsonNode)EngineeringGroupOperations.Get(x, "Name").ToString()!).ToArray());
        internal static void Safe(JsonObject row, string key, Func<JsonNode?> read)
        {
            try { row[key] = read(); }
            catch (Exception ex) { row[key] = null; row[key + "Error"] = ex.GetBaseException().Message; }
        }

        internal static JsonArray Names(object collection)
            => new JsonArray(EngineeringGroupOperations.Items(collection).Select(x => (JsonNode)JsonValue.Create(EngineeringGroupOperations.Get(x, "Name").ToString())!).ToArray());

        internal static JsonObject Page(JsonNode[] all, int offset, int limit, JsonObject meta)
        {
            var rows = all.Skip(offset).Take(limit).ToArray();
            meta["records"] = new JsonArray(rows);
            foreach (var pair in HardwareServicesLogic.PageMeta(all.Length, offset, limit, rows.Length)) meta[pair.Key] = pair.Value?.DeepClone();
            return meta;
        }

        internal static string? BestEffortExtractFirstName(object? importReturnValue)
        {
            try
            {
                if (importReturnValue is IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch { }
            return null;
        }

        internal static object? TryGetPropertyValue(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                try
                {
                    var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v != null) return v;
                }
                catch { }
            }
            return null;
        }

        internal static IEnumerable<string> DescribeTypeMembers(Type type, bool includeNonPublic)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var result = new List<string>();
            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                result.Add($"Property:{p.Name}:{p.PropertyType.FullName ?? p.PropertyType.Name}");
            }

            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                var ps = string.Join(", ", m.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}"));
                result.Add($"Method:{m.Name}({ps}) -> {m.ReturnType.FullName ?? m.ReturnType.Name}");
            }

            return result
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
        }

        internal static IEnumerable<string> FormatEnumerableObjects(object? value, int limit)
        {
            var lines = new List<string>();
            if (value == null)
            {
                lines.Add("<null>");
                return lines;
            }

            if (value is not IEnumerable enumerable || value is string)
            {
                lines.Add(value.ToString() ?? "<null>");
                return lines;
            }

            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= Math.Max(1, limit)) break;
                if (item == null)
                {
                    lines.Add("<null>");
                    continue;
                }

                var type = item.GetType();
                var parts = new List<string> { type.FullName ?? type.Name };
                foreach (var propertyName in new[] { "Name", "CompositionName", "Type", "Description" })
                {
                    try
                    {
                        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        var propValue = prop?.GetValue(item);
                        if (propValue != null)
                        {
                            parts.Add($"{propertyName}={propValue}");
                        }
                    }
                    catch { }
                }
                lines.Add(string.Join(" | ", parts));
            }

            if (lines.Count == 0) lines.Add("<empty>");
            return lines;
        }

        internal static object? ConvertReflectionArgument(object? value, Type targetType)
        {
            if (value == null) return null;

            var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullable.IsInstanceOfType(value)) return value;

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(nonNullable))
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(object));
                var dict = Activator.CreateInstance(dictType);
                var addMethod = dictType.GetMethod("Add", new[] { typeof(string), typeof(object) });
                if (value is IEnumerable<KeyValuePair<string, object?>> kvps)
                {
                    foreach (var kv in kvps)
                    {
                        addMethod?.Invoke(dict, new object?[] { kv.Key, kv.Value });
                    }
                    return dict;
                }
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(nonNullable) && nonNullable != typeof(string))
            {
                var enumerableInterface = nonNullable.IsInterface && nonNullable.IsGenericType
                    ? nonNullable
                    : nonNullable.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                if (enumerableInterface != null)
                {
                    var elementType = enumerableInterface.GetGenericArguments()[0];
                    if (elementType.IsGenericType &&
                        elementType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                        elementType.GenericTypeArguments[0] == typeof(string))
                    {
                        var valueType = elementType.GenericTypeArguments[1];
                        var listType = typeof(List<>).MakeGenericType(elementType);
                        var list = (System.Collections.IList)Activator.CreateInstance(listType);
                        if (value is IEnumerable<KeyValuePair<string, object?>> kvps)
                        {
                            foreach (var kv in kvps)
                            {
                                var kvValue = kv.Value;
                                if (kvValue != null && valueType != typeof(object) && !valueType.IsInstanceOfType(kvValue))
                                {
                                    kvValue = Convert.ChangeType(kvValue, valueType);
                                }
                                var pair = Activator.CreateInstance(elementType, kv.Key, kvValue);
                                list.Add(pair);
                            }
                            return list;
                        }
                    }
                }
            }

            if (nonNullable.IsEnum)
            {
                return value is string s
                    ? Enum.Parse(nonNullable, s, ignoreCase: true)
                    : Enum.ToObject(nonNullable, value);
            }

            return Convert.ChangeType(value, nonNullable);
        }
    }
}
