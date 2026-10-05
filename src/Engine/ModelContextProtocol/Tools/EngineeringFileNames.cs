using System;
using System.IO;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class EngineeringFileNames
    {
        internal static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(name) ? "plc_artifact" : name.Trim())
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();
            var safe = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "plc_artifact" : safe;
        }
    }
}
