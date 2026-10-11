using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed class PlcB5Object : Dictionary<string, object?>
    {
        internal PlcB5Object() { }
        internal PlcB5Object(IDictionary<string, object?> source) : base(source) { }
    }
    internal sealed class PlcB5Array : List<object?>
    {
        internal PlcB5Array(params object?[] values) : base(values) { }
    }
    internal static class PlcB5Values
    {
        internal static T GetValue<T>(this object value) => (T)value;
        internal static object DeepClone(this object value) => value;
    }
    internal static class PlcB5Meta
    {
        internal static PlcB5Object Unstamped(bool success, params (string Key, object? Value)[] fields)
        {
            var result = new PlcB5Object { ["success"] = success };
            foreach (var field in fields) result[field.Key] = field.Value;
            return result;
        }
    }
    internal static class PlcB5Result
    {
        internal static void Record(Dictionary<string, object?> evidence, object? state, bool changesProject, string? logFilePath = null, string? targetPath = null, object? messages = null)
        {
            evidence["nativeResultReturned"] = true; evidence["nativeState"] = state?.ToString();
            if (state is Enum) evidence["nativeStateType"] = state.GetType().FullName;
            evidence["mayHaveChanged"] = changesProject; evidence["logFilePath"] = logFilePath; evidence["nativeMessages"] = messages;
            if (targetPath != null) evidence["targetFile"] = FileRow(targetPath);
        }
        internal static PlcB5Object FileRow(string path)
        {
            var file = new FileInfo(path);
            return new PlcB5Object { ["path"] = file.FullName, ["exists"] = file.Exists, ["sizeBytes"] = file.Exists ? file.Length : (long?)null };
        }
    }
    internal static class PlcB5Scalar
    {
        internal static object? Value(object? value) => value;
        internal static bool Scalar(Type type) => HardwareScalarEvidence.Scalar(type);
        internal static object? Json(object? value) => HardwareAddressingPolicy.ScalarValue(value);
        internal static PlcB5Object Read(object target) => new PlcB5Object(HardwareScalarEvidence.Read(target));
        internal static HardwareScalar ScalarValue(object? value)
            => value == null ? new HardwareScalar { Kind = "null" } : value is bool flag ? new HardwareScalar { Kind = "boolean", Text = flag ? "true" : "false" }
                : value is string text ? new HardwareScalar { Kind = "string", Text = text }
                : new HardwareScalar { Kind = "number", Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
        internal static object? ConvertValue(object? value, Type type) => HardwareAddressingPolicy.ConvertValue(ScalarValue(value), type);
        internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, PlcB5Object changes)
        {
            if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            var result = new List<(PropertyInfo, object?)>();
            foreach (var pair in changes)
            {
                var property = HardwareScalarEvidence.DistinctProperties(type).FirstOrDefault(p => p.Name == pair.Key);
                if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                    throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + pair.Key);
                result.Add((property, ConvertValue(pair.Value, property.PropertyType)));
            }
            return result;
        }
        internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> values, Dictionary<string, object?> evidence) => HardwareScalarEvidence.Apply(target, values, evidence);
        internal static object? TryProperty(object target, string name)
        {
            try { return target.GetType().GetProperty(name)?.GetValue(target); }
            catch { /* swallow(probe-optional): Preserve the optional property probe used by the interface inventory. */ return null; }
        }
        internal static bool TrySetProperty(object target, string name, object value)
        {
            try
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.CanWrite != true) return false;
                property.SetValue(target, value); return true;
            }
            catch { /* swallow(native-fallback): Unsupported property conversion or assignment reports false to the original caller fallback. */ return false; }
        }
        internal static object? TryInvokeMethodByName(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethods().FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
            try { return method?.Invoke(target, args); }
            catch { /* swallow(probe-optional): Missing or unsupported reflective methods return null to the existing caller fallback. */ return null; }
        }
    }
}
