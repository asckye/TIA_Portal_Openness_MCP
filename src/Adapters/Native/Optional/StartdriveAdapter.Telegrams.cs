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
        public HardwareAddressingReply ManageDriveTelegrams(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1,
                string action = "read", string telegramType = "", int telegramNumber = -1,
            int inputSize = -1, int outputSize = -1, string direction = "", int size = -1, bool keepOriginalAddress = true, string softwarePath = "",
                string objectPath = "", string interfaceKind = "", int sensorIndex = 0, string connectOption = "", bool dryRun = true)
            => _session.RunHmiStepTool("ManageDriveTelegrams", meta =>
            {
                var r = Logic
                    .ValidateTelegramRequest(action, telegramType, telegramNumber, inputSize, outputSize, direction, size, keepOriginalAddress, softwarePath,
                        objectPath, interfaceKind, sensorIndex, connectOption, dryRun);
                using var access = r.Writes ? _session.AcquireHmiEditAccess() : null;
                var drive = _session.ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                TelegramComposition telegrams = drive.Telegrams;
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun;
                meta["mayHaveChanged"] = false;
                HmiArray Rows() => new HmiArray(EngineeringGroupOperations.Items(telegrams).Cast<Telegram>().Select(t => (object)TelegramRow(t)).ToArray());
                meta["before"] = Rows();
                if (action == "read")
                    return "Drive object telegrams read (Telegram type / number / sizes / addresses / hardware identifiers / PKW); no modification.";
                var type = (TelegramType)Enum.Parse(typeof(TelegramType), r.TelegramType);
                Telegram? existing = telegrams.Find(type);
                var checks = new HmiObject();
                // TIA V21, 2026-09-19 (docs/reference/real-machine-ledger.md), real project (freshly added G120C without a bus interface, Telegrams empty): CanInsertAdditionalTelegram(2, 4) threw
                // "Invalid operation" and CanInsertTelegram(700, SupplementaryTelegram) took TIA Portal V21 down. The Can* / Insert* family
                // presupposes a networked drive object with its main telegram; without any telegram nothing is asked of TIA.
                if (telegrams.Count == 0 && action != "read")
                    throw new PlcSoftwareException("InvalidState",
                            "This drive object has no telegram yet (not connected to a PROFINET / PROFIBUS IO system): TelegramComposition.Can* / Insert* / " +
                            "Erase are not called in this state (TIA Portal V21 crashed on a G120C here). Connect the drive to the IO system first, then " +
                            "retry.");
                // TIA V21, 2026-09-19 (docs/reference/real-machine-ledger.md), real project (G120C, main telegram present): CanInsertTelegram(1, MainTelegram) answered false and the following
                // CanInsertMainTelegram(1) took TIA Portal V21 down. Official: main telegrams are only inserted / erased on G220 drives, so
                // neither Can* nor Insert* is called while a main telegram exists - changeNumber / changeSize edit the existing one.
                if (type == TelegramType.MainTelegram && existing != null && (action == "check" || action == "insert"))
                {
                    checks["mainTelegramPresent"] = true; checks["canInsertMainTelegram"] = null;
                    checks["note"] =
                        "A MainTelegram already exists on this drive object: CanInsertMainTelegram / InsertMainTelegram are not called (TIA Portal V21 " +
                        "crashed on a G120C in that state; main telegrams can only be added on G220 drives). Use changeNumber / changeSize.";
                    meta["checks"] = checks;
                    if (action == "insert")
                        throw new PlcSoftwareException("InvalidState", "MainTelegram already exists on this drive object (number " + existing.TelegramNumber
                            + "); insert is refused - use changeNumber or changeSize instead.");
                    return "Telegram feasibility: a MainTelegram is already present, the native Can* checks for inserting another one are skipped; nothing " +
                        "changed.";
                }
                if (action == "check" || action == "insert")
                {
                    if (type == TelegramType.AdditionalTelegram) checks["canInsertAdditionalTelegram"] = telegrams
                        .CanInsertAdditionalTelegram(r.InputSize, r.OutputSize);
                    else
                    {
                        Safe(checks, "canInsertTelegram", () => telegrams.CanInsertTelegram(r.TelegramNumber, type));
                        switch (type)
                        {
                            case TelegramType.MainTelegram: Safe(checks, "canInsertMainTelegram", () => telegrams.CanInsertMainTelegram(r.TelegramNumber));
                            break;
                            case TelegramType.SupplementaryTelegram
                                : Safe(checks, "canInsertSupplementaryTelegram", () => telegrams.CanInsertSupplementaryTelegram(r.TelegramNumber)); break;
                            case TelegramType.SafetyTelegram
                                : Safe(checks, "canInsertSafetyTelegram", () => telegrams.CanInsertSafetyTelegram(r.TelegramNumber)); break;
                            case TelegramType.TorqueTelegram
                                : Safe(checks, "canInsertTorqueTelegram", () => telegrams.CanInsertTorqueTelegram(r.TelegramNumber)); break;
                        }
                    }
                    if (existing != null && r.TelegramNumber >= 0) Safe(checks, "canChangeTelegram", () => existing.CanChangeTelegram(r.TelegramNumber));
                }
                if (action == "changeNumber") {
                    if (existing == null) throw new PlcSoftwareException("NotFound", "No " + type + " on this drive object."); checks["canChangeTelegram"] =
                        existing.CanChangeTelegram(r.TelegramNumber);
                }
                if (action == "changeSize") {
                    if (existing == null) throw new PlcSoftwareException("NotFound", "No " + type + " on this drive object."); var dir = (AddressIoType)Enum
                        .Parse(typeof(AddressIoType), r.Direction); checks["currentWords"] = existing.GetSize(dir); checks["canChangeSize"] = existing
                        .CanChangeSize(dir, r.Size, r.KeepOriginalAddress);
                }
                if (action == "erase" && existing == null) throw new PlcSoftwareException("NotFound", "No " + type + " on this drive object to erase.");
                if (action == "connectTechnologyObject" && existing == null)
                    throw new PlcSoftwareException("NotFound", "No " + type + " on this drive object to connect.");
                meta["checks"] = checks;
                if (action == "check") return "Telegram feasibility checked with the native Can* methods; nothing changed.";
                if (!r.Writes) return "Telegram " + action + " preview; nothing changed (native Can* results in checks).";
                if (action == "insert")
                {
                    string check = type == TelegramType.AdditionalTelegram ? "canInsertAdditionalTelegram"
                        : type == TelegramType.MainTelegram ? "canInsertMainTelegram"
                        : type == TelegramType.SupplementaryTelegram ? "canInsertSupplementaryTelegram"
                        : type == TelegramType.SafetyTelegram ? "canInsertSafetyTelegram"
                        : type == TelegramType.TorqueTelegram ? "canInsertTorqueTelegram" : "canInsertTelegram";
                    OptionalNativeOperations.RequireCanChange(checks[check]);
                    if (type != TelegramType.AdditionalTelegram) OptionalNativeOperations.RequireCanChange(checks["canInsertTelegram"]);
                }
                if (action == "changeNumber") OptionalNativeOperations.RequireCanChange(checks["canChangeTelegram"]);
                if (action == "changeSize") OptionalNativeOperations.RequireCanChange(checks["canChangeSize"]);
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "insert":
                        if (type == TelegramType.AdditionalTelegram) {
                            meta["nativeSignature"] = "TelegramComposition.InsertAdditionalTelegram(int, int)"; telegrams
                                .InsertAdditionalTelegram(r.InputSize, r.OutputSize);
                        }
                        else if (type == TelegramType.MainTelegram) {
                            meta["nativeSignature"] = "TelegramComposition.InsertMainTelegram(int)"; telegrams.InsertMainTelegram(r.TelegramNumber); }
                        else if (type == TelegramType.SupplementaryTelegram) {
                            meta["nativeSignature"] = "TelegramComposition.InsertSupplementaryTelegram(int)"; telegrams
                                .InsertSupplementaryTelegram(r.TelegramNumber);
                        }
                        else if (type == TelegramType.SafetyTelegram) {
                            meta["nativeSignature"] = "TelegramComposition.InsertSafetyTelegram(int)"; telegrams.InsertSafetyTelegram(r.TelegramNumber); }
                        else if (type == TelegramType.TorqueTelegram) {
                            meta["nativeSignature"] = "TelegramComposition.InsertTorqueTelegram(int)"; telegrams.InsertTorqueTelegram(r.TelegramNumber); }
                        else {
                            meta["nativeSignature"] = "TelegramComposition.InsertTelegram(int, TelegramType)"; telegrams.InsertTelegram(r.TelegramNumber, type);
                        }
                        break;
                    case "erase": meta["nativeSignature"] = "TelegramComposition.EraseTelegram(TelegramType)"; telegrams.EraseTelegram(type); break;
                    case "changeNumber": meta["nativeSignature"] = "Telegram.TelegramNumber = n"; existing!.TelegramNumber = r.TelegramNumber; break;
                    case "changeSize": meta["nativeSignature"] = "Telegram.ChangeSize(AddressIoType, int, bool)"; meta["result"] = existing!
                        .ChangeSize((AddressIoType)Enum.Parse(typeof(AddressIoType), r.Direction), r.Size, r.KeepOriginalAddress); break;
                    case "connectTechnologyObject": ConnectTelegramToTechnologyObject(existing!, r, meta); break;
                }
                // official note: telegram objects become invalid after size changes - always re-navigate for the readback
                var fresh = drive.Telegrams;
                meta["after"] = new HmiArray(EngineeringGroupOperations.Items(fresh).Cast<Telegram>().Select(t => (object)TelegramRow(t)).ToArray());
                if (action == "erase") meta["verifiedAbsent"] = fresh.Find(type) == null;
                if (action == "insert") meta["verifiedPresent"] = fresh.Find(type) != null;
                return "Telegram " + action + " executed and telegrams read back on a fresh navigation; no download or drive command.";
            });

        // V21: the SDR interfaces of the axis / encoder technology object take a Telegram; V20 has Connect(Telegram[, MC.Drives.Enums.ConnectOption]) on the base interfaces.
        private void ConnectTelegramToTechnologyObject(Telegram telegram, Logic.TelegramRequest r, HmiObject meta)
        {
            var target = _session.ExactTechnology(r.SoftwarePath, r.ObjectPath, true);
            var provider = target as IEngineeringServiceProvider ?? throw new NotSupportedException("Technology object is not a service provider.");
            meta["technologyObject"] = r.ObjectPath; meta["interfaceKind"] = r.InterfaceKind;
#if !OPTIONAL_V21
            var option = r.HasConnectOption ? (DriveConnectOption)Enum.Parse(typeof(DriveConnectOption), r.ConnectOption) : DriveConnectOption.Default;
            if (r.InterfaceKind == "encoder")
            {
                var encoder = provider.GetService<EncoderHardwareConnectionProvider>()
                    ?? throw new NotSupportedException("EncoderHardwareConnectionProvider not provided by " + target.GetType().Name + ".");
                if (r.HasConnectOption) encoder.SensorInterface.Connect(telegram, option); else encoder.SensorInterface.Connect(telegram);
                meta["nativeSignature"] = "AxisEncoderHardwareConnectionInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")";
                meta["after"] = _session.InterfaceRow(encoder.SensorInterface); return;
            }
            var axis = provider.GetService<AxisHardwareConnectionProvider>()
                ?? throw new NotSupportedException("AxisHardwareConnectionProvider not provided by " + target.GetType().Name
                    + " (axis technology objects only).");
            if (r.InterfaceKind == "torque") {
                if (r.HasConnectOption) axis.TorqueInterface.Connect(telegram, option); else axis.TorqueInterface.Connect(telegram); meta["after"] = _session
                    .InterfaceRow(axis.TorqueInterface);
            }
            else
            {
                AxisEncoderHardwareConnectionInterface iface = r.InterfaceKind == "actor" ? axis.ActorInterface : axis.SensorInterface[r.SensorIndex];
                if (r.HasConnectOption) iface.Connect(telegram, option); else iface.Connect(telegram); meta["after"] = _session.InterfaceRow(iface);
            }
            meta["nativeSignature"] = "AxisEncoderHardwareConnectionInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")";
#else
            var option = r.HasConnectOption ? (MotionConnectOption)Enum.Parse(typeof(MotionConnectOption), r.ConnectOption) : MotionConnectOption.Default;
            if (r.InterfaceKind == "encoder")
            {
                var encoder = provider.GetService<EncoderHardwareConnectionSDRProvider>()
                    ?? throw new NotSupportedException("EncoderHardwareConnectionSDRProvider not provided by " + target.GetType().Name
                        + " (external encoder technology objects only).");
                AxisEncoderHardwareConnectionSDRInterface sensor = encoder.SensorInterface;
                if (r.HasConnectOption) sensor.Connect(telegram, option); else sensor.Connect(telegram);
                meta["nativeSignature"] = "AxisEncoderHardwareConnectionSDRInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")";
                meta["after"] = _session.InterfaceRow(sensor); return;
            }
            var axis = provider.GetService<AxisHardwareConnectionSDRProvider>()
                ?? throw new NotSupportedException("AxisHardwareConnectionSDRProvider not provided by " + target.GetType().Name
                    + " (axis technology objects only).");
            if (r.InterfaceKind == "torque")
            {
                TorqueHardwareConnectionSDRInterface torque = axis.TorqueInterface;
                if (r.HasConnectOption) torque.Connect(telegram, option); else torque.Connect(telegram);
                meta["nativeSignature"] = "TorqueHardwareConnectionSDRInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")";
                meta["after"] = _session.InterfaceRow(torque); return;
            }
            AxisEncoderHardwareConnectionSDRInterfaceComposition sensors = axis.SensorInterface;
            if (r.InterfaceKind == "sensor" && r.SensorIndex >= sensors.Count)
                throw new ArgumentException("sensorIndex out of range: " + sensors.Count + " sensor interface(s).");
            AxisEncoderHardwareConnectionSDRInterface iface = r.InterfaceKind == "actor" ? axis.ActorInterface : sensors[r.SensorIndex];
            if (r.HasConnectOption) iface.Connect(telegram, option); else iface.Connect(telegram);
            meta["nativeSignature"] = "AxisEncoderHardwareConnectionSDRInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")";
            meta["after"] = _session.InterfaceRow(iface);
#endif
        }

    }
}

#endif
