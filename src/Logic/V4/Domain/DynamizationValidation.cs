using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    internal static partial class DomainValidation
    {
        // The host supplies already resolved, version-specific mapping CLR types. Input
        // cannot name a CLR type; this examines metadata only and invokes no accessor.
        public static void Dynamization(DynamizationMapping[] entries, string action, string releaseKey,
            bool packageAvailable, bool readOnly, IReadOnlyDictionary<string, Type> mappingTypes, bool hasMappingTable = true)
        {
            Capability(releaseKey, packageAvailable, "Unified dynamization", readOnly, action != "read");
            Require(new[] { "read", "create", "update", "delete" }.Contains(action));
            if (action == "read" || action == "delete") Require(entries.Length == 0);
            if (entries.Length != 0 && !hasMappingTable) throw new NotSupportedException("Mapping entries require ValueConverter.MappingTable.");
            ValidateParameter(entries);
            foreach (var entry in entries)
            {
                if (!mappingTypes.TryGetValue(entry.Kind, out var type)) throw new NotSupportedException("Mapping type is unavailable: " + entry.Kind);
                foreach (var pair in entry.Properties)
                {
                    Require(!new[] { "Name", "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" }
                        .Contains(pair.Key, StringComparer.OrdinalIgnoreCase));
                    var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.Name == pair.Key && p.GetIndexParameters().Length == 0)
                        .OrderBy(p => Depth(type, p.DeclaringType)).FirstOrDefault();
                    if (property?.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
                        throw new NotSupportedException("Public writable property unavailable: " + pair.Key);
                    MappingScalar(pair.Value, property.PropertyType);
                }
            }
        }
        private static int Depth(Type type, Type? declaring)
        { int depth = 0; for (Type? t = type; t != null && t != declaring; t = t.BaseType) depth++; return depth; }
        private static void MappingScalar(Scalar value, Type type)
        {
            if (value.Kind == JsonValueKind.Null) { Require(!type.IsValueType); return; }
            if (type == typeof(object)) return;
            if (type.FullName == "System.Drawing.Color")
            {
                string text = value.String?.Trim() ?? "";
                Require(text.StartsWith("#", StringComparison.Ordinal) && (text.Length == 7 || text.Length == 9)
                    && int.TryParse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)); return;
            }
            if (type.IsEnum)
            {
                Require(value.String != null);
                Require(Enum.IsDefined(type, Enum.Parse(type, value.String!, false))); return;
            }
            if (type == typeof(Version)) { Version.Parse(value.String!); return; }
            if (type == typeof(Guid)) { Guid.Parse(value.String!); return; }
            if (type == typeof(TimeSpan)) { TimeSpan.ParseExact(value.String!, "c", CultureInfo.InvariantCulture); return; }
            if (!type.IsPrimitive && type != typeof(string) && type != typeof(decimal) && type != typeof(DateTime))
                throw new NotSupportedException("Complex/reference property requires a dedicated adapter.");
            JsonSerializer.Deserialize(V4Json.Serialize(value), type);
        }
    }
}
