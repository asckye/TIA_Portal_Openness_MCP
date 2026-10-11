using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaMcp.Adapters.Contracts
{
    public static class HmiScalarPolicy
    {
        public static object? ConvertValue(HardwareScalar value, Type type)
        {
            if (value.Kind == "null")
            {
                if (type.IsValueType) throw new ArgumentException("Null is invalid for " + type.Name);
                return null;
            }
            if (type == typeof(object))
                return value.Kind == "string" ? (object)value.Text : value.Kind == "boolean" ? bool.Parse(value.Text)
                    : value.Kind == "number" ? int.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                        ? (object)integer : double.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : throw new ArgumentException("Only scalar values are supported.");
            if (!type.IsEnum && !type.IsPrimitive && type != typeof(string) && type != typeof(decimal)
                && type != typeof(DateTime) && type != typeof(TimeSpan) && type != typeof(Guid) && type != typeof(Version))
                throw new NotSupportedException("Complex/reference property requires a dedicated adapter: " + type.FullName);
            if (type.IsEnum)
            {
                Require(value, "string", true);
                var parsed = Enum.Parse(type, value.Text, false);
                if (!Enum.IsDefined(type, parsed)) throw new ArgumentException("Undefined enum value.");
                return parsed;
            }
            if (type == typeof(string) || type == typeof(char) || type == typeof(Version) || type == typeof(Guid)
                || type == typeof(TimeSpan) || type == typeof(DateTime))
            {
                Require(value, "string", type == typeof(Version) || type == typeof(Guid) || type == typeof(TimeSpan));
                if (type == typeof(string)) return value.Text;
                if (type == typeof(char)) return char.Parse(value.Text);
                if (type == typeof(Version)) return Version.Parse(value.Text);
                if (type == typeof(Guid)) return Guid.Parse(value.Text);
                if (type == typeof(TimeSpan)) return TimeSpan.ParseExact(value.Text, "c", CultureInfo.InvariantCulture);
                return DateValue(value.Text);
            }
            if (type == typeof(bool)) { Require(value, "boolean"); return bool.Parse(value.Text); }
            Require(value, "number");
            // Keep the original JSON token until the target type is known. In particular,
            // object values distinguish 1 from 1.0, and decimal values must not pass through double.
            if (type == typeof(decimal)) return decimal.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            return Convert.ChangeType(value.Text, type, CultureInfo.InvariantCulture);
        }
        private static void Require(HardwareScalar value, string kind, bool getter = false)
        {
            if (value.Kind == kind) return;
            if (getter) throw new InvalidOperationException("Expected a " + kind + " scalar.");
            throw new FormatException("Expected a " + kind + " scalar.");
        }
        private static DateTime DateValue(string text)
        {
            var match = Regex.Match(text, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?<fraction>\.[0-9]{1,16})?)?(?:Z|(?<offset>[+-][0-9]{2}:[0-9]{2}))?)?\z");
            if (!match.Success) throw new FormatException("Expected an ISO date scalar.");
            var offset = match.Groups["offset"];
            if (offset.Success)
            {
                int hours = int.Parse(offset.Value.Substring(1, 2), CultureInfo.InvariantCulture);
                int minutes = int.Parse(offset.Value.Substring(4, 2), CultureInfo.InvariantCulture);
                if (hours > 14 || minutes > 59 || hours == 14 && minutes != 0)
                    throw new FormatException("Invalid ISO date offset.");
            }
            var fraction = match.Groups["fraction"];
            if (fraction.Length > 8) text = text.Remove(fraction.Index + 8, fraction.Length - 8);
            return DateTime.ParseExact(text, new[] { "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" }, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
    }
}
