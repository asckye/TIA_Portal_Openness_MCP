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
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;
using static TiaMcpServer.ModelContextProtocol.McpServer.PlcSourceToolSupport;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class DocumentsTools
    {
        private readonly IEngineeringSession _session;
        private readonly DocumentsService _domain;

        public DocumentsTools(DocumentsService domain, IEngineeringSession session)
        {
            _domain = domain;
            _session = session;
        }

        [McpServerTool(Name = "ExportAsDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for exporting one block. Exports a single program block to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML (ExportBlock). Requires TIA Portal V20 or newer.")]
        public ResponseExportAsDocuments ExportAsDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ExportAsDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                if (WithAutoOffline(() => _domain.ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath)))
                {
                    return new ResponseExportAsDocuments
                    {
                        Message = $"Documents exported from '{blockPath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
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

        [McpServerTool(Name = "ExportBlocksAsDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for batch export. Exports multiple program blocks to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML. Requires TIA Portal V20 or newer.")]
        public async Task<ResponseExportBlocksAsDocuments> ExportBlocksAsDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context?.Params?.ProgressToken;

            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ExportBlocksAsDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                // First, get the list of blocks to determine total count
                Logger?.LogInformation($"Starting export of blocks as documents from '{softwarePath}' to '{exportPath}'");

                var allBlocks = await Task.Run(() => _session.GetBlocks(softwarePath, regexName));
                if (allBlocks == null)
                    throw new McpException("No project is open; document export did not run.", McpErrorCode.InvalidParams);
                var totalBlocks = allBlocks?.Count ?? 0;

                if (totalBlocks == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No blocks found to export as documents",
                            progressToken
                        });
                    }

                    return new ResponseExportBlocksAsDocuments
                    {
                        Message = $"No blocks found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseBlockInfo>(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = 0,
                            ["exportedBlocks"] = 0,
                            ["duration"] = (DateTime.Now - startTime).TotalSeconds
                        }
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalBlocks,
                        Message = $"Starting export of {totalBlocks} blocks as documents...",
                        progressToken
                    });
                }

                // Export blocks as documents asynchronously
                var exportedBlocks = await Task.Run(() => _domain.ExportBlocksAsDocuments(softwarePath, exportPath, regexName, preservePath));

                // Send progress update after export completion
                if (exportedBlocks != null && progressToken != null)
                {
                    var exportedCount = exportedBlocks.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalBlocks,
                        Message = $"Exported {exportedCount} of {totalBlocks} blocks as documents",
                        progressToken
                    });
                }

                if (exportedBlocks != null)
                {
                    var responseList = new List<ResponseBlockInfo>();
                    var processedCount = 0;

                    foreach (var block in exportedBlocks)
                    {
                        if (block != null)
                        {
                            var attributes = Helper.GetAttributeList(block);

                            responseList.Add(new ResponseBlockInfo
                            {
                                Name = block.Name,
                                TypeName = block.GetType().Name,
                                Namespace = block.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                                IsConsistent = block.IsConsistent,
                                HeaderName = block.HeaderName,
                                ModifiedDate = block.ModifiedDate,
                                IsKnowHowProtected = block.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = block.ToString()
                            });
                        }
                        processedCount++;
                    }

                    // Send final progress notification
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processedCount,
                            Total = totalBlocks,
                            Message = $"Document export completed: {processedCount} blocks exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Document export completed: {processedCount} blocks exported in {duration:F2} seconds");

                    // Surface per-block skip/failure reasons so a "matched N but exported 0" is never silent.
                    var failures = _domain.LastExportAsDocumentsFailures;
                    var skipped = totalBlocks - processedCount;
                    var msg = $"Document export completed: {processedCount}/{totalBlocks} blocks (regex '{regexName}') from '{softwarePath}' to '{exportPath}'";
                    if (skipped > 0)
                    {
                        var reason = (failures != null && failures.Count > 0)
                            ? string.Join("; ", failures)
                            : "no reason captured (inconsistent block? compile first, or the block type/language may not support SIMATIC SD export — try ExportBlock for XML)";
                        msg += $". {skipped} not exported: {reason}";
                    }

                    var meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = processedCount > 0 || totalBlocks == 0,
                        ["totalBlocks"] = totalBlocks,
                        ["exportedBlocks"] = processedCount,
                        ["skippedBlocks"] = skipped,
                        ["duration"] = duration
                    };
                    if (failures != null && failures.Count > 0)
                    {
                        var arr = new JsonArray();
                        foreach (var f in failures) arr.Add(f);
                        meta["failures"] = arr;
                    }

                    return new ResponseExportBlocksAsDocuments
                    {
                        Message = msg,
                        Items = responseList,
                        Meta = meta
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting documents to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Send error progress notification if we have a progress token
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Document export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    { /* swallow(teardown): Progress notification failure must not replace the original export or import error. */
                        // Ignore notification errors during error handling
                    }
                }

                Logger?.LogError(ex, $"Failed exporting documents to '{exportPath}'");
                throw new McpException($"Unexpected error exporting documents to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportFromDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for importing one block. Imports a single program block from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. After import it checks exact native imported names in the target group (Meta.existsVerified / legacy verified); contentVerified remains unknown. Use InspectSimaticSdCompatibility before import and compare exported documents for content verification.")]
        public ResponseImportFromDocuments ImportFromDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the block should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("fileNameWithoutExtension: name of the block file without extension") ] string fileNameWithoutExtension,
            [Description("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ImportFromDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                var option = ParseImportDocumentOption(importOption);

                // Pre-check .s7res for missing en-US tags
                var warnings = new JsonArray();
                try
                {
                    var missingIds = GetResMissingEnUsIds(importPath, fileNameWithoutExtension);
                    if (missingIds != null && missingIds.Count > 0)
                    {
                        Logger?.LogWarning($".s7res for '{fileNameWithoutExtension}' missing en-US tags for {missingIds.Count} items: {string.Join(", ", missingIds)}");
                        warnings.Add(new JsonObject
                        {
                            ["name"] = fileNameWithoutExtension,
                            ["missingEnUsIds"] = new JsonArray(missingIds.Select(id => (JsonNode)id).ToArray())
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Never silently swallow: a pre-check that cannot run must say so,
                    // otherwise "no warnings" is indistinguishable from "check crashed".
                    Logger?.LogWarning(ex, "Failed to evaluate .s7res warnings");
                    warnings.Add(new JsonObject
                    {
                        ["name"] = fileNameWithoutExtension,
                        ["precheckError"] = ex.Message
                    });
                }

                if (EngineeringGroupOperations.Parts(fileNameWithoutExtension).Length != 1 || fileNameWithoutExtension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("fileNameWithoutExtension must be one safe document basename.");
                var formatPreflight = EngineeringAuditLogic.DocumentPreflight(File.ReadAllText(Path.Combine(importPath, fileNameWithoutExtension + ".s7dcl")), Engineering.TiaMajorVersion, null);
                var ok = WithAutoOffline(() => _domain.ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, option));
                if (ok)
                {
                    // Read-back verification: confirm the block is actually present after import.
                    // Wrapped so a verification hiccup never masks a successful import.
                    bool verified = false;
                    string verifyDetail;
                    try
                    {
                        verified = _domain.VerifyLastDocumentImport(softwarePath, groupPath);
                        verifyDetail = verified ? "Native imported names found in the exact target group; content completeness has not been verified."
                            : "Exact target readback failed; imported content has not been verified.";
                    }
                    catch (Exception vex) { verifyDetail = "readback skipped: " + vex.Message; }

                    return new ResponseImportFromDocuments
                    {
                        Message = $"Imported '{fileNameWithoutExtension}' from '{importPath}'" + (verified ? " (verified)" : ""),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["verified"] = verified,
                            ["existsVerified"] = verified,
                            ["contentVerified"] = null,
                            ["verificationScope"] = "exact target group and native ImportedPlcBlocks names",
                            ["formatPreflight"] = formatPreflight,
                            ["verifyDetail"] = verifyDetail,
                            ["warnings"] = warnings
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed importing '{fileNameWithoutExtension}' from '{importPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing from documents: {ex.Message}. If import was attempted, the project may have changed; do not retry automatically.", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportBlocksFromDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for batch import. Imports multiple program blocks from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. Stops after the first native failure or unknown result; partial project changes are possible and must not be retried automatically.")]
        public async Task<ResponseImportBlocksFromDocuments> ImportBlocksFromDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the blocks should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("regexName: name or regular expression to select block files (empty for all)")] string regexName = "",
            [Description("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            var startTime = DateTime.Now;
            var progressToken = context?.Params?.ProgressToken;

            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ImportBlocksFromDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                // Determine total by scanning .s7dcl files matching regex
                int total = 0;
                var scanWarnings = new JsonArray();
                try
                {
                    if (Directory.Exists(importPath))
                    {
                        var rx = string.IsNullOrWhiteSpace(regexName) ? null : new Regex(regexName, RegexOptions.Compiled);
                        var files = Directory.GetFiles(importPath, "*.s7dcl", SearchOption.TopDirectoryOnly);
                        foreach (var f in files)
                        {
                            var name = Path.GetFileNameWithoutExtension(f);
                            if (rx != null && !rx.IsMatch(name))
                                continue;
                            total++;

                            try
                            {
                                var missingIds = GetResMissingEnUsIds(importPath, name);
                                if (missingIds != null && missingIds.Count > 0)
                                {
                                    scanWarnings.Add(new JsonObject
                                    {
                                        ["name"] = name,
                                        ["missingEnUsIds"] = new JsonArray(missingIds.Select(id => (JsonNode)id).ToArray())
                                    });
                                }
                            }
                            catch (Exception rex)
                            {
                                scanWarnings.Add(new JsonObject
                                {
                                    ["name"] = name,
                                    ["precheckError"] = rex.Message
                                });
                            }
                        }
                    }
                }
                catch (Exception sex)
                {
                    scanWarnings.Add(new JsonObject { ["scanError"] = sex.Message });
                }

                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = total,
                        Message = total > 0 ? $"Starting import of {total} blocks from documents..." : "Scanning import directory...",
                        progressToken
                    });
                }

                var option = ParseImportDocumentOption(importOption);
                var imported = await Task.Run(() => _domain.ImportBlocksFromDocuments(softwarePath, groupPath, importPath, regexName, option));

                // Snapshot native evidence before block metadata/progress readback can fail.
                var failures = _domain.LastImportFromDocumentsFailures.ToList();
                int scanned = _domain.LastImportFromDocumentsScanned;
                int selected = _domain.LastImportFromDocumentsSelected;
                int attempted = _domain.LastImportFromDocumentsAttempted;
                int succeeded = _domain.LastImportFromDocumentsSucceeded;
                bool stopped = _domain.LastImportFromDocumentsStopped;
                bool responseReportingFailed = false;

                var responseList = new List<ResponseBlockInfo>();
                int processed = 0;
                try
                {
                    if (imported != null)
                    {
                        foreach (var block in imported)
                        {
                            if (block != null)
                            {
                                var attributes = Helper.GetAttributeList(block);
                                responseList.Add(new ResponseBlockInfo
                                {
                                    Name = block.Name,
                                    TypeName = block.GetType().Name,
                                    Namespace = block.Namespace,
                                    ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                                    MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                                    IsConsistent = block.IsConsistent,
                                    HeaderName = block.HeaderName,
                                    ModifiedDate = block.ModifiedDate,
                                    IsKnowHowProtected = block.IsKnowHowProtected,
                                    Attributes = attributes,
                                    Description = block.ToString()
                                });
                            }
                            processed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    responseReportingFailed = true;
                    failures.Add("Block metadata readback failed after import: " + ex.Message + ". Project changes may already exist; do not retry automatically.");
                }

                try
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processed,
                            Total = total,
                            Message = $"Document import ended: {succeeded}/{selected} document sets reported Success; stopped={stopped}",
                            progressToken
                        });
                    }
                }
                catch (Exception ex)
                {
                    responseReportingFailed = true;
                    failures.Add("Progress notification failed after import: " + ex.Message + ". Project changes may already exist; do not retry automatically.");
                }

                var duration = (DateTime.Now - startTime).TotalSeconds;
                Logger?.LogInformation($"Document import completed: {processed} blocks imported in {duration:F2} seconds");

                var failArr = new JsonArray();
                foreach (var f in failures.Take(50)) failArr.Add(f);

                bool ok = imported != null && selected > 0 && !stopped && !responseReportingFailed && succeeded == selected;
                string msg = stopped
                    ? $"Document import stopped after {attempted}/{selected} selected document sets: {succeeded} reported Success, {selected - attempted} not attempted. The failed or unknown import may have changed the project; do not retry automatically. See meta.failures."
                    : responseReportingFailed
                        ? $"Native import ended with {succeeded}/{selected} document sets reporting Success, but response readback or progress reporting failed. The project may have changed; do not retry automatically. See meta.failures."
                    : imported == null
                        ? "No document import attempted: no open project or unsupported version."
                        : selected == 0
                            ? $"No document sets selected from {scanned} scanned .s7dcl files; no import attempted."
                            : $"Document import completed: {succeeded} document sets reported Success, returning {processed} blocks. Content completeness has not been verified.";

                return new ResponseImportBlocksFromDocuments
                {
                    Message = msg,
                    Items = responseList,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["totalBlocks"] = total,
                        ["scannedFiles"] = scanned,
                        ["selectedFiles"] = selected,
                        ["attemptedFiles"] = attempted,
                        ["succeededFiles"] = succeeded,
                        ["notAttemptedFiles"] = selected - attempted,
                        ["stopped"] = stopped,
                        ["responseReportingFailed"] = responseReportingFailed,
                        ["mayHaveChanged"] = attempted > 0,
                        ["contentVerified"] = null,
                        ["importedBlocks"] = processed,
                        ["duration"] = duration,
                        ["failures"] = failArr,
                        ["warnings"] = scanWarnings
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Document import failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch { /* swallow(teardown): Progress notification failure must not replace the original export or import error. */ }
                }

                Logger?.LogError(ex, $"Failed importing documents from '{importPath}'");
                throw new McpException($"Unexpected error importing documents from '{importPath}': {ex.Message}. If import was attempted, the project may have changed; do not retry automatically.", ex, McpErrorCode.InternalError);
            }
        }

        private static ImportDocumentOptions ParseImportDocumentOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option)) return ImportDocumentOptions.Override;

            var normalized = option.Trim();

            // Primary: accept exact enum names (case-insensitive)
            if (Enum.TryParse<ImportDocumentOptions>(normalized, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            // Aliases and common misspellings
            switch (normalized.ToLowerInvariant())
            {
                case "override": return ImportDocumentOptions.Override;
                case "none": return ImportDocumentOptions.None;
                case "skipinactiveculture":
                case "skipinactivecultures":
                case "skipinactive":
                case "skipinactivecult":
                    return ImportDocumentOptions.SkipInactiveCultures;
                case "activeinactiveculture":
                case "activateinactivecultures":
                case "activeinactivecultures":
                case "activateinactive":
                    return ImportDocumentOptions.ActivateInactiveCultures;
                default:
                    throw new McpException($"Invalid importOption '{option}'. Allowed: None, Override, SkipInactiveCultures, ActivateInactiveCultures", McpErrorCode.InvalidParams);
            }
        }

        // .s7res is YAML; S7ResScanner reads its culture entries.
        private static List<string> GetResMissingEnUsIds(string directory, string baseName)
            => S7ResScanner.GetMissingEnUsIds(directory, baseName);
    }
}
