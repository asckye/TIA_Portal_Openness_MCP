using System;
using System.Collections.Generic;

namespace TiaMcp.PlcWorker
{
    internal static class WorkerOperations
    {
        internal static readonly HashSet<string> Names=new HashSet<string>(StringComparer.Ordinal) {
            "ReadBlockInfo","ReadTypeInfo","ReadExternalSourceNames",
            "Attach","ListProjects","BindProject","OpenProject","CreateProject","SaveProject","CloseProject","ReadProjectTree",
            "ReadBlocks","ReadBlockHierarchy","ReadTypes","ReadTagTableNames","ListTags","ListUserConstants","ListSystemConstants",
            "ExportBlock","ExportType","ExportTagTable","ImportBlocks","ImportTypes","ImportTagTables","CreateTagTable","CreateTag","CreateUserConstant","CompileSoftware"
        };
    }
}
