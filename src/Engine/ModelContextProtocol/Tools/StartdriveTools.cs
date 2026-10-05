using System;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using ModelContextProtocol.Protocol;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    // Typed Startdrive option package. Drive objects are addressed by devicePathJson + itemPathJson (the drive
    // unit / control unit device item hosting DriveObjectContainer) plus driveObjectNumber (official DriveObjectNumber) or
    // driveObjectIndex (position in DriveObjects; the reliable selector on G120 drives, where DriveObjectNumber is not retrievable).
    [McpServerToolType]
    internal sealed class StartdriveTools
    {
        private readonly StartdriveService _startdrive;

        public StartdriveTools(StartdriveService startdrive) => _startdrive = startdrive;

        [McpServerTool(Name="ListDriveObjects"), Description("[L2][Hardware][READ] Typed Startdrive drive objects of one device item (DriveObjectContainer.DriveObjects): per object the index, DriveObjectNumber (null when TIA cannot retrieve it), telegrams (Telegram type / number / input-output words and bytes / addresses / hardware identifiers / PKW), the DriveFunctionInterface view (current and possible drive object types, activation state, availability of function-in-use / commissioning / safety commissioning / hardware projection), technology extensions and the DCC chart container summary; plus the device item's ModuleAccessPoint hardware identifiers, V21 DriveItemHardwareModule and online-container availability. Read-only, offline.")]
        public CallToolResult ReadDriveObjects(
            string[] devicePath,
            string[] itemPath,
            [Description("includeTelegrams: true also returns the telegrams.")] bool includeTelegrams=true,
            [Description("includeFunctions: true also returns the drive functions.")] bool includeFunctions=true,
            [Description("includeTechnologyExtensions: true also returns the technology extensions.")] bool includeTechnologyExtensions=true,
            [Description("includeDcc: true also returns the DCC charts.")] bool includeDcc=true)
            => DriveToolContract.Run("ListDriveObjects", false, false, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                return _startdrive.ReadDriveObjects(devicePathJson,itemPathJson,includeTelegrams,includeFunctions,includeTechnologyExtensions,includeDcc);
            });
        [McpServerTool(Name="GetDriveParameters"), Description("[L2][Hardware][READ] For one BICO connection prefer ManageStartdriveParameter action=read; a reported p2051[0] crash awaits native retest, so do not automatically replay it. Typed offline drive parameters of one drive object: source read (ReadDriveParameter view) or write (DriveParameter view); names [\"p1000[0]\",\"r47\"] uses Find(name), numbers [{number, arrayIndex}] uses Find(number, arrayIndex) (arrayIndex -1 for array-less parameters); without both the composition is enumerated in native order with offset / limit. Rows: name, number, array index / length, value (BICO sinks report {bicoSource, parameterText}), min / max, unit, parameter text, optional enum value list and bit parameters (Bits); bit names such as r722.0 resolve through the parent's Bits. includeValue=false reads the metadata without touching Value - do that first for BICO sinks and status words on a freshly added drive: on a G120C, TIA Portal V21 itself went down while reading the Value of r2139 and of the unwired p840[0] (2.7.39 real project). Read-only, offline; GetOnlineDriveParameters reads the connected drive.")]
        public CallToolResult ReadDriveParameters(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("source: read | write.")] string source="read",
            [Description("names: Array of exact parameter names.")] string[] names = null!,
            [Description("numbers: Array of {number, arrayIndex?}; omitted arrayIndex means -1.")] ParameterRef[] numbers = null!,
            [Description("includeBits: true also returns the bit definitions of bit-coded parameters.")] bool includeBits=false,
            [Description("includeEnumValues: true also returns the enumeration values of enum parameters.")] bool includeEnumValues=false,
            int offset=0,
            int limit=100,
            [Description("includeValue: true also reads the current value of every parameter.")] bool includeValue=true)
            => DriveToolContract.Run("GetDriveParameters", false, false, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string namesJson = V4Json.Serialize(names ?? Array.Empty<string>());
                string numbersJson = V4Json.Serialize(numbers ?? Array.Empty<ParameterRef>());
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateParametersRequest(source, namesJson, numbersJson, offset, limit);
                });
                return _startdrive.ReadDriveParameters(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,source,namesJson,numbersJson,includeBits,includeEnumValues,offset,limit,includeValue);
            }, offset, limit);
        [McpServerTool(Name="ManageStartdriveParameter"), Description("[L2][Hardware][WRITE] dryRun=true still performs native reads; never replay a known failing parameter after TIA/channel loss. Exact offline drive parameter read or write on one drive object (driveObjectNumber or driveObjectIndex): read follows Parameters.Find(name).Value for BICO, using ReadParameters only when absent in Parameters; returns name, parameterClass and value (including BICO source). Reads the selected Value once without expanding bits, limits or enum tables; use GetDriveParameters for an explicitly requested detailed view. A reported p2051[0] TIA crash remains pending native retest; write sets DriveParameter.Value from a JSON scalar (converted to the current value type) or wires a BICO sink with value {\"bicoSource\":\"r2050[1]\"} (Value = Parameters.Find(source); bit sources such as r722.0 resolve through the parent's Bits). Exact parameter names such as p1000[0], r47 or p2080[0].6; no fuzzy matching. Real project (G120C, 2.7.39): p1120[0] 10 -> 11 -> 10 and p1070[0] <- r2050[1] verified. Default preview; readback after the write. Never uses OnlineDriveObjectContainer or commands a drive. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageStartdriveParameter(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber,
            [Description("parameter: exact parameter name.")] string parameter,
            [Description("action: the operation to perform - read | write.")] string action="read",
            [Description("value: the value to write, as JSON (number, string, boolean or object as the parameter expects).")] NativeValue value = default,
            bool dryRun=true,
            int driveObjectIndex=-1)
            => DriveToolContract.Run("ManageStartdriveParameter", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string valueJson = DriveToolContract.ParameterValue(value, action);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateParameterWriteRequest(parameter, action);
                    if (action == "write") StartdriveLogic.ParseParameterValue(valueJson);
                });
                return _startdrive.ManageStartdriveParameter(devicePathJson,itemPathJson,driveObjectNumber,parameter,action,valueJson,dryRun,driveObjectIndex);
            });
        [McpServerTool(Name="ManageDriveTelegrams"), Description("[L2][Hardware][WRITE] Telegrams of one drive object (TelegramComposition): read; check (CanInsertTelegram / CanInsertMainTelegram / CanInsertSupplementaryTelegram / CanInsertSafetyTelegram / CanInsertTorqueTelegram / CanInsertAdditionalTelegram(inputSize, outputSize) and CanChangeTelegram); insert (typed Insert* per telegramType MainTelegram / SupplementaryTelegram / AdditionalTelegram / SafetyTelegram / TorqueTelegram / EdgeTelegram, telegramNumber 999 = free telegram); erase (EraseTelegram, main telegrams only on G220); while a MainTelegram exists, check / insert for MainTelegram skip the native Can* / Insert* (TIA Portal V21 crashed on a G120C there) - use changeNumber / changeSize; a drive object without any telegram (not networked) refuses every check / insert / erase (same crash on a fresh G120C); changeNumber (Telegram.TelegramNumber after CanChangeTelegram); changeSize (Telegram.ChangeSize(direction Input|Output, size words, keepOriginalAddress) after CanChangeSize); connectTechnologyObject (V21 AxisHardwareConnectionSDRProvider / EncoderHardwareConnectionSDRProvider SDR interfaces, V20 base interfaces: interfaceKind actor / sensor (sensorIndex) / torque / encoder of the technology object at softwarePath + objectPath, optional connectOption Default | AllowAllModules). Default preview; telegrams are read back on a fresh navigation because TIA invalidates Telegram objects after size changes. No download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDriveTelegrams(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("action: the operation to perform - read | check | insert | erase | changeNumber | changeSize | connectTechnologyObject.")] string action="read",
            [Description("telegramType: MainTelegram | SupplementaryTelegram | AdditionalTelegram | SafetyTelegram | TorqueTelegram | EdgeTelegram.")] string telegramType="",
            [Description("telegramNumber: telegram number, e.g. 1, 3, 105.")] int telegramNumber=-1,
            [Description("inputSize: input size in words.")] int inputSize=-1,
            [Description("outputSize: output size in words.")] int outputSize=-1,
            [Description("direction: Input | Output.")] string direction="",
            [Description("size: size in words.")] int size=-1,
            [Description("keepOriginalAddress: true keeps the telegram's address after the change.")] bool keepOriginalAddress=true,
            string softwarePath="",
            string objectPath="",
            [Description("interfaceKind: actor | sensor | torque | encoder.")] string interfaceKind="",
            int sensorIndex=0,
            [Description("connectOption: Default | AllowAllModules.")] string connectOption="",
            bool dryRun=true)
            => DriveToolContract.Run("ManageDriveTelegrams", !dryRun && action != "read" && action != "check", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateTelegramRequest(action, telegramType, telegramNumber, inputSize, outputSize, direction, size, keepOriginalAddress, softwarePath, objectPath, interfaceKind, sensorIndex, connectOption, dryRun);
                });
                return _startdrive.ManageDriveTelegrams(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,action,telegramType,telegramNumber,inputSize,outputSize,direction,size,keepOriginalAddress,softwarePath,objectPath,interfaceKind,sensorIndex,connectOption,dryRun);
            });
        [McpServerTool(Name="ManageDriveFunctions"), Description("[L2][Hardware][WRITE] Offline DriveFunctionInterface of one drive object. read: drive object type handler (current / possible types), activation (state, isActive), availability of FunctionInUse / Commissioning / SafetyCommissioning / HardwareProjection. Actions with value: changeDriveObjectType {driveObjectType} (DriveObjectTypeHandler.ChangeDriveObjectType from PossibleDriveObjectTypes), changeActivationState {activationState Deactivate|Activate|DeactivateAndNotPresent}, activateFunction / deactivateFunction {functionKey BasicPositioner|TechnologyController} and setSIAxisType {rotaryLinear} (FunctionInUse, SINAMICS FW V6.3+), setMotorCode {motorCode, motorDataSet} and setSimoGearMlfb {mlfb} (Commissioning), updateCheckSums (SafetyCommissioning, FW V6.1+), setMotorType {motorType, dataSet} (G120), readMotorConfiguration / projectMotorConfiguration {dataSet, entries {p305: 20}} and setEquivalentCircuitDiagramData {dataSet, equivalentCircuitDiagram}, setEncoder {encoderInterface, encoderType, absoluteIncremental, rotaryLinear, encoderNumber} (G120), readEncoderConfiguration / setEncoderType {encoderNumber, rotaryLinear[, absoluteIncremental]} / projectEncoderConfiguration {encoderNumber, entries} (HardwareProjection; configuration entries by name or number). Native bool results are reported; false sets operationSuccess=false. Default preview; offline only, no download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDriveFunctions(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("action: the operation to perform - read | changeDriveObjectType | changeActivationState | activateFunction | deactivateFunction | setSIAxisType | setMotorCode | setSimoGearMlfb | updateCheckSums | setMotorType | readMotorConfiguration | projectMotorConfiguration | setEquivalentCircuitDiagramData | setEncoder | readEncoderConfiguration | setEncoderType | projectEncoderConfiguration.")] string action="read",
            [Description("value: the value to write, as JSON (number, string, boolean or object as the parameter expects).")] NativeValue value = default,
            bool dryRun=true)
            => DriveToolContract.Run("ManageDriveFunctions", !dryRun && action != "read" && action != "readMotorConfiguration" && action != "readEncoderConfiguration", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string valueJson = DriveToolContract.FunctionValue(value, action);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateFunctionRequest(action, valueJson, dryRun);
                });
                return _startdrive.ManageDriveFunctions(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,action,valueJson,dryRun);
            });
        [McpServerTool(Name="ManageDriveSecurity"), Description("[L2][Hardware][WRITE] Drive security of one drive object (DriveObject.Security, SINAMICS FW V6.1+): read reports UmacConfiguration / DriveDataEncryption availability (the API exposes no state); activateUmac / deactivateUmac call UmacConfiguration.Activate / Deactivate (offline only); activateEncryption / deactivateEncryption call DriveDataEncryption.Activate / Deactivate with password (SecureString, never logged). Default preview; native bool result reported; no download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDriveSecurity(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("action: the operation to perform - read | activateUmac | deactivateUmac | activateEncryption | deactivateEncryption.")] string action="read",
            string password="",
            bool dryRun=true)
            => DriveToolContract.Run("ManageDriveSecurity", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateSecurityRequest(action, password, dryRun);
                });
                return _startdrive.ManageDriveSecurity(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,action,password,dryRun);
            });
        [McpServerTool(Name="ManageTechnologyExtensions"), Description("[L2][Hardware][WRITE] Startdrive technology extensions. Drive scope (devicePath + itemPath + drive object): read lists TechnologyExtensionContainer.TechnologyExtensions (identifier / name / display name / isActivated, parameters with includeParameters), activate / deactivate by identifier or name (TechnologyExtension.Activate / Deactivate, readback verifies IsActivated). Portal scope (TiaPortal.GetService<TechnologyExtensionInstallationProvider>): readPackages, install / installAndGetIdentifier (filePath .tec), uninstall (identifier, confirmUninstallInUse) - these change the TIA Portal installation, not the project. Default preview. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageTechnologyExtensions(
            [Description("action: the operation to perform - read | activate | deactivate | readPackages | install | installAndGetIdentifier | uninstall.")] string action="read",
            string[] devicePath = null!,
            string[] itemPath = null!,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="",
            string filePath="",
            [Description("confirmUninstallInUse: must be true to uninstall a technology extension that is still in use.")] bool confirmUninstallInUse=false,
            [Description("includeParameters: true also returns parameters.")] bool includeParameters=false,
            bool dryRun=true)
            => DriveToolContract.Run("ManageTechnologyExtensions", !dryRun && action != "read" && action != "readPackages", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: true);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: true);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateTechnologyExtensionRequest(action, identifier, filePath, confirmUninstallInUse, dryRun);
                });
                return _startdrive.ManageTechnologyExtensions(action,devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,identifier,filePath,confirmUninstallInUse,includeParameters,dryRun);
            });
        [McpServerTool(Name="ManageDriveHardwareModule"), Description("[L2][Hardware][WRITE] Drive component hardware module of one device item: read returns the ModuleAccessPoint hardware identifiers and the V21 DriveItemHardwareModule (component name, position number, type name, type identifier); changeType calls DriveItemHardwareModule.ChangeType(typeIdentifier such as OrderNumber:6SL3120-2TE21-8Axx//10014), setPositionNumber writes PositionNumber (V21; NotSupported on V20). Default preview; readback verifies the type identifier; no download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDriveHardwareModule(
            string[] devicePath,
            string[] itemPath,
            [Description("action: the operation to perform - read | changeType | setPositionNumber.")] string action="read",
            [Description("typeIdentifier: catalog type identifier of the form 'OrderNumber:6ES7 ...' or 'OrderNumber:.../V2.9' (SearchHardwareCatalog / ManageHardwareUtilities normalizeTypeIdentifier).")] string typeIdentifier="",
            [Description("positionNumber: slot / position number of the module or item (-1 = not given).")] int positionNumber=-1,
            bool dryRun=true)
            => DriveToolContract.Run("ManageDriveHardwareModule", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ValidateHardwareModuleRequest(action, typeIdentifier, positionNumber, dryRun);
                });
                return _startdrive.ManageDriveHardwareModule(devicePathJson,itemPathJson,action,typeIdentifier,positionNumber,dryRun);
            });
        [McpServerTool(Name="ManageDriveSafetyAcceptanceTest"), Description("[L2][Hardware][WRITE] V21 Safety Integrated acceptance test of a drive device item: read lists SafetyAcceptanceTestProvider.TestFunctions (identifier, active) and whether the owning device provides SafetyAcceptanceTestReport; setActive writes TestFunction.Active for one identifier; resetTestFunctions calls ResetTestFunctions(); createProtocol writes the report file through SafetyAcceptanceTestReport.CreateProtocol(filePath, fileOperation None|Overwrite) and verifies it by size and SHA-256. NotSupported on V20. Default preview; no download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDriveSafetyAcceptanceTest(
            string[] devicePath,
            string[] itemPath,
            [Description("action: the operation to perform - read | setActive | resetTestFunctions | createProtocol.")] string action="read",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="",
            [Description("active: true activates, false deactivates.")] bool active=false,
            string filePath="",
            [Description("fileOperation: None | Overwrite.")] string fileOperation="",
            bool dryRun=true)
            => DriveToolContract.Run("ManageDriveSafetyAcceptanceTest", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ValidateSafetyTestRequest(action, identifier, filePath, fileOperation, dryRun);
                });
                return _startdrive.ManageDriveSafetyAcceptanceTest(devicePathJson,itemPathJson,action,identifier,active,filePath,fileOperation,dryRun);
            });
        [McpServerTool(Name="GetOnlineDriveParameters"), Description("[L2][Hardware][ONLINE] Offline reads and online reads are distinct, and native read calls can still fail or terminate TIA. Parameters of the connected drive (OnlineDriveObjectContainer.OnlineDriveObjects[n].ReadParameters): names / numbers selectors or paged enumeration, same typed rows as GetDriveParameters (BICO sources, bits, enum values, includeValue). Requires the drive to be online in TIA Portal; nothing is written.")]
        public CallToolResult ReadOnlineDriveParameters(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("names: Array of exact parameter names.")] string[] names = null!,
            [Description("numbers: Array of {number, arrayIndex?}; omitted arrayIndex means -1.")] ParameterRef[] numbers = null!,
            [Description("includeBits: true also returns the bit definitions of bit-coded parameters.")] bool includeBits=false,
            [Description("includeEnumValues: true also returns the enumeration values of enum parameters.")] bool includeEnumValues=false,
            int offset=0,
            int limit=100,
            [Description("includeValue: true also reads the current value of every parameter.")] bool includeValue=true)
            => DriveToolContract.Run("GetOnlineDriveParameters", false, false, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string namesJson = V4Json.Serialize(names ?? Array.Empty<string>());
                string numbersJson = V4Json.Serialize(numbers ?? Array.Empty<ParameterRef>());
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateParametersRequest("read", namesJson, numbersJson, offset, limit);
                });
                return _startdrive.ReadOnlineDriveParameters(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,namesJson,numbersJson,includeBits,includeEnumValues,offset,limit,includeValue);
            }, offset, limit);
        [McpServerTool(Name="ManageOnlineDriveFunctions"), Description("[L2][Hardware][ONLINE-WRITE] OnlineDriveFunctionInterface of the connected drive: read reports DriveDomainFunctions / HardwareProjection / FunctionInUse availability and the activation state; performFactoryReset {resetMode ParameterReset|SafetyParameterReset} and performRamToRomCopy (DriveDomainFunctions.PerformFactoryReset / PerformRAMtoROMCopyAllDriveObject) and changeActivationState {activationState} act on the real drive and need confirmOnline=true. No dryRun: the read action is the preview. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageOnlineDriveFunctions(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            [Description("action: the operation to perform - read | performFactoryReset | performRamToRomCopy | changeActivationState.")] string action="read",
            [Description("resetMode: ParameterReset | SafetyParameterReset.")] string resetMode="",
            [Description("activationState: Deactivate | Activate | DeactivateAndNotPresent.")] string activationState="",
            [Description("confirmOnline: must be true together with dryRun=false to execute the online drive function.")] bool confirmOnline=false)
            => DriveToolContract.Run("ManageOnlineDriveFunctions", action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    StartdriveLogic.ValidateOnlineFunctionRequest(action, resetMode, activationState, confirmOnline);
                });
                return _startdrive.ManageOnlineDriveFunctions(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,action,resetMode,activationState,confirmOnline);
            });
    }
}
