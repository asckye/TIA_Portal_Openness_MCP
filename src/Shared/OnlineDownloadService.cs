using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class OnlineDownloadService
{
    private readonly Func<string, JsonObject, JsonNode?> _call;
    private readonly Func<string> _identity;
    public OnlineDownloadService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal OnlineDownloadService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _call = call;
        _identity = identity;
    }
    private T Invoke<T>(OnlineRequest request)
    {
        string? unsupported = OnlineCapabilities.Unsupported(HardwareContract.ReleaseKey, request);
        if (unsupported != null) throw new NotSupportedException(unsupported);
        try { request.PromptAnswers = DownloadPromptPolicy.ParseExplicitAnswers(request.PromptAnswersJson); }
        catch (ArgumentException error) { request.PromptAnswersError = error.Message; }
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = _identity(); }
        var reply = _call("plc-online.ExecuteOnline", arguments) as JsonObject ?? throw new InvalidOperationException("Missing online worker reply.");
        var failure = reply["Failure"]?.Deserialize<PlcSoftwareFailure>();
        if (failure != null) throw new PortalException(failure.Code == "NotFound" ? PortalErrorCode.NotFound : PortalErrorCode.InvalidState, failure.Message);
        return reply["Value"]!.Deserialize<T>()!;
    }
    public ResponseOnlineState GetOnlineState(string softwarePath)
        => Invoke<ResponseOnlineState>(new OnlineRequest { Operation = "GetOnlineState", SoftwarePath = softwarePath });

    public ResponseOnlineState GoOnline(string softwarePath, string? ipAddress, string? password, string? userName, string? userType, string rhTarget, string? pgPcInterface, bool trustDeviceCertificate)
        => Invoke<ResponseOnlineState>(new OnlineRequest { Operation = "ConnectOnlinePlc", DryRun = false, SoftwarePath = softwarePath, IpAddress = ipAddress ?? "", Password = password ?? "", UserName = userName ?? "", UserType = userType ?? "", RhTarget = rhTarget, PgPcInterface = pgPcInterface ?? "", TrustDeviceCertificate = trustDeviceCertificate });

    public ResponseMessage GoOffline(string softwarePath)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "DisconnectOnlinePlc", DryRun = false, SoftwarePath = softwarePath });

    public JsonObject GoOfflineAll()
        => Invoke<JsonObject>(new OnlineRequest { Operation = "DisconnectOnlinePlcs", DryRun = false });

    public ResponseCompare CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
        => Invoke<ResponseCompare>(new OnlineRequest { Operation = "CompareSoftwareToOnline", SoftwarePath = softwarePath, MaxDepth = maxDepth, MaxEntries = maxEntries });

    public ResponseDownload DownloadToPlc(
            string softwarePath,
            bool consistentBlocksOnly = true,
            bool keepActualValues = true,
            bool startAfterDownload = true,
            bool stopBeforeDownload = true,
            string? password = null,
            string? pgPcInterface = null,
            string? targetIpAddress = null,
            string userManagementMode = "keep",
            string promptAnswersJson = "{}",
            string? moduleAccessPassword = null,
            string? blockBindingPassword = null,
            string? masterSecretPassword = null,
            string rhTarget = "",
            bool trustDeviceCertificate = true)
        => Invoke<ResponseDownload>(new OnlineRequest { Operation = "DownloadPlc", DryRun = false, SoftwarePath = softwarePath, ConsistentBlocksOnly = consistentBlocksOnly, KeepActualValues = keepActualValues, StartAfterDownload = startAfterDownload, StopBeforeDownload = stopBeforeDownload, Password = password ?? "", PgPcInterface = pgPcInterface ?? "", TargetIpAddress = targetIpAddress ?? "", UserManagementMode = userManagementMode, PromptAnswersJson = promptAnswersJson, ModuleAccessPassword = moduleAccessPassword ?? "", BlockBindingPassword = blockBindingPassword ?? "", MasterSecretPassword = masterSecretPassword ?? "", RhTarget = rhTarget, TrustDeviceCertificate = trustDeviceCertificate });

    public ResponseCheckDownload CheckDownloadReadiness(string softwarePath)
        => Invoke<ResponseCheckDownload>(new OnlineRequest { Operation = "CheckDownloadReadiness", SoftwarePath = softwarePath });

    public ResponseMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "ScanAccessibleDevices", PgPcInterface = pgPcInterface, SoftwarePath = softwarePath, Offset = offset, Limit = limit });

    public ResponseMessage UploadStationFromPlc(string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "UploadStationFromPlc", TargetIpAddress = targetIpAddress, PgPcInterface = pgPcInterface, Password = password, PromptAnswersJson = promptAnswersJson, ConfirmUpload = confirmUpload, DryRun = dryRun });

    public ResponseMessage UploadDeviceParameters(string devicePathJson, string itemPathJson, string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "UploadDeviceParameters", DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!, ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!, TargetIpAddress = targetIpAddress, PgPcInterface = pgPcInterface, Password = password, PromptAnswersJson = promptAnswersJson, ConfirmUpload = confirmUpload, DryRun = dryRun });

    public ResponseMessage DownloadPlcToFolder(string softwarePath, string destinationDirectory, string targetForSoftware = "CPU", bool overwriteOnMemoryCard = false,
            bool keepActualValues = true, string userManagementMode = "keep", string promptAnswersJson = "{}", bool confirmDownload = false, bool dryRun = true)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "DownloadPlcToFolder", SoftwarePath = softwarePath, DestinationDirectory = destinationDirectory, TargetForSoftware = targetForSoftware, OverwriteOnMemoryCard = overwriteOnMemoryCard, KeepActualValues = keepActualValues, UserManagementMode = userManagementMode, PromptAnswersJson = promptAnswersJson, ConfirmDownload = confirmDownload, DryRun = dryRun });

    public ResponseMessage ReadTransferRoutes(string softwarePath, int maxItems = 500)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "ListTransferRoutes", SoftwarePath = softwarePath, MaxItems = maxItems });

    public ResponseMessage ManagePlcDataBlockSnapshot(string softwarePath, string blockPath, string action, string filePath = "", bool confirmValueChange = false, bool dryRun = true)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "ManagePlcDataBlockSnapshot", SoftwarePath = softwarePath, BlockPath = blockPath, Action = action, FilePath = filePath, ConfirmValueChange = confirmValueChange, DryRun = dryRun });
    public ResponseMessage ReadPlcBlockFingerprints(string softwarePath, string targetIpAddress, string pgPcInterface = "", string password = "", int offset = 0, int limit = 100, bool dryRun = true)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "GetPlcBlockFingerprints", SoftwarePath = softwarePath, TargetIpAddress = targetIpAddress, PgPcInterface = pgPcInterface, Password = password, Offset = offset, Limit = limit, DryRun = dryRun });
    public ResponseMessage GeneratePlcLoadableFile(string softwarePath, string objectPathsJson, string objectKind, string targetOption, string filePath, bool dryRun = true)
        => Invoke<ResponseMessage>(new OnlineRequest { Operation = "GeneratePlcLoadableFile", SoftwarePath = softwarePath, ObjectPaths = JsonSerializer.Deserialize<string[]>(objectPathsJson)!, ObjectKind = objectKind, TargetOption = targetOption, FilePath = filePath, DryRun = dryRun });
}
