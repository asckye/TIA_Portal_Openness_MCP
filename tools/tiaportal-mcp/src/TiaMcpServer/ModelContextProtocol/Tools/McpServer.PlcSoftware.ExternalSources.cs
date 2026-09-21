using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: plc software. Family file split out of McpServer.PlcSoftware.cs (2.8.0); behavior unchanged.
    public static partial class McpServer
    {
        #region plc software - ExternalSources

        [McpServerTool(Name = "GetCrossReferences"), Description("[L2][PLC-Software] Get cross references for a Step7 block/type (best-effort; CrossReferenceService.GetCrossReferences with the filter). Requires applicable object and Openness support. KNOWN RISK: on the maintainer's real project (TIA Portal V21, 2026-09-21) this query took the whole TIA Portal process down right after five blocks had been re-imported with Override without a compile (the query answered from the stale index, then TIA exited). Since 2.9.1 the query is REFUSED while any block of the PLC is uncompiled (IsConsistent=false) and the refusal names them: run CompileSoftware first. Save the project before querying anyway; the delete tools only call it with crossReferences=true.")]
        public static ResponseCrossReferences GetCrossReferences(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("objectPath: blockPath or typePath inside the PLC software")] string objectPath,
            [Description("objectKind: Block or Type")] string objectKind = "Block",
            [Description("filter: CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects)")] string filter = "AllObjects")
        {
            try
            {
                var items = Portal.GetCrossReferences(softwarePath, objectPath, objectKind, filter, out var reason);
                if (items != null)
                {
                    return new ResponseCrossReferences
                    {
                        Message = $"Cross references retrieved for {objectKind} '{objectPath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Cross references unavailable for {objectKind} '{objectPath}': {reason ?? "unknown reason"}", McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving cross references: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcExternalSources"), Description("[L2][PLC-Software]List PLC external source names (best-effort)")]
        public static ResponseStringList GetPlcExternalSources(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcExternalSources(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC external sources listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{Portal.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC external sources: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WritePlcSclSourceFile"), Description("[L1][PLC-Software][OFFLINE] Write SCL source text to a local .scl external-source file (UTF-8 WITH BOM, so Chinese comments are not imported as mojibake/乱码). This tool does NOT connect to TIA Portal and does NOT import anything — it only writes the file to disk and returns the path plus manual-import instructions. Use it as the robust fallback when XML block import is rejected (e.g. a TIA V20 portal rejecting V21 SimaticML tokens: 'Cannot create SW.Blocks.CompileUnit... token not supported'): the user imports the .scl manually in TIA via project tree → 'External source files' → 'Add new external file', then right-clicks the source → 'Generate blocks from source'. The sclContent must be a complete source, e.g. FUNCTION_BLOCK \"Name\" ... END_FUNCTION_BLOCK.")]
        public static ResponseMessage WritePlcSclSourceFile(
            [Description("sclContent: the full SCL source text (complete FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / TYPE declarations). This is written verbatim.")] string sclContent,
            [Description("outputPath: target .scl file path. If a directory is given (or the path has no extension), the file is named after the first block found in the source. Empty means a temp file under %TEMP%\\tia_mcp_scl.")] string outputPath = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sclContent))
                    throw new McpException("sclContent is empty — provide the full SCL source text to write.", McpErrorCode.InvalidParams);

                // Derive a default file name from the first block declaration in the source.
                var nameMatch = Regex.Match(sclContent,
                    "(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\\s+\"?([A-Za-z_][A-Za-z0-9_]*)\"?",
                    RegexOptions.IgnoreCase);
                var defaultName = MakeSafeFileName(nameMatch.Success ? nameMatch.Groups[1].Value : "MCP_Source");

                string finalPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var dir = Path.Combine(Path.GetTempPath(), "tia_mcp_scl");
                    finalPath = Path.Combine(dir, defaultName + ".scl");
                }
                else if (Directory.Exists(outputPath) ||
                         outputPath.EndsWith("\\", StringComparison.Ordinal) ||
                         outputPath.EndsWith("/", StringComparison.Ordinal))
                {
                    finalPath = Path.Combine(outputPath, defaultName + ".scl");
                }
                else
                {
                    finalPath = string.IsNullOrEmpty(Path.GetExtension(outputPath))
                        ? outputPath + ".scl"
                        : outputPath;
                }

                var parent = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                // UTF-8 WITH BOM: TIA reads BOM-less UTF-8 SCL with Chinese comments as mojibake.
                File.WriteAllText(finalPath, sclContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                return new ResponseMessage
                {
                    Message =
                        $"SCL source written to '{finalPath}'. To import in TIA Portal: project tree → " +
                        "'External source files' → 'Add new external file' → select this .scl → " +
                        "right-click the source → 'Generate blocks from source'. " +
                        "(Or call ImportPlcExternalSource then GenerateBlocksFromExternalSource if connected.)",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["path"] = finalPath,
                        ["blockName"] = nameMatch.Success ? nameMatch.Groups[1].Value : null,
                        ["bytes"] = new System.IO.FileInfo(finalPath).Length
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing SCL source file: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcExternalSource"), Description("[L2][PLC-Software]Import one PLC external source file into a group (best-effort)")]
        public static ResponseMessage ImportPlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: external source group path (use empty for root)")] string groupPath,
            [Description("filePath: path to external source file (.scl, etc.)")] string filePath)
        {
            try
            {
                Portal.ImportPlcExternalSource(softwarePath, groupPath, filePath);
                return new ResponseMessage
                {
                    Message = "PLC external source imported",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC external source [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcExternalSource"), Description("[L2][PLC-Software]Delete a PLC external source by name so ImportPlcExternalSource can replace it (idempotent). Name may include or omit .scl.")]
        public static ResponseMessage DeletePlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl)")] string externalSourceName)
        {
            try
            {
                Portal.DeletePlcExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"PLC external source '{externalSourceName}' deleted or was not present",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed deleting PLC external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error deleting PLC external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GenerateBlocksFromExternalSource"), Description("[L2][PLC-Software]Generate blocks from a PLC external source by name (best-effort)")]
        public static ResponseMessage GenerateBlocksFromExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources")] string externalSourceName)
        {
            try
            {
                Portal.GenerateBlocksFromExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"Blocks generated from external source '{externalSourceName}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed generating blocks from external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating blocks from external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
