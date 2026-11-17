using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    // Object names are decoded only after splitting: an escaped slash belongs to one name.
    internal static class PlcObjectPath
    {
        internal static string[] Parts(string path, bool rootAllowed = false, string parameter = "groupPath")
        {
            if (rootAllowed && path == "") return Array.Empty<string>();
            var parts = (path ?? "").Split('/').Select(Uri.UnescapeDataString).ToArray();
            if ((path ?? "").Contains("\\") || parts.Length > 64 || parts.Any(name => string.IsNullOrWhiteSpace(name) || name == "." || name == ".."))
                throw new AdapterPreconditionException(
                    "Use a relative exact path with 1-64 nonempty segments, without '.', '..' or unencoded backslashes; encode name backslashes as %5C.",
                    parameter);
            return parts;
        }

        internal static string Format(IEnumerable<string> names) => string.Join("/", names.Select(Uri.EscapeDataString));
    }
}
