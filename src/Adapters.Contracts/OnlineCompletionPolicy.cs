namespace TiaMcp.Adapters.Contracts;

public static class OnlineCompletionPolicy
{
    public static bool RequiresSessionReset(OnlineRequest request, bool success, bool nativeCallIssued,
        bool mayHaveChanged, bool mayHaveWrittenFiles, bool rejected, string? nativeState)
    {
        if (request.DryRun || success || rejected || !nativeCallIssued || OnlineCapabilities.IsReadOnly(request))
            return false;
        if (request.Operation == "DownloadPlc" || request.Operation == "DownloadPlcToFolder")
            return nativeState != "Error";
        return mayHaveChanged || mayHaveWrittenFiles;
    }
}
