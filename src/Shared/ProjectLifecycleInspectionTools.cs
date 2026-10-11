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
internal sealed class ProjectLifecycleInspectionTools
{
    private readonly ProjectLifecyclePortService _service;
    public ProjectLifecycleInspectionTools(ProjectLifecyclePortService service) => _service = service;
        [McpServerTool(Name="ArchiveSavedProject"),Description("[L2][Project][FILE]Create a native compressed .zap20/.zap21 archive of an already saved project. Default dryRun=true. Rejects unsaved state, unsupported sessions and existing target files. Does not save, change project path, close the project, or test retrieval. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ArchiveSavedProjectV4(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            bool dryRun=true)
            => HmiInspectionContract.Run("ArchiveSavedProject", !dryRun, true, () => ArchiveSavedProject(archivePath, dryRun));

        public ResponseMessage ArchiveSavedProject(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            bool dryRun=true)
        =>_service.ArchiveSavedProject(archivePath,dryRun);

}
