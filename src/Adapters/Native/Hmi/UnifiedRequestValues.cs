using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class UnifiedRequestValues
    {
        [ThreadStatic] internal static Dictionary<string, object?>? Inputs;
        [ThreadStatic] internal static Func<string, UnifiedValue>? Decoder;
        [ThreadStatic] internal static int Major;
        internal static object? Decode(UnifiedValue value)
        {
            if (value.Error != null) return new InputFailure(value.Error);
            switch (value.Kind)
            {
                case "object": return new HmiObject(value.Properties.ToDictionary(p => p.Key, p => Decode(p.Value), StringComparer.Ordinal))
                    { ScalarInputs = value.Properties.Where(p => p.Value.Kind != "object" && p.Value.Kind != "array")
                        .ToDictionary(p => p.Key, p => new HardwareScalar { Kind = p.Value.Kind, Text = p.Value.Text }) };
                case "array": return new HmiArray(value.Items.Select(Decode).ToArray());
                case "string": return value.Text;
                case "boolean": return bool.Parse(value.Text);
                case "number":
                    if (int.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
                    if (long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wide)) return wide;
                    return decimal.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
                default: return null;
            }
        }
        private sealed class InputFailure
        {
            internal string Message;
            internal InputFailure(string message) => Message = message;
        }
        internal static object? Parse(string text)
        {
            if (text == "{}") return new HmiObject();
            if (text == "[]") return new HmiArray();
            if (Inputs == null || !Inputs.TryGetValue(text, out var value))
                value = Decoder != null ? Decode(Decoder(text)) : throw new ArgumentException("Structured input was not decoded by the host.");
            if (value is InputFailure failure) throw new ArgumentException(failure.Message);
            return value?.DeepClone();
        }
        internal static string ToJsonString(this object value)
        {
            var wrapped = HmiValues.Serialize(new Dictionary<string, object?> { ["value"] = value });
            var text = wrapped.Substring(9, wrapped.Length - 10);
            if (Inputs != null) Inputs[text] = value.DeepClone();
            return text;
        }
        internal static HmiObject Object(this object value) => (HmiObject)value;
        internal static bool TryGetValue<T>(this object value, out T result)
        {
            if (value is T typed) { result = typed; return true; }
            result = default!; return false;
        }
        internal static bool Same(object? left, object? right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left is IDictionary<string, object?> a && right is IDictionary<string, object?> b)
                return a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && Same(p.Value, v));
            if (left is IList x && right is IList y)
                return x.Count == y.Count && x.Cast<object?>().Zip(y.Cast<object?>(), Same).All(v => v);
            return Equals(left, right);
        }
    }
}
