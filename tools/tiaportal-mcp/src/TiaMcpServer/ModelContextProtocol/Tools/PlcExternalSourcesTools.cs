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
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcExternalSourcesTools
    {
        private readonly IEngineeringSession _session;
        private readonly PlcExternalSourcesService _domain;

        public PlcExternalSourcesTools(PlcExternalSourcesService domain, IEngineeringSession session)
        {
            _domain = domain;
            _session = session;
        }

        [McpServerTool(Name = "GetCrossReferences"), Description("[L2][PLC-Software] Native Step7 block/type/tag/system-constant cross references, including software and safety units. DISABLED BY DEFAULT: CrossReferenceService queries have terminated TIA Portal V21, even after returning a result. Refusal means NOT QUERIED, never zero references. Controlled diagnosis on a saved test project requires the server-process setting TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1; do not enable it automatically. Even with opt-in, any uncompiled block or unreadable IsConsistent refuses the query. Compilation does not guarantee crash safety. Prefer exported PLC documents for partial offline call/reference analysis (GeneratePlcDocumentation); that is not a complete replacement for native references. Delete tools share this policy even with crossReferences=true.")]
        public ResponseCrossReferences GetCrossReferences(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("objectPath: exact relative block/type path, or [group/]table/tag-or-constant under the selected scope")] string objectPath,
            [Description("objectKind: Block | Type | Tag | SystemConstant")] string objectKind = "Block",
            [Description("filter: CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects)")] string filter = "AllObjects",
            [Description("unitName: exact software/safety unit; empty selects PLC root.")] string unitName = "",
            [Description("unitKind: unit | safety. Empty unitName selects PLC root.")] string unitKind = "unit")
        {
            try
            {
                var items = _session.GetCrossReferences(softwarePath, objectPath, objectKind, filter, out var reason, out var queried, unitName, unitKind);
                if (items != null)
                {
                    return new ResponseCrossReferences
                    {
                        Message = $"Cross references retrieved for {objectKind} '{objectPath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true, ("queried", queried), ("complete", true), ("status", "complete"))
                    };
                }

                // envelope: legacy-multiple-dynamic-fields
                return new ResponseCrossReferences { Message = reason ?? "Cross-reference read failed.", Items = null,
                    Meta = new JsonObject { ["success"] = false, ["queried"] = queried, ["complete"] = false, ["status"] = queried ? "failed" : "notQueried" } };
            }
            catch (Exception ex) when (ex is not McpException && ex.GetBaseException() is not global::Siemens.Engineering.NonRecoverableException)
            {
                throw new McpException($"Unexpected error retrieving cross references: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcExternalSources"), Description("[L2][PLC-Software]List PLC external source names (best-effort)")]
        public ResponseStringList GetPlcExternalSources(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = _domain.GetPlcExternalSources(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC external sources listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{_session.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC external sources: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WritePlcSclSourceFile"), Description("[L1][PLC-Software][OFFLINE] Write SCL source text to a local .scl external-source file (UTF-8 WITH BOM, so Chinese comments are not imported as mojibake/乱码). This tool does NOT connect to TIA Portal and does NOT import anything — it only writes the file to disk and returns the path plus manual-import instructions. Use it as the robust fallback when XML block import is rejected (e.g. a TIA V20 portal rejecting V21 SimaticML tokens: 'Cannot create SW.Blocks.CompileUnit... token not supported'): the user imports the .scl manually in TIA via project tree → 'External source files' → 'Add new external file', then right-clicks the source → 'Generate blocks from source'. The sclContent must be a complete source, e.g. FUNCTION_BLOCK \"Name\" ... END_FUNCTION_BLOCK.")]
        public ResponseMessage WritePlcSclSourceFile(
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
                var defaultName = EngineeringFileNames.MakeSafeFileName(nameMatch.Success ? nameMatch.Groups[1].Value : "MCP_Source");

                string finalPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var dir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_scl");
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
        public ResponseMessage ImportPlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: external source group path (use empty for root)")] string groupPath,
            [Description("filePath: path to external source file (.scl, etc.)")] string filePath)
        {
            try
            {
                _domain.ImportPlcExternalSource(softwarePath, groupPath, filePath);
                return new ResponseMessage
                {
                    Message = "PLC external source imported",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
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
        public ResponseMessage DeletePlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl)")] string externalSourceName)
        {
            try
            {
                _domain.DeletePlcExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"PLC external source '{externalSourceName}' deleted or was not present",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
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
        public ResponseMessage GenerateBlocksFromExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources")] string externalSourceName)
        {
            try
            {
                _domain.GenerateBlocksFromExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"Blocks generated from external source '{externalSourceName}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
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

        [McpServerTool(Name="ManagePlcExternalSources"), Description("[L2][PLC-Software][WRITE] Native external source files of the exact PLC (PlcSoftware.ExternalSourceGroup) or of a unit (unitName + unitKind), in the root system group or a user group at groupPath: list (PlcExternalSourceGroup Name / ExternalSources / Groups), read, createFromFile (PlcExternalSourceComposition.CreateFromFile(name, filePath): an existing ASCII .scl/.awl/.stl/.db/.udt file on the TIA Portal machine), createFromMasterCopy (libraryName empty = project library, masterCopyPath, copyMode ThrowIfExists/Rename/Replace), delete, generateBlocks (PlcExternalSource.GenerateBlocksFromSource with generateOption None/KeepOnError, optionally into an exact block or type user group via targetKind + targetGroupPath; existing objects are overwritten natively, returns the generated names), createGroup / renameGroup / deleteGroup (PlcExternalSourceUserGroup; deletion only when empty). Every write is read back. Default dryRun=true; delete needs confirmDelete=true; real writes require an Offline PLC. No save/compile/download. Source generation from blocks stays GeneratePlcSourceFromBlocks.")]
        public ResponseMessage ManagePlcExternalSources(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("action: list | read | createFromFile | createFromMasterCopy | delete | generateBlocks | createGroup | renameGroup | deleteGroup.")] string action,
            [Description("name: external source name (read / createFromFile / delete / generateBlocks) or group name (createGroup / renameGroup / deleteGroup).")] string name="",
            [Description("unitName: software unit holding the sources ('' = the PLC program).")] string unitName="",
            [Description("unitKind: unit | safety - which unit collection unitName refers to.")] string unitKind="unit",
            [Description("groupPath: 'Folder/Subfolder' inside the external sources ('' = root).")] string groupPath="",
            [Description("filePath: for createFromFile - full path of the .scl / .awl / .stl / .db / .udt file on the TIA machine.")] string filePath="",
            [Description("libraryName: for createFromMasterCopy - global library name ('' = the project library).")] string libraryName="",
            [Description("masterCopyPath: for createFromMasterCopy - 'Folder/Name' of the master copy.")] string masterCopyPath="",
            [Description("copyMode: for createFromMasterCopy - ThrowIfExists | Rename | Replace.")] string copyMode="",
            [Description("generateOption: for generateBlocks - None | KeepOnError.")] string generateOption="None",
            [Description("targetKind: for generateBlocks - block | type ('' = the source decides).")] string targetKind="",
            [Description("targetGroupPath: for generateBlocks - block / type group that receives the result ('' = root).")] string targetGroupPath="",
            [Description("newName: for renameGroup.")] string newName="",
            [Description("confirmDelete: must be true together with dryRun=false for delete / deleteGroup.")] bool confirmDelete=false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => _domain.ManagePlcExternalSources(softwarePath,action,name,unitName,unitKind,groupPath,filePath,libraryName,masterCopyPath,copyMode,generateOption,targetKind,targetGroupPath,newName,confirmDelete,dryRun);

        [McpServerTool(Name="ReadPlcSystemGroups"), Description("[L2][PLC-Software][READ] Typed read of the system-generated groups of the exact PLC (or of a unit via unitName + unitKind): PlcBlockSystemGroup.SystemBlockGroups as a PlcSystemBlockGroup tree (Name, blocks with number / class / language when includeBlocks, nested Groups to maxDepth) and PlcTypeSystemGroup.SystemTypeGroups (PlcSystemTypeGroup Name / Types). First 200 objects per group. No modification.")]
        public ResponseMessage ReadPlcSystemGroups(
            string softwarePath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("includeBlocks: true also returns the blocks of each chart / group.")] bool includeBlocks=true,
            int maxDepth=4)
            => _domain.ReadPlcSystemGroups(softwarePath,unitName,unitKind,includeBlocks,maxDepth);
    }
}
