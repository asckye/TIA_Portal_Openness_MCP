using System;
using System.Globalization;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Optional;

internal static class OptionalInputs
{
    [ThreadStatic] internal static OptionalEngineeringRequest? Current;
    internal static object? Parse(string raw)
    {
        var request = Current ?? throw new InvalidOperationException("Optional-product request unavailable.");
        if (request.InputErrors.TryGetValue(raw, out var error)) throw new ArgumentException(error);
        if (!request.Inputs.TryGetValue(raw, out var value)) throw new ArgumentException("Unprepared optional-product input.");
        return Value(value);
    }
    private static object? Value(OptionalEngineeringValue value) => value.Kind == "object"
        ? new HmiObject(value.Properties.ToDictionary(p => p.Key, p => Value(p.Value)))
        : value.Kind == "array" ? new HmiArray(value.Items.Select(Value).ToArray())
        : HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = value.Kind, Text = value.Text }, typeof(object));
    internal static T OptionalValue<T>(this object value)
    {
        if (value is T typed) return typed;
        if (typeof(T) == typeof(string) || typeof(T) == typeof(bool)) throw new InvalidOperationException("Scalar input has an incompatible type.");
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
    internal static bool TryOptionalValue<T>(this object value, out T result)
    {
        if (value is T typed) { result = typed; return true; }
        result = default!;
        return false;
    }
}
