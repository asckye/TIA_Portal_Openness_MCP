using System;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcFoundation;
using TiaMcpServer.Siemens;

namespace TiaMcp.Adapters
{
    // Borrows an already validated engine. No native access, thread switch or ownership transfer.
    public sealed class OpennessAdapter : IOpennessAdapter
    {
        private readonly PlcFoundationEngine engine;
        public OpennessAdapter(PlcFoundationEngine engine)
        {
            this.engine=engine ?? throw new ArgumentNullException(nameof(engine));
            PortalSession=new SessionFacet(engine);
            PlcProgram=new ProgramFacet(engine);
            PlcData=new DataFacet(engine);
        }
        public string ReleaseKey => engine.ReleaseKey;
        public string ApiIdentity => OpennessReleaseContract.For(ReleaseKey).CoreAssemblyIdentity.FullName;
        public AdapterCapabilities Capabilities => AdapterCapabilities.PortalSession | AdapterCapabilities.PlcProgram | AdapterCapabilities.PlcData
#if PLC_SOURCE_RESULTS
            | AdapterCapabilities.SourceGenerationResults
#endif
#if PLC_RH
            | AdapterCapabilities.RedundantPlc
#endif
#if PLC_WATCH_READ
            | AdapterCapabilities.WatchTableRead
#endif
#if PLC_SPECIAL_EXPORT
            | AdapterCapabilities.WatchTableExport | AdapterCapabilities.TechnologyObjectExport
#endif
#if PLC_WATCH_EXPORT
            | AdapterCapabilities.WatchTableExport
#endif
#if PLC_SAFETY
            | AdapterCapabilities.Safety
#endif
#if PLC_TECH_GROUP_READ
            | AdapterCapabilities.TechnologyObjectGroups
#endif
#if PLC_HARDWARE_CATALOG
            | AdapterCapabilities.HardwareCatalog
#endif
#if PLC_DOCUMENT_EXPORT
            | AdapterCapabilities.PlcDocuments
#endif
            ;
        public IPortalSession PortalSession { get; }
        public IPlcProgram PlcProgram { get; }
        public IPlcData PlcData { get; }
        public IHardware? Hardware => null;
        public IVersionControl? VersionControl => null;
        public IHmiExport? HmiExport => null;

        private sealed class SessionFacet : IPortalSession
        {
            private readonly PlcFoundationEngine engine;
            internal SessionFacet(PlcFoundationEngine engine) { this.engine=engine; }
            public PlcConnectionResult Attach(int processId) => engine.Attach(processId);
            public PlcProjectDetails[] ListProjects() => engine.ListProjects();
            public void RequireProjectIdentity(string expectedProjectFile) => engine.RequireProjectIdentity(expectedProjectFile);
            public PlcMutationResult BindProject(string projectName,string expectedProjectFile) => engine.BindProject(projectName, expectedProjectFile);
            public PlcMutationResult OpenProject(string path, bool dryRun = true) => engine.OpenProject(path, dryRun);
            public PlcMutationResult CreateProject(string directoryPath, string projectName, bool dryRun = true) => engine.CreateProject(directoryPath, projectName, dryRun);
            public PlcMutationResult SaveProject(bool dryRun = true) => engine.SaveProject(dryRun);
            public PlcMutationResult CloseProject(bool dryRun = true) => engine.CloseProject(dryRun);
            public void UnbindProject() => engine.UnbindProject();
            public string ReadProjectTree() => engine.ReadProjectTree();
            public PlcRuntimeState ReadState() => engine.ReadState();
            public PlcProcessQuery ReadPortalProcessProjects() => engine.ReadPortalProcessProjects();
            public PlcConnectReadiness ReadPortalConnectReadiness(int processId) => engine.ReadPortalConnectReadiness(processId);
            public PlcDisconnectResult Disconnect() => engine.Disconnect();
        }

        private sealed class ProgramFacet : IPlcProgram
        {
            private readonly PlcFoundationEngine engine;
            internal ProgramFacet(PlcFoundationEngine engine) { this.engine=engine; }
            public PlcCompileResult CompileSoftware(string softwarePath, string password="", bool dryRun = true) => engine.CompileSoftware(softwarePath, password, dryRun);
            public PlcExternalSourceDeleteResult DeletePlcExternalSource(string softwarePath,string groupPath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => engine.DeletePlcExternalSource(softwarePath, groupPath, externalSourceName, dryRun, expectedPlanHash, confirm, expectedProjectFile);
            public PlcDocumentExportResult ExportAsDocuments(string softwarePath,string blockPath,string exportPath,bool preservePath=false,string expectedPlanHash="",bool dryRun=true) => engine.ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath, expectedPlanHash, dryRun);
            public PlcMutationResult ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath=false, bool dryRun = true) => engine.ExportBlock(softwarePath, blockPath, exportPath, preservePath, dryRun);
            public PlcBatchExportResult ExportBlocks(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true) => engine.ExportBlocks(softwarePath, groupPath, exportPath, recursive, maxItems, expectedInventoryHash, dryRun);
            public PlcBatchDocumentExportResult ExportBlocksAsDocuments(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,bool preservePath=false,string expectedPlanHash="",bool dryRun=true) => engine.ExportBlocksAsDocuments(softwarePath, groupPath, exportPath, recursive, maxItems, preservePath, expectedPlanHash, dryRun);
            public PlcSpecialExportResult ExportPlcWatchTable(string softwarePath,string watchTableName,string exportPath,string expectedPlanHash="",bool dryRun=true) => engine.ExportPlcWatchTable(softwarePath, watchTableName, exportPath, expectedPlanHash, dryRun);
            public PlcSpecialExportResult ExportTechnologyObject(string softwarePath,string toName,string exportPath,string expectedPlanHash="",bool dryRun=true) => engine.ExportTechnologyObject(softwarePath, toName, exportPath, expectedPlanHash, dryRun);
            public PlcExternalSourceGenerationResult GenerateBlocksFromExternalSource(string softwarePath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => engine.GenerateBlocksFromExternalSource(softwarePath, externalSourceName, dryRun, expectedPlanHash, confirm, expectedProjectFile);
            public PlcMutationResult ImportBlocks(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true) => engine.ImportBlocks(softwarePath, groupPath, importPath, overwrite, dryRun);
            public PlcBatchImportResult ImportBlocksFromDirectory(string softwarePath,string groupPath,string dir,string regexName="",bool overwrite=false,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",int maxItems=128) => engine.ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, overwrite, dryRun, importOrder, expectedPlanHash, confirm, expectedProjectFile, maxItems);
            public PlcBatchDocumentImportResult ImportBlocksFromDocuments(string softwarePath,string groupPath,string importPath,string[] fileNamesWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => engine.ImportBlocksFromDocuments(softwarePath, groupPath, importPath, fileNamesWithoutExtension, overwrite, dryRun, expectedPlanHash, confirm, expectedProjectFile);
            public PlcDocumentImportResult ImportFromDocuments(string softwarePath,string groupPath,string importPath,string fileNameWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => engine.ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, overwrite, dryRun, expectedPlanHash, confirm, expectedProjectFile);
            public PlcExternalSourceImportResult ImportPlcExternalSource(string softwarePath,string groupPath,string filePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => engine.ImportPlcExternalSource(softwarePath, groupPath, filePath, dryRun, expectedPlanHash, confirm, expectedProjectFile);
            public PlcBatchImportResult ImportPlcProgramFromDirectory(string softwarePath,string sourceDir,string typeGroupPath="",string tagFolderPath="",string technologyFolderPath="",string blockGroupPath="",string regexName="",bool compileAfter=false,bool stopOnImportFailure=true,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",bool overwrite=false,int maxItems=128) => engine.ImportPlcProgramFromDirectory(softwarePath, sourceDir, typeGroupPath, tagFolderPath, technologyFolderPath, blockGroupPath, regexName, compileAfter, stopOnImportFailure, dryRun, importOrder, expectedPlanHash, confirm, expectedProjectFile, overwrite, maxItems);
            public PlcObjectInfo[] ListBlocks(string plc) => engine.ListBlocks(plc);
            public PlcObjectInfo[] ListPlcs() => engine.ListPlcs();
            public PlcExternalSourceImportPlan PlanPlcExternalSourceImport(string softwarePath,string groupPath,string filePath,string allowedFilePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="") => engine.PlanPlcExternalSourceImport(softwarePath, groupPath, filePath, allowedFilePath, dryRun, expectedPlanHash, confirm, expectedProjectFile);
            public PlcBlockHierarchy ReadBlockHierarchy(string softwarePath) => engine.ReadBlockHierarchy(softwarePath);
            public PlcBlockDetails ReadBlockInfo(string softwarePath, string blockPath) => engine.ReadBlockInfo(softwarePath, blockPath);
            public PlcBlockDetails[] ReadBlocks(string softwarePath, string regexName = "") => engine.ReadBlocks(softwarePath, regexName);
            public string[] ReadExternalSourceNames(string softwarePath) => engine.ReadExternalSourceNames(softwarePath);
            public PlcSoftwareDetails ReadSoftwareInfo(string softwarePath) => engine.ReadSoftwareInfo(softwarePath);
            public PlcSoftwareTreeDetails ReadSoftwareTree(string softwarePath) => engine.ReadSoftwareTree(softwarePath);
            public PlcSupplementaryReadResult ReadTechnologyObjects(string softwarePath) => engine.ReadTechnologyObjects(softwarePath);
            public PlcSupplementaryReadResult ReadWatchTableNames(string softwarePath) => engine.ReadWatchTableNames(softwarePath);
        }

        private sealed class DataFacet : IPlcData
        {
            private readonly PlcFoundationEngine engine;
            internal DataFacet(PlcFoundationEngine engine) { this.engine=engine; }
            public PlcObjectInfo[] ListTypes(string plc) => engine.ListTypes(plc);
            public PlcObjectInfo[] ListTagTables(string plc) => engine.ListTagTables(plc);
            public PlcObjectInfo[] ListTags(string plc, string table) => engine.ListTags(plc, table);
            public PlcObjectInfo[] ListUserConstants(string plc, string table) => engine.ListUserConstants(plc, table);
            public PlcObjectInfo[] ListSystemConstants(string plc, string table) => engine.ListSystemConstants(plc, table);
            public PlcTypeDetails ReadTypeInfo(string softwarePath, string typePath) => engine.ReadTypeInfo(softwarePath, typePath);
            public PlcTypeDetails[] ReadTypes(string softwarePath, string regexName = "") => engine.ReadTypes(softwarePath, regexName);
            public string[] ReadTagTableNames(string softwarePath) => engine.ReadTagTableNames(softwarePath);
            public PlcMutationResult ExportType(string softwarePath, string exportPath, string typePath, bool preservePath=false, bool dryRun = true) => engine.ExportType(softwarePath, exportPath, typePath, preservePath, dryRun);
            public PlcBatchExportResult ExportTypes(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true) => engine.ExportTypes(softwarePath, groupPath, exportPath, recursive, maxItems, expectedInventoryHash, dryRun);
            public PlcMutationResult ExportTagTable(string softwarePath, string tagTableName, string exportPath, bool dryRun = true) => engine.ExportTagTable(softwarePath, tagTableName, exportPath, dryRun);
            public PlcMutationResult ImportTypes(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true) => engine.ImportTypes(softwarePath, groupPath, importPath, overwrite, dryRun);
            public PlcMutationResult ImportTagTables(string softwarePath, string folderPath, string importPath, bool overwrite = false, bool dryRun = true) => engine.ImportTagTables(softwarePath, folderPath, importPath, overwrite, dryRun);
            public PlcMutationResult CreateTagTable(string plc, string group, string name, bool dryRun = true) => engine.CreateTagTable(plc, group, name, dryRun);
            public PlcMutationResult CreateTag(string plc, string table, string name, string dataType, string address, bool dryRun = true) => engine.CreateTag(plc, table, name, dataType, address, dryRun);
            public PlcMutationResult CreateUserConstant(string plc, string table, string name, string dataType, string value, bool dryRun = true) => engine.CreateUserConstant(plc, table, name, dataType, value, dryRun);
        }
    }
}
