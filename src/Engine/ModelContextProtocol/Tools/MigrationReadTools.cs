using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Linq;
using System.Collections.Generic;
using System;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class MigrationReadTools
    {
        private readonly MigrationReadService _migrationRead;

        public MigrationReadTools(MigrationReadService service) => _migrationRead = service;

        [McpServerTool(Name = "ListUnifiedGlobalScripts"), Description("[L2][HMI-Unified][READ]Page global script module names only. Names are NOT proof of body acquisition. Exact expectedProject and HMI path required. Resume with identical arguments and nextCursor.")]
        public CallToolResult ListUnifiedGlobalScriptsV4(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => HmiInspectionContract.Run("ListUnifiedGlobalScripts", false, false, () => ListUnifiedGlobalScripts(softwarePath, expectedProject, cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ListUnifiedGlobalScripts(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
        => _migrationRead.ListUnifiedGlobalScripts(softwarePath, expectedProject, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "GetUnifiedGlobalScript"), Description("[L2][HMI-Unified][READ]Native-export ONE exact global module to temporary files, return original JS/YAML, functions, parameters, bodies, global definitions, imports and unresolved tag expressions. Never imports, saves or executes scripts. Follow nextCursor; inspect dataComplete and failures.")]
        public CallToolResult ReadUnifiedGlobalScriptV4(
            string softwarePath,
            string expectedProject,
            [Description("moduleName: exact name of the global script module.")] string moduleName,
            string cursor = "",
            int pageSize = 50,
            int budgetMs = 5000)
            => HmiInspectionContract.Run("GetUnifiedGlobalScript", false, false, () => ReadUnifiedGlobalScript(softwarePath, expectedProject, moduleName, cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ReadUnifiedGlobalScript(
            string softwarePath,
            string expectedProject,
            [Description("moduleName: exact name of the global script module.")] string moduleName,
            string cursor = "",
            int pageSize = 50,
            int budgetMs = 5000)
        => _migrationRead.ReadUnifiedGlobalScript(softwarePath, expectedProject, moduleName, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ListUnifiedTagDefinitions"), Description("[L2][HMI-Unified][READ]Resume recursive root/table/group/UDT/array member definitions. Returns path-addressed records, own API fields and separately labelled root source inference. Unknown bounds remain gaps. pageSize counts evidence records, not tags. Follow nextCursor until null; inspect cumulative failures.")]
        public CallToolResult ReadUnifiedTagDefinitionsV4(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => HmiInspectionContract.Run("ListUnifiedTagDefinitions", false, false, () => ReadUnifiedTagDefinitions(softwarePath, expectedProject, cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ReadUnifiedTagDefinitions(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
        => _migrationRead.ReadUnifiedTagDefinitions(softwarePath, expectedProject, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "GetUnifiedScreenBranch"), Description("[L2][HMI-Unified][READ]Read an exact screen/item branch with continuation. screenPath is /Group/Screen with URI-escaped names. branch is an array of at most 64 steps: {property}, {attribute}, {index} or {name,key?}. [] reads the selected subtree, never Parent. No writes or arbitrary method invocation.")]
        public CallToolResult ReadUnifiedScreenBranchV4(
            string softwarePath,
            string expectedProject,
            string screenPath,
            string itemName = "",
            [Description("branch: exact property, attribute, nonnegative index or name selection steps.")] BranchStep[] branch = null!,
            string cursor = "",
            int pageSize = 100,
            int budgetMs = 5000)
            => HmiInspectionContract.Run("GetUnifiedScreenBranch", false, false,
                () => ReadUnifiedScreenBranch(softwarePath, expectedProject, screenPath, itemName,
                    V4Json.Serialize(branch ?? Array.Empty<BranchStep>()), cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ReadUnifiedScreenBranch(
            string softwarePath,
            string expectedProject,
            string screenPath,
            string itemName = "",
            [Description("branchJson: JSON array path of the screen branch to read.")] string branchJson = "[]",
            string cursor = "",
            int pageSize = 100,
            int budgetMs = 5000)
            => _migrationRead.ReadUnifiedScreenBranch(softwarePath, expectedProject, screenPath, itemName, branchJson, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "GetUnifiedLibraryType"), Description("[L2][HMI-Unified][READ]Read/native-export ONE exact project-library type AND version. typePath=/Folder/Type, version exact (never default). Uses advertised document format; non-script types with no document formats use the separate official Export(FileInfo, WithReadOnly) XML action once. XML file acquisition is separate from unverified internal-content coverage. No guessed formats, edit mode, instantiation, whole-library traversal or automatic retry after failure. Direct dependency identifiers, original files, hashes and explicit gaps are returned.")]
        public CallToolResult ReadUnifiedLibraryTypeV4(string softwarePath, string expectedProject, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => HmiInspectionContract.Run("GetUnifiedLibraryType", false, false, () => ReadUnifiedLibraryType(softwarePath, expectedProject, typePath, version, cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ReadUnifiedLibraryType(string softwarePath, string expectedProject, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
        => _migrationRead.ReadUnifiedLibraryType(softwarePath, expectedProject, typePath, version, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "GetUnifiedFaceplateInstance"), Description("[L2][HMI-Unified][READ]Trace one exact screen faceplate instance to its caller-selected library type/version. Reads interface assignments, events and property bindings; verifies native library-version GUID before exporting internals. Mismatch or unavailable version service is an explicit gap; never substitutes a default version or scans other types.")]
        public CallToolResult ReadUnifiedFaceplateInstanceV4(string softwarePath, string expectedProject, string screenPath, string itemName, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => HmiInspectionContract.Run("GetUnifiedFaceplateInstance", false, false, () => ReadUnifiedFaceplateInstance(softwarePath, expectedProject, screenPath, itemName, typePath, version, cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ReadUnifiedFaceplateInstance(string softwarePath, string expectedProject, string screenPath, string itemName, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
        => _migrationRead.ReadUnifiedFaceplateInstance(softwarePath, expectedProject, screenPath, itemName, typePath, version, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ListUnifiedLibraryFolderEntries"), Description("[L2][HMI-Unified][READ]Page only direct type/folder names in ONE exact project-library folder, for locating a referenced script/faceplate. No recursive search or default-version substitution. / selects root. Inspect returned folders before choosing the next exact path.")]
        public CallToolResult ListUnifiedLibraryFolderV4(string softwarePath, string expectedProject, string folderPath = "/", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => HmiInspectionContract.Run("ListUnifiedLibraryFolderEntries", false, false, () => ListUnifiedLibraryFolder(softwarePath, expectedProject, folderPath, cursor, pageSize, budgetMs), cursor: cursor, pageSize: pageSize);

        public ResponseMessage ListUnifiedLibraryFolder(string softwarePath, string expectedProject, string folderPath = "/", string cursor = "", int pageSize = 100, int budgetMs = 5000)
        => _migrationRead.ListUnifiedLibraryFolder(softwarePath, expectedProject, folderPath, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReleaseUnifiedReadCursor"), Description("[L2][HMI-Unified][READ]Release one collection cursor and its temporary export files. Never saves or closes TIA Portal. Cursors otherwise expire after 30 minutes without use or on project switch/server restart.")]
        public CallToolResult ReleaseUnifiedReadCursorV4(string cursor)
            => HmiInspectionContract.Run("ReleaseUnifiedReadCursor", false, false, () => ReleaseUnifiedReadCursor(cursor));

        public ResponseMessage ReleaseUnifiedReadCursor(string cursor)
        => _migrationRead.ReleaseUnifiedReadCursor(cursor);
    }
}
