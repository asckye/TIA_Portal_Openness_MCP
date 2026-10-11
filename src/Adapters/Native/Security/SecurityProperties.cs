using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Security;

internal static class SecurityProperties
{
    internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, HmiObject changes)
    {
        if (changes.Count > 50) throw new ArgumentException("At most 50 properties per request.");
        var result = new List<(PropertyInfo, object?)>();
        foreach (var change in changes)
        {
            var property = HmiScalar.DistinctProperties(type).FirstOrDefault(p => p.Name == change.Key);
            if (property == null || property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                throw new NotSupportedException("Public writable property unavailable: " + type.FullName + "." + change.Key);
            result.Add((property, HmiScalar.ConvertInput(changes, change.Key, property.PropertyType)));
        }
        return result;
    }
    internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> changes, HmiObject meta)
        => HardwareScalarEvidence.Apply(target, changes, meta);
}
