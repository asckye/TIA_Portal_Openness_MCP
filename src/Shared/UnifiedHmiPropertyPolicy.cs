using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class UnifiedHmiPropertyPolicy
    {
        private static IEnumerable<PropertyInfo> PublicProperties(Type type)
        {
            int Depth(PropertyInfo property) { int depth = 0; for (var current = type; current != null && current != property.DeclaringType; current = current.BaseType) depth++; return depth; }
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true)
                .GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.OrderBy(Depth).First()).OrderBy(p => p.Name, StringComparer.Ordinal);
        }
        private static string Kind(PropertyInfo property)
        {
            var type = property.PropertyType;
            if (type == typeof(System.Drawing.Color)) return "color";
            if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Version) || type == typeof(object)) return "scalar";
            if (type.Name == "MultilingualText") return "multilingual";
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string)) return "collection";
            if (type.Namespace == "Siemens.Engineering.HmiUnified.UI.Parts" || type.Name.EndsWith("Part", StringComparison.Ordinal)) return "part";
            return "reference";
        }
        internal static Error? ValidateProperties(Type type, CompositeAttributeMap properties)
        {
            IReadOnlyDictionary<string, AttributeRule> Scalars(Type owner) => PublicProperties(owner)
                .Where(p => Kind(p) == "scalar" || Kind(p) == "color")
                .ToDictionary(p => p.Name, p => new AttributeRule(InputSchema.Scalar(), p.SetMethod?.IsPublic == true), StringComparer.Ordinal);
            var parts = new Dictionary<string, IReadOnlyDictionary<string, AttributeRule>>(StringComparer.Ordinal);
            foreach (var property in PublicProperties(type))
            {
                if (Kind(property) == "part") parts.Add(property.Name, Scalars(property.PropertyType));
                else if (Kind(property) == "multilingual")
                {
                    var cultures = new Dictionary<string, AttributeRule>(StringComparer.Ordinal);
                    if (properties.TryGetValue(property.Name, out var value) && value.Json.ValueKind == JsonValueKind.Object)
                        foreach (var culture in value.Json.EnumerateObject())
                            if (!string.IsNullOrWhiteSpace(culture.Name) && culture.Name.Length <= 16)
                                cultures.Add(culture.Name, new AttributeRule(InputSchema.String()));
                    parts.Add(property.Name, cultures);
                }
            }
            return CompositeAttributeMapValidator.ScreenItem(Scalars(type), parts).Validate(properties, "properties").Error;
        }
    }
}
