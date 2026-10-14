using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
#if !WORKER_PROPERTY_ADMISSION
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class UnifiedHmiPropertyPolicy
    {
        internal static Error? ValidateProperties(Type type, CompositeAttributeMap properties)
        {
            var error = TiaMcp.Shared.UnifiedHmiPropertyRules.Validate(type, V4Json.Serialize(properties));
            return error == null ? null : V4Json.Deserialize<Error>(JsonSerializer.Serialize(error));
        }
    }
}
#endif

namespace TiaMcp.Shared
{
    internal static class UnifiedHmiPropertyRules
    {
        internal static readonly string[] Excluded =
            { "Name", "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        // V20/V21 use the original domain contract, including its validation order.
        // The standalone V19 wrapper retains its legacy admission until review.
        internal static Dictionary<string, object?>? Validate(Type type, string input)
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            var pending = new Stack<(JsonElement Value, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count != 0)
            {
                var entry = pending.Pop();
                if (entry.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    if (entry.Depth + 1 > 2) return Limit(2, entry.Depth + 1);
                    if (entry.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in entry.Value.EnumerateArray()) pending.Push((item, entry.Depth + 1));
                    }
                    else
                    {
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var field in entry.Value.EnumerateObject())
                        {
                            if (!names.Add(field.Name)) return Invalid();
                            pending.Push((field.Value, entry.Depth + 1));
                        }
                    }
                }
            }
            int characters = JsonSerializer.Serialize(root).Length;
            if (characters > 65536) return Limit(65536, characters);
            if (root.ValueKind != JsonValueKind.Object) return Invalid();
            var fields = Properties(type);
            var leafCounts = new List<int>();
            foreach (var field in root.EnumerateObject())
            {
                int leaves = 0;
                if (Excluded.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                    || !fields.TryGetValue(field.Name, out var property)) return Invalid();
                var valueType = property.PropertyType;
                if (Scalar(valueType))
                {
                    if (property.SetMethod?.IsPublic != true || !ScalarValue(field.Value)) return Invalid();
                    leaves++;
                }
                else
                {
                    bool multilingual = valueType.Name == "MultilingualText";
                    if (!multilingual && (typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType)
                        || valueType.Namespace != "Siemens.Engineering.HmiUnified.UI.Parts"
                            && !valueType.Name.EndsWith("Part", StringComparison.Ordinal))) return Invalid();
                    if (field.Value.ValueKind != JsonValueKind.Object) return Invalid();
                    var entries = field.Value.EnumerateObject().ToArray();
                    if (entries.Length > 50) return Limit(50, entries.Length);
                    var parts = Properties(valueType);
                    foreach (var entry in entries)
                    {
                        if (multilingual)
                        {
                            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 16
                                || Excluded.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                                || entry.Value.ValueKind != JsonValueKind.String) return Invalid();
                        }
                        else if (Excluded.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                            || !parts.TryGetValue(entry.Name, out var part) || !Scalar(part.PropertyType)
                            || part.SetMethod?.IsPublic != true || !ScalarValue(entry.Value)) return Invalid();
                        leaves++;
                    }
                }
                leafCounts.Add(leaves);
            }
            int total = 0;
            foreach (int count in leafCounts)
            {
                total += count;
                if (total > 50) return Limit(50, total);
            }
            return null;
        }

        private static bool ScalarValue(JsonElement value) => Scalar(value)
            && (value.ValueKind != JsonValueKind.Number
                || value.TryGetDouble(out double number) && !double.IsNaN(number) && !double.IsInfinity(number));

        internal static Dictionary<string, PropertyInfo> Properties(Type type)
        {
            int Depth(PropertyInfo property)
            {
                int depth = 0;
                for (var current = type; current != null && current != property.DeclaringType; current = current.BaseType) depth++;
                return depth;
            }
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true)
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(Depth).First(), StringComparer.Ordinal);
        }

        internal static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string)
            || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(Version) || type == typeof(object)
            || type.FullName == "System.Drawing.Color";
        internal static bool Scalar(JsonElement value) => value.ValueKind is JsonValueKind.String
            or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        internal static Dictionary<string, object?> Invalid() => new Dictionary<string, object?>
        {
            ["code"] = "INVALID_ARGUMENT",
            ["message"] = "Input does not satisfy the declared contract.",
            ["details"] = new Dictionary<string, object?>
            {
                ["parameter"] = "properties", ["allowedValues"] = Array.Empty<string>(), ["parameters"] = new[] { "properties" }
            }
        };
        internal static Dictionary<string, object?> Limit(int limit, int actual) => new Dictionary<string, object?>
        {
            ["code"] = "LIMIT_EXCEEDED",
            ["message"] = "Input exceeds its declared budget.",
            ["details"] = new Dictionary<string, object?> { ["parameter"] = "properties", ["limit"] = limit, ["actual"] = actual }
        };
    }
}
