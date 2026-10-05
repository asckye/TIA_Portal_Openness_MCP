using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;

#if TIA_SHARED_ADAPTER_PATHS
using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
#else
using Documents = TiaMcpServer.Siemens.LocalDocuments.PlcDocumentPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class DocumentsService
    {
        private readonly IEngineeringSession _session;

        public DocumentsService(IEngineeringSession session) => _session = session;

        private bool IsProjectNull()
        {
#if TIA_SHARED_ADAPTER_PATHS
            if (_session.IsProjectNull()) return true;
            return TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).Documents.CurrentProject == null;
#else
            return _session.IsProjectNull();
#endif
        }

        // TIA portal crashes when exporting blocks as documents, :-(
        /// <summary>
        /// Per-block failure reasons from the most recent ExportBlocksAsDocuments call (empty on full success).
        /// The tool layer surfaces this so a "totalBlocks &gt; 0 but exportedBlocks == 0" no longer looks silent.
        /// </summary>
        public IReadOnlyList<string> LastExportAsDocumentsFailures { get; private set; } = new List<string>();

        /// <summary>Names of the blocks the last single-document import created (DocumentImportResultForBlocks.ImportedPlcBlocks).</summary>
        public string[] LastImportedDocumentBlocks { get; private set; } = Array.Empty<string>();

        /// <summary>Per-file diagnostics; native failures can leave partial project changes.</summary>
        public IReadOnlyList<string> LastImportFromDocumentsFailures { get; private set; } = new List<string>();

        public int LastImportFromDocumentsScanned { get; private set; }

        public int LastImportFromDocumentsSelected { get; private set; }

        public int LastImportFromDocumentsAttempted { get; private set; }

        public int LastImportFromDocumentsSucceeded { get; private set; }

        public bool LastImportFromDocumentsStopped { get; private set; }

        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _session.Logger?.LogInformation($"Exporting block as documents by path: {blockPath}");
            var success = false;
            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                }

                Capability.RequireSupported(TiaFeature.DocumentExport);

                var softwareContainer = _session.GetSoftwareContainer(softwarePath);
                if (Documents.Software(softwareContainer) is PlcSoftware plcSoftware)
                {
                    if (plcSoftware != null)
                    {
                        // Export code blocks as documents
                        // https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500

                        var groupPath = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                        var blockName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                        var group = _session.GetPlcBlockGroupByPath(softwarePath, groupPath);

                        // join exportPath and groupPath
                        if (!Directory.Exists(exportPath))
                        {
                            Directory.CreateDirectory(exportPath);
                        }

                        if (preservePath && !string.IsNullOrEmpty(groupPath))
                        {
                            exportPath = Path.Combine(exportPath, groupPath);

                            if (!Directory.Exists(exportPath))
                            {
                                Directory.CreateDirectory(exportPath);
                            }
                        }

                        try
                        {
                            // delete files s7dcl/s7res if already exists
                            var blockFiles7dclPath = Path.Combine(exportPath, $"{blockName}.s7dcl");
                            if (File.Exists(blockFiles7dclPath))
                            {
                                File.Delete(blockFiles7dclPath);
                            }
                            var blockFiles7resPath = Path.Combine(exportPath, $"{blockName}.s7res");
                            if (File.Exists(blockFiles7resPath))
                            {
                                File.Delete(blockFiles7resPath);
                            }

                            var result = Documents.ExportOptional(group, exportPath, blockName);

                            if (result != null && Documents.State(result) == DocumentResultState.Success)
                            {
                                success = true;
                            }
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            // The export or import of blocks with mixed programming languages is not possible
                            throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at block '{blockName}'. {ex.Message}", null, ex);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.ExportFailed, $"Exception at block '{blockName}'. {ex.Message}", null, ex);
                        }

                    }

                }

            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _session.Logger?.LogError(pex, "ExportAsDocuments failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
            return success;
        }

        public IEnumerable<PlcBlock>? ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _session.Logger?.LogInformation("Exporting blocks as documents...");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _session.Logger?.LogWarning("ExportBlocksAsDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();

            // Resolution/traversal failures must propagate, never appear as a successful empty export.
            var list = _session.GetBlocks(softwarePath, regexName)?.ToArray()
                ?? throw new PortalException(PortalErrorCode.InvalidState, "No project is open; document export did not run.");

            for (int i = 0; i < list.Count(); i++)
            {
                var block = list[i];

                _session.Logger?.LogDebug($"- Exporting block as document {i}/{list.Count()} : {Documents.Name(block)}");

                // Skip inconsistent blocks (TIA generally won’t export them)
                if (!Documents.IsConsistent(block))
                {
                    _session.Logger?.LogWarning($"Skipping inconsistent block {Documents.Name(block)}");
                    continue;
                }

                // Determine base directory (preserve group path if requested)
                string targetDir = exportPath;
                if (preservePath && Documents.Parent(block) is PlcBlockGroup parentGroup)
                {
                    var groupPath = _session.GetPlcBlockGroupPath(parentGroup);
                    if (!string.IsNullOrWhiteSpace(groupPath))
                    {
                        targetDir = Path.Combine(exportPath, groupPath.Replace('/', '\\'));
                    }
                }

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Documents.Name(block)}: cannot create directory '{targetDir}' ({ex.Message})");
                    _session.Logger?.LogError(ex, $"Directory creation failed for {targetDir}");
                    continue;
                }

                var fileDcl = Path.Combine(targetDir, $"{Documents.Name(block)}.s7dcl");
                var fileRes = Path.Combine(targetDir, $"{Documents.Name(block)}.s7res");

                // Clean previous artifacts
                foreach (var f in new[] { fileDcl, fileRes })
                {
                    try
                    {
                        if (File.Exists(f))
                        {
                            File.Delete(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Documents.Name(block)}: cannot delete existing '{Path.GetFileName(f)}' ({ex.Message})");
                        _session.Logger?.LogError(ex, $"Failed deleting existing file {f}");
                        // Continue anyway; export might overwrite.
                    }
                }

                try
                {
                    DocumentExportResult? result = null;
                    try
                    {
                        result = Documents.Export(block, new DirectoryInfo(targetDir), Documents.Name(block));
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        failures.Add($"{Documents.Name(block)}: not supported ({ex.Message})");
                        _session.Logger?.LogWarning(ex, $"EngineeringNotSupported exporting {Documents.Name(block)}");
                        continue;
                    }
                    catch (LicenseNotFoundException ex)
                    {
                        failures.Add($"{Documents.Name(block)}: license not found ({ex.Message})");
                        _session.Logger?.LogError(ex, $"License issue exporting {Documents.Name(block)}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Documents.Name(block)}: export threw ({ex.Message})");
                        _session.Logger?.LogError(ex, $"ExportAsDocuments failed for {Documents.Name(block)}");
                        continue;
                    }

                    if (result == null)
                    {
                        failures.Add($"{Documents.Name(block)}: no result returned");
                        continue;
                    }

                    if (Documents.State(result) == DocumentResultState.Success)
                    {
                        exportList.Add(block);
                    }
                    else
                    {
                        failures.Add($"{Documents.Name(block)}: result state {Documents.State(result)}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Documents.Name(block)}: unexpected exception ({ex.Message})");
                    _session.Logger?.LogError(ex, $"Unexpected wrapper error for {Documents.Name(block)}");
                }
            }

            if (failures.Count > 0)
            {
                _session.Logger?.LogWarning($"ExportBlocksAsDocuments completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optional verbose list:

            }
            else
            {
                _session.Logger?.LogInformation($"ExportBlocksAsDocuments completed successfully. Exported {exportList.Count} blocks.");
            }

            LastExportAsDocumentsFailures = failures;
            return exportList;
        }

        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, ImportDocumentOptions option)
        {
            LastImportedDocumentBlocks = Array.Empty<string>();
            _session.Logger?.LogInformation($"Importing block from documents: {fileNameWithoutExtension} in {importPath}");

            if (IsProjectNull())
            {
                return false;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _session.Logger?.LogWarning("ImportFromDocuments is only supported on TIA Portal V20 or newer");
                return false;
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (!(Documents.Software(softwareContainer) is PlcSoftware plcSoftware))
            {
                throw new PortalException(PortalErrorCode.NotFound, $"PLC software '{softwarePath}' not found. Use GetProjectTree for the exact PLC name.");
            }

            var dir = new DirectoryInfo(importPath);
            if (!dir.Exists)
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"Import directory does not exist: {importPath}");
            }
            if (!File.Exists(Path.Combine(importPath, fileNameWithoutExtension + ".s7dcl")))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"No '{fileNameWithoutExtension}.s7dcl' found under {importPath}. importPath is the DIRECTORY holding the .s7dcl/.s7res, and fileNameWithoutExtension omits the extension.");
            }

            // Resolve the target group. Empty path = root. A non-empty path that does NOT resolve is a
            // caller error — DO NOT silently retarget root (that is how a nested-group import used to
            // land the block at root and get AutoNumber-renumbered).
            PlcBlockGroup targetGroup;
            if (string.IsNullOrWhiteSpace(groupPath))
            {
                targetGroup = Documents.BlockGroup(plcSoftware);
            }
            else
            {
                targetGroup = _session.GetPlcBlockGroupByPath(softwarePath, groupPath)
                    ?? throw new PortalException(PortalErrorCode.NotFound,
                        $"Group path '{groupPath}' not found under PLC '{softwarePath}'. Use GetSoftwareTree for exact group names, or pass an empty groupPath to import at the root.");
            }

            // Preserve the existing Override-only number-restoration feature. These are additional
            // writes, not transactional rollback or proof that the import preserved all attributes.
            // Do not perform these writes for None or culture-only options.
            var existing = Documents.FindBlock(Documents.Blocks(targetGroup), fileNameWithoutExtension);
            int? prevNumber = null;
            bool prevAutoNumber = false;
            try { if ((option & ImportDocumentOptions.Override) != 0 && existing != null) { prevNumber = Documents.Number(existing); prevAutoNumber = Documents.AutoNumber(existing); } } catch { /* swallow(probe-optional): Unavailable previous numbering leaves the existing best-effort restoration disabled. */ }

            DocumentImportResultForBlocks? result;
            try
            {
                result = InvocationJournal.Native("ImportFromDocuments.import", () => Documents.Import(Documents.Blocks(targetGroup), dir, fileNameWithoutExtension, option));
            }
            catch (EngineeringNotSupportedException ex)
            {
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, $"ImportFromDocuments not supported for '{fileNameWithoutExtension}': {ex.Message}. The native call was attempted and the project may have changed; do not retry automatically.", null, ex);
            }
            catch (EngineeringTargetInvocationException ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, $"ImportFromDocuments failed for '{fileNameWithoutExtension}' into group '{(string.IsNullOrWhiteSpace(groupPath) ? "<root>" : groupPath)}': {ex.Message}. The native call was attempted and the project may have changed; do not retry automatically. Check the .s7dcl syntax (types/attributes) and that .s7res matches the S7_MLC ids.", null, ex);
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, $"ImportFromDocuments failed for '{fileNameWithoutExtension}' into group '{(string.IsNullOrWhiteSpace(groupPath) ? "<root>" : groupPath)}': {ex.Message}. The native call was attempted and the project may have changed; do not retry automatically.", null, ex);
            }

            try
            {
                if (result == null || Documents.State(result) != DocumentResultState.Success || Documents.ImportedBlocks(result) == null)
                {
                    throw new PortalException(PortalErrorCode.ImportFailed,
                        $"ImportFromDocuments returned state '{Documents.OptionalState(result)?.ToString() ?? "null"}' for '{fileNameWithoutExtension}'. The project may have changed; do not retry automatically." + DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result));
                }
                try
                {
                    LastImportedDocumentBlocks = Documents.ImportedBlocks(result) == null ? Array.Empty<string>() : EngineeringGroupOperations.Items(Documents.ImportedBlocks(result)).Cast<PlcBlock>().Select(b => Documents.Name(b)).ToArray();
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.ImportFailed,
                        $"Native import reported Success but imported-name readback failed: {ex.Message}. The project may have changed; do not retry automatically." + DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result), null, ex);
                }

                // Legacy Override-only post-import writes; preservation is best-effort, not verified.
                if (prevNumber.HasValue)
                {
                    var imported = Documents.FindBlock(Documents.Blocks(targetGroup), fileNameWithoutExtension);
                    if (imported != null)
                    {
                        try
                        {
                            if (Documents.Number(imported) != prevNumber.Value)
                            {
                                Documents.SetAutoNumber(imported, false);
                                Documents.SetNumber(imported, prevNumber.Value);
                            }
                            else
                            {
                                Documents.SetAutoNumber(imported, prevAutoNumber);
                            }
                        }
                        catch (Exception ex)
                        {
                            _session.Logger?.LogWarning(ex, $"Could not restore block number {prevNumber} for {fileNameWithoutExtension}");
                        }
                    }
                }
                return true;
            }
            catch (PortalException) { throw; }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed,
                    $"Post-import result or identity readback failed: {ex.Message}. The project may have changed; do not retry automatically."
                    + DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result), null, ex);
            }
        }

        // Report names as evidence, never as proof that an ambiguous import was rolled back.
        private static string DocumentImportedNamesSuffix(DocumentImportResultForBlocks? result)
        {
            var names = new List<string>();
            try
            {
                if (Documents.OptionalImportedBlocks(result) != null)
                    foreach (var block in Documents.Enumerate(Documents.ImportedBlocks(result)))
                        if (block != null) names.Add(Documents.Name(block));
                return " Native reported imported names: [" + string.Join(", ", names) + "].";
            }
            catch (Exception ex)
            {
                return " Native reported imported names (incomplete): [" + string.Join(", ", names) + "]. Readback error: " + ex.Message;
            }
        }

        // Native log lines of a document export / import (DocumentResultMessageComposition of DocumentResultMessage).
        private string DocumentMessageSuffix(DocumentImportResult? result)
        {
            try
            {
                var lines = _session.DocumentMessages(Documents.Messages(result)).Select(m => m?.ToString()).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
                return lines.Length == 0 ? "" : " Native messages: " + string.Join(" | ", lines);
            }
            catch { /* swallow(probe-optional): Unavailable native document messages must not replace the import result diagnostic. */ return ""; }
        }

        /// <summary>Depth-first search for a block by exact name across all nested block groups.</summary>
        private PlcBlock? FindBlockRecursive(PlcBlockGroup group, string blockName)
        {
            if (group == null)
            {
                return null;
            }
            var here = Documents.FindBlock(Documents.Blocks(group), blockName);
            if (here != null)
            {
                return here;
            }
            foreach (var sub in Documents.Enumerate(Documents.BlockGroups(group)))
            {
                var found = FindBlockRecursive(sub, blockName);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, ImportDocumentOptions option, bool preservePath = false)
        {
            _session.Logger?.LogInformation($"Importing blocks from documents in {importPath} with regex '{regexName}'");
            var failures = new List<string>();
            LastImportFromDocumentsFailures = failures;
            LastImportFromDocumentsScanned = 0;
            LastImportFromDocumentsSelected = 0;
            LastImportFromDocumentsAttempted = 0;
            LastImportFromDocumentsSucceeded = 0;
            LastImportFromDocumentsStopped = false;

            if (IsProjectNull()) return null;
            if (Engineering.TiaMajorVersion < 20) return null;

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (!(Documents.Software(softwareContainer) is PlcSoftware plcSoftware))
                throw new PortalException(PortalErrorCode.NotFound, $"PLC software '{softwarePath}' not found.");
            // Resolve before any native import. Only an explicitly empty path selects root.
            var group = string.IsNullOrWhiteSpace(groupPath) ? Documents.BlockGroup(plcSoftware)
                : _session.GetPlcBlockGroupByPath(softwarePath, groupPath)
                    ?? throw new PortalException(PortalErrorCode.NotFound, $"Group path '{groupPath}' not found under PLC '{softwarePath}'. No import attempted.");
            var dir = new DirectoryInfo(importPath);
            if (!dir.Exists)
                throw new PortalException(PortalErrorCode.InvalidParams, $"Import directory does not exist: {importPath}. No import attempted.");
            var rx = string.IsNullOrWhiteSpace(regexName) ? null : new Regex(regexName, RegexOptions.Compiled);
            var files = dir.GetFiles("*.s7dcl", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
            LastImportFromDocumentsScanned = files.Length;
            var selected = files.Where(f => rx == null || rx.IsMatch(Path.GetFileNameWithoutExtension(f.Name))).ToArray();
            LastImportFromDocumentsSelected = selected.Length;
            var imported = new List<PlcBlock>();
            foreach (var file in selected)
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                DocumentImportResultForBlocks? result = null;
                try
                {
                    LastImportFromDocumentsAttempted++;
                    result = InvocationJournal.Native("ImportBlocksFromDocuments.import", () => Documents.Import(Documents.Blocks(group), dir, name, option));
                    if (result == null || Documents.State(result) != DocumentResultState.Success || Documents.ImportedBlocks(result) == null)
                    {
                        failures.Add($"{name}: native state={Documents.OptionalState(result)?.ToString() ?? "null"}. The project may have changed; batch stopped, remaining files not attempted. Do not retry automatically."
                            + DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result));
                        LastImportFromDocumentsStopped = true;
                        break;
                    }
                    // Materialize before adding: failed result enumeration must not masquerade as success.
                    var blocks = EngineeringGroupOperations.Items(Documents.ImportedBlocks(result)).Cast<PlcBlock>().Where(block => block != null).ToArray();
                    imported.AddRange(blocks);
                    LastImportFromDocumentsSucceeded++;
                }
                catch (Exception ex)
                {
                    _session.Logger?.LogWarning(ex, "Stopping document batch after '{Name}'", name);
                    failures.Add($"{name}: {ex.Message}. Native import was attempted; the project may have changed. Batch stopped, remaining files not attempted. Do not retry automatically." + DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result));
                    LastImportFromDocumentsStopped = true;
                    break;
                }
            }
            return imported;
        }

        public bool VerifyLastDocumentImport(string softwarePath, string groupPath)
        {
            var group = _session.GetPlcBlockGroupByPath(softwarePath, groupPath) ?? throw new PortalException(PortalErrorCode.NotFound, "Import target group not found.");
            return InvocationJournal.Native("ImportFromDocuments.exactReadback", () => EngineeringAuditLogic.ExactNamesPresent(LastImportedDocumentBlocks,
                EngineeringGroupOperations.Items(Documents.Blocks(group)).Cast<PlcBlock>().Select(b => Documents.Name(b))));
        }
    }
}
