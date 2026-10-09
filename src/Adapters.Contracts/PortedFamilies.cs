using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // Reviewed release availability, shared by the host, worker and roster generators.
    // Availability never implies native acceptance; ported families remain current / NOT RUN.
    public static class PortedFamilies
    {
        public sealed class Family
        {
            public string Name { get; }
            public string OperationPrefix { get; }
            public string[] Releases { get; }
            public string[] Tools { get; }
            public IReadOnlyDictionary<string, string[]> Actions { get; }
            public Family(string name, string operationPrefix, string[] releases, string[] tools,
                IReadOnlyDictionary<string, string[]>? actions = null)
            { Name = name; OperationPrefix = operationPrefix; Releases = releases; Tools = tools;
                Actions = actions ?? new Dictionary<string, string[]>(StringComparer.Ordinal); }
            public bool Available(string release) => Releases.Contains(release, StringComparer.Ordinal);
            public bool SupportsAction(string release, string action) => Available(release)
                && (Actions.Count == 0 || Actions.TryGetValue(action, out var releases) && releases.Contains(release, StringComparer.Ordinal));
        }

        public static readonly IReadOnlyList<Family> All = new[] {
            new Family("F19", "hardware-addressing",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GetDeviceAddressing", "GetDeviceIpAddress", "GetDeviceItemIoAddresses", "SetDeviceAddress", "SetDeviceItemIoAddress" })
        };

        public static Family ForTool(string tool) => All.Single(f => f.Tools.Contains(tool, StringComparer.Ordinal));
        public static bool Contains(string tool) => All.Any(f => f.Tools.Contains(tool, StringComparer.Ordinal));
        public static bool Available(string release, string tool) => ForTool(tool).Available(release);
    }
}
