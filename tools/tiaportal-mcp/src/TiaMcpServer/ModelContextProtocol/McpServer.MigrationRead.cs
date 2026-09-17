using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ListUnifiedGlobalScripts"), Description("[L2][HMI-Unified][READ]Page global script module names only. Names are NOT proof of body acquisition. Exact expectedProject and HMI path required. Resume with identical arguments and nextCursor.")]
        public static ResponseMessage ListUnifiedGlobalScripts(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Portal.ListUnifiedGlobalScripts(softwarePath, expectedProject, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReadUnifiedGlobalScript"), Description("[L2][HMI-Unified][READ]Native-export ONE exact global module to temporary files, return original JS/YAML, functions, parameters, bodies, global definitions, imports and unresolved tag expressions. Never imports, saves or executes scripts. Follow nextCursor; inspect dataComplete and failures.")]
        public static ResponseMessage ReadUnifiedGlobalScript(string softwarePath, string expectedProject, string moduleName, string cursor = "", int pageSize = 50, int budgetMs = 5000)
            => Portal.ReadUnifiedGlobalScript(softwarePath, expectedProject, moduleName, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReadUnifiedTagDefinitions"), Description("[L2][HMI-Unified][READ]Resume recursive root/table/group/UDT/array member definitions. Returns path-addressed records, own API fields and separately labelled root source inference. Unknown bounds remain gaps. pageSize counts evidence records, not tags. Follow nextCursor until null; inspect cumulative failures.")]
        public static ResponseMessage ReadUnifiedTagDefinitions(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Portal.ReadUnifiedTagDefinitions(softwarePath, expectedProject, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReadUnifiedScreenBranch"), Description("[L2][HMI-Unified][READ]Read an exact screen/item branch with continuation. screenPath is /Group/Screen with URI-escaped names. branchJson is [{property:'Interface'},{name:'Speed',key:'PropertyName'},{property:'Dynamizations'}] encoded as valid JSON; property, attribute, index and exact name selectors only. [] reads the selected subtree, never Parent. No writes or arbitrary method invocation.")]
        public static ResponseMessage ReadUnifiedScreenBranch(string softwarePath, string expectedProject, string screenPath, string itemName = "", string branchJson = "[]", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Portal.ReadUnifiedScreenBranch(softwarePath, expectedProject, screenPath, itemName, branchJson, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReadUnifiedLibraryType"), Description("[L2][HMI-Unified][READ]Read/native-export ONE exact project-library type AND version. typePath=/Folder/Type, version exact (never default). Uses advertised document format; non-script types with no document formats use the separate official Export(FileInfo, WithReadOnly) XML action once. XML file acquisition is separate from unverified internal-content coverage. No guessed formats, edit mode, instantiation, whole-library traversal or automatic retry after failure. Direct dependency identifiers, original files, hashes and explicit gaps are returned.")]
        public static ResponseMessage ReadUnifiedLibraryType(string softwarePath, string expectedProject, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Portal.ReadUnifiedLibraryType(softwarePath, expectedProject, typePath, version, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReadUnifiedFaceplateInstance"), Description("[L2][HMI-Unified][READ]Trace one exact screen faceplate instance to its caller-selected library type/version. Reads interface assignments, events and property bindings; verifies native library-version GUID before exporting internals. Mismatch or unavailable version service is an explicit gap; never substitutes a default version or scans other types.")]
        public static ResponseMessage ReadUnifiedFaceplateInstance(string softwarePath, string expectedProject, string screenPath, string itemName, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Portal.ReadUnifiedFaceplateInstance(softwarePath, expectedProject, screenPath, itemName, typePath, version, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ListUnifiedLibraryFolder"), Description("[L2][HMI-Unified][READ]Page only direct type/folder names in ONE exact project-library folder, for locating a referenced script/faceplate. No recursive search or default-version substitution. / selects root. Inspect returned folders before choosing the next exact path.")]
        public static ResponseMessage ListUnifiedLibraryFolder(string softwarePath, string expectedProject, string folderPath = "/", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Portal.ListUnifiedLibraryFolder(softwarePath, expectedProject, folderPath, cursor, pageSize, budgetMs);
        [McpServerTool(Name = "ReleaseUnifiedReadCursor"), Description("[L2][HMI-Unified][READ]Release one collection cursor and its temporary export files. Never saves or closes TIA Portal. Cursors otherwise expire after 30 minutes without use or on project switch/server restart.")]
        public static ResponseMessage ReleaseUnifiedReadCursor(string cursor) => Portal.ReleaseUnifiedReadCursor(cursor);
    }
}
