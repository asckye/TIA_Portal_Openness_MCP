#if OPTIONAL_V20_PLUS
using static TiaMcp.Adapters.Native.Optional.OptionalEvidence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Optional.OptionalScalars;
using EngineeringObjectAddress = TiaMcp.Adapters.Native.Optional.OptionalObjectAddress;
using NativeFileOutput = TiaMcp.Adapters.Native.Safety.SafetyFileOutput;
using PlcBlockServicesLogic = TiaMcp.Adapters.Native.Safety.SafetyPrimitives;
using HardwareServicesLogic = TiaMcp.Adapters.Native.Optional.OptionalRules;
using ArgumentRules = TiaMcp.Adapters.Native.Optional.OptionalRules;
using EngineeringPath = TiaMcp.Adapters.Hardware.HardwarePath;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;
using Siemens.Engineering.MC.Drives.Enums;
using Siemens.Engineering.MC.Drives.SecurityObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcp.Adapters.Native.Optional.StartdrivePolicy;
using MotionConnectOption = Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption;
using DriveEncoderType = Siemens.Engineering.MC.Drives.Enums.EncoderType;
#if !OPTIONAL_V21
using DriveConnectOption = Siemens.Engineering.MC.Drives.Enums.ConnectOption;
#endif

namespace TiaMcp.Adapters.Native.Optional
{
    // Typed Startdrive option package (V21 Siemens.Engineering.Startdrive.dll, V20 inside Siemens.Engineering.dll).
    // DriveObjectContainer is a service of the drive unit / control unit DeviceItem; DriveObject carries Parameters / ReadParameters /
    // Telegrams / Security and the DriveFunctionInterface, TechnologyExtensionContainer and (DCC) DriveControlChartContainer services;
    // OnlineDriveObjectContainer / OnlineDriveFunctionInterface mirror the offline shape for a connected drive. TIA V21, 2026-09-19 (docs/reference/real-machine-ledger.md), real project
    // (G120C): DriveObject.DriveObjectNumber throws "Drive object number could not be retrieved", so every selector accepts the
    // composition index and DriveObjectNumber is read defensively.
    internal sealed partial class StartdriveAdapter
    {
        public HardwareAddressingReply ManageDriveFunctions(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1,
                string action = "read", string valueJson = "{}", bool dryRun = true)
            => _session.RunHmiStepTool("ManageDriveFunctions", meta =>
            {
                var r = Logic.ValidateFunctionRequest(action, valueJson, dryRun);
                using var access = r.Writes ? _session.AcquireHmiEditAccess() : null;
                var drive = _session.ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var dfi = drive.GetService<DriveFunctionInterface>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveFunctionInterface not provided by this drive object.");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun;
                meta["mayHaveChanged"] = false;
                meta["before"] = FunctionInterfaceRow(dfi);
                if (action == "read")
                    return "Drive function interface read (drive object type handler, activation, function-in-use / commissioning / safety / hardware " +
                    "projection availability); no modification.";
                T Need<T>(T? value, string what) where T : class => value
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", what + " is not provided by this drive object.");
                HardwareProjection Projection() => Need(dfi.HardwareProjection, "HardwareProjection (G120: needs the Power Module; S120: offline only)");
                if (action == "readMotorConfiguration") {
                    meta["dataSet"] = r.DataSet; meta["motorConfiguration"] =
                        MotorConfigurationRow(Projection().GetCurrentMotorConfiguration((ushort)r.DataSet));
                    return "Current motor configuration read (set the motor type first on G120 drives); no modification.";
                }
                if (action == "readEncoderConfiguration") {
                    meta["encoderNumber"] = r.EncoderNumber; meta["encoderConfiguration"] =
                        EncoderConfigurationRow(Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber));
                    return "Current encoder configuration read; no modification.";
                }
                if (!r.Writes) return "Drive function " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                bool result = true;
                switch (action)
                {
                    case "changeDriveObjectType":
                        {
                            var handler = Need(dfi.DriveObjectFunctions?.DriveObjectTypeHandler, "DriveObjectTypeHandler");
                            var target = handler.PossibleDriveObjectTypes.Find(r.DriveObjectType)
                                ?? throw new PlcSoftwareException("NotFound", "Drive object type not among PossibleDriveObjectTypes: " + r.DriveObjectType);
                            meta["nativeSignature"] = "DriveObjectTypeHandler.ChangeDriveObjectType(DriveObjectType)"; result = handler
                                .ChangeDriveObjectType(target); break;
                        }
                    case "changeActivationState": meta["nativeSignature"] = "DriveObjectActivation.ChangeActivationState(DriveObjectActivationState)"; result =
                        Need(dfi.DriveObjectFunctions?.DriveObjectActivation, "DriveObjectActivation")
                        .ChangeActivationState((DriveObjectActivationState)Enum.Parse(typeof(DriveObjectActivationState), r.ActivationState)); break;
                    case "activateFunction": meta["nativeSignature"] = "FunctionInUse.Activate(FunctionKeys)"; result =
                        Need(dfi.FunctionInUse, "FunctionInUse (SINAMICS FW V6.3+)").Activate((FunctionKeys)Enum.Parse(typeof(FunctionKeys), r.FunctionKey));
                    break;
                    case "deactivateFunction": meta["nativeSignature"] = "FunctionInUse.Deactivate(FunctionKeys)"; result =
                        Need(dfi.FunctionInUse, "FunctionInUse (SINAMICS FW V6.3+)").Deactivate((FunctionKeys)Enum.Parse(typeof(FunctionKeys), r.FunctionKey));
                    break;
                    case "setSIAxisType": meta["nativeSignature"] = "FunctionInUse.SetSIAxisType(RotaryLinearFlag)"; result =
                        Need(dfi.FunctionInUse, "FunctionInUse (SINAMICS FW V6.3+)")
                        .SetSIAxisType((RotaryLinearFlag)Enum.Parse(typeof(RotaryLinearFlag), r.RotaryLinear)); break;
                    case "setMotorCode": meta["nativeSignature"] = "Commissioning.SetMotorCode(int, int)"; result = Need(dfi.Commissioning, "Commissioning")
                        .SetMotorCode(r.MotorCode, r.MotorDataSet); break;
                    case "setSimoGearMlfb": meta["nativeSignature"] = "Commissioning.SetSimoGearMlfb(string)"; Need(dfi.Commissioning, "Commissioning")
                        .SetSimoGearMlfb(r.Mlfb); break;
                    case "updateCheckSums": meta["nativeSignature"] = "SafetyCommissioning.UpdateCheckSums()"; result =
                        Need(dfi.SafetyCommissioning, "SafetyCommissioning (SINAMICS FW V6.1+)").UpdateCheckSums(); break;
                    case "setMotorType": meta["nativeSignature"] = "HardwareProjection.SetMotorType(MotorType, ushort)"; result = Projection()
                        .SetMotorType((MotorType)Enum.Parse(typeof(MotorType), r.MotorType), (ushort)r.DataSet); break;
                    case "setEquivalentCircuitDiagramData": {
                        var config = Projection().GetCurrentMotorConfiguration((ushort)r.DataSet); meta["nativeSignature"] =
                            "MotorConfiguration.SetEquivalentCircuitDiagramData(bool)"; result = config
                            .SetEquivalentCircuitDiagramData(r.EquivalentCircuitDiagram); meta["after"] = MotorConfigurationRow(config); break;
                    }
                    case "projectMotorConfiguration":
                        {
                            var config = Projection().GetCurrentMotorConfiguration((ushort)r.DataSet);
                            meta["appliedEntries"] = ApplyEntries(r.Entries, config.RequiredConfigurationEntries, config.OptionalConfigurationEntries);
                            meta["nativeSignature"] = "HardwareProjection.ProjectMotorConfiguration(MotorConfiguration, ushort)"; result = Projection()
                                .ProjectMotorConfiguration(config, (ushort)r.DataSet);
                            meta["after"] = MotorConfigurationRow(Projection().GetCurrentMotorConfiguration((ushort)r.DataSet)); break;
                        }
                    case "setEncoder": meta["nativeSignature"] =
                        "HardwareProjection.SetEncoder(EncoderInterface, EncoderType, AbsoluteIncrementalFlag, RotaryLinearFlag, ushort)"; result = Projection()
                        .SetEncoder((EncoderInterface)Enum.Parse(typeof(EncoderInterface), r.EncoderInterface), (DriveEncoderType)Enum
                            .Parse(typeof(DriveEncoderType), r.EncoderType), (AbsoluteIncrementalFlag)Enum
                            .Parse(typeof(AbsoluteIncrementalFlag), r.AbsoluteIncremental), (RotaryLinearFlag)Enum
                            .Parse(typeof(RotaryLinearFlag), r.RotaryLinear), (ushort)r.EncoderNumber);
                    break;
                    case "setEncoderType":
                        {
                            var config = Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber); var rotary = (RotaryLinearFlag)Enum
                                .Parse(typeof(RotaryLinearFlag), r.RotaryLinear);
                            meta["nativeSignature"] = r.HasAbsoluteIncremental
                                ? "EncoderConfiguration.SetEncoderType(RotaryLinearFlag, AbsoluteIncrementalFlag)"
                                : "EncoderConfiguration.SetEncoderType(RotaryLinearFlag)";
                            result = r.HasAbsoluteIncremental ? config
                                .SetEncoderType(rotary, (AbsoluteIncrementalFlag)Enum.Parse(typeof(AbsoluteIncrementalFlag), r.AbsoluteIncremental)) : config
                                .SetEncoderType(rotary);
                            meta["after"] = EncoderConfigurationRow(config); break;
                        }
                    case "projectEncoderConfiguration":
                        {
                            var config = Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber);
                            meta["appliedEntries"] = ApplyEntries(r.Entries, config.RequiredConfigurationEntries, null);
                            meta["nativeSignature"] = "HardwareProjection.ProjectEncoderConfiguration(EncoderConfiguration, ushort)"; result = Projection()
                                .ProjectEncoderConfiguration(config, (ushort)r.EncoderNumber);
                            meta["after"] = EncoderConfigurationRow(Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber)); break;
                        }
                }
                meta["result"] = result; if (!result) meta["operationSuccess"] = false;
                if (!meta.ContainsKey("after")) meta["after"] = FunctionInterfaceRow(dfi);
                return result ? "Drive function " + action
                    + " executed (native bool result true) and the function interface read back; no download or drive command." : "Drive function " + action
                    + " returned false (TIA refused it; see before / after); no download or drive command.";
            });
        // entries {"p305": 20, "307": 30} -> ConfigurationEntry.Value by Name or Number in the required (then optional) composition.
        private static HmiArray ApplyEntries(Dictionary<string, object?> entries, ConfigurationEntryComposition required, ConfigurationEntryComposition
                ? optional)
        {
            var applied = new HmiArray();
            foreach (var pair in entries)
            {
                ConfigurationEntry? entry = uint.TryParse(pair.Key, out var number) ? required.Find(number) ?? optional?.Find(number) : required.Find(pair.Key)
                    ?? optional?.Find(pair.Key);
                if (entry == null) throw new PlcSoftwareException("NotFound", "Configuration entry not found in the current configuration: " + pair.Key);
                var current = entry.Value;
                var converted = current == null ? EngineeringScalarProperties.ConvertValue(pair.Value, typeof(object)) : EngineeringScalarProperties
                    .ConvertValue(pair.Value, current.GetType());
                entry.Value = converted;
                applied.Add(new HmiObject { ["name"] = entry.Name, ["number"] = entry.Number, ["value"] = EngineeringScalarProperties.Json(entry.Value) });
            }
            return applied;
        }

        public HardwareAddressingReply ManageDriveSecurity(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1,
                string action = "read", string password = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageDriveSecurity", meta =>
            {
                bool write = Logic.ValidateSecurityRequest(action, password, dryRun);
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var drive = _session.ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                global::Siemens.Engineering.MC.Drives.Security security = drive.Security
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "Security is not provided by this drive object (SINAMICS FW V6.1+).");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun;
                meta["mayHaveChanged"] = false;
                Safe(meta, "umacConfigurationAvailable", () => security.UmacConfiguration != null);
                Safe(meta, "driveDataEncryptionAvailable", () => security.DriveDataEncryption != null);
                if (action == "read")
                    return "Drive security services read (UmacConfiguration / DriveDataEncryption availability; the API exposes no state); no modification.";
                if (!write) return "Drive security " + action + " preview; nothing changed (offline only; the password is never logged).";
                meta["mayHaveChanged"] = true; bool result;
                if (action == "activateUmac" || action == "deactivateUmac")
                {
                    UmacConfiguration umac = security.UmacConfiguration
                        ?? throw new PlcSoftwareException("NotSupportedOnVersion", "UmacConfiguration is not provided by this drive object.");
                    meta["nativeSignature"] = "UmacConfiguration." + (action == "activateUmac" ? "Activate()" : "Deactivate()"); result =
                        action == "activateUmac" ? umac.Activate() : umac.Deactivate();
                }
                else
                {
                    DriveDataEncryption dde = security.DriveDataEncryption
                        ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveDataEncryption is not provided by this drive object.");
                    using var secure = EngineeringCredentialRules.ToSecureString(password);
                    meta["nativeSignature"] = "DriveDataEncryption." + (action == "activateEncryption" ? "Activate(SecureString)" : "Deactivate(SecureString)");
                    result = action == "activateEncryption" ? dde.Activate(secure) : dde.Deactivate(secure);
                }
                meta["result"] = result; if (!result) meta["operationSuccess"] = false;
                return result ? "Drive security " + action + " executed (native result true); no download." : "Drive security " + action
                    + " returned false (TIA refused it); no download.";
            });

        public HardwareAddressingReply ManageTechnologyExtensions(string action = "read", string devicePathJson = "[]", string itemPathJson = "[]",
                ushort driveObjectNumber = 0, int driveObjectIndex = -1, string identifier = "", string filePath = "", bool confirmUninstallInUse = false,
                bool includeParameters = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageTechnologyExtensions", meta =>
            {
                bool write = Logic.ValidateTechnologyExtensionRequest(action, identifier, filePath, confirmUninstallInUse, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (Logic.IsPortalScopedExtensionAction(action))
                {
                    var provider = (_session.CurrentPortal as IEngineeringServiceProvider)?.GetService<TechnologyExtensionInstallationProvider>()
                        ?? throw new PlcSoftwareException("NotSupportedOnVersion",
                            "TechnologyExtensionInstallationProvider unavailable on this TIA Portal (Startdrive not installed).");
                    HmiArray Packages() =>
                        new HmiArray(EngineeringGroupOperations.Items(provider.TechnologyExtensionPackages).Cast<TechnologyExtensionPackage>()
                            .Select(p => (object)TechnologyExtensionPackageRow(p)).ToArray());
                    meta["before"] = Packages();
                    if (action == "readPackages")
                        return "Installed technology extension packages read (TechnologyExtensionInstallationProvider.TechnologyExtensionPackages); no " +
                        "modification.";
                    if (action == "uninstall") {
                        var package = provider.TechnologyExtensionPackages.Find(identifier)
                            ?? throw new PlcSoftwareException("NotFound", "Technology extension package not installed: " + identifier); meta["package"] =
                            TechnologyExtensionPackageRow(package);
                    }
                    else { var file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["file"] = file.FullName; }
                    if (!write) return "Technology extension package " + action
                        + " preview; nothing installed or removed (this changes the TIA Portal installation, not the project).";
                    meta["mayHaveChanged"] = true;
                    switch (action)
                    {
                        case "install": meta["nativeSignature"] = "TechnologyExtensionInstallationProvider.Install(FileInfo)"; meta["result"] = provider
                            .Install(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath))); break;
                        case "installAndGetIdentifier": meta["nativeSignature"] = "TechnologyExtensionInstallationProvider.InstallAndGetIdentifier(FileInfo)";
                        var id = provider.InstallAndGetIdentifier(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath))); meta["identifier"] =
                            id;
                        Safe(meta, "package", () => {
                                var p = provider.TechnologyExtensionPackages.Find(id); return p == null ? null : TechnologyExtensionPackageRow(p); });
                        break;
                        case "uninstall": meta["nativeSignature"] = "TechnologyExtensionInstallationProvider.Uninstall(string, bool)"; meta["result"] = provider
                            .Uninstall(identifier, confirmUninstallInUse); meta["verifiedAbsent"] = provider.TechnologyExtensionPackages
                            .Find(identifier) == null; break;
                    }
                    if (meta["result"] is object v && v.TryOptionalValue<bool>(out var ok) && !ok) meta["operationSuccess"] = false;
                    meta["after"] = Packages();
                    return "Technology extension package " + action + " executed on the TIA Portal installation; packages read back.";
                }
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var drive = _session.ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var container = drive.GetService<TechnologyExtensionContainer>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "TechnologyExtensionContainer not provided by this drive object.");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label;
                HmiArray Rows() =>
                    new HmiArray(EngineeringGroupOperations.Items(container.TechnologyExtensions).Cast<TechnologyExtension>()
                        .Select(e => (object)TechnologyExtensionRow(e, includeParameters)).ToArray());
                meta["before"] = Rows();
                if (action == "read") return "Technology extensions of the drive object read (identifier / name / display name / activation"
                    + (includeParameters ? " / parameters" : "") + "); no modification.";
                TechnologyExtension extension = container.TechnologyExtensions.Find(identifier) ?? EngineeringGroupOperations
                    .Items(container.TechnologyExtensions).Cast<TechnologyExtension>().FirstOrDefault(e => e.Name == identifier)
                    ?? throw new PlcSoftwareException("NotFound", "Technology extension not found by identifier or name: " + identifier);
                meta["extension"] = TechnologyExtensionRow(extension, false);
                if (!write) return "Technology extension " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true; meta["nativeSignature"] = "TechnologyExtension." + (action == "activate" ? "Activate()" : "Deactivate()");
                bool result = action == "activate" ? extension.Activate() : extension.Deactivate();
                meta["result"] = result; if (!result) meta["operationSuccess"] = false;
                var fresh = container.TechnologyExtensions.Find(extension.Identifier);
                meta["after"] = fresh == null ? null : TechnologyExtensionRow(fresh, false); meta["activationVerified"] = fresh != null && fresh
                    .IsActivated == (action == "activate");
                return "Technology extension " + action + " executed and read back; no download.";
            });

        public HardwareAddressingReply ManageDriveHardwareModule(string devicePathJson, string itemPathJson, string action = "read", string typeIdentifier = "",
                int positionNumber = -1, bool dryRun = true)
            => _session.RunHmiStepTool("ManageDriveHardwareModule", meta =>
            {
                bool write = Logic.ValidateHardwareModuleRequest(action, typeIdentifier, positionNumber, dryRun);
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var item = _session.ExactDriveItem(devicePathJson, itemPathJson);
                meta["deviceItem"] = item.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                Safe(meta, "moduleAccessPointHwIdentifiers", () => {
                        var ap = item.GetService<ModuleAccessPoint>(); return ap == null ? null
                            : new HmiArray(EngineeringGroupOperations.Items(ap.HwIdentifiers).Cast<HwIdentifier>().Select(h => (object)h.Identifier).ToArray());
                    });
#if !OPTIONAL_V21
                meta["hardwareModule"] = null; meta["hardwareModuleNote"] =
                    "DriveItemHardwareModule (component name / position / type identifier / ChangeType) is a V21 addition; absent on V20.";
                if (action == "read") return "Module access point read; DriveItemHardwareModule is not available on V20.";
                throw new PlcSoftwareException("NotSupportedOnVersion", "DriveItemHardwareModule." + (action == "changeType" ? "ChangeType" : "PositionNumber")
                        + " is a V21 addition; absent on V20.");
#else
                var module = item.GetService<DriveItemHardwareModule>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveItemHardwareModule not provided by device item '" + item.Name
                        + "' (drive components only).");
                meta["before"] = HardwareModuleRow(module);
                if (action == "read")
                    return "Drive hardware module read (DriveItemHardwareModule component name / position / type name / type identifier, module access point " +
                    "hardware identifiers); no modification.";
                if (!write) return "Drive hardware module " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (action == "changeType") { meta["nativeSignature"] = "DriveItemHardwareModule.ChangeType(string)"; module.ChangeType(typeIdentifier); }
                else { meta["nativeSignature"] = "DriveItemHardwareModule.PositionNumber = n"; module.PositionNumber = positionNumber; }
                var fresh = item.GetService<DriveItemHardwareModule>() ?? module;
                meta["after"] = HardwareModuleRow(fresh);
                if (action == "changeType") meta["typeIdentifierVerified"] = string.Equals(fresh.TypeIdentifier, typeIdentifier, StringComparison.Ordinal);
                return "Drive hardware module " + action + " executed and read back; no download.";
#endif
            });

        public HardwareAddressingReply ManageDriveSafetyAcceptanceTest(string devicePathJson, string itemPathJson, string action = "read", string identifier =
                "", bool active = false, string filePath = "", string fileOperation = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageDriveSafetyAcceptanceTest", meta =>
            {
                bool write = Logic.ValidateSafetyTestRequest(action, identifier, filePath, fileOperation, dryRun);
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var item = _session.ExactDriveItem(devicePathJson, itemPathJson);
                meta["deviceItem"] = item.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] =
                    false;
#if !OPTIONAL_V21
                throw new PlcSoftwareException("NotSupportedOnVersion",
                        "SafetyAcceptanceTestProvider / SafetyAcceptanceTestReport are V21 additions; absent on V20.");
#else
                var provider = item.GetService<SafetyAcceptanceTestProvider>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "SafetyAcceptanceTestProvider not provided by device item '" + item.Name + "'.");
                HmiArray Rows() =>
                    new HmiArray(EngineeringGroupOperations.Items(provider.TestFunctions).Cast<TestFunction>()
                        .Select(t => (object)new HmiObject { ["identifier"] = t.Identifier, ["active"] = t.Active }).ToArray());
                meta["before"] = Rows();
                IEngineeringObject? owner = item.Parent; while (owner is DeviceItem parentItem) owner = parentItem.Parent; var device = owner as Device;
                if (action == "read") {
                    Safe(meta, "reportAvailable", () => device?.GetService<SafetyAcceptanceTestReport>() != null);
                    return "Safety acceptance test functions read (identifier / active); no modification.";
                }
                if (action == "createProtocol")
                {
                    var report = device?.GetService<SafetyAcceptanceTestReport>()
                        ?? throw new PlcSoftwareException("NotSupportedOnVersion", "SafetyAcceptanceTestReport not provided by the owning device.");
                    var operation = (FileOperations)Enum.Parse(typeof(FileOperations), fileOperation);
                    var file = operation == FileOperations.Overwrite ? new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)) : NativeFileOutput
                        .Plan(filePath);
                    meta["file"] = file.FullName; meta["fileOperation"] = fileOperation;
                    if (!write) return "Safety acceptance test protocol preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true; meta["nativeSignature"] = "SafetyAcceptanceTestReport.CreateProtocol(FileInfo, FileOperations)";
                    bool created = report.CreateProtocol(file, operation); meta["result"] = created; if (!created) meta["operationSuccess"] = false;
                    file.Refresh(); if (file.Exists) meta["output"] = NativeFileOutput.Verify(file);
                    return created ? "Safety acceptance test protocol written by TIA (see output); project unchanged."
                        : "SafetyAcceptanceTestReport.CreateProtocol returned false; no file evidence.";
                }
                if (!write) return "Safety acceptance test " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (action == "resetTestFunctions") {
                    meta["nativeSignature"] = "SafetyAcceptanceTestProvider.ResetTestFunctions()"; provider.ResetTestFunctions(); }
                else
                {
                    var function = provider.TestFunctions.Find(identifier)
                        ?? throw new PlcSoftwareException("NotFound", "Test function not found: " + identifier);
                    meta["nativeSignature"] = "TestFunction.Active = " + (active ? "true" : "false"); function.Active = active;
                    meta["activeVerified"] = (provider.TestFunctions.Find(identifier)?.Active ?? !active) == active;
                }
                meta["after"] = Rows();
                return "Safety acceptance test " + action + " executed and functions read back; no download.";
#endif
            });

        // ---- online (connected drive) --------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadOnlineDriveParameters(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0,
                int driveObjectIndex = -1, string namesJson = "[]", string numbersJson = "[]", bool includeBits = false, bool includeEnumValues = false,
                int offset = 0, int limit = 100, bool includeValue = true)
            => _session.RunHmiStepTool("GetOnlineDriveParameters", meta =>
            {
                var selector = Logic.ValidateParametersRequest("read", namesJson, numbersJson, offset, limit);
                var item = _session.ExactDriveItem(devicePathJson, itemPathJson);
                var drive = ExactOnlineDriveObject(item, Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["online"] = true;
                Safe(meta, "driveObjectNumber", () => drive.DriveObjectNumber);
                ReadDriveParameterComposition parameters = drive.ReadParameters; var missing = new HmiArray(); object[] rows;
                if (selector.Enumerate) {
                    var all = EngineeringGroupOperations.Items(parameters).Cast<ReadDriveParameter>().Skip(offset).Take(limit + 1).ToArray(); rows = all
                        .Take(limit).Select(p => (object)DriveParameterRow(p, includeBits, includeEnumValues, includeValue)).ToArray(); meta["truncated"] = all
                        .Length > limit; meta["nextOffset"] = all.Length > limit ? offset + limit : (int?)null;
                }
                else rows = selector.Names.Select(n => (Name: n, Parameter: FindParameter(parameters, n)))
                    .Concat(selector.Numbers
                        .Select(x =>
                            (Name: x.Number + (x.ArrayIndex >= 0 ? "[" + x.ArrayIndex + "]" : ""), Parameter: parameters.Find(x.Number, x.ArrayIndex))))
                    .Where(x => { if (x.Parameter == null) missing.Add(x.Name); return x.Parameter != null; })
                        .Select(x => (object)DriveParameterRow(x.Parameter!, includeBits, includeEnumValues, includeValue)).ToArray();
                meta["records"] = new HmiArray(rows); meta["actualCount"] = rows.Length; meta["offset"] = offset; meta["limit"] = limit; meta["notFound"] =
                    missing; meta["includeValue"] = includeValue;
                return "Online drive parameters read from the connected drive (OnlineDriveObject.ReadParameters); nothing written.";
            });

        public HardwareAddressingReply ManageOnlineDriveFunctions(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0,
                int driveObjectIndex = -1, string action = "read", string resetMode = "", string activationState = "", bool confirmOnline = false)
            => _session.RunHmiStepTool("ManageOnlineDriveFunctions", meta =>
            {
                Logic.ValidateOnlineFunctionRequest(action, resetMode, activationState, confirmOnline);
                var item = _session.ExactDriveItem(devicePathJson, itemPathJson);
                var drive = ExactOnlineDriveObject(item, Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
                var dfi = drive.GetService<OnlineDriveFunctionInterface>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "OnlineDriveFunctionInterface not provided (drive offline or unsupported).");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["online"] = true;
                meta["mayHaveChanged"] = false;
                HmiObject Row()
                {
                    var row = new HmiObject();
                    Safe(row, "driveDomainFunctionsAvailable", () => dfi.DriveDomainFunctions != null);
                    Safe(row, "hardwareProjectionAvailable", () => dfi.HardwareProjection != null);
                    Safe(row, "functionInUseAvailable", () => dfi.FunctionInUse != null);
                    Safe(row, "activation", () => { var a = dfi.DriveObjectFunctions?.DriveObjectActivation; return a == null ? null : ActivationRow(a); });
                    Safe(row, "securityAvailable", () => drive.Security != null);
                    return row;
                }
                meta["before"] = Row();
                if (action == "read")
                    return "Online drive function interface read (domain functions / hardware projection / function in use availability, activation); " +
                    "nothing written.";
                meta["mayHaveChanged"] = true; bool result;
                switch (action)
                {
                    case "performFactoryReset": {
                        var functions = dfi.DriveDomainFunctions
                            ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveDomainFunctions not provided (G120: Power Module required).");
                        meta["nativeSignature"] = "DriveDomainFunctions.PerformFactoryReset(ResetMode)"; result = functions
                            .PerformFactoryReset((ResetMode)Enum.Parse(typeof(ResetMode), resetMode)); break;
                    }
                    case "performRamToRomCopy": {
                        var functions = dfi.DriveDomainFunctions
                            ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveDomainFunctions not provided (G120: Power Module required).");
                        meta["nativeSignature"] = "DriveDomainFunctions.PerformRAMtoROMCopyAllDriveObject()"; result = functions
                            .PerformRAMtoROMCopyAllDriveObject(); break;
                    }
                    default: {
                        var activation = dfi.DriveObjectFunctions?.DriveObjectActivation
                            ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveObjectActivation not provided online."); meta["nativeSignature"] =
                            "DriveObjectActivation.ChangeActivationState(DriveObjectActivationState)"; result = activation
                            .ChangeActivationState((DriveObjectActivationState)Enum.Parse(typeof(DriveObjectActivationState), activationState)); break;
                    }
                }
                meta["result"] = result; if (!result) meta["operationSuccess"] = false; meta["after"] = Row();
                return result ? "Online drive function " + action + " executed on the connected drive (native result true)." : "Online drive function "
                    + action + " returned false (the drive refused it).";
            });
    }
}

#endif
