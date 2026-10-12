using System;
using TiaMcp.Adapters;

namespace TiaMcp.Adapters.Contracts
{
    [Flags]
    public enum AdapterCapabilities
    {
        None = 0,
        PortalSession = 1,
        PlcProgram = 2,
        PlcData = 4,
        Hardware = 8,
        VersionControl = 16,
        HmiExport = 32,
        // Compiled native features; the facet flags above describe implemented surfaces.
        SourceGenerationResults = 64,
        RedundantPlc = 128,
        WatchTableRead = 256,
        WatchTableExport = 512,
        TechnologyObjectExport = 1024,
        Safety = 2048,
        TechnologyObjectGroups = 4096,
        HardwareCatalog = 8192,
        PlcDocuments = 16384,
        StudioSession = 32768,
        GenerateSource = 65536,
        VersionControlInitial = 131072,
        VersionControlModern = 262144
    }

    // Facets preserve the engine's arguments, defaults, results and exceptions.
    // Hosts still own engine lifetime, thread selection and wire error mapping.
    public interface IOpennessAdapter
    {
        public string ReleaseKey { get; }
        public string ApiIdentity { get; }
        public AdapterCapabilities Capabilities { get; }
        public IPortalSession? PortalSession { get; }
        public IPlcProgram? PlcProgram { get; }
        public IPlcData? PlcData { get; }
        public IHardware? Hardware { get; }
        public IVersionControl? VersionControl { get; }
        public IHmiExport? HmiExport { get; }
        public IStudioSession? StudioSession { get; }
    }

    public interface IPortalSession
    {
        public PlcConnectionResult Attach(int processId);
        public PlcProjectDetails[] ListProjects();
        public void RequireProjectIdentity(string expectedProjectFile);
        public PlcMutationResult BindProject(string projectName,string expectedProjectFile);
        public PlcMutationResult OpenProject(string path, bool dryRun = true);
        public PlcMutationResult CreateProject(string directoryPath, string projectName, bool dryRun = true);
        public PlcMutationResult SaveProject(bool dryRun = true);
        public PlcMutationResult CloseProject(bool dryRun = true);
        public void UnbindProject();
        public string ReadProjectTree();
        public PlcRuntimeState ReadState();
        public PlcProcessQuery ReadPortalProcessProjects();
        public PlcConnectReadiness ReadPortalConnectReadiness(int processId);
        public PlcDisconnectResult Disconnect();
    }
    public interface IPlcProgram
    {
        public PlcCompileResult CompileSoftware(string softwarePath, string password="", bool dryRun = true);
        public PlcExternalSourceDeleteResult DeletePlcExternalSource(string softwarePath,string groupPath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        public PlcDocumentExportResult ExportAsDocuments(string softwarePath,string blockPath,string exportPath,bool preservePath=false,string expectedPlanHash="",bool dryRun=true);
        public PlcMutationResult ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath=false, bool dryRun = true);
        public PlcBatchExportResult ExportBlocks(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true);
        public PlcBatchDocumentExportResult ExportBlocksAsDocuments(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,bool preservePath=false,string expectedPlanHash="",bool dryRun=true);
        public PlcSpecialExportResult ExportPlcWatchTable(string softwarePath,string watchTableName,string exportPath,string expectedPlanHash="",bool dryRun=true);
        public PlcSpecialExportResult ExportTechnologyObject(string softwarePath,string toName,string exportPath,string expectedPlanHash="",bool dryRun=true);
        public PlcExternalSourceGenerationResult GenerateBlocksFromExternalSource(string softwarePath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        public PlcMutationResult ImportBlocks(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true);
        public PlcBatchImportResult ImportBlocksFromDirectory(string softwarePath,string groupPath,string dir,string regexName="",bool overwrite=false,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",int maxItems=128);
        public PlcBatchDocumentImportResult ImportBlocksFromDocuments(string softwarePath,string groupPath,string importPath,string[] fileNamesWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        public PlcDocumentImportResult ImportFromDocuments(string softwarePath,string groupPath,string importPath,string fileNameWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        public PlcExternalSourceImportResult ImportPlcExternalSource(string softwarePath,string groupPath,string filePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        public PlcBatchImportResult ImportPlcProgramFromDirectory(string softwarePath,string sourceDir,string typeGroupPath="",string tagFolderPath="",string technologyFolderPath="",string blockGroupPath="",string regexName="",bool compileAfter=false,bool stopOnImportFailure=true,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",bool overwrite=false,int maxItems=128);
        public PlcObjectInfo[] ListBlocks(string plc);
        public PlcObjectInfo[] ListPlcs();
        public PlcExternalSourceImportPlan PlanPlcExternalSourceImport(string softwarePath,string groupPath,string filePath,string allowedFilePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        public PlcBlockHierarchy ReadBlockHierarchy(string softwarePath);
        public PlcBlockDetails ReadBlockInfo(string softwarePath, string blockPath);
        public PlcBlockDetails[] ReadBlocks(string softwarePath, string regexName = "");
        public string[] ReadExternalSourceNames(string softwarePath);
        public PlcSoftwareDetails ReadSoftwareInfo(string softwarePath);
        public PlcSoftwareTreeDetails ReadSoftwareTree(string softwarePath);
        public PlcSupplementaryReadResult ReadTechnologyObjects(string softwarePath);
        public PlcSupplementaryReadResult ReadWatchTableNames(string softwarePath);
    }
    public interface IPlcData
    {
        public PlcObjectInfo[] ListTypes(string plc);
        public PlcObjectInfo[] ListTagTables(string plc);
        public PlcObjectInfo[] ListTags(string plc, string table);
        public PlcObjectInfo[] ListUserConstants(string plc, string table);
        public PlcObjectInfo[] ListSystemConstants(string plc, string table);
        public PlcTypeDetails ReadTypeInfo(string softwarePath, string typePath);
        public PlcTypeDetails[] ReadTypes(string softwarePath, string regexName = "");
        public string[] ReadTagTableNames(string softwarePath);
        public PlcMutationResult ExportType(string softwarePath, string exportPath, string typePath, bool preservePath=false, bool dryRun = true);
        public PlcBatchExportResult ExportTypes(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true);
        public PlcMutationResult ExportTagTable(string softwarePath, string tagTableName, string exportPath, bool dryRun = true);
        public PlcMutationResult ImportTypes(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true);
        public PlcMutationResult ImportTagTables(string softwarePath, string folderPath, string importPath, bool overwrite = false, bool dryRun = true);
        public PlcMutationResult CreateTagTable(string plc, string group, string name, bool dryRun = true);
        public PlcMutationResult CreateTag(string plc, string table, string name, string dataType, string address, bool dryRun = true);
        public PlcMutationResult CreateUserConstant(string plc, string table, string name, string dataType, string value, bool dryRun = true);
    }
}
