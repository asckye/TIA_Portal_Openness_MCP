using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Linq;
using System.Reflection;
using System.Security;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Reflection
{
    internal sealed class ReflectionSession
    {
        private readonly Func<string, string, string, bool, object?> resolve;
        private readonly Func<string?> lookupPaths;
        internal ReflectionSession(Func<string, string, string, bool, object?> resolve, Func<string?> lookupPaths)
        { this.resolve = resolve; this.lookupPaths = lookupPaths; }
        internal string? PlcLookupPathsSuffix => lookupPaths();
        internal object? ResolveObject(string kind, string path, string software, bool write = false) => resolve(kind, path, software, write);
        internal static Dictionary<string, object?> Meta(bool success, params (string Key, object? Value)[] values)
        {
            var result = new Dictionary<string, object?> { ["success"] = success };
            foreach (var value in values) result[value.Key] = value.Value;
            return result;
        }
        internal static object? Argument(HardwareScalar value) => value.Kind switch {
            "null" => null, "int" => int.Parse(value.Text, CultureInfo.InvariantCulture),
            "long" => long.Parse(value.Text, CultureInfo.InvariantCulture), "double" => double.Parse(value.Text, CultureInfo.InvariantCulture),
            "bool" => bool.Parse(value.Text), "string" => value.Text,
            _ => throw new ArgumentException("Unknown reflection argument kind: " + value.Kind)
        };
        internal static SecureString Secure(string value)
        {
            var secret = new SecureString();
            foreach (var c in value) secret.AppendChar(c);
            secret.MakeReadOnly(); return secret;
        }
        internal string? TryGetName(object value)
        {
            try { return value.GetType().GetProperty("Name")?.GetValue(value)?.ToString(); }
            catch /* swallow(probe-optional): a missing optional name retains the original ToString fallback */ { return null; }
        }
        internal IEnumerable<OpennessMember> DescribeMembers(object value, int maxMembers)
        {
            var type = value.GetType(); var members = new List<OpennessMember>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                members.Add(new OpennessMember { Kind = "Property", Name = property.Name, Type = property.PropertyType.FullName ?? property.PropertyType.Name });
                if (members.Count >= maxMembers) return members;
            }
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.IsSpecialName) continue;
                var signature = $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}) -> {method.ReturnType.Name}";
                members.Add(new OpennessMember { Kind = "Method", Name = method.Name, Type = method.ReturnType.FullName ?? method.ReturnType.Name, Signature = signature });
                if (members.Count >= maxMembers) return members;
            }
            return members;
        }
        internal Type? FindTypeBySuffix(string typeSuffix)
        {
            if (string.IsNullOrWhiteSpace(typeSuffix)) return null;
            string suffix = typeSuffix.Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch /* swallow(probe-optional): unloadable assemblies cannot supply the optional service type */ { continue; }
                foreach (var type in types)
                    if ((type.FullName ?? type.Name).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || type.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return type;
            }
            return null;
        }
        internal object? TryGetService(object target, Type serviceType)
        {
            try
            {
                var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                return method?.MakeGenericMethod(serviceType).Invoke(target, null);
            }
            catch /* swallow(probe-optional): unsupported reflective GetService lookup returns no service */ { return null; }
        }
        internal void DenyCrossReferenceReflection(string? service, string? method)
        {
            var refusal = ReflectionRefusal(service, method);
            if (refusal != null) throw new PlcSoftwareException("InvalidState", refusal);
        }
        internal object? CoerceReflectionValue(object? value, Type targetType)
        {
            if (value == null) return null;

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null) targetType = nullableType;
            if (targetType.IsInstanceOfType(value)) return value;

            if (targetType == typeof(string)) return value.ToString();
            if (targetType.IsEnum) return value is string enumText
                ? Enum.Parse(targetType, enumText, ignoreCase: true)
                : Enum.ToObject(targetType, value);
            if (targetType == typeof(bool)) return value is string boolText ? bool.Parse(boolText) : Convert.ToBoolean(value);
            if (targetType == typeof(byte)) return value is string byteText ? byte.Parse(byteText) : Convert.ToByte(value);
            if (targetType == typeof(short)) return value is string shortText ? short.Parse(shortText) : Convert.ToInt16(value);
            if (targetType == typeof(ushort)) return value is string ushortText ? ushort.Parse(ushortText) : Convert.ToUInt16(value);
            if (targetType == typeof(int)) return value is string intText ? int.Parse(intText) : Convert.ToInt32(value);
            if (targetType == typeof(uint)) return value is string uintText ? uint.Parse(uintText) : Convert.ToUInt32(value);
            if (targetType == typeof(long)) return value is string longText ? long.Parse(longText) : Convert.ToInt64(value);
            if (targetType == typeof(ulong)) return value is string ulongText ? ulong.Parse(ulongText) : Convert.ToUInt64(value);
            if (targetType == typeof(float)) return value is string floatText ? float.Parse(floatText, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToSingle(value);
            if (targetType == typeof(double)) return value is string doubleText ? double.Parse(doubleText, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToDouble(value);
            if (targetType == typeof(Color)) return CoerceColor(value);

            return value;
        }

        private static Color CoerceColor(object value)
        {
            if (value is Color c) return c;

            if (value is string s)
            {
                var text = s.Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Color.FromArgb(unchecked((int)Convert.ToUInt32(text.Substring(2), 16)));
                }
                if (text.StartsWith("#", StringComparison.Ordinal))
                {
                    return ColorTranslator.FromHtml(text);
                }
                if (Regex.IsMatch(text, "^[0-9A-Fa-f]{8}$"))
                {
                    return Color.FromArgb(unchecked((int)Convert.ToUInt32(text, 16)));
                }
                return ColorTranslator.FromHtml(text);
            }

            if (value is long l) return Color.FromArgb(unchecked((int)l));
            if (value is int i) return Color.FromArgb(i);
            if (value is uint ui) return Color.FromArgb(unchecked((int)ui));

            return Color.FromArgb(Convert.ToInt32(value));
        }

        internal static string? ReflectionRefusal(string? service, string? method)
            => OpennessReflectionPolicy.ReflectionRefusal(service, method);

        internal static object Multilingual(object value)
        {
            object? Get(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target);
            if (Get(value, "Items") is not IEnumerable items) throw new InvalidOperationException("MultilingualText.Items is unavailable.");
            var rows = new List<object?>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null) throw new InvalidOperationException("Null multilingual item.");
                var language = Get(item, "Language"); var culture = language == null ? null : Get(language, "Culture");
                string? name = culture == null ? null : Get(culture, "Name") as string;
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Multilingual item has no Language.Culture.Name.");
                if (!seen.Add(name!)) throw new InvalidOperationException("Duplicate language: " + name);
                if (Get(item, "Text") is not string text) throw new InvalidOperationException("Text is not readable for " + name);
                rows.Add(new Dictionary<string, object?> { ["culture"] = name, ["text"] = text });
            }
            return rows;
        }
    }
}
