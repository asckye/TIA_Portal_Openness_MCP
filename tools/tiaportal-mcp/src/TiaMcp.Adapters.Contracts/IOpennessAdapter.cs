using System;
using TiaMcp.PlcFoundation;

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
        PlcDocuments = 16384
    }

    // Facets preserve the engine's arguments, defaults, results and exceptions.
    // Hosts still own engine lifetime, thread selection and wire error mapping.
    public interface IOpennessAdapter
    {
        string ReleaseKey { get; }
        string ApiIdentity { get; }
        AdapterCapabilities Capabilities { get; }
        IPortalSession? PortalSession { get; }
        IPlcProgram? PlcProgram { get; }
        IPlcData? PlcData { get; }
        IHardware? Hardware { get; }
        IVersionControl? VersionControl { get; }
        IHmiExport? HmiExport { get; }
    }

    public interface IPortalSession
    {
        PlcConnectionResult Attach(int processId);
        PlcProjectDetails[] ListProjects();
        void RequireProjectIdentity(string expectedProjectFile);
        PlcMutationResult BindProject(string projectName,string expectedProjectFile);
        PlcMutationResult OpenProject(string path, bool dryRun = true);
        PlcMutationResult CreateProject(string directoryPath, string projectName, bool dryRun = true);
        PlcMutationResult SaveProject(bool dryRun = true);
        PlcMutationResult CloseProject(bool dryRun = true);
        void UnbindProject();
        string ReadProjectTree();
        PlcRuntimeState ReadState();
        PlcProcessQuery ReadPortalProcessProjects();
        PlcConnectReadiness ReadPortalConnectReadiness(int processId);
        PlcDisconnectResult Disconnect();
    }
    public interface IPlcProgram
    {
        PlcCompileResult CompileSoftware(string softwarePath, string password="", bool dryRun = true);
        PlcExternalSourceDeleteResult DeletePlcExternalSource(string softwarePath,string groupPath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        PlcDocumentExportResult ExportAsDocuments(string softwarePath,string blockPath,string exportPath,bool preservePath=false,string expectedPlanHash="",bool dryRun=true);
        PlcMutationResult ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath=false, bool dryRun = true);
        PlcBatchExportResult ExportBlocks(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true);
        PlcBatchDocumentExportResult ExportBlocksAsDocuments(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,bool preservePath=false,string expectedPlanHash="",bool dryRun=true);
        PlcSpecialExportResult ExportPlcWatchTable(string softwarePath,string watchTableName,string exportPath,string expectedPlanHash="",bool dryRun=true);
        PlcSpecialExportResult ExportTechnologyObject(string softwarePath,string toName,string exportPath,string expectedPlanHash="",bool dryRun=true);
        PlcExternalSourceGenerationResult GenerateBlocksFromExternalSource(string softwarePath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        PlcMutationResult ImportBlocks(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true);
        PlcBatchImportResult ImportBlocksFromDirectory(string softwarePath,string groupPath,string dir,string regexName="",bool overwrite=false,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",int maxItems=128);
        PlcBatchDocumentImportResult ImportBlocksFromDocuments(string softwarePath,string groupPath,string importPath,string[] fileNamesWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        PlcDocumentImportResult ImportFromDocuments(string softwarePath,string groupPath,string importPath,string fileNameWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        PlcExternalSourceImportResult ImportPlcExternalSource(string softwarePath,string groupPath,string filePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        PlcBatchImportResult ImportPlcProgramFromDirectory(string softwarePath,string sourceDir,string typeGroupPath="",string tagFolderPath="",string technologyFolderPath="",string blockGroupPath="",string regexName="",bool compileAfter=false,bool stopOnImportFailure=true,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",bool overwrite=false,int maxItems=128);
        PlcObjectInfo[] ListBlocks(string plc);
        PlcObjectInfo[] ListPlcs();
        PlcExternalSourceImportPlan PlanPlcExternalSourceImport(string softwarePath,string groupPath,string filePath,string allowedFilePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
        PlcBlockHierarchy ReadBlockHierarchy(string softwarePath);
        PlcBlockDetails ReadBlockInfo(string softwarePath, string blockPath);
        PlcBlockDetails[] ReadBlocks(string softwarePath, string regexName = "");
        string[] ReadExternalSourceNames(string softwarePath);
        PlcSoftwareDetails ReadSoftwareInfo(string softwarePath);
        PlcSoftwareTreeDetails ReadSoftwareTree(string softwarePath);
        PlcSupplementaryReadResult ReadTechnologyObjects(string softwarePath);
        PlcSupplementaryReadResult ReadWatchTableNames(string softwarePath);
    }
    public interface IPlcData
    {
        PlcObjectInfo[] ListTypes(string plc);
        PlcObjectInfo[] ListTagTables(string plc);
        PlcObjectInfo[] ListTags(string plc, string table);
        PlcObjectInfo[] ListUserConstants(string plc, string table);
        PlcObjectInfo[] ListSystemConstants(string plc, string table);
        PlcTypeDetails ReadTypeInfo(string softwarePath, string typePath);
        PlcTypeDetails[] ReadTypes(string softwarePath, string regexName = "");
        string[] ReadTagTableNames(string softwarePath);
        PlcMutationResult ExportType(string softwarePath, string exportPath, string typePath, bool preservePath=false, bool dryRun = true);
        PlcBatchExportResult ExportTypes(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true);
        PlcMutationResult ExportTagTable(string softwarePath, string tagTableName, string exportPath, bool dryRun = true);
        PlcMutationResult ImportTypes(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true);
        PlcMutationResult ImportTagTables(string softwarePath, string folderPath, string importPath, bool overwrite = false, bool dryRun = true);
        PlcMutationResult CreateTagTable(string plc, string group, string name, bool dryRun = true);
        PlcMutationResult CreateTag(string plc, string table, string name, string dataType, string address, bool dryRun = true);
        PlcMutationResult CreateUserConstant(string plc, string table, string name, string dataType, string value, bool dryRun = true);
    }
    public interface IHardware { }
    public interface IVersionControl { }
    public interface IHmiExport { }
}
