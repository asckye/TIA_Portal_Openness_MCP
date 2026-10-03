using System;
using System.Collections.Generic;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static string ReadReflectedString(object? owner, string propertyName)
        {
            try { return owner?.GetType().GetProperty(propertyName)?.GetValue(owner)?.ToString() ?? string.Empty; }
            catch /* swallow(probe-optional): an unavailable route label is represented by an empty string */ { return string.Empty; }
        }

        // Read a property by name and materialize it as a sequence (Openness compositions are
        // IEnumerable). Materialized inside try/catch so a throwing enumerator can't escape.
        private static List<object?> EnumerateReflectedProperty(object? owner, string propertyName)
        {
            var items = new List<object?>();
            try
            {
                var value = owner?.GetType().GetProperty(propertyName)?.GetValue(owner);
                if (value is System.Collections.IEnumerable en)
                    foreach (var item in en) items.Add(item);
            }
            catch /* swallow(enumerate-optional): return route items collected before an optional composition becomes unavailable */ { }
            return items;
        }

    }
}
