using System;
using System.IO;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): pure logic (no Siemens dependency) for the CFC option package (Siemens.Engineering.CFC, identical on
    // V20 / V21): ChartProvider XML (ZIP) exchange, chart passwords and the ChartProviderS7 instruction data export.
    internal static class CfcLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireText(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }

        internal static readonly string[] ExchangeActions = { "export", "selectiveExport", "import", "exportInstructionData" };
        internal static readonly string[] ProtectionActions = { "read", "add", "change", "remove" };

        // ---- exchange ------------------------------------------------------------------------------------------------------------
        internal sealed class ExchangeRequest { public string Action = ""; public bool Writes; public bool WritesFile; public string[] ChartNames = Array.Empty<string>(); }
        internal static ExchangeRequest ValidateExchangeRequest(string action, string filePath, string modelVersion, long filter, string chartNamesJson, bool deleteAtTarget, bool dryRun)
        {
            RequireOneOf(action, ExchangeActions, "action");
            var r = new ExchangeRequest { Action = action };
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("filePath must be an absolute path (official: the XML is exchanged as a ZIP, e.g. Chart1.xml.zip; instruction data is a plain file).");
            if (action == "exportInstructionData") { Refuse(modelVersion, "modelVersion", "does not apply to exportInstructionData (ChartProviderS7.ExportInstructionData(filePath))."); if (filter != 0) throw new ArgumentException("filter does not apply to exportInstructionData."); }
            else RequireText(modelVersion, "modelVersion", 32);                                     // official example: "V2.0" (S7TIA exchange model version)
            if (filter < 0) throw new ArgumentException("filter must be >= 0 (official: Int64 automation-interface filter, 0 = none; not evaluated by the current CFC version).");
            r.ChartNames = SivarcLogic.ParseNames(chartNamesJson, "chartNamesJson");
            if (action == "selectiveExport") { if (r.ChartNames.Length == 0) throw new ArgumentException("selectiveExport needs chartNamesJson with at least one chart name (ChartProvider.SelectiveExport(path, string[] selectedObjects, ...))."); }
            else if (r.ChartNames.Length > 0) throw new ArgumentException("chartNamesJson applies to selectiveExport only (CompleteExport writes every chart of the PLC).");
            if (deleteAtTarget && action != "import") throw new ArgumentException("deleteAtTarget applies to import only.");
            r.Writes = action == "import" && !dryRun; r.WritesFile = action != "import" && !dryRun;
            return r;
        }

        // ---- chart protection ------------------------------------------------------------------------------------------------------
        internal sealed class ProtectionRequest { public string Action = ""; public bool Writes; }
        internal static ProtectionRequest ValidateProtectionRequest(string action, string chartName, string currentPassword, string newHashedPassword, bool dryRun)
        {
            RequireOneOf(action, ProtectionActions, "action"); RequireText(chartName, "chartName");
            if (action == "change" || action == "remove") { if (string.IsNullOrEmpty(currentPassword)) throw new ArgumentException("currentPassword is required for " + action + " (official: SecureString; never logged)."); }
            else Refuse(currentPassword, "currentPassword", "applies to change / remove only.");
            if (action == "add" || action == "change") RequireText(newHashedPassword, "newHashedPassword", 4096);
            else Refuse(newHashedPassword, "newHashedPassword", "applies to add / change only (the value is the password hash as TIA displays it).");
            return new ProtectionRequest { Action = action, Writes = action != "read" && !dryRun };
        }
    }
}
