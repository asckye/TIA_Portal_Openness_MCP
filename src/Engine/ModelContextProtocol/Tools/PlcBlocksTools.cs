using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
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
using Siemens.Engineering;
using System.Globalization;
using System.Text;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcBlocksTools
    {
        private readonly IEngineeringSession _session;
        private readonly PlcBlocksService _blocks;

        public PlcBlocksTools(IEngineeringSession session, PlcBlocksService blocks)
        {
            _session = session;
            _blocks = blocks;
        }

        #region blocks

        [McpServerTool(Name = "GetPlcBlockInfo"), Description("[L2][PLC-Software] Get detailed info for one block (attributes, language, number, modification time). Requires: Connect + OpenProject. blockPath must be fully qualified: 'Group/Subgroup/BlockName' — get it from GetSoftwareTree or GetPlcBlockHierarchy. Returns: IsConsistent (false = must compile before export). Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetBlockInfoV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath)
            => PlcToolContract.Run("GetPlcBlockInfo", false, true, () => GetBlockInfo(softwarePath, blockPath));

        public ResponseBlockInfo GetBlockInfo(
            string softwarePath,
            string blockPath)
        {
            try
            {
                var block = _session.GetBlock(softwarePath, blockPath);
                if (block != null)
                {
                    var attributes = Helper.GetAttributeList(block);

                    return new ResponseBlockInfo
                    {
                        Message = $"Block info retrieved from '{blockPath}' in '{softwarePath}'",
                        Name = block.Name,
                        TypeName = block.GetType().Name,
                        Namespace = block.Namespace,
                        ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage),block.ProgrammingLanguage),
                        MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                        IsConsistent = block.IsConsistent,
                        HeaderName = block.HeaderName,
                        ModifiedDate = block.ModifiedDate,
                        IsKnowHowProtected = block.IsKnowHowProtected,
                        Attributes = attributes,
                        Description = block.ToString(),
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException($"Block not found at '{blockPath}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving block info from '{blockPath}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListPlcBlocks"), Description("[L2][PLC-Software] Get a flat list of user-group blocks in PLC software. System block groups are excluded; inspect meta.dataComplete for unreadable attributes. Requires: Connect + OpenProject. Use GetPlcBlockHierarchy instead when you need group/folder paths for ExportPlcBlock. Returns: block name, number, type (OB/FC/FB/GlobalDB/InstanceDB), programming language. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetBlocksV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "")
            => PlcToolContract.Run("ListPlcBlocks", false, true, () => GetBlocks(softwarePath, regexName));

        public ResponseBlocks GetBlocks(
            string softwarePath,
            string regexName = "")
        {
            try
            {
                var list = _session.GetBlocks(softwarePath, regexName);

                // null = 根本没查成（没连接/没打开项目）；空列表 = 这个 PLC 里确实没有。
                if (list == null)
                {
                    throw new McpException(
                        $"No TIA project is open, cannot list blocks of '{softwarePath}'. "
                        + "Call Connect / OpenProject (or AttachToOpenProject) first. "
                        + "This does NOT mean the PLC has no blocks.",
                        McpErrorCode.InvalidParams);
                }

                var read = new PlcListingRead();
                var responseList = new List<ResponseBlockInfo>();
                foreach (var block in list) responseList.Add(Helper.ReadBlockInfo(block, read));

                if (list != null)
                {
                    return new ResponseBlocks
                    {
                        Message = $"Blocks with regex '{regexName}' retrieved from '{softwarePath}'",
                        Items = responseList,
                        Meta = read.Metadata(softwarePath + ": user block groups; system block groups excluded")
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving blocks with regex '{regexName}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving blocks with regex '{regexName}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcBlockHierarchy"), Description("[L2][PLC-Software]Get user blocks with their group hierarchy. System block groups are excluded; inspect meta.dataComplete for unreadable attributes. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetBlocksWithHierarchyV4(
        [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
            => PlcToolContract.Run("GetPlcBlockHierarchy", false, true, () => GetBlocksWithHierarchy(softwarePath));

        public ResponseBlocksWithHierarchy GetBlocksWithHierarchy(
        string softwarePath)
        {
            try
            {
                var rootGroup = _session.GetBlockRootGroup(softwarePath);
                if (rootGroup != null)
                {
                    var read = new PlcListingRead();
                    var hierarchy = read.Required(softwarePath + "/BlockGroup/Hierarchy", () => Helper.BuildBlockHierarchy(rootGroup, read));
                    return new ResponseBlocksWithHierarchy
                    {
                        Message = $"Block hierarchy retrieved from '{softwarePath}'",
                        Root = hierarchy,
                        Meta = read.Metadata(softwarePath + ": user block groups; system block groups excluded")
                    };
                }
                else
                {
                    // Specific failure: root group could not be resolved
                    throw new McpException($"Block root group not found for '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Generic unexpected failure wrapper
                throw new McpException($"Unexpected error retrieving block hierarchy for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }



        [McpServerTool(Name = "ExportPlcBlock"), Description("[L2][PLC-Software] Export one block to an XML file. Requires: Connect + OpenProject + block must be consistent (compile first if IsConsistent=false). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree — bare names return InvalidParams with suggestions. Pick the right tool: batch → ExportPlcBlocks; readable SCL/.s7dcl text → ExportPlcBlockDocuments. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportBlockV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: full path to the block in the project structure, e.g. 'Group/Subgroup/Name' (single names are ambiguous)")] string blockPath,
            [Description("exportPath: directory that receives '<BlockName>.xml' - or, when it ends in .xml, the target file itself; the response reports the file written (Meta.exportedFile)")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
            => PlcToolContract.Run("ExportPlcBlock", true, true, () => ExportBlock(softwarePath, blockPath, exportPath, preservePath));

        public ResponseExportBlock ExportBlock(
            string softwarePath,
            string blockPath,
            string exportPath,
            bool preservePath = false)
        {
            try
            {
                var block = _session.ExportBlock(softwarePath, blockPath, exportPath, preservePath);
                if (block != null)
                {
                    return new ResponseExportBlock
                    {
                        Message = $"Block exported from '{blockPath}' to '{_session.LastExportedFile ?? exportPath}'",
                        Meta = ResponseMeta.Basic(DateTime.Now, true, ("exportedFile", _session.LastExportedFile))
                    };
                }
                // Should not be reachable because _session.ExportBlock throws on failure
                throw new McpException($"Failed exporting block from '{blockPath}' to '{exportPath}'", McpErrorCode.InternalError);
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                // Map known portal errors to sharper MCP errors and messages.
                switch (pex.Code)
                {
                    case TiaMcpServer.Siemens.PortalErrorCode.NotFound:
                        {
                            var msg = ("Block not found." + EngineeringLookupHints.BuildBlockDidYouMean(softwarePath, blockPath)).Trim();
                            throw new McpException(msg, McpErrorCode.InvalidParams);
                        }

                    case TiaMcpServer.Siemens.PortalErrorCode.ExportFailed:
                        {
                            // Relay underlying portal error with concise reason; log full details
                            var reason = pex.InnerException?.Message?.Trim();
                            var msg = "Failed to export block.";
                            if (!string.IsNullOrEmpty(reason)) msg += $" Reason: {reason}";

                            McpServer.Logger?.LogError(pex, "MCP ExportPlcBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}",
                                pex.Data?["softwarePath"], pex.Data?["blockPath"], pex.Data?["exportPath"]);

                            throw new McpException(msg, McpErrorCode.InternalError);
                        }

                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidParams:
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidState:
                        {
                            throw new McpException(pex.Message, McpErrorCode.InvalidParams);
                        }
                }

                // Fallback
                throw new McpException(pex.Message, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting block from '{blockPath}' to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Un-exposed from MCP (tool consolidation): use ExportBlock with a caller-chosen directory. Method kept for internal use.
        public ResponseTempExport ExportBlockToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("blockPath: full block path inside PLC software")] string blockPath,
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = _session.ExportBlockToTemp(softwarePath, blockPath, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Block exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException("Failed exporting block to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting block to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private string BuildBlockPathSuggestion(string softwarePath, string blockPath)
        {
            if (string.IsNullOrEmpty(blockPath) || blockPath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(blockPath);
                var blocks = _session.GetBlocks(softwarePath, $"^{escaped}$");
                if (blocks == null || blocks.Count == 0)
                {
                    blocks = _session.GetBlocks(softwarePath, escaped);
                }

                var candidates = blocks
                    .Take(10)
                    .Select(b =>
                    {
                        var name = b.Name;
                        var parts = new List<string> { name };
                        var parent = b.Parent;
                        while (parent != null)
                        {
                            if (parent is PlcBlockSystemGroup) break;
                            if (parent is PlcBlockGroup grp)
                            {
                                parts.Insert(0, grp.Name);
                                parent = grp.Parent;
                            }
                            else break;
                        }
                        if (parts.Count > 1) parts.RemoveAt(0);
                        return string.Join("/", parts);
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return candidates.Count > 0 ? $" Did you mean: {string.Join(", ", candidates)}?" : string.Empty;
            }
            catch
            {
 /* swallow(probe-optional): Path suggestions are optional and must not replace the original lookup error. */                return string.Empty; // best effort only
            }
        }

        private string BestEffortSuggestGroupPath(string softwarePath, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return string.Empty;

            try
            {
                // Suggest existing group paths based on blocks' parent groups (best effort).
                var blocks = _session.GetBlocks(softwarePath, "");
                if (blocks == null) return string.Empty;

                var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in blocks.Take(300))
                {
                    var parent = b?.Parent;
                    var parts = new List<string>();
                    while (parent != null)
                    {
                        if (parent is PlcBlockSystemGroup) break;
                        if (parent is PlcBlockGroup grp)
                        {
                            parts.Insert(0, grp.Name);
                            parent = grp.Parent;
                        }
                        else break;
                    }
                    if (parts.Count > 0)
                        groups.Add(string.Join("/", parts));
                }

                var key = groupPath.Trim().Trim('/').ToLowerInvariant();
                var candidates = groups
                    .Where(g => g.ToLowerInvariant().Contains(key) || key.Contains(g.ToLowerInvariant()))
                    .Take(10)
                    .ToList();

                return candidates.Count > 0 ? $" Did you mean groupPath: {string.Join(", ", candidates)}?" : string.Empty;
            }
            catch
            {
 /* swallow(probe-optional): Group suggestions are optional and must not replace the original import error. */                return string.Empty;
            }
        }
        [McpServerTool(Name = "ImportPlcBlock"), Description("[L1][PLC-Software] Import a single SimaticML XML block file into PLC software. Requires: Connect + OpenProject. importPath must be an absolute path to a .xml file. After import it reads back to confirm the block is present (Meta.verified); call CompilePlcDiagnostics for full consistency. Pick the right tool: SCL/.s7dcl text → ImportPlcBlockDocuments; multiple XML files → ImportPlcBlocksFromDirectory; a full exported program (UDTs+tags+blocks) → ImportPlcProgramFromDirectory; JSON-built blocks → BuildAndImportPlcArtifact. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportBlockV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the block")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the block")] string importPath)
            => PlcToolContract.Run("ImportPlcBlock", true, true, () => ImportBlock(softwarePath, groupPath, importPath));

        public ResponseImportBlock ImportBlock(
            string softwarePath,
            string groupPath,
            string importPath)
        {
            try
            {
                _session.ImportBlock(softwarePath, groupPath, importPath);

                // 读回校验的判据是 **XML 里声明的块名 + 块号**，不是 XML 文件名。
                // 例如 OB100.xml 可以声明名为 Startup 的块，不能按文件名推断导入结果。
                var outcome = VerifyImportedBlock(softwarePath, importPath);

                if (outcome.State == PlcBlockVerificationState.Mismatch)
                {
                    // 确知不符：块进去了，但和 XML 声明的不是同一个东西。
                    // 这是**可判定的失败**，不能返回一条带 ⚠ 的正常响应了事。
                    throw new PlcBlockVerificationException(
                        $"ImportPlcBlock: the block was imported from '{importPath}' into '{groupPath}', "
                        + $"but read-back does NOT match what the XML declares: {outcome.Detail}. "
                        + "⚠ The project HAS been modified — inspect it in TIA before retrying.");
                }

                bool verified = outcome.State == PlcBlockVerificationState.Verified;

                return new ResponseImportBlock
                {
                    Message = verified
                        ? $"Block imported from '{importPath}' to '{groupPath}' (verified)"
                        : $"⚠ 未验证：block imported from '{importPath}' to '{groupPath}', but the read-back "
                          + $"could not confirm it ({outcome.Detail}). Confirm with ListPlcBlocks / GetPlcBlockInfo "
                          + "before treating this as done.",
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["verified"] = verified,
                        ["verifyDetail"] = outcome.Detail
                    }
                };
            }
            catch (PortalException pex) when (pex.Code == PortalErrorCode.NotFound)
            {
                var hint = BestEffortSuggestGroupPath(softwarePath, groupPath);
                throw new McpException($"{pex.Message}{hint}", McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Failed importing block from '{importPath}' to '{groupPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcBlocksFromDirectory"), Description("[L2][PLC-Software] Batch import PLC block .xml (SimaticML) files from a directory into a block group. Pick the right tool: SCL/.s7dcl text → ImportPlcBlocksDocuments; a full mixed program with UDTs+tag tables+blocks in types-first lexical order (not dependency resolution) → ImportPlcProgramFromDirectory; a single XML file → ImportPlcBlock. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportBlocksFromDirectoryV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import blocks")] string groupPath,
            [Description("dir: directory that contains block .xml files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override, false=None (reject existing objects; no rename)")] bool overwrite = true)
            => PlcToolContract.Run("ImportPlcBlocksFromDirectory", true, true, () => ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, overwrite));

        public ResponseImportBatch ImportBlocksFromDirectory(
            string softwarePath,
            string groupPath,
            string dir,
            string regexName = "",
            bool overwrite = true)
        {
            try
            {
                var result = _session.ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} blocks from '{dir}' into '{groupPath}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = ResponseMeta.Basic(DateTime.Now, (result.Failed == null || !result.Failed.Any()))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing blocks from '{dir}' to '{groupPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcProgramFromDirectory"), Description("[L2][PLC-Software] HIGH-LEVEL batch import tool. Recursively scans a directory for PLC XML files, auto-classifies them as UDT/TagTable/TechnologyObject/Block, rejects duplicate kind/name candidates before import, and optionally compiles. Uses types-first lexical scheduling, not dependency resolution: types, tag tables, technology objects, then blocks by subtype and lexical file path. Requires: Connect + OpenProject. Best for importing a full exported PLC program or a set of generated XML blocks. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportPlcProgramFromDirectoryV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("sourceDir: root directory containing exported PLC XML files")] string sourceDir,
            [Description("typeGroupPath: PLC data type group path; use empty for root")] string typeGroupPath = "",
            [Description("tagFolderPath: PLC tag table group path; use empty for root")] string tagFolderPath = "",
            [Description("technologyFolderPath: PLC technology object group path; use empty for root")] string technologyFolderPath = "",
            [Description("blockGroupPath: PLC block group path; use empty for root Program blocks")] string blockGroupPath = "",
            [Description("regexName: optional regex filter applied to file name without extension")] string regexName = "",
            [Description("compileAfter: compile PLC software after imports")] bool compileAfter = true,
            [Description("stopOnImportFailure: skip remaining imports after first import failure")] bool stopOnImportFailure = false,
            [Description("dryRun: only classify and return discovered objects; do not import or compile")] bool dryRun = false)
            => PlcToolContract.Run("ImportPlcProgramFromDirectory", !dryRun, true, () => ImportPlcProgramFromDirectory(softwarePath, sourceDir, typeGroupPath, tagFolderPath, technologyFolderPath, blockGroupPath, regexName, compileAfter, stopOnImportFailure, dryRun));

        public ResponsePlcProgramImport ImportPlcProgramFromDirectory(
            string softwarePath,
            string sourceDir,
            string typeGroupPath = "",
            string tagFolderPath = "",
            string technologyFolderPath = "",
            string blockGroupPath = "",
            string regexName = "",
            bool compileAfter = true,
            bool stopOnImportFailure = false,
            bool dryRun = false)
        {
            var importedTypes = new List<string>();
            var importedTagTables = new List<string>();
            var importedTechnologyObjects = new List<string>();
            var importedBlocks = new List<string>();
            var failed = new List<ImportFailure>();
            ResponseCompile? compile = null;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    failed.Add(new ImportFailure { Path = sourceDir, Error = "Directory not found" });
                    return PlcProgramImport.BuildPlcProgramImportResponse(sourceDir, dryRun, new List<string>(), new List<string>(), new List<string>(), new List<string>(), importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }

                var sourceRoot = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var files = Directory.GetFiles(sourceRoot, "*.xml", SearchOption.AllDirectories)
                    .Where(f => regex == null || regex.IsMatch(Path.GetFileNameWithoutExtension(f)))
                    .Select(f =>
                    {
                        var kind = PlcProgramImport.ClassifyPlcXml(f, out var subKind, out var objectName);
                        return new { File = f, Kind = kind, SubKind = subKind, ObjectName = objectName };
                    })
                    .Where(x => x.Kind != "unknown")
                    .OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.File, StringComparer.Ordinal)
                    .ToList();

                var discoveredTypes = files.Where(x => x.Kind == "type").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredTagTables = files.Where(x => x.Kind == "tagtable").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredTechnologyObjects = files.Where(x => x.Kind == "technology").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredBlocks = files.Where(x => x.Kind == "block").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                // No native import or compile may run if the selected batch is ambiguous.
                var conflicts = ImportSelectionPolicy.FindConflicts(files, x => x.Kind, x => x.ObjectName, x => x.File.Substring(sourceRoot.Length));
                foreach (var conflict in conflicts.Take(16))
                    failed.Add(new ImportFailure { Path = conflict.Paths[0], Error = conflict.Message });
                if (conflicts.Count > 16)
                    failed.Add(new ImportFailure { Path = ".", Error = $"Duplicate diagnostics truncated: conflictCount={conflicts.Count}, reportedConflicts=16, truncated=true. Entire selected batch rejected before import or compile." });
                if (conflicts.Count > 0)
                {
                    return PlcProgramImport.BuildPlcProgramImportResponse(sourceDir, dryRun, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                if (dryRun)
                {
                    return PlcProgramImport.BuildPlcProgramImportResponse(sourceDir, true, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                foreach (var item in files.Where(x => x.Kind == "type").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.File, StringComparer.Ordinal))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        _session.ImportType(softwarePath, typeGroupPath, item.File);
                        importedTypes.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "tagtable").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.File, StringComparer.Ordinal))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        _session.ImportPlcTagTable(softwarePath, tagFolderPath, item.File);
                        importedTagTables.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "technology").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.File, StringComparer.Ordinal))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        _session.ImportTechnologyObject(softwarePath, technologyFolderPath, item.File);
                        importedTechnologyObjects.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "block")
                                          .OrderBy(x => GetBlockImportOrder(x.SubKind))
                                          .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.File, StringComparer.Ordinal))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        _session.ImportBlock(softwarePath, blockGroupPath, item.File);
                        importedBlocks.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                if (compileAfter && !(stopOnImportFailure && failed.Any()))
                {
                    try
                    {
                        compile = PlcCompilation.BuildCompileResponse(softwarePath, _session.CompileSoftware(softwarePath));
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = softwarePath, Error = $"[{pex.Code}] {pex.Message}" });
                    }
                }

                return PlcProgramImport.BuildPlcProgramImportResponse(sourceDir, false, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                failed.Add(new ImportFailure { Path = sourceDir, Error = ex.ToString() });
                return PlcProgramImport.BuildPlcProgramImportResponse(sourceDir, dryRun, new List<string>(), new List<string>(), new List<string>(), new List<string>(), importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            }
        }

        [McpServerTool(Name = "CompilePlcDiagnostics"), Description("[L1][PLC-Software] PREFERRED compile tool. Compiles PLC and returns structured errors/warnings by recursively walking CompilerResult.Messages (V20/V21 PublicAPI). Leaf diagnostics include Path + Description; Line/Column when exposed as public properties. Requires: Connect + OpenProject. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult CompileAndDiagnosePlcV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("password: optional safety password")] string password = "")
            => PlcToolContract.Run("CompilePlcDiagnostics", true, true, () => CompileAndDiagnosePlc(softwarePath, password));

        public ResponseCompileDiagnose CompileAndDiagnosePlc(
            string softwarePath,
            string password = "")
            => PlcCompilation.CompileAndDiagnoseCore(softwarePath, password);



        [McpServerTool(Name = "RepairAndReimportPlcBlock"), Description("[L2][PLC-Software]Try import a block XML; if compile fails, return diagnostics and best-effort suggestions (no destructive actions). Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult RepairAndReimportBlockV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: block XML path")] string importPath,
            [Description("groupPath: block group path; use empty for root Program blocks")] string groupPath = "",
            [Description("compileAfter: compile PLC after import")] bool compileAfter = true)
            => PlcToolContract.Run("RepairAndReimportPlcBlock", true, true, () => RepairAndReimportBlock(softwarePath, importPath, groupPath, compileAfter));

        public ResponseRepairAndCompile RepairAndReimportBlock(
            string softwarePath,
            string importPath,
            string groupPath = "",
            bool compileAfter = true)
        {
            var suggestions = new List<string>();
            try
            {
                // ImportBlock 以 PortalException 报失败；成功即已导入
                _session.ImportBlock(softwarePath, groupPath, importPath);

                ResponseCompileDiagnose? compile = null;
                if (compileAfter)
                {
                    compile = CompileAndDiagnosePlc(softwarePath);
                    if (compile.Meta?["success"]?.GetValue<bool>() == false)
                    {
                        suggestions.Add("If errors mention missing symbols, ensure PLC tag table/UDTs are imported before blocks.");
                        suggestions.Add("If block/type is inconsistent, compile PLC software once to update consistency before exporting.");
                    }
                }

                return new ResponseRepairAndCompile
                {
                    Message = "Imported (best-effort) and compiled.",
                    Imported = true,
                    ImportError = null,
                    Compile = compile,
                    Suggestions = suggestions,
                    Meta = ResponseMeta.Basic(DateTime.Now, compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false))
                };
            }
            catch (PortalException pex)
            {
                // 导入失败：返回诊断而非抛出（本工具契约是给出修复建议）
                suggestions.Add("If groupPath is wrong, retry with empty groupPath for root Program blocks.");
                return new ResponseRepairAndCompile
                {
                    Message = "Import failed.",
                    Imported = false,
                    ImportError = $"[{pex.Code}] {pex.Message}",
                    Compile = null,
                    Suggestions = suggestions,
                    Meta = ResponseMeta.Basic(DateTime.Now, false)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error repairing/reimporting block '{importPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }





        private static int GetBlockImportOrder(string subKind)
        {
            return subKind switch
            {
                "SW.Blocks.GlobalDB" => 10,
                "SW.Blocks.FC" => 20,
                "SW.Blocks.FB" => 20,
                "SW.Blocks.InstanceDB" => 30,
                "SW.Blocks.OB" => 40,
                _ => 50
            };
        }

        [McpServerTool(Name = "ExportPlcBlocks"), Description("[L2][PLC-Software] Export all (or regexName-filtered) blocks to a directory as SimaticML XML. Pick the right tool: readable SCL/.s7dcl text → ExportPlcBlocksDocuments; a single block → ExportPlcBlock. Current native policy; V4 safety behavior is not yet accepted.")]
        public Task<CallToolResult> ExportBlocksV4(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the blocks")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
            => PlcToolContract.RunAsync("ExportPlcBlocks", true, true, async () => await ExportBlocks(server, context, softwarePath, exportPath, regexName, preservePath));

        public async Task<ResponseExportBlocks> ExportBlocks(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            string softwarePath,
            string exportPath,
            string regexName = "",
            bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context?.Params?.ProgressToken;
            
            try
            {
                // First, get the list of blocks to determine total count
                McpServer.Logger?.LogInformation($"Starting export of blocks from '{softwarePath}' to '{exportPath}'");
                
                var allBlocks = await Task.Run(() => _session.GetBlocks(softwarePath, regexName));
                var totalBlocks = allBlocks?.Count ?? 0;

                if (totalBlocks == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No blocks found to export",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportBlocks
                    {
                        Message = $"No blocks found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseBlockInfo>(),
                        Meta = ResponseMeta.Basic(DateTime.Now, true, ("totalBlocks", 0), ("exportedBlocks", 0), ("duration", (DateTime.Now - startTime).TotalSeconds))
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalBlocks,
                        Message = $"Starting export of {totalBlocks} blocks...",
                        progressToken
                    });
                }

                // Export blocks asynchronously
                var exportedBlocks = await Task.Run(() => _blocks.ExportBlocks(softwarePath, exportPath, regexName, preservePath));

                // Build list of inconsistent (skipped) blocks for reporting
                var inconsistentInfos = new List<ResponseBlockInfo>();
                if (allBlocks != null)
                {
                    foreach (var b in allBlocks)
                    {
                        if (b != null && b.IsConsistent == false)
                        {
                            var attrs = Helper.GetAttributeList(b);
                            inconsistentInfos.Add(new ResponseBlockInfo
                            {
                                Name = b.Name,
                                TypeName = b.GetType().Name,
                                Namespace = b.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), b.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), b.MemoryLayout),
                                IsConsistent = b.IsConsistent,
                                HeaderName = b.HeaderName,
                                ModifiedDate = b.ModifiedDate,
                                IsKnowHowProtected = b.IsKnowHowProtected,
                                Attributes = attrs,
                                Description = b.ToString()
                            });
                        }
                    }
                }
                
                // Send progress update after export completion
                if (exportedBlocks != null && progressToken != null)
                {
                    var exportedCount = exportedBlocks.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalBlocks,
                        Message = $"Exported {exportedCount} of {totalBlocks} blocks",
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
                            Message = $"Export completed: {processedCount} blocks exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    McpServer.Logger?.LogInformation($"Export completed: {processedCount} blocks exported in {duration:F2} seconds");

                    return new ResponseExportBlocks
                    {
                        Message = $"Export completed: {processedCount} blocks with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Inconsistent = inconsistentInfos,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = totalBlocks,
                            ["exportedBlocks"] = processedCount,
                            ["inconsistentBlocks"] = inconsistentInfos.Count,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}", McpErrorCode.InternalError);
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
                            Message = $"Export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
 /* swallow(logging-failure): A failed progress notification must not replace the batch export failure. */                        // Ignore notification errors during error handling
                    }
                }
                
                McpServer.Logger?.LogError(ex, $"Failed exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}");
                throw new McpException($"Unexpected error exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Un-exposed from MCP (tool consolidation): use ExportBlocks with a caller-chosen directory. Method kept (used internally by CLI self-test).
        public ResponseTempExport ExportBlocksToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("regexName: optional regex filter")] string regexName = "",
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = _blocks.ExportBlocksToTemp(softwarePath, regexName, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Blocks exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                throw new McpException("Failed exporting blocks to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting blocks to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        [McpServerTool(Name = "DescribePlcBlockLogic"), Description("[L1][PLC-Software] Read a block's LOGIC as READABLE TEXT — the fast, accurate way to analyze LADDER (LAD) without hand-parsing FlgNet XML. For each LAD network it reconstructs the power flow as a boolean-ish expression (series = ' · ', parallel = ' + '), shows coils ( )/(S)/(R), MOVE/compare/timer boxes with their operands, and — critically — FLAGS any contact whose operand is a LITERAL CONSTANT with a literal-constant marker (a normally-open contact wired to FALSE silently disables its whole rung; this is nearly impossible to spot by eye). SCL/STL networks are rendered inline as code. Use this to understand or review LAD logic before editing. Requires: Connect + OpenProject + the block consistent (compile first if IsConsistent=false; export does not work in online mode — GoOffline first). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DescribeBlockLogicV4(
            [Description("softwarePath: PLC software path, e.g. '5T车' or 'PLC_1' (from GetProjectTree)")] string softwarePath,
            [Description("blockPath: fully qualified 'Group/Subgroup/Name' from GetSoftwareTree")] string blockPath)
            => PlcToolContract.Run("DescribePlcBlockLogic", false, true, () => DescribeBlockLogic(softwarePath, blockPath));

        public ResponseBlockLogic DescribeBlockLogic(
            string softwarePath,
            string blockPath)
        {
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpLogic_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);

                var block = _session.ExportBlock(softwarePath, blockPath, tempDir);
                if (block == null)
                {
                    throw new McpException($"Could not export '{blockPath}' from '{softwarePath}' for analysis. If IsConsistent=false, compile first; if online, GoOffline first; verify the path with GetSoftwareTree.", McpErrorCode.InternalError);
                }

                var xmlFile = new DirectoryInfo(tempDir).GetFiles("*.xml")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (xmlFile == null)
                {
                    throw new McpException($"Export of '{blockPath}' produced no XML to analyze.", McpErrorCode.InternalError);
                }

                var xml = File.ReadAllText(xmlFile.FullName);
                var readable = LadTextRenderer.Render(xml);
                var lang = block.ProgrammingLanguage.ToString();

                return new ResponseBlockLogic
                {
                    BlockPath = blockPath,
                    Language = lang,
                    Readable = readable,
                    Message = $"Logic of '{block.Name}' [{lang}] decoded. Series contacts joined with ' · ', parallel branches with ' + '; '⟨常量⟩' marks a contact wired to a literal constant (disabled/forced rung).",
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("language", lang))
                };
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpException($"DescribePlcBlockLogic failed for '{blockPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch {  /* swallow(cleanup): Temporary logic export cleanup must not replace the analysis result or export error. */}
            }
        }

        #region block import verification

    /// <summary>
    /// Partial: 导入后的读回校验。
    ///
    /// 判据是 **XML 里声明的块名 + 块编号**，不是 XML 文件名。两个原因：
    /// 1. 文件名 ≠ 块名。OB100 在 TIA 里默认叫 Startup，把它的 XML 存成 OB100.xml
    ///    导进去，按文件名去比就会把**一次成功的导入**报成「NOT found after import」，
    ///    调用方于是去重导、去排查一个根本不存在的问题。
    /// 2. 反过来，块若被静默降级（.s7dcl 导 OB 会全变成 OB1），光比名字也可能"对上"，
    ///    只有块编号抓得住。
    /// </summary>
        /// 导入后校验：判据是 XML 里声明的块名 + 块编号，而不是 XML 文件名。
        /// 校验过程本身出错（没连接、代理失效、XML 读不出块名）一律记 Unknown，
        /// 既不冒充导入失败，也不冒充校验通过。
        /// </summary>
        internal PlcBlockVerificationOutcome VerifyImportedBlock(string softwarePath, string xmlPath)
        {
            PlcBlockAttributeSnapshot? expected;
            try
            {
                expected = ReadExpectedBlockFromXml(System.IO.File.ReadAllText(xmlPath));
            }
            catch (Exception ex)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Unknown,
                    $"could not read the generated XML back for verification ({ex.Message})");
            }

            if (expected == null)
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Unknown,
                    "the generated XML declares no AttributeList/Name, so there is nothing to verify against");

            PlcBlockAttributeSnapshot? actual;
            try
            {
                actual = ReadBackPlcBlockSnapshot(softwarePath, expected);
            }
            catch (Exception ex)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Unknown,
                    $"read-back of block '{expected.Name}' failed ({ex.Message})");
            }

            return CompareBlockSnapshots(expected, actual, System.IO.Path.GetFileNameWithoutExtension(xmlPath));
        }

        /// <summary>
        /// 导入后按 "XML 里声明的块名 + 块编号" 读回一个块。
        /// 名字找不到就按编号在全量块里兜底 —— OB 的名字可以被工程改掉，编号不会。
        /// </summary>
        internal PlcBlockAttributeSnapshot? ReadBackPlcBlockSnapshot(string softwarePath, PlcBlockAttributeSnapshot expected)
        {
            var escaped = Regex.Escape(expected.Name);
            var found = _session.GetBlocks(softwarePath, $"^{escaped}$");
            if (found == null || found.Count == 0)
                found = _session.GetBlocks(softwarePath, escaped);

            PlcBlock? hit = found?.FirstOrDefault();

            if (hit == null && expected.Number.HasValue)
            {
                var all = _session.GetBlocks(softwarePath, "");
                hit = all?.FirstOrDefault(b => SafeNumber(b) == expected.Number.Value);
            }

            return hit == null ? null : SnapshotOf(hit);
        }

        /// <summary>
        /// 从 SimaticML 文档里读出 "我打算导入的到底是什么"。
        /// 块名取 AttributeList/Name（不是文件名），编号取 Number，OB 再多带一个 SecondaryType。
        /// </summary>
        internal static PlcBlockAttributeSnapshot? ReadExpectedBlockFromXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            var doc = XDocument.Parse(xml);
            var obj = doc.Root?.Elements().FirstOrDefault(e =>
                e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
            var attrs = obj?.Element("AttributeList");
            if (attrs == null)
                return null;

            var name = attrs.Element("Name")?.Value?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
                return null;

            return new PlcBlockAttributeSnapshot
            {
                Name = name,
                Number = ParseIntOrNull(attrs.Element("Number")?.Value),
                SecondaryType = NullIfBlank(attrs.Element("SecondaryType")?.Value),
                // SimaticML 的 OB 导出里没有 PriorityNumber —— 真实导出对拍过，这里读不到是正常的。
                PriorityNumber = ParseIntOrNull(attrs.Element("PriorityNumber")?.Value),
                BlockKind = obj!.Name.LocalName
            };
        }

        /// <summary>
        /// 逐属性比对。任何一项对不上都要点名，不能只给一个总的布尔值。
        /// 读不到的属性（比如 Openness 不暴露 PriorityNumber）不算不相等，
        /// 但要在说明里出现，否则调用方会把 "没验" 当成 "验过了"。
        /// </summary>
        internal static PlcBlockVerificationOutcome CompareBlockSnapshots(
            PlcBlockAttributeSnapshot expected,
            PlcBlockAttributeSnapshot? actual,
            string importFileNameWithoutExtension = "")
        {
            var fileNameHint = BuildFileNameHint(expected, importFileNameWithoutExtension);

            if (actual == null)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Mismatch,
                    $"block '{expected.Name}'"
                    + (expected.Number.HasValue ? $" (number {expected.Number.Value})" : "")
                    + " NOT found after import" + fileNameHint);
            }

            var mismatches = new List<string>();
            var unavailable = new List<string>();

            if (actual.Name == null)
                unavailable.Add("Name");
            else if (!string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"Name expected '{expected.Name}' actual '{actual.Name}'");

            if (!expected.Number.HasValue)
                unavailable.Add("Number (not declared in the imported XML)");
            else if (!actual.Number.HasValue)
                unavailable.Add("Number (not exposed on read-back)");
            else if (expected.Number.Value != actual.Number.Value)
                mismatches.Add($"Number expected '{expected.Number.Value}' actual '{actual.Number.Value}'");

            if (!string.IsNullOrEmpty(expected.SecondaryType))
            {
                if (string.IsNullOrEmpty(actual.SecondaryType))
                    unavailable.Add("SecondaryType (not exposed on read-back)");
                else if (!string.Equals(expected.SecondaryType, actual.SecondaryType, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add($"SecondaryType expected '{expected.SecondaryType}' actual '{actual.SecondaryType}'");
            }

            // PriorityNumber 走 XML 往返必丢：导出文档里没有这一项，读回侧也常常不暴露。
            // 所以只有两边都拿得到值时才真比，否则记 unavailable。
            if (expected.PriorityNumber.HasValue && actual.PriorityNumber.HasValue)
            {
                if (expected.PriorityNumber.Value != actual.PriorityNumber.Value)
                    mismatches.Add($"PriorityNumber expected '{expected.PriorityNumber.Value}' actual '{actual.PriorityNumber.Value}'");
            }
            else
            {
                unavailable.Add("PriorityNumber (SimaticML block export does not carry it; set/check it in TIA)");
            }

            if (mismatches.Count > 0)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Mismatch,
                    $"block '{actual.Name}' found but attribute mismatch: " + string.Join("; ", mismatches) + fileNameHint);
            }

            var detail = $"block '{actual.Name}'"
                + (actual.Number.HasValue ? $" (number {actual.Number.Value})" : "")
                + " present after import; attributes match" + fileNameHint
                + (unavailable.Count > 0 ? $"; not verifiable via XML round-trip: {string.Join(", ", unavailable)}" : "");

            return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Verified, detail);
        }

        private static string BuildFileNameHint(PlcBlockAttributeSnapshot expected, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            if (string.Equals(fileName, expected.Name, StringComparison.OrdinalIgnoreCase)) return "";

            // 文件名叫 OB100、块名叫 Startup，两者本来就不该相等 —— 按文件名比会把成功误判成失败。
            return $" (file name '{fileName}' differs from the block name '{expected.Name}' declared in the XML — "
                 + "normal for OBs, so the block number is the authoritative check)";
        }

        private static PlcBlockAttributeSnapshot SnapshotOf(PlcBlock block)
        {
            var snapshot = new PlcBlockAttributeSnapshot
            {
                Name = block.Name,
                Number = SafeNumber(block),
                BlockKind = block.GetType().Name
            };

            if (block is OB ob)
            {
                try { snapshot.SecondaryType = ob.SecondaryType; } catch {  /* swallow(probe-optional): 代理失效时将 SecondaryType 标为 unavailable，不能谎报不相等。 *//* 代理失效时宁可标 unavailable，也不谎报不相等 */ }
            }

            snapshot.PriorityNumber = TryReadPriority(block);
            return snapshot;
        }

        /// <summary>
        /// 优先级在 .NET API 上没有属性（V21 反射确认：OB 只有 Name/Number/SecondaryType 等），
        /// 只可能作为动态属性出现。所以按属性名去问，问不到就返回 null（记 unavailable），不猜。
        /// </summary>
        private static int? TryReadPriority(PlcBlock block)
        {
            try
            {
                if (block is not IEngineeringObject eo) return null;
                var info = eo.GetAttributeInfos()
                    .FirstOrDefault(a => a.Name.IndexOf("Priority", StringComparison.OrdinalIgnoreCase) >= 0);
                if (info == null) return null;
                var value = eo.GetAttribute(info.Name);
                return value == null ? null : Convert.ToInt32(value);
            }
            catch
            {
 /* swallow(probe-optional): Unreadable optional OB priority remains unavailable for verification. */                return null;
            }
        }

        private static int? SafeNumber(PlcBlock block)
        {
            try { return block.Number; } catch {  /* swallow(probe-optional): Unreadable block number remains unavailable rather than reporting a mismatch. */return null; }
        }

        private static int? ParseIntOrNull(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return int.TryParse(value!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        #endregion

        [McpServerTool(Name = "ManagePlcBlockProtection"), Description("[L2][PLC-Software][WRITE] Read/protect/unprotect know-how protection of one exact block path via native PlcBlockProtectionProvider. Password is passed straight to TIA and never stored or logged; native invalid-password characters are reported. Refuses protecting an already protected block or unprotecting an unprotected one. Default preview; real change requires dryRun=false AND confirmProtectionChange=true, an Offline PLC, and is verified by IsKnowHowProtected readback. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcBlockProtectionV4(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | protect | unprotect.")] string action,
            string password="",
            [Description("confirmProtectionChange: must be true together with dryRun=false to change the protection.")] bool confirmProtectionChange=false,
            bool dryRun=true)
            => PlcToolContract.Run("ManagePlcBlockProtection", !dryRun && action != "read", true, () => ManagePlcBlockProtection(softwarePath, blockPath, action, password, confirmProtectionChange, dryRun));

        public ResponseMessage ManagePlcBlockProtection(
            string softwarePath,
            string blockPath,
            string action,
            string password="",
            bool confirmProtectionChange=false,
            bool dryRun=true)
            => _blocks.ManagePlcBlockProtection(softwarePath,blockPath,action,password,confirmProtectionChange,dryRun);
        [McpServerTool(Name = "ManagePlcDataBlockSnapshot"), Description("[L2][PLC-Online][ONLINE] read/createSnapshot/loadSnapshotAsActualValues/loadStartValuesAsActualValues/exportSnapshot on one exact data block via native ValueService/InterfaceSnapshot. Siemens semantics: createSnapshot reads actual values from the CPU and load actions write values INTO the running CPU, so the PLC must already be online in TIA; this tool never goes online/offline. Load actions require confirmValueChange=true besides dryRun=false; no value readback exists, only native return. exportSnapshot writes a NEW absolute file and returns its SHA-256. ValueService is V21+ (V20 returns NotSupported). Default preview; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcDataBlockSnapshotV4(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | createSnapshot | loadSnapshotAsActualValues | loadStartValuesAsActualValues | exportSnapshot.")] string action,
            string filePath="",
            [Description("confirmValueChange: must be true together with dryRun=false to change data block values.")] bool confirmValueChange=false,
            bool dryRun=true)
            => PlcToolContract.Run("ManagePlcDataBlockSnapshot", !dryRun && action != "read", true, () => ManagePlcDataBlockSnapshot(softwarePath, blockPath, action, filePath, confirmValueChange, dryRun));

        public ResponseMessage ManagePlcDataBlockSnapshot(
            string softwarePath,
            string blockPath,
            string action,
            string filePath="",
            bool confirmValueChange=false,
            bool dryRun=true)
            => _blocks.ManagePlcDataBlockSnapshot(softwarePath,blockPath,action,filePath,confirmValueChange,dryRun);
        [McpServerTool(Name = "SetPlcProgram"), Description("[L2][PLC-Software][WRITE] Native PlcSoftware.UpdateProgram() (TIA 'Update program') for one exact PLC software path. Returns void natively; PLC scalars before/after are reported, program content changes are not enumerated. Default preview; real execution requires dryRun=false AND confirmUpdate=true and an Offline PLC. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult UpdatePlcProgramV4(
            string softwarePath,
            [Description("confirmUpdate: must be true together with dryRun=false to update the program.")] bool confirmUpdate=false,
            bool dryRun=true)
            => PlcToolContract.Run("SetPlcProgram", !dryRun, true, () => UpdatePlcProgram(softwarePath, confirmUpdate, dryRun));

        public ResponseMessage UpdatePlcProgram(
            string softwarePath,
            bool confirmUpdate=false,
            bool dryRun=true)
            => _blocks.UpdatePlcProgram(softwarePath,confirmUpdate,dryRun);
        [McpServerTool(Name = "GetPlcBlockFingerprints"), Description("[L2][PLC-Online][ONLINE] Read fingerprint data (identifier/value pairs) from the CPU via native FingerprintDataProvider.GetFingerprintData using the configured route whose CPU address exactly equals targetIpAddress; several PG/PC adapters to that address require the exact pgPcInterface name. Optional CPU password is passed only to the native legitimation callback. Paginated offset/limit<=500. Default preview resolves the route without contacting the PLC; dryRun=false contacts the PLC read-only. No project or PLC change. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcBlockFingerprintsV4(
            string softwarePath,
            [Description("targetIpAddress: IP address of the PLC as ScanAccessibleDevices lists it.")] string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true)
            => PlcToolContract.Run("GetPlcBlockFingerprints", false, true, () => ReadPlcBlockFingerprints(softwarePath, targetIpAddress, pgPcInterface, password, offset, limit, dryRun));

        public ResponseMessage ReadPlcBlockFingerprints(
            string softwarePath,
            string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true)
            => _blocks.ReadPlcBlockFingerprints(softwarePath,targetIpAddress,pgPcInterface,password,offset,limit,dryRun);

        [McpServerTool(Name = "GetPlcBlockEditCapabilities"), Description("[L2][Validation][READ] Inspect one SimaticML PLC block file, or export an exact live block, to report each network's own language/source shape, existing multilingual title/comment targets, library binding and document fingerprint. Exactly one filePath or blockPath (+softwarePath). Describes the supported offline patch operations; never claims a generic rung editor or target CPU/native import validation. No compile/save/write to project. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcBlockEditCapabilitiesV4(
            [Description("Absolute exported SimaticML XML; mutually exclusive with blockPath.")] string filePath = "",
            [Description("Exact PLC software path when exporting a live block.")] string softwarePath = "",
            [Description("Exact group-qualified live block path; empty for file-only mode.")] string blockPath = "")
            => PlcToolContract.Run("GetPlcBlockEditCapabilities", false, true, () => ReadPlcBlockEditCapabilities(filePath, softwarePath, blockPath));

        public ResponseMessage ReadPlcBlockEditCapabilities(
            string filePath = "",
            string softwarePath = "",
            string blockPath = "")
            => OfflineToolExecution.RunOfflineAnalysisTool("GetPlcBlockEditCapabilities", meta => {
                string? temp = null;
                try {
                    meta["offlineOnly"] = !string.IsNullOrWhiteSpace(filePath);
                    var path = OfflineToolExecution.ResolveCompareSide("document", filePath, blockPath, softwarePath, meta, out temp);
                    meta["data"] = PlcDocumentEditing.Inspect(PlcDocumentEditing.Read(path));
                    return "Exported block editing capabilities inspected. Network indexes are zero-based; native import remains unverified.";
                } finally { OfflineToolExecution.DeleteAnalysisTempDir(temp); }
            });

        [McpServerTool(Name = "AnalyzePlcReferences"), Description("[L2][Validation][OFFLINE] Query supplied exports without native CrossReferenceService. action summary/callers/callees/callPaths/unreachable/references. Recursively indexes single-block SimaticML XML (max 2000 files / 64 MiB); SCL/S7DCL and invalid files are reported unindexed. target is exact block name or relative file ID; references takes a quoted component path e.g. \"Motor\".\"Run\". Uses explicit CallInfo and global Symbol nodes only. Reports missing/ambiguous callees, cycles, truncation and failures; coverageComplete and safeToDelete are always false. An empty result is not proof of no references. No TIA calls or writes.")]
        public CallToolResult AnalyzePlcReferencesV4(
            [Description("Absolute directory of exports from one PLC/scope, scanned recursively.")] string directory,
            [Description("summary | callers | callees | callPaths | unreachable | references. summary, callers, callees, callPaths, unreachable or references.")] string action = "summary",
            [Description("Exact block name or relative file ID; references uses quoted symbol components.")] string target = "",
            [Description("Maximum call-path edge depth, 1..50.")] int maxDepth = 10,
            [Description("Zero-based result offset.")] int offset = 0,
            [Description("Page size, 1..500.")] int limit = 100)
            => PlcToolContract.Run("AnalyzePlcReferences", false, false, () => AnalyzePlcReferences(directory, action, target, maxDepth, offset, limit));

        public ResponseMessage AnalyzePlcReferences(
            string directory,
            string action = "summary",
            string target = "",
            int maxDepth = 10,
            int offset = 0,
            int limit = 100)
            => OfflineToolExecution.RunOfflineAnalysisTool("AnalyzePlcReferences", meta => {
                meta["data"] = PlcOfflineReferences.Analyze(directory, action, target, maxDepth, offset, limit);
                meta["dataComplete"] = false;
                return "Partial offline reference query completed. Inspect failures, unresolvedCalls and scope; never use it alone to justify deletion.";
            });

        [McpServerTool(Name = "PatchPlcBlockDocument"), Description("[L2][PLC-Builders][FILE] Patch an exported single-block SimaticML file into a NEW file, preserving all unrelated XML. Requires expectedFingerprint from GetPlcBlockEditCapabilities. changes array: setBlockText(field Title/Comment,culture,expectedValue,value), setNetworkText(same plus zero-based networkIndex), setMemberStartValue(section,memberPath slash-separated,expectedValue,value). Only existing scalar StartValue or existing multilingual entries; no guessed cultures/defaults, library-connected blocks or read-only members. Default dryRun=true previews; false writes outputPath. No native calls/import/compile; input remains unchanged.")]
        public CallToolResult PatchPlcBlockDocumentV4(
            [Description("Absolute existing single-block SimaticML XML file.")] string filePath,
            [Description("JSON array of objects with action=setBlockText/setNetworkText/setMemberStartValue, exact target fields, expectedValue and value. The selector key is action. Example: [{\"action\":\"setBlockText\",\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"Old title\",\"value\":\"Example\"}].")] BlockEdit[] changes,
            [Description("documentFingerprint returned by GetPlcBlockEditCapabilities for this input.")] string expectedFingerprint,
            [Description("New absolute output file; existing parent directory required when dryRun=false.")] string outputPath = "",
            [Description("Preview only by default; false writes a new file, never imports into TIA.")] bool dryRun = true)
            => PlcToolContract.Run("PatchPlcBlockDocument", !dryRun, false, () =>
                PatchPlcBlockDocument(filePath, V4Json.Serialize(changes), expectedFingerprint, outputPath, dryRun));

        public ResponseMessage PatchPlcBlockDocument(
            string filePath,
            string changesJson,
            string expectedFingerprint,
            string outputPath = "",
            bool dryRun = true)
            => OfflineToolExecution.RunOfflineAnalysisTool("PatchPlcBlockDocument", meta => {
                var edited = PlcDocumentEditing.Patch(PlcDocumentEditing.Read(filePath), changesJson, expectedFingerprint);
                meta["dryRun"] = dryRun; meta["result"] = PlcDocumentEditing.Inspect(edited);
                meta["nativeImportValidated"] = false;
                if (!dryRun) {
                    if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("New absolute outputPath required.");
                    var output = Siemens.NativeFileOutput.Plan(outputPath);
                    using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true))) writer.Write(edited);
                    meta["output"] = Siemens.NativeFileOutput.Verify(output);
                }
                return dryRun ? "Offline patch preview validated. No file or project changed." : "Patched document written; native import/compilation remain unverified.";
            });

        [McpServerTool(Name = "ImportPlcBlockVerified"), Description("[L2][PLC-Software][WRITE] Overwrite one existing non-library PLC block in its exact current user group with a reviewed SimaticML file. Default dryRun=true exports a retained backup, preserves omitted scalar block attributes and returns planned.xml plus binding/content token. Execution requires SAME arguments and expectedToken; re-exports current block, rechecks binding/content before import, then re-exports for strict document verification preserving wiring and literal values. Requires Offline and consistent exports. Refuses library-bound blocks, InstanceDB, changed name/type/number/layout/language and empty replacement logic. Missing interface members/networks still mean deletion. compileAfterImport=false by default; true explicitly compiles the imported block before readback, rejecting compile errors. Without compile, inconsistent readback remains failed/unverified. No save/download/rollback/retry; failures may leave changes. Native V20/V21 execution not yet validated. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportPlcBlockVerifiedV4(
            [Description("Exact PLC software path.")] string softwarePath,
            [Description("Exact existing block path relative to Program blocks, e.g. Pumps/FB_Pump.")] string blockPath,
            [Description("Absolute reviewed single-block SimaticML XML; name must equal the target.")] string importPath,
            [Description("Absolute local folder for retained before/planned/after evidence, one new subdirectory per invocation.")] string evidenceDirectory,
            [Description("True exports a backup and preview only; false attempts the native import.")] bool dryRun = true,
            [Description("Token returned by the matching preview; binds session, project, target, current document, candidate and compile option.")] string expectedToken = "",
            [Description("Explicitly compile only the imported block before readback; default false. Compilation is never run in preview.")] bool compileAfterImport = false)
            => PlcToolContract.Run("ImportPlcBlockVerified", !dryRun, true, () => ImportPlcBlockVerified(softwarePath, blockPath, importPath, evidenceDirectory, dryRun, expectedToken, compileAfterImport));

        public ResponseMessage ImportPlcBlockVerified(
            string softwarePath,
            string blockPath,
            string importPath,
            string evidenceDirectory,
            bool dryRun = true,
            string expectedToken = "",
            bool compileAfterImport = false)
            => _blocks.ImportPlcBlockVerified(softwarePath, blockPath, importPath, evidenceDirectory, dryRun, expectedToken, compileAfterImport);

        #region delete blocks / tag tables / types

        [McpServerTool(Name = "DeletePlcBlock"), Description(
            "[L2][PLC-Software][WRITE] Preview or delete exactly one PLC block by its exact path, including "
            + "blocks inside nested groups. THIS IS ALSO THE TOOL FOR DELETING A DATA BLOCK: global DB, instance DB, "
            + "ARRAY DB, FB, FC and OB are all PLC blocks, so there is no separate DeleteGlobalDb / DeleteDb / "
            + "DeleteFunctionBlock tool - use this one. Defaults to dryRun=true, which changes nothing and reports "
            + "the resolved target (pinned block number, warnings). Native cross references are disabled by default at the server-process level; see GetPlcCrossReferences. They also require "
            + "crossReferences=true: on the maintainer's real project (2026-09-21) that CrossReferenceService query took "
            + "TIA Portal V21 down during a dry run, so it is off by default and the response says 'not queried' - never "
            + "read that as 'nobody uses it'. It never deletes instance DBs or callers automatically. Before dryRun=false, "
            + "back the block up with ExportPlcBlockDocuments and review dependencies (an unavailable query does not prove it is unused); compile with "
            + "CompilePlcSoftware after deletion and before SaveProject. Regex and wildcards are rejected. To delete a tag "
            + "table use DeletePlcTagTable, a UDT use DeletePlcType. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeletePlcBlockV4(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockPath: exact block path, e.g. 'DB_Test' or 'GroupA/FB_Motor'. Regex and wildcards are rejected.")] string blockPath,
            [Description("dryRun: true (default) only resolves and reports the target; false performs Delete() and verifies the block is absent")] bool dryRun = true,
            [Description("crossReferences: false (default) skips native references; true requests them but does not bypass the default-disabled process policy. Controlled diagnosis requires TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1 on the server; do not enable it automatically. Uncompiled or unreadable block state still refuses. Native queries have terminated TIA Portal V21; compiling does not guarantee safety. Inspect crossReferenceQueried and crossReferenceUnavailableReason; not queried never means unused.")] bool crossReferences = false)
            => PlcToolContract.Run("DeletePlcBlock", !dryRun, true, () => DeletePlcBlock(softwarePath, blockPath, dryRun, crossReferences));

    /// <summary>
    /// Partial: 删除族 —— 程序块（含全局 DB / 背景 DB / FB / FC / OB）、PLC 变量表、用户数据类型。
    /// 三个工具一个规格：dryRun 默认 true、路径必须精确、预览要把代价说清楚、
    /// 实删之后必须读回确认对象真的不在了。
    ///
    /// 关于「成功」的口径（这一族最容易骗人的地方）：
    /// 本线的 ResponseMessage 只有 Message/Meta，没有三态 Outcome，所以「删成功」和
    /// 「删是删了，但关键判据没拿到」只能靠 Message + Warnings 区分。规矩定死：
    ///   · Portal 抛异常 → 这里转成 McpException，**绝不**吞成一条空 Message 报成功；
    ///   · 删完回读没确认对象消失 → Ok=false + 消息里明写「未验证」，不算成功；
    ///   · dryRun 预览里交叉引用取不到 → Ok 仍为 true（预览本身是做成了的），
    ///     但消息与 Warnings 必须写明「查不到 ≠ 没人用」，免得被读成「确认可以删」。
    /// </summary>
        public ResponseJsonReport DeletePlcBlock(
            string softwarePath,
            string blockPath,
            bool dryRun = true,
            bool crossReferences = false)
        {
            try
            {
                var data = _blocks.DeletePlcBlock(softwarePath, blockPath, dryRun, crossReferences);
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;
                int? pinned = data["pinnedBlockNumber"]?.GetValue<int>();

                // 🔴 显式块号是**在删除这一刻**丢的，事后再提醒已经晚了：
                // 把 FB 钉成 103（AutoNumber=false）→ 删掉 → 从同一份外部源重建 →
                // 新块拿到自动分配的号，103 一去不返，依赖它的实例 DB 关联随之断裂，全程无报错。
                // 「不删、直接对已有块重新生成」是安全的，编号原样保留 —— 所以这里要说的不是
                // 「别删」，而是「删了就拿不回来，想保号就别删」。
                string? pinnedWarning = pinned == null ? null
                    : $"该块钉着显式块号 {pinned}（AutoNumber=false）。"
                      + (dryRun ? "一旦真的删除，这个号就没了 —— " : "这个号已经随块一起没了 —— ")
                      + "从外部源重建时新块会拿到自动分配的号，依赖原块号的实例 DB 关联会断，且不会有任何报错。"
                      + "若只是想更新块内容，请不要删，直接对已有块 GenerateBlocksFromExternalSource，编号会保留。"
                      + $"确实要删并重建的话，重建后用 InvokeObject 把号改回去：SetAttribute(\"AutoNumber\", false) 然后 SetAttribute(\"Number\", {pinned})。";

                return BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"程序块 '{data["resolvedBlockPath"]}'",
                    dryRunTail: "确认无误后用 dryRun=false 实际删除。",
                    extraWarning: pinnedWarning,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcBlockDocuments - back up this block before deletion.",
                            "GetPlcCrossReferences - inspect each remaining caller.",
                            "确认后再 DeletePlcBlock(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompilePlcSoftware to detect dangling caller references",
                            "SaveProject —— 确认无误后再存盘"
                        });
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC block '{blockPath}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC block '{blockPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcTagTable"), Description(
            "[L2][PLC-Software][WRITE] Preview or delete ONE PLC tag table (variable table / tag list) "
            + "by name, including tables nested in user groups. Defaults to dryRun=true, which only reports what "
            + "the table contains and deletes nothing. DANGER: deleting a tag table removes the SYMBOLS of every "
            + "tag in it. HMI panels bind PLC tags by symbolic name, so the PLC may still compile clean while the "
            + "HMI silently loses its bindings - always review the previewed tag list first. Native cross references are disabled by default at the server-process level; see GetPlcCrossReferences. They are "
            + "queried only with crossReferences=true (the same TIA CrossReferenceService that took TIA Portal V21 down "
            + "during a DeletePlcBlock dry run on the maintainer's project, 2026-09-21) and may be unavailable at "
            + "tag-table level anyway; the response says explicitly whether they were queried and obtained. Regex and "
            + "wildcards are rejected. Back up first with ExportPlcTagTable. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeletePlcTagTableV4(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tagTableName: bare table name, or the group-qualified path from ListPlcTagTables (e.g. 'Drives/VFD tags'). Regex and wildcards are rejected.")] string tagTableName,
            [Description("dryRun: true (default) only resolves the table and lists its contents; false performs Delete() and verifies the table is absent")] bool dryRun = true,
            [Description("crossReferences: false (default) skips native references; true requests them but does not bypass the default-disabled process policy. Controlled diagnosis requires TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1 on the server; do not enable it automatically. Uncompiled or unreadable block state still refuses. Native queries have terminated TIA Portal V21; compiling does not guarantee safety. Inspect crossReferenceQueried and crossReferenceUnavailableReason; not queried never means unused.")] bool crossReferences = false)
            => PlcToolContract.Run("DeletePlcTagTable", !dryRun, true, () => DeletePlcTagTable(softwarePath, tagTableName, dryRun, crossReferences));

        public ResponseJsonReport DeletePlcTagTable(
            string softwarePath,
            string tagTableName,
            bool dryRun = true,
            bool crossReferences = false)
        {
            try
            {
                var data = _blocks.DeletePlcTagTable(softwarePath, tagTableName, dryRun, crossReferences);
                int tagCount = data["tagCount"]?.GetValue<int>() ?? 0;
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;

                var report = BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"变量表 '{data["resolvedTagTablePath"]}'（{tagCount} 个变量）",
                    dryRunTail: "确认无误后用 dryRun=false 实际删除。",
                    extraWarning: null,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcTagTable —— 删之前先把这张表导出备份",
                            "Use GetPlcCrossReferences on the related blocks; table-level references may be unavailable.",
                            "确认后再 DeletePlcTagTable(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompilePlcSoftware to detect broken PLC references",
                            "⚠️ HMI 侧的符号绑定编译查不出来，请单独核对画面变量",
                            "SaveProject —— 确认无误后再存盘"
                        });

                report.Meta!["tagCount"] = tagCount;
                return report;
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC tag table '{tagTableName}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcType"), Description(
            "[L2][PLC-Software][WRITE] Preview or delete ONE PLC user data type (UDT / PlcType) by its exact "
            + "path. Defaults to dryRun=true. Deleting a UDT breaks every DB and block interface declared with it; "
            + "native cross references are disabled by default at the server-process level (see GetPlcCrossReferences) and also require crossReferences=true (the TIA CrossReferenceService query "
            + "took TIA Portal V21 down during a DeletePlcBlock dry run on the maintainer's project, 2026-09-21), so "
            + "review dependencies before deletion; an unavailable query is not evidence it is unused. Regex and wildcards are rejected. Export the type "
            + "first with ExportPlcType, and CompilePlcSoftware afterwards. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeletePlcTypeV4(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("typePath: exact UDT path, e.g. 'UDT_Motor' or 'GroupA/UDT_Motor'. Regex and wildcards are rejected.")] string typePath,
            [Description("dryRun: true (default) only resolves the type; false performs Delete() and verifies the type is absent")] bool dryRun = true,
            [Description("crossReferences: false (default) skips native references; true requests them but does not bypass the default-disabled process policy. Controlled diagnosis requires TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1 on the server; do not enable it automatically. Uncompiled or unreadable block state still refuses. Native queries have terminated TIA Portal V21; compiling does not guarantee safety. Inspect crossReferenceQueried and crossReferenceUnavailableReason; not queried never means unused.")] bool crossReferences = false)
            => PlcToolContract.Run("DeletePlcType", !dryRun, true, () => DeletePlcType(softwarePath, typePath, dryRun, crossReferences));

        public ResponseJsonReport DeletePlcType(
            string softwarePath,
            string typePath,
            bool dryRun = true,
            bool crossReferences = false)
        {
            try
            {
                var data = _blocks.DeletePlcType(softwarePath, typePath, dryRun, crossReferences);
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;

                return BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"UDT '{typePath}'",
                    dryRunTail: "确认无误后用 dryRun=false 实际删除。",
                    extraWarning: null,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcType: back up this UDT before deletion",
                            "GetPlcCrossReferences - inspect DBs and blocks using this data type.",
                            "确认后再 DeletePlcType(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompilePlcSoftware to detect DBs or blocks with missing type definitions",
                            "SaveProject —— 确认无误后再存盘"
                        });
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC type '{typePath}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC type '{typePath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        /// <summary>
        /// 三个删除工具共用的成品报告。抽出来是因为「什么算成功」这条口径必须三处完全一致 ——
        /// 分开写迟早会有一处漏掉「回读未确认」或「交叉引用查不到」的措辞，而那正是这一族的命门。
        /// </summary>
        private static ResponseJsonReport BuildDeletionReport(
            JsonObject data, bool dryRun, bool crossRefOk,
            string objectLabel, string dryRunTail, string? extraWarning, JsonArray nextActions)
        {
            bool deleted = data["deleted"]?.GetValue<bool>() ?? false;
            bool verifiedAbsent = data["verifiedAbsent"]?.GetValue<bool>() ?? false;

            var warnings = new List<string>();
            if (data["warnings"] is JsonArray raw)
            {
                warnings.AddRange(raw.Select(w => w?.GetValue<string>()).Where(w => w != null)!);
            }
            if (extraWarning != null) warnings.Add(extraWarning);

            string message;
            bool ok;
            if (dryRun)
            {
                // 预览路径：工程一行没动。交叉引用是预览的全部价值，取不到就必须明说，
                // 否则「成功」会被读成「确认可以删」—— 删除类工具里这是代价最大的错档。
                ok = true;
                bool queried = data["crossReferenceQueried"]?.GetValue<bool>() ?? true;
                message = $"[dryRun] 未做任何改动。目标 {objectLabel}，"
                        + (crossRefOk
                            ? $"交叉引用 {data["crossReferenceCount"]} 条（见 data.crossReferences）。"
                            : queried
                                ? "⚠️ 交叉引用查不到 —— 这不等于没人引用它，请先自行核对。"
                                : "交叉引用未查询（" + (data["crossReferenceUnavailableReason"]?.GetValue<string>() ?? "crossReferences=false，默认")
                                    + "）—— 这不等于没人引用它。")
                        + dryRunTail;
            }
            else if (deleted && verifiedAbsent)
            {
                ok = true;
                message = $"{objectLabel} 已删除，并已重新读回确认它确实不在了。";
            }
            else
            {
                // 走到这里说明 Delete() 调过但回读没能确认对象消失。绝不当成功报。
                ok = false;
                message = $"⚠️ 未验证：{objectLabel} 的删除结果无法确认（deleted={deleted}, verifiedAbsent={verifiedAbsent}）。"
                        + "请在 TIA 里手工确认该对象是否还在，不要按「已删除」继续操作。";
                warnings.Add("删除后的回读确认没有通过，本次结果不可信。");
            }

            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = data,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = ResponseMeta.Basic(ok, ("dryRun", dryRun), ("deleted", deleted),
                    ("verifiedAbsent", verifiedAbsent), ("crossReferenceAvailable", crossRefOk), ("nextActions", nextActions))
            };
        }

        #endregion

    // Block-group operations. Kept in a partial file so the McpServer god-file is not
    // touched. Fills the long-standing gap "MCP cannot operate on block groups":
    //   - CreatePlcBlockGroup: native create of (nested) program-block user groups.
    //   - MoveBlockToGroup: organize an existing block into a group (export/delete/
    //     import round-trip, because Openness has no block-reparent API).
        [McpServerTool(Name = "CreatePlcTypeGroup"), Description("[L2][PLC-Software][WRITE] Preview or create nested PLC data type (UDT) user groups. Exact groupPath relative to PLC data types, e.g. Common/Motors. Creates missing parents and reuses existing groups. dryRun defaults to true; pass false to create. Uses exact PLC software resolution. Does not create a UDT, save, compile or download. Errors report already-created parents; no automatic rollback. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult CreatePlcTypeGroupV4(string softwarePath, string groupPath, bool dryRun = true)
            => PlcToolContract.Run("CreatePlcTypeGroup", !dryRun, true, () => CreatePlcTypeGroup(softwarePath, groupPath, dryRun));

        public ResponseMessage CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            try
            {
                return new ResponseMessage
                {
                    Message = dryRun ? "PLC type group creation preview; nothing changed." : "PLC type group path ready.",
                    Meta = _blocks.CreatePlcTypeGroup(softwarePath, groupPath, dryRun)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException("CreatePlcTypeGroup failed: " + ex.Message, ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "DeleteEmptyPlcBlockGroup"), Description("[L2][PLC-Software][WRITE] Preview or delete exactly one empty PLC user block group. dryRun defaults to true. Requires an exact groupPath relative to Program blocks, e.g. ZZ_MCP_TEST or Parent/Child. Root, nonempty, ambiguous targets are refused. Real deletion requires confirmed Offline state and exclusive access, rechecks contents, calls native Delete and verifies absence. No recursive deletion, save, compile, download or automatic GoOffline. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult DeleteEmptyPlcBlockGroupV4(string softwarePath, string groupPath, bool dryRun=true)
            => PlcToolContract.Run("DeleteEmptyPlcBlockGroup", !dryRun, true, () => DeleteEmptyPlcBlockGroup(softwarePath, groupPath, dryRun));

        public ResponseMessage DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun=true)
        {
            try
            {
                var result=_blocks.DeleteEmptyPlcBlockGroup(softwarePath,groupPath,dryRun);
                return new ResponseMessage { Message=dryRun ? "Empty PLC group deletion preview; nothing changed." : "Empty PLC user group deleted and absence verified.", Meta=result };
            }
            catch(Exception ex) when (ex is not McpException)
            { throw new McpException("DeleteEmptyPlcBlockGroup failed: " + ex.Message,ex,McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "CreatePlcBlockGroup"), Description("[L2][PLC-Software] Create nested program-block groups, creating missing parents and reusing existing groups. groupPath is relative to Program blocks. Requires Connect + OpenProject. Organize blocks with MovePlcBlockToGroup. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult CreatePlcBlockGroupV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("groupPath: '/'-separated group path under Program blocks, e.g. '01_手动控制/手动意图'")] string groupPath)
            => PlcToolContract.Run("CreatePlcBlockGroup", true, true, () => CreatePlcBlockGroup(softwarePath, groupPath));

        public ResponseMessage CreatePlcBlockGroup(
            string softwarePath,
            string groupPath)
        {
            try
            {
                var group = _blocks.EnsurePlcBlockGroup(softwarePath, groupPath, out var created);
                if (group == null)
                {
                    throw new McpException($"Could not create block group '{groupPath}': PlcSoftware not found at '{softwarePath}'", McpErrorCode.InvalidParams);
                }
                return new ResponseMessage
                {
                    Message = created.Count > 0
                        ? $"PLC block group '{groupPath}' ready (created: {string.Join(", ", created)})"
                        : $"PLC block group '{groupPath}' already existed",
                    Meta = ResponseMeta.Basic(DateTime.Now, true, ("createdCount", created.Count))
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed creating PLC block group '{groupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error creating PLC block group '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "MovePlcBlockToGroup"), Description("[L2][PLC-Software] Move/organize an existing block into a program-block group (found anywhere by exact name). Openness cannot reparent a block, so this exports the block, deletes it, and re-imports it into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback for STL/mixed-language). The block number and references are preserved. autoCreateGroup creates the target group path if missing. Requires: Connect + OpenProject + block consistent (compile first). After moving, call CompilePlcDiagnostics to confirm 0 errors. Note: avoid moving OBs with event bindings via this round-trip. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult MoveBlockToGroupV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockName: exact block name to move (searched across all groups)")] string blockName,
            [Description("targetGroupPath: '/'-separated destination group under Program blocks, e.g. '02_手自动接口'")] string targetGroupPath,
            [Description("autoCreateGroup: create the target group path if it does not exist (default true)")] bool autoCreateGroup = true)
            => PlcToolContract.Run("MovePlcBlockToGroup", true, true, () => MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup));

        public ResponseMessage MoveBlockToGroup(
            string softwarePath,
            string blockName,
            string targetGroupPath,
            bool autoCreateGroup = true)
        {
            try
            {
                var summary = _blocks.MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup);
                return new ResponseMessage
                {
                    Message = summary,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed moving block '{blockName}' to '{targetGroupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error moving block '{blockName}' to '{targetGroupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ManagePlcUserGroup"), Description("[L2][PLC-Software][WRITE] Create, rename or deleteEmpty an exact nested PLC user group. family: blocks/types/tags/technology/watchTables/externalSources (PlcBlockUserGroup / PlcTypeUserGroup / PlcTagTableUserGroup / TechnologicalInstanceDBUserGroup / PlcWatchAndForceTableUserGroup / PlcExternalSourceUserGroup; the resulting group is read back as a typed row). groupPath is relative to the family's root. newName is one segment for rename. Default dryRun=true; actual edits require Offline and exclusive access. Missing parents are created; root and nonempty deletion refused. No save/compile/download. Partial failures may leave created parents; inspect errors. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcUserGroupV4(string softwarePath, [Description("blocks | types | tags | technology | watchTables | externalSources. PLC user-group family.")] string family, string groupPath, [Description("create | rename | deleteEmpty. ")] string action, string newName = "", bool dryRun = true)
            => PlcToolContract.Run("ManagePlcUserGroup", !dryRun, true, () => ManagePlcUserGroup(softwarePath, family, groupPath, action, newName, dryRun));

        public ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => _blocks.ManagePlcUserGroup(softwarePath, family, groupPath, action, newName, dryRun);
    }
}
