using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Shared;
namespace TiaMcpServer.ModelContextProtocol
{
    // Optional-package services retain the native calls and record typed failure evidence.
    [McpServerToolType]
    internal sealed class CfcTools
    {
        private readonly CfcPortService _cfc;

        public CfcTools(CfcPortService cfc) => _cfc = cfc;

        [McpServerTool(Name="ExchangeCfcCharts"), Description("[L2][PLC-Software][WRITE] Typed CFC chart exchange of one PLC software (ChartProviderS7): export (ChartProvider.CompleteExport(path, modelVersion e.g. V2.0, filter, unattended) - every chart with block types, task assignment and run sequence, written as XML packed in a ZIP such as Chart1.xml.zip), selectiveExport (SelectiveExport(path, chartNames [..], modelVersion, filter, unattended) - the named charts only), import (Import(path, modelVersion, filter, unattended, deleteAtTarget) - the ZIP from a complete or selective export; deleteAtTarget removes charts missing from the file), exportInstructionData (ChartProviderS7.ExportInstructionData(path)). Official requirements: PLC offline (import requires the confirmed Offline state), password-protected charts are skipped by the exports, the imported block types must exist and be compiled. Output files are new and verified by size / SHA-256; export additionally reports the chart inventory parsed from the ZIP. V18 and later export, selectiveExport and exportInstructionData are refused with UNSUPPORTED_CAPABILITY before provider access: the SDK exposes no independent chart inventory, CompleteExport preflight preceded a V20 crash on a chartless PLC, and ExportInstructionData crashed V21. skipChartPreflight cannot bypass the crash guard. ExportInstructionData has no API on V18 (requires V19 or later). Imports remain available on V18 and later. Default preview; no save / compile / download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangeCfcCharts(
            string softwarePath,
            [Description("action: the operation to perform - export | selectiveExport | import | exportInstructionData.")] string action,
            [Description("filePath: absolute ZIP containing the native chart XML (e.g. Charts.xml.zip); exportInstructionData writes a plain file. New output or existing import file.")] string filePath,
            [Description("modelVersion: CFC data-model version string, e.g. 'V2.0'.")] string modelVersion="",
            [Description("filter: nonnegative Int64 automation-interface filter; 0 = none.")] long filter=0,
            [Description("unattended: true answers CFC prompts automatically.")] bool unattended=true,
            [Description("deleteAtTarget: true deletes charts at the target that the import does not contain.")] bool deleteAtTarget=false,
            bool dryRun=true,
            [Description("chartNames: Array of chart paths.")] string[] chartNames = null!,
            [Description("skipChartPreflight: retained for compatibility; cannot bypass the V18-and-later crash guard.")] bool skipChartPreflight=false)
            => OptionalPackageContract.Run("ExchangeCfcCharts", !dryRun, () => _cfc.ExchangeCfcCharts(softwarePath,action,filePath,modelVersion,filter,unattended,deleteAtTarget,dryRun,V4Json.Serialize(chartNames ?? Array.Empty<string>()),skipChartPreflight));
        [McpServerTool(Name="ManageCfcChartProtection"), Description("[L2][PLC-Software][WRITE] CFC chart password of one chart (ChartProviderS7 on the PLC software): read (GetChartProtection -> password hash, empty = unprotected), add (AddChartProtection(chartName, newHashedPassword - the hash as TIA displays it)), change (ChangeChartProtection(chartName, currentPassword as SecureString, newHashedPassword)), remove (RemoveChartProtection(chartName, currentPassword)). Official: the password only protects against unintentional editing (no know-how protection); native bool reported and the hash read back. Passwords are never logged. V18 and later refuse every protection action with UNSUPPORTED_CAPABILITY before provider access: the SDK exposes no independent chart inventory, CompleteExport preflight preceded a V20 crash on a chartless PLC, and unknown-chart GetChartProtection crashed V21. skipChartPreflight cannot bypass the crash guard. Default preview; no automatic save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageCfcChartProtection(
            string softwarePath,
            [Description("chartName: chart path 'Root/Sub' (exact names).")] string chartName,
            [Description("action: the operation to perform - read | add | change | remove.")] string action="read",
            [Description("currentPassword: the password currently set; never logged.")] string currentPassword="",
            [Description("newHashedPassword: the new password value as the CFC API expects it; never logged.")] string newHashedPassword="",
            bool dryRun=true,
            [Description("modelVersion: CFC data-model version string, e.g. 'V2.0'.")] string modelVersion="V2.0",
            [Description("skipChartPreflight: retained for compatibility; cannot bypass the V18-and-later crash guard.")] bool skipChartPreflight=false)
            => OptionalPackageContract.Run("ManageCfcChartProtection", !dryRun && action != "read", () => _cfc.ManageCfcChartProtection(softwarePath,chartName,action,currentPassword,newHashedPassword,dryRun,modelVersion,skipChartPreflight));
    }
}
