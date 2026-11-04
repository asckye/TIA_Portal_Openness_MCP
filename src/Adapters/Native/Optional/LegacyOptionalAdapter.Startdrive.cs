using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Safety;
using Group = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;

namespace TiaMcp.Adapters.Native.Optional;

internal sealed partial class LegacyOptionalAdapter
{
    private string DriveObjects(HmiObject meta)
    {
        var item = DriveItem();
        var objects = Drives(item);
        meta["deviceItem"] = item.Name;
        meta["records"] = new HmiArray(objects.Select((drive, index) => {
            var row = new HmiObject { ["index"] = index, ["driveObjectNumber"] = null };
            OptionalEvidence.Safe(row, "telegramCount", () => Items(Get(drive, "Telegrams")).Length);
            OptionalEvidence.Safe(row, "securityAvailable", () => Optional(drive, "Security") != null);
            if (_request.IncludeTelegrams) OptionalEvidence
                .Safe(row, "telegrams", () => new HmiArray(Items(Get(drive, "Telegrams")).Select(v => (object?)Row(v)).ToArray()));
            if (_request.IncludeFunctions && _release != "14sp1") OptionalEvidence
                .Safe(row, "functionInterface", () => FunctionRow(Service(drive, "MC.Drives.DFI.DriveFunctionInterface")));
            if (_request.IncludeTechnologyExtensions && _release is "18" or "19") OptionalEvidence
                .Safe(row, "technologyExtensions", () =>
                    new HmiArray(Items(Get(Service(drive, "MC.Drives.TechnologyExtensionContainer"), "TechnologyExtensions")).Select(v => (object?)Row(v))
                        .ToArray()));
            if (_request.IncludeDcc && _release is not ("14sp1" or "15.1")) OptionalEvidence
                .Safe(row, "dcc", () => DccSummary(Service(drive, "MC.Drives.Dcc.DriveControlChartContainer")));
            return (object?)row;
        }).ToArray());
        meta["expectedCount"] = objects.Length;
        meta["actualCount"] = objects.Length;
        meta["scope"] = "Available offline drive fields; select objects by composition index on this release.";
        meta["unavailableIncludes"] = new HmiArray(new[] {
            _request.IncludeFunctions && _release == "14sp1" ? "functionInterface requires V15.1" : null,
            _request.IncludeTechnologyExtensions && _release is not ("18" or "19") ? "technologyExtensions requires V18" : null,
            _request.IncludeDcc && _release is "14sp1" or "15.1" ? "dcc requires V16" : null
        }.Where(note => note != null).Cast<object>().ToArray());
        meta["dataComplete"] = false;
        return "Startdrive objects read; no online drive access.";
    }

    private static object? Parameter(object composition, string name)
    {
        var direct = Call(composition, "Find", name);
        if (direct != null) return direct;
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        var parent = Call(composition, "Find", name.Substring(0, dot));
        return parent == null ? null : Items(Get(parent, "Bits")).FirstOrDefault(v => string.Equals(Name(v), name, StringComparison.OrdinalIgnoreCase));
    }

    private string Parameters(HmiObject meta)
    {
        if (_request.Operation is "GetDriveParameters" or "GetOnlineDriveParameters")
        {
            bool online = _request.Operation == "GetOnlineDriveParameters";
            var selection = StartdrivePolicy
                .ValidateParametersRequest(online ? "write" : _request.Source, _request.NamesJson, _request.NumbersJson, _request.Offset, _request.Limit);
            foreach (var number in selection.Numbers) OptionalNativeOperations.ParameterNumber(_release, number.Number);
            object parameterOwner;
            if (online)
            {
                var objects = Items(Get(Service(DriveItem(), "MC.Drives.OnlineDriveObjectContainer"), "OnlineDriveObjects"));
                var selector = StartdrivePolicy.ParseDriveSelector(_request.DriveObjectNumber, _request.DriveObjectIndex);
                if (selector.Index >= objects.Length) throw new PlcSoftwareException("NotFound", "online driveObjectIndex is out of range.");
                parameterOwner = objects[selector.Index];
                meta["online"] = true;
                meta["driveObject"] = selector.Label;
            }
            else parameterOwner = Drive();
            var composition = Get(parameterOwner, "Parameters");
            var missing = new HmiArray();
            object[] parameters;
            if (selection.Enumerate)
            {
                var page = Items(composition).Skip(_request.Offset).Take(_request.Limit + 1).ToArray();
                parameters = page.Take(_request.Limit).ToArray();
                meta["truncated"] = page.Length > _request.Limit;
                meta["nextOffset"] = page.Length > _request.Limit ? _request.Offset + _request.Limit : (int?)null;
            }
            else parameters = selection.Names.Select(n => (Name: n, Parameter: Parameter(composition, n)))
                .Concat(selection.Numbers
                        .Select(n =>
                            (Name: n.Number + (n.ArrayIndex >= 0 ? "[" + n.ArrayIndex + "]" : ""), Parameter
                                : Call(composition, "Find", OptionalNativeOperations.ParameterNumber(_release, n.Number), n.ArrayIndex))))
                .Where(p => { if (p.Parameter == null) missing.Add(p.Name); return p.Parameter != null; }).Select(p => p.Parameter!).ToArray();
            meta["records"] =
                new HmiArray(parameters.Select(p => (object?)ParameterRow(p, _request.IncludeValue, _request.IncludeBits, _request.IncludeEnumValues))
                    .ToArray());
            meta["actualCount"] = parameters.Length;
            meta["source"] = "Parameters";
            meta["offset"] = _request.Offset;
            meta["limit"] = _request.Limit;
            meta["notFound"] = missing;
            meta["includeValue"] = _request.IncludeValue;
            return online ? "Online drive parameters read through OnlineDriveObject.Parameters; nothing written."
                : "Offline writable-view drive parameters read; no online device access.";
        }
        StartdrivePolicy.ValidateParameterWriteRequest(_request.Parameter, _request.Action);
        bool writing = _request.Action == "write" && !_request.DryRun;
        using var access = Edit(writing);
        var drive = Drive();
        var parametersView = Get(drive, "Parameters");
        if (_request.Action == "read")
        {
            meta["before"] = StartdrivePolicy.ReadExactParameter<object, object>(_request.Parameter, n => Parameter(parametersView, n), n => null,
                p => BicoOrScalar(Get(p, "Value")), p => null)
                ?? throw new PlcSoftwareException("NotFound", "Exact drive parameter not found: " + _request.Parameter);
            return "Exact offline parameter value read; no bit expansion or online device access.";
        }
        var target = Parameter(parametersView, _request.Parameter)
            ?? throw new PlcSoftwareException("NotFound", "Exact writable drive parameter not found: " + _request.Parameter);
        meta["before"] = ParameterRow(target, true);
        var requested = StartdrivePolicy.ParseParameterValue(_request.ValueJson);
        var current = Get(target, "Value");
        object? value = requested.IsBico ? Parameter(parametersView, requested.BicoSource)
            ?? throw new PlcSoftwareException("NotFound", "BICO source not found.")
            : HmiScalar.ConvertValue(requested.Scalar, current?.GetType() ?? typeof(object));
        if (!writing) return "Offline parameter write preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        target.GetType().GetProperty("Value")!.SetValue(target, value);
        meta["after"] = ParameterRow(Parameter(parametersView, _request.Parameter) ?? target, true);
        return "Offline parameter changed and read back; no download or online device command.";
    }

    private static HmiObject BicoOrScalar(object? value)
    {
        if (value != null && value.GetType().FullName is "Siemens.Engineering.MC.Drives.DriveParameter" or "Siemens.Engineering.Startdrive.DriveParameter")
            return new HmiObject { ["bicoSource"] = Name(value), ["parameterText"] = Get(value, "ParameterText") };
        if (value != null && !HmiScalar.Scalar(value.GetType()))
            throw new NotSupportedException("Complex drive parameter values cannot leave the worker lane.");
        return new HmiObject { ["value"] = HmiScalar.Json(value) };
    }

    private static HmiObject ParameterRow(object parameter, bool value, bool bits = false, bool enums = false)
    {
        var row = new HmiObject { ["name"] = Name(parameter), ["parameterClass"] = parameter.GetType().Name };
        OptionalEvidence.Safe(row, "number", () => HmiScalar.Json(Optional(parameter, "Number") ?? Get(parameter, "ParamNo")));
        foreach (var field in new[] { "ArrayIndex", "ArrayLength", "MinValue", "MaxValue", "Unit", "ParameterText", "TypeName" })
            OptionalEvidence.Safe(row, char.ToLowerInvariant(field[0]) + field.Substring(1), () => HmiScalar.Json(Get(parameter, field)));
        if (enums) OptionalEvidence.Safe(row, "enumValueList", () => {
            var values = new HmiObject();
            if (Get(parameter, "EnumValueList") is IDictionary entries)
                foreach (DictionaryEntry pair in entries)
                {
                    if (values.Count == 500) break;
                    values[Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!] = pair.Value;
                }
            return values;
        });
        if (bits) OptionalEvidence
            .Safe(row, "bits", () => new HmiArray(Items(Get(parameter, "Bits")).Take(64).Select(p => (object?)ParameterRow(p, value, false, enums)).ToArray()));
        if (value) OptionalEvidence.Safe(row, "value", () => BicoOrScalar(Get(parameter, "Value")));
        else row["value"] = null;
        return row;
    }

    private string HardwareModule(HmiObject meta)
    {
        StartdrivePolicy.ValidateHardwareModuleRequest(_request.Action, _request.TypeIdentifier, _request.PositionNumber, _request.DryRun);
        var item = DriveItem();
        var point = Service(item, "MC.Drives.ModuleAccessPoint");
        meta["deviceItem"] = item.Name;
        meta["moduleAccessPointHwIdentifiers"] = new HmiArray(Items(Get(point, "HwIdentifiers")).Select(v => Optional(v, "Identifier")).ToArray());
        meta["hardwareModule"] = null;
        meta["hardwareModuleNote"] = "DriveItemHardwareModule requires V21.";
        return "Module access point read; no drive component mutation.";
    }

    private string Security(HmiObject meta)
    {
        bool writing = StartdrivePolicy.ValidateSecurityRequest(_request.Action, _request.Password, _request.DryRun);
        using var access = Edit(writing);
        var security = Get(Drive(), "Security");
        meta["before"] = Row(security);
        if (_request.Action == "read") return "Drive security read; no change.";
        if (!writing) return "Drive security preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        object? result;
        if (_request.Action is "activateUmac" or "deactivateUmac") result =
            Call(Get(security, "UmacConfiguration"), _request.Action == "activateUmac" ? "Activate" : "Deactivate");
        else
        {
            using var password = EngineeringCredentialRules.ToSecureString(_request.Password);
            result = Call(Get(security, "DriveDataEncryption"), _request.Action == "activateEncryption" ? "Activate" : "Deactivate", password);
        }
        meta["result"] = result;
        if (result is false) meta["operationSuccess"] = false;
        return "Drive security action returned; no download.";
    }

    private string Extensions(HmiObject meta)
    {
        bool writing = StartdrivePolicy
            .ValidateTechnologyExtensionRequest(_request.Action, _request.Identifier, _request.FilePath, _request.ConfirmUninstallInUse, _request.DryRun);
        if (StartdrivePolicy.IsPortalScopedExtensionAction(_request.Action))
        {
            var provider = Service(_engine.PlcB5Session.CurrentPortal!, "MC.Drives.TechnologyExtensionInstallationProvider");
            var packages = Get(provider, "TechnologyExtensionPackages");
            meta["before"] = new HmiArray(Items(packages).Select(v => (object?)Row(v)).ToArray());
            if (_request.Action == "readPackages") return "Installed technology extension packages read.";
            var package = _request.Action == "uninstall" ? Items(packages)
                .SingleOrDefault(v => Convert.ToString(Get(v, "Identifier"), CultureInfo.InvariantCulture) == _request.Identifier)
                ?? throw new PlcSoftwareException("NotFound", "Exact technology extension package not found.") : null;
            var file = package == null ? OptionalRules.RequireExistingInputFile(_request.FilePath, "filePath") : null;
            if (!writing) return "Technology extension installation preview; nothing changed.";
            meta["mayHaveChanged"] = true;
            meta["result"] = package == null ? Call(provider, "Install", file!)
                : Call(provider, "Uninstall", _request.Identifier, _request.ConfirmUninstallInUse);
            return "Technology extension installation action returned.";
        }
        using var access = Edit(writing);
        var container = Service(Drive(), "MC.Drives.TechnologyExtensionContainer");
        var extensions = Get(container, "TechnologyExtensions");
        meta["before"] = new HmiArray(Items(extensions).Select(v => (object?)Row(v)).ToArray());
        if (_request.Action == "read") return "Drive technology extensions read.";
        var target = Items(extensions).SingleOrDefault(v => Convert.ToString(Get(v, "Identifier"), CultureInfo.InvariantCulture) == _request.Identifier)
            ?? throw new PlcSoftwareException("NotFound", "Exact drive technology extension not found.");
        if (!writing) return "Drive technology extension preview; nothing changed.";
        meta["mayHaveChanged"] = true;
        meta["result"] = Call(target, _request.Action == "activate" ? "Activate" : "Deactivate");
        return "Drive technology extension action returned; no download.";
    }

    private string Telegrams(HmiObject meta)
    {
        var plan = StartdrivePolicy.ValidateTelegramRequest(_request.Action, _request.TelegramType, _request.TelegramNumber, _request.InputSize,
            _request.OutputSize, _request.Direction, _request.Size, _request.KeepOriginalAddress, _request.SoftwarePath, _request.ObjectPath,
            _request.InterfaceKind, _request.SensorIndex, _request.ConnectOption, _request.DryRun);
        using var access = Edit(plan.Writes);
        var composition = Get(Drive(), "Telegrams");
        var all = Items(composition);
        meta["before"] = new HmiArray(all.Select(v => (object?)Row(v)).ToArray());
        if (_request.Action == "read") return "Drive telegrams read; no change.";
        if (all.Length == 0)
            throw new PlcSoftwareException("InvalidState",
                "This drive object has no telegram yet; Can*/Insert*/Erase calls are refused to avoid a known TIA crash.");
        var type =
            EnumValue(_release == "14sp1" ? "Siemens.Engineering.Startdrive.Enums.TelegramType" : "Siemens.Engineering.MC.Drives.Enums.TelegramType", plan
                .TelegramType);
        var target = Call(composition, "Find", type);
        if (_request.TelegramType == "MainTelegram" && target != null && _request.Action is "check" or "insert")
        {
            if (_request.Action == "insert") throw new PlcSoftwareException("InvalidState", "MainTelegram already exists; use changeNumber or changeSize.");
            meta["checks"] = new HmiObject { ["mainTelegramPresent"] = true, ["canInsertMainTelegram"] = null };
            return "A main telegram already exists; no CanInsertMainTelegram or InsertMainTelegram call was issued.";
        }
        string suffix = plan.TelegramType == "AdditionalTelegram" ? "AdditionalTelegram" : plan.TelegramType == "SupplementaryTelegram"
            ? "SupplementaryTelegram" : plan.TelegramType == "MainTelegram" ? "MainTelegram" : plan.TelegramType == "TorqueTelegram" ? "TorqueTelegram"
            : "SafetyTelegram";
        if (_request.Action is "check" or "insert")
        {
            var args = plan.TelegramType == "AdditionalTelegram" ? new object?[] { plan.InputSize, plan.OutputSize } : new object?[] { plan.TelegramNumber };
            var allowed = Call(composition, "CanInsert" + suffix, args);
            meta["canInsert"] = allowed;
            if (_request.Action == "check" || !plan.Writes) return "Drive telegram insertion checked; nothing changed.";
            OptionalNativeOperations.RequireCanChange(allowed);
            meta["mayHaveChanged"] = true;
            var inserted = Call(composition, "Insert" + suffix, args);
            meta["result"] = inserted == null || inserted is bool ? inserted : Row(inserted);
        }
        else
        {
            if (target == null) throw new PlcSoftwareException("NotFound", "Exact telegram not found.");
            if (_request.Action == "changeNumber")
            {
                OptionalNativeOperations.ChangeTelegramNumber(target, plan.TelegramNumber, plan.Writes, () => meta["mayHaveChanged"] = true);
                if (!plan.Writes) return "Drive telegram edit preview; nothing changed.";
            }
            else if (_request.Action == "changeSize")
            {
                var direction = EnumValue("Siemens.Engineering.HW.AddressIoType", plan.Direction);
                OptionalNativeOperations.RequireCanChange(Call(target, "CanChangeSize", direction, plan.Size, plan.KeepOriginalAddress));
                if (!plan.Writes) return "Drive telegram edit preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                Call(target, "ChangeSize", direction, plan.Size, plan.KeepOriginalAddress);
            }
            else
            {
                if (!plan.Writes) return "Drive telegram edit preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (_request.Action == "erase") Call(composition, "EraseTelegram", type);
                else
                {
                    var technology = _engine.OptionalTechnology(plan.SoftwarePath, plan.ObjectPath, true);
                    var provider =
                        Service(technology, plan.InterfaceKind == "encoder" ? "SW.TechnologicalObjects.Motion.EncoderHardwareConnectionProvider"
                            : "SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider");
                    var connection = Get(provider, plan.InterfaceKind == "actor" ? "ActorInterface" : "SensorInterface");
                    if (plan.InterfaceKind == "sensor")
                    {
                        var sensors = Items(connection);
                        if (plan.SensorIndex >= sensors.Length)
                            throw new ArgumentException("sensorIndex out of range: " + sensors.Length + " sensor interface(s).");
                        connection = sensors[plan.SensorIndex];
                    }
                    if (plan.HasConnectOption)
                        Call(connection, "Connect", target, EnumValue("Siemens.Engineering.MC.Drives.Enums.ConnectOption", plan.ConnectOption));
                    else Call(connection, "Connect", target);
                    meta["after"] = OptionalObjectAddress.Read(connection);
                }
            }
        }
        meta["after"] = new HmiArray(Items(Get(Drive(), "Telegrams")).Select(v => (object?)Row(v)).ToArray());
        return "Drive telegram action returned and read back; no download.";
    }

    private string Functions(HmiObject meta)
    {
        if (_request.Operation == "ManageOnlineDriveFunctions")
        {
            StartdrivePolicy.ValidateOnlineFunctionRequest(_request.Action, _request.ResetMode, _request.ActivationState, _request.ConfirmOnline);
            var container = Service(DriveItem(), "MC.Drives.OnlineDriveObjectContainer");
            var drives = Items(Get(container, "OnlineDriveObjects"));
            var selector = StartdrivePolicy.ParseDriveSelector(_request.DriveObjectNumber, _request.DriveObjectIndex);
            if (selector.Index >= drives.Length) throw new PlcSoftwareException("NotFound", "Online driveObjectIndex is out of range.");
            var online = Service(drives[selector.Index], "MC.Drives.DFI.OnlineDriveFunctionInterface");
            meta["before"] = Row(online);
            if (_request.Action == "read") return "Online drive function interface read; nothing written.";
            meta["mayHaveChanged"] = true;
            meta["result"] = _request.Action == "performFactoryReset"
                ? Call(Get(online, "DriveDomainFunctions"), "PerformFactoryReset",
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.ResetMode", _request.ResetMode))
                : _request.Action == "performRamToRomCopy" ? Call(Get(online, "DriveDomainFunctions"), "PerformRAMtoROMCopyAllDriveObject")
                : Call(Get(Get(online, "DriveObjectFunctions"), "DriveObjectActivation"), "ChangeActivationState",
                        EnumValue("Siemens.Engineering.MC.Drives.Enums.DriveObjectActivationState", _request.ActivationState));
            if (meta["result"] is false) meta["operationSuccess"] = false;
            return "Online drive function returned; inspect the device state before any retry.";
        }
        var plan = StartdrivePolicy.ValidateFunctionRequest(_request.Action, _request.ValueJson, _request.DryRun);
        using var access = Edit(plan.Writes);
        var functions = Service(Drive(), "MC.Drives.DFI.DriveFunctionInterface");
        meta["before"] = FunctionRow(functions);
        if (_request.Action == "read") return "Offline drive function interface read.";
        if (!plan.Writes && _request.Action is not "readMotorConfiguration" and not "readEncoderConfiguration")
            return "Offline drive function preview; nothing changed.";
        object? result;
        if (_request.Action == "changeDriveObjectType")
        {
            var handler = Get(Get(functions, "DriveObjectFunctions"), "DriveObjectTypeHandler");
            var type = Call(Get(handler, "PossibleDriveObjectTypes"), "Find", plan.DriveObjectType)
                ?? throw new PlcSoftwareException("NotFound", "Requested drive object type is unavailable.");
            meta["mayHaveChanged"] = true;
            result = Call(handler, "ChangeDriveObjectType", type);
        }
        else if (_request.Action == "changeActivationState")
        {
            meta["mayHaveChanged"] = true;
            result = Call(Get(Get(functions, "DriveObjectFunctions"), "DriveObjectActivation"), "ChangeActivationState",
                EnumValue("Siemens.Engineering.MC.Drives.Enums.DriveObjectActivationState", plan.ActivationState));
        }
        else if (_request.Action == "setSimoGearMlfb")
        {
            meta["mayHaveChanged"] = true;
            result = Call(Get(functions, "Commissioning"), "SetSimoGearMlfb", plan.Mlfb);
        }
        else if (_request.Action == "updateCheckSums")
        {
            meta["mayHaveChanged"] = true;
            result = Call(Get(functions, "SafetyCommissioning"), "UpdateCheckSums");
        }
        else
        {
            var projection = Get(functions, "HardwareProjection");
            string method = _request.Action switch {
                "setMotorType" => "SetMotorType", "readMotorConfiguration" or "projectMotorConfiguration" or "setEquivalentCircuitDiagramData" =>
                    "GetCurrentMotorConfiguration",
                "setEncoder" => "SetEncoder", _ => "GetCurrentEncoderConfiguration" };
            object?[] args = _request.Action == "setMotorType" ? new object?[] {
                EnumValue("Siemens.Engineering.MC.Drives.Enums.MotorType", plan.MotorType), (ushort)plan.DataSet }
                : _request.Action == "setEncoder" ? new object?[] { EnumValue("Siemens.Engineering.MC.Drives.Enums.EncoderInterface", plan.EncoderInterface),
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.EncoderType", plan.EncoderType),
                        EnumValue("Siemens.Engineering.MC.Drives.Enums.AbsoluteIncrementalFlag", plan.AbsoluteIncremental),
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.RotaryLinearFlag", plan.RotaryLinear), (ushort)plan.EncoderNumber }
                : _request.Action.Contains("Motor") || _request.Action == "setEquivalentCircuitDiagramData" ? new object?[] { (ushort)plan.DataSet }
                : new object?[] { (ushort)plan.EncoderNumber };
            if (plan.Writes && _request.Action is "setMotorType" or "setEncoder") meta["mayHaveChanged"] = true;
            result = Call(projection, method, args);
            if (_request.Action == "setEncoderType")
            {
                meta["mayHaveChanged"] = true;
                var rotary = EnumValue("Siemens.Engineering.MC.Drives.Enums.RotaryLinearFlag", plan.RotaryLinear);
                result = plan.HasAbsoluteIncremental ? Call(result!, "SetEncoderType", rotary,
                    EnumValue("Siemens.Engineering.MC.Drives.Enums.AbsoluteIncrementalFlag", plan.AbsoluteIncremental))
                        : Call(result!, "SetEncoderType", rotary);
            }
            if (_request.Action is "projectMotorConfiguration" or "projectEncoderConfiguration" or "setEquivalentCircuitDiagramData")
            {
                var configuration = result!;
                var entries = Items(Get(configuration, "RequiredConfigurationEntries"))
                    .Concat(Optional(configuration, "OptionalConfigurationEntries") is object optional ? Items(optional) : Array.Empty<object>()).ToArray();
                OptionalNativeOperations.ApplyConfigurationEntries(entries, plan.Entries, HmiScalar.ConvertValue,
                    () => meta["mayHaveChanged"] = true);
                result = _request.Action == "setEquivalentCircuitDiagramData"
                    ? Call(configuration, "SetEquivalentCircuitDiagramData", plan.EquivalentCircuitDiagram)
                    : Call(projection, _request.Action == "projectEncoderConfiguration" ? "ProjectEncoderConfiguration" : "ProjectMotorConfiguration",
                            configuration,
                        (ushort)(_request.Action == "projectEncoderConfiguration" ? plan.EncoderNumber : plan.DataSet));
            }
        }
        meta["result"] = result == null || result is bool ? result : Row(result);
        if (result is false) meta["operationSuccess"] = false;
        meta["after"] = FunctionRow(functions);
        return "Offline drive function returned; no download or online drive command.";
    }

    private static HmiObject DccSummary(object container) => new HmiObject { ["chartCount"] = Items(Get(container, "Charts")).Length };

}
