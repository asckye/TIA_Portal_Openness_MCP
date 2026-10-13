using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class UnifiedRequestScalar
    {
        internal static bool Scalar(Type type) => HmiScalar.Scalar(type);
        // Engine-side renderer for API value types the tests never see (Siemens.Engineering.Hmi.ConstValue / NullableDateTime); null in the offline suite.
        internal static object? Json(object? value) => HmiScalar.Json(value);
        internal static object? ConvertValue(object? value, Type type)
        {
            var scalar = value == null ? new HardwareScalar { Kind = "null" }
                : value is string text ? new HardwareScalar { Kind = "string", Text = text }
                : value is bool boolean ? new HardwareScalar { Kind = "boolean", Text = boolean ? "true" : "false" }
                : value is System.Collections.IEnumerable ? throw new ArgumentException("Only scalar values are supported.")
                : new HardwareScalar { Kind = "number", Text = Convert.ToString(value, CultureInfo.InvariantCulture)! };
            return HmiScalarPolicy.ConvertValue(scalar, type);
        }
        internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, HmiObject changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            TiaMcp.Adapters.Libraries.LibraryPolicy.GuardKnownTypeWrites(UnifiedRequestValues.Major, type, changes.Select(p => p.Key));
            var result = new List<(PropertyInfo, object?)>();
            foreach (var change in changes)
            {
                var property = DistinctProperties(type).FirstOrDefault(p => p.Name == change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
                result.Add((property, HmiScalar.ConvertInput(changes, change.Key, property.PropertyType)));
            }
            return result;
        }
        internal static HmiObject Read(object target) => HmiScalar.Read(target);
        // A property redeclared with `new` on a derived type (HmiSlider.EventHandlers) is reported once, from the most derived type.
        internal static IEnumerable<PropertyInfo> DistinctProperties(Type type) => HmiScalar.DistinctProperties(type);
        // Readback comparison. Object-typed Openness properties (tag ranges, substitute values, thresholds) store the
        // value in their own representation (JSON 0 comes back as the string "0"), so different CLR types are compared
        // by invariant text and, when both parse, by numeric value.
        internal static bool SameValue(object? actual, object? expected) => HmiScalar.SameValue(actual, expected);
        internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> changes, HmiObject meta)
        {
            var applied = new HmiArray(); meta["appliedProperties"] = applied;
            // Check the entire batch, including externally prepared lists, before its first write.
            TiaMcp.Adapters.Libraries.LibraryPolicy.GuardKnownTypeWrites(UnifiedRequestValues.Major, target.GetType(), changes.Select(p => p.Property.Name));
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
