using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static partial class HmiScalar
    {
internal static object? Json(object? value) => value != null && HmiValueTypes.Render(value) is object rendered ? rendered : value is TimeSpan span ? span.ToString("c", CultureInfo.InvariantCulture)
            : value != null && (value.GetType().IsEnum || value is Version || value is Guid || value is DateTime)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : value;
        internal static object? ConvertValue(object? value, Type type) => HardwareAddressingPolicy.ConvertValue(value == null ? new HardwareScalar { Kind = "null" } : value is string text ? new HardwareScalar { Kind = "string", Text = text } : value is bool flag ? new HardwareScalar { Kind = "boolean", Text = flag ? "true" : "false" } : new HardwareScalar { Kind = "number", Text = Convert.ToString(value, CultureInfo.InvariantCulture)! }, type);
        internal static object? ConvertInput(HmiObject values, string name, Type type)
            => values.ScalarInputs != null && values.ScalarInputs.TryGetValue(name, out var scalar)
                ? HmiScalarPolicy.ConvertValue(scalar, type) : ConvertValue(values[name], type);
    }
}
