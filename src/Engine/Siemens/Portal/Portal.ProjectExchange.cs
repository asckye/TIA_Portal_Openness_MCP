using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens;
public partial class Portal
{
    public ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade = false, bool dryRun = true)
        => new Services.ProjectLifecyclePortService().RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun);
    public ResponseMessage ReadPortalInfo(bool includeProcesses = true, bool includeSessions = true, bool includeProducts = true)
        => new Services.ProjectLifecyclePortService().ReadPortalInfo(includeProcesses, includeSessions, includeProducts);
}
