using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcp.Shared;

internal sealed class OptionalProductsPortService
{
    private readonly OptionalProductPort _port;
    public OptionalProductsPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal OptionalProductsPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalProductPort(call, identity);
    }
    public ResponseMessage ManageSinumerikArchive(string action, string filePath, string devicePathJson = "[]", string itemPathJson = "[]",
            string modifiedDevicePathJson = "[]", string modifiedItemPathJson = "[]", string mode = "HardwareAndAllProgramBlocks", string comment = "", string author = "", string password = "", bool dryRun = true)
    {
        var request = new OptionalProductRequest { Operation = "ManageSinumerikArchive", Action = action, FilePath = filePath, Mode = mode, Comment = comment, Author = author, Password = password, DryRun = dryRun };
        return _port.Invoke(request, () =>
        {
            request.DevicePathJson = JsonSerializer.Deserialize<string[]>(devicePathJson) ?? Array.Empty<string>();
            request.ItemPathJson = JsonSerializer.Deserialize<string[]>(itemPathJson) ?? Array.Empty<string>();
            request.ModifiedDevicePathJson = JsonSerializer.Deserialize<string[]>(modifiedDevicePathJson) ?? Array.Empty<string>();
            request.ModifiedItemPathJson = JsonSerializer.Deserialize<string[]>(modifiedItemPathJson) ?? Array.Empty<string>();
        });
    }
    public ResponseMessage ImportSinumerikAlarmTexts(string devicePathJson, string filesJson, bool dryRun = true)
    {
        var request = new OptionalProductRequest { Operation = "ImportSinumerikAlarmTexts", DryRun = dryRun };
        return _port.Invoke(request, () =>
        {
            request.DevicePathJson = JsonSerializer.Deserialize<string[]>(devicePathJson) ?? Array.Empty<string>();
            request.FilesJson = JsonSerializer.Deserialize<string[]>(filesJson) ?? Array.Empty<string>();
        });
    }
    public ResponseMessage ManageSinumerikSafetyMode(string devicePathJson, string action = "read", string mode = "", bool dryRun = true, bool confirmSafetyChange = false)
    {
        var request = new OptionalProductRequest { Operation = "ManageSinumerikSafetyMode", Action = action, Mode = mode, DryRun = dryRun, ConfirmSafetyChange = confirmSafetyChange };
        return _port.Invoke(request, () =>
        {
            request.DevicePathJson = JsonSerializer.Deserialize<string[]>(devicePathJson) ?? Array.Empty<string>();
        });
    }
    public ResponseMessage InitializeSimotionScripting(bool dryRun = true)
    {
        var request = new OptionalProductRequest { Operation = "InitializeSimotionScripting", DryRun = dryRun };
        return _port.Invoke(request, () =>
        {
        });
    }
    public ResponseMessage ExportScadaData(string filePath, string softwarePath = "", bool dryRun = true)
    {
        var request = new OptionalProductRequest { Operation = "ExportScadaData", FilePath = filePath, SoftwarePath = softwarePath, DryRun = dryRun };
        return _port.Invoke(request, () =>
        {
        });
    }
}
