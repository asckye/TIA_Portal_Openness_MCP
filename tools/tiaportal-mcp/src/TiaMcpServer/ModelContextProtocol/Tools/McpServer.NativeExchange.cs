using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ManageProjectLanguage"), Description("[L2][Project][WRITE] Read/activate/deactivate/setEditing/setReference project language by exact culture. Default preview. Editing/reference must be active, cannot deactivate either; readback verified; no save/compile/download.")]
        public static ResponseMessage ManageProjectLanguage(string action="read", string culture="", bool dryRun=true)
            => Portal.ManageProjectLanguage(action,culture,dryRun);
        [McpServerTool(Name="CreatePlcInstanceDb"), Description("[L2][PLC-Software][WRITE] Create a native instance DB from an exact FB and destination block group. Default preview; requires offline PLC when executing. No save/compile/download.")]
        public static ResponseMessage CreatePlcInstanceDb(string softwarePath, string fbPath, string name, string groupPath="", bool autoNumber=true, int number=0, bool dryRun=true)
            => Portal.CreatePlcInstanceDb(softwarePath,fbPath,name,groupPath,autoNumber,number,dryRun);
        [McpServerTool(Name="GeneratePlcSourceFromBlocks"), Description("[L2][PLC-Software][FILE] Generate native source from 1..500 exact block paths (JSON array). New absolute output file only; SHA-256 returned. Unsupported block languages rejected; default preview.")]
        public static ResponseMessage GeneratePlcSourceFromBlocks(string softwarePath, string blockPathsJson, string filePath, bool dryRun=true)
            => Portal.GeneratePlcSourceFromBlocks(softwarePath,blockPathsJson,filePath,dryRun);
        [McpServerTool(Name="GeneratePlcLoadableFile"), Description("[L2][PLC-Software][FILE] Native GenerateLoadable for exact blocks or units with explicit TargetOption enum name. New absolute output file; default preview. Does not download or assert compiled content completeness.")]
        public static ResponseMessage GeneratePlcLoadableFile(string softwarePath, string objectPathsJson, string objectKind, string targetOption, string filePath, bool dryRun=true)
            => Portal.GeneratePlcLoadableFile(softwarePath,objectPathsJson,objectKind,targetOption,filePath,dryRun);
        [McpServerTool(Name="RetrieveProjectArchive"), Description("[L2][Project][WRITE] Retrieve an archive into a new absolute directory and bind returned project. Requires connected Portal with no open project/session; never closes existing projects. upgrade=false, dryRun=true.")]
        public static ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade=false, bool dryRun=true)
            => Portal.RetrieveProjectArchive(archivePath,destinationDirectory,upgrade,dryRun);
        [McpServerTool(Name="ExportProjectTexts"), Description("[L2][Project][FILE] Native project text export for explicit source/target cultures to a new absolute file. Returns API completion and file hash; default preview.")]
        public static ResponseMessage ExportProjectTexts(string filePath, string sourceCulture, string targetCulture, bool dryRun=true)
            => Portal.ExportProjectTexts(filePath,sourceCulture,targetCulture,dryRun);
        [McpServerTool(Name="ImportProjectTexts"), Description("[L2][Project][WRITE] Native project text import. updateSourceLanguage explicitly controls updating SOURCE language, not a generic overwrite switch. Default preview; returns native diagnostics; no save/compile/download.")]
        public static ResponseMessage ImportProjectTexts(string filePath, bool updateSourceLanguage, bool dryRun=true)
            => Portal.ImportProjectTexts(filePath,updateSourceLanguage,dryRun);
        [McpServerTool(Name="ManageGlobalLibrary"), Description("[L2][Library][WRITE] Global library lifecycle: list (open libraries), infos (GlobalLibraryComposition.GetGlobalLibraryInfos: every library this Portal knows with path, type and IsOpen), create/open/openInfo (Open(GlobalLibraryInfo) by exact name)/retrieve/save/saveAs/close, and archive (UserGlobalLibrary.Archive(destinationDirectory, archiveName, archiveMode None/Compressed/DiscardRestorableData/DiscardRestorableDataAndCompressed); the library must be saved first). Exact expected name, new destination; preview by default. Close is explicit, never auto-save. Upgrade opening requires explicit ReadWrite.")]
        public static ResponseMessage ManageGlobalLibrary(string action, string libraryName="", string filePath="", string destinationDirectory="", string openMode="ReadOnly", bool upgrade=false, string archiveName="", string archiveMode="Compressed", bool dryRun=true)
            => Portal.ManageGlobalLibrary(action,libraryName,filePath,destinationDirectory,openMode,upgrade,archiveName,archiveMode,dryRun);
        [McpServerTool(Name="ManageLibraryFolder"), Description("[L2][Library][WRITE] Read/create/rename/delete exact types or masterCopies folder. Empty-folder deletion only, no recursive deletion or save. Default preview.")]
        public static ResponseMessage ManageLibraryFolder(string folderKind, string folderPath, string action, string libraryName="", string newName="", bool dryRun=true)
            => Portal.ManageLibraryFolder(folderKind,folderPath,action,libraryName,newName,dryRun);
        [McpServerTool(Name="ManagePlcTagDefinition"), Description("[L2][PLC-Software][WRITE] Native tag/constant read/create/update/delete in exact table path. Creation requires dataType and logical address or constant value. Public scalar edits (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; constants: Value); Comment is a MultilingualText and is written per culture - propertiesJson {\"Comment\":\"text\"} goes to the project editing language, {\"Comment\":{\"zh-CN\":\"文本\",\"en-US\":\"text\"}} per active culture (inactive culture = NotFound, activate with ManageProjectLanguage); commentBefore / commentAfter report every culture. Default preview, execution requires Offline. No runtime value write, save/compile/download.")]
        public static ResponseMessage ManagePlcTagDefinition(string softwarePath, string tablePath, string name, string kind, string action, string dataType="", string addressOrValue="", string propertiesJson="{}", bool dryRun=true)
            => Portal.ManagePlcTagDefinition(softwarePath,tablePath,name,kind,action,dataType,addressOrValue,propertiesJson,dryRun);
    }
}
