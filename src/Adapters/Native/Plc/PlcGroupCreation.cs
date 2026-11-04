using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcGroupCreation
    {
        internal static string[] Parse(string path) => PlcOrganisationPaths.Parse(path, true);

        internal static Dictionary<string, object?> Execute<T>(T root, string path, bool dryRun,
            Func<T, IEnumerable<T>> children, Func<T, string> name, Func<T, string, T> create, bool stripTypeRoot = true, Func<string, string[]>? pathParts =
                null) where T : class
        {
            var parts = pathParts != null ? pathParts(path) : stripTypeRoot ? Parse(path) : PlcGroupOperations.Parts(path);
            T? current = root;
            var created = new List<object?>();
            var missing = new List<object?>();
            string prefix = "";
            try
            {
                foreach (var part in parts)
                {
                    var segment = pathParts == null ? part : Uri.EscapeDataString(part);
                    prefix = prefix.Length == 0 ? (pathParts == null ? "" : "/") + segment : prefix + "/" + segment;
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
                throw new PlcSoftwareException("OpennessError",
                    "Type group creation failed at '" + prefix + "'. Groups already created (not rolled back): " + PlcSoftwareValues.StringArray(created)
                        + ". " + ex.Message, null, ex);
            }
            return new Dictionary<string, object?> {
                ["success"] = true, ["groupPath"] = pathParts == null ? string.Join("/", parts) : "/" + string.Join("/", parts.Select(Uri.EscapeDataString)),
                ["dryRun"] = dryRun, ["createdCount"] = created.Count, ["createdPaths"] = created,
                ["missingPaths"] = missing, ["alreadyExisted"] = missing.Count == 0 };
        }
    }
}
