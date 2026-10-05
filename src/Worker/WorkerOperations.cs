using System;
using System.Collections.Generic;

namespace TiaMcp.PlcWorker
{
    internal static class WorkerOperations
    {
        internal const string DeviceCreationCandidate = "CreateHardwareDeviceCandidate";
        internal static bool IsDevicePreview(string name, string? mode) => name == DeviceCreationCandidate && (mode == null || mode == "preview");
        internal static bool IsReadOnly(string name) => name=="SearchHardwareCatalog" || name=="PlanPlcExternalSourceImport" || name=="ReadState" || name=="ReadPortalProcessProjects" || name=="ReadPortalConnectReadiness" || name=="ReadWatchTableNames" || name=="ReadTechnologyObjects" || name=="ReadSoftwareInfo" || name=="ReadSoftwareTree" || name=="ReadExternalSourceNames" || name=="ListTags" || name=="ListUserConstants" || name=="ListSystemConstants" || name=="ReadBlockInfo" || name=="ReadTypeInfo" || name=="ReadBlocks" || name=="ReadTypes" || name=="ReadTagTableNames" || name=="ReadBlockHierarchy" || name=="ReadProjectTree" || name=="ListProjects";

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
