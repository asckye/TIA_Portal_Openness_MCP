using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiObject : Dictionary<string, object?>
    {
        internal Dictionary<string, HardwareScalar>? ScalarInputs;
        internal HmiObject() { }
        internal HmiObject(IDictionary<string, object?> values) : base(values) { }
        public new object? this[string key] { get => TryGetValue(key, out var value) ? value : null; set => base[key] = value; }
        internal string ToJsonString() => HmiValues.Serialize(this);
    }
    internal sealed class HmiArray : List<object?>
    {
        internal HmiArray(params object?[] values) : base(values) { }
    }
    internal static class HmiValues
    {
        [ThreadStatic] internal static Func<Dictionary<string, object?>, string>? Renderer;
        [ThreadStatic] internal static Action<Exception>? ExportFailure;
        [ThreadStatic] internal static Action<string, string>? TraceWriter;
        internal static string Serialize(Dictionary<string, object?> value) => (Renderer ?? throw new InvalidOperationException("HMI evidence serializer unavailable."))(value);
        internal static object? Value<T>(T value) => value;
        internal static T GetValue<T>(this object value) => (T)value;
        internal static object? Field(this object value, string key) => value is IDictionary<string, object?> fields && fields.TryGetValue(key, out var item) ? item : null;
        internal static List<object?> Array(this object value) => value is List<object?> array ? array : ((IEnumerable)value).Cast<object?>().ToList();
        internal static object? DeepClone(this object value)
        {
            if (value is IDictionary<string, object?> fields)
                return new HmiObject(fields.ToDictionary(p => p.Key, p => p.Value?.DeepClone()))
                {
                    ScalarInputs = value is HmiObject source && source.ScalarInputs != null
                        ? new Dictionary<string, HardwareScalar>(source.ScalarInputs) : null
                };
            if (value is IList items) return new HmiArray(items.Cast<object?>().Select(v => v?.DeepClone()).ToArray());
            return value;
        }
        internal static Exception Cause(Exception error) { while (error.InnerException != null) error = error.InnerException; return error; }
    }
    internal static class HmiMeta
    {
        internal static HmiObject Basic(bool success, params (string Key, object? Value)[] values)
        {
            var meta = new HmiObject(LegacyResponseMetadata.Basic(TiaMcpServer.ModelContextProtocol.ResponseClock.Now, success));
            foreach (var value in values) meta[value.Key] = value.Value;
            return meta;
        }
    }
}
