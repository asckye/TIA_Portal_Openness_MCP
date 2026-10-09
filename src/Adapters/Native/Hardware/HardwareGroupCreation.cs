using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Hardware
{
    internal static class HardwareGroupCreation
    {
        private static string Quote(string value)
        {
            var text = new System.Text.StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '\\') text.Append("\\\\");
                else if (c == '\n') text.Append("\\n");
                else if (c == '\r') text.Append("\\r");
                else if (c == '\t') text.Append("\\t");
                else if (c == '\b') text.Append("\\b");
                else if (c == '\f') text.Append("\\f");
                else if (c < 32 || c > 126 || c == '"' || c == '\'' || c == '<' || c == '>' || c == '&' || c == '+')
                    text.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }

        internal static Dictionary<string, object?> Execute<T>(T root, string path, bool dryRun,
            Func<T, IEnumerable<T>> children, Func<T, string> name, Func<T, string, T> create, bool stripTypeRoot = false) where T : class
        {
            var parts = HardwareGroupOperations.Parts(path);
            T? current = root;
            var created = new List<object?>();
            var missing = new List<object?>();
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
                throw new HardwareAddressingException("OpennessError",
                    "Type group creation failed at '" + prefix + "'. Groups already created (not rolled back): " + "[" + string.Join(",", created.Select(value => Quote((string)value!))) + "]" + ". " + ex.Message);
            }
            var result = PlcFoundationEngine.HardwareStepMeta(null, true);
            foreach (var pair in new Dictionary<string, object?> { ["groupPath"] = string.Join("/", parts),
                ["dryRun"] = dryRun, ["createdCount"] = created.Count, ["createdPaths"] = created,
                ["missingPaths"] = missing, ["alreadyExisted"] = missing.Count == 0 }) result[pair.Key] = pair.Value;
            return result;
        }
    }
}
