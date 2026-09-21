using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ManagePlcExternalSources"), Description("[L2][PLC-Software][WRITE] Native external source files of the exact PLC (PlcSoftware.ExternalSourceGroup) or of a unit (unitName + unitKind), in the root system group or a user group at groupPath: list (PlcExternalSourceGroup Name / ExternalSources / Groups), read, createFromFile (PlcExternalSourceComposition.CreateFromFile(name, filePath): an existing ASCII .scl/.awl/.stl/.db/.udt file on the TIA Portal machine), createFromMasterCopy (libraryName empty = project library, masterCopyPath, copyMode ThrowIfExists/Rename/Replace), delete, generateBlocks (PlcExternalSource.GenerateBlocksFromSource with generateOption None/KeepOnError, optionally into an exact block or type user group via targetKind + targetGroupPath; existing objects are overwritten natively, returns the generated names), createGroup / renameGroup / deleteGroup (PlcExternalSourceUserGroup; deletion only when empty). Every write is read back. Default dryRun=true; delete needs confirmDelete=true; real writes require an Offline PLC. No save/compile/download. Source generation from blocks stays GeneratePlcSourceFromBlocks.")]
        public static ResponseMessage ManagePlcExternalSources(
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
            => Portal.ManagePlcExternalSources(softwarePath,action,name,unitName,unitKind,groupPath,filePath,libraryName,masterCopyPath,copyMode,generateOption,targetKind,targetGroupPath,newName,confirmDelete,dryRun);
        [McpServerTool(Name="ReadPlcSystemGroups"), Description("[L2][PLC-Software][READ] Typed read of the system-generated groups of the exact PLC (or of a unit via unitName + unitKind): PlcBlockSystemGroup.SystemBlockGroups as a PlcSystemBlockGroup tree (Name, blocks with number / class / language when includeBlocks, nested Groups to maxDepth) and PlcTypeSystemGroup.SystemTypeGroups (PlcSystemTypeGroup Name / Types). First 200 objects per group. No modification.")]
        public static ResponseMessage ReadPlcSystemGroups(string softwarePath, string unitName="", string unitKind="unit", bool includeBlocks=true, int maxDepth=4)
            => Portal.ReadPlcSystemGroups(softwarePath,unitName,unitKind,includeBlocks,maxDepth);
        [McpServerTool(Name="ReadPlcTagTableConstants"), Description("[L2][PLC-Software][READ] Constants of one exact PLC tag table (tablePath under TagTableGroup, or a unit's TagTableGroup via unitName + unitKind): PlcTagTable.UserConstants and SystemConstants as typed PlcConstant rows (Name, DataTypeName, Value, kind user/system); kind filters. Paginated. User constants are edited with ManagePlcTag kind=constant. No modification.")]
        public static ResponseMessage ReadPlcTagTableConstants(string softwarePath, string tablePath, string kind="all", string unitName="", string unitKind="unit", int offset=0, int limit=200)
            => Portal.ReadPlcTagTableConstants(softwarePath,tablePath,kind,unitName,unitKind,offset,limit);
        [McpServerTool(Name="ExchangePlcAlarmTextListsXlsx"), Description("[L2][PLC-Alarms][WRITE] Native XLSX exchange of PLC alarm text lists through PlcAlarmTextListProvider of the exact PLC or of a unit (unitName + unitKind): export writes a NEW absolute .xlsx (every user text list in every project language, or the filtered overload with textListNamesJson + culturesJson, both required together; TIA refuses system text lists and inactive languages natively) and returns the TextListXlsxResult state, log file and file hash; import (importOption None refuses existing lists, Override replaces their entries) needs confirmImport=true besides dryRun=false and an Offline PLC. No save/compile/download.")]
        public static ResponseMessage ExchangePlcAlarmTextListsXlsx(string softwarePath, string action, string filePath, string unitName="", string unitKind="unit", string textListNamesJson="[]", string culturesJson="[]", string importOption="None", bool confirmImport=false, bool dryRun=true)
            => Portal.ExchangePlcAlarmTextListsXlsx(softwarePath,action,filePath,unitName,unitKind,textListNamesJson,culturesJson,importOption,confirmImport,dryRun);
        [McpServerTool(Name="ManagePlcTableEntries"), Description("[L2][PLC-Software][WRITE] Entries of one exact watch or force table (tableKind + tablePath under WatchAndForceTableGroup) as typed rows in native order: PlcWatchTableEntry (Name, Address, DisplayFormat, MonitorTrigger, ModifyTrigger, ModifyValue, ModifyIntention), PlcForceTableEntry (Name, Address, DisplayFormat, MonitorTrigger, ForceValue, ForceIntention) and comment rows (PlcTableCommentEntry). Watch tables also accept createComment (PlcTableCommentEntryComposition.Create), deleteEntry (0-based entryIndex from a read, confirmDelete=true) and deleteTable (PlcWatchTable.Delete, confirmDelete=true, verified absent), each counted back; tag rows are added with SetWatchTableModifyValue (SimaticML round trip - the typed API only creates comment rows); force tables are read-only here on purpose. Configuration values only, never online data. Default dryRun=true; no save/compile/download.")]
        public static ResponseMessage ManagePlcTableEntries(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tableKind: watch | force.")] string tableKind,
            [Description("tablePath: 'Group/Table' path under the watch-and-force-table group, e.g. 'MCP_W/MCP_WT' (bare name for a root table).")] string tablePath,
            [Description("action: read | createComment | deleteEntry | deleteTable (force tables: read only).")] string action="read",
            [Description("entryIndex: 0-based row index from a read, for deleteEntry.")] int entryIndex=-1,
            [Description("confirmDelete: must be true together with dryRun=false for deleteEntry / deleteTable.")] bool confirmDelete=false,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true,
            [Description("offset: first row to return (paging).")] int offset=0,
            [Description("limit: maximum rows to return.")] int limit=200)
            => Portal.ManagePlcTableEntries(softwarePath,tableKind,tablePath,action,entryIndex,confirmDelete,dryRun,offset,limit);
        [McpServerTool(Name="ExportPlcProDiagInfo"), Description("[L2][PLC-Software][READ] Native CodeBlock.ExportProDIAGInfo: writes the alarm messages of one exact ProDiag FB (blockPath under BlockGroup or a unit's BlockGroup) as CSV files into an existing directoryPath on the TIA Portal machine and hashes the new files. Refused before the call unless the block is a ProDiag FB and consistent (compile first). Default dryRun=true; no project change.")]
        public static ResponseMessage ExportPlcProDiagInfo(string softwarePath, string blockPath, string directoryPath, string unitName="", string unitKind="unit", bool dryRun=true)
            => Portal.ExportPlcProDiagInfo(softwarePath,blockPath,directoryPath,unitName,unitKind,dryRun);
    }
}
