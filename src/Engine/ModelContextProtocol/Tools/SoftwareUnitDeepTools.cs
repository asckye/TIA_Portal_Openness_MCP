using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SoftwareUnitDeepTools
    {
        private readonly SoftwareUnitDeepService _softwareUnitDeep;

        public SoftwareUnitDeepTools(SoftwareUnitDeepService softwareUnitDeep) => _softwareUnitDeep = softwareUnitDeep;

        [McpServerTool(Name="ManagePlcDocuments"), Description("[L2][PLC-Software][WRITE] Named value type documents (objectKind=document: PlcTypeGroup.Documents / PlcDocument) and UDT documents (objectKind=type: PlcTypeComposition / PlcType) of the exact PLC type group (root, groupPath under it, or a unit's TypeGroup via unitName + unitKind). list / read; export = ExportAsDocuments(directoryPath, name) with the DocumentExportResult (State, ExportedDocuments hashed, native messages) and the new files, refusing to overwrite name.*; import = ImportFromDocuments(directoryPath, name, importOption None/Override/SkipInactiveCultures/ActivateInactiveCultures) returning DocumentImportResultForSplDocument.ImportedDocuments or DocumentImportResultForTypes.ImportedPlcTypes plus DocumentResultMessage lines, verified by Find; createFromMasterCopy (libraryName empty = project library, masterCopyPath, copyMode ThrowIfExists/Rename/Replace) and createFromLibraryType (typePath + version of a PlcDocumentLibraryTypeVersion / PlcTypeLibraryTypeVersion, updatePathsMode) instantiate into the group (document CreateFrom is V21+). Default dryRun=true; real writes need an Offline PLC; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcDocumentsV4(
            string softwarePath,
            [Description("action: the operation to perform - list | read | export | import | createFromMasterCopy | createFromLibraryType.")] string action,
            [Description("objectKind: document | type.")] string objectKind="document",
            string name="",
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            string groupPath="",
            [Description("directoryPath: folder on the TIA machine that receives the files.")] string directoryPath="",
            [Description("importOption: import option - None | Override | SkipInactiveCultures | ActivateInactiveCultures.")] string importOption="Override",
            string libraryName="",
            string masterCopyPath="",
            [Description("copyMode: what to do when the name exists - ThrowIfExists | Rename | Replace.")] string copyMode="",
            string typePath="",
            string version="",
            [Description("updatePathsMode: UpdatePathsInTarget | KeepExistingPathsInTarget | ThrowIfPathsConflict.")] string updatePathsMode="",
            bool dryRun=true)
            => EngineeringToolContract.Run("ManagePlcDocuments", !dryRun && action != "read" && action != "list", () => ManagePlcDocuments(softwarePath, action, objectKind, name, unitName, unitKind, groupPath, directoryPath, importOption, libraryName, masterCopyPath, copyMode, typePath, version, updatePathsMode, dryRun));

        public ResponseMessage ManagePlcDocuments(
            string softwarePath,
            string action,
            string objectKind="document",
            string name="",
            string unitName="",
            string unitKind="unit",
            string groupPath="",
            string directoryPath="",
            string importOption="Override",
            string libraryName="",
            string masterCopyPath="",
            string copyMode="",
            string typePath="",
            string version="",
            string updatePathsMode="",
            bool dryRun=true)
            => _softwareUnitDeep.ManagePlcDocuments(softwarePath,action,objectKind,name,unitName,unitKind,groupPath,directoryPath,importOption,libraryName,masterCopyPath,copyMode,typePath,version,updatePathsMode,dryRun);
    }
}
