using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcReadModifyTools
    {
        private readonly PlcOrganisationPortService _service;
        public PlcReadModifyTools(PlcOrganisationPortService service) => _service = service;
        public ResponseMessage CreatePlcInstanceDb(
            string softwarePath,
            [Description("fbPath: 'Group/Name' of the function block the instance DB is created for.")] string fbPath,
            string name,
            string groupPath="",
            [Description("autoNumber: true lets TIA assign the block number.")] bool autoNumber=true,
            [Description("number: block number when autoNumber is false.")] int number=0,
            bool dryRun=true)
        => _service.CreatePlcInstanceDb(softwarePath,fbPath,name,groupPath,autoNumber,number,dryRun);

        public ResponseMessage GeneratePlcSourceFromBlocks(
            string softwarePath,
            [Description("blockPathsJson: JSON array of block paths.")] string blockPathsJson,
            string filePath,
            bool dryRun=true)
        => _service.GeneratePlcSourceFromBlocks(softwarePath,blockPathsJson,filePath,dryRun);

        public ResponseMessage ManagePlcTagDefinition(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tablePath: 'Group/Table' path of the tag table, e.g. 'Default tag table'.")] string tablePath,
            [Description("name: exact tag or constant name.")] string name,
            [Description("kind: tag | constant.")] string kind,
            [Description("action: read | create | update | delete.")] string action,
            [Description("dataType: for create / update - PLC data type, e.g. Bool, Int, Real.")] string dataType="",
            [Description("addressOrValue: for create / update - logical address of a tag (e.g. %M0.0, %I0.0) or the value of a constant.")] string addressOrValue="",
            [Description("propertiesJson: JSON object of further scalar properties (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; Comment as text or per culture).")] string propertiesJson="{}",
            [Description("dryRun: true (default) previews; false executes (project must be offline).")] bool dryRun=true)
        => _service.ManagePlcTagDefinition(softwarePath,tablePath,name,kind,action,dataType,addressOrValue,propertiesJson,dryRun);

        [McpServerTool(Name = "CreatePlcInstanceDb"), Description("[L2][PLC-Software][WRITE] Create a native instance DB from an exact FB and destination block group. Default preview; requires offline PLC when executing. No save/compile/download." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult CreatePlcInstanceDbV4(
            string softwarePath,
            [Description("fbPath: 'Group/Name' of the function block the instance DB is created for.")] string fbPath,
            string name,
            string groupPath="",
            [Description("autoNumber: true lets TIA assign the block number.")] bool autoNumber=true,
            [Description("number: block number when autoNumber is false.")] int number=0,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("CreatePlcInstanceDb", () => CreatePlcInstanceDb(softwarePath, fbPath, name, groupPath, autoNumber, number, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "GeneratePlcSourceFromBlocks"), Description("[L2][PLC-Software][FILE] Generate native source from 1..500 exact block paths (array). New absolute output file only; SHA-256 returned. Unsupported block languages rejected; default preview." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult GeneratePlcSourceFromBlocksV4(
            string softwarePath,
            [Description("blockPaths: Array of block paths.")] string[] blockPaths,
            [Description("filePath: new absolute native source file in an existing directory, e.g. FC_Ready.scl; the selected blocks determine the source language.")] string filePath,
            bool dryRun=true)
        {
            var validated = PlcExchangeContract.Paths.Validate(blockPaths, "blockPaths");
            if (validated.Error != null) return PlcExchangeContract.Reject("GeneratePlcSourceFromBlocks", validated.Error);
            return PlcExchangeContract.Run("GeneratePlcSourceFromBlocks", () => GeneratePlcSourceFromBlocks(softwarePath, V4Json.Serialize(validated.Value), filePath, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "ManagePlcTagDefinition"), Description("[L2][PLC-Software][WRITE] Read/create/update/delete an exact tag or constant. properties accepts scalar SDK attributes; Comment is text in the editing language. Default preview, execution requires Offline. No runtime write, save, compile or download. V4 envelope; PLC path policy remains current pending native acceptance.")]
        public CallToolResult ManagePlcTagDefinitionV4(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tablePath: 'Group/Table' path of the tag table, e.g. 'Default tag table'.")] string tablePath,
            [Description("name: exact tag or constant name.")] string name,
            [Description("kind: tag | constant.")] string kind,
            [Description("action: read | create | update | delete.")] string action,
            [Description("dataType: for create / update - PLC data type, e.g. Bool, Int, Real.")] string dataType="",
            [Description("addressOrValue: for create / update - logical address of a tag (e.g. %M0.0, %I0.0) or the value of a constant.")] string addressOrValue="",
            [Description("properties: Scalar attribute map of further scalar properties (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; constants: Value; Comment: text).")] AttributeMap<Scalar>? properties=null,
            [Description("dryRun: true (default) previews; false executes (project must be offline).")] bool dryRun=true)
        {
            Func<ResponseMessage> invoke = () => ManagePlcTagDefinition(softwarePath, tablePath, name, kind, action, dataType, addressOrValue, dryRun: dryRun);
            if (properties != null)
            {
                var validated = PlcExchangeContract.TagProperties(kind).Validate(properties, "properties");
                if (validated.Error != null) return PlcExchangeContract.Reject("ManagePlcTagDefinition", validated.Error);
                invoke = () => ManagePlcTagDefinition(softwarePath, tablePath, name, kind, action, dataType, addressOrValue, V4Json.Serialize(validated.Value), dryRun);
            }
            return PlcExchangeContract.Run("ManagePlcTagDefinition", invoke, write: !dryRun && action != "read", current: true);
        }

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
            => _service.UpdatePlcProgram(softwarePath,confirmUpdate,dryRun);

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

        [McpServerTool(Name = "GetPlcTagTableConstants"), Description("[L2][PLC-Software][READ] Constants of one exact PLC tag table (tablePath under TagTableGroup, or a unit's TagTableGroup via unitName + unitKind): PlcTagTable.UserConstants and SystemConstants as typed PlcConstant rows (Name, DataTypeName, Value, kind user/system); kind filters. Paginated. User constants are edited with ManagePlcTag kind=constant. No modification. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcTagTableConstantsV4(
            string softwarePath,
            string tablePath,
            [Description("kind: all | user | system.")] string kind="all",
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            int offset=0,
            int limit=200)
            => PlcToolContract.Run("GetPlcTagTableConstants", false, true, () => ReadPlcTagTableConstants(softwarePath, tablePath, kind, unitName, unitKind, offset, limit));

        public ResponseMessage ReadPlcTagTableConstants(
            string softwarePath,
            string tablePath,
            string kind="all",
            string unitName="",
            string unitKind="unit",
            int offset=0,
            int limit=200)
            => _service.ReadPlcTagTableConstants(softwarePath,tablePath,kind,unitName,unitKind,offset,limit);

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
        => _service.ManagePlcExternalSources(softwarePath,action,name,unitName,unitKind,groupPath,filePath,libraryName,masterCopyPath,copyMode,generateOption,targetKind,targetGroupPath,newName,confirmDelete,dryRun);

        [McpServerTool(Name = "ManagePlcExternalSources"), Description("[L2][PLC-Software][WRITE] Native external source files of the exact PLC (PlcSoftware.ExternalSourceGroup) or of a unit (unitName + unitKind), in the root system group or a user group at groupPath: list (PlcExternalSourceGroup Name / ExternalSources / Groups), read, createFromFile (PlcExternalSourceComposition.CreateFromFile(name, filePath): an existing ASCII .scl/.awl/.stl/.db/.udt file on the TIA Portal machine), createFromMasterCopy (libraryName empty = project library, masterCopyPath, copyMode ThrowIfExists/Rename/Replace), delete, generateBlocks (PlcExternalSource.GenerateBlocksFromSource with generateOption None/KeepOnError, optionally into an exact block or type user group via targetKind + targetGroupPath; existing objects are overwritten natively, returns the generated names), createGroup / renameGroup / deleteGroup (PlcExternalSourceUserGroup; deletion only when empty). Every write is read back. Default dryRun=true; delete needs confirmDelete=true; real writes require an Offline PLC. No save/compile/download. Source generation from blocks stays GeneratePlcSourceFromBlocks." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePlcExternalSourcesV4(
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
        {
            return PlcExchangeContract.Run("ManagePlcExternalSources", () => ManagePlcExternalSources(softwarePath, action, name, unitName, unitKind, groupPath, filePath, libraryName, masterCopyPath, copyMode, generateOption, targetKind, targetGroupPath, newName, confirmDelete, dryRun), write: !dryRun && action != "read" && action != "list", current: true);
        }
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
                var items = _service.GetCrossReferences(softwarePath, objectPath, objectKind, filter, out var reason, out var queried, unitName, unitKind);
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
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving cross references: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcCrossReferences"), Description("[L2][PLC-Software] Native Step7 block/type/tag/system-constant cross references, including software and safety units. DISABLED BY DEFAULT: CrossReferenceService queries have terminated TIA Portal V21, even after returning a result. Refusal means NOT QUERIED, never zero references. Controlled diagnosis on a saved test project requires the server-process setting TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1; do not enable it automatically. Even with opt-in, any uncompiled block or unreadable IsConsistent refuses the query. Compilation does not guarantee crash safety. Prefer exported PLC documents for partial offline call/reference analysis (GeneratePlcDocumentation); that is not a complete replacement for native references. Delete tools share this policy even with crossReferences=true." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult GetCrossReferencesV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("objectPath: exact relative block/type path, or [group/]table/tag-or-constant under the selected scope")] string objectPath,
            [Description("objectKind: Block | Type | Tag | SystemConstant")] string objectKind = "Block",
            [Description("filter: CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects)")] string filter = "AllObjects",
            [Description("unitName: exact software/safety unit; empty selects PLC root.")] string unitName = "",
            [Description("unitKind: unit | safety. Empty unitName selects PLC root.")] string unitKind = "unit")
        {
            return PlcExchangeContract.Run("GetPlcCrossReferences", () => GetCrossReferences(softwarePath, objectPath, objectKind, filter, unitName, unitKind), write: false, current: true);
        }
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
            => _service.ImportPlcBlockVerified(softwarePath, blockPath, importPath, evidenceDirectory, dryRun, expectedToken, compileAfterImport);
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
                new PlcArtifactPortSession(_service, "RepairAndReimportPlcBlock").ImportBlock(softwarePath, groupPath, importPath);

                ResponseCompileDiagnose? compile = null;
                if (compileAfter)
                {
                    compile = new PlcArtifactPortSession(_service, "RepairAndReimportPlcBlock").CompileAndDiagnose(softwarePath);
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

        [McpServerTool(Name = "DescribePlcBlockLogic"), Description("[L1][PLC-Software] Read a block's LOGIC as READABLE TEXT — the fast, accurate way to analyze LADDER (LAD) without hand-parsing FlgNet XML. For each LAD network it reconstructs the power flow as a boolean-ish expression (series = ' · ', parallel = ' + '), shows coils ( )/(S)/(R), MOVE/compare/timer boxes with their operands, and — critically — FLAGS any contact whose operand is a LITERAL CONSTANT with a literal-constant marker (a normally-open contact wired to FALSE silently disables its whole rung; this is nearly impossible to spot by eye). SCL/STL networks are rendered inline as code. Use this to understand or review LAD logic before editing. Requires: ConnectPortal + OpenProject + the block consistent (compile first if IsConsistent=false; export does not work in online mode — DisconnectOnlinePlc first). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree. Current native policy; V4 safety behavior is not yet accepted.")]
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

                var block = new PlcArtifactPortSession(_service, "DescribePlcBlockLogic").ExportBlock(softwarePath, blockPath, tempDir);
                if (block == null)
                {
                    throw new McpException($"Could not export '{blockPath}' from '{softwarePath}' for analysis. If IsConsistent=false, compile first; if online, DisconnectOnlinePlc first; verify the path with GetSoftwareTree.", McpErrorCode.InternalError);
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
        [McpServerTool(Name = "ImportPlcTagTablesFromDirectory"), Description("[L2][PLC-Software]Batch import PLC tag table .xml files from a directory (best-effort) Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ImportPlcTagTablesFromDirectoryV4(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing PLC tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
            => PlcToolContract.Run("ImportPlcTagTablesFromDirectory", true, true, () => ImportPlcTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite));

        public ResponseImportBatch ImportPlcTagTablesFromDirectory(
            string softwarePath,
            string folderPath,
            string dir,
            string regexName = "",
            bool overwrite = true)
        {
            try
            {
                var result = _service.ImportPlcTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} PLC tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = ResponseMeta.Basic(DateTime.Now, (result.Failed == null || !result.Failed.Any()))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag tables from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
        [McpServerTool(Name = "SeedProjectFromReference"), Description("[L2][PLC-Software]Seed PLC blocks/types and HMI screens/tagtables from a reference directory (manifest.json + {{PLACEHOLDER}} replace) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult SeedProjectFromReferenceV4(
            [Description("plcSoftwarePath: path in the project structure to the PLC software")] string plcSoftwarePath,
            [Description("hmiSoftwarePath: path in the project structure to the HMI software")] string hmiSoftwarePath,
            [Description("referenceDir: directory containing manifest.json and subfolders (plc/blocks, plc/types, hmi/screens, hmi/tags)")] string referenceDir,
            [Description("placeholders: JsonObject key-values for replacement in XML, e.g. {\"PLC_NAME\":\"PLC_1\"}")] JsonObject? placeholders = null)
            => PlcToolContract.Run("SeedProjectFromReference", true, true, () => SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders));

        public ResponseSeed SeedProjectFromReference(
            string plcSoftwarePath,
            string hmiSoftwarePath,
            string referenceDir,
            JsonObject? placeholders = null)
        {
            try
            {
                var res = _service.SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders);
                // envelope: legacy-existing-meta
                res.Meta ??= new JsonObject();
                res.Meta["timestamp"] = DateTime.Now;
                res.Meta["success"] = (res.Failed == null || !res.Failed.Any());
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error seeding project from reference: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
