using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed partial class DocumentsTools
    {
        private readonly DocumentsService _domain;

        public DocumentsTools(DocumentsService domain)
        {
            _domain = domain;
        }

        public ResponseExportAsDocuments ExportAsDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                if (TiaMcp.Versioning.TiaVersionCatalog.Get(HardwareContract.ReleaseKey).MajorVersion < 20)
                {
                    throw new McpException("ExportPlcBlockDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                if (_domain.ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath))
                {
                    return new ResponseExportAsDocuments
                    {
                        Message = $"Documents exported from '{blockPath}' to '{exportPath}'",
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting documents from '{blockPath}' to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting documents from '{blockPath}' to '{exportPath}': {ex}", ex, McpErrorCode.InternalError);
            }
        }

        // .s7res is YAML; S7ResScanner reads its culture entries.
        private static List<string> GetResMissingEnUsIds(string directory, string baseName)
            => S7ResScanner.GetMissingEnUsIds(directory, baseName);

        [McpServerTool(Name = "ExportPlcBlockDocuments"),
                Description("[L2][PLC-Software] PREFERRED on V21+ for exporting one block. Exports a single program block to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML (ExportPlcBlock). Requires TIA Portal V20 or newer."
                    + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance."
                    + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExportAsDocumentsV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            return PlcExchangeContract
                .Run("ExportPlcBlockDocuments", () => ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath), write: true, current: true);
        }

        [McpServerTool(Name = "ExportPlcBlocksDocuments"),
                Description("[L2][PLC-Software] PREFERRED on V21+ for batch export. Exports multiple program blocks to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML. Requires TIA Portal V20 or newer."
                    + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance."
                    + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public async Task<CallToolResult> ExportBlocksAsDocumentsV4(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            return await PlcExchangeContract
                .RunAsync("ExportPlcBlocksDocuments", () => ExportBlocksAsDocuments(server, context, softwarePath, exportPath, regexName, preservePath), write
                    : true, current: true);
        }

        [McpServerTool(Name = "ImportPlcBlockDocuments"),
                Description("[L2][PLC-Software] PREFERRED on V21+ for importing one block. Imports a single program block from SIMATIC SD textual / SCL " +
                "documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. After import it checks exact native imported names in the " +
                "target group (data.existsVerified); contentVerified remains unknown. Use InspectSimaticSdCompatibility before import and compare exported " +
                "documents for content verification."
                    + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance."
                    + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportFromDocumentsV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the block should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("fileNameWithoutExtension: name of the block file without extension") ] string fileNameWithoutExtension,
            [Description("importOption: string value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            return PlcExchangeContract
                .Run("ImportPlcBlockDocuments", () => ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, importOption), write
                    : true, current: true);
        }

        [McpServerTool(Name = "ImportPlcBlocksDocuments"),
                Description("[L2][PLC-Software] PREFERRED on V21+ for batch import. Imports multiple program blocks from SIMATIC SD textual / SCL documents " +
                "(.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. Stops after the first native failure or unknown result; partial " +
                "project changes are possible and must not be retried automatically."
                    + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance."
                    + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public async Task<CallToolResult> ImportBlocksFromDocumentsV4(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the blocks should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("regexName: name or regular expression to select block files (empty for all)")] string regexName = "",
            [Description("importOption: string value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            return await PlcExchangeContract.RunAsync("ImportPlcBlocksDocuments", () => ImportBlocksFromDocuments(server, context, softwarePath, groupPath, importPath, regexName, importOption), write: true, current: true);
        }

    }
}
