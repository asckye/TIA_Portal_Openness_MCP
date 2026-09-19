using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;
using Siemens.Engineering.MC.Drives.Enums;
using Siemens.Engineering.MC.Drives.SecurityObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.StartdriveLogic;
using MotionConnectOption = Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption;
using DriveEncoderType = Siemens.Engineering.MC.Drives.Enums.EncoderType;
#if TIA_V20
using DriveConnectOption = Siemens.Engineering.MC.Drives.Enums.ConnectOption;
#endif

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-② (2.7.39): typed Startdrive option package (V21 Siemens.Engineering.Startdrive.dll, V20 inside Siemens.Engineering.dll).
    // DriveObjectContainer is a service of the drive unit / control unit DeviceItem; DriveObject carries Parameters / ReadParameters /
    // Telegrams / Security and the DriveFunctionInterface, TechnologyExtensionContainer and (DCC) DriveControlChartContainer services;
    // OnlineDriveObjectContainer / OnlineDriveFunctionInterface mirror the offline shape for a connected drive. 2.7.38 real project
    // (G120C): DriveObject.DriveObjectNumber throws "Drive object number could not be retrieved", so every selector accepts the
    // composition index and DriveObjectNumber is read defensively.
    public partial class Portal
    {
        // ---- resolution ----------------------------------------------------------------------------------------------------------
        private DeviceItem ExactDriveItem(string devicePathJson, string itemPathJson)
            => ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("itemPathJson must select a device item (the drive unit / control unit), not the device itself.");
        private DriveObjectContainer ExactDriveContainer(DeviceItem item)
            => item.GetService<DriveObjectContainer>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveObjectContainer unavailable on device item '" + item.Name + "' (not a Startdrive drive object host, or Startdrive not installed).");
        private static ushort? SafeDriveObjectNumber(DriveObject drive) { try { return drive.DriveObjectNumber; } catch { return null; } }
        private static DriveObject ExactDriveObject(DriveObjectContainer container, Logic.DriveSelector selector)
        {
            DriveObjectComposition objects = container.DriveObjects;
            if (selector.ByNumber)
            {
                var matches = objects.Where(o => SafeDriveObjectNumber(o) == selector.Number).Take(2).ToArray();
                if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, "Exactly one drive object with " + selector.Label + " expected, found " + matches.Length + " (use driveObjectIndex when DriveObjectNumber is unavailable on this drive).");
                return matches[0];
            }
            if (selector.Index >= objects.Count) throw new PortalException(PortalErrorCode.NotFound, selector.Label + " out of range: the container has " + objects.Count + " drive object(s).");
            return objects[selector.Index];
        }
        private DriveObject ExactDriveObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, int driveObjectIndex)
            => ExactDriveObject(ExactDriveContainer(ExactDriveItem(devicePathJson, itemPathJson)), Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
        private static OnlineDriveObject ExactOnlineDriveObject(DeviceItem item, Logic.DriveSelector selector)
        {
            var container = item.GetService<OnlineDriveObjectContainer>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "OnlineDriveObjectContainer unavailable on device item '" + item.Name + "' (drive not online, or not a Startdrive drive object host).");
            OnlineDriveObjectComposition objects = container.OnlineDriveObjects;
            if (selector.ByNumber)
            {
                var matches = objects.Where(o => { try { return o.DriveObjectNumber == selector.Number; } catch { return false; } }).Take(2).ToArray();
                if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, "Exactly one online drive object with " + selector.Label + " expected, found " + matches.Length + ".");
                return matches[0];
            }
            if (selector.Index >= objects.Count) throw new PortalException(PortalErrorCode.NotFound, selector.Label + " out of range: " + objects.Count + " online drive object(s).");
            return objects[selector.Index];
        }

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private static JsonObject BicoOrScalar(object? value) => value switch
        {
            DriveParameter source => new JsonObject { ["bicoSource"] = source.Name, ["parameterText"] = source.ParameterText },
            ReadDriveParameter source => new JsonObject { ["bicoSource"] = source.Name, ["parameterText"] = source.ParameterText },
            _ => new JsonObject { ["value"] = EngineeringScalarProperties.Json(value) }
        };
        private static JsonObject EnumValues(IDictionary<int, string>? list)
        {
            var o = new JsonObject(); if (list == null) return o;
            foreach (var pair in list.Take(500)) o[pair.Key.ToString()] = pair.Value; return o;
        }
        private static JsonObject DriveParameterRow(DriveParameter p, bool bits, bool enums)
        {
            var row = new JsonObject { ["name"] = p.Name, ["parameterClass"] = "DriveParameter" };
            Safe(row, "number", () => p.Number); Safe(row, "arrayIndex", () => p.ArrayIndex); Safe(row, "arrayLength", () => p.ArrayLength);
            Safe(row, "value", () => BicoOrScalar(p.Value)); Safe(row, "minValue", () => EngineeringScalarProperties.Json(p.MinValue)); Safe(row, "maxValue", () => EngineeringScalarProperties.Json(p.MaxValue));
            Safe(row, "unit", () => p.Unit); Safe(row, "parameterText", () => p.ParameterText);
            if (enums) Safe(row, "enumValueList", () => EnumValues(p.EnumValueList));
            if (bits) Safe(row, "bits", () => new JsonArray(p.Bits.Take(64).Select(b => (JsonNode)DriveParameterRow(b, false, enums)).ToArray()));
            return row;
        }
        private static JsonObject DriveParameterRow(ReadDriveParameter p, bool bits, bool enums)
        {
            var row = new JsonObject { ["name"] = p.Name, ["parameterClass"] = "ReadDriveParameter" };
            Safe(row, "number", () => p.Number); Safe(row, "arrayIndex", () => p.ArrayIndex); Safe(row, "arrayLength", () => p.ArrayLength);
            Safe(row, "value", () => BicoOrScalar(p.Value)); Safe(row, "minValue", () => EngineeringScalarProperties.Json(p.MinValue)); Safe(row, "maxValue", () => EngineeringScalarProperties.Json(p.MaxValue));
            Safe(row, "unit", () => p.Unit); Safe(row, "parameterText", () => p.ParameterText);
            if (enums) Safe(row, "enumValueList", () => EnumValues(p.EnumValueList));
            if (bits) Safe(row, "bits", () => new JsonArray(p.Bits.Take(64).Select(b => (JsonNode)DriveParameterRow(b, false, enums)).ToArray()));
            return row;
        }
        private static JsonObject TelegramRow(Telegram t, bool nested = false)
        {
            var row = new JsonObject();
            Safe(row, "type", () => t.Type.ToString()); Safe(row, "telegramNumber", () => t.TelegramNumber);
            Safe(row, "inputWords", () => t.GetSize(AddressIoType.Input)); Safe(row, "outputWords", () => t.GetSize(AddressIoType.Output));
            Safe(row, "inputBytes", () => t.GetSizeInBytes(AddressIoType.Input)); Safe(row, "outputBytes", () => t.GetSizeInBytes(AddressIoType.Output));
            Safe(row, "addresses", () => new JsonArray(EngineeringGroupOperations.Items(t.Addresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)).ToArray()));
            Safe(row, "hwIdentifiers", () => new JsonArray(EngineeringGroupOperations.Items(t.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)h.Identifier).ToArray()));
            if (!nested) Safe(row, "pkw", () => t.PKW == null ? null : TelegramRow(t.PKW, true));
            return row;
        }
        private static JsonObject DriveObjectTypeRow(DriveObjectType t) => new JsonObject { ["name"] = t.Name, ["number"] = t.Number };
        private static JsonObject ActivationRow(DriveObjectActivation a) => new JsonObject { ["activationState"] = a.ActivationState.ToString(), ["isActive"] = a.IsActive };
        private static JsonObject FunctionInterfaceRow(DriveFunctionInterface dfi)
        {
            var row = new JsonObject();
            Safe(row, "driveObjectType", () => { var h = dfi.DriveObjectFunctions?.DriveObjectTypeHandler; return h == null ? null : new JsonObject { ["current"] = h.CurrentDriveObjectType == null ? null : DriveObjectTypeRow(h.CurrentDriveObjectType), ["possible"] = new JsonArray(EngineeringGroupOperations.Items(h.PossibleDriveObjectTypes).Cast<DriveObjectType>().Select(t => (JsonNode)DriveObjectTypeRow(t)).ToArray()) }; });
            Safe(row, "activation", () => { var a = dfi.DriveObjectFunctions?.DriveObjectActivation; return a == null ? null : ActivationRow(a); });
            Safe(row, "functionInUseAvailable", () => dfi.FunctionInUse != null); Safe(row, "commissioningAvailable", () => dfi.Commissioning != null);
            Safe(row, "safetyCommissioningAvailable", () => dfi.SafetyCommissioning != null); Safe(row, "hardwareProjectionAvailable", () => dfi.HardwareProjection != null);
            return row;
        }
        private static JsonObject ConfigurationEntryRow(ConfigurationEntry e)
        {
            var row = new JsonObject { ["name"] = e.Name };
            Safe(row, "number", () => e.Number); Safe(row, "value", () => EngineeringScalarProperties.Json(e.Value)); Safe(row, "minValue", () => EngineeringScalarProperties.Json(e.MinValue));
            Safe(row, "maxValue", () => EngineeringScalarProperties.Json(e.MaxValue)); Safe(row, "unit", () => e.Unit); Safe(row, "description", () => e.Description); Safe(row, "enumValueList", () => EnumValues(e.EnumValueList));
            return row;
        }
        private static JsonArray EntryRows(ConfigurationEntryComposition entries) => new JsonArray(EngineeringGroupOperations.Items(entries).Cast<ConfigurationEntry>().Take(500).Select(e => (JsonNode)ConfigurationEntryRow(e)).ToArray());
        private static JsonObject MotorConfigurationRow(MotorConfiguration c)
        {
            var row = new JsonObject();
            Safe(row, "requiredEntries", () => EntryRows(c.RequiredConfigurationEntries)); Safe(row, "optionalEntries", () => EntryRows(c.OptionalConfigurationEntries)); Safe(row, "canSetEquivalentCircuitDiagramData", () => c.CanSetEquivalentCircuitDiagramData());
            return row;
        }
        private static JsonObject EncoderConfigurationRow(EncoderConfiguration c)
        {
            var row = new JsonObject();
            Safe(row, "requiredEntries", () => EntryRows(c.RequiredConfigurationEntries)); Safe(row, "encoderTypes", () => EntryRows(c.EncoderTypes));
            return row;
        }
        private static JsonObject TechnologyExtensionRow(TechnologyExtension e, bool parameters)
        {
            var row = new JsonObject { ["identifier"] = e.Identifier };
            Safe(row, "name", () => e.Name); Safe(row, "displayName", () => e.DisplayName); Safe(row, "isActivated", () => e.IsActivated);
            if (parameters) Safe(row, "parameters", () => new JsonArray(EngineeringGroupOperations.Items(e.Parameters).Cast<DriveParameter>().Take(200).Select(p => (JsonNode)DriveParameterRow(p, false, false)).ToArray()));
            else Safe(row, "parameterCount", () => e.Parameters.Count);
            return row;
        }
        private static JsonObject TechnologyExtensionPackageRow(TechnologyExtensionPackage p) => new JsonObject { ["identifier"] = p.Identifier, ["name"] = p.Name, ["displayName"] = p.DisplayName };
        private static JsonObject DriveObjectRow(DriveObject d, int index, bool telegrams, bool functions, bool extensions, bool dcc)
        {
            var row = new JsonObject { ["index"] = index };
            Safe(row, "driveObjectNumber", () => d.DriveObjectNumber);
            Safe(row, "telegramCount", () => d.Telegrams.Count);
            if (telegrams) Safe(row, "telegrams", () => new JsonArray(EngineeringGroupOperations.Items(d.Telegrams).Cast<Telegram>().Select(t => (JsonNode)TelegramRow(t)).ToArray()));
            Safe(row, "securityAvailable", () => d.Security != null);
            if (functions) Safe(row, "functionInterface", () => { var dfi = d.GetService<DriveFunctionInterface>(); return dfi == null ? null : FunctionInterfaceRow(dfi); });
            if (extensions) Safe(row, "technologyExtensions", () => { var c = d.GetService<TechnologyExtensionContainer>(); return c == null ? null : new JsonArray(EngineeringGroupOperations.Items(c.TechnologyExtensions).Cast<TechnologyExtension>().Select(e => (JsonNode)TechnologyExtensionRow(e, false)).ToArray()); });
            if (dcc) Safe(row, "dcc", () => DccContainerSummary(d));
            return row;
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public ResponseMessage ReadDriveObjects(string devicePathJson, string itemPathJson, bool includeTelegrams = true, bool includeFunctions = true, bool includeTechnologyExtensions = true, bool includeDcc = true)
            => RunHmiStepTool("ReadDriveObjects", meta =>
            {
                var item = ExactDriveItem(devicePathJson, itemPathJson);
                var container = ExactDriveContainer(item);
                meta["deviceItem"] = item.Name; meta["hostClass"] = item.GetType().Name;
                Safe(meta, "moduleAccessPointHwIdentifiers", () => { var ap = item.GetService<ModuleAccessPoint>(); return ap == null ? null : new JsonArray(EngineeringGroupOperations.Items(ap.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)h.Identifier).ToArray()); });
#if !TIA_V20
                Safe(meta, "hardwareModule", () => { var m = item.GetService<DriveItemHardwareModule>(); return m == null ? null : HardwareModuleRow(m); });
#endif
                Safe(meta, "onlineContainerAvailable", () => item.GetService<OnlineDriveObjectContainer>() != null);
                var rows = EngineeringGroupOperations.Items(container.DriveObjects).Cast<DriveObject>().Select((d, i) => (JsonNode)DriveObjectRow(d, i, includeTelegrams, includeFunctions, includeTechnologyExtensions, includeDcc)).ToArray();
                meta["records"] = new JsonArray(rows); meta["expectedCount"] = rows.Length; meta["actualCount"] = rows.Length;
                return "Drive objects of the device item read (DriveObjectContainer.DriveObjects with telegrams, function interface, technology extensions and DCC summary); no modification.";
            });

        public ResponseMessage ReadDriveParameters(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string source = "read", string namesJson = "[]", string numbersJson = "[]",
            bool includeBits = false, bool includeEnumValues = false, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadDriveParameters", meta =>
            {
                var selector = Logic.ValidateParametersRequest(source, namesJson, numbersJson, offset, limit);
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["source"] = source == "read" ? "ReadParameters" : "Parameters";
                var missing = new JsonArray();
                JsonNode[] rows;
                if (source == "read")
                {
                    ReadDriveParameterComposition parameters = drive.ReadParameters;
                    if (selector.Enumerate) { var all = EngineeringGroupOperations.Items(parameters).Cast<ReadDriveParameter>().Skip(offset).Take(limit + 1).ToArray(); rows = all.Take(limit).Select(p => (JsonNode)DriveParameterRow(p, includeBits, includeEnumValues)).ToArray(); meta["truncated"] = all.Length > limit; meta["nextOffset"] = all.Length > limit ? offset + limit : (int?)null; }
                    else rows = selector.Names.Select(n => (Name: n, Parameter: parameters.Find(n))).Concat(selector.Numbers.Select(x => (Name: x.Number + (x.ArrayIndex >= 0 ? "[" + x.ArrayIndex + "]" : ""), Parameter: parameters.Find(x.Number, x.ArrayIndex))))
                        .Where(x => { if (x.Parameter == null) missing.Add(x.Name); return x.Parameter != null; }).Select(x => (JsonNode)DriveParameterRow(x.Parameter!, includeBits, includeEnumValues)).ToArray();
                }
                else
                {
                    DriveParameterComposition parameters = drive.Parameters;
                    if (selector.Enumerate) { var all = EngineeringGroupOperations.Items(parameters).Cast<DriveParameter>().Skip(offset).Take(limit + 1).ToArray(); rows = all.Take(limit).Select(p => (JsonNode)DriveParameterRow(p, includeBits, includeEnumValues)).ToArray(); meta["truncated"] = all.Length > limit; meta["nextOffset"] = all.Length > limit ? offset + limit : (int?)null; }
                    else rows = selector.Names.Select(n => (Name: n, Parameter: parameters.Find(n))).Concat(selector.Numbers.Select(x => (Name: x.Number + (x.ArrayIndex >= 0 ? "[" + x.ArrayIndex + "]" : ""), Parameter: parameters.Find(x.Number, x.ArrayIndex))))
                        .Where(x => { if (x.Parameter == null) missing.Add(x.Name); return x.Parameter != null; }).Select(x => (JsonNode)DriveParameterRow(x.Parameter!, includeBits, includeEnumValues)).ToArray();
                }
                meta["records"] = new JsonArray(rows); meta["actualCount"] = rows.Length; meta["offset"] = offset; meta["limit"] = limit; meta["notFound"] = missing;
                if (selector.Enumerate) meta["scope"] = "Enumeration of the composition in native order; the drive has thousands of parameters, so page with offset / limit or ask by namesJson / numbersJson.";
                return "Offline drive parameters read (" + (source == "read" ? "ReadDriveParameter rows incl. BICO sources" : "DriveParameter rows incl. BICO sources") + "); no OnlineDriveObject or drive access.";
            });

        // Typed retrofit of the 2.7.x reflective tool (same signature plus driveObjectIndex): Parameters.Find(name), scalar or BICO-source writes.
        public ResponseMessage ManageStartdriveParameter(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string parameter, string action = "read", string valueJson = "null", bool dryRun = true, int driveObjectIndex = -1)
            => RunHmiStepTool("ManageStartdriveParameter", meta =>
            {
                Logic.ValidateParameterWriteRequest(parameter, action);
                bool write = action == "write" && !dryRun; using var access = write ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["online"] = false;
                if (action == "read")
                {
                    ReadDriveParameter target = drive.ReadParameters.Find(parameter) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact drive parameter not found: " + parameter);
                    meta["before"] = DriveParameterRow(target, true, true);
                    return "Offline drive parameter read (ReadParameters view with bits, enum values and BICO source). No OnlineDriveObject or live device access used.";
                }
                DriveParameter writable = drive.Parameters.Find(parameter) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact writable drive parameter not found: " + parameter + " (read-only parameters live in ReadParameters).");
                meta["before"] = DriveParameterRow(writable, false, true);
                var value = Logic.ParseParameterValue(valueJson);
                object? converted = null; DriveParameter? source = null;
                if (value.IsBico) { source = drive.Parameters.Find(value.BicoSource) ?? throw new PortalException(PortalErrorCode.NotFound, "BICO source parameter not found: " + value.BicoSource); meta["bicoSource"] = DriveParameterRow(source, false, false); }
                else { var current = writable.Value; converted = current == null || current is DriveParameter ? EngineeringScalarProperties.ConvertValue(value.Scalar, typeof(object)) : EngineeringScalarProperties.ConvertValue(value.Scalar, current.GetType()); meta["requestedValue"] = EngineeringScalarProperties.Json(converted); }
                if (!write) return "Offline drive parameter write preview; native limits / BICO semantics are checked on execution.";
                meta["mayHaveChanged"] = true;
                writable.Value = value.IsBico ? source : converted;      // official: "P2080Bit6.Value = cu.Parameters.Find("r19")" wires a BICO sink
                var after = drive.Parameters.Find(parameter) ?? writable;
                meta["after"] = DriveParameterRow(after, false, true);
                return "Offline drive parameter changed and read back; no download, online parameter write or drive command.";
            });

        public ResponseMessage ManageDriveTelegrams(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string telegramType = "", int telegramNumber = -1,
            int inputSize = -1, int outputSize = -1, string direction = "", int size = -1, bool keepOriginalAddress = true, string softwarePath = "", string objectPath = "", string interfaceKind = "", int sensorIndex = 0, string connectOption = "", bool dryRun = true)
            => RunHmiStepTool("ManageDriveTelegrams", meta =>
            {
                var r = Logic.ValidateTelegramRequest(action, telegramType, telegramNumber, inputSize, outputSize, direction, size, keepOriginalAddress, softwarePath, objectPath, interfaceKind, sensorIndex, connectOption, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                TelegramComposition telegrams = drive.Telegrams;
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                JsonArray Rows() => new JsonArray(EngineeringGroupOperations.Items(telegrams).Cast<Telegram>().Select(t => (JsonNode)TelegramRow(t)).ToArray());
                meta["before"] = Rows();
                if (action == "read") return "Drive object telegrams read (Telegram type / number / sizes / addresses / hardware identifiers / PKW); no modification.";
                var type = (TelegramType)Enum.Parse(typeof(TelegramType), r.TelegramType);
                Telegram? existing = telegrams.Find(type);
                var checks = new JsonObject();
                if (action == "check" || action == "insert")
                {
                    if (type == TelegramType.AdditionalTelegram) checks["canInsertAdditionalTelegram"] = telegrams.CanInsertAdditionalTelegram(r.InputSize, r.OutputSize);
                    else
                    {
                        Safe(checks, "canInsertTelegram", () => telegrams.CanInsertTelegram(r.TelegramNumber, type));
                        switch (type)
                        {
                            case TelegramType.MainTelegram: Safe(checks, "canInsertMainTelegram", () => telegrams.CanInsertMainTelegram(r.TelegramNumber)); break;
                            case TelegramType.SupplementaryTelegram: Safe(checks, "canInsertSupplementaryTelegram", () => telegrams.CanInsertSupplementaryTelegram(r.TelegramNumber)); break;
                            case TelegramType.SafetyTelegram: Safe(checks, "canInsertSafetyTelegram", () => telegrams.CanInsertSafetyTelegram(r.TelegramNumber)); break;
                            case TelegramType.TorqueTelegram: Safe(checks, "canInsertTorqueTelegram", () => telegrams.CanInsertTorqueTelegram(r.TelegramNumber)); break;
                        }
                    }
                    if (existing != null && r.TelegramNumber >= 0) Safe(checks, "canChangeTelegram", () => existing.CanChangeTelegram(r.TelegramNumber));
                }
                if (action == "changeNumber") { if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "No " + type + " on this drive object."); checks["canChangeTelegram"] = existing.CanChangeTelegram(r.TelegramNumber); }
                if (action == "changeSize") { if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "No " + type + " on this drive object."); var dir = (AddressIoType)Enum.Parse(typeof(AddressIoType), r.Direction); checks["currentWords"] = existing.GetSize(dir); checks["canChangeSize"] = existing.CanChangeSize(dir, r.Size, r.KeepOriginalAddress); }
                if (action == "erase" && existing == null) throw new PortalException(PortalErrorCode.NotFound, "No " + type + " on this drive object to erase.");
                if (action == "connectTechnologyObject" && existing == null) throw new PortalException(PortalErrorCode.NotFound, "No " + type + " on this drive object to connect.");
                meta["checks"] = checks;
                if (action == "check") return "Telegram feasibility checked with the native Can* methods; nothing changed.";
                if (!r.Writes) return "Telegram " + action + " preview; nothing changed (native Can* results in checks).";
                meta["mayHaveChanged"] = true;
                switch (action)
                {
                    case "insert":
                        if (type == TelegramType.AdditionalTelegram) { meta["nativeSignature"] = "TelegramComposition.InsertAdditionalTelegram(int, int)"; telegrams.InsertAdditionalTelegram(r.InputSize, r.OutputSize); }
                        else if (type == TelegramType.MainTelegram) { meta["nativeSignature"] = "TelegramComposition.InsertMainTelegram(int)"; telegrams.InsertMainTelegram(r.TelegramNumber); }
                        else if (type == TelegramType.SupplementaryTelegram) { meta["nativeSignature"] = "TelegramComposition.InsertSupplementaryTelegram(int)"; telegrams.InsertSupplementaryTelegram(r.TelegramNumber); }
                        else if (type == TelegramType.SafetyTelegram) { meta["nativeSignature"] = "TelegramComposition.InsertSafetyTelegram(int)"; telegrams.InsertSafetyTelegram(r.TelegramNumber); }
                        else if (type == TelegramType.TorqueTelegram) { meta["nativeSignature"] = "TelegramComposition.InsertTorqueTelegram(int)"; telegrams.InsertTorqueTelegram(r.TelegramNumber); }
                        else { meta["nativeSignature"] = "TelegramComposition.InsertTelegram(int, TelegramType)"; telegrams.InsertTelegram(r.TelegramNumber, type); }
                        break;
                    case "erase": meta["nativeSignature"] = "TelegramComposition.EraseTelegram(TelegramType)"; telegrams.EraseTelegram(type); break;
                    case "changeNumber": meta["nativeSignature"] = "Telegram.TelegramNumber = n"; existing!.TelegramNumber = r.TelegramNumber; break;
                    case "changeSize": meta["nativeSignature"] = "Telegram.ChangeSize(AddressIoType, int, bool)"; meta["result"] = existing!.ChangeSize((AddressIoType)Enum.Parse(typeof(AddressIoType), r.Direction), r.Size, r.KeepOriginalAddress); break;
                    case "connectTechnologyObject": ConnectTelegramToTechnologyObject(existing!, r, meta); break;
                }
                // official note: telegram objects become invalid after size changes - always re-navigate for the readback
                var fresh = drive.Telegrams;
                meta["after"] = new JsonArray(EngineeringGroupOperations.Items(fresh).Cast<Telegram>().Select(t => (JsonNode)TelegramRow(t)).ToArray());
                if (action == "erase") meta["verifiedAbsent"] = fresh.Find(type) == null;
                if (action == "insert") meta["verifiedPresent"] = fresh.Find(type) != null;
                return "Telegram " + action + " executed and telegrams read back on a fresh navigation; no download or drive command.";
            });

        // V21: the SDR interfaces of the axis / encoder technology object take a Telegram; V20 has Connect(Telegram[, MC.Drives.Enums.ConnectOption]) on the base interfaces.
        private void ConnectTelegramToTechnologyObject(Telegram telegram, Logic.TelegramRequest r, JsonObject meta)
        {
            var target = ExactTechnology(r.SoftwarePath, r.ObjectPath, true);
            var provider = target as IEngineeringServiceProvider ?? throw new NotSupportedException("Technology object is not a service provider.");
            meta["technologyObject"] = r.ObjectPath; meta["interfaceKind"] = r.InterfaceKind;
#if TIA_V20
            var option = r.HasConnectOption ? (DriveConnectOption)Enum.Parse(typeof(DriveConnectOption), r.ConnectOption) : DriveConnectOption.Default;
            if (r.InterfaceKind == "encoder")
            {
                var encoder = provider.GetService<EncoderHardwareConnectionProvider>() ?? throw new NotSupportedException("EncoderHardwareConnectionProvider not provided by " + target.GetType().Name + ".");
                if (r.HasConnectOption) encoder.SensorInterface.Connect(telegram, option); else encoder.SensorInterface.Connect(telegram);
                meta["nativeSignature"] = "AxisEncoderHardwareConnectionInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")"; meta["after"] = InterfaceRow(encoder.SensorInterface); return;
            }
            var axis = provider.GetService<AxisHardwareConnectionProvider>() ?? throw new NotSupportedException("AxisHardwareConnectionProvider not provided by " + target.GetType().Name + " (axis technology objects only).");
            if (r.InterfaceKind == "torque") { if (r.HasConnectOption) axis.TorqueInterface.Connect(telegram, option); else axis.TorqueInterface.Connect(telegram); meta["after"] = InterfaceRow(axis.TorqueInterface); }
            else
            {
                AxisEncoderHardwareConnectionInterface iface = r.InterfaceKind == "actor" ? axis.ActorInterface : axis.SensorInterface[r.SensorIndex];
                if (r.HasConnectOption) iface.Connect(telegram, option); else iface.Connect(telegram); meta["after"] = InterfaceRow(iface);
            }
            meta["nativeSignature"] = "AxisEncoderHardwareConnectionInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")";
#else
            var option = r.HasConnectOption ? (MotionConnectOption)Enum.Parse(typeof(MotionConnectOption), r.ConnectOption) : MotionConnectOption.Default;
            if (r.InterfaceKind == "encoder")
            {
                var encoder = provider.GetService<EncoderHardwareConnectionSDRProvider>() ?? throw new NotSupportedException("EncoderHardwareConnectionSDRProvider not provided by " + target.GetType().Name + " (external encoder technology objects only).");
                AxisEncoderHardwareConnectionSDRInterface sensor = encoder.SensorInterface;
                if (r.HasConnectOption) sensor.Connect(telegram, option); else sensor.Connect(telegram);
                meta["nativeSignature"] = "AxisEncoderHardwareConnectionSDRInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")"; meta["after"] = InterfaceRow(sensor); return;
            }
            var axis = provider.GetService<AxisHardwareConnectionSDRProvider>() ?? throw new NotSupportedException("AxisHardwareConnectionSDRProvider not provided by " + target.GetType().Name + " (axis technology objects only).");
            if (r.InterfaceKind == "torque")
            {
                TorqueHardwareConnectionSDRInterface torque = axis.TorqueInterface;
                if (r.HasConnectOption) torque.Connect(telegram, option); else torque.Connect(telegram);
                meta["nativeSignature"] = "TorqueHardwareConnectionSDRInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")"; meta["after"] = InterfaceRow(torque); return;
            }
            AxisEncoderHardwareConnectionSDRInterfaceComposition sensors = axis.SensorInterface;
            if (r.InterfaceKind == "sensor" && r.SensorIndex >= sensors.Count) throw new ArgumentException("sensorIndex out of range: " + sensors.Count + " sensor interface(s).");
            AxisEncoderHardwareConnectionSDRInterface iface = r.InterfaceKind == "actor" ? axis.ActorInterface : sensors[r.SensorIndex];
            if (r.HasConnectOption) iface.Connect(telegram, option); else iface.Connect(telegram);
            meta["nativeSignature"] = "AxisEncoderHardwareConnectionSDRInterface.Connect(Telegram" + (r.HasConnectOption ? ", ConnectOption" : "") + ")"; meta["after"] = InterfaceRow(iface);
#endif
        }

        public ResponseMessage ManageDriveFunctions(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string valueJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageDriveFunctions", meta =>
            {
                var r = Logic.ValidateFunctionRequest(action, valueJson, dryRun);
                using var access = r.Writes ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var dfi = drive.GetService<DriveFunctionInterface>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveFunctionInterface not provided by this drive object.");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["before"] = FunctionInterfaceRow(dfi);
                if (action == "read") return "Drive function interface read (drive object type handler, activation, function-in-use / commissioning / safety / hardware projection availability); no modification.";
                T Need<T>(T? value, string what) where T : class => value ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, what + " is not provided by this drive object.");
                HardwareProjection Projection() => Need(dfi.HardwareProjection, "HardwareProjection (G120: needs the Power Module; S120: offline only)");
                if (action == "readMotorConfiguration") { meta["dataSet"] = r.DataSet; meta["motorConfiguration"] = MotorConfigurationRow(Projection().GetCurrentMotorConfiguration((ushort)r.DataSet)); return "Current motor configuration read (set the motor type first on G120 drives); no modification."; }
                if (action == "readEncoderConfiguration") { meta["encoderNumber"] = r.EncoderNumber; meta["encoderConfiguration"] = EncoderConfigurationRow(Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber)); return "Current encoder configuration read; no modification."; }
                if (!r.Writes) return "Drive function " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                bool result = true;
                switch (action)
                {
                    case "changeDriveObjectType":
                        {
                            var handler = Need(dfi.DriveObjectFunctions?.DriveObjectTypeHandler, "DriveObjectTypeHandler");
                            var target = handler.PossibleDriveObjectTypes.Find(r.DriveObjectType) ?? throw new PortalException(PortalErrorCode.NotFound, "Drive object type not among PossibleDriveObjectTypes: " + r.DriveObjectType);
                            meta["nativeSignature"] = "DriveObjectTypeHandler.ChangeDriveObjectType(DriveObjectType)"; result = handler.ChangeDriveObjectType(target); break;
                        }
                    case "changeActivationState": meta["nativeSignature"] = "DriveObjectActivation.ChangeActivationState(DriveObjectActivationState)"; result = Need(dfi.DriveObjectFunctions?.DriveObjectActivation, "DriveObjectActivation").ChangeActivationState((DriveObjectActivationState)Enum.Parse(typeof(DriveObjectActivationState), r.ActivationState)); break;
                    case "activateFunction": meta["nativeSignature"] = "FunctionInUse.Activate(FunctionKeys)"; result = Need(dfi.FunctionInUse, "FunctionInUse (SINAMICS FW V6.3+)").Activate((FunctionKeys)Enum.Parse(typeof(FunctionKeys), r.FunctionKey)); break;
                    case "deactivateFunction": meta["nativeSignature"] = "FunctionInUse.Deactivate(FunctionKeys)"; result = Need(dfi.FunctionInUse, "FunctionInUse (SINAMICS FW V6.3+)").Deactivate((FunctionKeys)Enum.Parse(typeof(FunctionKeys), r.FunctionKey)); break;
                    case "setSIAxisType": meta["nativeSignature"] = "FunctionInUse.SetSIAxisType(RotaryLinearFlag)"; result = Need(dfi.FunctionInUse, "FunctionInUse (SINAMICS FW V6.3+)").SetSIAxisType((RotaryLinearFlag)Enum.Parse(typeof(RotaryLinearFlag), r.RotaryLinear)); break;
                    case "setMotorCode": meta["nativeSignature"] = "Commissioning.SetMotorCode(int, int)"; result = Need(dfi.Commissioning, "Commissioning").SetMotorCode(r.MotorCode, r.MotorDataSet); break;
                    case "setSimoGearMlfb": meta["nativeSignature"] = "Commissioning.SetSimoGearMlfb(string)"; Need(dfi.Commissioning, "Commissioning").SetSimoGearMlfb(r.Mlfb); break;
                    case "updateCheckSums": meta["nativeSignature"] = "SafetyCommissioning.UpdateCheckSums()"; result = Need(dfi.SafetyCommissioning, "SafetyCommissioning (SINAMICS FW V6.1+)").UpdateCheckSums(); break;
                    case "setMotorType": meta["nativeSignature"] = "HardwareProjection.SetMotorType(MotorType, ushort)"; result = Projection().SetMotorType((MotorType)Enum.Parse(typeof(MotorType), r.MotorType), (ushort)r.DataSet); break;
                    case "setEquivalentCircuitDiagramData": { var config = Projection().GetCurrentMotorConfiguration((ushort)r.DataSet); meta["nativeSignature"] = "MotorConfiguration.SetEquivalentCircuitDiagramData(bool)"; result = config.SetEquivalentCircuitDiagramData(r.EquivalentCircuitDiagram); meta["after"] = MotorConfigurationRow(config); break; }
                    case "projectMotorConfiguration":
                        {
                            var config = Projection().GetCurrentMotorConfiguration((ushort)r.DataSet);
                            meta["appliedEntries"] = ApplyEntries(r.Entries, config.RequiredConfigurationEntries, config.OptionalConfigurationEntries);
                            meta["nativeSignature"] = "HardwareProjection.ProjectMotorConfiguration(MotorConfiguration, ushort)"; result = Projection().ProjectMotorConfiguration(config, (ushort)r.DataSet);
                            meta["after"] = MotorConfigurationRow(Projection().GetCurrentMotorConfiguration((ushort)r.DataSet)); break;
                        }
                    case "setEncoder": meta["nativeSignature"] = "HardwareProjection.SetEncoder(EncoderInterface, EncoderType, AbsoluteIncrementalFlag, RotaryLinearFlag, ushort)"; result = Projection().SetEncoder((EncoderInterface)Enum.Parse(typeof(EncoderInterface), r.EncoderInterface), (DriveEncoderType)Enum.Parse(typeof(DriveEncoderType), r.EncoderType), (AbsoluteIncrementalFlag)Enum.Parse(typeof(AbsoluteIncrementalFlag), r.AbsoluteIncremental), (RotaryLinearFlag)Enum.Parse(typeof(RotaryLinearFlag), r.RotaryLinear), (ushort)r.EncoderNumber); break;
                    case "setEncoderType":
                        {
                            var config = Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber); var rotary = (RotaryLinearFlag)Enum.Parse(typeof(RotaryLinearFlag), r.RotaryLinear);
                            meta["nativeSignature"] = r.HasAbsoluteIncremental ? "EncoderConfiguration.SetEncoderType(RotaryLinearFlag, AbsoluteIncrementalFlag)" : "EncoderConfiguration.SetEncoderType(RotaryLinearFlag)";
                            result = r.HasAbsoluteIncremental ? config.SetEncoderType(rotary, (AbsoluteIncrementalFlag)Enum.Parse(typeof(AbsoluteIncrementalFlag), r.AbsoluteIncremental)) : config.SetEncoderType(rotary);
                            meta["after"] = EncoderConfigurationRow(config); break;
                        }
                    case "projectEncoderConfiguration":
                        {
                            var config = Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber);
                            meta["appliedEntries"] = ApplyEntries(r.Entries, config.RequiredConfigurationEntries, null);
                            meta["nativeSignature"] = "HardwareProjection.ProjectEncoderConfiguration(EncoderConfiguration, ushort)"; result = Projection().ProjectEncoderConfiguration(config, (ushort)r.EncoderNumber);
                            meta["after"] = EncoderConfigurationRow(Projection().GetCurrentEncoderConfiguration((ushort)r.EncoderNumber)); break;
                        }
                }
                meta["result"] = result; if (!result) meta["operationSuccess"] = false;
                if (!meta.ContainsKey("after")) meta["after"] = FunctionInterfaceRow(dfi);
                return result ? "Drive function " + action + " executed (native bool result true) and the function interface read back; no download or drive command." : "Drive function " + action + " returned false (TIA refused it; see before / after); no download or drive command.";
            });
        // entries {"p305": 20, "307": 30} -> ConfigurationEntry.Value by Name or Number in the required (then optional) composition.
        private static JsonArray ApplyEntries(Dictionary<string, JsonNode?> entries, ConfigurationEntryComposition required, ConfigurationEntryComposition? optional)
        {
            var applied = new JsonArray();
            foreach (var pair in entries)
            {
                ConfigurationEntry? entry = uint.TryParse(pair.Key, out var number) ? required.Find(number) ?? optional?.Find(number) : required.Find(pair.Key) ?? optional?.Find(pair.Key);
                if (entry == null) throw new PortalException(PortalErrorCode.NotFound, "Configuration entry not found in the current configuration: " + pair.Key);
                var current = entry.Value;
                var converted = current == null ? EngineeringScalarProperties.ConvertValue(pair.Value, typeof(object)) : EngineeringScalarProperties.ConvertValue(pair.Value, current.GetType());
                entry.Value = converted;
                applied.Add(new JsonObject { ["name"] = entry.Name, ["number"] = entry.Number, ["value"] = EngineeringScalarProperties.Json(entry.Value) });
            }
            return applied;
        }

        public ResponseMessage ManageDriveSecurity(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string password = "", bool dryRun = true)
            => RunHmiStepTool("ManageDriveSecurity", meta =>
            {
                bool write = Logic.ValidateSecurityRequest(action, password, dryRun);
                using var access = write ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                global::Siemens.Engineering.MC.Drives.Security security = drive.Security ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "Security is not provided by this drive object (SINAMICS FW V6.1+).");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                Safe(meta, "umacConfigurationAvailable", () => security.UmacConfiguration != null); Safe(meta, "driveDataEncryptionAvailable", () => security.DriveDataEncryption != null);
                if (action == "read") return "Drive security services read (UmacConfiguration / DriveDataEncryption availability; the API exposes no state); no modification.";
                if (!write) return "Drive security " + action + " preview; nothing changed (offline only; the password is never logged).";
                meta["mayHaveChanged"] = true; bool result;
                if (action == "activateUmac" || action == "deactivateUmac")
                {
                    UmacConfiguration umac = security.UmacConfiguration ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "UmacConfiguration is not provided by this drive object.");
                    meta["nativeSignature"] = "UmacConfiguration." + (action == "activateUmac" ? "Activate()" : "Deactivate()"); result = action == "activateUmac" ? umac.Activate() : umac.Deactivate();
                }
                else
                {
                    DriveDataEncryption dde = security.DriveDataEncryption ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveDataEncryption is not provided by this drive object.");
                    var secure = new SecureString(); foreach (var c in password) secure.AppendChar(c); secure.MakeReadOnly();
                    meta["nativeSignature"] = "DriveDataEncryption." + (action == "activateEncryption" ? "Activate(SecureString)" : "Deactivate(SecureString)"); result = action == "activateEncryption" ? dde.Activate(secure) : dde.Deactivate(secure);
                }
                meta["result"] = result; if (!result) meta["operationSuccess"] = false;
                return result ? "Drive security " + action + " executed (native result true); no download." : "Drive security " + action + " returned false (TIA refused it); no download.";
            });

        public ResponseMessage ManageTechnologyExtensions(string action = "read", string devicePathJson = "[]", string itemPathJson = "[]", ushort driveObjectNumber = 0, int driveObjectIndex = -1, string identifier = "", string filePath = "", bool confirmUninstallInUse = false, bool includeParameters = false, bool dryRun = true)
            => RunHmiStepTool("ManageTechnologyExtensions", meta =>
            {
                bool write = Logic.ValidateTechnologyExtensionRequest(action, identifier, filePath, confirmUninstallInUse, dryRun);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (Logic.IsPortalScopedExtensionAction(action))
                {
                    var provider = (_portal as IEngineeringServiceProvider)?.GetService<TechnologyExtensionInstallationProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TechnologyExtensionInstallationProvider unavailable on this TIA Portal (Startdrive not installed).");
                    JsonArray Packages() => new JsonArray(EngineeringGroupOperations.Items(provider.TechnologyExtensionPackages).Cast<TechnologyExtensionPackage>().Select(p => (JsonNode)TechnologyExtensionPackageRow(p)).ToArray());
                    meta["before"] = Packages();
                    if (action == "readPackages") return "Installed technology extension packages read (TechnologyExtensionInstallationProvider.TechnologyExtensionPackages); no modification.";
                    if (action == "uninstall") { var package = provider.TechnologyExtensionPackages.Find(identifier) ?? throw new PortalException(PortalErrorCode.NotFound, "Technology extension package not installed: " + identifier); meta["package"] = TechnologyExtensionPackageRow(package); }
                    else { var file = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["file"] = file.FullName; }
                    if (!write) return "Technology extension package " + action + " preview; nothing installed or removed (this changes the TIA Portal installation, not the project).";
                    meta["mayHaveChanged"] = true;
                    switch (action)
                    {
                        case "install": meta["nativeSignature"] = "TechnologyExtensionInstallationProvider.Install(FileInfo)"; meta["result"] = provider.Install(new FileInfo(filePath)); break;
                        case "installAndGetIdentifier": meta["nativeSignature"] = "TechnologyExtensionInstallationProvider.InstallAndGetIdentifier(FileInfo)"; var id = provider.InstallAndGetIdentifier(new FileInfo(filePath)); meta["identifier"] = id; Safe(meta, "package", () => { var p = provider.TechnologyExtensionPackages.Find(id); return p == null ? null : TechnologyExtensionPackageRow(p); }); break;
                        case "uninstall": meta["nativeSignature"] = "TechnologyExtensionInstallationProvider.Uninstall(string, bool)"; meta["result"] = provider.Uninstall(identifier, confirmUninstallInUse); meta["verifiedAbsent"] = provider.TechnologyExtensionPackages.Find(identifier) == null; break;
                    }
                    if (meta["result"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok) meta["operationSuccess"] = false;
                    meta["after"] = Packages();
                    return "Technology extension package " + action + " executed on the TIA Portal installation; packages read back.";
                }
                using var access = write ? AcquireHmiEditAccess() : null;
                var drive = ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                var container = drive.GetService<TechnologyExtensionContainer>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "TechnologyExtensionContainer not provided by this drive object.");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label;
                JsonArray Rows() => new JsonArray(EngineeringGroupOperations.Items(container.TechnologyExtensions).Cast<TechnologyExtension>().Select(e => (JsonNode)TechnologyExtensionRow(e, includeParameters)).ToArray());
                meta["before"] = Rows();
                if (action == "read") return "Technology extensions of the drive object read (identifier / name / display name / activation" + (includeParameters ? " / parameters" : "") + "); no modification.";
                TechnologyExtension extension = container.TechnologyExtensions.Find(identifier) ?? EngineeringGroupOperations.Items(container.TechnologyExtensions).Cast<TechnologyExtension>().FirstOrDefault(e => e.Name == identifier)
                    ?? throw new PortalException(PortalErrorCode.NotFound, "Technology extension not found by identifier or name: " + identifier);
                meta["extension"] = TechnologyExtensionRow(extension, false);
                if (!write) return "Technology extension " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true; meta["nativeSignature"] = "TechnologyExtension." + (action == "activate" ? "Activate()" : "Deactivate()");
                bool result = action == "activate" ? extension.Activate() : extension.Deactivate();
                meta["result"] = result; if (!result) meta["operationSuccess"] = false;
                var fresh = container.TechnologyExtensions.Find(extension.Identifier);
                meta["after"] = fresh == null ? null : TechnologyExtensionRow(fresh, false); meta["activationVerified"] = fresh != null && fresh.IsActivated == (action == "activate");
                return "Technology extension " + action + " executed and read back; no download.";
            });

#if !TIA_V20
        private static JsonObject HardwareModuleRow(DriveItemHardwareModule m)
        {
            var row = new JsonObject();
            Safe(row, "componentName", () => m.ComponentName); Safe(row, "positionNumber", () => m.PositionNumber); Safe(row, "typeName", () => m.TypeName); Safe(row, "typeIdentifier", () => m.TypeIdentifier);
            return row;
        }
#endif
        public ResponseMessage ManageDriveHardwareModule(string devicePathJson, string itemPathJson, string action = "read", string typeIdentifier = "", int positionNumber = -1, bool dryRun = true)
            => RunHmiStepTool("ManageDriveHardwareModule", meta =>
            {
                bool write = Logic.ValidateHardwareModuleRequest(action, typeIdentifier, positionNumber, dryRun);
                using var access = write ? AcquireHmiEditAccess() : null;
                var item = ExactDriveItem(devicePathJson, itemPathJson);
                meta["deviceItem"] = item.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                Safe(meta, "moduleAccessPointHwIdentifiers", () => { var ap = item.GetService<ModuleAccessPoint>(); return ap == null ? null : new JsonArray(EngineeringGroupOperations.Items(ap.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)h.Identifier).ToArray()); });
#if TIA_V20
                meta["hardwareModule"] = null; meta["hardwareModuleNote"] = "DriveItemHardwareModule (component name / position / type identifier / ChangeType) is a V21 addition; absent on V20.";
                if (action == "read") return "Module access point read; DriveItemHardwareModule is not available on V20.";
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveItemHardwareModule." + (action == "changeType" ? "ChangeType" : "PositionNumber") + " is a V21 addition; absent on V20.");
#else
                var module = item.GetService<DriveItemHardwareModule>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveItemHardwareModule not provided by device item '" + item.Name + "' (drive components only).");
                meta["before"] = HardwareModuleRow(module);
                if (action == "read") return "Drive hardware module read (DriveItemHardwareModule component name / position / type name / type identifier, module access point hardware identifiers); no modification.";
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

        public ResponseMessage ManageDriveSafetyAcceptanceTest(string devicePathJson, string itemPathJson, string action = "read", string identifier = "", bool active = false, string filePath = "", string fileOperation = "", bool dryRun = true)
            => RunHmiStepTool("ManageDriveSafetyAcceptanceTest", meta =>
            {
                bool write = Logic.ValidateSafetyTestRequest(action, identifier, filePath, fileOperation, dryRun);
                using var access = write ? AcquireHmiEditAccess() : null;
                var item = ExactDriveItem(devicePathJson, itemPathJson);
                meta["deviceItem"] = item.Name; meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
#if TIA_V20
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyAcceptanceTestProvider / SafetyAcceptanceTestReport are V21 additions; absent on V20.");
#else
                var provider = item.GetService<SafetyAcceptanceTestProvider>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyAcceptanceTestProvider not provided by device item '" + item.Name + "'.");
                JsonArray Rows() => new JsonArray(EngineeringGroupOperations.Items(provider.TestFunctions).Cast<TestFunction>().Select(t => (JsonNode)new JsonObject { ["identifier"] = t.Identifier, ["active"] = t.Active }).ToArray());
                meta["before"] = Rows();
                IEngineeringObject? owner = item.Parent; while (owner is DeviceItem parentItem) owner = parentItem.Parent; var device = owner as Device;
                if (action == "read") { Safe(meta, "reportAvailable", () => device?.GetService<SafetyAcceptanceTestReport>() != null); return "Safety acceptance test functions read (identifier / active); no modification."; }
                if (action == "createProtocol")
                {
                    var report = device?.GetService<SafetyAcceptanceTestReport>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyAcceptanceTestReport not provided by the owning device.");
                    var operation = (FileOperations)Enum.Parse(typeof(FileOperations), fileOperation);
                    var file = operation == FileOperations.Overwrite ? new FileInfo(filePath) : NativeFileOutput.Plan(filePath);
                    meta["file"] = file.FullName; meta["fileOperation"] = fileOperation;
                    if (!write) return "Safety acceptance test protocol preview; no file written.";
                    meta["mayHaveWrittenFiles"] = true; meta["nativeSignature"] = "SafetyAcceptanceTestReport.CreateProtocol(FileInfo, FileOperations)";
                    bool created = report.CreateProtocol(file, operation); meta["result"] = created; if (!created) meta["operationSuccess"] = false;
                    file.Refresh(); if (file.Exists) meta["output"] = NativeFileOutput.Verify(file);
                    return created ? "Safety acceptance test protocol written by TIA (see output); project unchanged." : "SafetyAcceptanceTestReport.CreateProtocol returned false; no file evidence.";
                }
                if (!write) return "Safety acceptance test " + action + " preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (action == "resetTestFunctions") { meta["nativeSignature"] = "SafetyAcceptanceTestProvider.ResetTestFunctions()"; provider.ResetTestFunctions(); }
                else
                {
                    var function = provider.TestFunctions.Find(identifier) ?? throw new PortalException(PortalErrorCode.NotFound, "Test function not found: " + identifier);
                    meta["nativeSignature"] = "TestFunction.Active = " + (active ? "true" : "false"); function.Active = active;
                    meta["activeVerified"] = (provider.TestFunctions.Find(identifier)?.Active ?? !active) == active;
                }
                meta["after"] = Rows();
                return "Safety acceptance test " + action + " executed and functions read back; no download.";
#endif
            });

        // ---- online (connected drive) --------------------------------------------------------------------------------------------
        public ResponseMessage ReadOnlineDriveParameters(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string namesJson = "[]", string numbersJson = "[]", bool includeBits = false, bool includeEnumValues = false, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadOnlineDriveParameters", meta =>
            {
                var selector = Logic.ValidateParametersRequest("read", namesJson, numbersJson, offset, limit);
                var item = ExactDriveItem(devicePathJson, itemPathJson);
                var drive = ExactOnlineDriveObject(item, Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["online"] = true;
                Safe(meta, "driveObjectNumber", () => drive.DriveObjectNumber);
                ReadDriveParameterComposition parameters = drive.ReadParameters; var missing = new JsonArray(); JsonNode[] rows;
                if (selector.Enumerate) { var all = EngineeringGroupOperations.Items(parameters).Cast<ReadDriveParameter>().Skip(offset).Take(limit + 1).ToArray(); rows = all.Take(limit).Select(p => (JsonNode)DriveParameterRow(p, includeBits, includeEnumValues)).ToArray(); meta["truncated"] = all.Length > limit; meta["nextOffset"] = all.Length > limit ? offset + limit : (int?)null; }
                else rows = selector.Names.Select(n => (Name: n, Parameter: parameters.Find(n))).Concat(selector.Numbers.Select(x => (Name: x.Number + (x.ArrayIndex >= 0 ? "[" + x.ArrayIndex + "]" : ""), Parameter: parameters.Find(x.Number, x.ArrayIndex))))
                    .Where(x => { if (x.Parameter == null) missing.Add(x.Name); return x.Parameter != null; }).Select(x => (JsonNode)DriveParameterRow(x.Parameter!, includeBits, includeEnumValues)).ToArray();
                meta["records"] = new JsonArray(rows); meta["actualCount"] = rows.Length; meta["offset"] = offset; meta["limit"] = limit; meta["notFound"] = missing;
                return "Online drive parameters read from the connected drive (OnlineDriveObject.ReadParameters); nothing written.";
            });

        public ResponseMessage ManageOnlineDriveFunctions(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string resetMode = "", string activationState = "", bool confirmOnline = false)
            => RunHmiStepTool("ManageOnlineDriveFunctions", meta =>
            {
                Logic.ValidateOnlineFunctionRequest(action, resetMode, activationState, confirmOnline);
                var item = ExactDriveItem(devicePathJson, itemPathJson);
                var drive = ExactOnlineDriveObject(item, Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
                var dfi = drive.GetService<OnlineDriveFunctionInterface>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "OnlineDriveFunctionInterface not provided (drive offline or unsupported).");
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["action"] = action; meta["online"] = true; meta["mayHaveChanged"] = false;
                JsonObject Row()
                {
                    var row = new JsonObject();
                    Safe(row, "driveDomainFunctionsAvailable", () => dfi.DriveDomainFunctions != null); Safe(row, "hardwareProjectionAvailable", () => dfi.HardwareProjection != null); Safe(row, "functionInUseAvailable", () => dfi.FunctionInUse != null);
                    Safe(row, "activation", () => { var a = dfi.DriveObjectFunctions?.DriveObjectActivation; return a == null ? null : ActivationRow(a); });
                    Safe(row, "securityAvailable", () => drive.Security != null);
                    return row;
                }
                meta["before"] = Row();
                if (action == "read") return "Online drive function interface read (domain functions / hardware projection / function in use availability, activation); nothing written.";
                meta["mayHaveChanged"] = true; bool result;
                switch (action)
                {
                    case "performFactoryReset": { var functions = dfi.DriveDomainFunctions ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveDomainFunctions not provided (G120: Power Module required)."); meta["nativeSignature"] = "DriveDomainFunctions.PerformFactoryReset(ResetMode)"; result = functions.PerformFactoryReset((ResetMode)Enum.Parse(typeof(ResetMode), resetMode)); break; }
                    case "performRamToRomCopy": { var functions = dfi.DriveDomainFunctions ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveDomainFunctions not provided (G120: Power Module required)."); meta["nativeSignature"] = "DriveDomainFunctions.PerformRAMtoROMCopyAllDriveObject()"; result = functions.PerformRAMtoROMCopyAllDriveObject(); break; }
                    default: { var activation = dfi.DriveObjectFunctions?.DriveObjectActivation ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveObjectActivation not provided online."); meta["nativeSignature"] = "DriveObjectActivation.ChangeActivationState(DriveObjectActivationState)"; result = activation.ChangeActivationState((DriveObjectActivationState)Enum.Parse(typeof(DriveObjectActivationState), activationState)); break; }
                }
                meta["result"] = result; if (!result) meta["operationSuccess"] = false; meta["after"] = Row();
                return result ? "Online drive function " + action + " executed on the connected drive (native result true)." : "Online drive function " + action + " returned false (the drive refused it).";
            });
    }
}
