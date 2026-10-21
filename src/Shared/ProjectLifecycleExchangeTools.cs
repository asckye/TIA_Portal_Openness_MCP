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
        [McpServerTool(Name = "RetrieveProjectArchive"), Description("[L2][Project][WRITE] Retrieve an archive into a new absolute directory and bind returned project. Requires connected Portal with no open project/session; never closes existing projects. upgrade=false, dryRun=true. Protected (UMAC) archives: pass umacUserName and umacPassword (umacUserType Project or Global); the password becomes a SecureString and is never logged, journaled or echoed. A documented TIA refusal (access denied, missing products, missing licence) is PRECONDITION_FAILED with its condition." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult RetrieveProjectArchiveV4(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            [Description("destinationDirectory: absolute path of a new directory on the TIA machine; existing directories are refused (no merge or overwrite).")] string destinationDirectory,
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            [Description("dryRun: true validates the archive and destination without retrieving, creating files or opening a project; false retrieves and binds the returned project.")] bool dryRun=true,
            [Description("umacUserName: UMAC user of a protected archive; give it together with umacPassword. Empty for an unprotected archive.")] string umacUserName="",
            [Description("umacPassword: password of umacUserName. Converted to SecureString in the worker; never logged, journaled, echoed or shown on the approval card.")] string umacPassword="",
            [Description("umacUserType: Project (default) or Global (UMAC server user); only together with umacUserName and umacPassword.")] string umacUserType="")
        {
            return PlcExchangeContract.Run("RetrieveProjectArchive", () => RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun, umacUserName, umacPassword, umacUserType), write: !dryRun, current: true);
        }


        public ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade = false, bool dryRun = true,
            string umacUserName = "", string umacPassword = "", string umacUserType = "")
            => _service.RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun, umacUserName, umacPassword, umacUserType);

}
