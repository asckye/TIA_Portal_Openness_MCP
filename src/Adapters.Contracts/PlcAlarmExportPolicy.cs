using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;

namespace TiaMcp.Adapters.Contracts
{
    internal static class PlcAlarmExportPolicy
    {
        internal static string MissingTextListGuidance(string release) => PortedFamilies.Available(release, "ManagePlcAlarmTextList")
            ? "This PLC has no alarm text list; create one in TIA or with ManagePlcAlarmTextList createFromMasterCopy."
            : "This PLC has no alarm text list; create one in TIA.";

        private static readonly string NoExportableAlarms = new ResourceManager("TiaMcp.Adapters.Contracts.VendorDiagnostics",
            typeof(PlcAlarmExportPolicy).Assembly).GetString(nameof(NoExportableAlarms), CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException(nameof(NoExportableAlarms));

        internal static bool NoAlarms(string reason) => reason.Contains(NoExportableAlarms);

        internal static void RequireUserLists(IEnumerable<string> available, string[] requested)
        {
            var names = new HashSet<string>(available, StringComparer.Ordinal);
            if (names.Count == 0 || requested.Any(name => !names.Contains(name)))
                throw AdapterPreconditionException.Failed("The selected owner has no matching user alarm text lists.", "plc-text-lists",
                    requested.Length == 0 ? "softwarePath" : "textListNames");
        }
    }
}
