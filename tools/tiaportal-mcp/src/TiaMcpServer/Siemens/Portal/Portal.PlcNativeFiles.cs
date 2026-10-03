using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Loader;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static string[] ExactNameList(string json)
        {
            if (json.Length > 65536) throw new ArgumentException("Name list too large.");
            var names = (JsonNode.Parse(json) as JsonArray ?? throw new ArgumentException("Expected a JSON array of exact paths.")).Select(n => n!.GetValue<string>()).ToArray();
            if (names.Length < 1 || names.Length > 500 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new ArgumentException("Provide 1..500 unique nonempty paths.");
            return names;
        }

    }
}
