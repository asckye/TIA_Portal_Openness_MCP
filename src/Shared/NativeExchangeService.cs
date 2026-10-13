namespace TiaMcpServer.Siemens.Services;

// The compatibility type remains a managed facade for service discovery; no Openness implementation remains here.
internal sealed class NativeExchangeService
{
    private readonly OnlineDownloadService _onlineDownload;
    public NativeExchangeService(OnlineDownloadService onlineDownload) => _onlineDownload = onlineDownload;
    public TiaMcpServer.ModelContextProtocol.ResponseMessage GeneratePlcLoadableFile(string softwarePath, string objectPathsJson, string objectKind, string targetOption, string filePath, bool dryRun = true)
        => _onlineDownload.GeneratePlcLoadableFile(softwarePath, objectPathsJson, objectKind, targetOption, filePath, dryRun);
}
