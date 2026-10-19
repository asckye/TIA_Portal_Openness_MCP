using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Online;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
#if ONLINE_DOWNLOAD
    private OnlineDownloadAdapter? _onlineFallbackAdapter;
    private OnlineDownloadAdapter OnlineFallbackAdapter => _onlineFallbackAdapter ??= OnlineAdapter(new OnlineRequest());
    public TiaMcp.Adapters.Contracts.Candidates.IFallbackAdapter OnlineFallback(TiaMcp.Adapters.Contracts.Candidates.FallbackRequest request, string password, Func<SessionState> binding, Action uncertain, Func<TiaMcp.Adapters.Contracts.Candidates.FallbackRequest, TiaMcp.Adapters.Contracts.Candidates.FallbackNativeResult> otherOperation)
    {
        Check();
        return OnlineFallbackAdapter.FallbackAdapter(request, password, binding, uncertain, otherOperation);
    }
    public string OnlineFallbackOfflineState(string softwarePath)
    {
        Check();
        return OnlineFallbackAdapter.FallbackOfflineState(softwarePath);
    }
#endif
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
            "ConnectOnlinePlc" => adapter.GoOnline(request.SoftwarePath, Empty(request.IpAddress), Empty(request.Password), Empty(request.UserName), Empty(request.UserType), request.RhTarget, Empty(request.PgPcInterface), request.TrustDeviceCertificate, request.AllowNonTlsLogin),
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
            "ManagePlcDataBlockSnapshot" => adapter.ManagePlcDataBlockSnapshot(request.SoftwarePath, request.BlockPath, request.Action, request.FilePath, request.ConfirmValueChange, request.DryRun),
#endif
#if ONLINE_FINGERPRINTS
            "GetPlcBlockFingerprints" => adapter.ReadPlcBlockFingerprints(request.SoftwarePath, request.TargetIpAddress, request.PgPcInterface, request.Password, request.Offset, request.Limit, request.DryRun, request.TrustDeviceCertificate, request.AllowNonTlsLogin),
#endif
#if ONLINE_LOADABLE
            "GeneratePlcLoadableFile" => adapter.GeneratePlcLoadableFile(request.SoftwarePath, "", request.ObjectKind, request.TargetOption, request.FilePath, request.DryRun),
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
        var meta = value is HmiMessage message ? message.Meta : value is OnlineDownloadResult download ? download.Meta : null;
        bool changed = meta != null && meta.TryGetValue("mayHaveChanged", out var flag) && flag is true;
        bool succeeded = meta != null && meta.TryGetValue("success", out var success) && success is true;
        bool files = meta != null && meta.TryGetValue("mayHaveWrittenFiles", out var written) && written is true;
        bool rejected = meta != null && meta.TryGetValue("v4Rejection", out var refusal) && refusal != null;
        string? state = value is OnlineDownloadResult result ? result.State
            : meta != null && meta.TryGetValue("nativeState", out var nativeState) ? nativeState as string : null;
        return new OnlineReply { Value = value, MayHaveChanged = changed,
            RequiresSessionReset = OnlineCompletionPolicy.RequiresSessionReset(request, succeeded,
                nativeCalls.NativeCallIssued, changed, files, rejected, state) };
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
