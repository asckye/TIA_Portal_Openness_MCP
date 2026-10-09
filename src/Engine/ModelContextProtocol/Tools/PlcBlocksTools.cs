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

        [McpServerTool(Name = "GetPlcBlockInfo"), Description("[L2][PLC-Software] Get detailed info for one block (attributes, language, number, modification time). Requires: ConnectPortal + OpenProject. blockPath must be fully qualified: 'Group/Subgroup/BlockName' — get it from GetSoftwareTree or GetPlcBlockHierarchy. Returns: IsConsistent (false = must compile before export). Current native policy; V4 safety behavior is not yet accepted.")]
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

        [McpServerTool(Name = "ListPlcBlocks"), Description("[L2][PLC-Software] Get a flat list of user-group blocks in PLC software. System block groups are excluded; inspect meta.dataComplete for unreadable attributes. Requires: ConnectPortal + OpenProject. Use GetPlcBlockHierarchy instead when you need group/folder paths for ExportPlcBlock. Returns: block name, number, type (OB/FC/FB/GlobalDB/InstanceDB), programming language. Current native policy; V4 safety behavior is not yet accepted.")]
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
                        + "Call ConnectPortal / OpenProject (or AttachOpenProject) first. "
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



        [McpServerTool(Name = "ExportPlcBlock"), Description("[L2][PLC-Software] Export one block to an XML file. Requires: ConnectPortal + OpenProject + block must be consistent (compile first if IsConsistent=false). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree — bare names return InvalidParams with suggestions. Pick the right tool: batch → ExportPlcBlocks; readable SCL/.s7dcl text → ExportPlcBlockDocuments. Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
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
        [McpServerTool(Name = "ImportPlcBlock"), Description("[L1][PLC-Software] Import a single SimaticML XML block file into PLC software. Requires: ConnectPortal + OpenProject. importPath must be an absolute path to a .xml file. After import it reads back to confirm the block is present (Meta.verified); call CompilePlcDiagnostics for full consistency. Pick the right tool: SCL/.s7dcl text → ImportPlcBlockDocuments; multiple XML files → ImportPlcBlocksFromDirectory; a full exported program (UDTs+tags+blocks) → ImportPlcProgramFromDirectory; JSON-built blocks → BuildAndImportPlcArtifact. Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportBlockV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the block")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the block")] string importPath)
            => PlcSingleImportRecovery.Run(_session, "ImportPlcBlock", softwarePath, groupPath, importPath, "block",
                () => PlcToolContract.Run("ImportPlcBlock", true, true, () => ImportBlock(softwarePath, groupPath, importPath)));

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
                        : $"⚠ UNVERIFIED: block imported from '{importPath}' to '{groupPath}', but the read-back "
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

        [McpServerTool(Name = "ImportPlcBlocksFromDirectory"), Description("[L2][PLC-Software][WRITE] Same-release XML directory import. Use dryRun=true to review each create/replace action in planHash. overwrite=false apply without a plan retains the existing None import route. Apply requires importOrder, expectedPlanHash, confirm and exact expectedProjectFile. overwrite=true exports every replacement before imports; failure stops and restores known earlier replacements in dependency order. Unknown native outcomes retain recoveryDirectory and require session reset. No compile/save or retry. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportBlocksFromDirectoryV4([Description("Exact PLC software path.")] string softwarePath, [Description("Exact destination block group; empty selects root.")] string groupPath, [Description("Absolute TIA-machine XML directory, for example from StageImportFiles.")] string dir, [Description("Bounded filename regex; empty selects every XML file.")] string regexName = "", [Description("Allow reviewed replacements with recovery export before any import.")] bool overwrite = false,
            [Description("Preview only by default; false applies after approval.")] bool dryRun = true, [Description("Complete explicit relative-file manifest from preview.")] string[]? importOrder = null, [Description("Exact planHash from the reviewed preview.")] string expectedPlanHash = "", [Description("Explicit confirmation for project mutation.")] bool confirm = false, [Description("Exact absolute bound project path.")] string expectedProjectFile = "", [Description("Selected file limit 1..256; no truncation.")] int maxItems = 128)
            => !overwrite && !dryRun && string.IsNullOrEmpty(expectedPlanHash)
                ? PlcToolContract.Run("ImportPlcBlocksFromDirectory", true, true, () => ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, false))
                : PlcBatchImportService.Run(_session, "ImportPlcBlocksFromDirectory", new TiaMcp.Adapters.PlcBatchImportRequest(
                software: softwarePath, blockGroup: groupPath, directory: dir, regex: regexName, overwrite: overwrite, dryRun: dryRun,
                order: importOrder, expectedHash: expectedPlanHash, confirm: confirm, expectedProject: expectedProjectFile, maxItems: maxItems), softwarePath, dryRun);

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

        [McpServerTool(Name = "ImportPlcProgramFromDirectory"), Description("[L2][PLC-Software][WRITE] Bounded recursive same-release block/UDT/tag-table XML program import. Preview defaults true and lists create/replace actions in planHash. Apply requires a complete explicit importOrder, expectedPlanHash, confirm and exact expectedProjectFile. overwrite=true exports all replacements before imports; first failure stops and restores known replacements in dependency order. Unknown outcomes retain recoveryDirectory and require session reset. compileAfter/technology/continuation refused. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportPlcProgramFromDirectoryV4([Description("Exact PLC software path.")] string softwarePath, [Description("Absolute staged program XML directory.")] string sourceDir, [Description("Exact destination type group; empty selects root.")] string typeGroupPath = "", [Description("Exact destination tag group; empty selects root.")] string tagFolderPath = "",
            [Description("Must be empty; technology import is outside this batch policy.")] string technologyFolderPath = "", [Description("Exact destination block group; empty selects root.")] string blockGroupPath = "", [Description("Bounded filename regex; empty selects every XML file.")] string regexName = "", [Description("Must be false; compilation is a separate call.")] bool compileAfter = false, [Description("Must be true; stop and recover after the first failure.")] bool stopOnImportFailure = true,
            [Description("Preview only by default; false applies after approval.")] bool dryRun = true, [Description("Complete explicit relative-file manifest from preview.")] string[]? importOrder = null, [Description("Exact planHash from the reviewed preview.")] string expectedPlanHash = "", [Description("Explicit confirmation for project mutation.")] bool confirm = false, [Description("Exact absolute bound project path.")] string expectedProjectFile = "", [Description("Allow reviewed replacements with recovery export before any import.")] bool overwrite = false, [Description("Selected file limit 1..256; no truncation.")] int maxItems = 128)
            => PlcBatchImportService.Run(_session, "ImportPlcProgramFromDirectory", new TiaMcp.Adapters.PlcBatchImportRequest(
                software: softwarePath, directory: sourceDir, program: true, typeGroup: typeGroupPath, tagGroup: tagFolderPath, technologyGroup: technologyFolderPath,
                blockGroup: blockGroupPath, regex: regexName, compileAfter: compileAfter, stopOnImportFailure: stopOnImportFailure, dryRun: dryRun,
                order: importOrder, expectedHash: expectedPlanHash, confirm: confirm, expectedProject: expectedProjectFile, overwrite: overwrite, maxItems: maxItems), softwarePath, dryRun);

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
            if (!_session.IsProjectNull()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                TiaMcpServer.Siemens.SoftwareContainerLookup.PathOf(_session.GetPlcSoftware(softwarePath)), true);

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

        [McpServerTool(Name = "CompilePlcDiagnostics"), Description("[L1][PLC-Software] PREFERRED compile tool. Compiles PLC and returns structured errors/warnings by recursively walking CompilerResult.Messages (V20/V21 PublicAPI). Leaf diagnostics include Path + Description; Line/Column when exposed as public properties. Requires: ConnectPortal + OpenProject. Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CompileAndDiagnosePlcV4(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("password: optional safety password")] string password = "")
            => PlcToolContract.Run("CompilePlcDiagnostics", true, true, () => CompileAndDiagnosePlc(softwarePath, password));

        public ResponseCompileDiagnose CompileAndDiagnosePlc(
            string softwarePath,
            string password = "")
            => PlcCompilation.CompileAndDiagnoseCore(softwarePath, password);











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

        [McpServerTool(Name = "ExportPlcBlocks"), Description("[L2][PLC-Software] Export all (or regexName-filtered) blocks to an existing directory as SimaticML XML; exportPath must already exist. This XML export does not create a new directory. Pick the right tool: readable SCL/.s7dcl text → ExportPlcBlocksDocuments (which requires a new directory); a single block → ExportPlcBlock. Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
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

















        #region delete blocks / tag tables / types



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










        /// <summary>
        /// 三个删除工具共用的成品报告。抽出来是因为「什么算成功」这条口径必须三处完全一致 ——
        /// 分开写迟早会有一处漏掉「回读未确认」或「交叉引用查不到」的措辞，而那正是这一族的命门。
        /// </summary>


        #endregion

    // Block-group operations. Kept in a partial file so the McpServer god-file is not
    // touched. Fills the long-standing gap "MCP cannot operate on block groups":
    //   - CreatePlcBlockGroup: native create of (nested) program-block user groups.
    //   - MoveBlockToGroup: organize an existing block into a group (export/delete/
    //     import round-trip, because Openness has no block-reparent API).



















    }
}
