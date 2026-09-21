using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ManagePlcBlockProtection"), Description("[L2][PLC-Software][WRITE] Read/protect/unprotect know-how protection of one exact block path via native PlcBlockProtectionProvider. Password is passed straight to TIA and never stored or logged; native invalid-password characters are reported. Refuses protecting an already protected block or unprotecting an unprotected one. Default preview; real change requires dryRun=false AND confirmProtectionChange=true, an Offline PLC, and is verified by IsKnowHowProtected readback. No save/compile/download.")]
        public static ResponseMessage ManagePlcBlockProtection(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | protect | unprotect.")] string action,
            string password="",
            [Description("confirmProtectionChange: must be true together with dryRun=false to change the protection.")] bool confirmProtectionChange=false,
            bool dryRun=true)
            => Portal.ManagePlcBlockProtection(softwarePath,blockPath,action,password,confirmProtectionChange,dryRun);
        [McpServerTool(Name="ManagePlcDataBlockSnapshot"), Description("[L2][PLC-Online][ONLINE] read/createSnapshot/loadSnapshotAsActualValues/loadStartValuesAsActualValues/exportSnapshot on one exact data block via native ValueService/InterfaceSnapshot. Siemens semantics: createSnapshot reads actual values from the CPU and load actions write values INTO the running CPU, so the PLC must already be online in TIA; this tool never goes online/offline. Load actions require confirmValueChange=true besides dryRun=false; no value readback exists, only native return. exportSnapshot writes a NEW absolute file and returns its SHA-256. ValueService is V21+ (V20 returns NotSupported). Default preview; no save/compile/download.")]
        public static ResponseMessage ManagePlcDataBlockSnapshot(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | createSnapshot | loadSnapshotAsActualValues | loadStartValuesAsActualValues | exportSnapshot.")] string action,
            string filePath="",
            [Description("confirmValueChange: must be true together with dryRun=false to change data block values.")] bool confirmValueChange=false,
            bool dryRun=true)
            => Portal.ManagePlcDataBlockSnapshot(softwarePath,blockPath,action,filePath,confirmValueChange,dryRun);
        [McpServerTool(Name="UpdatePlcProgram"), Description("[L2][PLC-Software][WRITE] Native PlcSoftware.UpdateProgram() (TIA 'Update program') for one exact PLC software path. Returns void natively; PLC scalars before/after are reported, program content changes are not enumerated. Default preview; real execution requires dryRun=false AND confirmUpdate=true and an Offline PLC. No save/compile/download.")]
        public static ResponseMessage UpdatePlcProgram(
            string softwarePath,
            [Description("confirmUpdate: must be true together with dryRun=false to update the program.")] bool confirmUpdate=false,
            bool dryRun=true)
            => Portal.UpdatePlcProgram(softwarePath,confirmUpdate,dryRun);
        [McpServerTool(Name="ReadPlcBlockFingerprints"), Description("[L2][PLC-Online][ONLINE] Read fingerprint data (identifier/value pairs) from the CPU via native FingerprintDataProvider.GetFingerprintData using the configured route whose CPU address exactly equals targetIpAddress; several PG/PC adapters to that address require the exact pgPcInterface name. Optional CPU password is passed only to the native legitimation callback. Paginated offset/limit<=500. Default preview resolves the route without contacting the PLC; dryRun=false contacts the PLC read-only. No project or PLC change.")]
        public static ResponseMessage ReadPlcBlockFingerprints(
            string softwarePath,
            [Description("targetIpAddress: IP address of the PLC as ScanAccessibleDevices lists it.")] string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true)
            => Portal.ReadPlcBlockFingerprints(softwarePath,targetIpAddress,pgPcInterface,password,offset,limit,dryRun);
        [McpServerTool(Name="ImportPlcAlarmInstanceTexts"), Description("[L2][PLC-Alarms][WRITE] Native PlcAlarmTextProvider.ImportInstanceTextsFromXlsx for one exact PLC from an existing absolute xlsx and a JSON array of exact project culture names (each must be a project language). Returns native state and log file path; text changes need separate export/readback. Default preview; execution requires Offline PLC. No save/compile/download.")]
        public static ResponseMessage ImportPlcAlarmInstanceTexts(
            string softwarePath,
            string filePath,
            [Description("culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages).")] string culturesJson,
            bool dryRun=true)
            => Portal.ImportPlcAlarmInstanceTexts(softwarePath,filePath,culturesJson,dryRun);
        [McpServerTool(Name="ManagePlcAlarmTextList"), Description("[L2][PLC-Alarms][WRITE] read/createFromMasterCopy/delete PLC alarm text lists (PlcAlarmTextlistGroup system+user lists; entries are not exposed by Openness). read paginates Name/ID/ListRange, optionally one exact name. createFromMasterCopy needs an open library name and exact master copy path; copyMode ThrowIfExists/Rename/Replace optional. delete refuses system lists and requires confirmDelete=true besides dryRun=false; absence verified. Default preview, Offline PLC for execution; no save/compile/download.")]
        public static ResponseMessage ManagePlcAlarmTextList(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("action: read | createFromMasterCopy | delete.")] string action="read",
            [Description("name: exact alarm text list name (delete) or the name to give the copy (createFromMasterCopy).")] string name="",
            [Description("libraryName: for createFromMasterCopy - global library name ('' = the project library).")] string libraryName="",
            [Description("masterCopyPath: for createFromMasterCopy - 'Folder/Name' of the master copy.")] string masterCopyPath="",
            [Description("copyMode: for createFromMasterCopy - ThrowIfExists | Rename | Replace.")] string copyMode="",
            [Description("confirmDelete: must be true together with dryRun=false to delete.")] bool confirmDelete=false,
            [Description("offset: first list to return (paging).")] int offset=0,
            [Description("limit: maximum lists to return.")] int limit=100,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => Portal.ManagePlcAlarmTextList(softwarePath,action,name,libraryName,masterCopyPath,copyMode,confirmDelete,offset,limit,dryRun);
    }
}
