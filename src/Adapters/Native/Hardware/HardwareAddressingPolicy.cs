using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Hardware
{
    public static class HardwareAddressingPolicy
    {
        public static readonly string[] AddressAttributes = { "Context", "ProcessImage", "IsochronousMode", "InterruptObNumber" };
        public static readonly string[] IoTypes = { "None", "Input", "Output", "Diagnosis", "Substitute" };

        public static void RequireAvailable(string release, string tool)
        {
            if (!PortedFamilies.Available(release, tool))
                throw new NotSupportedException("This capability is unavailable for " + release + ": " + tool);
        }

        public static void ValidatePath(string[] names, bool device)
        {
            if (names == null || names.Length > 64 || device && names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException(device ? "Device path needs 1-64 exact nonempty names." : "Invalid item path.");
        }

        public static void ValidatePage(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
        }

        public static object? ConvertValue(HardwareScalar value, Type type)
        {
            if (value.Kind == "null")
            {
                if (type.IsValueType) throw new ArgumentException("Null is invalid for " + type.Name);
                return null;
            }
            if (type == typeof(object))
                return value.Kind == "string" ? (object)value.Text : value.Kind == "boolean" ? bool.Parse(value.Text)
                    : value.Kind == "number" ? int.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        ? (object)n : double.Parse(value.Text, CultureInfo.InvariantCulture)
                    : throw new ArgumentException("Only scalar values are supported.");
            if (type.IsEnum)
            {
                if (value.Kind != "string") throw new ArgumentException("Enum values must be strings.");
                var parsed = Enum.Parse(type, value.Text, false);
                if (!Enum.IsDefined(type, parsed)) throw new ArgumentException("Undefined enum value.");
                return parsed;
            }
            if (type == typeof(Version)) return Version.Parse(value.Text);
            if (type == typeof(Guid)) return Guid.Parse(value.Text);
            if (type == typeof(TimeSpan)) return TimeSpan.ParseExact(value.Text, "c", CultureInfo.InvariantCulture);
            if (type == typeof(DateTime)) return DateTime.Parse(value.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(string))
            {
                if (value.Kind != "string") throw new ArgumentException("Expected a string scalar.");
                return value.Text;
            }
            if (type == typeof(bool))
            {
                if (value.Kind != "boolean") throw new ArgumentException("Expected a boolean scalar.");
                return bool.Parse(value.Text);
            }
            if (!type.IsPrimitive && type != typeof(decimal)) throw new NotSupportedException("Complex/reference property requires a dedicated adapter: " + type.FullName);
            if (value.Kind != "number") throw new ArgumentException("Expected a numeric scalar.");
            if (type == typeof(int)) return int.Parse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return Convert.ChangeType(value.Text, type, CultureInfo.InvariantCulture);
        }

        public static object? ScalarValue(object? value)
        {
            if (value == null) return null;
            if (value is TimeSpan span) return span.ToString("c", CultureInfo.InvariantCulture);
            var type = value.GetType();
            if (type.IsEnum || value is Version || value is Guid || value is DateTime)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (type.IsPrimitive || value is string || value is decimal) return value;
            throw new NotSupportedException("Complex/reference attribute requires a dedicated adapter: " + type.FullName);
        }

        public static bool SameValue(object? actual, object? expected)
        {
            if (actual == null || expected == null) return actual == null && expected == null;
            if (Equals(actual, expected)) return true;
            if (actual.GetType() == expected.GetType()) return false;
            var a = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "";
            var e = Convert.ToString(expected, CultureInfo.InvariantCulture) ?? "";
            if (a == e) return true;
            return decimal.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da)
                && decimal.TryParse(e, NumberStyles.Any, CultureInfo.InvariantCulture, out var de) && da == de;
        }

        public static List<(PropertyInfo Property, object? Value)> Prepare(Type type, Dictionary<string, HardwareScalar> changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            var result = new List<(PropertyInfo, object?)>();
            foreach (var change in changes)
            {
                var property = type.GetProperties().FirstOrDefault(p => p.Name == change.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
                result.Add((property, ConvertValue(change.Value, property.PropertyType)));
            }
            return result;
        }
    }
}
