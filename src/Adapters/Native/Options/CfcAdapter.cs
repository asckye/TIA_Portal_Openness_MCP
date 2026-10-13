#if CFC_CHARTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaOpenness.Shared;
using Siemens.Engineering.SW.FunctionCharts;

namespace TiaMcp.Adapters.Native.Options;

internal sealed class CfcAdapter
{
    private readonly OptionalProductSession _session;
    internal CfcAdapter(OptionalProductSession session) => _session = session;
        private static ChartProviderS7 RequireChartProvider(PlcSoftware plc)
            => plc.GetService<ChartProviderS7>() ?? throw new PlcSoftwareException("NotSupportedOnVersion", "ChartProviderS7 is not provided by PLC software '" + plc.Name + "' (CFC not installed, or the PLC family has no CFC).");

        private static HmiObject CfcInventoryRow(CfcInventory.ExportInventory inventory) => new HmiObject
        {
            ["chartCount"] = inventory.Charts.Length,
            ["chartNames"] = new HmiArray(inventory.Charts.Take(500).Select(c => (object?)c).ToArray()),
            ["zipEntries"] = new HmiArray(inventory.Entries.Take(20).Select(e => (object?)e).ToArray()),
            ["xmlElements"] = new HmiArray(inventory.Elements.Take(60).Select(e => (object?)e).ToArray())
        };
        private static CfcInventory.ExportInventory CfcPreflight(ChartProviderS7 provider, string modelVersion, long filter, HmiObject meta)
        {
            var temp = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia-mcp-cfc-preflight-" + Guid.NewGuid().ToString("N") + ".xml.zip")));
            try
            {
                provider.CompleteExport(temp.FullName, modelVersion, filter, true);
                temp.Refresh();
                var inventory = temp.Exists ? CfcInventory.InspectExport(temp.FullName) : new CfcInventory.ExportInventory();
                var row = CfcInventoryRow(inventory); row["method"] = "ChartProvider.CompleteExport to a temporary ZIP, Data.xml scanned for chart elements"; row["bytes"] = temp.Exists ? temp.Length : 0;
                meta["preflight"] = row;
                return inventory;
            }
            finally { try { if (temp.Exists) temp.Delete(); } catch { /* swallow(cleanup): Temporary preflight ZIP deletion must not replace the inventory result or the original export failure. */ } }
        }
        private static void RequireCfcCharts(ChartProviderS7 provider, string[] chartNames, string modelVersion, long filter, HmiObject meta, string purpose)
        {
            var inventory = CfcPreflight(provider, modelVersion, filter, meta);
            if (inventory.Charts.Length == 0) throw new PlcSoftwareException("InvalidState", "The PLC has no CFC charts (CompleteExport preflight found none); " + purpose + " is refused - on the 2.7.42 real project this call took TIA Portal V21 down on such a PLC. meta.preflight lists the ZIP entries and XML elements seen.");
            var missing = chartNames.Where(n => !inventory.Charts.Contains(n, StringComparer.Ordinal)).ToArray();
            if (missing.Length > 0) throw new PlcSoftwareException("NotFound", "CFC chart(s) " + string.Join(", ", missing) + " not in the CompleteExport inventory (" + string.Join(", ", inventory.Charts.Take(20)) + "); " + purpose + " is refused (unknown chart names took TIA Portal V21 down).");
        }

        private void RequireSafeChartInventory(HmiObject meta, string action)
        {
            try { CfcSafetyPolicy.RequireSafeChartInventory(int.Parse(_session.ReleaseKey, System.Globalization.CultureInfo.InvariantCulture), "CFC " + action); }
            catch (NotSupportedException error) { meta["unsupportedReason"] = error.Message; throw; }
        }

        public OptionalProductReply ExchangeCfcCharts(string softwarePath, string action, string filePath, string modelVersion = "", long filter = 0, bool unattended = true, bool deleteAtTarget = false, bool dryRun = true, string chartNamesJson = "[]", bool skipChartPreflight = false)
            => _session.RunHmiStepTool("ExchangeCfcCharts", meta =>
            {
                try
                {
                var r = _session.Plan();
#if !CFC_INSTRUCTION_EXPORT
                if (action == "exportInstructionData") throw new NotSupportedException("ExportInstructionData requires V19 or later.");
#endif
                if (action != "import") RequireSafeChartInventory(meta, action);
                using var access = r.Writes ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, r.Writes);
                var provider = RequireChartProvider(plc);
                var file = action == "import" ? HmiFiles.RequireExistingInputFile(filePath, "filePath") : OptionalProductFiles.Plan(filePath);
                meta["software"] = plc.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["deleteAtTarget"] = deleteAtTarget; meta["unattended"] = unattended; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (action != "exportInstructionData") { meta["modelVersion"] = modelVersion; meta["filter"] = filter; }
                if (action == "selectiveExport") meta["chartNames"] = new HmiArray(r.ChartNames.Select(n => (object?)n).ToArray());
                // preflight (not for the complete export itself, which is the preflight, nor for import)
                bool needsPreflight = (action == "selectiveExport" || action == "exportInstructionData") && !skipChartPreflight;
                if (needsPreflight) RequireCfcCharts(provider, r.ChartNames, action == "exportInstructionData" ? "V2.0" : modelVersion, filter, meta, action);
                else if (skipChartPreflight && action != "export" && action != "import") meta["preflight"] = new HmiObject { ["skipped"] = true, ["warning"] = "Chart inventory not verified; on a PLC without CFC charts this call took TIA Portal V21 down (2.7.42 real project)." };
                if (dryRun) return "CFC " + action + " preview (ChartProvider " + (action == "import" ? "Import; deleteAtTarget removes charts absent from the file when enabled" : action == "exportInstructionData" ? "S7.ExportInstructionData" : action == "selectiveExport" ? "SelectiveExport of the named charts" : "CompleteExport of every chart, block type, task assignment and run sequence") + "); nothing changed.";
                meta["mayHaveChanged"] = action == "import"; meta["mayHaveWrittenFiles"] = action != "import";
                switch (action)
                {
                    case "export": meta["nativeSignature"] = "ChartProvider.CompleteExport(string, string, long, bool)"; provider.CompleteExport(file.FullName, modelVersion, filter, unattended); break;
                    case "selectiveExport": meta["nativeSignature"] = "ChartProvider.SelectiveExport(string, string[], string, long, bool)"; provider.SelectiveExport(file.FullName, r.ChartNames, modelVersion, filter, unattended); break;
#if CFC_INSTRUCTION_EXPORT
                    case "exportInstructionData": meta["nativeSignature"] = "ChartProviderS7.ExportInstructionData(string)"; provider.ExportInstructionData(file.FullName); break;
#else
                    case "exportInstructionData": throw new NotSupportedException("ExportInstructionData requires V19 or later.");
#endif
                    case "import": meta["nativeSignature"] = "ChartProvider.Import(string, string, long, bool, bool)"; provider.Import(file.FullName, modelVersion, filter, unattended, deleteAtTarget); return "CFC charts imported (native void return; the Inspector window carries the import log). No independent chart verification, no save / compile / download.";
                    default: throw new ArgumentException("Unknown CFC exchange action.");
                }
                meta["file"] = OptionalProductFiles.Verify(file);
                if (action == "export") OptionalProductEvidence.Safe(meta, "inventory", () => CfcInventoryRow(CfcInventory.InspectExport(file.FullName)));
                return "CFC " + action + " written and verified by size / SHA-256 (content semantics not asserted; password-protected charts are skipped natively). Project unchanged.";
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            });

        public OptionalProductReply ManageCfcChartProtection(string softwarePath, string chartName, string action = "read", string currentPassword = "", string newHashedPassword = "", bool dryRun = true, string modelVersion = "V2.0", bool skipChartPreflight = false)
            => _session.RunHmiStepTool("ManageCfcChartProtection", meta =>
            {
                try
                {
                var r = _session.Plan();
                if (action != "import") RequireSafeChartInventory(meta, action);
                using var access = r.Writes ? _session.AcquireHmiEditAccess() : null;
                var plc = _session.ExactPlcForEngineering(softwarePath, r.Writes);
                var provider = RequireChartProvider(plc);
                meta["software"] = plc.Name; meta["chartName"] = chartName; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!skipChartPreflight) RequireCfcCharts(provider, new[] { chartName }, string.IsNullOrEmpty(modelVersion) ? "V2.0" : modelVersion, 0, meta, "the chart password call");
                else meta["preflight"] = new HmiObject { ["skipped"] = true, ["warning"] = "Chart existence not verified; GetChartProtection of an unknown chart took TIA Portal V21 down (2.7.42 real project)." };
                OptionalProductEvidence.Safe(meta, "before", () => { var hash = provider.GetChartProtection(chartName); return new HmiObject { ["protected"] = !string.IsNullOrEmpty(hash), ["passwordHash"] = null }; });
                if (action == "read") return "CFC chart protection read (ChartProvider.GetChartProtection returns the password hash; empty = unprotected); nothing changed.";
                if (dryRun) return "CFC chart protection " + action + " preview; nothing changed (official: the password only guards against unintentional editing, it is no know-how protection).";
                meta["mayHaveChanged"] = true;
                bool accepted;
                switch (action)
                {
                    case "add": meta["nativeSignature"] = "ChartProvider.AddChartProtection(string, string)"; accepted = provider.AddChartProtection(chartName, newHashedPassword); break;
                    case "change": meta["nativeSignature"] = "ChartProvider.ChangeChartProtection(string, SecureString, string)"; using (var secure = EngineeringCredentialRules.ToSecureString(currentPassword)) accepted = provider.ChangeChartProtection(chartName, secure, newHashedPassword); break;
                    default: meta["nativeSignature"] = "ChartProvider.RemoveChartProtection(string, SecureString)"; using (var secure = EngineeringCredentialRules.ToSecureString(currentPassword)) accepted = provider.RemoveChartProtection(chartName, secure); break;
                }
                meta["nativeResult"] = accepted; if (!accepted) meta["operationSuccess"] = false;
                OptionalProductEvidence.Safe(meta, "after", () => { var hash = provider.GetChartProtection(chartName); return new HmiObject { ["protected"] = !string.IsNullOrEmpty(hash), ["passwordHash"] = null }; });
                return "CFC chart protection " + action + " returned " + accepted + " and was read back; no automatic save.";
                }
                catch (Exception ex) { OptionalProductEvidence.RecordFailure(meta, ex); throw; }
            });
}
#endif
