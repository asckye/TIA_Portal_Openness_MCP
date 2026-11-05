using TiaMcpServer.Siemens.Services;
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
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{

    [McpServerToolType]
    internal sealed class HmiExchangeTools
    {
        private readonly HmiExchangeService _service;

        public HmiExchangeTools(HmiExchangeService service) => _service = service;

        #region plc software - HmiExchange

        [McpServerTool(Name = "ListHmiScreens"),
                Description("[L2][HMI] List all screen names in an HMI (Classic or Unified), including all nested screen folders/groups. Returns screen " +
                "names, not folder paths; screen operations resolve these names recursively. Requires: ConnectPortal + OpenProject. softwarePath from " +
                "GetProjectTree. Use before EnsureUnifiedHmiScreen/ExportHmiScreen to confirm which screens exist.")]
        public CallToolResult GetHmiScreensV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
            => HmiExchangeContract.Run("ListHmiScreens", false, false, () => GetHmiScreens(softwarePath));

        public ResponseStringList GetHmiScreens(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = _service.GetHmiScreens(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI screens listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI screens for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ListHmiTagTables"), Description("[L2][HMI]List HMI tag table names (Classic/Unified, best-effort)")]
        public CallToolResult GetHmiTagTablesV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
            => HmiExchangeContract.Run("ListHmiTagTables", false, false, () => GetHmiTagTables(softwarePath));

        public ResponseStringList GetHmiTagTables(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = _service.GetHmiTagTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tag tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tag tables for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ListHmiTags"),
                Description("[L2][HMI]List HMI tag names (best-effort). If tagTableName empty, returns tags found at root collection if available.")]
        public CallToolResult GetHmiTagsV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: optional tag table name to list tags from")] string tagTableName = "")
            => HmiExchangeContract.Run("ListHmiTags", false, false, () => GetHmiTags(softwarePath, tagTableName));

        public ResponseStringList GetHmiTags(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: optional tag table name to list tags from")] string tagTableName = "")
        {
            try
            {
                var items = _service.GetHmiTags(softwarePath, tagTableName);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tags listed for '{softwarePath}' (table='{tagTableName}')",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tags for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ListHmiConnections"), Description("[L2][HMI]List HMI connection names (Classic/Unified, best-effort)")]
        public CallToolResult GetHmiConnectionsV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
            => HmiExchangeContract.Run("ListHmiConnections", false, false, () => GetHmiConnections(softwarePath));

        public ResponseStringList GetHmiConnections(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = _service.GetHmiConnections(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI connections listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI connections for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiScreen"),
                Description("[L2][HMI]Export one HMI screen to a file (Classic WinCC only; default exportOptions=WithDefaults) Current native policy; V4 " +
                "safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiScreenV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("screenName: the screen name to export")] string screenName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)")] string exportPath,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
            => HmiExchangeContract.Run("ExportHmiScreen", true, true, () => ExportHmiScreen(softwarePath, screenName, exportPath, exportOptions));

        public ResponseExportFile ExportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("screenName: the screen name to export")] string screenName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)")] string exportPath,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
        {
            try
            {
                var export = _service.ExportHmiScreen(softwarePath, screenName, exportPath, exportOptions);
                return new ResponseExportFile
                {
                    Message = export["success"]!.GetValue<bool>() ? "Native export completed" : "Native export failed or unsupported",
                    ExportPath = export["path"]?.ToString() ?? exportPath,
                    Meta = export
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI screen '{screenName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode
                        .InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiTagTable"),
                Description("[L2][HMI]Export one HMI tag table to a file (Classic WinCC only; default exportOptions=WithDefaults) Current native policy; V4 " +
                "safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiTagTableV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: the tag table name to export")] string tagTableName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\tagtable.xml)")] string exportPath,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
            => HmiExchangeContract.Run("ExportHmiTagTable", true, true, () => ExportHmiTagTable(softwarePath, tagTableName, exportPath, exportOptions));

        public ResponseExportFile ExportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: the tag table name to export")] string tagTableName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\tagtable.xml)")] string exportPath,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
        {
            try
            {
                var export = _service.ExportHmiTagTable(softwarePath, tagTableName, exportPath, exportOptions);
                return new ResponseExportFile
                {
                    Message = export["success"]!.GetValue<bool>() ? "Native export completed" : "Native export failed or unsupported",
                    ExportPath = export["path"]?.ToString() ?? exportPath,
                    Meta = export
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI tag table '{tagTableName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode
                        .InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiConnection"),
                Description("[L2][HMI]Export one HMI connection to a file (Classic WinCC only; Unified refuses with UNSUPPORTED_CAPABILITY) Current native " +
                "policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiConnectionV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("connectionName: the HMI connection name to export")] string connectionName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\connection.xml)")] string exportPath,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
            => HmiExchangeContract.Run("ExportHmiConnection", true, true, () => ExportHmiConnection(softwarePath, connectionName, exportPath, exportOptions));

        public ResponseExportFile ExportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("connectionName: the HMI connection name to export")] string connectionName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\connection.xml)")] string exportPath,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
        {
            try
            {
                var export = _service.ExportHmiConnection(softwarePath, connectionName, exportPath, exportOptions);
                return new ResponseExportFile
                {
                    Message = export["success"]!.GetValue<bool>() ? "Native export completed" : "Native export failed or unsupported",
                    ExportPath = export["path"]?.ToString() ?? exportPath,
                    Meta = export
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI connection '{connectionName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex,
                        McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI connection '{connectionName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiProgram"),
                Description("[L2][HMI]Batch export classic WinCC screens/tagtables into a directory; default exportOptions=WithDefaults Current native " +
                "policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiProgramV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("exportDir: directory to write exported files into")] string exportDir,
            [Description("exportScreens: default true")] bool exportScreens = true,
            [Description("exportTagTables: default true")] bool exportTagTables = true,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
            => HmiExchangeContract
                .Run("ExportHmiProgram", true, true, () => ExportHmiProgram(softwarePath, exportDir, exportScreens, exportTagTables, exportOptions));

        public ResponseBatchExport ExportHmiProgram(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("exportDir: directory to write exported files into")] string exportDir,
            [Description("exportScreens: default true")] bool exportScreens = true,
            [Description("exportTagTables: default true")] bool exportTagTables = true,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
        {
            try
            {
                var res = _service.ExportHmiProgram(softwarePath, exportDir, exportScreens, exportTagTables, exportOptions);
                if (res != null)
                {
                    return new ResponseBatchExport
                    {
                        Message = $"HMI program export finished; see Failed list. Destination: '{exportDir}'",
                        Exported = res.Value.Exported,
                        Failed = res.Value.Failed,
                        Meta = ResponseMeta
                            .Basic(DateTime.Now, res.Value.Failed.Count == 0, ("operationSuccess", res.Value.Failed.Count == 0), ("items", res.Value.Items))
                    };
                }
                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI program: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreen"),
                Description("[L2][HMI]Import one HMI screen XML file into an HMI program (Classic WinCC only; Unified refuses with UNSUPPORTED_CAPABILITY) " +
                "Current native policy; V4 safety behavior is not yet accepted." +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; execution requires dryRun=false, expectedPlanHash " +
                "from this preview, and the exact bound expectedProjectFile. Existing approval and native checks remain.")]
        public CallToolResult ImportHmiScreenV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("importPath: full file path of exported screen XML")] string importPath,
            [Description("overwrite: default false; existing targets refuse before native Import. Set true explicitly to replace.")] bool overwrite = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when " +
                "dryRun=false.")] string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("ImportHmiScreen", dryRun, new { softwarePath, folderPath, importPath, overwrite }, () => HmiExchangeContract
                    .Run("ImportHmiScreen", true, true, () => ImportHmiScreen(softwarePath, folderPath, importPath, overwrite)), expectedPlanHash,
                    expectedProjectFile);

        public ResponseMessage ImportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("importPath: full file path of exported screen XML")] string importPath,
            [Description("overwrite: default false; existing targets refuse before native Import. Set true explicitly to replace.")] bool overwrite = false)
        {
            try
            {
                _service.ImportHmiScreen(softwarePath, folderPath, importPath, overwrite);
                return new ResponseMessage
                {
                    Message = $"HMI screen imported from '{importPath}'" + (_service.LastImportNotes != null ? " - " + _service.LastImportNotes : ""),
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI screen from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screen: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTable"),
                Description("[L2][HMI]Import one HMI tag table XML file into an HMI program (Classic WinCC only; Unified refuses with " +
                "UNSUPPORTED_CAPABILITY) Current native policy; V4 safety behavior is not yet accepted." +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; execution requires dryRun=false, expectedPlanHash " +
                "from this preview, and the exact bound expectedProjectFile. Existing approval and native checks remain.")]
        public CallToolResult ImportHmiTagTableV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("importPath: full file path of exported tag table XML")] string importPath,
            [Description("overwrite: default false; existing targets refuse before native Import. Set true explicitly to replace.")] bool overwrite = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when " +
                "dryRun=false.")] string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("ImportHmiTagTable", dryRun, new { softwarePath, folderPath, importPath, overwrite }, () => HmiExchangeContract
                    .Run("ImportHmiTagTable", true, true, () => ImportHmiTagTable(softwarePath, folderPath, importPath, overwrite)), expectedPlanHash,
                    expectedProjectFile);

        public ResponseMessage ImportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("importPath: full file path of exported tag table XML")] string importPath,
            [Description("overwrite: default false; existing targets refuse before native Import. Set true explicitly to replace.")] bool overwrite = false)
        {
            try
            {
                _service.ImportHmiTagTable(softwarePath, folderPath, importPath, overwrite);
                return new ResponseMessage
                {
                    Message = $"HMI tag table imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiConnection"),
                Description("[L2][HMI]Import one HMI connection XML file into an HMI program (Classic WinCC only; Unified refuses with " +
                "UNSUPPORTED_CAPABILITY) Current native policy; V4 safety behavior is not yet accepted." +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; execution requires dryRun=false, expectedPlanHash " +
                "from this preview, and the exact bound expectedProjectFile. Existing approval and native checks remain.")]
        public CallToolResult ImportHmiConnectionV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("importPath: full file path of exported HMI connection XML")] string importPath,
            [Description("overwrite: default false; existing targets refuse before native Import. Set true explicitly to replace.")] bool overwrite = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when " +
                "dryRun=false.")] string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("ImportHmiConnection", dryRun, new { softwarePath, importPath, overwrite }, () => HmiExchangeContract
                    .Run("ImportHmiConnection", true, true, () => ImportHmiConnection(softwarePath, importPath, overwrite)), expectedPlanHash,
                    expectedProjectFile);

        public ResponseMessage ImportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("importPath: full file path of exported HMI connection XML")] string importPath,
            [Description("overwrite: default false; existing targets refuse before native Import. Set true explicitly to replace.")] bool overwrite = false)
        {
            try
            {
                _service.ImportHmiConnection(softwarePath, importPath, overwrite);
                return new ResponseMessage
                {
                    Message = $"HMI connection imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI connection from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI connection: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreensFromDirectory"),
                Description("[L2][HMI]Batch import classic WinCC screen .xml files; overwrite defaults false, unresolved folders refuse Current native " +
                "policy; V4 safety behavior is not yet accepted." +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; execution requires dryRun=false, expectedPlanHash " +
                "from this preview, and the exact bound expectedProjectFile. Existing approval and native checks remain.")]
        public CallToolResult ImportHmiScreensFromDirectoryV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("dir: directory containing exported screen XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: default false (native import option None); true explicitly selects Override")] bool overwrite = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when " +
                "dryRun=false.")] string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("ImportHmiScreensFromDirectory", dryRun, new { softwarePath, folderPath, dir, regexName, overwrite }, () => HmiExchangeContract
                .Run("ImportHmiScreensFromDirectory", true, true, () => ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite)),
                    expectedPlanHash, expectedProjectFile);

        public ResponseImportBatch ImportHmiScreensFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("dir: directory containing exported screen XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: default false (native import option None); true explicitly selects Override")] bool overwrite = false)
        {
            try
            {
                var result = _service.ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI screens from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = result.Meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screens from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTablesFromDirectory"),
                Description("[L2][HMI]Batch import classic WinCC tag table .xml files; overwrite defaults false, unresolved folders refuse Current native " +
                "policy; V4 safety behavior is not yet accepted." +
            " Default dryRun=true returns a planHash for the normalized arguments and target identity; execution requires dryRun=false, expectedPlanHash " +
                "from this preview, and the exact bound expectedProjectFile. Existing approval and native checks remain.")]
        public CallToolResult ImportHmiTagTablesFromDirectoryV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("dir: directory containing exported tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: default false (native import option None); true explicitly selects Override")] bool overwrite = false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun = true,
            [Description("expectedPlanHash: planHash returned by this tool's dry-run preview; required when dryRun=false.")] string expectedPlanHash = "",
            [Description("expectedProjectFile: exact project file path returned by this tool's dry-run preview; required when " +
                "dryRun=false.")] string expectedProjectFile =
                "")
            => McpServer
                .RunWritePreview("ImportHmiTagTablesFromDirectory", dryRun, new { softwarePath, folderPath, dir, regexName, overwrite }, () =>
                    HmiExchangeContract
                .Run("ImportHmiTagTablesFromDirectory", true, true, () => ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite)),
                    expectedPlanHash, expectedProjectFile);

        public ResponseImportBatch ImportHmiTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: absolute /Group/Sub with URI-escaped segments; / selects the root (empty retains the legacy root selection). Relative " +
                "and backslash paths are refused.")] string folderPath,
            [Description("dir: directory containing exported tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: default false (native import option None); true explicitly selects Override")] bool overwrite = false)
        {
            try
            {
                var result = _service.ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = result.Meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag tables from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        #endregion
    }
}
