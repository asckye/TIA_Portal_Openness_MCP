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

        [McpServerTool(Name="ListPlcSoftwareUnits"), Description("[L2][PLC-Software][READ] Typed read of the native software units of one exact PLC (PlcUnitProvider.UnitGroup): the PlcUnitSystemGroup (Name on V21, unit / safety-unit counts) and every PlcUnit / PlcSafetyUnit with Name, Author, NamespacePreset, Comment per culture, relations (RelatedObject / RelationType) and, with includeContents, the names in its BlockGroup (blocks, groups, system block groups), TypeGroup (types, groups, named value type documents), TagTableGroup, ExternalSourceGroup and PlcAlarmTextlistGroup (first 200 each). unitKind all/unit/safety, optional exact unitName. Paginated; PLCs without unit support answer NotSupported. No modification. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcSoftwareUnitsV4(
            string softwarePath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - all | unit | safety.")] string unitKind="all",
            [Description("includeContents: true also returns the contents of each unit.")] bool includeContents=true,
            int offset=0,
            int limit=50)
            => EngineeringToolContract.Run("ListPlcSoftwareUnits", false, () => ReadPlcSoftwareUnits(softwarePath, unitName, unitKind, includeContents, offset, limit));

        public ResponseMessage ReadPlcSoftwareUnits(
            string softwarePath,
            string unitName="",
            string unitKind="all",
            bool includeContents=true,
            int offset=0,
            int limit=50)
            => _softwareUnitDeep.ReadPlcSoftwareUnits(softwarePath,unitName,unitKind,includeContents,offset,limit);
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
        [McpServerTool(Name="GetPlcChecksums"), Description("[L2][PLC-Software][READ] Native PlcChecksumProvider of the exact PLC software: Software (program checksum) and TextLists checksum strings, both read-only and null until the program is compiled; PLCs without checksum support (GetService returns null) answer supported=false. No modification. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcChecksumsV4(string softwarePath)
            => EngineeringToolContract.Run("GetPlcChecksums", false, () => ReadPlcChecksums(softwarePath));

        public ResponseMessage ReadPlcChecksums(string softwarePath)
            => _softwareUnitDeep.ReadPlcChecksums(softwarePath);
        [McpServerTool(Name="GetPlcObjectFingerprints"), Description("[L2][PLC-Software][READ] Offline fingerprints of one exact block (objectKind=block, path under BlockGroup) or UDT (objectKind=type, path under TypeGroup), optionally inside a unit (unitName + unitKind), via native FingerprintProvider.GetFingerprints: every Fingerprint Id (Code/Interface/Properties/Comments/LibraryType/Texts/Alarms/Supervisions/TechnologyObject/Events/TextualInterface/ProgramCode) with its value; fingerprints consider user input only. An inconsistent object is refused natively (compile first). Online CPU fingerprints are GetPlcBlockFingerprints. No modification. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcObjectFingerprintsV4(
            string softwarePath,
            [Description("objectKind: block | type.")] string objectKind,
            string objectPath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit")
            => EngineeringToolContract.Run("GetPlcObjectFingerprints", false, () => ReadPlcObjectFingerprints(softwarePath, objectKind, objectPath, unitName, unitKind));

        public ResponseMessage ReadPlcObjectFingerprints(
            string softwarePath,
            string objectKind,
            string objectPath,
            string unitName="",
            string unitKind="unit")
            => _softwareUnitDeep.ReadPlcObjectFingerprints(softwarePath,objectKind,objectPath,unitName,unitKind);
        [McpServerTool(Name="ManagePlcBlockWriteProtection"), Description("[L2][PLC-Software][WRITE] Block write protection (V21 PlcBlockWriteProtectionProvider, distinct from know-how protection): read IsDefined / IsProtected; define sets the password, protect / unprotect toggle protection with it, change rotates it to newPassword, remove calls Change(password, null) dropping password and protection. The official state machine is enforced before any call (define refused when defined, protect needs a defined password, ...), passwords go in as SecureString and are never echoed, native invalid-password characters are checked, every change is verified by readback. blockPath under BlockGroup or a unit's BlockGroup (unitName + unitKind). Real change requires dryRun=false AND confirmProtectionChange=true and an Offline PLC; V20 answers NotSupported. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcBlockWriteProtectionV4(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | define | protect | unprotect | change | remove.")] string action,
            string password="",
            [Description("newPassword: the new password; never logged.")] string newPassword="",
            [Description("confirmProtectionChange: must be true together with dryRun=false to change the protection.")] bool confirmProtectionChange=false,
            bool dryRun=true,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit")
            => EngineeringToolContract.Run("ManagePlcBlockWriteProtection", !dryRun && action != "read" && action != "list", () => ManagePlcBlockWriteProtection(softwarePath, blockPath, action, password, newPassword, confirmProtectionChange, dryRun, unitName, unitKind));

        public ResponseMessage ManagePlcBlockWriteProtection(
            string softwarePath,
            string blockPath,
            string action,
            string password="",
            string newPassword="",
            bool confirmProtectionChange=false,
            bool dryRun=true,
            string unitName="",
            string unitKind="unit")
            => _softwareUnitDeep.ManagePlcBlockWriteProtection(softwarePath,blockPath,action,password,newPassword,confirmProtectionChange,dryRun,unitName,unitKind);
        [McpServerTool(Name="ManageProjectCompilationSettings"), Description("[L2][Project][WRITE] Project simulation / virtual PLC support during block compilation (official 'Updating project properties'): read or update properties {IsSimulationDuringBlockCompilationEnabled, IsVirtualPlcDuringBlockCompilationEnabled} (booleans). V20 reads / writes the two Project properties; V21 removed them and goes through the project's PlcSimulationSettingsProvider / VirtualPlcSettingsProvider services; every write is read back. Default dryRun=true; no save. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageProjectCompilationSettingsV4([Description("read | update. ")] string action="read", AttributeMap<Scalar> properties = null!, bool dryRun=true)
            => EngineeringToolContract.Run("ManageProjectCompilationSettings", !dryRun && action != "read" && action != "list", () => ManageProjectCompilationSettings(action, EngineeringToolContract.Json(properties), dryRun));

        public ResponseMessage ManageProjectCompilationSettings(string action="read", string propertiesJson="{}", bool dryRun=true)
            => _softwareUnitDeep.ManageProjectCompilationSettings(action,propertiesJson,dryRun);
        [McpServerTool(Name="ManagePlcSoftwareUnit"), Description("[L2][PLC-Software][WRITE] Native software units (PlcUnitProvider.UnitGroup): list (standard and safety units), read (typed PlcUnitBase row with contents), create, createFromMasterCopy (libraryName empty = project library, masterCopyPath, copyMode ThrowIfExists/Rename/Replace), delete, update (properties Author / NamespacePreset; comments {culture: text} writes Comment per active project language; Name refused), createRelation (relatedUnit + relationType SoftwareUnit/NonUnitDB/TODB) and deleteRelation. unitKind=safety addresses the system-generated PlcSafetyUnit (read / update / relations only; it can neither be created nor deleted). Every write is verified by readback. dryRun=true default; real writes require an Offline PLC. Delete includes contained objects. No save/compile/download or implicit publication of objects (SetPlcUnitObjectAccess). Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcSoftwareUnitV4(
            string softwarePath,
            [Description("action: the operation to perform - list | read | create | createFromMasterCopy | delete | update | createRelation | deleteRelation.")] string action,
            string name="",
            [Description("relatedUnit: exact name of the related software unit.")] string relatedUnit="",
            [Description("relationType: SoftwareUnit | NonUnitDB | TODB.")] string relationType="",
            AttributeMap<Scalar> properties = null!,
            bool dryRun=true,
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("comments: Object language tag -> comment text.")] AttributeMap<string> comments = null!,
            string libraryName="",
            string masterCopyPath="",
            [Description("copyMode: what to do when the name exists - ThrowIfExists | Rename | Replace.")] string copyMode="")
            => EngineeringToolContract.Run("ManagePlcSoftwareUnit", !dryRun && action != "read" && action != "list", () => ManagePlcSoftwareUnit(softwarePath, action, name, relatedUnit, relationType, EngineeringToolContract.Json(properties), dryRun, unitKind, EngineeringToolContract.Json(comments), libraryName, masterCopyPath, copyMode));

        public ResponseMessage ManagePlcSoftwareUnit(
            string softwarePath,
            string action,
            string name="",
            string relatedUnit="",
            string relationType="",
            string propertiesJson="{}",
            bool dryRun=true,
            string unitKind="unit",
            string commentsJson="{}",
            string libraryName="",
            string masterCopyPath="",
            string copyMode="")
            => _softwareUnitDeep.ManagePlcSoftwareUnit(softwarePath,action,name,relatedUnit,relationType,propertiesJson,dryRun,unitKind,commentsJson,libraryName,masterCopyPath,copyMode);
    }
}
