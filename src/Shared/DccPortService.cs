using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class DccService
{
    private readonly OptionalEngineeringPort _port;
    public DccService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal DccService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalEngineeringPort(call, identity);
    }
    public ResponseMessage ReadDccCharts(string devicePathJson, string itemPathJson, ushort driveObjectNumber = 0, int driveObjectIndex = -1, string chartPath = "", int maxDepth = 3, bool includeBlocks = true, bool includePins = false, bool includeLibraries = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ListDccCharts", IsWrite = false, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, ChartPath = chartPath, MaxDepth = maxDepth, IncludeBlocks = includeBlocks, IncludePins = includePins, IncludeLibraries = includeLibraries };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ManageDccChart(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string chartName, string action, string filePath = "", string importOptions = "", string propertiesJson = "{}", bool dryRun = true, int driveObjectIndex = -1, bool confirmDelete = false, int sequenceIndex = -1)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDccChart", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, ChartName = chartName, Action = action, FilePath = filePath, ImportOptions = importOptions, PropertiesJson = propertiesJson, DryRun = dryRun, DriveObjectIndex = driveObjectIndex, ConfirmDelete = confirmDelete, SequenceIndex = sequenceIndex };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, propertiesJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ManageDccBlock(string devicePathJson, string itemPathJson, string chartPath, string blockName = "", string action = "read", string blockType = "", string libraryName = "", string propertiesJson = "{}", ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool confirmDelete = false, int sequenceIndex = -1, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDccBlock", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, ChartPath = chartPath, BlockName = blockName, Action = action, BlockType = blockType, LibraryName = libraryName, PropertiesJson = propertiesJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, ConfirmDelete = confirmDelete, SequenceIndex = sequenceIndex, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, propertiesJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ManageDccPin(string devicePathJson, string itemPathJson, string chartPath, string blockName, string pinName, string action = "read", string propertiesJson = "{}", string partnerJson = "{}", bool setAsSignal = false, int parameterNumber = -1, int arrayIndex = -1, ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDccPin", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, ChartPath = chartPath, BlockName = blockName, PinName = pinName, Action = action, PropertiesJson = propertiesJson, PartnerJson = partnerJson, SetAsSignal = setAsSignal, ParameterNumber = parameterNumber, ArrayIndex = arrayIndex, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, propertiesJson);
        OptionalEngineeringPort.Input(request, partnerJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ManageDccChartInterface(string devicePathJson, string itemPathJson, string chartPath, string interfaceName = "", string action = "read", string sourceBlock = "", string sourcePin = "", string propertiesJson = "{}", ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool confirmDelete = false, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDccChartInterface", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, ChartPath = chartPath, InterfaceName = interfaceName, Action = action, SourceBlock = sourceBlock, SourcePin = sourcePin, PropertiesJson = propertiesJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, ConfirmDelete = confirmDelete, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, propertiesJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ManageDccChartPartition(string devicePathJson, string itemPathJson, string chartPath, string partitionName = "", string action = "read", string propertiesJson = "{}", ushort driveObjectNumber = 0, int driveObjectIndex = -1, bool confirmDelete = false, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDccChartPartition", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, ChartPath = chartPath, PartitionName = partitionName, Action = action, PropertiesJson = propertiesJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, ConfirmDelete = confirmDelete, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, propertiesJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ManageDcbLibraries(string action = "read", string devicePathJson = "[]", string itemPathJson = "[]", ushort driveObjectNumber = 0, int driveObjectIndex = -1, string filePath = "", bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDcbLibraries", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), Action = action, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, DriveObjectIndex = driveObjectIndex, FilePath = filePath, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
    public ResponseMessage ReadDccObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, string objectPathJson = "[]", int offset = 0, int limit = 100, int driveObjectIndex = -1)
    {
        var request = new OptionalEngineeringRequest { Operation = "GetDccObject", IsWrite = false, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, DriveObjectNumber = driveObjectNumber, ObjectPathJson = objectPathJson, Offset = offset, Limit = limit, DriveObjectIndex = driveObjectIndex };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        OptionalEngineeringPort.Input(request, objectPathJson);
        return _port.Invoke("startdrive.ExecuteDcc", request);
    }
}
