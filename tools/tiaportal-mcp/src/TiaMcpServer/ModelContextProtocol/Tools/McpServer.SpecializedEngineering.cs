using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ExchangePlcSupervisions"), Description("[L2][PLC-Software][WRITE] ProDiag XLSX export/import/importSettings, explicit native options and diagnostic state. Import requires offline PLC; output is new and hashed. Default preview; no save/compile/download.")]
        public static ResponseMessage ExchangePlcSupervisions(
            string softwarePath,
            [Description("action: the operation to perform - export | import | importSettings.")] string action,
            string filePath,
            string importOptions="None",
            bool dryRun=true)
            => Portal.ExchangePlcSupervisions(softwarePath,action,filePath,importOptions,dryRun);
        [McpServerTool(Name="ExchangeMotionCamData"), Description("[L2][PLC-TechnologyObjects][WRITE] Native cam text/binary/point-list export and text/binary import. Explicit native format/separator, new output file, default preview. Import requires Offline. No drive/motion command.")]
        public static ResponseMessage ExchangeMotionCamData(
            string softwarePath,
            string objectPath,
            string action,
            string filePath,
            [Description("format: file format name (see the tool description).")] string format="",
            [Description("separator: column separator character for CSV.")] string separator="",
            [Description("pointCount: number of points to export.")] int pointCount=0,
            bool dryRun=true)
            => Portal.ExchangeMotionCamData(softwarePath,objectPath,action,filePath,format,separator,pointCount,dryRun);
        [McpServerTool(Name="ConfigureMotionHardwareConnection"), Description("[L2][PLC-TechnologyObjects][WRITE] Offline axis actor/sensor/torque hardware mapping read/connect/disconnect. Addresses are BIT addresses. Explicit sensor index; default preview. Native readback, no live drive or motion command.")]
        public static ResponseMessage ConfigureMotionHardwareConnection(
            string softwarePath,
            string objectPath,
            [Description("interfaceKind: actor | sensor | torque.")] string interfaceKind,
            [Description("action: the operation to perform - read | connect | disconnect.")] string action,
            [Description("inputBitAddress: input bit address, e.g. '%I0.0'.")] int inputBitAddress=0,
            [Description("outputBitAddress: output bit address, e.g. '%Q0.0'.")] int outputBitAddress=0,
            [Description("connectOption: connect option name (Default or AllowAllModules).")] string connectOption="Default",
            int sensorIndex=0,
            bool dryRun=true)
            => Portal.ConfigureMotionHardwareConnection(softwarePath,objectPath,interfaceKind,action,inputBitAddress,outputBitAddress,connectOption,sensorIndex,dryRun);
        [McpServerTool(Name="ManageUnifiedEvent"), Description("[L2][HMI-Unified][WRITE] Exact screen/control event or property event read/create/update/delete. Object JSON path. Updates preserve omitted script fields. Mutations need preview token. No Script SyntaxCheck, script execution, save/compile/download.")]
        public static ResponseMessage ManageUnifiedEvent(
            string softwarePath,
            string objectPathJson,
            string eventType,
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            string propertyName="",
            [Description("scriptPropertiesJson: JSON object of script properties to set.")] string scriptPropertiesJson="{}",
            string expectedToken="",
            bool dryRun=true)
            => Portal.ManageUnifiedEvent(softwarePath,objectPathJson,eventType,action,propertyName,scriptPropertiesJson,expectedToken,dryRun);
        [McpServerTool(Name="ReadSiVArcRules"), Description("[L2][HMI][READ] Exact SiVArc rule category and property-only JSON path; live scalar pagination with schema, complex values excluded. No generation.")]
        public static ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
            => Portal.ReadSiVArcRules(category,objectPathJson,offset,limit);
        [McpServerTool(Name="ManageSiVArcRule"), Description("[L2][HMI][WRITE] Native SiVArc rule/folder/table composition create/update/delete with exact collection path and name. Nonempty container deletion refused. Default preview; no generation/save/compile/download.")]
        public static ResponseMessage ManageSiVArcRule(
            string category,
            [Description("collectionPathJson: JSON array path of the rule collection.")] string collectionPathJson,
            string name,
            string action,
            string propertiesJson="{}",
            bool dryRun=true)
            => Portal.ManageSiVArcRule(category,collectionPathJson,name,action,propertiesJson,dryRun);
        [McpServerTool(Name="GenerateSiVArc"), Description("[L2][HMI][WRITE] Native SiVArc generation (typed Sivarc.Generate) for one exact HMI device plus optional additionalHmiDeviceNamesJson (multi-device overload) and explicit PLC paths. generationOptions are official GenerationOptions flags joined with | (AllTags|FullGeneration; None = project settings). Default preview; can create/replace HMI objects according to rules. plcSoftwarePathsJson names the PLC software; the native call is tried with the software names and then with the owning device names (SiVArc only knows PLCs connected to the HMI device - the 2.7.39 real project without an HMI connection answered 'PLC device not found' for both). Returns the typed SivarcGenerationResult with recursive feedback messages. No automatic save/compile/download.")]
        public static ResponseMessage GenerateSiVArc(
            [Description("hmiDeviceName: exact HMI device name to generate for.")] string hmiDeviceName,
            [Description("plcSoftwarePathsJson: JSON array of PLC software paths included in the generation.")] string plcSoftwarePathsJson,
            [Description("generationOptions: SiVArc generation option name ('' = default).")] string generationOptions,
            bool dryRun=true,
            [Description("additionalHmiDeviceNamesJson: JSON array of further HMI device names.")] string additionalHmiDeviceNamesJson="[]")
            => Portal.GenerateSiVArc(hmiDeviceName,plcSoftwarePathsJson,generationOptions,dryRun,additionalHmiDeviceNamesJson);
        [McpServerTool(Name="ManageLibraryMasterCopy"), Description("[L2][Library][WRITE] Exact master-copy read/copy/compare/delete. copy destinationPath is a folder; compare uses exact master-copy path. No overwrites or automatic save. Default preview for mutations.")]
        public static ResponseMessage ManageLibraryMasterCopy(
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            [Description("action: the operation to perform - read | copy | compare | delete.")] string action,
            string libraryName="",
            [Description("destinationLibraryName: global library that receives the copy ('' = the project library).")] string destinationLibraryName="",
            [Description("destinationPath: 'Folder/Name' where the copy is created.")] string destinationPath="",
            bool dryRun=true)
            => Portal.ManageLibraryMasterCopy(sourcePath,action,libraryName,destinationLibraryName,destinationPath,dryRun);
        [McpServerTool(Name="ImportLibraryTypeDocuments"), Description("[L2][Library][WRITE] Native library document import (SimaticML / WinCC ML / S7DCL / SCL / STL / UDT / NVT): without typePath, LibraryTypeComposition.CreateFromDocuments creates a new type with an InWork default version in the exact library folder; with typePath, LibraryTypeVersionComposition.CreateFromDocuments adds a version to that type (createOptions None fails natively if an in-work version exists, Override replaces it). STEP 7 documents need targetSoftwarePath + targetGroupKind (blocks/types) + targetGroupPath as target environment. importOptions None/SkipInactiveCultures/ActivateInactiveCultures. Returns TransferResultState, messages and the created type/version. Project library only (global libraries throw natively); default preview; no automatic save.")]
        public static ResponseMessage ImportLibraryTypeDocuments(
            string filePath,
            string folderPath="",
            string libraryName="",
            [Description("importOptions: import option - None | SkipInactiveCultures | ActivateInactiveCultures.")] string importOptions="None",
            string typePath="",
            [Description("createOptions: None | Override.")] string createOptions="None",
            string targetSoftwarePath="",
            [Description("targetGroupKind: blocks or types - which group receives the imported documents.")] string targetGroupKind="",
            string targetGroupPath="",
            bool dryRun=true)
            => Portal.ImportLibraryTypeDocuments(filePath,folderPath,libraryName,importOptions,typePath,createOptions,targetSoftwarePath,targetGroupKind,targetGroupPath,dryRun);
    }
}
