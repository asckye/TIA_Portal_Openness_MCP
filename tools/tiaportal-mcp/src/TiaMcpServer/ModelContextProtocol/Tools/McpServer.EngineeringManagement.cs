using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="SetPlcUnitObjectAccess"), Description("[L2][PLC-Software][WRITE] Publish/unpublish a block or PLC type inside an exact software unit using official Access attribute. access=Published/Unpublished. objectPath includes nested groups relative to the unit's block/type root; OB publication is refused by native API. dryRun=true default; writes require Offline and readback. No save/compile/download.")]
        public static ResponseMessage SetPlcUnitObjectAccess(
            string softwarePath,
            string unitName,
            string objectKind,
            string objectPath,
            [Description("access: access level to set (Read, Write or None).")] string access,
            bool dryRun=true)
            => Portal.SetPlcUnitObjectAccess(softwarePath,unitName,objectKind,objectPath,access,dryRun);
        [McpServerTool(Name="ManagePlcSafety"), Description("[L2][Safety][WRITE] Official Siemens.Engineering.Safety administration of one exact F-PLC. read: password/login state, SafetySettings incl. AssignmentOfBlockNumbers (ManagementMode, From/To FB/FC/DB), SafetySystemVersion + applicable versions, EnableConsistentUploadFromFCpu/EnableFCommunicationIdTag attributes, runtime groups incl. FOBNumber/FOBCycleTime/FOBPhaseShift/FOBPriority, collective/software/hardware/communication-address F-signatures (V21), portal GlobalSettings and CPU Failsafe_FCapabilityActivated. Writes: createRuntimeGroup (optional mainSafetyBlockPath FC, or FB + mainSafetyInstanceDbPath), deleteRuntimeGroup, updateRuntimeGroup/updateSettings (propertiesJson: scalar properties, AssignmentOfBlockNumbers.*, SafetySystemVersion exact string, documented attributes), generateGlobalFIOStatusBlock, cleanSystemGeneratedObjects, generateBaseId (V21, F-BaseID CPUs), login/logoff/setPassword/revokePassword (password passed straight to TIA, never stored or logged). dryRun=true default; program edits require Offline and F-login when protected; delete/generate/clean/password actions and SafetyModeCanBeDisabled require confirmSafetyChange=true. No F-compile, save or download; API access is not functional safety acceptance.")]
        public static ResponseMessage ManagePlcSafety(
            string softwarePath,
            string action="read",
            [Description("runtimeGroup: exact name of the F-runtime group.")] string runtimeGroup="",
            string propertiesJson="{}",
            bool dryRun=true,
            string password="",
            [Description("confirmSafetyChange: must be true together with dryRun=false to change safety settings.")] bool confirmSafetyChange=false,
            [Description("mainSafetyBlockPath: path of the main safety block ('Group/Name').")] string mainSafetyBlockPath="",
            [Description("mainSafetyInstanceDbPath: path of the main safety instance DB.")] string mainSafetyInstanceDbPath="")
            => Portal.ManagePlcSafety(softwarePath,action,runtimeGroup,propertiesJson,dryRun,password,confirmSafetyChange,mainSafetyBlockPath,mainSafetyInstanceDbPath);
        [McpServerTool(Name="ManageSafetyGlobalSettings"), Description("[L2][Safety][WRITE] Official Safety GlobalSettings service of the connected TIA Portal (portal-wide, no project needed): read/update SafetyModificationsPossible, GenerationOfDefaultFailsafeProgram, ManagementOfFailsafeInSoftwareUnitsEnvironment, UsernameForFChangeHistory (<=256 chars; empty resets). propertiesJson holds only those four names. dryRun=true default; update reads every value back. SafetyModificationsPossible=false locks all F-program changes in TIA regardless of login.")]
        public static ResponseMessage ManageSafetyGlobalSettings(string action="read",string propertiesJson="{}",bool dryRun=true)
            => Portal.ManageSafetyGlobalSettings(action,propertiesJson,dryRun);
        [McpServerTool(Name="ReadSafetyBlockSignatures"), Description("[L2][Safety][READ] Per-block F-signatures via native PlcBlock.GetService<SafetySignatureProvider>() for one exact blockPath or every block of the PLC (recursive), plus the PLC collective/software/hardware/communication-address signatures (V21 ProgramSignatures) when includeProgramSignatures=true. Blocks without the service (non-F blocks, non-F-CPU) are counted, not listed. Value 0 = no valid signature (changed since the last F-compile). Offline project values, never read from the CPU. Paginated offset/limit<=500. No change.")]
        public static ResponseMessage ReadSafetyBlockSignatures(
            string softwarePath,
            string blockPath="",
            [Description("includeProgramSignatures: true also returns the F-program signatures (V21).")] bool includeProgramSignatures=true,
            int offset=0,
            int limit=100)
            => Portal.ReadSafetyBlockSignatures(softwarePath,blockPath,includeProgramSignatures,offset,limit);
        [McpServerTool(Name="ExportSafetyPrintout"), Description("[L2][Safety][FILE] Official STEP 7 Safety printout of one exact F-PLC via native SafetyPrintout.Print on the CPU device item: printer MicrosoftPrintToPdf (.pdf) or MicrosoftXpsDocumentWriter (.xps/.oxps), option All or Compact, documentLayout default DocuInfo_ISO_A4_Portrait. filePath is absolute on the MCP server, must not exist (native Print would overwrite) and its parent must exist; the Windows printer driver must be enabled on the TIA machine. dryRun=true default; execution returns the native bool, byte size and SHA-256. No project change.")]
        public static ResponseMessage ExportSafetyPrintout(
            string softwarePath,
            string filePath,
            [Description("printer: printer name ('' = the default printer).")] string printer="MicrosoftPrintToPdf",
            [Description("option: Openness print / export option name ('' = default).")] string option="All",
            [Description("documentLayout: document layout name ('' = default).")] string documentLayout="",
            bool dryRun=true)
            => Portal.ExportSafetyPrintout(softwarePath,filePath,printer,option,documentLayout,dryRun);
        [McpServerTool(Name="ManagePlcCertificate"), Description("[L2][Security][WRITE] Offline engineering certificates of the exact PLC DeviceItem (LocalCertificateManager.LocalCertificateStore): list, read, template (default CertificateTemplate for usage Tls/WebServer/OpcUaServer/OpcUaClient/OpcUaClientServer: Signature, SubjectCommonName, Usage, ValidFrom, ValidUntil, SubjectAlternativeNames), create (usage + template propertiesJson Signature Sha1RSA/Sha256RSA, SubjectCommonName, ValidFrom/ValidUntil ISO dates + subjectAlternativeNamesJson [{type Dns/Email/IP/Uri, value}] via SubjectAlternativeNameComposition.Create), import (filePath; optional password for protected files via Import(file, SecureString), never echoed), export (new file), delete (by exact certificateId), assign/unassign (WebserverCertificate/OpcUaServerCertificate dynamic attribute on the selected owner). dryRun=true default; no download/save; export refuses overwrite; private keys are never exported.")]
        public static ResponseMessage ManagePlcCertificate(
            string devicePathJson,
            string itemPathJson,
            string action,
            [Description("certificateId: certificate id from the read action.")] string certificateId="",
            string filePath="",
            [Description("usage: Tls | WebServer | OpcUaServer | OpcUaClient | OpcUaClientServer.")] string usage="",
            string propertiesJson="{}",
            [Description("assignment: assignment kind as the read action lists it.")] string assignment="",
            bool dryRun=true,
            [Description("assignmentItemPathJson: JSON array of device-item names the certificate is assigned to.")] string assignmentItemPathJson="",
            [Description("subjectAlternativeNamesJson: JSON array of subject alternative names for the certificate.")] string subjectAlternativeNamesJson="[]",
            string password="")
            => Portal.ManagePlcCertificate(devicePathJson,itemPathJson,action,certificateId,filePath,usage,propertiesJson,assignment,dryRun,assignmentItemPathJson,subjectAlternativeNamesJson,password);
        [McpServerTool(Name="ManageLibraryTypeVersion"), Description("[L2][Library][WRITE] Exact library type/version read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances. Empty libraryName selects project library; otherwise unique already-open global library. typePath relative to TypeFolder. Release requires newVersion and official dependenciesMode. findInstances is read-only and requires exact targetSoftwarePath; discard removes the selected editable version. updateInstances requires exact targetSoftwarePath; native type update chooses its applicable versions, not necessarily version argument. dryRun=true default. No export, save, close or compile. Semantic validity/dependency impact determined by native TIA.")]
        public static ResponseMessage ManageLibraryTypeVersion(
            string typePath,
            string version,
            [Description("action: the operation to perform - read | edit | release | setDefault | deleteVersion | updateInstances | discard | findInstances.")] string action,
            string libraryName="",
            [Description("newVersion: version string of the new type version, e.g. 'V1.0.1'.")] string newVersion="",
            [Description("dependenciesMode: how dependent types are handled by the action ('' = default).")] string dependenciesMode="",
            [Description("author: author text of the version.")] string author="",
            [Description("comment: comment text.")] string comment="",
            string targetSoftwarePath="",
            bool dryRun=true)
            => Portal.ManageLibraryTypeVersion(typePath,version,action,libraryName,newVersion,dependenciesMode,author,comment,targetSoftwarePath,dryRun);
        [McpServerTool(Name="CreateLibraryMasterCopy"), Description("[L2][Library][WRITE] Create a native master copy from an exact block/type/device/screen in an existing library folder. sourcePath is relative object path; for device use JSON array of exact group/station names. Empty libraryName=project library; otherwise already-open global library. dryRun=true default. Native IMasterCopySource required; no automatic save or close.")]
        public static ResponseMessage CreateLibraryMasterCopy(
            [Description("sourceKind: block | type | device | screen.")] string sourceKind,
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            string softwarePath="",
            string folderPath="",
            string libraryName="",
            bool dryRun=true)
            => Portal.CreateLibraryMasterCopy(sourceKind,sourcePath,softwarePath,folderPath,libraryName,dryRun);
        [McpServerTool(Name="ManageHardwareObject"), Description("[L2][Hardware][WRITE] Native deleteDevice/deleteItem/moveItem/copyItem. devicePathJson=[group,...,station] or [unique exact station name]; itemPathJson lists exact child names, preserving slashes in names. Move/copy require destinationDevicePathJson,destinationItemPathJson,position and pass native CanPlug check. dryRun=true default. Deletes may remove contained software. No save/download/online control.")]
        public static ResponseMessage ManageHardwareObject(
            [Description("devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name, e.g. [\"PLC_2\"].")] string devicePathJson,
            [Description("action: deleteDevice | deleteItem | moveItem | copyItem.")] string action,
            [Description("itemPathJson: JSON array of exact device-item names (deleteItem / moveItem / copyItem); [] for deleteDevice.")] string itemPathJson="[]",
            [Description("destinationDevicePathJson: for moveItem / copyItem - JSON array naming the destination station.")] string destinationDevicePathJson="[]",
            [Description("destinationItemPathJson: for moveItem / copyItem - JSON array of device-item names of the destination container.")] string destinationItemPathJson="[]",
            [Description("position: for moveItem / copyItem - target slot / position number (-1 = let TIA pick).")] int position=-1,
            [Description("dryRun: true (default) previews (CanPlug check only); false executes.")] bool dryRun=true)
            => Portal.ManageHardwareObject(devicePathJson,action,itemPathJson,destinationDevicePathJson,destinationItemPathJson,position,dryRun);
        [McpServerTool(Name="ImportPlcWatchTableOffline"), Description("[L2][PLC-Software][WRITE] Import native SimaticML watch tables into an exact existing group. Offline-only, no overwrite. Rejects force-table and mixed-object XML and DTD. dryRun=true default. Does not execute modify values, start monitoring, save or download. filePath is on the MCP server.")]
        public static ResponseMessage ImportPlcWatchTableOffline(string softwarePath,string filePath,string groupPath="",bool dryRun=true)
            => Portal.ImportPlcWatchTableOffline(softwarePath,filePath,groupPath,dryRun);
        [McpServerTool(Name="ManageTechnologyObject"), Description("[L2][PLC-Software][WRITE] Native technology object read/create/delete/setParameter. Exact objectPath relative to TechnologicalObjectGroup, including user folders. create requires official typeIdentifier and version (official 'Overview of technology objects and versions': S7-1500 TO_PositioningAxis / TO_SpeedAxis / ... >= V5.0 with FW >= 2.8, PID_Compact >= V2.3; version as 'major.minor'). WARNING (real project, crash 9 in the handoff): Create(\"MCP_Axis\", \"TO_PositioningAxis\", 6.0) on a CPU 1515F-2 PN V2.9 in TIA V21 threw NonRecoverableException and TIA Portal exited, while TO_PositioningAxis / TO_SpeedAxis 5.0 and PID_Compact 2.3 were created normally on the same CPU - a version the CPU does not offer is not refused cleanly, so use the lowest version of the official table (S7-1500 motion 5.0, PID_Compact 2.3) and save the project before a create. setParameter takes exact parameter name and scalar valueJson. dryRun=true default; writes require Offline. No save/compile/download; dependencies not analyzed.")]
        public static ResponseMessage ManageTechnologyObject(
            string softwarePath,
            string objectPath,
            [Description("action: the operation to perform - read | create | delete | setParameter.")] string action,
            [Description("typeIdentifier: catalog type identifier of the form 'OrderNumber:6ES7 ...' or 'OrderNumber:.../V2.9' (SearchHardwareCatalog / ManageHardwareUtilities normalizeTypeIdentifier).")] string typeIdentifier="",
            string version="",
            [Description("parameter: exact parameter name.")] string parameter="",
            [Description("valueJson: the value to write, as JSON (number, string, boolean or object as the parameter expects).")] string valueJson="null",
            bool dryRun=true)
            => Portal.ManageTechnologyObject(softwarePath,objectPath,action,typeIdentifier,version,parameter,valueJson,dryRun);
        [McpServerTool(Name="ManagePlcSoftwareUnit"), Description("[L2][PLC-Software][WRITE] Native software units (PlcUnitProvider.UnitGroup): list (standard and safety units), read (typed PlcUnitBase row with contents), create, createFromMasterCopy (libraryName empty = project library, masterCopyPath, copyMode ThrowIfExists/Rename/Replace), delete, update (propertiesJson Author / NamespacePreset; commentsJson {culture: text} writes Comment per active project language; Name refused), createRelation (relatedUnit + relationType SoftwareUnit/NonUnitDB/TODB) and deleteRelation. unitKind=safety addresses the system-generated PlcSafetyUnit (read / update / relations only; it can neither be created nor deleted). Every write is verified by readback. dryRun=true default; real writes require an Offline PLC. Delete includes contained objects. No save/compile/download or implicit publication of objects (SetPlcUnitObjectAccess).")]
        public static ResponseMessage ManagePlcSoftwareUnit(
            string softwarePath,
            [Description("action: the operation to perform - list | read | create | createFromMasterCopy | delete | update | createRelation | deleteRelation.")] string action,
            string name="",
            [Description("relatedUnit: exact name of the related software unit.")] string relatedUnit="",
            [Description("relationType: SoftwareUnit | NonUnitDB | TODB.")] string relationType="",
            string propertiesJson="{}",
            bool dryRun=true,
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("commentsJson: JSON object language tag -> comment text.")] string commentsJson="{}",
            string libraryName="",
            string masterCopyPath="",
            [Description("copyMode: what to do when the name exists - ThrowIfExists | Rename | Replace.")] string copyMode="")
            => Portal.ManagePlcSoftwareUnit(softwarePath,action,name,relatedUnit,relationType,propertiesJson,dryRun,unitKind,commentsJson,libraryName,masterCopyPath,copyMode);
    }
}
