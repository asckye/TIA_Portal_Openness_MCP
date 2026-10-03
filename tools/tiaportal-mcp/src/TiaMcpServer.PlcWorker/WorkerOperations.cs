using System;
using System.Collections.Generic;

namespace TiaMcp.PlcWorker
{
    internal static class WorkerOperations
    {
        internal static readonly HashSet<string> Names=new HashSet<string>(StringComparer.Ordinal) {
            "SearchHardwareCatalog",
            "ReadState","ReadPortalProcessProjects","ReadPortalConnectReadiness",
            "ReadWatchTableNames","ReadTechnologyObjects",
            "PlanPlcExternalSourceImport","DeletePlcExternalSource","AddDeviceWithFallback",
            "ImportPlcExternalSource","GenerateBlocksFromExternalSource",
            "ReadSoftwareInfo","ReadSoftwareTree",
            "ReadBlockInfo","ReadTypeInfo","ReadExternalSourceNames",
            "Attach","Disconnect","ListProjects","BindProject","OpenProject","CreateProject","SaveProject","CloseProject","ReadProjectTree",
            "ReadBlocks","ReadBlockHierarchy","ReadTypes","ReadTagTableNames","ListTags","ListUserConstants","ListSystemConstants",
            "ImportFromDocuments","ImportBlocksFromDocuments","ImportBlocksFromDirectory","ImportPlcProgramFromDirectory","ExportAsDocuments","ExportBlocksAsDocuments","ExportPlcWatchTable","ExportTechnologyObject","ExportBlocks","ExportTypes","ExportBlock","ExportType","ExportTagTable","ImportBlocks","ImportTypes","ImportTagTables","CreateTagTable","CreateTag","CreateUserConstant","CompileSoftware"
        };
    }
}
