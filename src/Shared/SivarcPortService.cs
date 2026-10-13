using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class SivarcService
{
    private readonly OptionalEngineeringPort _port;
    public SivarcService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal SivarcService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalEngineeringPort(call, identity);
    }
    public ResponseMessage ReadSivarcRuleTree(string category, string folderPath = "", string tablePath = "", bool includeRules = true, int maxDepth = 4, int offset = 0, int limit = 200)
    {
        var request = new OptionalEngineeringRequest { Operation = "GetSivarcRuleTree", IsWrite = false, Category = category, FolderPath = folderPath, TablePath = tablePath, IncludeRules = includeRules, MaxDepth = maxDepth, Offset = offset, Limit = limit };
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ManageSivarcRuleContainer(string category, string kind, string path, string action = "read", string libraryName = "", string typePath = "", string typeVersion = "", bool confirmDelete = false, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageSivarcRuleContainer", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), Category = category, Kind = kind, Path = path, Action = action, LibraryName = libraryName, TypePath = typePath, TypeVersion = typeVersion, ConfirmDelete = confirmDelete, DryRun = dryRun };
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ManageSivarcTableRule(string category, string tablePath, string rulePath = "", string kind = "rule", string action = "read", string propertiesJson = "{}", string referencesJson = "{}",
            string deviceSelectionJson = "{}", string deviceNamesJson = "[]", string libraryName = "", string masterCopyPath = "", string createOption = "Replace", bool confirmDelete = false, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageSivarcTableRule", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), Category = category, TablePath = tablePath, RulePath = rulePath, Kind = kind, Action = action, PropertiesJson = propertiesJson, ReferencesJson = referencesJson, DeviceSelectionJson = deviceSelectionJson, DeviceNamesJson = deviceNamesJson, LibraryName = libraryName, MasterCopyPath = masterCopyPath, CreateOption = createOption, ConfirmDelete = confirmDelete, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, propertiesJson);
        OptionalEngineeringPort.Input(request, referencesJson);
        OptionalEngineeringPort.Input(request, deviceSelectionJson);
        OptionalEngineeringPort.Input(request, deviceNamesJson);
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ReadSivarcBlockDefinitions(string softwarePath, string blockPath, bool includeBlockParameters = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ListSivarcBlockDefinitions", IsWrite = false, SoftwarePath = softwarePath, BlockPath = blockPath, IncludeBlockParameters = includeBlockParameters };
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ManageSivarcBlockDefinition(string softwarePath, string blockPath, string kind, string name = "", string action = "read", string propertiesJson = "{}", string textsJson = "{}", bool confirmDelete = false, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageSivarcBlockDefinition", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), SoftwarePath = softwarePath, BlockPath = blockPath, Kind = kind, Name = name, Action = action, PropertiesJson = propertiesJson, TextsJson = textsJson, ConfirmDelete = confirmDelete, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, propertiesJson);
        OptionalEngineeringPort.Input(request, textsJson);
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ResolveSivarcExpression(string softwarePath, string blockPath, string devicePathJson, string itemPathJson, string libraryItemKind, string libraryItemPath, string expression, string libraryName = "", int maxResults = 500)
    {
        var request = new OptionalEngineeringRequest { Operation = "ResolveSivarcExpression", IsWrite = false, SoftwarePath = softwarePath, BlockPath = blockPath, DevicePathJson = devicePathJson, ItemPathJson = itemPathJson, LibraryItemKind = libraryItemKind, LibraryItemPath = libraryItemPath, Expression = expression, LibraryName = libraryName, MaxResults = maxResults };
        OptionalEngineeringPort.Input(request, devicePathJson);
        OptionalEngineeringPort.Input(request, itemPathJson);
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ManageSivarcScreenLayout(string softwarePath, string screenName, string action, string filePath, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageSivarcScreenLayout", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), SoftwarePath = softwarePath, ScreenName = screenName, Action = action, FilePath = filePath, DryRun = dryRun };
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage UpgradeSivarcDefinitions(string softwarePath, bool dryRun = true)
    {
        var request = new OptionalEngineeringRequest { Operation = "UpgradeSivarcDefinitions", IsWrite = !dryRun && !(false), SoftwarePath = softwarePath, DryRun = dryRun };
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage GenerateSiVArc(string hmiDeviceName, string plcSoftwarePathsJson, string generationOptions, bool dryRun = true, string additionalHmiDeviceNamesJson = "[]")
    {
        var request = new OptionalEngineeringRequest { Operation = "GenerateSivarc", IsWrite = !dryRun && !(false), HmiDeviceName = hmiDeviceName, PlcSoftwarePathsJson = plcSoftwarePathsJson, GenerationOptions = generationOptions, DryRun = dryRun, AdditionalHmiDeviceNamesJson = additionalHmiDeviceNamesJson };
        OptionalEngineeringPort.Input(request, plcSoftwarePathsJson);
        OptionalEngineeringPort.Input(request, additionalHmiDeviceNamesJson);
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
}
