using System;
using System.Linq;

namespace TiaMcp.Adapters.Hardware
{
    internal static class HardwarePath
    {
        internal static string[] Parts(string path, bool rootAllowed = false)
        {
            if (rootAllowed && string.IsNullOrEmpty(path)) return Array.Empty<string>();
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (parts.Length > 64 || parts.Any(x => string.IsNullOrWhiteSpace(x) || x == "." || x == ".."))
                throw new ArgumentException("Use a relative exact path with 1-64 nonempty segments, without '.' or '..'.");
            return parts;
        }
    }
}
