using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Optional;

internal static class OptionalScalars
{
    internal static object? Json(object? value) => HmiScalar.Json(value);
    internal static HmiObject Read(object value) => HmiScalar.Read(value);
    internal static object? ConvertValue(object? value, Type type) => HmiScalar.ConvertValue(value, type);
    internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, HmiObject values)
    {
        values.ScalarInputs = new Dictionary<string, HardwareScalar>();
        foreach (var pair in values)
        {
            if (pair.Value is HmiObject || pair.Value is HmiArray) throw new ArgumentException("Only scalar values are supported.");
            values.ScalarInputs[pair.Key] = new HardwareScalar { Kind = pair.Value == null ? "null" : pair.Value is string ? "string"
                : pair.Value is bool ? "boolean" : "number", Text = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "" };
        }
        return TiaMcp.Adapters.Native.Security.SecurityProperties.Prepare(type, values);
    }
    internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> values, HmiObject meta) => HardwareScalarEvidence.Apply(target, values, meta);
}
