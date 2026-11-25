using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Online;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
    public Func<List<PlcSoftware>>? OnlinePlcs { get; set; }
    public Action<int, Exception?, string, object?[]>? OnlineLog { get; set; }
    private OnlineDownloadAdapter OnlineAdapter(OnlineRequest request)
    {
        var session = EngineeringPlcOrganisationSession ?? new OrganisationSession(this);
        var adapter = new OnlineDownloadAdapter(new OnlineSession(session, request, OnlinePlcs ?? (() => Plcs().Select(p => p.Value).ToList()), OnlineLog));
        adapter.Bind(request);
        return adapter;
    }
    public OnlineReply ExecuteOnline(OnlineRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting online preview mode.");
        if (OnlineCapabilities.Unsupported(ReleaseKey, request) is string unsupported) throw new NotSupportedException(unsupported);
        var adapter = OnlineAdapter(request);
        using var nativeCalls = TiaMcpServer.ModelContextProtocol.InvocationJournal.BeginNativeCallScope();
        object value = request.Operation switch
        {
            "GetOnlineState" => adapter.GetOnlineState(request.SoftwarePath),
            "ConnectOnlinePlc" => adapter
                .GoOnline(request.SoftwarePath, Empty(request.IpAddress), Empty(request.Password), Empty(request.UserName), Empty(request.UserType), request
                    .RhTarget, Empty(request.PgPcInterface), request.TrustDeviceCertificate, request.AllowNonTlsLogin),
            "DisconnectOnlinePlc" => adapter.GoOffline(request.SoftwarePath),
            "DisconnectOnlinePlcs" => adapter.GoOfflineAll(),
            "CompareSoftwareToOnline" => adapter.CompareSoftwareToOnline(request.SoftwarePath, request.MaxDepth, request.MaxEntries),
#if ONLINE_DOWNLOAD
            "CheckDownloadReadiness" => adapter.CheckDownloadReadiness(request.SoftwarePath),
            "DownloadPlc" => adapter.DownloadToPlc(request),
#if ONLINE_FOLDER
            "DownloadPlcToFolder" => adapter.DownloadPlcToFolder(request),
#endif
            "UploadStationFromPlc" => adapter.UploadStationFromPlc(request),
            "ManagePlcDataBlockSnapshot" => adapter
                .ManagePlcDataBlockSnapshot(request.SoftwarePath, request.BlockPath, request.Action, request.FilePath, request.ConfirmValueChange, request
                    .DryRun),
#endif
#if ONLINE_FINGERPRINTS
            "GetPlcBlockFingerprints" => adapter
                .ReadPlcBlockFingerprints(request.SoftwarePath, request.TargetIpAddress, request.PgPcInterface, request.Password, request.Offset, request.Limit,
                    request.DryRun, request.TrustDeviceCertificate, request.AllowNonTlsLogin),
#endif
#if ONLINE_LOADABLE
            "GeneratePlcLoadableFile" => adapter
                .GeneratePlcLoadableFile(request.SoftwarePath, "", request.ObjectKind, request.TargetOption, request.FilePath, request.DryRun),
#endif
#if ONLINE_SCAN
            "ScanAccessibleDevices" => adapter.ScanAccessibleDevices(request.PgPcInterface, request.SoftwarePath, request.Offset, request.Limit),
#endif
#if ONLINE_PARAMETERS
            "UploadDeviceParameters" => adapter.UploadDeviceParameters(request),
#endif
            "ListTransferRoutes" => adapter.ReadTransferRoutes(request.SoftwarePath, request.MaxItems),
            _ => throw new NotSupportedException("Unsupported online operation: " + request.Operation)
        };
        return OnlineCompletionPolicy.Complete(request, value, nativeCalls.NativeCallIssued);
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
