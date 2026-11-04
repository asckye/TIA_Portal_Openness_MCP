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
        private readonly OptionalSession _session;

        public StartdriveAdapter(OptionalSession session) => _session = session;

        // ---- resolution ----------------------------------------------------------------------------------------------------------
        // TIA V21, 2026-09-19 (docs/reference/real-machine-ledger.md), real project (G120C): Find("r722.0") answers null - bit parameters are only reachable through the parent's Bits, whose
        // members carry the dotted name. Find(name) first, then base name + Bits when the name has a bit suffix.
        private static DriveParameter? FindParameter(DriveParameterComposition parameters, string name)
        {
            var direct = parameters.Find(name); if (direct != null) return direct;
            int dot = name.LastIndexOf('.'); if (dot <= 0) return null;
            var parent = parameters.Find(name.Substring(0, dot)); if (parent == null) return null;
            return EngineeringGroupOperations.Items(parent.Bits).Cast<DriveParameter>()
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        private static ReadDriveParameter? FindParameter(ReadDriveParameterComposition parameters, string name)
        {
            var direct = parameters.Find(name); if (direct != null) return direct;
            int dot = name.LastIndexOf('.'); if (dot <= 0) return null;
            var parent = parameters.Find(name.Substring(0, dot)); if (parent == null) return null;
            return EngineeringGroupOperations.Items(parent.Bits).Cast<ReadDriveParameter>()
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        private static OnlineDriveObject ExactOnlineDriveObject(DeviceItem item, Logic.DriveSelector selector)
        {
            var container = item.GetService<OnlineDriveObjectContainer>()
                ?? throw new PlcSoftwareException("NotSupportedOnVersion", "OnlineDriveObjectContainer unavailable on device item '" + item.Name
                    + "' (drive not online, or not a Startdrive drive object host).");
            OnlineDriveObjectComposition objects = container.OnlineDriveObjects;
            if (selector.ByNumber)
            {
                var matches = objects.Where(o => { try { return o.DriveObjectNumber == selector.Number; } catch { /* swallow(enumerate-optional): a drive without a readable number cannot match the requested number; index selection remains available. */ return false; } }).Take(2).ToArray();
                if (matches.Length != 1)
                    throw new PlcSoftwareException("NotFound", "Exactly one online drive object with " + selector.Label + " expected, found " + matches.Length
                        + ".");
                return matches[0];
            }
            if (selector.Index >= objects.Count)
                throw new PlcSoftwareException("NotFound", selector.Label + " out of range: " + objects.Count + " online drive object(s).");
            return objects[selector.Index];
        }

        // ---- rows ----------------------------------------------------------------------------------------------------------------
        private static HmiObject BicoOrScalar(object? value) => value switch
        {
            DriveParameter source => new HmiObject { ["bicoSource"] = source.Name, ["parameterText"] = source.ParameterText },
            ReadDriveParameter source => new HmiObject { ["bicoSource"] = source.Name, ["parameterText"] = source.ParameterText },
            _ => new HmiObject { ["value"] = EngineeringScalarProperties.Json(value) }
        };
        private static HmiObject EnumValues(IDictionary<int, string>? list)
        {
            var o = new HmiObject(); if (list == null) return o;
            foreach (var pair in list.Take(500)) o[pair.Key.ToString()] = pair.Value; return o;
        }
        // Value is read last and only on request (includeValue): TIA V21, 2026-09-19
        // (docs/reference/real-machine-ledger.md): TIA went down while reading the Value of
        // r2139 (existing G120C) and of the unwired BICO sink p840[0] (freshly added G120C) - the metadata reads before it are safe.
        private static HmiObject DriveParameterRow(DriveParameter p, bool bits, bool enums, bool value = true)
        {
            var row = new HmiObject { ["name"] = p.Name, ["parameterClass"] = "DriveParameter" };
            Safe(row, "number", () => p.Number); Safe(row, "arrayIndex", () => p.ArrayIndex); Safe(row, "arrayLength", () => p.ArrayLength);
            Safe(row, "minValue", () => EngineeringScalarProperties.Json(p.MinValue));
            Safe(row, "maxValue", () => EngineeringScalarProperties.Json(p.MaxValue));
            Safe(row, "unit", () => p.Unit); Safe(row, "parameterText", () => p.ParameterText);
            if (enums) Safe(row, "enumValueList", () => EnumValues(p.EnumValueList));
            if (bits) Safe(row, "bits", () => new HmiArray(p.Bits.Take(64).Select(b => (object)DriveParameterRow(b, false, enums, value)).ToArray()));
            if (value) Safe(row, "value", () => BicoOrScalar(p.Value)); else row["value"] = null;
            return row;
        }
        private static HmiObject DriveParameterRow(ReadDriveParameter p, bool bits, bool enums, bool value = true)
        {
            var row = new HmiObject { ["name"] = p.Name, ["parameterClass"] = "ReadDriveParameter" };
            Safe(row, "number", () => p.Number); Safe(row, "arrayIndex", () => p.ArrayIndex); Safe(row, "arrayLength", () => p.ArrayLength);
            Safe(row, "minValue", () => EngineeringScalarProperties.Json(p.MinValue));
            Safe(row, "maxValue", () => EngineeringScalarProperties.Json(p.MaxValue));
            Safe(row, "unit", () => p.Unit); Safe(row, "parameterText", () => p.ParameterText);
            if (enums) Safe(row, "enumValueList", () => EnumValues(p.EnumValueList));
            if (bits) Safe(row, "bits", () => new HmiArray(p.Bits.Take(64).Select(b => (object)DriveParameterRow(b, false, enums, value)).ToArray()));
            if (value) Safe(row, "value", () => BicoOrScalar(p.Value)); else row["value"] = null;
            return row;
        }
        private HmiObject TelegramRow(Telegram t, bool nested = false)
        {
            var row = new HmiObject();
            Safe(row, "type", () => t.Type.ToString()); Safe(row, "telegramNumber", () => t.TelegramNumber);
            Safe(row, "inputWords", () => t.GetSize(AddressIoType.Input)); Safe(row, "outputWords", () => t.GetSize(AddressIoType.Output));
            Safe(row, "inputBytes", () => t.GetSizeInBytes(AddressIoType.Input)); Safe(row, "outputBytes", () => t.GetSizeInBytes(AddressIoType.Output));
            Safe(row, "addresses", () =>
                    new HmiArray(EngineeringGroupOperations.Items(t.Addresses).Cast<Address>().Select(a => (object)_session.AddressRow(a)).ToArray()));
            Safe(row, "hwIdentifiers", () =>
                    new HmiArray(EngineeringGroupOperations.Items(t.HwIdentifiers).Cast<HwIdentifier>().Select(h => (object)h.Identifier).ToArray()));
            if (!nested) Safe(row, "pkw", () => t.PKW == null ? null : TelegramRow(t.PKW, true));
            return row;
        }
        private static HmiObject DriveObjectTypeRow(DriveObjectType t) => new HmiObject { ["name"] = t.Name, ["number"] = t.Number };
        private static HmiObject ActivationRow(DriveObjectActivation a) => new HmiObject {
            ["activationState"] = a.ActivationState.ToString(), ["isActive"] = a.IsActive };
        private static HmiObject FunctionInterfaceRow(DriveFunctionInterface dfi)
        {
            var row = new HmiObject();
            Safe(row, "driveObjectType", () => {
                    var h = dfi.DriveObjectFunctions?.DriveObjectTypeHandler; return h == null ? null : new HmiObject {
                        ["current"] = h.CurrentDriveObjectType == null ? null : DriveObjectTypeRow(h.CurrentDriveObjectType), ["possible"] =
                            new HmiArray(EngineeringGroupOperations.Items(h.PossibleDriveObjectTypes).Cast<DriveObjectType>()
                                .Select(t => (object)DriveObjectTypeRow(t)).ToArray())
                    };
                });
            Safe(row, "activation", () => { var a = dfi.DriveObjectFunctions?.DriveObjectActivation; return a == null ? null : ActivationRow(a); });
            Safe(row, "functionInUseAvailable", () => dfi.FunctionInUse != null); Safe(row, "commissioningAvailable", () => dfi.Commissioning != null);
            Safe(row, "safetyCommissioningAvailable", () => dfi.SafetyCommissioning != null);
            Safe(row, "hardwareProjectionAvailable", () => dfi.HardwareProjection != null);
            return row;
        }
        private static HmiObject ConfigurationEntryRow(ConfigurationEntry e)
        {
            var row = new HmiObject { ["name"] = e.Name };
            Safe(row, "number", () => e.Number); Safe(row, "value", () => EngineeringScalarProperties.Json(e.Value));
            Safe(row, "minValue", () => EngineeringScalarProperties.Json(e.MinValue));
            Safe(row, "maxValue", () => EngineeringScalarProperties.Json(e.MaxValue)); Safe(row, "unit", () => e.Unit);
            Safe(row, "description", () => e.Description); Safe(row, "enumValueList", () => EnumValues(e.EnumValueList));
            return row;
        }
        private static HmiArray EntryRows(ConfigurationEntryComposition entries) =>
            new HmiArray(EngineeringGroupOperations.Items(entries).Cast<ConfigurationEntry>().Take(500).Select(e => (object)ConfigurationEntryRow(e))
                .ToArray());
        private static HmiObject MotorConfigurationRow(MotorConfiguration c)
        {
            var row = new HmiObject();
            Safe(row, "requiredEntries", () => EntryRows(c.RequiredConfigurationEntries));
            Safe(row, "optionalEntries", () => EntryRows(c.OptionalConfigurationEntries));
            Safe(row, "canSetEquivalentCircuitDiagramData", () => c.CanSetEquivalentCircuitDiagramData());
            return row;
        }
        private static HmiObject EncoderConfigurationRow(EncoderConfiguration c)
        {
            var row = new HmiObject();
            Safe(row, "requiredEntries", () => EntryRows(c.RequiredConfigurationEntries)); Safe(row, "encoderTypes", () => EntryRows(c.EncoderTypes));
            return row;
        }
        private static HmiObject TechnologyExtensionRow(TechnologyExtension e, bool parameters)
        {
            var row = new HmiObject { ["identifier"] = e.Identifier };
            Safe(row, "name", () => e.Name); Safe(row, "displayName", () => e.DisplayName); Safe(row, "isActivated", () => e.IsActivated);
            if (parameters)
                Safe(row, "parameters", () =>
                    new HmiArray(EngineeringGroupOperations.Items(e.Parameters).Cast<DriveParameter>().Take(200)
                        .Select(p => (object)DriveParameterRow(p, false, false)).ToArray()));
            else Safe(row, "parameterCount", () => e.Parameters.Count);
            return row;
        }
        private static HmiObject TechnologyExtensionPackageRow(TechnologyExtensionPackage p) => new HmiObject {
            ["identifier"] = p.Identifier, ["name"] = p.Name, ["displayName"] = p.DisplayName };
        private HmiObject DriveObjectRow(DriveObject d, int index, bool telegrams, bool functions, bool extensions, bool dcc)
        {
            var row = new HmiObject { ["index"] = index };
            Safe(row, "driveObjectNumber", () => d.DriveObjectNumber);
            Safe(row, "telegramCount", () => d.Telegrams.Count);
            if (telegrams)
                Safe(row, "telegrams", () =>
                    new HmiArray(EngineeringGroupOperations.Items(d.Telegrams).Cast<Telegram>().Select(t => (object)TelegramRow(t)).ToArray()));
            Safe(row, "securityAvailable", () => d.Security != null);
            if (functions)
                Safe(row, "functionInterface", () => { var dfi = d.GetService<DriveFunctionInterface>(); return dfi == null ? null : FunctionInterfaceRow(dfi);
                });
            if (extensions)
                Safe(row, "technologyExtensions", () => {
                    var c = d.GetService<TechnologyExtensionContainer>(); return c == null ? null
                        : new HmiArray(EngineeringGroupOperations.Items(c.TechnologyExtensions).Cast<TechnologyExtension>()
                            .Select(e => (object)TechnologyExtensionRow(e, false)).ToArray());
                });
            if (dcc) Safe(row, "dcc", () => OptionalSession.DccContainerSummary(d));
            return row;
        }

        // ---- tools ---------------------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ReadDriveObjects(string devicePathJson, string itemPathJson, bool includeTelegrams = true, bool includeFunctions = true,
                bool includeTechnologyExtensions = true, bool includeDcc = true)
            => _session.RunHmiStepTool("ListDriveObjects", meta =>
            {
                var item = _session.ExactDriveItem(devicePathJson, itemPathJson);
                var container = _session.ExactDriveContainer(item);
                meta["deviceItem"] = item.Name; meta["hostClass"] = item.GetType().Name;
                Safe(meta, "moduleAccessPointHwIdentifiers", () => {
                        var ap = item.GetService<ModuleAccessPoint>(); return ap == null ? null
                            : new HmiArray(EngineeringGroupOperations.Items(ap.HwIdentifiers).Cast<HwIdentifier>().Select(h => (object)h.Identifier).ToArray());
                    });
#if OPTIONAL_V21
                Safe(meta, "hardwareModule", () => { var m = item.GetService<DriveItemHardwareModule>(); return m == null ? null : HardwareModuleRow(m); });
#endif
                Safe(meta, "onlineContainerAvailable", () => item.GetService<OnlineDriveObjectContainer>() != null);
                var rows = EngineeringGroupOperations.Items(container.DriveObjects).Cast<DriveObject>()
                    .Select((d, i) => (object)DriveObjectRow(d, i, includeTelegrams, includeFunctions, includeTechnologyExtensions, includeDcc)).ToArray();
                meta["records"] = new HmiArray(rows); meta["expectedCount"] = rows.Length; meta["actualCount"] = rows.Length;
                return "Drive objects of the device item read (DriveObjectContainer.DriveObjects with telegrams, function interface, technology extensions " +
                    "and DCC summary); no modification.";
            });

        public HardwareAddressingReply ReadDriveParameters(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1,
                string source = "read", string namesJson = "[]", string numbersJson = "[]",
            bool includeBits = false, bool includeEnumValues = false, int offset = 0, int limit = 100, bool includeValue = true)
            => _session.RunHmiStepTool("GetDriveParameters", meta =>
            {
                var selector = Logic.ValidateParametersRequest(source, namesJson, numbersJson, offset, limit);
                var drive = _session.ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["source"] = source == "read" ? "ReadParameters"
                    : "Parameters";
                var missing = new HmiArray();
                object[] rows;
                if (source == "read")
                {
                    ReadDriveParameterComposition parameters = drive.ReadParameters;
                    if (selector.Enumerate) {
                        var all = EngineeringGroupOperations.Items(parameters).Cast<ReadDriveParameter>().Skip(offset).Take(limit + 1).ToArray(); rows = all
                            .Take(limit).Select(p => (object)DriveParameterRow(p, includeBits, includeEnumValues, includeValue)).ToArray(); meta["truncated"] =
                            all.Length > limit; meta["nextOffset"] = all.Length > limit ? offset + limit : (int?)null;
                    }
                    else rows = selector.Names.Select(n => (Name: n, Parameter: FindParameter(parameters, n)))
                        .Concat(selector.Numbers
                            .Select(x =>
                                (Name: x.Number + (x.ArrayIndex >= 0 ? "[" + x.ArrayIndex + "]" : ""), Parameter: parameters.Find(x.Number, x.ArrayIndex))))
                        .Where(x => { if (x.Parameter == null) missing.Add(x.Name); return x.Parameter != null; })
                            .Select(x => (object)DriveParameterRow(x.Parameter!, includeBits, includeEnumValues, includeValue)).ToArray();
                }
                else
                {
                    DriveParameterComposition parameters = drive.Parameters;
                    if (selector.Enumerate) {
                        var all = EngineeringGroupOperations.Items(parameters).Cast<DriveParameter>().Skip(offset).Take(limit + 1).ToArray(); rows = all
                            .Take(limit).Select(p => (object)DriveParameterRow(p, includeBits, includeEnumValues, includeValue)).ToArray(); meta["truncated"] =
                            all.Length > limit; meta["nextOffset"] = all.Length > limit ? offset + limit : (int?)null;
                    }
                    else rows = selector.Names.Select(n => (Name: n, Parameter: FindParameter(parameters, n)))
                        .Concat(selector.Numbers
                            .Select(x =>
                                (Name: x.Number + (x.ArrayIndex >= 0 ? "[" + x.ArrayIndex + "]" : ""), Parameter: parameters.Find(x.Number, x.ArrayIndex))))
                        .Where(x => { if (x.Parameter == null) missing.Add(x.Name); return x.Parameter != null; })
                            .Select(x => (object)DriveParameterRow(x.Parameter!, includeBits, includeEnumValues, includeValue)).ToArray();
                }
                meta["records"] = new HmiArray(rows); meta["actualCount"] = rows.Length; meta["offset"] = offset; meta["limit"] = limit; meta["notFound"] =
                    missing; meta["includeValue"] = includeValue;
                if (!includeValue) meta["scope"] =
                    "Metadata only (Value not read): use this first for BICO sinks / status words on a freshly added drive - 2.7.39 real project: reading " +
                    "Value of r2139 and of the unwired p840[0] took TIA Portal V21 down.";
                if (selector.Enumerate) meta["scope"] =
                    "Enumeration of the composition in native order; the drive has thousands of parameters, so page with offset / limit or ask by namesJson " +
                    "/ numbersJson.";
                return "Offline drive parameters read ("
                    + (source == "read" ? "ReadDriveParameter rows incl. BICO sources" : "DriveParameter rows incl. BICO sources")
                    + "); no OnlineDriveObject or drive access.";
            });

        // Parameters.Find(name) resolves scalar or BICO-source writes; driveObjectIndex selects by composition position.
        public HardwareAddressingReply ManageStartdriveParameter(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string parameter,
                string action = "read", string valueJson = "null", bool dryRun = true, int driveObjectIndex = -1)
            => _session.RunHmiStepTool("ManageStartdriveParameter", meta =>
            {
                Logic.ValidateParameterWriteRequest(parameter, action);
                bool write = action == "write" && !dryRun; using var access = write ? _session.AcquireHmiEditAccess() : null;
                var drive = _session.ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
                meta["driveObject"] = Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex).Label; meta["dryRun"] = dryRun; meta["mayHaveChanged"] =
                    false; meta["online"] = false;
                if (action == "read")
                {
                    // Official BICO example: Parameters.Find(name).Value. Do not expand Bits,
                    // limits or enum tables when asked for one value (TIA V21 p2051[0] crash report;
                    // docs/reference/real-machine-ledger.md, recorded by 2026-10-03; native retest pending).
                    meta["before"] = Logic.ReadExactParameter<DriveParameter, ReadDriveParameter>(parameter,
                        name => FindParameter(drive.Parameters, name), name => FindParameter(drive.ReadParameters, name),
                        target => BicoOrScalar(target.Value), target => BicoOrScalar(target.Value))
                        ?? throw new PlcSoftwareException("NotFound", "Exact drive parameter not found: " + parameter);
                    return "Exact offline parameter value read, including BICO source when available; no bit expansion, limits or enum table reads. No " +
                        "OnlineDriveObject or live device access used.";
                }
                DriveParameter writable = FindParameter(drive.Parameters, parameter)
                    ?? throw new PlcSoftwareException("NotFound", "Exact writable drive parameter not found: " + parameter
                        + " (read-only parameters live in ReadParameters).");
                meta["before"] = DriveParameterRow(writable, false, true);
                var value = Logic.ParseParameterValue(valueJson);
                object? converted = null; DriveParameter? source = null;
                if (value.IsBico) {
                    source = FindParameter(drive.Parameters, value.BicoSource)
                        ?? throw new PlcSoftwareException("NotFound", "BICO source parameter not found in the writable Parameters view: " + value.BicoSource
                            + " (bit sources as r722.0 resolve through the parent's Bits; r19 does not exist on G120C).");
                    meta["bicoSource"] = DriveParameterRow(source, false, false);
                }
                else {
                    var current = writable.Value; converted = current == null || current is DriveParameter ? EngineeringScalarProperties
                        .ConvertValue(value.Scalar, typeof(object)) : EngineeringScalarProperties.ConvertValue(value.Scalar, current.GetType());
                    meta["requestedValue"] = EngineeringScalarProperties.Json(converted);
                }
                if (!write) return "Offline drive parameter write preview; native limits / BICO semantics are checked on execution.";
                meta["mayHaveChanged"] = true;
                writable.Value = value.IsBico ? source : converted;      // official: "P2080Bit6.Value = cu.Parameters.Find("r19")" wires a BICO sink
                var after = FindParameter(drive.Parameters, parameter) ?? writable;
                meta["after"] = DriveParameterRow(after, false, true);
                return "Offline drive parameter changed and read back; no download, online parameter write or drive command.";
            });

#if OPTIONAL_V21
        private static HmiObject HardwareModuleRow(DriveItemHardwareModule m)
        {
            var row = new HmiObject();
            Safe(row, "componentName", () => m.ComponentName); Safe(row, "positionNumber", () => m.PositionNumber); Safe(row, "typeName", () => m.TypeName);
            Safe(row, "typeIdentifier", () => m.TypeIdentifier);
            return row;
        }
#endif
    }
}

#endif
