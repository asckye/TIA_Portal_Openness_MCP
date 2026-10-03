using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;

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

        // ---- exchange file inventory (2.7.43 preflight) ------------------------------------------------------------------------------
        // The exchange file is a ZIP holding Data.xml (S7TIA exchange model). 2.7.43 real project (PLC without charts): the export is
        // Document > DocumentInfo / FunctionChartsFolder(Name="Charts") > ObjectList / UsedAlarmClasses - the folder itself carries a Name, so
        // chart names are taken from elements whose name contains "Chart" but is neither a folder / list container nor a plural (2.7.44);
        // the distinct element names and ZIP entries are reported so an unexpected schema stays visible.
        internal static bool IsChartElement(string localName)
            => localName.IndexOf("Chart", StringComparison.OrdinalIgnoreCase) >= 0 && localName.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) < 0
               && localName.IndexOf("List", StringComparison.OrdinalIgnoreCase) < 0 && !localName.EndsWith("s", StringComparison.OrdinalIgnoreCase);
        internal sealed class ExportInventory { public string[] Charts = Array.Empty<string>(); public string[] Elements = Array.Empty<string>(); public string[] Entries = Array.Empty<string>(); }
        internal static ExportInventory InspectExport(string zipPath)
        {
            var charts = new List<string>(); var elements = new HashSet<string>(StringComparer.Ordinal); var entries = new List<string>();
            using (var archive = ZipFile.OpenRead(zipPath))
                foreach (var entry in archive.Entries)
                {
                    entries.Add(entry.FullName);
                    if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    using var stream = entry.Open();
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true });
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element) continue;
                        if (elements.Count < 200) elements.Add(reader.LocalName);
                        if (!IsChartElement(reader.LocalName)) continue;
                        var name = reader.GetAttribute("Name") ?? reader.GetAttribute("name");
                        if (!string.IsNullOrEmpty(name) && !charts.Contains(name, StringComparer.Ordinal)) charts.Add(name);
                    }
                }
            return new ExportInventory { Charts = charts.ToArray(), Elements = elements.OrderBy(e => e, StringComparer.Ordinal).ToArray(), Entries = entries.ToArray() };
        }
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
