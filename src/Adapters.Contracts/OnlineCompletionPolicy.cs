namespace TiaMcp.Adapters.Contracts;

public static class OnlineCompletionPolicy
{
    internal static OnlineReply Complete(OnlineRequest request, object value, bool nativeCallIssued)
    {
        var meta = value switch { HmiMessage message => message.Meta, OnlineDownloadResult download => download.Meta,
            OnlineStateResult online => online.Meta, _ => null };
        bool attempted = meta != null && meta.TryGetValue("writeAttempted", out var attempt) && attempt is true;
        bool changed = attempted || meta != null && meta.TryGetValue("mayHaveChanged", out var flag) && flag is true;
        bool succeeded = meta != null && meta.TryGetValue("success", out var success) && success is true;
        bool files = meta != null && meta.TryGetValue("mayHaveWrittenFiles", out var written) && written is true;
        bool rejected = meta != null && meta.TryGetValue("v4Rejection", out var refusal) && refusal != null;
        string? state = value is OnlineDownloadResult result ? result.State
            : meta != null && meta.TryGetValue("nativeState", out var nativeState) ? nativeState as string : null;
        bool unknownDownload = false;
        if (request.Operation == "DownloadPlc" && meta != null)
        {
            if (meta.TryGetValue("writeAttempted", out var phase) && phase is bool) nativeCallIssued = changed;
            unknownDownload = !succeeded && meta.TryGetValue("writeOutcomeKnown", out var known) && known is false;
        }
        return new OnlineReply { Value = value, MayHaveChanged = changed,
            OutcomeUnknown = unknownDownload || OutcomeUnknown(request, succeeded, nativeCallIssued, changed, files, rejected, state) };
    }

    public static bool OutcomeUnknown(OnlineRequest request, bool success, bool nativeCallIssued,
        bool mayHaveChanged, bool mayHaveWrittenFiles, bool rejected, string? nativeState)
    {
        if (request.DryRun || success || rejected || !nativeCallIssued || OnlineCapabilities.IsReadOnly(request))
            return false;
        if (request.Operation is "GeneratePlcLoadableFile" or "DownloadPlcToFolder") return false;
        if (request.Operation == "DownloadPlc")
            return nativeState != "Error";
        return mayHaveChanged;
    }
}
