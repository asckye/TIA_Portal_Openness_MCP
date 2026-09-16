using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class PlcTypeGroupCreation
    {
        internal static string[] Parse(string path)
        {
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (parts.Length > 0 && (string.Equals(parts[0], "PLC data types", StringComparison.OrdinalIgnoreCase) || parts[0] == "PLC 数据类型"))
                parts = parts.Skip(1).ToArray();
            if (parts.Length == 0 || parts.Length > 64 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".."))
                throw new PortalException(PortalErrorCode.InvalidParams, "Specify a non-root type group path without empty, '.' or '..' segments.");
            return parts;
        }

        internal static JsonObject Execute<T>(T root, string path, bool dryRun,
            Func<T, IEnumerable<T>> children, Func<T, string> name, Func<T, string, T> create, bool stripTypeRoot = true) where T : class
        {
            var parts = stripTypeRoot ? Parse(path) : EngineeringGroupOperations.Parts(path);
            T? current = root;
            var created = new JsonArray();
            var missing = new JsonArray();
            string prefix = "";
            try
            {
                foreach (var part in parts)
                {
                    prefix = prefix.Length == 0 ? part : prefix + "/" + part;
                    var matches = current == null ? new List<T>() : children(current)
                        .Where(g => string.Equals(name(g), part, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                    if (matches.Count > 1) throw new InvalidOperationException("Ambiguous type group: " + prefix);
                    if (matches.Count == 1) { current = matches[0]; continue; }
                    missing.Add(prefix);
                    if (dryRun) { current = null; continue; }
                    current = create(current!, part);
                    created.Add(prefix);
                }
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    "Type group creation failed at '" + prefix + "'. Groups already created (not rolled back): " + created.ToJsonString() + ". " + ex.Message, null, ex);
            }
            return new JsonObject { ["success"] = true, ["groupPath"] = string.Join("/", parts),
                ["dryRun"] = dryRun, ["createdCount"] = created.Count, ["createdPaths"] = created,
                ["missingPaths"] = missing, ["alreadyExisted"] = missing.Count == 0 };
        }
    }
}
