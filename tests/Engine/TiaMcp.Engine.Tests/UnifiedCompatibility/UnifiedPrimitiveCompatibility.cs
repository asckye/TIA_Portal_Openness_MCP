using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcpServer.Siemens
{
    internal static class UnifiedPrimitiveCompatibility
    {
        private sealed class Codecs : IUnifiedCodecs
        {
            public Func<string, string, UnifiedScriptPair>? ReadUnifiedScriptPair => Services.UnifiedScriptFileCodec.ReadPair;
            public Action<string, string>? RequireUnifiedPlainFile => Services.UnifiedScriptFileCodec.RequirePlainFile;
            public Action<string>? ValidateUnifiedScript => Services.UnifiedScriptFileCodec.ValidateScript;
            public Func<string, string, string, IEnumerable<Dictionary<string, object?>>>? DecodeUnifiedDocument => Services.UnifiedDocumentCodec.Document;
            public Func<string, string, Dictionary<string, object?>>? AnalyzeUnifiedScript => (code, path) => (Dictionary<string, object?>)ConvertValue(JavaScriptEvidence.Analyze(code, path), typeof(Dictionary<string, object?>))!;
        }
        internal static void Configure()
            => UnifiedPrimitiveBridge.Configure(values => JsonSerializer.Serialize(values), Decode, new Codecs(), Engineering.TiaMajorVersion);
        private static UnifiedValue Decode(string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            return Services.UnifiedInputCodec.Decode(doc.RootElement);
        }
        internal static T Call<T>(string helper, string member, params object?[] arguments)
            => (T)InvokeCore(null, helper, member, typeof(T), arguments)!;
        internal static void Call(string helper, string member, params object?[] arguments)
            => InvokeCore(null, helper, member, typeof(void), arguments);
        internal static T CallState<T>(object state, string helper, string member, params object?[] arguments)
            => (T)InvokeCore(state, helper, member, typeof(T), arguments)!;
        internal static void CallState(object state, string helper, string member, params object?[] arguments)
            => InvokeCore(state, helper, member, typeof(void), arguments);
        internal static object Create(string helper, params object?[] arguments)
            => InvokeCore(null, helper, ".ctor", typeof(object), arguments)!;
        private static object? InvokeCore(object? state, string helper, string member, Type resultType, object?[] arguments)
        {
            Configure();
            var type = UnifiedPrimitiveBridge.Type(helper);
            var methods = member == ".ctor" ? type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>()
                : type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == member).Cast<MethodBase>();
            var method = methods.Where(m => m.GetParameters().Length == arguments.Length)
                .OrderByDescending(m => m.GetParameters().Select((p, i) => arguments[i] == null ? 0
                    : p.ParameterType == arguments[i]!.GetType() ? 4 : p.ParameterType != typeof(object) && p.ParameterType.IsInstanceOfType(arguments[i]) ? 2 : 0).Sum())
                .First();
            var prepared = method.GetParameters().Select((p, i) => ConvertValue(arguments[i], p.ParameterType)).ToArray();
            object? result;
            try { result = UnifiedPrimitiveBridge.Invoke(state, method, prepared); }
            catch (PlcSoftwareException error)
            { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), error.Code), error.Message, error.Candidates, error.InnerException); }
            finally
            {
                for (int i = 0; i < arguments.Length; i++)
                    if (arguments[i] is JsonObject original && prepared[i] is IDictionary<string, object?> values)
                    {
                        var updated = JsonSerializer.SerializeToNode(values)!.AsObject();
                        original.Clear(); foreach (var pair in updated) original[pair.Key] = pair.Value?.DeepClone();
                    }
            }
            return resultType == typeof(void) ? null : ConvertValue(result, resultType);
        }
        internal static T Field<T>(object? state, string helper, string name)
            => (T)ConvertValue(UnifiedPrimitiveBridge.Field(state, helper, name), typeof(T))!;
        private static object? ConvertValue(object? value, Type target)
        {
            if (value is JsonNode input && target == typeof(object))
                return NativePlain(input, UnifiedPrimitiveBridge.Type("UnifiedUiModelLogic").Assembly);
            if (value == null || target == typeof(object) || target.IsInstanceOfType(value)) return value;
            if (typeof(JsonNode).IsAssignableFrom(target)) return JsonSerializer.SerializeToNode(value);
            if (value is JsonNode node)
            {
                if (node is JsonObject map)
                {
                    var result = (IDictionary)Activator.CreateInstance(target, true)!;
                    foreach (var pair in map) result.Add(pair.Key, NativePlain(pair.Value, UnifiedPrimitiveBridge.Type("UnifiedUiModelLogic").Assembly));
                    var inputs = target.GetField("ScalarInputs", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (inputs != null)
                        inputs.SetValue(result, map.Where(p => p.Value is not JsonObject && p.Value is not JsonArray).ToDictionary(p => p.Key, p => Scalar(p.Value)));
                    return result;
                }
                if (node is JsonArray array)
                {
                    var result = (IList)Activator.CreateInstance(target, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new object[] { Array.Empty<object>() }, null)!;
                    foreach (var item in array) result.Add(Plain(item));
                    return result;
                }
                return Plain(node);
            }
            if (value is Delegate callback && typeof(Delegate).IsAssignableFrom(target))
            {
                var arguments = target.GetGenericArguments();
                var factory = arguments.Length == 1 ? nameof(Callback0) : arguments.Length == 2 ? nameof(Callback1)
                    : arguments.Length == 3 ? nameof(Callback2) : throw new NotSupportedException("Unsupported HMI callback shape.");
                return typeof(UnifiedPrimitiveCompatibility).GetMethod(factory, BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(arguments).Invoke(null, new object[] { callback });
            }
            if (value is IEnumerable items && target.IsGenericType)
            {
                var element = target.GetGenericArguments().Last();
                if (target.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return typeof(UnifiedPrimitiveCompatibility).GetMethod(nameof(EnumerableValues), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(element).Invoke(null, new object[] { items });
                if (typeof(IList).IsAssignableFrom(target))
                {
                    var list = (IList)Activator.CreateInstance(target)!;
                    foreach (var item in items) list.Add(ConvertValue(item, element));
                    return list;
                }
            }
            var copy = Activator.CreateInstance(target, true)!;
            foreach (var field in target.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var source = value.GetType().GetField(field.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (source != null) field.SetValue(copy, ConvertValue(source.GetValue(value), field.FieldType));
            }
            return copy;
        }
        private static IEnumerable<T> EnumerableValues<T>(IEnumerable values)
        {
            Configure();
            foreach (var value in values) yield return (T)ConvertValue(value, typeof(T))!;
        }
        private static object? InvokeCallback(Delegate callback, Type result, object?[] args)
        {
            var signature = callback.GetType().GetMethod("Invoke")!;
            var prepared = signature.GetParameters().Select((p, i) => ConvertValue(args[i], p.ParameterType)).ToArray();
            try { return ConvertValue(callback.DynamicInvoke(prepared), result); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static Func<T> Callback0<T>(Delegate callback)
            => () => (T)InvokeCallback(callback, typeof(T), Array.Empty<object>())!;
        private static Func<T1, T> Callback1<T1, T>(Delegate callback)
            => first => (T)InvokeCallback(callback, typeof(T), new object?[] { first })!;
        private static Func<T1, T2, T> Callback2<T1, T2, T>(Delegate callback)
            => (first, second) => (T)InvokeCallback(callback, typeof(T), new object?[] { first, second })!;
        private static object? Plain(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonObject map) return map.ToDictionary(p => p.Key, p => Plain(p.Value));
            if (node is JsonArray array) return array.Select(Plain).ToList();
            using var doc = JsonDocument.Parse(node.ToJsonString());
            var value = doc.RootElement;
            return value.ValueKind == JsonValueKind.String ? value.GetString()
                : value.ValueKind == JsonValueKind.True ? (object)true : value.ValueKind == JsonValueKind.False ? false
                : value.TryGetInt32(out var integer) ? integer : value.GetDecimal();
        }
        private static object? NativePlain(JsonNode? node, Assembly assembly)
        {
            if (node is JsonObject) return ConvertValue(node, assembly.GetType("TiaMcp.Adapters.Native.Hmi.HmiObject", true)!);
            if (node is JsonArray) return ConvertValue(node, assembly.GetType("TiaMcp.Adapters.Native.Hmi.HmiArray", true)!);
            return Plain(node);
        }
        private static HardwareScalar Scalar(JsonNode? value)
            => value == null ? new HardwareScalar { Kind = "null" }
                : value.GetValueKind() == JsonValueKind.String ? new HardwareScalar { Kind = "string", Text = value.GetValue<string>() }
                : value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? new HardwareScalar { Kind = "boolean", Text = value.ToJsonString() }
                : new HardwareScalar { Kind = "number", Text = value.ToJsonString() };
    }
}
