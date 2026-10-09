using System;
using System.Linq;
namespace TiaMcp.Adapters.Contracts
{
    public static class PlcOrganisationPaths
    {
        public static bool IsBlockRoot(string name) => string.Equals(name, "Program blocks", StringComparison.OrdinalIgnoreCase) || name == "程序块";
        public static string[] Parse(string path, bool types)
        {
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (parts.Length > 0 && (types ? string.Equals(parts[0], "PLC data types", StringComparison.OrdinalIgnoreCase) || parts[0] == "PLC 数据类型" : IsBlockRoot(parts[0]))) parts = parts.Skip(1).ToArray();
            if (parts.Length == 0 || types && parts.Length > 64 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".."))
                throw new PlcSoftwareException("InvalidParams", types ? "Specify a non-root type group path without empty, '.' or '..' segments." : "Specify an exact non-root user group path; empty and relative segments are refused.");
            return parts;
        }
    }
}
