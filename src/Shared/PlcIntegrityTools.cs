using System;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcIntegrityTools
    {
        private readonly PlcOrganisationPortService _integrity;
        public PlcIntegrityTools(PlcOrganisationPortService service) => _integrity = service;
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
            => _integrity.ReadPlcSoftwareUnits(softwarePath,unitName,unitKind,includeContents,offset,limit);
        [McpServerTool(Name="GetPlcChecksums"), Description("[L2][PLC-Software][READ] Native PlcChecksumProvider of the exact PLC software: Software (program checksum) and TextLists checksum strings, both read-only and null until the program is compiled; PLCs without checksum support (GetService returns null) answer supported=false. No modification. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcChecksumsV4(string softwarePath)
            => EngineeringToolContract.Run("GetPlcChecksums", false, () => ReadPlcChecksums(softwarePath));

        public ResponseMessage ReadPlcChecksums(string softwarePath)
            => _integrity.ReadPlcChecksums(softwarePath);
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
            => _integrity.ReadPlcObjectFingerprints(softwarePath,objectKind,objectPath,unitName,unitKind);
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
            => _integrity.ManagePlcBlockWriteProtection(softwarePath,blockPath,action,password,newPassword,confirmProtectionChange,dryRun,unitName,unitKind);
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
            => _integrity.ManagePlcSoftwareUnit(softwarePath,action,name,relatedUnit,relationType,propertiesJson,dryRun,unitKind,commentsJson,libraryName,masterCopyPath,copyMode);
        [McpServerTool(Name="SetPlcUnitObjectAccess"), Description("[L2][PLC-Software][WRITE] Publish/unpublish a block or PLC type inside an exact software unit using official Access attribute. access=Published/Unpublished. objectPath includes nested groups relative to the unit's block/type root; OB publication is refused by native API. dryRun=true default; writes require Offline and readback. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult SetPlcUnitObjectAccessV4(
            string softwarePath,
            string unitName,
            [Description("block | type. Object family inside the exact software unit.")] string objectKind,
            string objectPath,
            [Description("Published | Unpublished. Native software-unit object publication state.")] string access,
            bool dryRun=true)
            => EngineeringToolContract.Run("SetPlcUnitObjectAccess", !dryRun, () => SetPlcUnitObjectAccess(softwarePath, unitName, objectKind, objectPath, access, dryRun));

        public ResponseMessage SetPlcUnitObjectAccess(
            string softwarePath,
            string unitName,
            string objectKind,
            string objectPath,
            string access,
            bool dryRun=true)
            => _integrity.SetPlcUnitObjectAccess(softwarePath,unitName,objectKind,objectPath,access,dryRun);
    }
}
