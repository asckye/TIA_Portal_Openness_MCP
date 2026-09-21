using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    // Phase 6 ⑥-③ (2.7.42): typed CFC option package (PlcSoftware.GetService<ChartProviderS7>; identical on V20 / V21).
    public static partial class McpServer
    {
        [McpServerTool(Name="ExchangeCfcCharts"), Description("[L2][PLC-Software][WRITE] Typed CFC chart exchange of one PLC software (ChartProviderS7): export (ChartProvider.CompleteExport(path, modelVersion e.g. V2.0, filter, unattended) - every chart with block types, task assignment and run sequence, written as XML packed in a ZIP such as Chart1.xml.zip), selectiveExport (SelectiveExport(path, chartNamesJson [..], modelVersion, filter, unattended) - the named charts only), import (Import(path, modelVersion, filter, unattended, deleteAtTarget) - the ZIP from a complete or selective export; deleteAtTarget removes charts missing from the file), exportInstructionData (ChartProviderS7.ExportInstructionData(path)). Official requirements: PLC offline (import requires the confirmed Offline state), password-protected charts are skipped by the exports, the imported block types must exist and be compiled. Output files are new and verified by size / SHA-256; export additionally reports the chart inventory parsed from the ZIP. selectiveExport and exportInstructionData first run a CompleteExport preflight into a temporary ZIP and are refused when the PLC has no charts or a named chart is missing (2.7.42 real project, CPU 1510SP F with CFC installed but no chart folder: ExportInstructionData took TIA Portal V21 down; skipChartPreflight=true bypasses the check at that risk). Default preview; no save / compile / download.")]
        public static ResponseMessage ExchangeCfcCharts(
            string softwarePath,
            [Description("action: the operation to perform - export | selectiveExport | import | exportInstructionData.")] string action,
            string filePath,
            [Description("modelVersion: CFC data-model version string, e.g. 'V2.0'.")] string modelVersion="",
            [Description("filter: filter text ('' = all).")] long filter=0,
            [Description("unattended: true answers CFC prompts automatically.")] bool unattended=true,
            [Description("deleteAtTarget: true deletes charts at the target that the import does not contain.")] bool deleteAtTarget=false,
            bool dryRun=true,
            [Description("chartNamesJson: JSON array of chart paths.")] string chartNamesJson="[]",
            [Description("skipChartPreflight: true skips the chart-folder preflight (only when the PLC is known to have charts).")] bool skipChartPreflight=false)
            => Portal.ExchangeCfcCharts(softwarePath,action,filePath,modelVersion,filter,unattended,deleteAtTarget,dryRun,chartNamesJson,skipChartPreflight);
        [McpServerTool(Name="ManageCfcChartProtection"), Description("[L2][PLC-Software][WRITE] CFC chart password of one chart (ChartProviderS7 on the PLC software): read (GetChartProtection -> password hash, empty = unprotected), add (AddChartProtection(chartName, newHashedPassword - the hash as TIA displays it)), change (ChangeChartProtection(chartName, currentPassword as SecureString, newHashedPassword)), remove (RemoveChartProtection(chartName, currentPassword)). Official: the password only protects against unintentional editing (no know-how protection); native bool reported and the hash read back. Passwords are never logged. Every action first verifies the chart through a CompleteExport preflight (modelVersion, default V2.0) and is refused when the PLC has no charts or the name is unknown - GetChartProtection of an unknown chart took TIA Portal V21 down on the 2.7.42 real project (skipChartPreflight=true bypasses the check at that risk). Default preview; no automatic save.")]
        public static ResponseMessage ManageCfcChartProtection(
            string softwarePath,
            [Description("chartName: chart path 'Root/Sub' (exact names).")] string chartName,
            [Description("action: the operation to perform - read | add | change | remove.")] string action="read",
            [Description("currentPassword: the password currently set; never logged.")] string currentPassword="",
            [Description("newHashedPassword: the new password value as the CFC API expects it; never logged.")] string newHashedPassword="",
            bool dryRun=true,
            [Description("modelVersion: CFC data-model version string, e.g. 'V2.0'.")] string modelVersion="V2.0",
            [Description("skipChartPreflight: true skips the chart-folder preflight (only when the PLC is known to have charts).")] bool skipChartPreflight=false)
            => Portal.ManageCfcChartProtection(softwarePath,chartName,action,currentPassword,newHashedPassword,dryRun,modelVersion,skipChartPreflight);
    }
}
