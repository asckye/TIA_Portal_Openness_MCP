using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Safety;

internal static class SafetyScalars
{
    internal static HmiObject Values(Dictionary<string, HardwareScalar> inputs)
        => new HmiObject(HardwareScalarEvidence.Requested(inputs)) { ScalarInputs = inputs };
    internal static object? Json(object? value) => HmiScalar.Json(value);
    internal static object? ConvertValue(object? value, Type type) => HmiScalar.ConvertValue(value, type);
    internal static HmiObject Read(object value) => HmiScalar.Read(value);
    internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, HmiObject values) => TiaMcp.Adapters.Native.Security.SecurityProperties.Prepare(type, values);
    internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> values, HmiObject meta) => HardwareScalarEvidence.Apply(target, values, meta);
}
