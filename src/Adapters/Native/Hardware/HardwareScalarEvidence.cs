using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Hardware
{
    internal static class HardwareScalarEvidence
    {
        internal static Dictionary<string, object?> Requested(Dictionary<string, HardwareScalar> values)
            => values.ToDictionary(p => p.Key, p => HardwareAddressingPolicy.ConvertValue(p.Value, typeof(object)));
        internal static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Version);

        internal static Dictionary<string, object?> Read(object target)
        {
            var values = new Dictionary<string, object?>(); var failures = new List<object?>(); var excluded = new List<object?>();
            foreach (var p in DistinctProperties(target.GetType()))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod?.IsPublic != true) continue;
                if (!Scalar(p.PropertyType) && p.PropertyType != typeof(object)) { excluded.Add(p.Name); continue; }
                try {
                    var value = p.GetValue(target);
                    if (value != null && !Scalar(value.GetType())) { excluded.Add(p.Name); continue; }
                    values[p.Name] = HardwareAddressingPolicy.ScalarValue(value);
                }
                catch (Exception ex) { failures.Add(new Dictionary<string, object?> { ["property"] = p.Name, ["error"] = ex.GetBaseException().Message }); if (TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(ex)) throw; }
            }
            return new Dictionary<string, object?> { ["type"] = target.GetType().FullName, ["scope"] = "public scalar properties only",
                ["values"] = values, ["excludedComplexProperties"] = excluded, ["failures"] = failures,
                ["dataComplete"] = failures.Count == 0, ["fullObjectComplete"] = excluded.Count == 0 && failures.Count == 0 };
        }

        internal static IEnumerable<PropertyInfo> DistinctProperties(Type type)
        {
            int Depth(PropertyInfo p) { int d = 0; for (var t = type; t != null && t != p.DeclaringType; t = t.BaseType) d++; return d; }
            return type.GetProperties().GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.OrderBy(Depth).First()).OrderBy(p => p.Name, StringComparer.Ordinal);
        }

        internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> changes, Dictionary<string, object?> meta)
        {
            var applied = new List<object?>(); meta["appliedProperties"] = applied;
            foreach (var change in changes)
            {
                meta["mayHaveChanged"] = true; meta["lastAttemptedProperty"] = change.Property.Name;
                change.Property.SetValue(target, change.Value);
                applied.Add(change.Property.Name);
                if (!HardwareAddressingPolicy.SameValue(change.Property.GetValue(target), change.Value)) throw new InvalidOperationException("Property readback differs: " + change.Property.Name + ". Changes are not rolled back.");
            }
        }
    }
}
