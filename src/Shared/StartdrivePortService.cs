using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class StartdriveService
{
    private readonly OptionalEngineeringPort _port;
    public StartdriveService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal StartdriveService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalEngineeringPort(call, identity);
    }
    public ResponseMessage ReadDriveObjects(string devicePathJson, string itemPathJson, bool includeTelegrams = true, bool includeFunctions = true, bool includeTechnologyExtensions = true, bool includeDcc = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ListDriveObjects", IsWrite = false, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, IncludeTelegrams = includeTelegrams, IncludeFunctions = includeFunctions, IncludeTechnologyExtensions = includeTechnologyExtensions, IncludeDcc = includeDcc };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ReadDriveParameters(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string source = "read", string namesJson = "[]", string numbersJson = "[]",
            bool includeBits = false, bool includeEnumValues = false, int offset = 0, int limit = 100, bool includeValue = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "GetDriveParameters", IsWrite = false, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, Source = source, NamesJson = namesJson, NumbersJson = numbersJson, IncludeBits = includeBits, IncludeEnumValues = includeEnumValues, Offset = offset, Limit = limit, IncludeValue = includeValue };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, namesJson);
        OptionalEngineeringPort.Input(request, numbersJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageStartdriveParameter(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string parameter, string action = "read", string valueJson = "null", bool dryRun = true, int driveObjectIndex = -1)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageStartdriveParameter", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, Parameter = parameter, Action = action, ValueJson = valueJson, DryRun = dryRun, DriveObjectIndex = driveObjectIndex };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, valueJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageDriveTelegrams(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string telegramType = "", int telegramNumber = -1,
            int inputSize = -1, int outputSize = -1, string direction = "", int size = -1, bool keepOriginalAddress = true, string softwarePath = "", string objectPath = "", string interfaceKind = "", int sensorIndex = 0, string connectOption = "", bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveTelegrams", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, Action = action, TelegramType = telegramType, TelegramNumber = telegramNumber, InputSize = inputSize, OutputSize = outputSize, Direction = direction, Size = size, KeepOriginalAddress = keepOriginalAddress, SoftwarePath = softwarePath, ObjectPath = objectPath, InterfaceKind = interfaceKind, SensorIndex = sensorIndex, ConnectOption = connectOption, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageDriveFunctions(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string valueJson = "{}", bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveFunctions", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, Action = action, ValueJson = valueJson, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, valueJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageDriveSecurity(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string password = "", bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveSecurity", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, Action = action, Password = password, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageTechnologyExtensions(string action = "read", string devicePathJson = "[]", string itemPathJson = "[]", ushort driveObjectNumber = 0, int driveObjectIndex = -1, string identifier = "", string filePath = "", bool confirmUninstallInUse = false, bool includeParameters = false, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageTechnologyExtensions", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), Action = action, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, Identifier = identifier, FilePath = filePath, ConfirmUninstallInUse = confirmUninstallInUse, IncludeParameters = includeParameters, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageDriveHardwareModule(string devicePathJson, string itemPathJson, string action = "read", string typeIdentifier = "", int positionNumber = -1, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveHardwareModule", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, Action = action, TypeIdentifier = typeIdentifier, PositionNumber = positionNumber, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageDriveSafetyAcceptanceTest(string devicePathJson, string itemPathJson, string action = "read", string identifier = "", bool active = false, string filePath = "", string fileOperation = "", bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveSafetyAcceptanceTest", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, Action = action, Identifier = identifier, Active = active, FilePath = filePath, FileOperation = fileOperation, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ReadOnlineDriveParameters(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string namesJson = "[]", string numbersJson = "[]", bool includeBits = false, bool includeEnumValues = false, int offset = 0, int limit = 100, bool includeValue = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "GetOnlineDriveParameters", IsWrite = false, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, NamesJson = namesJson, NumbersJson = numbersJson, IncludeBits = includeBits, IncludeEnumValues = includeEnumValues, Offset = offset, Limit = limit, IncludeValue = includeValue };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, namesJson);
        OptionalEngineeringPort.Input(request, numbersJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
    public ResponseMessage ManageOnlineDriveFunctions(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string action = "read", string resetMode = "", string activationState = "", bool confirmOnline = false)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageOnlineDriveFunctions", IsWrite = !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, Action = action, ResetMode = resetMode, ActivationState = activationState, ConfirmOnline = confirmOnline };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteStartdrive", request);
    }
}
