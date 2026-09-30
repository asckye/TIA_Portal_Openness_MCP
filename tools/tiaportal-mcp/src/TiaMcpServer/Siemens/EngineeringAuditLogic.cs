using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringAuditLogic
    {
        // A compile-capable service says nothing about its current state. Empty/failed reads are unknown.
        internal static bool? Consistency(IEnumerable<bool?> values, bool complete)
        {
            var rows = values.ToArray();
            if (rows.Any(x => x == false)) return false;
            return complete && rows.Length > 0 && rows.All(x => x == true) ? true : (bool?)null;
        }
        internal static bool? DownloadReady(bool provider, bool configuration, bool? consistent, bool errors)
            => !provider || !configuration || consistent == false || errors ? false : (bool?)null;

        internal static JsonObject DocumentPreflight(string text, int major, int? installedUpdate)
        {
            bool textual = Regex.IsMatch(text, @"\bS7_(?:TextualInterface|Textual)\b", RegexOptions.IgnoreCase)
                || text.IndexOf("TextualInterface", StringComparison.OrdinalIgnoreCase) >= 0;
            bool expandedLanguage = Regex.IsMatch(text, @"\b(SCL|FBD)\b", RegexOptions.IgnoreCase);
            var warnings = new JsonArray();
            if (major == 20 && (installedUpdate == null || installedUpdate < 4))
                warnings.Add("V20 SCL/FBD/mixed-network SIMATIC SD requires Update 4. Installed patch is not inferred from an SDK copy; verify the installation before importing.");
            if (major == 20)
                warnings.Add("Siemens documents possible data loss for V20 SCL textual interfaces. This lexical scan cannot rule out that interface form. Use a table interface and compare an exported readback before accepting the import.");
            warnings.Add("SIMATIC SD is not a lossless project backup: OB classification, supervisions/alarms, SiVArc and certain attributes are not preserved by the format.");
            return new JsonObject { ["major"] = major, ["installedUpdate"] = installedUpdate,
                ["expandedLanguageDetected"] = expandedLanguage, ["textualInterfaceDetected"] = textual,
                ["patchSupportKnown"] = major > 20 || major == 20 && installedUpdate >= 4,
                ["losslessRoundTrip"] = false, ["warnings"] = warnings,
                ["source"] = "https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-step-7/improvements-in-update-4" };
        }
        internal static bool ExactNamesPresent(IEnumerable<string> imported, IEnumerable<string> actual)
        {
            var expected = imported.ToArray();
            var present = new HashSet<string>(actual, StringComparer.Ordinal);
            return expected.Length > 0 && expected.All(present.Contains);
        }
    }
}
