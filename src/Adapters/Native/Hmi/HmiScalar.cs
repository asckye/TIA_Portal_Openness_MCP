using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
namespace TiaMcp.Adapters.Native.Hmi { internal static partial class HmiScalar {
internal static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Version);
internal static HmiObject Read(object target)
        {
            var values = new HmiObject(); var failures = new HmiArray(); var excluded = new HmiArray();
            foreach (var p in DistinctProperties(target.GetType()))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod?.IsPublic != true) continue;
                if (!Scalar(p.PropertyType) && p.PropertyType != typeof(object)) { excluded.Add(p.Name); continue; }
                try {
                    var value = p.GetValue(target);
                    if (value != null && !Scalar(value.GetType())) { excluded.Add(p.Name); continue; }
                    values[p.Name] = Json(value);
                }
                catch (Exception ex) { failures.Add(new HmiObject { ["property"] = p.Name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            return new HmiObject { ["type"] = target.GetType().FullName, ["scope"] = "public scalar properties only",
                ["values"] = values, ["excludedComplexProperties"] = excluded, ["failures"] = failures,
                ["dataComplete"] = failures.Count == 0, ["fullObjectComplete"] = excluded.Count == 0 && failures.Count == 0 };
        }
internal static IEnumerable<PropertyInfo> DistinctProperties(Type type)
        {
            int Depth(PropertyInfo p) { int d = 0; for (var t = type; t != null && t != p.DeclaringType; t = t.BaseType) d++; return d; }
            return type.GetProperties().GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.OrderBy(Depth).First()).OrderBy(p => p.Name, StringComparer.Ordinal);
        }
internal static bool SameValue(object? actual, object? expected)
        {
            if (actual == null || expected == null) return actual == null && expected == null;
            if (Equals(actual, expected)) return true;
            if (actual.GetType() == expected.GetType()) return false;
            var a = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? ""; var e = Convert.ToString(expected, CultureInfo.InvariantCulture) ?? "";
            if (a == e) return true;
            return decimal.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da) && decimal.TryParse(e, NumberStyles.Any, CultureInfo.InvariantCulture, out var de) && da == de;
        }
} }
