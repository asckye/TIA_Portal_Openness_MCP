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

        public ResponseImportFromDocuments ImportFromDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the block should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("fileNameWithoutExtension: name of the block file without extension") ] string fileNameWithoutExtension,
            [Description("importOption: string value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            try
            {
                if (TiaMcp.Versioning.TiaVersionCatalog.Get(HardwareContract.ReleaseKey).MajorVersion < 20)
                {
                    throw new McpException("ImportPlcBlockDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                var option = ParseImportDocumentOption(importOption);

                // Pre-check .s7res for missing en-US tags
                var warnings = new JsonArray();
                try
                {
                    var missingIds = GetResMissingEnUsIds(importPath, fileNameWithoutExtension);
                    if (missingIds != null && missingIds.Count > 0)
                    {

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

                    warnings.Add(new JsonObject
                    {
                        ["name"] = fileNameWithoutExtension,
                        ["precheckError"] = ex.Message
                    });
                }

                if (fileNameWithoutExtension.Replace('\\', '/').Split('/').Length != 1 || fileNameWithoutExtension
                        .IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("fileNameWithoutExtension must be one safe document basename.");
                var formatPreflight = EngineeringAuditLogic
                    .DocumentPreflight(File.ReadAllText(Path.Combine(importPath, fileNameWithoutExtension + ".s7dcl")), TiaMcp.Versioning.TiaVersionCatalog
                        .Get(HardwareContract.ReleaseKey).MajorVersion, null);
                var ok = _domain.ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, option);
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
                throw new McpException($"Unexpected error importing from documents: {ex.Message}. If import was attempted, the project may have changed; do not retry automatically.",
                        ex, McpErrorCode.InternalError);
            }
        }

        public async Task<ResponseImportBlocksFromDocuments> ImportBlocksFromDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the blocks should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("regexName: name or regular expression to select block files (empty for all)")] string regexName = "",
            [Description("importOption: string value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            var startTime = DateTime.Now;
            var progressToken = context?.Params?.ProgressToken;

            try
            {
                if (TiaMcp.Versioning.TiaVersionCatalog.Get(HardwareContract.ReleaseKey).MajorVersion < 20)
                {
                    throw new McpException("ImportPlcBlocksDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
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
                            processed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    responseReportingFailed = true;
                    failures
                        .Add("Block metadata readback failed after import: " + ex.Message + ". Project changes may already exist; do not retry automatically.");
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
                    failures
                        .Add("Progress notification failed after import: " + ex.Message + ". Project changes may already exist; do not retry automatically.");
                }

                var duration = (DateTime.Now - startTime).TotalSeconds;


                var failArr = new JsonArray();
                foreach (var f in failures.Take(50)) failArr.Add(f);

                bool ok = imported != null && selected > 0 && !stopped && !responseReportingFailed && succeeded == selected;
                string msg = stopped
                    ? $"Document import stopped after {attempted}/{selected} selected document sets: {succeeded} reported Success, {selected - attempted} not attempted. The failed or unknown import may have changed the project; do not retry automatically. See data.failures."
                    : responseReportingFailed
                        ? $"Native import ended with {succeeded}/{selected} document sets reporting Success, but response readback or progress reporting failed. The project may have changed; do not retry automatically. See data.failures."
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

                throw new McpException($"Unexpected error importing documents from '{importPath}': {ex.Message}. If import was attempted, the project may have changed; do not retry automatically.",
                        ex, McpErrorCode.InternalError);
            }
        }

        [Flags]
        private enum DocumentImportOption { None = 0, Override = 1, SkipInactiveCultures = 2, ActivateInactiveCultures = 4 }

        private static string ParseImportDocumentOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option)) return "Override";

            var normalized = option.Trim();

            // Primary: accept exact enum names (case-insensitive)
            if (Enum.TryParse(normalized, ignoreCase: true, out DocumentImportOption parsed))
            {
                return parsed.ToString();
            }

            // Aliases and common misspellings
            switch (normalized.ToLowerInvariant())
            {
                case "override": return "Override";
                case "none": return "None";
                case "skipinactiveculture":
                case "skipinactivecultures":
                case "skipinactive":
                case "skipinactivecult":
                    return "SkipInactiveCultures";
                case "activeinactiveculture":
                case "activateinactivecultures":
                case "activeinactivecultures":
                case "activateinactive":
                    return "ActivateInactiveCultures";
                default:
                    throw new McpException($"Invalid importOption '{option}'. Allowed: None, Override, SkipInactiveCultures, ActivateInactiveCultures",
                            McpErrorCode.InvalidParams);
            }
        }
    }
}
