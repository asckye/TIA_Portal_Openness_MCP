using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace TiaMcp.Shared
{
    // The standalone worker has no Logic dependency. This metadata-only projection
    // matches CompositeAttributeMapValidator.ScreenItem; parity is tested with fakes.
    internal static class UnifiedHmiPropertyAdmission
    {
        private static readonly string[] Excluded =
            { "Name", "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };

        internal static Dictionary<string, object?>? Validate(Type type, string input)
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid();
            var fields = Properties(type);
            int leaves = 0;
            foreach (var field in root.EnumerateObject())
            {
                if (Excluded.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                    || !fields.TryGetValue(field.Name, out var property)) return Invalid();
                var valueType = property.PropertyType;
                if (Scalar(valueType))
                {
                    if (property.SetMethod?.IsPublic != true || !Scalar(field.Value)) return Invalid();
                    leaves++;
                }
                else
                {
                    bool multilingual = valueType.Name == "MultilingualText";
                    if (!multilingual && valueType.Namespace != "Siemens.Engineering.HmiUnified.UI.Parts"
                        && !valueType.Name.EndsWith("Part", StringComparison.Ordinal)) return Invalid();
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
                            || part.SetMethod?.IsPublic != true || !Scalar(entry.Value)) return Invalid();
                        leaves++;
                    }
                }
                if (leaves > 50) return Limit(50, leaves);
            }
            return null;
        }

        private static Dictionary<string, PropertyInfo> Properties(Type type)
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

        private static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string)
            || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(Version) || type == typeof(object)
            || type.FullName == "System.Drawing.Color";
        private static bool Scalar(JsonElement value) => value.ValueKind is JsonValueKind.String
            or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        private static Dictionary<string, object?> Invalid() => new Dictionary<string, object?>
        {
            ["code"] = "INVALID_ARGUMENT",
            ["message"] = "Input does not satisfy the declared contract.",
            ["details"] = new Dictionary<string, object?> { ["parameter"] = "properties", ["allowedValues"] = Array.Empty<string>() }
        };
        private static Dictionary<string, object?> Limit(int limit, int actual) => new Dictionary<string, object?>
        {
            ["code"] = "LIMIT_EXCEEDED",
            ["message"] = "Input exceeds its declared budget.",
            ["details"] = new Dictionary<string, object?> { ["parameter"] = "properties", ["limit"] = limit, ["actual"] = actual }
        };
    }
}
