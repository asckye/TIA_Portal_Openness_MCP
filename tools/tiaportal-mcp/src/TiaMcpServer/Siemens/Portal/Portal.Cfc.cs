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
    public partial class Portal
    {
        private static ChartProviderS7 RequireChartProvider(PlcSoftware plc)
            => plc.GetService<ChartProviderS7>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "ChartProviderS7 is not provided by PLC software '" + plc.Name + "' (CFC not installed, or the PLC family has no CFC).");

        public ResponseMessage ExchangeCfcCharts(string softwarePath, string action, string filePath, string modelVersion = "", long filter = 0, bool unattended = true, bool deleteAtTarget = false, bool dryRun = true, string chartNamesJson = "[]")
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
                return "CFC " + action + " written and verified by size / SHA-256 (content semantics not asserted; password-protected charts are skipped natively). Project unchanged.";
            });

        public ResponseMessage ManageCfcChartProtection(string softwarePath, string chartName, string action = "read", string currentPassword = "", string newHashedPassword = "", bool dryRun = true)
            => RunHmiStepTool("ManageCfcChartProtection", meta =>
            {
                var r = Logic.ValidateProtectionRequest(action, chartName, currentPassword, newHashedPassword, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, r.Writes);
                var provider = RequireChartProvider(plc);
                meta["software"] = plc.Name; meta["chartName"] = chartName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
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
