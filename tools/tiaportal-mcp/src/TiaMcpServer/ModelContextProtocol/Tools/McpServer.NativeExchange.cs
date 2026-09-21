using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ManageProjectLanguage"), Description("[L2][Project][WRITE] Read/activate/deactivate/setEditing/setReference project language by exact culture. Default preview. Editing/reference must be active, cannot deactivate either; readback verified; no save/compile/download.")]
        public static ResponseMessage ManageProjectLanguage(
            [Description("action: read | activate | deactivate | setEditing | setReference.")] string action="read",
            [Description("culture: exact language tag, e.g. 'en-US', 'zh-CN' (required for every action except read).")] string culture="",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => Portal.ManageProjectLanguage(action,culture,dryRun);
        [McpServerTool(Name="CreatePlcInstanceDb"), Description("[L2][PLC-Software][WRITE] Create a native instance DB from an exact FB and destination block group. Default preview; requires offline PLC when executing. No save/compile/download.")]
        public static ResponseMessage CreatePlcInstanceDb(
            string softwarePath,
            [Description("fbPath: 'Group/Name' of the function block the instance DB is created for.")] string fbPath,
            string name,
            string groupPath="",
            [Description("autoNumber: true lets TIA assign the block number.")] bool autoNumber=true,
            [Description("number: block number when autoNumber is false.")] int number=0,
            bool dryRun=true)
            => Portal.CreatePlcInstanceDb(softwarePath,fbPath,name,groupPath,autoNumber,number,dryRun);
        [McpServerTool(Name="GeneratePlcSourceFromBlocks"), Description("[L2][PLC-Software][FILE] Generate native source from 1..500 exact block paths (JSON array). New absolute output file only; SHA-256 returned. Unsupported block languages rejected; default preview.")]
        public static ResponseMessage GeneratePlcSourceFromBlocks(
            string softwarePath,
            [Description("blockPathsJson: JSON array of block paths.")] string blockPathsJson,
            string filePath,
            bool dryRun=true)
            => Portal.GeneratePlcSourceFromBlocks(softwarePath,blockPathsJson,filePath,dryRun);
        [McpServerTool(Name="GeneratePlcLoadableFile"), Description("[L2][PLC-Software][FILE] Native GenerateLoadable for exact blocks or units with explicit TargetOption enum name. New absolute output file; default preview. Does not download or assert compiled content completeness.")]
        public static ResponseMessage GeneratePlcLoadableFile(
            string softwarePath,
            [Description("objectPathsJson: JSON array of object paths.")] string objectPathsJson,
            [Description("objectKind: blocks | units.")] string objectKind,
            [Description("targetOption: Openness loadable-file target option name ('' = default).")] string targetOption,
            string filePath,
            bool dryRun=true)
            => Portal.GeneratePlcLoadableFile(softwarePath,objectPathsJson,objectKind,targetOption,filePath,dryRun);
        [McpServerTool(Name="RetrieveProjectArchive"), Description("[L2][Project][WRITE] Retrieve an archive into a new absolute directory and bind returned project. Requires connected Portal with no open project/session; never closes existing projects. upgrade=false, dryRun=true.")]
        public static ResponseMessage RetrieveProjectArchive(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            string destinationDirectory,
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            bool dryRun=true)
            => Portal.RetrieveProjectArchive(archivePath,destinationDirectory,upgrade,dryRun);
        [McpServerTool(Name="ExportProjectTexts"), Description("[L2][Project][FILE] Native project text export for explicit source/target cultures to a new absolute file. Returns API completion and file hash; default preview.")]
        public static ResponseMessage ExportProjectTexts(
            string filePath,
            [Description("sourceCulture: source language tag, e.g. 'en-US'.")] string sourceCulture,
            [Description("targetCulture: target language tag, e.g. 'zh-CN'.")] string targetCulture,
            bool dryRun=true)
            => Portal.ExportProjectTexts(filePath,sourceCulture,targetCulture,dryRun);
        [McpServerTool(Name="ImportProjectTexts"), Description("[L2][Project][WRITE] Native project text import. updateSourceLanguage explicitly controls updating SOURCE language, not a generic overwrite switch. Default preview; returns native diagnostics; no save/compile/download.")]
        public static ResponseMessage ImportProjectTexts(
            string filePath,
            [Description("updateSourceLanguage: true also writes the source-language texts.")] bool updateSourceLanguage,
            bool dryRun=true)
            => Portal.ImportProjectTexts(filePath,updateSourceLanguage,dryRun);
        [McpServerTool(Name="ManageGlobalLibrary"), Description("[L2][Library][WRITE] Global library lifecycle: list (open libraries), infos (GlobalLibraryComposition.GetGlobalLibraryInfos: every library this Portal knows with path, type and IsOpen), create/open/openInfo (Open(GlobalLibraryInfo) by exact name)/retrieve/save/saveAs/close, and archive (UserGlobalLibrary.Archive(destinationDirectory, archiveName, archiveMode None/Compressed/DiscardRestorableData/DiscardRestorableDataAndCompressed); the library must be saved first). Exact expected name, new destination; preview by default. Close is explicit, never auto-save. Upgrade opening requires explicit ReadWrite.")]
        public static ResponseMessage ManageGlobalLibrary(
            [Description("action: the operation to perform - list | infos | create | open | openInfo | retrieve | save | saveAs | close | archive.")] string action,
            string libraryName="",
            string filePath="",
            string destinationDirectory="",
            [Description("openMode: how to open the library - ReadOnly or ReadWrite.")] string openMode="ReadOnly",
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            [Description("archiveName: file name of the library archive to write.")] string archiveName="",
            [Description("archiveMode: None | Compressed | DiscardRestorableData | DiscardRestorableDataAndCompressed.")] string archiveMode="Compressed",
            bool dryRun=true)
            => Portal.ManageGlobalLibrary(action,libraryName,filePath,destinationDirectory,openMode,upgrade,archiveName,archiveMode,dryRun);
        [McpServerTool(Name="ManageLibraryFolder"), Description("[L2][Library][WRITE] Read/create/rename/delete exact types or masterCopies folder. Empty-folder deletion only, no recursive deletion or save. Default preview.")]
        public static ResponseMessage ManageLibraryFolder(
            [Description("folderKind: types | masterCopies.")] string folderKind,
            string folderPath,
            [Description("action: the operation to perform - read | create | rename | delete.")] string action,
            string libraryName="",
            string newName="",
            bool dryRun=true)
            => Portal.ManageLibraryFolder(folderKind,folderPath,action,libraryName,newName,dryRun);
        [McpServerTool(Name="ManagePlcTagDefinition"), Description("[L2][PLC-Software][WRITE] Native tag/constant read/create/update/delete in exact table path. Creation requires dataType and logical address or constant value. Public scalar edits (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; constants: Value); Comment is a MultilingualText and is written per culture - propertiesJson {\"Comment\":\"text\"} goes to the project editing language, {\"Comment\":{\"zh-CN\":\"文本\",\"en-US\":\"text\"}} per active culture (inactive culture = NotFound, activate with ManageProjectLanguage); commentBefore / commentAfter report every culture. Default preview, execution requires Offline. No runtime value write, save/compile/download.")]
        public static ResponseMessage ManagePlcTagDefinition(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("tablePath: 'Group/Table' path of the tag table, e.g. 'Default tag table'.")] string tablePath,
            [Description("name: exact tag or constant name.")] string name,
            [Description("kind: tag | constant.")] string kind,
            [Description("action: read | create | update | delete.")] string action,
            [Description("dataType: for create / update - PLC data type, e.g. Bool, Int, Real.")] string dataType="",
            [Description("addressOrValue: for create / update - logical address of a tag (e.g. %M0.0, %I0.0) or the value of a constant.")] string addressOrValue="",
            [Description("propertiesJson: JSON object of further scalar properties (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; Comment as text or per culture).")] string propertiesJson="{}",
            [Description("dryRun: true (default) previews; false executes (project must be offline).")] bool dryRun=true)
            => Portal.ManagePlcTagDefinition(softwarePath,tablePath,name,kind,action,dataType,addressOrValue,propertiesJson,dryRun);
    }
}
