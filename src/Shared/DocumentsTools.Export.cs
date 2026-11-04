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
    internal sealed partial class DocumentsTools
    {

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
                if (TiaMcp.Versioning.TiaVersionCatalog.Get(HardwareContract.ReleaseKey).MajorVersion < 20)
                {
                    throw new McpException("ExportPlcBlocksDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                // First, get the list of blocks to determine total count


                var allBlocks = await Task.Run(() => _domain.GetBlocks(softwarePath, regexName));
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
                        Meta = ResponseMeta
                            .Basic(DateTime.Now, true, ("totalBlocks", 0), ("exportedBlocks", 0), ("duration", (DateTime.Now - startTime).TotalSeconds))
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
                            var attributes = block.Attributes?.Select(a => new Attribute { Name = a.Name, Value = a.Value, AccessMode = a.AccessMode });

                            responseList.Add(new ResponseBlockInfo
                            {
                                Name = block.Name,
                                TypeName = block.TypeName,
                                Namespace = block.Namespace,
                                ProgrammingLanguage = block.ProgrammingLanguage,
                                MemoryLayout = block.MemoryLayout,
                                IsConsistent = block.IsConsistent,
                                HeaderName = block.HeaderName,
                                ModifiedDate = block.ModifiedDate,
                                IsKnowHowProtected = block.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = block.Description
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


                    // Surface per-block skip/failure reasons so a "matched N but exported 0" is never silent.
                    var failures = _domain.LastExportAsDocumentsFailures;
                    var skipped = totalBlocks - processedCount;
                    var msg = $"Document export completed: {processedCount}/{totalBlocks} blocks (regex '{regexName}') from '{softwarePath}' to '{exportPath}'";
                    if (skipped > 0)
                    {
                        var reason = (failures != null && failures.Count > 0)
                            ? string.Join("; ", failures)
                            : "no reason captured (inconsistent block? compile first, or the block type/language may not support SIMATIC SD export — try ExportPlcBlock for XML)";
                        msg += $". {skipped} not exported: {reason}";
                    }

                    // envelope: legacy-multiple-dynamic-fields
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

                throw new McpException($"Unexpected error exporting documents to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }
    }
}
