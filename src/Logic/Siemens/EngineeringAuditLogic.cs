using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

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
            TiaMcp.Adapters.Contracts.SdBlockDeclaration header;
            string? scanProblem = null;
            try { header = TiaMcp.Adapters.Contracts.SdBlockDeclaration.Scan(text); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {
                scanProblem = error.Message;
                header = new TiaMcp.Adapters.Contracts.SdBlockDeclaration();
            }
            var warnings = new JsonArray();
            if (major == 20 && (installedUpdate == null || installedUpdate < 4))
                warnings
                    .Add("V20 SCL/FBD/mixed-network SIMATIC SD requires Update 4. Installed patch is not inferred from an SDK copy; verify the installation " +
                    "before importing.");
            if (major == 20)
                warnings
                    .Add("Siemens documents possible data loss for V20 SCL textual interfaces. This lexical scan cannot rule out that interface form. Use a " +
                    "table interface and compare an exported readback before accepting the import.");
            warnings
                .Add("SIMATIC SD can change OB classification, numbering and DB attributes. Block property readback cannot verify " +
                    string.Join(", ", TiaMcp.Adapters.Contracts.SdBlockOverridePolicy.NotCarried) + ".");
            var declarations = new JsonArray();
            foreach (var entry in header.Declarations) declarations.Add(new JsonObject { ["kind"] = entry.Kind, ["name"] = entry.Name });
            var pragmas = new JsonObject();
            foreach (var entry in header.Pragmas) pragmas[entry.Key] = entry.Value;
            var inspection = new JsonObject { ["declarations"] = declarations, ["pragmas"] = pragmas, ["major"] = major, ["installedUpdate"] = installedUpdate,
                ["expandedLanguageDetected"] = header.ExpandedLanguage, ["textualInterfaceDetected"] = header.TextualInterface,
                ["patchSupportKnown"] = major > 20 || major == 20 && installedUpdate >= 4,
                ["losslessRoundTrip"] = false, ["warnings"] = warnings,
                ["source"] = "https://docs.tia.siemens.cloud/r/en-us/v21/creating-and-managing-blocks/" +
                    "exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500-s7-1200-g2/" +
                    "exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500-s7-1200-g2",
                ["updateSource"] = "https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/" +
                    "improvements-in-step-7/improvements-in-update-4" };
            if (scanProblem != null)
            {
                inspection["scanProblem"] = scanProblem;
                warnings.Add(scanProblem);
            }
            return inspection;
        }
        internal static bool ExactNamesPresent(IEnumerable<string> imported, IEnumerable<string> actual)
        {
            var expected = imported.ToArray();
            var present = new HashSet<string>(actual, StringComparer.Ordinal);
            return expected.Length > 0 && expected.All(present.Contains);
        }
    }
}
