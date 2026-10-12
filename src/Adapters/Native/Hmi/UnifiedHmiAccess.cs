using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class UnifiedHmiAccess
    {
        internal static PlcSoftwareException Error(string code, string message)
            => new PlcSoftwareException(code, message);
        [ThreadStatic] internal static Dictionary<string, UnifiedHmiInput>? Inputs;
        internal static bool TryString(object? value, out string text)
        {
            text = value as string ?? string.Empty;
            return value is string;
        }

        internal static object? Parse(string text)
        {
            if (Inputs == null || !Inputs.TryGetValue(text, out var input))
                throw new ArgumentException("Unified HMI input was not decoded by the host.");
            return Value(input);
        }

        private static object? Value(UnifiedHmiInput input)
        {
            if (input.Kind == "object")
            {
                var result = new HmiObject { ScalarInputs = new Dictionary<string, HardwareScalar>() };
                foreach (var pair in input.Properties)
                {
                    result[pair.Key] = Value(pair.Value);
                    if (pair.Value.Kind is "null" or "string" or "boolean" or "number")
                        result.ScalarInputs[pair.Key] = new HardwareScalar { Kind = pair.Value.Kind, Text = pair.Value.Text };
                }
                return result;
            }
            if (input.Kind == "array") return new HmiArray(input.Items.Select(Value).ToArray());
            if (input.Kind == "error") throw new ArgumentException(input.Text);
            if (input.Kind == "number" && long.TryParse(input.Text, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long integer) && (integer < int.MinValue || integer > int.MaxValue)) return integer;
            return HmiScalarPolicy.ConvertValue(new HardwareScalar { Kind = input.Kind, Text = input.Text }, typeof(object));
        }

        internal static object? ConvertField(HmiObject fields, string key, Type type)
        {
            if (type == typeof(System.Drawing.Color)) return UnifiedUiModelLogic.ParseColor(fields[key]);
            return HmiScalar.ConvertInput(fields, key, type);
        }

        internal static void CopyScalar(HmiObject source, HmiObject target, string key)
        {
            if (source.ScalarInputs == null || !source.ScalarInputs.TryGetValue(key, out var scalar)) return;
            target.ScalarInputs ??= new Dictionary<string, HardwareScalar>();
            target.ScalarInputs[key] = scalar;
        }

        internal static object? ConvertValue(object? value, Type type)
        {
            var scalar = new HardwareScalar
            {
                Kind = value == null ? "null" : value is string ? "string" : value is bool ? "boolean" : "number",
                Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            };
            return HmiScalarPolicy.ConvertValue(scalar, type);
        }

        internal static object? TryGetPropertyValue(object obj, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                try
                {
                    var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null) continue;
                    var value = property.GetValue(obj);
                    if (value != null) return value;
                }
                catch /* swallow(probe-optional): preserve the next reflected property fallback */ { }
            }
            return null;
        }

        internal static HmiObject Unstamped(bool success, params (string Key, object? Value)[] values)
        {
            var result = new HmiObject(LegacyResponseMetadata.Basic(null, success));
            foreach (var value in values) result[value.Key] = value.Value;
            return result;
        }

        internal static string SerializeValue(object? value)
        {
            string wrapped = HmiValues.Serialize(new Dictionary<string, object?> { ["value"] = value });
            return wrapped.Substring(wrapped.IndexOf(':') + 1, wrapped.LastIndexOf('}') - wrapped.IndexOf(':') - 1).Trim();
        }
    }
}
