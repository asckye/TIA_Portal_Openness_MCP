using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectLifecycleExchangeTools
{
    private readonly ProjectLifecyclePortService _service;
    public ProjectLifecycleExchangeTools(ProjectLifecyclePortService service) => _service = service;
        [McpServerTool(Name = "RetrieveProjectArchive"), Description("[L2][Project][WRITE] Retrieve an archive into a new absolute directory and bind returned project. Requires connected Portal with no open project/session; never closes existing projects. upgrade=false, dryRun=true." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult RetrieveProjectArchiveV4(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            [Description("destinationDirectory: absolute path of a new directory on the TIA machine; existing directories are refused (no merge or overwrite).")] string destinationDirectory,
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            [Description("dryRun: true validates the archive and destination without retrieving, creating files or opening a project; false retrieves and binds the returned project.")] bool dryRun=true)
        {
            return PlcExchangeContract.Run("RetrieveProjectArchive", () => RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun), write: !dryRun, current: true);
        }


        public ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade = false, bool dryRun = true)
            => _service.RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun);

}
