using System.Reflection;

namespace TiaMcp.PlcFoundation
{
    // The software read partial uses the SDK doubles in NativeShapeFakes. Other operations
    // record the engine boundary so every facet can be exercised without a licensed SDK.
    public sealed partial class PlcFoundationEngine
    {
        public string ReleaseKey => typeof(PlcFoundationEngine).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a=>a.Key=="FacetTestReleaseKey").Value!;
        internal string? Operation;
        internal object?[] Arguments = Array.Empty<object?>();
        internal object? Result;
        internal Exception? Failure;
        internal int Calls, ThreadId;
        private object? Record(string operation,params object?[] arguments)
        {
            Operation=operation; Arguments=arguments; Calls++; ThreadId=Environment.CurrentManagedThreadId;
            if(Failure!=null) throw Failure;
            return Result;
        }
        public PlcBatchDocumentExportResult ExportBlocksAsDocuments(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,bool preservePath=false,string expectedPlanHash="",bool dryRun=true) => (PlcBatchDocumentExportResult)Record(nameof(ExportBlocksAsDocuments), softwarePath, groupPath, exportPath, recursive, maxItems, preservePath, expectedPlanHash, dryRun)!;
        public PlcBatchDocumentImportResult ImportBlocksFromDocuments(string softwarePath,string groupPath,string importPath,string[] fileNamesWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => (PlcBatchDocumentImportResult)Record(nameof(ImportBlocksFromDocuments), softwarePath, groupPath, importPath, fileNamesWithoutExtension, overwrite, dryRun, expectedPlanHash, confirm, expectedProjectFile)!;
        public PlcBatchExportResult ExportBlocks(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true) => (PlcBatchExportResult)Record(nameof(ExportBlocks), softwarePath, groupPath, exportPath, recursive, maxItems, expectedInventoryHash, dryRun)!;
        public PlcBatchExportResult ExportTypes(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true) => (PlcBatchExportResult)Record(nameof(ExportTypes), softwarePath, groupPath, exportPath, recursive, maxItems, expectedInventoryHash, dryRun)!;
        public PlcBatchImportResult ImportBlocksFromDirectory(string softwarePath,string groupPath,string dir,string regexName="",bool overwrite=false,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",int maxItems=128) => (PlcBatchImportResult)Record(nameof(ImportBlocksFromDirectory), softwarePath, groupPath, dir, regexName, overwrite, dryRun, importOrder, expectedPlanHash, confirm, expectedProjectFile, maxItems)!;
        public PlcBatchImportResult ImportPlcProgramFromDirectory(string softwarePath,string sourceDir,string typeGroupPath="",string tagFolderPath="",string technologyFolderPath="",string blockGroupPath="",string regexName="",bool compileAfter=false,bool stopOnImportFailure=true,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",bool overwrite=false,int maxItems=128) => (PlcBatchImportResult)Record(nameof(ImportPlcProgramFromDirectory), softwarePath, sourceDir, typeGroupPath, tagFolderPath, technologyFolderPath, blockGroupPath, regexName, compileAfter, stopOnImportFailure, dryRun, importOrder, expectedPlanHash, confirm, expectedProjectFile, overwrite, maxItems)!;
        public PlcDocumentExportResult ExportAsDocuments(string softwarePath,string blockPath,string exportPath,bool preservePath=false,string expectedPlanHash="",bool dryRun=true) => (PlcDocumentExportResult)Record(nameof(ExportAsDocuments), softwarePath, blockPath, exportPath, preservePath, expectedPlanHash, dryRun)!;
        public PlcDocumentImportResult ImportFromDocuments(string softwarePath,string groupPath,string importPath,string fileNameWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => (PlcDocumentImportResult)Record(nameof(ImportFromDocuments), softwarePath, groupPath, importPath, fileNameWithoutExtension, overwrite, dryRun, expectedPlanHash, confirm, expectedProjectFile)!;
        public PlcExternalSourceDeleteResult DeletePlcExternalSource(string softwarePath,string groupPath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => (PlcExternalSourceDeleteResult)Record(nameof(DeletePlcExternalSource), softwarePath, groupPath, externalSourceName, dryRun, expectedPlanHash, confirm, expectedProjectFile)!;
        public PlcExternalSourceImportPlan PlanPlcExternalSourceImport(string softwarePath,string groupPath,string filePath,string allowedFilePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => (PlcExternalSourceImportPlan)Record(nameof(PlanPlcExternalSourceImport), softwarePath, groupPath, filePath, allowedFilePath, dryRun, expectedPlanHash, confirm, expectedProjectFile)!;
        public PlcExternalSourceImportResult ImportPlcExternalSource(string softwarePath,string groupPath,string filePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => (PlcExternalSourceImportResult)Record(nameof(ImportPlcExternalSource), softwarePath, groupPath, filePath, dryRun, expectedPlanHash, confirm, expectedProjectFile)!;
        public PlcExternalSourceGenerationResult GenerateBlocksFromExternalSource(string softwarePath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => (PlcExternalSourceGenerationResult)Record(nameof(GenerateBlocksFromExternalSource), softwarePath, externalSourceName, dryRun, expectedPlanHash, confirm, expectedProjectFile)!;
        public string ReadProjectTree() => (string)Record(nameof(ReadProjectTree))!;
        public string[] ReadExternalSourceNames(string softwarePath) => (string[])Record(nameof(ReadExternalSourceNames), softwarePath)!;
        public PlcBlockDetails[] ReadBlocks(string softwarePath, string regexName = "") => (PlcBlockDetails[])Record(nameof(ReadBlocks), softwarePath, regexName)!;
        public PlcBlockDetails ReadBlockInfo(string softwarePath, string blockPath) => (PlcBlockDetails)Record(nameof(ReadBlockInfo), softwarePath, blockPath)!;
        public PlcTypeDetails ReadTypeInfo(string softwarePath, string typePath) => (PlcTypeDetails)Record(nameof(ReadTypeInfo), softwarePath, typePath)!;
        public PlcTypeDetails[] ReadTypes(string softwarePath, string regexName = "") => (PlcTypeDetails[])Record(nameof(ReadTypes), softwarePath, regexName)!;
        public string[] ReadTagTableNames(string softwarePath) => (string[])Record(nameof(ReadTagTableNames), softwarePath)!;
        public PlcBlockHierarchy ReadBlockHierarchy(string softwarePath) => (PlcBlockHierarchy)Record(nameof(ReadBlockHierarchy), softwarePath)!;
        public PlcSpecialExportResult ExportPlcWatchTable(string softwarePath,string watchTableName,string exportPath,string expectedPlanHash="",bool dryRun=true) => (PlcSpecialExportResult)Record(nameof(ExportPlcWatchTable), softwarePath, watchTableName, exportPath, expectedPlanHash, dryRun)!;
        public PlcSpecialExportResult ExportTechnologyObject(string softwarePath,string toName,string exportPath,string expectedPlanHash="",bool dryRun=true) => (PlcSpecialExportResult)Record(nameof(ExportTechnologyObject), softwarePath, toName, exportPath, expectedPlanHash, dryRun)!;
        public PlcSupplementaryReadResult ReadWatchTableNames(string softwarePath) => (PlcSupplementaryReadResult)Record(nameof(ReadWatchTableNames), softwarePath)!;
        public PlcSupplementaryReadResult ReadTechnologyObjects(string softwarePath) => (PlcSupplementaryReadResult)Record(nameof(ReadTechnologyObjects), softwarePath)!;
        public PlcConnectionResult Attach(int processId) => (PlcConnectionResult)Record(nameof(Attach), processId)!;
        public PlcProjectDetails[] ListProjects() => (PlcProjectDetails[])Record(nameof(ListProjects))!;
        public void RequireProjectIdentity(string expectedProjectFile) => Record(nameof(RequireProjectIdentity), expectedProjectFile);
        public PlcMutationResult BindProject(string projectName,string expectedProjectFile) => (PlcMutationResult)Record(nameof(BindProject), projectName, expectedProjectFile)!;
        public PlcMutationResult OpenProject(string path, bool dryRun = true) => (PlcMutationResult)Record(nameof(OpenProject), path, dryRun)!;
        public PlcMutationResult CreateProject(string directoryPath, string projectName, bool dryRun = true) => (PlcMutationResult)Record(nameof(CreateProject), directoryPath, projectName, dryRun)!;
        public PlcMutationResult SaveProject(bool dryRun = true) => (PlcMutationResult)Record(nameof(SaveProject), dryRun)!;
        public PlcMutationResult CloseProject(bool dryRun = true) => (PlcMutationResult)Record(nameof(CloseProject), dryRun)!;
        public void UnbindProject() => Record(nameof(UnbindProject));
        public PlcObjectInfo[] ListPlcs() => (PlcObjectInfo[])Record(nameof(ListPlcs))!;
        public PlcObjectInfo[] ListBlocks(string plc) => (PlcObjectInfo[])Record(nameof(ListBlocks), plc)!;
        public PlcObjectInfo[] ListTypes(string plc) => (PlcObjectInfo[])Record(nameof(ListTypes), plc)!;
        public PlcObjectInfo[] ListTagTables(string plc) => (PlcObjectInfo[])Record(nameof(ListTagTables), plc)!;
        public PlcObjectInfo[] ListTags(string plc, string table) => (PlcObjectInfo[])Record(nameof(ListTags), plc, table)!;
        public PlcObjectInfo[] ListUserConstants(string plc, string table) => (PlcObjectInfo[])Record(nameof(ListUserConstants), plc, table)!;
        public PlcObjectInfo[] ListSystemConstants(string plc, string table) => (PlcObjectInfo[])Record(nameof(ListSystemConstants), plc, table)!;
        public PlcMutationResult ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath=false, bool dryRun = true) => (PlcMutationResult)Record(nameof(ExportBlock), softwarePath, blockPath, exportPath, preservePath, dryRun)!;
        public PlcMutationResult ExportType(string softwarePath, string exportPath, string typePath, bool preservePath=false, bool dryRun = true) => (PlcMutationResult)Record(nameof(ExportType), softwarePath, exportPath, typePath, preservePath, dryRun)!;
        public PlcMutationResult ExportTagTable(string softwarePath, string tagTableName, string exportPath, bool dryRun = true) => (PlcMutationResult)Record(nameof(ExportTagTable), softwarePath, tagTableName, exportPath, dryRun)!;
        public PlcMutationResult ImportBlocks(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true) => (PlcMutationResult)Record(nameof(ImportBlocks), softwarePath, groupPath, importPath, overwrite, dryRun)!;
        public PlcMutationResult ImportTypes(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true) => (PlcMutationResult)Record(nameof(ImportTypes), softwarePath, groupPath, importPath, overwrite, dryRun)!;
        public PlcMutationResult ImportTagTables(string softwarePath, string folderPath, string importPath, bool overwrite = false, bool dryRun = true) => (PlcMutationResult)Record(nameof(ImportTagTables), softwarePath, folderPath, importPath, overwrite, dryRun)!;
        public PlcMutationResult CreateTagTable(string plc, string group, string name, bool dryRun = true) => (PlcMutationResult)Record(nameof(CreateTagTable), plc, group, name, dryRun)!;
        public PlcMutationResult CreateTag(string plc, string table, string name, string dataType, string address, bool dryRun = true) => (PlcMutationResult)Record(nameof(CreateTag), plc, table, name, dataType, address, dryRun)!;
        public PlcMutationResult CreateUserConstant(string plc, string table, string name, string dataType, string value, bool dryRun = true) => (PlcMutationResult)Record(nameof(CreateUserConstant), plc, table, name, dataType, value, dryRun)!;
        public PlcCompileResult CompileSoftware(string softwarePath, string password="", bool dryRun = true) => (PlcCompileResult)Record(nameof(CompileSoftware), softwarePath, password, dryRun)!;
        public PlcDisconnectResult Disconnect() => (PlcDisconnectResult)Record(nameof(Disconnect))!;
        public PlcRuntimeState ReadState() => (PlcRuntimeState)Record(nameof(ReadState))!;
        public PlcProcessQuery ReadPortalProcessProjects() => (PlcProcessQuery)Record(nameof(ReadPortalProcessProjects))!;
        public PlcConnectReadiness ReadPortalConnectReadiness(int processId) => (PlcConnectReadiness)Record(nameof(ReadPortalConnectReadiness), processId)!;
    }
}
