using System;
using System.Reflection;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static object? WatchProperty(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                try
                {
                    var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v != null) return v;
                }
                catch /* swallow(probe-optional): an unavailable property lets the next candidate property be tried */ { }
            }
            return null;
        }

    }
}
