using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.FunctionCharts;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.CfcLogic;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): typed CFC option package (Siemens.Engineering.CFC, identical on V20 / V21; replaces the reflective 2.7.x
    // ExchangeCfcCharts). Official entry: PlcSoftware.GetService<ChartProviderS7>() (null when CFC is not installed); ChartProvider
    // carries CompleteExport / SelectiveExport / Import (XML packed as ZIP) and the chart password functions, ChartProviderS7 adds
    // ExportInstructionData. Requirements per manual: PLC offline, protected charts are skipped by the export.
    // 2.7.42 real project (+S1-K1, CPU 1510SP F, CFC installed but no chart folder on the PLC): the provider was provided and CompleteExport
    // wrote an empty 484-byte ZIP, yet GetChartProtection("MCP_NONE") and ExportInstructionData(path) each took TIA Portal V21 down
    // (NonRecoverableException, process gone). The provider has no chart enumeration, so since 2.7.43 every name-bound or PLC-bound
    // action first takes the chart inventory from a CompleteExport preflight into a temporary ZIP and refuses when it is empty or the
    // chart is missing.
    public partial class Portal
    {
        private static ChartProviderS7 RequireChartProvider(PlcSoftware plc)
            => plc.GetService<ChartProviderS7>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "ChartProviderS7 is not provided by PLC software '" + plc.Name + "' (CFC not installed, or the PLC family has no CFC).");

        private static JsonObject CfcInventoryRow(Logic.ExportInventory inventory) => new JsonObject
        {
            ["chartCount"] = inventory.Charts.Length,
            ["chartNames"] = new JsonArray(inventory.Charts.Take(500).Select(c => (JsonNode)c).ToArray()),
            ["zipEntries"] = new JsonArray(inventory.Entries.Take(20).Select(e => (JsonNode)e).ToArray()),
            ["xmlElements"] = new JsonArray(inventory.Elements.Take(60).Select(e => (JsonNode)e).ToArray())
        };
        private static Logic.ExportInventory CfcPreflight(ChartProviderS7 provider, string modelVersion, long filter, JsonObject meta)
        {
            var temp = new FileInfo(Path.Combine(Path.GetTempPath(), "tia-mcp-cfc-preflight-" + Guid.NewGuid().ToString("N") + ".xml.zip"));
            try
            {
                provider.CompleteExport(temp.FullName, modelVersion, filter, true);
                temp.Refresh();
                var inventory = temp.Exists ? Logic.InspectExport(temp.FullName) : new Logic.ExportInventory();
                var row = CfcInventoryRow(inventory); row["method"] = "ChartProvider.CompleteExport to a temporary ZIP, Data.xml scanned for chart elements"; row["bytes"] = temp.Exists ? temp.Length : 0;
                meta["preflight"] = row;
                return inventory;
            }
            finally { try { if (temp.Exists) temp.Delete(); } catch { } }
        }
        private static void RequireCfcCharts(ChartProviderS7 provider, string[] chartNames, string modelVersion, long filter, JsonObject meta, string purpose)
        {
            var inventory = CfcPreflight(provider, modelVersion, filter, meta);
            if (inventory.Charts.Length == 0) throw new PortalException(PortalErrorCode.InvalidState, "The PLC has no CFC charts (CompleteExport preflight found none); " + purpose + " is refused - on the 2.7.42 real project this call took TIA Portal V21 down on such a PLC. meta.preflight lists the ZIP entries and XML elements seen.");
            var missing = chartNames.Where(n => !inventory.Charts.Contains(n, StringComparer.Ordinal)).ToArray();
            if (missing.Length > 0) throw new PortalException(PortalErrorCode.NotFound, "CFC chart(s) " + string.Join(", ", missing) + " not in the CompleteExport inventory (" + string.Join(", ", inventory.Charts.Take(20)) + "); " + purpose + " is refused (unknown chart names took TIA Portal V21 down).");
        }

        public ResponseMessage ExchangeCfcCharts(string softwarePath, string action, string filePath, string modelVersion = "", long filter = 0, bool unattended = true, bool deleteAtTarget = false, bool dryRun = true, string chartNamesJson = "[]", bool skipChartPreflight = false)
            => RunHmiStepTool("ExchangeCfcCharts", meta =>
            {
                var r = Logic.ValidateExchangeRequest(action, filePath, modelVersion, filter, chartNamesJson, deleteAtTarget, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, r.Writes);
                var provider = RequireChartProvider(plc);
                var file = action == "import" ? HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath") : NativeFileOutput.Plan(filePath);
                meta["software"] = plc.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["deleteAtTarget"] = deleteAtTarget; meta["unattended"] = unattended; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action != "exportInstructionData") { meta["modelVersion"] = modelVersion; meta["filter"] = filter; }
                if (action == "selectiveExport") meta["chartNames"] = new JsonArray(r.ChartNames.Select(n => (JsonNode)n).ToArray());
                // preflight (not for the complete export itself, which is the preflight, nor for import)
                bool needsPreflight = (action == "selectiveExport" || action == "exportInstructionData") && !skipChartPreflight;
                if (needsPreflight) RequireCfcCharts(provider, r.ChartNames, action == "exportInstructionData" ? "V2.0" : modelVersion, filter, meta, action);
                else if (skipChartPreflight && action != "export" && action != "import") meta["preflight"] = new JsonObject { ["skipped"] = true, ["warning"] = "Chart inventory not verified; on a PLC without CFC charts this call took TIA Portal V21 down (2.7.42 real project)." };
                if (dryRun) return "CFC " + action + " preview (ChartProvider " + (action == "import" ? "Import; deleteAtTarget removes charts absent from the file when enabled" : action == "exportInstructionData" ? "S7.ExportInstructionData" : action == "selectiveExport" ? "SelectiveExport of the named charts" : "CompleteExport of every chart, block type, task assignment and run sequence") + "); nothing changed.";
                meta["mayHaveChanged"] = action == "import"; meta["mayHaveWrittenFiles"] = action != "import";
                switch (action)
                {
                    case "export": meta["nativeSignature"] = "ChartProvider.CompleteExport(string, string, long, bool)"; provider.CompleteExport(file.FullName, modelVersion, filter, unattended); break;
                    case "selectiveExport": meta["nativeSignature"] = "ChartProvider.SelectiveExport(string, string[], string, long, bool)"; provider.SelectiveExport(file.FullName, r.ChartNames, modelVersion, filter, unattended); break;
                    case "exportInstructionData": meta["nativeSignature"] = "ChartProviderS7.ExportInstructionData(string)"; provider.ExportInstructionData(file.FullName); break;
                    default: meta["nativeSignature"] = "ChartProvider.Import(string, string, long, bool, bool)"; provider.Import(file.FullName, modelVersion, filter, unattended, deleteAtTarget); return "CFC charts imported (native void return; the Inspector window carries the import log). No independent chart verification, no save / compile / download.";
                }
                meta["file"] = NativeFileOutput.Verify(file);
                if (action == "export") Safe(meta, "inventory", () => CfcInventoryRow(Logic.InspectExport(file.FullName)));
                return "CFC " + action + " written and verified by size / SHA-256 (content semantics not asserted; password-protected charts are skipped natively). Project unchanged.";
            });

        public ResponseMessage ManageCfcChartProtection(string softwarePath, string chartName, string action = "read", string currentPassword = "", string newHashedPassword = "", bool dryRun = true, string modelVersion = "V2.0", bool skipChartPreflight = false)
            => RunHmiStepTool("ManageCfcChartProtection", meta =>
            {
                var r = Logic.ValidateProtectionRequest(action, chartName, currentPassword, newHashedPassword, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, r.Writes);
                var provider = RequireChartProvider(plc);
                meta["software"] = plc.Name; meta["chartName"] = chartName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!skipChartPreflight) RequireCfcCharts(provider, new[] { chartName }, string.IsNullOrEmpty(modelVersion) ? "V2.0" : modelVersion, 0, meta, "the chart password call");
                else meta["preflight"] = new JsonObject { ["skipped"] = true, ["warning"] = "Chart existence not verified; GetChartProtection of an unknown chart took TIA Portal V21 down (2.7.42 real project)." };
                Safe(meta, "before", () => { var hash = provider.GetChartProtection(chartName); return new JsonObject { ["protected"] = !string.IsNullOrEmpty(hash), ["passwordHash"] = hash }; });
                if (action == "read") return "CFC chart protection read (ChartProvider.GetChartProtection returns the password hash; empty = unprotected); nothing changed.";
                if (dryRun) return "CFC chart protection " + action + " preview; nothing changed (official: the password only guards against unintentional editing, it is no know-how protection).";
                meta["mayHaveChanged"] = true;
                bool accepted;
                switch (action)
                {
                    case "add": meta["nativeSignature"] = "ChartProvider.AddChartProtection(string, string)"; accepted = provider.AddChartProtection(chartName, newHashedPassword); break;
                    case "change": meta["nativeSignature"] = "ChartProvider.ChangeChartProtection(string, SecureString, string)"; using (var secure = PlcBlockServicesLogic.ToSecureString(currentPassword)) accepted = provider.ChangeChartProtection(chartName, secure, newHashedPassword); break;
                    default: meta["nativeSignature"] = "ChartProvider.RemoveChartProtection(string, SecureString)"; using (var secure = PlcBlockServicesLogic.ToSecureString(currentPassword)) accepted = provider.RemoveChartProtection(chartName, secure); break;
                }
                meta["nativeResult"] = accepted; if (!accepted) meta["operationSuccess"] = false;
                Safe(meta, "after", () => { var hash = provider.GetChartProtection(chartName); return new JsonObject { ["protected"] = !string.IsNullOrEmpty(hash), ["passwordHash"] = hash }; });
                return "CFC chart protection " + action + " returned " + accepted + " and was read back; no automatic save.";
            });
    }
}
